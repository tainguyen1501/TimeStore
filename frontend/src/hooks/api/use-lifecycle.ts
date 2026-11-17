import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import { lifecycleService, type LifecycleRuleDto } from "@/services/api/lifecycle-service"

export const LIFECYCLE_QUERY_KEYS = {
  UseGetLifecycle: (bucket: string) => ["UseGetLifecycle", bucket],
}

export const useGetLifecycle = (bucket: string, enabled = true) =>
  useQuery({
    queryKey: LIFECYCLE_QUERY_KEYS.UseGetLifecycle(bucket),
    queryFn: () => lifecycleService.get(bucket),
    enabled,
  })

export const useSetLifecycle = (bucket: string, options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (rules: LifecycleRuleDto[]) => lifecycleService.set(bucket, rules),
    onSuccess: (rules) =>
      queryClient.setQueryData(LIFECYCLE_QUERY_KEYS.UseGetLifecycle(bucket), rules),
    meta: options?.meta,
  })
}

/** Applies every bucket's rules now; listings change, so they are refreshed. */
export const useRunLifecycle = (options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: lifecycleService.runNow,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["UseGetObjectList"] }),
    meta: options?.meta,
  })
}
