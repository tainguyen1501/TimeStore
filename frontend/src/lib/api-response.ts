import { isAxiosError } from "axios"

import type { ApiErrorResponse } from "@/@types/common"

export function getErrorMessage(error: unknown): string {
  if (isAxiosError<ApiErrorResponse>(error)) {
    return error.response?.data?.message ?? error.message
  }
  if (error instanceof Error) return error.message
  return "Something went wrong."
}

/** The Server's error code, e.g. to branch on a conflict (`BUCKET_ALREADY_EXISTS`). */
export function getErrorCode(error: unknown): string | undefined {
  return isAxiosError<ApiErrorResponse>(error) ? error.response?.data?.code : undefined
}

/** Seconds from a `Retry-After` header (429 from sign-in throttling), or undefined when absent or unreadable. */
export function getRetryAfterSeconds(error: unknown): number | undefined {
  if (!isAxiosError(error)) return undefined
  const value = Number(error.response?.headers["retry-after"])
  return Number.isFinite(value) && value > 0 ? Math.ceil(value) : undefined
}
