import { withRetry } from "@/lib/retry"
import { objectService } from "@/services/api/object-service"

const MiB = 1024 * 1024

/**
 * Files up to this size go in one request; larger ones as a multipart upload. Kept small so no request runs long:
 * proxies in front of the admin port often cut a request after 60 s (Traefik's readTimeout, ingress-nginx's
 * proxy timeouts), and 16 MiB in 60 s needs only about 280 KB/s.
 */
export const SINGLE_UPLOAD_MAX = 16 * MiB
/** Smallest part (S3 allows 5 MiB); 8 MiB in 60 s needs about 140 KB/s. */
const MIN_PART_SIZE = 8 * MiB
const MAX_PARTS = 10_000
/** Parts in flight at once for one file. */
const PART_CONCURRENCY = 3

/** Part size for a file: at least 8 MiB, large enough to stay within S3's 10 000 parts. */
export function partSize(size: number) {
  return Math.max(MIN_PART_SIZE, Math.ceil(size / MAX_PARTS))
}

/**
 * Uploads `file` as `key`: one request when small, else a multipart upload with parts in parallel. Every request is
 * retried on a dropped connection or a timeout / overload status (lib/retry.ts), so one bad moment on the network does
 * not restart a large upload. `onProgress` gets the bytes sent so far. A failure that survives the retries, or an
 * abort (through `signal`), aborts the multipart upload on the server.
 */
export async function uploadFile(
  bucket: string,
  key: string,
  file: File,
  onProgress: (loaded: number) => void,
  signal: AbortSignal
) {
  if (file.size <= SINGLE_UPLOAD_MAX) {
    await withRetry(() => objectService.put(bucket, key, file, file.type, { onProgress, signal }), {
      signal,
      onRetry: () => onProgress(0),
    })
    onProgress(file.size)
    return
  }

  const size = partSize(file.size)
  const count = Math.ceil(file.size / size)
  const loaded = new Array<number>(count).fill(0)
  const report = () => onProgress(loaded.reduce((sum, n) => sum + n, 0))
  const uploadId = await withRetry(() => objectService.startUpload(bucket, key, file.type), {
    signal,
  })

  try {
    const parts = new Array<{ partNumber: number; eTag: string }>(count)
    let next = 0
    // Once a part has failed for good, the other workers start no new part (the upload is aborted below).
    let failed = false
    const worker = async () => {
      while (!failed && next < count) {
        const index = next++
        const start = index * size
        const blob = file.slice(start, Math.min(start + size, file.size))
        // A part has a fixed number: sending it again replaces the earlier attempt on the server.
        parts[index] = await withRetry(
          () =>
            objectService.uploadPart(bucket, key, uploadId, index + 1, blob, {
              signal,
              onProgress: (n) => {
                loaded[index] = n
                report()
              },
            }),
          {
            signal,
            onRetry: () => {
              loaded[index] = 0
              report()
            },
          }
        ).catch((error: unknown) => {
          failed = true
          throw error
        })
        loaded[index] = blob.size
        report()
      }
    }
    await Promise.all(Array.from({ length: Math.min(PART_CONCURRENCY, count) }, worker))
    await withRetry(() => objectService.completeUpload(bucket, key, uploadId, parts), { signal })
  } catch (error) {
    // Leave nothing behind: the parts of an unfinished upload hold space until aborted.
    await objectService.abortUpload(bucket, key, uploadId).catch(() => undefined)
    throw error
  }
}
