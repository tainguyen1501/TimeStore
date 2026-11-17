import axios, { isAxiosError } from "axios"

// Relative base URL only: the same build runs behind the Vite proxy and the Server's admin port.
// Auth is the same-origin session cookie (HttpOnly, SameSite=Strict): nothing to attach here.
export const api = axios.create({
  baseURL: "/api/v1",
})

export const isUnauthorized = (error: unknown) =>
  isAxiosError(error) && error.response?.status === 401

/**
 * Calls `onUnauthorized` when a request outside `ignorePrefix` (the auth endpoints, which the sign-in page handles)
 * answers 401, e.g. when the session expired while a page was open. The request still fails for its caller.
 */
export function onUnauthorizedResponse(ignorePrefix: string, onUnauthorized: () => void) {
  api.interceptors.response.use(undefined, (error: unknown) => {
    if (
      isUnauthorized(error) &&
      isAxiosError(error) &&
      !error.config?.url?.startsWith(ignorePrefix)
    ) {
      onUnauthorized()
    }
    return Promise.reject(error)
  })
}
