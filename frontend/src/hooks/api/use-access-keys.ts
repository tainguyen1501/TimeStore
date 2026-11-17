import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import {
  accessKeyService,
  type AccessKeyRole,
  type AccessKeyScope,
  type CreateAccessKeyRequest,
} from "@/services/api/access-key-service"

export const ACCESS_KEY_QUERY_KEYS = {
  UseGetAccessKeyList: () => ["UseGetAccessKeyList"],
}

export const useGetAccessKeyList = () =>
  useQuery({
    queryKey: ACCESS_KEY_QUERY_KEYS.UseGetAccessKeyList(),
    queryFn: accessKeyService.getList,
  })

const useInvalidateAccessKeys = () => {
  const queryClient = useQueryClient()
  return () =>
    queryClient.invalidateQueries({ queryKey: ACCESS_KEY_QUERY_KEYS.UseGetAccessKeyList() })
}

/** The response holds the secret: it lives only in this mutation's state, never in the query cache. */
export const useCreateAccessKey = (options?: ActionOptions) => {
  const invalidate = useInvalidateAccessKeys()
  return useMutation({
    mutationFn: (request: CreateAccessKeyRequest) => accessKeyService.create(request),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useSetAccessKeyRole = (options?: ActionOptions) => {
  const invalidate = useInvalidateAccessKeys()
  return useMutation({
    mutationFn: ({ id, role }: { id: string; role: AccessKeyRole }) =>
      accessKeyService.setRole(id, role),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useSetAccessKeyScope = (options?: ActionOptions) => {
  const invalidate = useInvalidateAccessKeys()
  return useMutation({
    mutationFn: ({ id, scope }: { id: string; scope: AccessKeyScope }) =>
      accessKeyService.setScope(id, scope),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useSetAccessKeyEnabled = (options?: ActionOptions) => {
  const invalidate = useInvalidateAccessKeys()
  return useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      enabled ? accessKeyService.enable(id) : accessKeyService.disable(id),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useDeleteAccessKey = (options?: ActionOptions) => {
  const invalidate = useInvalidateAccessKeys()
  return useMutation({
    mutationFn: (id: string) => accessKeyService.delete(id),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}
