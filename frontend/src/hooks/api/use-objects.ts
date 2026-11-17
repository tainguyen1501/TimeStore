import {
  keepPreviousData,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query"

import type { ActionOptions } from "@/lib/action"
import { objectService, type DeleteObjectsRequest } from "@/services/api/object-service"

export const OBJECT_QUERY_KEYS = {
  /** Every listing of a bucket (all prefixes): what an upload or delete invalidates. */
  UseGetObjectListAll: (bucket: string) => ["UseGetObjectList", bucket],
  UseGetObjectList: (bucket: string, prefix: string) => ["UseGetObjectList", bucket, prefix],
  UseGetObjectVersionList: (bucket: string, prefix: string) => [
    "UseGetObjectList",
    bucket,
    prefix,
    "versions",
  ],
  UseGetObjectAccess: (bucket: string, key: string, versionId?: string) => [
    "UseGetObjectAccess",
    bucket,
    key,
    versionId ?? "latest",
  ],
  UseGetObjectDetails: (bucket: string, key: string, versionId?: string) => [
    "UseGetObjectDetails",
    bucket,
    key,
    versionId ?? "latest",
  ],
}

/**
 * Folders and objects whose names start with `prefix` (a folder, or a folder plus filter text), page by page while
 * the table scrolls. The previous listing stays on screen while the next one loads (typing in the filter).
 */
export const useGetObjectList = (bucket: string, prefix: string) =>
  useInfiniteQuery({
    queryKey: OBJECT_QUERY_KEYS.UseGetObjectList(bucket, prefix),
    queryFn: ({ pageParam }) => objectService.list(bucket, prefix, pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextStartAfter ?? undefined,
    placeholderData: keepPreviousData,
  })

/** Every version and delete marker in `prefix`, page by page. */
export const useGetObjectVersionList = (bucket: string, prefix: string, enabled: boolean) =>
  useInfiniteQuery({
    queryKey: OBJECT_QUERY_KEYS.UseGetObjectVersionList(bucket, prefix),
    queryFn: ({ pageParam }) => objectService.listVersions(bucket, prefix, pageParam),
    initialPageParam: undefined as
      { keyMarker: string; versionIdMarker?: string | null } | undefined,
    getNextPageParam: (last) =>
      last.isTruncated && last.nextKeyMarker
        ? { keyMarker: last.nextKeyMarker, versionIdMarker: last.nextVersionIdMarker }
        : undefined,
    enabled,
  })

export const useGetObjectDetails = (bucket: string, key: string | null, versionId?: string) =>
  useQuery({
    queryKey: OBJECT_QUERY_KEYS.UseGetObjectDetails(bucket, key ?? "", versionId),
    queryFn: () => objectService.details(bucket, key!, versionId),
    enabled: key !== null,
  })

export const useRestoreVersion = (bucket: string, options?: ActionOptions) => {
  const invalidate = useInvalidateObjects(bucket)
  return useMutation({
    mutationFn: ({ key, versionId }: { key: string; versionId: string }) =>
      objectService.restore(bucket, key, versionId),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

export const useInvalidateObjects = (bucket: string) => {
  const queryClient = useQueryClient()
  return () =>
    queryClient.invalidateQueries({ queryKey: OBJECT_QUERY_KEYS.UseGetObjectListAll(bucket) })
}

export const useDeleteObjects = (bucket: string, options?: ActionOptions) => {
  const invalidate = useInvalidateObjects(bucket)
  return useMutation({
    mutationFn: (request: DeleteObjectsRequest) => objectService.delete(bucket, request),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

/** A folder is a zero-byte object named `prefix/` (as S3 consoles create it). */
export const useCreateFolder = (bucket: string, options?: ActionOptions) => {
  const invalidate = useInvalidateObjects(bucket)
  return useMutation({
    mutationFn: (prefix: string) => objectService.put(bucket, prefix, new Blob([]), undefined),
    onSuccess: invalidate,
    meta: options?.meta,
  })
}

/** The S3 URL and public state; refetched when the sheet opens (a policy may have changed). */
export const useGetObjectAccess = (bucket: string, key: string | null, versionId?: string) =>
  useQuery({
    queryKey: OBJECT_QUERY_KEYS.UseGetObjectAccess(bucket, key ?? "", versionId),
    queryFn: () => objectService.access(bucket, key!, versionId),
    enabled: key !== null,
    staleTime: 0,
  })

/** The link is a bearer credential: it lives only in this mutation's state, never in the query cache. */
export const useShareObject = (bucket: string, options?: ActionOptions) =>
  useMutation({
    mutationFn: ({
      key,
      versionId,
      seconds,
    }: {
      key: string
      versionId?: string
      seconds: number
    }) => objectService.share(bucket, key, versionId, seconds),
    meta: options?.meta,
  })
