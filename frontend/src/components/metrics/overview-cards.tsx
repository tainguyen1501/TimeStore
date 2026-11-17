import { StatCard } from "@/components/shared/metric"
import { formatBytes, percent } from "@/lib/format"
import { formatLatency, formatRate, lastSample, perSecond, rates } from "@/lib/metrics"
import type { MetricsResponse } from "@/services/api/metrics-service"

/** Headline numbers: storage, disk, request rate and latency, each with its last 15 minutes. */
export function OverviewCards({ metrics }: { metrics: MetricsResponse }) {
  const { storage, samples, sampleIntervalSeconds: interval } = metrics
  const last = lastSample(samples)
  const diskUsed =
    storage.diskTotalBytes != null && storage.diskFreeBytes != null
      ? storage.diskTotalBytes - storage.diskFreeBytes
      : null

  return (
    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <StatCard
        title="Stored"
        value={formatBytes(storage.totalBytes)}
        sub={`${storage.objects.toLocaleString()} objects in ${storage.buckets} bucket${storage.buckets === 1 ? "" : "s"}`}
        history={samples.map((s) => s.totalBytes)}
        accent="chart-1"
      />
      <StatCard
        title="Disk"
        value={diskUsed != null ? formatBytes(diskUsed) : "—"}
        sub={
          storage.diskTotalBytes != null
            ? `used of ${formatBytes(storage.diskTotalBytes)} (${formatBytes(storage.diskFreeBytes ?? 0)} free)`
            : "Size of the data volume is unknown"
        }
        usage={
          diskUsed != null && storage.diskTotalBytes
            ? percent(diskUsed, storage.diskTotalBytes)
            : undefined
        }
        accent="chart-4"
      />
      <StatCard
        title="S3 requests"
        value={last ? formatRate(perSecond(last.requests, interval)) : "—"}
        sub={
          last
            ? `${last.clientErrors + last.serverErrors} errors in the last ${interval} s`
            : "Collecting the first sample"
        }
        history={rates(samples, (s) => s.requests, interval)}
        accent="chart-2"
      />
      <StatCard
        title="Latency (p99)"
        value={formatLatency(last?.p99Ms)}
        sub={`p50 ${formatLatency(last?.p50Ms)}, last ${interval} s`}
        history={samples.map((s) => s.p99Ms ?? 0)}
        accent="chart-3"
      />
    </div>
  )
}
