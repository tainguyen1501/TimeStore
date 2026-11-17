import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type LifecycleRuleDto = components["schemas"]["LifecycleRuleDto"]
export type LifecycleConfigurationDto = components["schemas"]["LifecycleConfigurationDto"]
export type LifecycleRunResponse = components["schemas"]["LifecycleRunResponse"]

const url = (bucket: string) => `/buckets/${encodeURIComponent(bucket)}/lifecycle`

export const lifecycleService = {
  /** The rules, or an empty list when the bucket has no configuration (the API answers 404). */
  get: async (bucket: string): Promise<LifecycleRuleDto[]> => {
    const response = await api.get<LifecycleConfigurationDto>(url(bucket), {
      validateStatus: (status) => status === 200 || status === 404,
    })
    return response.status === 404 ? [] : response.data.rules
  },
  /** Replaces the whole configuration; an empty list removes it. */
  set: async (bucket: string, rules: LifecycleRuleDto[]) => {
    if (rules.length === 0) {
      await api.delete(url(bucket))
      return []
    }
    return (await api.put<LifecycleConfigurationDto>(url(bucket), { rules })).data.rules
  },
  runNow: async () => (await api.post<LifecycleRunResponse>("/maintenance/lifecycle")).data,
}
