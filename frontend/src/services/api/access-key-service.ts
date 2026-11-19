import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type AccessKeyResponse = components["schemas"]["AccessKeyResponse"]
export type CreateAccessKeyRequest = components["schemas"]["CreateAccessKeyRequest"]
export type CreatedAccessKeyResponse = components["schemas"]["CreatedAccessKeyResponse"]

/** Roles of an access key (TimeStore.Domain AccessKeyRoles). The API sends them as strings. */
export const ACCESS_KEY_ROLES = ["none", "viewer", "admin", "backup"] as const
export type AccessKeyRole = (typeof ACCESS_KEY_ROLES)[number]

/** What each role allows, shown where a role is picked. */
export const ROLE_INFO: Record<AccessKeyRole, { label: string; description: string }> = {
  none: { label: "S3 only", description: "S3 API only; cannot sign in to the admin UI." },
  viewer: { label: "Viewer", description: "S3 API, plus the admin UI read-only." },
  admin: { label: "Admin", description: "S3 API, plus the admin UI with every action." },
  backup: {
    label: "Backup",
    description:
      "For backup programs: the backup API with signed requests (curl --aws-sigv4, AWS SDKs); no admin UI, never the master key.",
  },
}

/** S3 scopes of an access key (TimeStore.Domain AccessKeyScopes). */
export const ACCESS_KEY_SCOPES = ["full", "policy"] as const
export type AccessKeyScope = (typeof ACCESS_KEY_SCOPES)[number]

/** What each scope allows on the S3 API (docs/research/s3-bucket-policy.md). */
export const SCOPE_INFO: Record<AccessKeyScope, { label: string; description: string }> = {
  full: { label: "All buckets", description: "Every bucket, unless a bucket policy denies it." },
  policy: {
    label: "Bucket policies only",
    description: "Only what a bucket policy allows; cannot list or create buckets.",
  },
}

/** Description length limit (TimeStore.Domain AccessKey.DescriptionMaxLength). */
export const DESCRIPTION_MAX_LENGTH = 256

const BASE_URL = "/access-keys"
const byId = (id: string) => `${BASE_URL}/${encodeURIComponent(id)}`

export const accessKeyService = {
  getList: async () => (await api.get<AccessKeyResponse[]>(BASE_URL)).data,
  create: async (request: CreateAccessKeyRequest) =>
    (await api.post<CreatedAccessKeyResponse>(BASE_URL, request)).data,
  setRole: async (id: string, role: AccessKeyRole) =>
    (await api.post<AccessKeyResponse>(`${byId(id)}/role`, { role })).data,
  setScope: async (id: string, s3Scope: AccessKeyScope) =>
    (await api.post<AccessKeyResponse>(`${byId(id)}/scope`, { s3Scope })).data,
  disable: async (id: string) => (await api.post<AccessKeyResponse>(`${byId(id)}/disable`)).data,
  enable: async (id: string) => (await api.post<AccessKeyResponse>(`${byId(id)}/enable`)).data,
  delete: async (id: string) => {
    await api.delete(byId(id))
  },
}
