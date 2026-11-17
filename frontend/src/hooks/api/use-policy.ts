import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import { policyService } from "@/services/api/policy-service"

export const POLICY_QUERY_KEYS = {
  UseGetBucketPolicy: (bucket: string) => ["UseGetBucketPolicy", bucket],
  UseGetPublicPrefixes: (bucket: string) => ["UseGetPublicPrefixes", bucket],
}

export const useGetBucketPolicy = (bucket: string, enabled = true) =>
  useQuery({
    queryKey: POLICY_QUERY_KEYS.UseGetBucketPolicy(bucket),
    queryFn: () => policyService.get(bucket),
    enabled,
  })

/** Saves the document, or removes the policy when it is null. */
export const useSetBucketPolicy = (bucket: string, options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (document: string | null) =>
      document === null
        ? (await policyService.remove(bucket), null)
        : policyService.set(bucket, document),
    onSuccess: (policy) => {
      queryClient.setQueryData(POLICY_QUERY_KEYS.UseGetBucketPolicy(bucket), policy)
      void queryClient.invalidateQueries({
        queryKey: POLICY_QUERY_KEYS.UseGetPublicPrefixes(bucket),
      })
      void queryClient.invalidateQueries({ queryKey: ["UseGetObjectAccess", bucket] })
    },
    meta: options?.meta,
  })
}

export const useGetPublicPrefixes = (bucket: string) =>
  useQuery({
    queryKey: POLICY_QUERY_KEYS.UseGetPublicPrefixes(bucket),
    queryFn: () => policyService.getPublic(bucket),
  })

/** "Make public" / "Make private" for the bucket ("") or a folder; the policy and object links change too. */
export const useSetPublic = (bucket: string, options?: ActionOptions) => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ prefix, isPublic }: { prefix: string; isPublic: boolean }) =>
      policyService.setPublic(bucket, prefix, isPublic),
    onSuccess: (prefixes) => {
      queryClient.setQueryData(POLICY_QUERY_KEYS.UseGetPublicPrefixes(bucket), prefixes)
      void queryClient.invalidateQueries({ queryKey: POLICY_QUERY_KEYS.UseGetBucketPolicy(bucket) })
      void queryClient.invalidateQueries({ queryKey: ["UseGetObjectAccess", bucket] })
    },
    meta: options?.meta,
  })
}
