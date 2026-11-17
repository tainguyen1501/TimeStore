import type { AxiosProgressEvent } from "axios"

import type { components } from "@/@types/api.gen"
import { api } from "@/lib/api"

export type ObjectListResponse = components["schemas"]["ObjectListResponse"]
export type ObjectItemResponse = components["schemas"]["ObjectItemResponse"]
export type FolderResponse = components["schemas"]["FolderResponse"]
export type ObjectDetailsResponse = components["schemas"]["ObjectDetailsResponse"]
export type ObjectWrittenResponse = components["schemas"]["ObjectWrittenResponse"]
export type DeleteObjectsRequest = components["schemas"]["DeleteObjectsRequest"]
export type DeleteObjectsResponse = components["schemas"]["DeleteObjectsResponse"]
export type UploadedPartResponse = components["schemas"]["UploadedPartResponse"]
export type ObjectVersionListResponse = components["schemas"]["ObjectVersionListResponse"]
export type ObjectVersionItemResponse = components["schemas"]["ObjectVersionItemResponse"]
export type ObjectAccessResponse = components["schemas"]["ObjectAccessResponse"]
export type ShareObjectResponse = components["schemas"]["ShareObjectResponse"]

/** Folder separator of the browser (TimeStore.Application ObjectBrowserLimits.Delimiter). */
export const DELIMITER = "/"
/** Entries per listing page. */
export const PAGE_SIZE = 200

const bucketUrl = (bucket: string) => `/buckets/${encodeURIComponent(bucket)}`

type Transfer = { onProgress?: (loaded: number) => void; signal?: AbortSignal }

const progress =
  (onProgress?: (loaded: number) => void) =>
  ({ loaded }: AxiosProgressEvent) =>
    onProgress?.(loaded)

/** The raw bytes are the request body (no form encoding): the server streams them into the blob store. */
const binary = (contentType: string | undefined) => ({
  "Content-Type": contentType || "application/octet-stream",
})

export const objectService = {
  list: async (bucket: string, prefix: string, startAfter?: string) =>
    (
      await api.get<ObjectListResponse>(`${bucketUrl(bucket)}/objects`, {
        params: { prefix, startAfter, limit: PAGE_SIZE },
      })
    ).data,
  listVersions: async (
    bucket: string,
    prefix: string,
    marker?: { keyMarker: string; versionIdMarker?: string | null }
  ) =>
    (
      await api.get<ObjectVersionListResponse>(`${bucketUrl(bucket)}/objects/versions`, {
        params: {
          prefix,
          keyMarker: marker?.keyMarker,
          versionIdMarker: marker?.versionIdMarker ?? undefined,
          limit: PAGE_SIZE,
        },
      })
    ).data,
  details: async (bucket: string, key: string, versionId?: string) =>
    (
      await api.get<ObjectDetailsResponse>(`${bucketUrl(bucket)}/objects/details`, {
        params: { key, versionId },
      })
    ).data,
  /** The object's URL on the S3 port, and whether anyone may open it without signing. */
  access: async (bucket: string, key: string, versionId?: string) =>
    (
      await api.get<ObjectAccessResponse>(`${bucketUrl(bucket)}/objects/access`, {
        params: { key, versionId },
      })
    ).data,
  /** A presigned GET URL signed with the signed-in key (admins only). */
  share: async (
    bucket: string,
    key: string,
    versionId: string | undefined,
    expiresInSeconds: number
  ) =>
    (
      await api.post<ShareObjectResponse>(`${bucketUrl(bucket)}/objects/share`, {
        key,
        versionId: versionId ?? null,
        expiresInSeconds,
      })
    ).data,
  /** A same-origin URL: the browser downloads it with the session cookie (always an attachment). */
  downloadUrl: (bucket: string, key: string, versionId?: string) =>
    `/api/v1${bucketUrl(bucket)}/objects/content?key=${encodeURIComponent(key)}` +
    (versionId ? `&versionId=${encodeURIComponent(versionId)}` : ""),
  /** Copies a version over its key: it becomes the latest version again. */
  restore: async (bucket: string, key: string, versionId: string) =>
    (
      await api.post<ObjectWrittenResponse>(`${bucketUrl(bucket)}/objects/restore`, {
        key,
        versionId,
      })
    ).data,
  put: async (
    bucket: string,
    key: string,
    body: Blob,
    contentType: string | undefined,
    { onProgress, signal }: Transfer = {}
  ) =>
    (
      await api.put<ObjectWrittenResponse>(`${bucketUrl(bucket)}/objects/content`, body, {
        params: { key },
        headers: binary(contentType),
        onUploadProgress: progress(onProgress),
        signal,
      })
    ).data,
  delete: async (bucket: string, request: DeleteObjectsRequest) =>
    (await api.post<DeleteObjectsResponse>(`${bucketUrl(bucket)}/objects/delete`, request)).data,
  startUpload: async (bucket: string, key: string, contentType: string | undefined) =>
    (
      await api.post<components["schemas"]["StartUploadResponse"]>(`${bucketUrl(bucket)}/uploads`, {
        key,
        contentType: contentType || null,
      })
    ).data.uploadId,
  uploadPart: async (
    bucket: string,
    key: string,
    uploadId: string,
    partNumber: number,
    body: Blob,
    { onProgress, signal }: Transfer = {}
  ) =>
    (
      await api.put<UploadedPartResponse>(
        `${bucketUrl(bucket)}/uploads/${encodeURIComponent(uploadId)}/parts/${partNumber}`,
        body,
        {
          params: { key },
          headers: binary(undefined),
          onUploadProgress: progress(onProgress),
          signal,
        }
      )
    ).data,
  completeUpload: async (
    bucket: string,
    key: string,
    uploadId: string,
    parts: UploadedPartResponse[]
  ) =>
    (
      await api.post<ObjectWrittenResponse>(
        `${bucketUrl(bucket)}/uploads/${encodeURIComponent(uploadId)}/complete`,
        { parts },
        { params: { key } }
      )
    ).data,
  abortUpload: async (bucket: string, key: string, uploadId: string) => {
    await api.delete(`${bucketUrl(bucket)}/uploads/${encodeURIComponent(uploadId)}`, {
      params: { key },
    })
  },
}
