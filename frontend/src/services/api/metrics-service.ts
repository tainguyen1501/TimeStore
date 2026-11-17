import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type MetricsResponse = components["schemas"]["MetricsResponse"]
export type MetricsSampleResponse = components["schemas"]["MetricsSampleResponse"]

export const metricsService = {
  get: async () => (await api.get<MetricsResponse>("/metrics")).data,
}
