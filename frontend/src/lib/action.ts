import { getErrorMessage } from "@/lib/api-response"

/**
 * How an action (mutation) reports a failure. By default the global handler (`main.tsx`) shows a toast with the Api's
 * message. Set by the hook as a default and overridden by the page that calls it.
 */
export type ActionMeta = {
  /** The page shows the error itself (inline, `mutation.error`, or `mutate(…, { onError })`): no toast. */
  skipErrorToast?: boolean
  /** Toast this text instead of the Api's message (a function gets the error, e.g. to add the Api's message). */
  errorMessage?: string | ((error: unknown) => string)
}

/** Last parameter of every action hook (`useCreateBucket({ meta: { skipErrorToast: true } })`). */
export type ActionOptions = { meta?: ActionMeta }

/** The toast text for a failed action, or null when the page handles it. */
export function actionErrorToast(error: unknown, meta: ActionMeta | undefined): string | null {
  if (meta?.skipErrorToast) return null
  if (typeof meta?.errorMessage === "function") return meta.errorMessage(error)
  return meta?.errorMessage ?? getErrorMessage(error)
}
