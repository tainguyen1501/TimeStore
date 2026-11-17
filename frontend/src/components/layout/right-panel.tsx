import { Link, useNavigate } from "@tanstack/react-router"
import { HardDrive, HelpCircle, LogOut, Moon, Settings, Sun } from "lucide-react"

import { Donut } from "@/components/charts/donut"
import { Skeleton } from "@/components/ui/skeleton"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { useGetMe, useLogout } from "@/hooks/api/use-auth"
import { useGetMetrics } from "@/hooks/api/use-metrics"
import { bucketPalette, paletteColor } from "@/lib/bucket-palette"
import { formatBytes } from "@/lib/format"
import { ADMIN_ROLES } from "@/services/api/auth-service"
import { useThemeStore } from "@/store/theme"

/** "Other" in the ring; a bucket uses its own palette colour (the same as its card). */
const OTHER_COLOR = "var(--chart-5)"
const TOP = 4

const ROLE_LABELS: Record<string, string> = {
  [ADMIN_ROLES.admin]: "Storage Admin",
  [ADMIN_ROLES.viewer]: "Viewer · read-only",
}

/** Design: profile row with theme and settings buttons, "Storage Details" ring, S3 endpoint link at the bottom. */
export function RightPanel() {
  return (
    <>
      <div>
        <ProfileRow />
        <StorageDetails />
      </div>
      <StorageFooter />
    </>
  )
}

function ProfileRow() {
  const { data: session } = useGetMe()
  const logout = useLogout()
  const navigate = useNavigate()
  const { theme, toggle } = useThemeStore()
  const initials = (session?.accessKeyId ?? "")
    .replace(/[^a-z0-9]/gi, "")
    .slice(0, 2)
    .toUpperCase()

  const signOut = () =>
    // Leave even if the call fails: from the user's point of view the session is over either way.
    logout.mutate(undefined, { onSettled: () => void navigate({ to: "/login", replace: true }) })

  return (
    <div className="mb-7 flex items-center justify-between gap-3">
      <DropdownMenu>
        <DropdownMenuTrigger
          className="flex min-w-0 items-center gap-3 rounded-2xl text-left outline-none focus-visible:ring-2 focus-visible:ring-ring/30"
          aria-label="Account"
        >
          <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-primary-soft text-xs font-bold text-primary-text">
            {initials || "?"}
          </span>
          <span className="min-w-0">
            <span className="block truncate font-mono text-[13px] font-bold text-foreground">
              {session?.accessKeyId}
            </span>
            <span className="block truncate text-[11px] text-muted-foreground">
              {session && (ROLE_LABELS[session.role] ?? session.role)}
            </span>
          </span>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-64">
          <DropdownMenuGroup>
            <DropdownMenuLabel className="space-y-0.5">
              <div className="truncate font-mono text-sm font-semibold text-foreground">
                {session?.accessKeyId}
              </div>
              {session && (
                <div
                  className="text-xs font-normal text-muted-foreground"
                  title={session.expiresAt}
                >
                  Session ends {new Date(session.expiresAt).toLocaleString()}
                </div>
              )}
            </DropdownMenuLabel>
          </DropdownMenuGroup>
          <DropdownMenuSeparator />
          <DropdownMenuItem onClick={signOut} disabled={logout.isPending}>
            <LogOut />
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <div className="flex shrink-0 items-center gap-1.5">
        <button
          type="button"
          onClick={toggle}
          title={theme === "light" ? "Switch to dark mode" : "Switch to light mode"}
          aria-label={theme === "light" ? "Switch to dark mode" : "Switch to light mode"}
          className="flex size-9 items-center justify-center rounded-full bg-field text-subtle-foreground transition-colors hover:bg-surface-hover dark:text-warning"
        >
          {theme === "light" ? <Moon className="size-4" /> : <Sun className="size-4" />}
        </button>
        <Link
          to="/settings"
          title="Settings"
          aria-label="Settings"
          className="flex size-9 items-center justify-center rounded-full bg-field text-subtle-foreground transition-colors hover:bg-surface-hover"
        >
          <Settings className="size-4" />
        </Link>
      </div>
    </div>
  )
}

/** The design's "Storage Details", by bucket: the four largest and the rest. */
function StorageDetails() {
  const { data: metrics, isLoading } = useGetMetrics()
  const storage = metrics?.storage
  const sorted = [...(storage?.byBucket ?? [])].sort((a, b) => b.totalBytes - a.totalBytes)
  const top = sorted
    .slice(0, TOP)
    .map((bucket) => ({ ...bucket, color: paletteColor(bucketPalette(bucket.name)) }))
  const rest = sorted.slice(TOP)
  const other = rest.length
    ? {
        name: `${rest.length} more`,
        objects: rest.reduce((n, b) => n + b.objects, 0),
        totalBytes: rest.reduce((n, b) => n + b.totalBytes, 0),
        color: OTHER_COLOR,
      }
    : null
  const rows = other ? [...top, other] : top
  const diskUsed =
    storage?.diskTotalBytes != null && storage.diskFreeBytes != null
      ? storage.diskTotalBytes - storage.diskFreeBytes
      : null

  return (
    <div className="flex flex-col">
      <div className="mb-4 flex items-center justify-between">
        <h2 className="text-[18px] font-bold tracking-tight text-foreground">Storage Details</h2>
        <Link
          to="/metrics"
          className="text-xs font-medium text-muted-foreground transition-colors hover:text-primary-text"
        >
          All metrics
        </Link>
      </div>

      {isLoading || !storage ? (
        <Skeleton className="mx-auto my-3 size-[172px] rounded-full" />
      ) : (
        <Donut
          className="my-3"
          segments={rows.map((row) => ({ key: row.name, value: row.totalBytes, color: row.color }))}
          center={formatBytes(storage.totalBytes)}
          caption={
            storage.diskTotalBytes != null && diskUsed != null
              ? `of ${formatBytes(storage.diskTotalBytes, 0)} disk`
              : `${storage.objects.toLocaleString()} objects`
          }
        />
      )}

      {rows.length > 0 && (
        <div className="mt-3 mb-5 grid grid-cols-2 gap-x-3 gap-y-2 px-1">
          {rows.map((row) => (
            <span
              key={row.name}
              className="flex min-w-0 items-center gap-2 text-xs text-muted-foreground"
            >
              <span
                className="size-2.5 shrink-0 rounded-full"
                style={{ backgroundColor: row.color }}
              />
              <span className="truncate">{row.name}</span>
            </span>
          ))}
        </div>
      )}

      <div className="space-y-3">
        {rows.map((row) => {
          const isBucket = row !== other
          const content = (
            <>
              <span className="flex min-w-0 items-center gap-3">
                <span
                  className="flex size-10 shrink-0 items-center justify-center rounded-xl tint"
                  style={{ "--tint-color": row.color } as React.CSSProperties}
                >
                  <HardDrive className="size-4" />
                </span>
                <span className="min-w-0">
                  <span className="block truncate text-[13px] font-semibold text-foreground">
                    {row.name}
                  </span>
                  <span className="block text-[11px] text-muted-foreground tabular-nums">
                    {row.objects.toLocaleString()} {row.objects === 1 ? "Object" : "Objects"}
                  </span>
                </span>
              </span>
              <span className="shrink-0 text-xs font-semibold text-subtle-foreground tabular-nums">
                {formatBytes(row.totalBytes)}
              </span>
            </>
          )
          const className =
            "flex w-full items-center justify-between gap-3 rounded-2xl bg-surface p-3.5 text-left transition-colors hover:bg-surface-hover"
          return isBucket ? (
            <Link
              key={row.name}
              to="/buckets/$bucket"
              params={{ bucket: row.name }}
              className={className}
            >
              {content}
            </Link>
          ) : (
            <Link key={row.name} to="/buckets" className={className}>
              {content}
            </Link>
          )
        })}
        {storage && rows.length === 0 && (
          <p className="rounded-2xl bg-surface p-4 text-center text-xs text-muted-foreground">
            No buckets yet.
          </p>
        )}
      </div>
    </div>
  )
}

function StorageFooter() {
  const buckets = useGetMetrics().data?.storage.buckets
  return (
    <div className="flex items-center justify-between pt-2 text-[11px] text-muted-foreground">
      <Link
        to="/access-keys"
        className="inline-flex items-center gap-1.5 transition-colors hover:text-primary-text"
      >
        <HelpCircle className="size-3.5" />
        <span>S3 Endpoint Info</span>
      </Link>
      {buckets !== undefined && (
        <span className="font-mono tabular-nums">
          {buckets} {buckets === 1 ? "bucket" : "buckets"}
        </span>
      )}
    </div>
  )
}
