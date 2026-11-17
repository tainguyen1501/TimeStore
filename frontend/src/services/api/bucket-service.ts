import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type BucketResponse = components["schemas"]["BucketResponse"]
export type CreateBucketRequest = components["schemas"]["CreateBucketRequest"]

/** Bucket versioning states (TimeStore.Domain BucketVersioning); a bucket never returns to Unversioned. */
export const VERSIONING = {
  unversioned: "Unversioned",
  enabled: "Enabled",
  suspended: "Suspended",
} as const

export type VersioningChange = typeof VERSIONING.enabled | typeof VERSIONING.suspended

const BASE_URL = "/buckets"

export const bucketService = {
  getList: async () => (await api.get<BucketResponse[]>(BASE_URL)).data,
  create: async (request: CreateBucketRequest) =>
    (await api.post<BucketResponse>(BASE_URL, request)).data,
  delete: async (name: string) => {
    await api.delete(`${BASE_URL}/${encodeURIComponent(name)}`)
  },
  setVersioning: async (name: string, status: VersioningChange) =>
    (
      await api.put<BucketResponse>(`${BASE_URL}/${encodeURIComponent(name)}/versioning`, {
        status,
      })
    ).data,
}
