import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import {
  replicationService,
  type ReplicationResponse,
  type SetReplicationRequest,
} from "@/services/api/replication-service"

export const REPLICATION_QUERY_KEYS = {
  UseGetReplication: (bucket: string) => ["UseGetReplication", bucket],
}

/** How often an open replication sheet refreshes the backlog (pending, lag, failures). */
const REFRESH_MS = 5_000

export const useGetReplication = (bucket: string, enabled = true) =>
  useQuery({
    queryKey: REPLICATION_QUERY_KEYS.UseGetReplication(bucket),
    queryFn: () => replicationService.get(bucket),
    enabled,
    refetchInterval: enabled ? REFRESH_MS : false,
  })

/** Every action answers the rule as it is now: the cache takes it as is. */
const useReplicationAction = <TInput>(
  bucket: string,
  action: (input: TInput) => Promise<ReplicationResponse | null>,
  options?: ActionOptions
) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: action,
    onSuccess: (rule) => {
      queryClient.setQueryData(REPLICATION_QUERY_KEYS.UseGetReplication(bucket), rule)
      void queryClient.invalidateQueries({ queryKey: ["UseGetMetrics"] })
    },
    meta: options?.meta,
  })
}

export const useSetReplication = (bucket: string, options?: ActionOptions) =>
  useReplicationAction(
    bucket,
    (request: SetReplicationRequest) => replicationService.set(bucket, request),
    options
  )

export const useDeleteReplication = (bucket: string, options?: ActionOptions) =>
  useReplicationAction(bucket, async () => (await replicationService.remove(bucket), null), options)

export const useTestReplication = (bucket: string, options?: ActionOptions) =>
  useReplicationAction(bucket, () => replicationService.test(bucket), options)

export const useRetryReplication = (bucket: string, options?: ActionOptions) =>
  useReplicationAction(bucket, () => replicationService.retry(bucket), options)

export const useResyncReplication = (bucket: string, options?: ActionOptions) =>
  useReplicationAction(bucket, () => replicationService.resync(bucket), options)
