import { isAxiosError, isCancel } from "axios"

/** Retries after the first attempt, and the wait before each (doubled, plus up to 30% jitter). */
export const RETRY_DELAYS_MS = [1_000, 2_000, 4_000]

/**
 * Statuses worth another try: the request timed out, the server or a proxy in front of it was overloaded or
 * restarting. Anything else (400, 401, 403, 404, 409, 413…) fails the same way again.
 */
const RETRYABLE_STATUSES = new Set([408, 425, 429, 500, 502, 503, 504])

/** True for a dropped connection (no response at all) or a retryable status; never for a cancel by the user. */
export function isRetryable(error: unknown): boolean {
  if (isCancel(error)) return false
  if (!isAxiosError(error)) return false
  if (!error.response) return error.code !== "ERR_CANCELED"
  return RETRYABLE_STATUSES.has(error.response.status)
}

/** Waits `ms`, or rejects at once when `signal` aborts. */
export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) return reject(signal.reason)
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort)
      resolve()
    }, ms)
    const onAbort = () => {
      clearTimeout(timer)
      reject(signal?.reason)
    }
    signal?.addEventListener("abort", onAbort, { once: true })
  })
}

/**
 * Runs `attempt` and, when it fails with a retryable error, runs it again after a growing delay (1 s, 2 s, 4 s with
 * jitter). `onRetry` runs before each new attempt (to reset progress). The last error is thrown when every attempt
 * failed or the error is not retryable; an abort stops the waiting at once.
 */
export async function withRetry<T>(
  attempt: () => Promise<T>,
  { signal, onRetry }: { signal?: AbortSignal; onRetry?: (retry: number, error: unknown) => void } = {}
): Promise<T> {
  for (let retry = 0; ; retry++) {
    try {
      return await attempt()
    } catch (error) {
      if (retry >= RETRY_DELAYS_MS.length || !isRetryable(error) || signal?.aborted) throw error
      const delay = RETRY_DELAYS_MS[retry] * (1 + Math.random() * 0.3)
      await sleep(delay, signal)
      onRetry?.(retry + 1, error)
    }
  }
}
