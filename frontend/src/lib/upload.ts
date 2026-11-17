import { objectService } from "@/services/api/object-service"

const MiB = 1024 * 1024

/** Files up to this size go in one request; larger ones as a multipart upload. */
export const SINGLE_UPLOAD_MAX = 64 * MiB
const MIN_PART_SIZE = 16 * MiB
const MAX_PARTS = 10_000
/** Parts in flight at once for one file. */
const PART_CONCURRENCY = 3

/** Part size for a file: at least 16 MiB, large enough to stay within S3's 10 000 parts. */
export function partSize(size: number) {
  return Math.max(MIN_PART_SIZE, Math.ceil(size / MAX_PARTS))
}

/**
 * Uploads `file` as `key`: one request when small, else a multipart upload with parts in parallel. `onProgress`
 * gets the bytes sent so far. A failure or an abort (through `signal`) aborts the multipart upload on the server.
 */
export async function uploadFile(
  bucket: string,
  key: string,
  file: File,
  onProgress: (loaded: number) => void,
  signal: AbortSignal
) {
  if (file.size <= SINGLE_UPLOAD_MAX) {
    await objectService.put(bucket, key, file, file.type, { onProgress, signal })
    onProgress(file.size)
    return
  }

  const size = partSize(file.size)
  const count = Math.ceil(file.size / size)
  const loaded = new Array<number>(count).fill(0)
  const report = () => onProgress(loaded.reduce((sum, n) => sum + n, 0))
  const uploadId = await objectService.startUpload(bucket, key, file.type)

  try {
    const parts = new Array<{ partNumber: number; eTag: string }>(count)
    let next = 0
    const worker = async () => {
      while (next < count) {
        const index = next++
        const start = index * size
        const blob = file.slice(start, Math.min(start + size, file.size))
        parts[index] = await objectService.uploadPart(bucket, key, uploadId, index + 1, blob, {
          signal,
          onProgress: (n) => {
            loaded[index] = n
            report()
          },
        })
        loaded[index] = blob.size
        report()
      }
    }
    await Promise.all(Array.from({ length: Math.min(PART_CONCURRENCY, count) }, worker))
    await objectService.completeUpload(bucket, key, uploadId, parts)
  } catch (error) {
    // Leave nothing behind: the parts of an unfinished upload hold space until aborted.
    await objectService.abortUpload(bucket, key, uploadId).catch(() => undefined)
    throw error
  }
}
