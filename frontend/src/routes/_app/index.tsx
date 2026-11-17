import { useState } from "react"
import { Link, createFileRoute } from "@tanstack/react-router"
import {
  Activity,
  AlertTriangle,
  ArrowUpRight,
  Clock,
  Database,
  HardDrive,
  Plus,
  UploadCloud,
} from "lucide-react"

import { BucketCard } from "@/components/buckets/bucket-views"
import { useBucketStats } from "@/hooks/use-bucket-stats"
import { CreateBucketDialog } from "@/components/buckets/create-bucket-dialog"
import { DeleteBucketDialog } from "@/components/buckets/delete-bucket-dialog"
import { BarChart } from "@/components/charts/bar-chart"
import { ListPage } from "@/components/shared/data-list"
import { PageHeader } from "@/components/shared/page-header"
import { EmptyState, QueryState } from "@/components/shared/query-state"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { useCanWrite } from "@/hooks/api/use-auth"
import { useGetMetrics } from "@/hooks/api/use-metrics"
import { formatBytes, formatUptime, percent } from "@/lib/format"
import { formatLatency, formatRate, lastSample, perSecond } from "@/lib/metrics"
import type { MetricsResponse } from "@/services/api/metrics-service"

export const Route = createFileRoute("/_app/")({
  component: DashboardPage,
})

/** Design: "Welcome back", bucket cards, an analytics card and a list card. */
function DashboardPage() {
  const buckets = useBucketStats()
  const { data: metrics, isLoading, error } = useGetMetrics()
  const canWrite = useCanWrite()
  const [createOpen, setCreateOpen] = useState(false)
  const [toDelete, setToDelete] = useState<string | null>(null)

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Welcome Back"
        description={
          metrics
            ? `Up ${formatUptime(metrics.uptimeSeconds)} · ${metrics.storage.objects.toLocaleString()} objects in ${metrics.storage.buckets} buckets`
            : "This TimeStore node at a glance."
        }
        actions={
          canWrite && (
            <Button size="lg" onClick={() => setCreateOpen(true)}>
              <Plus />
              <span className="hidden sm:inline">New Bucket</span>
            </Button>
          )
        }
      />

      <QueryState isLoading={buckets.isLoading} error={buckets.error} rows={1} />
      {buckets.data && buckets.data.length === 0 && (
        <EmptyState
          icon={<HardDrive />}
          title="No buckets yet"
          description={
            canWrite
              ? "Create your first bucket to start storing objects."
              : "An admin can create buckets."
          }
          action={canWrite && <Button onClick={() => setCreateOpen(true)}>+ Create Bucket</Button>}
        />
      )}
      {buckets.data && buckets.data.length > 0 && (
        <section className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {buckets.data.map((bucket) => (
            <BucketCard
              key={bucket.id}
              bucket={bucket}
              compact
              onDelete={canWrite ? setToDelete : undefined}
            />
          ))}
        </section>
      )}

      <QueryState isLoading={false} error={error} />
      <div className="grid grid-cols-1 gap-5 xl:grid-cols-12">
        <div className="xl:col-span-6">
          {isLoading || !metrics ? (
            <Skeleton className="h-[330px] rounded-3xl" />
          ) : (
            <TrafficCard metrics={metrics} />
          )}
        </div>
        <div className="xl:col-span-6">
          {isLoading || !metrics ? (
            <Skeleton className="h-[330px] rounded-3xl" />
          ) : (
            <NodeCard metrics={metrics} />
          )}
        </div>
      </div>

      <CreateBucketDialog open={createOpen} onOpenChange={setCreateOpen} />
      <DeleteBucketDialog bucket={toDelete} onClose={() => setToDelete(null)} />
    </ListPage>
  )
}

/** Design "Traffic Analytic": S3 requests by type since the server started. */
function TrafficCard({ metrics }: { metrics: MetricsResponse }) {
  const { requests } = metrics
  const bars = [
    { key: "reads", label: "Reads", value: requests.reads, tooltipTitle: "GET, HEAD" },
    {
      key: "writes",
      label: "Writes",
      value: requests.writes,
      tooltipTitle: "PUT, copy, multipart",
    },
    {
      key: "lists",
      label: "Lists",
      value: requests.lists,
      tooltipTitle: "List buckets and objects",
    },
    { key: "deletes", label: "Deletes", value: requests.deletes, tooltipTitle: "DELETE" },
    { key: "other", label: "Other", value: requests.other, tooltipTitle: "Other operations" },
  ]
  const top = bars.reduce((a, b) => (b.value > a.value ? b : a))
  return (
    <div className="rounded-3xl bg-card p-6 shadow-[0_10px_30px_rgba(24,26,36,0.03)] dark:shadow-none">
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h3 className="text-[17px] font-bold tracking-tight text-foreground">S3 Traffic</h3>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Requests by type since the server started
          </p>
        </div>
        <span className="text-xs font-medium text-primary-text tabular-nums">
          {requests.total.toLocaleString()} requests
        </span>
      </div>
      <BarChart bars={bars} initialKey={top.key} format={(value) => compactNumber(value)} />
    </div>
  )
}

const compactNumber = (value: number) =>
  new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 1 }).format(
    Math.round(value)
  )

/** Design "Recent Files" card, used for the node's status (TimeStore has no cross-bucket recent list). */
function NodeCard({ metrics }: { metrics: MetricsResponse }) {
  const { storage, requests, samples, sampleIntervalSeconds: interval } = metrics
  const last = lastSample(samples)
  const diskUsed =
    storage.diskTotalBytes != null && storage.diskFreeBytes != null
      ? storage.diskTotalBytes - storage.diskFreeBytes
      : null
  const rows = [
    {
      icon: <Database />,
      color: "var(--chart-1)",
      title: "Stored",
      sub: `${storage.objects.toLocaleString()} objects · ${storage.buckets} buckets`,
      value: formatBytes(storage.totalBytes),
    },
    {
      icon: <HardDrive />,
      color: "var(--chart-4)",
      title: "Disk",
      sub:
        storage.diskTotalBytes != null
          ? `${formatBytes(storage.diskFreeBytes ?? 0)} free of ${formatBytes(storage.diskTotalBytes)}`
          : "Size of the data volume is unknown",
      value:
        diskUsed != null && storage.diskTotalBytes
          ? `${percent(diskUsed, storage.diskTotalBytes)}%`
          : "—",
    },
    {
      icon: <Activity />,
      color: "var(--chart-3)",
      title: "Request rate",
      sub: `Last ${interval} s`,
      value: last ? formatRate(perSecond(last.requests, interval)) : "—",
    },
    {
      icon: <Clock />,
      color: "var(--chart-2)",
      title: "Latency",
      sub: `p50 ${formatLatency(last?.p50Ms)} · last ${interval} s`,
      value: `p99 ${formatLatency(last?.p99Ms)}`,
    },
    {
      icon: <AlertTriangle />,
      color: "var(--chart-5)",
      title: "Errors",
      sub: `${requests.clientErrors.toLocaleString()} client · ${requests.serverErrors.toLocaleString()} server, since start`,
      value: (requests.clientErrors + requests.serverErrors).toLocaleString(),
    },
    {
      icon: <UploadCloud />,
      color: "var(--file-folder)",
      title: "Incomplete uploads",
      sub: "Multipart uploads not completed yet",
      value: `${storage.incompleteUploads} · ${formatBytes(storage.incompleteUploadBytes)}`,
    },
  ]

  return (
    <div className="flex h-full flex-col rounded-3xl bg-card p-6 shadow-[0_10px_30px_rgba(24,26,36,0.03)] dark:shadow-none">
      <div className="mb-4 flex items-center justify-between">
        <div>
          <h3 className="text-[17px] font-bold tracking-tight text-foreground">Node Status</h3>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Storage, disk and S3 health of this node
          </p>
        </div>
        <Link
          to="/metrics"
          className="inline-flex items-center gap-1 text-xs font-semibold text-primary-text hover:underline"
        >
          <span>Explore</span>
          <ArrowUpRight className="size-3.5" />
        </Link>
      </div>
      <div className="space-y-1.5">
        {rows.map((row) => (
          <div
            key={row.title}
            className="flex items-center justify-between gap-3 rounded-2xl px-3 py-2.5 transition-all duration-150 hover:bg-raised hover:shadow-[0_6px_20px_rgba(24,26,36,0.04)]"
          >
            <div className="flex min-w-0 items-center gap-3.5">
              <span
                className="flex size-11 shrink-0 items-center justify-center rounded-[14px] tint [&_svg]:size-5"
                style={{ "--tint-color": row.color } as React.CSSProperties}
              >
                {row.icon}
              </span>
              <div className="min-w-0">
                <div className="truncate text-[13.5px] font-semibold text-foreground">
                  {row.title}
                </div>
                <div className="truncate text-[11px] text-muted-foreground">{row.sub}</div>
              </div>
            </div>
            <span className="shrink-0 text-xs font-semibold text-subtle-foreground tabular-nums">
              {row.value}
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}
