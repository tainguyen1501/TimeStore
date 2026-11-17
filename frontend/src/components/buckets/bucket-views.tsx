import { Link, useNavigate } from "@tanstack/react-router"
import { ExternalLink, HardDrive, MoreHorizontal, Trash2 } from "lucide-react"

import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { versioningLabel, type BucketStats } from "@/hooks/use-bucket-stats"
import { bucketPalette, paletteStyle } from "@/lib/bucket-palette"
import { formatAgo, formatBytes } from "@/lib/format"
import { cn } from "@/lib/utils"
import { VERSIONING } from "@/services/api/bucket-service"

const objectsLabel = (n: number) => `${n.toLocaleString()} ${n === 1 ? "object" : "objects"}`

/** The bar under a card: the bucket's share of all stored bytes (TimeStore has no bucket quotas). */
function ShareBar({ bucket, className }: { bucket: BucketStats; className?: string }) {
  const width = bucket.totalBytes > 0 ? Math.max(6, bucket.share) : 0
  return (
    <div className={className}>
      <div className="mb-1.5 flex items-center justify-between text-[11px] font-semibold text-subtle-foreground tabular-nums">
        <span>{formatBytes(bucket.totalBytes)}</span>
        <span title="Share of all stored bytes">{bucket.share}%</span>
      </div>
      <div className="h-1.5 w-full overflow-hidden rounded-full tint-strong">
        <div
          style={{ width: `${width}%`, backgroundColor: "var(--tint-color)" }}
          className="h-full rounded-full transition-all duration-300"
        />
      </div>
    </div>
  )
}

/** Open / delete (admins). */
export function BucketMenu({
  bucket,
  onDelete,
  className,
}: {
  bucket: string
  onDelete?: (bucket: string) => void
  className?: string
}) {
  const navigate = useNavigate()
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={`Actions for ${bucket}`}
        title="Bucket actions"
        onClick={(event) => event.stopPropagation()}
        className={cn(
          "rounded-lg p-1.5 text-muted-foreground transition-colors hover:text-foreground aria-expanded:text-foreground",
          className
        )}
      >
        <MoreHorizontal className="size-4" />
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        className="w-44"
        onClick={(event) => event.stopPropagation()}
      >
        <DropdownMenuItem
          onClick={() => void navigate({ to: "/buckets/$bucket", params: { bucket } })}
        >
          <ExternalLink />
          Open
        </DropdownMenuItem>
        {onDelete && (
          <DropdownMenuItem variant="destructive" onClick={() => onDelete(bucket)}>
            <Trash2 />
            Delete
          </DropdownMenuItem>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/** Design: pastel card with a round icon, name, a line of facts and the usage bar. */
export function BucketCard({
  bucket,
  onDelete,
  compact = false,
}: {
  bucket: BucketStats
  onDelete?: (bucket: string) => void
  /** The dashboard's smaller card. */
  compact?: boolean
}) {
  const navigate = useNavigate()
  const style = paletteStyle(bucketPalette(bucket.name))
  return (
    <div
      role="link"
      tabIndex={0}
      onClick={() => void navigate({ to: "/buckets/$bucket", params: { bucket: bucket.name } })}
      onKeyDown={(event) =>
        event.key === "Enter" &&
        void navigate({ to: "/buckets/$bucket", params: { bucket: bucket.name } })
      }
      style={style}
      className={cn(
        "group relative cursor-pointer rounded-2xl tint text-foreground transition-all duration-150 outline-none hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-ring/40",
        compact ? "p-4" : "p-5"
      )}
    >
      <div className={cn("flex items-center justify-between", compact ? "mb-3.5" : "mb-4")}>
        <div
          className={cn(
            "flex items-center justify-center rounded-full bg-raised shadow-xs",
            compact ? "size-10" : "size-11"
          )}
          style={{ color: "var(--tint-color)" }}
        >
          <HardDrive className={compact ? "size-4" : "size-5"} />
        </div>
        <BucketMenu bucket={bucket.name} onDelete={onDelete} />
      </div>
      <div
        className={cn(
          "truncate font-bold text-foreground",
          compact ? "text-[14.5px]" : "text-[15px]"
        )}
      >
        {bucket.name}
      </div>
      <div className="mt-0.5 truncate text-[11px] text-muted-foreground tabular-nums">
        {compact
          ? `${objectsLabel(bucket.objects)} · ${versioningLabel(bucket.versioning)}`
          : versioningLabel(bucket.versioning)}
      </div>
      {!compact && (
        <div className="mt-2 text-[11px] font-semibold text-subtle-foreground tabular-nums">
          {objectsLabel(bucket.objects)}
        </div>
      )}
      <ShareBar bucket={bucket} className={compact ? "mt-4" : "mt-3"} />
    </div>
  )
}

/** Design: list row (icon tile, name, facts, counts, bar, menu) inside a tonal container. */
export function BucketRow({
  bucket,
  onDelete,
}: {
  bucket: BucketStats
  onDelete?: (bucket: string) => void
}) {
  const style = paletteStyle(bucketPalette(bucket.name))
  return (
    <Link
      to="/buckets/$bucket"
      params={{ bucket: bucket.name }}
      style={style}
      className="flex flex-wrap items-center justify-between gap-4 rounded-2xl px-4 py-3.5 transition-colors hover:bg-raised sm:flex-nowrap"
    >
      <div className="flex min-w-0 flex-1 items-center gap-3.5">
        <div className="flex size-10 shrink-0 items-center justify-center rounded-2xl tint">
          <HardDrive className="size-4" />
        </div>
        <div className="min-w-0">
          <div className="truncate text-sm font-bold text-foreground">{bucket.name}</div>
          <div className="truncate text-[11px] text-muted-foreground">
            {versioningLabel(bucket.versioning)}
          </div>
        </div>
      </div>
      <div className="flex shrink-0 items-center gap-6">
        <div className="hidden text-right md:block">
          <div className="text-xs font-semibold text-foreground tabular-nums">
            {objectsLabel(bucket.objects)}
          </div>
          <div className="text-[11px] text-muted-foreground tabular-nums" title={bucket.createdAt}>
            created {formatAgo(bucket.createdAt)}
          </div>
        </div>
        <ShareBar bucket={bucket} className="w-32" />
        <BucketMenu bucket={bucket.name} onDelete={onDelete} className="hover:bg-field" />
      </div>
    </Link>
  )
}

/** Design: table mode with icon, versioning, objects, usage bar, created, actions. */
export function BucketTable({
  buckets,
  onDelete,
}: {
  buckets: BucketStats[]
  onDelete?: (bucket: string) => void
}) {
  const navigate = useNavigate()
  return (
    <div className="overflow-x-auto rounded-3xl bg-surface p-3">
      <table className="w-full min-w-[680px] border-collapse text-left">
        <thead>
          <tr className="text-[11px] font-semibold text-muted-foreground">
            <th className="px-4 py-2.5">Bucket Name</th>
            <th className="px-3 py-2.5">Versioning</th>
            <th className="px-3 py-2.5 text-right">Objects</th>
            <th className="px-4 py-2.5">Storage Usage</th>
            <th className="px-3 py-2.5 text-right">Created</th>
            <th className="px-4 py-2.5 text-right">Actions</th>
          </tr>
        </thead>
        <tbody>
          {buckets.map((bucket) => (
            <tr
              key={bucket.id}
              style={paletteStyle(bucketPalette(bucket.name))}
              onClick={() =>
                void navigate({ to: "/buckets/$bucket", params: { bucket: bucket.name } })
              }
              className="group cursor-pointer transition-colors hover:bg-raised"
            >
              <td className="rounded-l-2xl px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-xl tint">
                    <HardDrive className="size-4" />
                  </div>
                  <span className="text-xs font-bold text-foreground">{bucket.name}</span>
                </div>
              </td>
              <td className="px-3 py-3 text-xs text-subtle-foreground">
                {bucket.versioning === VERSIONING.unversioned ? "Disabled" : bucket.versioning}
              </td>
              <td className="px-3 py-3 text-right text-xs font-semibold text-foreground tabular-nums">
                {bucket.objects.toLocaleString()}
              </td>
              <td className="px-4 py-3">
                <ShareBar bucket={bucket} className="w-36" />
              </td>
              <td
                className="px-3 py-3 text-right text-[11px] whitespace-nowrap text-muted-foreground tabular-nums"
                title={bucket.createdAt}
              >
                {formatAgo(bucket.createdAt)}
              </td>
              <td
                className="rounded-r-2xl px-4 py-3 text-right"
                onClick={(event) => event.stopPropagation()}
              >
                <div className="inline-flex items-center gap-1">
                  <Link
                    to="/buckets/$bucket"
                    params={{ bucket: bucket.name }}
                    className="rounded-lg bg-primary-soft px-2.5 py-1 text-[11px] font-semibold text-primary-text"
                  >
                    Open
                  </Link>
                  <BucketMenu bucket={bucket.name} onDelete={onDelete} />
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
