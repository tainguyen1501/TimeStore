import { useQuery } from "@tanstack/react-query"

import { metricsService } from "@/services/api/metrics-service"

export const METRICS_QUERY_KEYS = {
  UseGetMetrics: () => ["UseGetMetrics"],
}

/** The server samples every 10 s; polling at the same pace shows each new point. */
const POLL_MS = 10_000

export const useGetMetrics = () =>
  useQuery({
    queryKey: METRICS_QUERY_KEYS.UseGetMetrics(),
    queryFn: metricsService.get,
    refetchInterval: POLL_MS,
    staleTime: POLL_MS,
  })
