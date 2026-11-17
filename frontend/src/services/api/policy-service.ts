import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type BucketPolicyResponse = components["schemas"]["BucketPolicyResponse"]
export type PublicAccessResponse = components["schemas"]["PublicAccessResponse"]

const url = (bucket: string) => `/buckets/${encodeURIComponent(bucket)}/policy`

export const policyService = {
  /** The policy, or null when the bucket has none (the API answers 404). */
  get: async (bucket: string): Promise<BucketPolicyResponse | null> => {
    const response = await api.get<BucketPolicyResponse>(url(bucket), {
      validateStatus: (status) => status === 200 || status === 404,
    })
    return response.status === 404 ? null : response.data
  },
  /** Replaces the policy; the server validates it (MalformedPolicy errors come back as 400). */
  set: async (bucket: string, document: string) =>
    (await api.put<BucketPolicyResponse>(url(bucket), { document })).data,
  remove: async (bucket: string) => {
    await api.delete(url(bucket))
  },
  /** The bucket ("") and folders made public with "Make public". */
  getPublic: async (bucket: string) =>
    (await api.get<PublicAccessResponse>(`/buckets/${encodeURIComponent(bucket)}/public`)).data
      .prefixes,
  setPublic: async (bucket: string, prefix: string, isPublic: boolean) =>
    (
      await api.put<PublicAccessResponse>(`/buckets/${encodeURIComponent(bucket)}/public`, {
        prefix,
        isPublic,
      })
    ).data.prefixes,
}
