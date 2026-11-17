import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import { ADMIN_ROLES, authService, type SessionResponse } from "@/services/api/auth-service"

export const AUTH_QUERY_KEYS = {
  UseGetMe: () => ["UseGetMe"],
}

// A 401 is an answer ("not signed in"), not a transient failure: never retry it. The session is long-lived, so the
// guard reuses the cached answer; an expired session shows up as a 401 on the next call (handled in main.tsx).
export const meQuery = {
  queryKey: AUTH_QUERY_KEYS.UseGetMe(),
  queryFn: authService.getMe,
  retry: false,
  staleTime: 5 * 60_000,
} as const

export const useGetMe = () => useQuery(meQuery)

/** Whether the signed-in key may change things. Viewers see no write actions (the server refuses them anyway). */
export const useCanWrite = () => useGetMe().data?.role === ADMIN_ROLES.admin

// The sign-in page shows its own error box: it passes `skipErrorToast`.
export const useLogin = (options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: authService.login,
    // Store the session so the guarded layout does not ask for it again.
    onSuccess: (session: SessionResponse) =>
      queryClient.setQueryData(AUTH_QUERY_KEYS.UseGetMe(), session),
    meta: options?.meta,
  })
}

export const useLogout = (options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: authService.logout,
    // Nothing of the previous session may show to the next one.
    onSettled: () => queryClient.clear(),
    meta: options?.meta,
  })
}
