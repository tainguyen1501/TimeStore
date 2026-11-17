import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type LoginRequest = components["schemas"]["LoginRequest"]
export type SessionResponse = components["schemas"]["SessionResponse"]

/** Roles that may sign in (TimeStore.Domain AccessKeyRoles; `none` is S3 only). The API sends them as strings. */
export const ADMIN_ROLES = { admin: "admin", viewer: "viewer" } as const

/** Prefix of every auth endpoint; the global 401 handler ignores it (the sign-in page shows its own errors). */
export const AUTH_BASE_URL = "/auth"

export const authService = {
  login: async (request: LoginRequest) =>
    (await api.post<SessionResponse>(`${AUTH_BASE_URL}/login`, request)).data,
  logout: async () => {
    await api.post(`${AUTH_BASE_URL}/logout`)
  },
  getMe: async () => (await api.get<SessionResponse>(`${AUTH_BASE_URL}/me`)).data,
}
