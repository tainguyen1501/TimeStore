import { Link, createFileRoute } from "@tanstack/react-router"

import { OverviewCards } from "@/components/metrics/overview-cards"
import { ListPage } from "@/components/shared/data-list"
import { StatCard } from "@/components/shared/metric"
import { PageHeader } from "@/components/shared/page-header"
import { EmptyState, QueryState } from "@/components/shared/query-state"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useGetMetrics } from "@/hooks/api/use-metrics"
import { formatBytes, formatUptime, percent } from "@/lib/format"
import { formatLatency, formatRate, lastSample, perSecond, rates } from "@/lib/metrics"
import type { MetricsResponse } from "@/services/api/metrics-service"

export const Route = createFileRoute("/_app/metrics/")({
  component: MetricsPage,
})

function MetricsPage() {
  const { data: metrics, isLoading, error } = useGetMetrics()

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Metrics"
        description={
          metrics
            ? `S3 traffic since the server started ${formatUptime(metrics.uptimeSeconds)} ago; charts show the last 15 minutes (one point every ${metrics.sampleIntervalSeconds} s, kept in memory).`
            : "Storage and S3 traffic of this node."
        }
      />
      <QueryState isLoading={isLoading} error={error} rows={2} />
      {metrics && (
        <>
          <OverviewCards metrics={metrics} />
          <Traffic metrics={metrics} />
          <div className="grid gap-4 lg:grid-cols-[2fr_1fr]">
            <BucketTable metrics={metrics} />
            <Totals metrics={metrics} />
          </div>
        </>
      )}
    </ListPage>
  )
}

function Traffic({ metrics }: { metrics: MetricsResponse }) {
  const { samples, sampleIntervalSeconds: interval } = metrics
  const last = lastSample(samples)
  const rate = (value: number | undefined) =>
    value === undefined ? "—" : formatRate(perSecond(value, interval))
  const bytesRate = (value: number | undefined) =>
    value === undefined ? "—" : `${formatBytes(perSecond(value, interval))}/s`

  return (
    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
      <StatCard
        title="Upload"
        value={bytesRate(last?.bytesIn)}
        sub="Request bodies on the S3 port"
        history={rates(samples, (s) => s.bytesIn, interval)}
        accent="chart-1"
      />
      <StatCard
        title="Download"
        value={bytesRate(last?.bytesOut)}
        sub="Response bodies with a known length"
        history={rates(samples, (s) => s.bytesOut, interval)}
        accent="chart-2"
      />
      <StatCard
        title="Client errors (4xx)"
        value={rate(last?.clientErrors)}
        sub="Denied, missing or invalid requests"
        history={rates(samples, (s) => s.clientErrors, interval)}
        accent="chart-4"
      />
      <StatCard
        title="Server errors (5xx)"
        value={rate(last?.serverErrors)}
        sub="Should stay at zero"
        history={rates(samples, (s) => s.serverErrors, interval)}
        accent="chart-5"
      />
    </div>
  )
}

function BucketTable({ metrics }: { metrics: MetricsResponse }) {
  const { storage } = metrics
  const buckets = [...storage.byBucket].sort((a, b) => b.totalBytes - a.totalBytes)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Buckets by size</CardTitle>
      </CardHeader>
      <CardContent>
        {buckets.length === 0 ? (
          <EmptyState title="No buckets yet" />
        ) : (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Bucket</TableHead>
                <TableHead className="text-right">Objects</TableHead>
                <TableHead className="text-right">Size</TableHead>
                <TableHead className="w-24 text-right">Share</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {buckets.map((bucket) => (
                <TableRow key={bucket.name}>
                  <TableCell className="font-mono">
                    <Link
                      to="/buckets/$bucket"
                      params={{ bucket: bucket.name }}
                      className="font-semibold text-foreground hover:text-primary-text"
                    >
                      {bucket.name}
                    </Link>
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {bucket.objects.toLocaleString()}
                  </TableCell>
                  <TableCell className="text-right tabular-nums">
                    {formatBytes(bucket.totalBytes)}
                  </TableCell>
                  <TableCell className="text-right text-muted-foreground tabular-nums">
                    {percent(bucket.totalBytes, storage.totalBytes)}%
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}

function Totals({ metrics }: { metrics: MetricsResponse }) {
  const { requests, storage } = metrics
  const rows: [string, string][] = [
    ["Requests", requests.total.toLocaleString()],
    ["Reads (GET, HEAD)", requests.reads.toLocaleString()],
    ["Writes (PUT, copy, multipart)", requests.writes.toLocaleString()],
    ["Lists", requests.lists.toLocaleString()],
    ["Deletes", requests.deletes.toLocaleString()],
    ["Other", requests.other.toLocaleString()],
    ["Client errors", requests.clientErrors.toLocaleString()],
    ["Server errors", requests.serverErrors.toLocaleString()],
    ["Uploaded", formatBytes(requests.bytesIn)],
    ["Downloaded", formatBytes(requests.bytesOut)],
    ["Latency p50 / p99", `${formatLatency(requests.p50Ms)} / ${formatLatency(requests.p99Ms)}`],
    [
      "Incomplete uploads",
      `${storage.incompleteUploads} (${formatBytes(storage.incompleteUploadBytes)})`,
    ],
  ]

  return (
    <Card>
      <CardHeader>
        <CardTitle>Since start</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-1.5 text-xs">
          {rows.map(([label, value]) => (
            <div key={label} className="contents">
              <dt className="text-muted-foreground">{label}</dt>
              <dd className="text-right font-mono tabular-nums">{value}</dd>
            </div>
          ))}
        </dl>
      </CardContent>
    </Card>
  )
}
