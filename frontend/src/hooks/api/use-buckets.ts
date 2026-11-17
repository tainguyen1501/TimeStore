import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import {
  bucketService,
  type CreateBucketRequest,
  type VersioningChange,
} from "@/services/api/bucket-service"

export const BUCKET_QUERY_KEYS = {
  UseGetBucketList: () => ["UseGetBucketList"],
}

export const useGetBucketList = () =>
  useQuery({
    queryKey: BUCKET_QUERY_KEYS.UseGetBucketList(),
    queryFn: bucketService.getList,
  })

const useInvalidateBuckets = () => {
  const queryClient = useQueryClient()
  return () => queryClient.invalidateQueries({ queryKey: BUCKET_QUERY_KEYS.UseGetBucketList() })
}

export const useCreateBucket = (options?: ActionOptions) => {
  const invalidate = useInvalidateBuckets()
  return useMutation({
    mutationFn: (request: CreateBucketRequest) => bucketService.create(request),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useSetBucketVersioning = (options?: ActionOptions) => {
  const invalidate = useInvalidateBuckets()
  return useMutation({
    mutationFn: ({ name, status }: { name: string; status: VersioningChange }) =>
      bucketService.setVersioning(name, status),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useDeleteBucket = (options?: ActionOptions) => {
  const invalidate = useInvalidateBuckets()
  return useMutation({
    mutationFn: (name: string) => bucketService.delete(name),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}
