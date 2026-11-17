import { useGetBucketList } from "@/hooks/api/use-buckets"
import { useGetMetrics } from "@/hooks/api/use-metrics"
import { percent } from "@/lib/format"
import { VERSIONING, type BucketResponse } from "@/services/api/bucket-service"

/** A bucket with what the metrics API knows of it (objects and bytes, from the usage triggers). */
export type BucketStats = BucketResponse & { objects: number; totalBytes: number; share: number }

/** Buckets joined with their usage; `share` is the bucket's part of all stored bytes (the cards' bar). */
export function useBucketStats() {
  const buckets = useGetBucketList()
  const metrics = useGetMetrics()
  const usage = new Map(metrics.data?.storage.byBucket.map((b) => [b.name, b]) ?? [])
  const total = metrics.data?.storage.totalBytes ?? 0
  const data: BucketStats[] | undefined = buckets.data?.map((bucket) => {
    const used = usage.get(bucket.name)
    const totalBytes = used?.totalBytes ?? 0
    return { ...bucket, objects: used?.objects ?? 0, totalBytes, share: percent(totalBytes, total) }
  })
  return { ...buckets, data }
}

export const versioningLabel = (versioning: string) =>
  versioning === VERSIONING.unversioned
    ? "Versioning Off"
    : versioning === VERSIONING.enabled
      ? "Versioning On"
      : "Versioning Suspended"
