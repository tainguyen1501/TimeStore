import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type ReplicationResponse = components["schemas"]["ReplicationResponse"]
export type ReplicationFailureResponse = components["schemas"]["ReplicationFailureResponse"]
export type SetReplicationRequest = components["schemas"]["SetReplicationRequest"]

const url = (bucket: string) => `/buckets/${encodeURIComponent(bucket)}/replication`

export const replicationService = {
  /** The bucket's rule with its backlog, or null when it has none (the API answers 404). */
  get: async (bucket: string): Promise<ReplicationResponse | null> => {
    const response = await api.get<ReplicationResponse>(url(bucket), {
      validateStatus: (status) => status === 200 || status === 404,
    })
    return response.status === 404 ? null : response.data
  },
  /** Tests the destination, then saves the rule (the secret is sent, never returned). */
  set: async (bucket: string, request: SetReplicationRequest) =>
    (await api.put<ReplicationResponse>(url(bucket), request)).data,
  remove: async (bucket: string) => {
    await api.delete(url(bucket))
  },
  test: async (bucket: string) => (await api.post<ReplicationResponse>(`${url(bucket)}/test`)).data,
  retry: async (bucket: string) =>
    (await api.post<ReplicationResponse>(`${url(bucket)}/retry`)).data,
  resync: async (bucket: string) =>
    (await api.post<ReplicationResponse>(`${url(bucket)}/resync`)).data,
}
