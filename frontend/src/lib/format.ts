const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["day", 86_400],
  ["hour", 3_600],
  ["minute", 60],
]

/** "2m ago", "3h ago", "just now" for an ISO timestamp. */
export function formatAgo(iso: string, now = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  for (const [unit, size] of units) {
    if (seconds >= size) return `${Math.floor(seconds / size)}${unit[0]} ago`
  }
  return "just now"
}

/** Duration between two ISO timestamps, e.g. "4m 12s". */
export function formatDuration(fromIso: string, toIso: string | null, now = Date.now()): string {
  const seconds = Math.max(
    0,
    Math.round(((toIso ? new Date(toIso).getTime() : now) - new Date(fromIso).getTime()) / 1000)
  )
  const minutes = Math.floor(seconds / 60)
  return minutes > 0 ? `${minutes}m ${seconds % 60}s` : `${seconds}s`
}

export const percent = (used: number, total: number) =>
  total > 0 ? Math.round((used / total) * 100) : 0

const byteUnits = ["B", "KB", "MB", "GB", "TB", "PB"]

/** 1536 → "1.5 KB" (binary units, like `df -h`). */
export function formatBytes(bytes: number, decimals = 1): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B"
  const unit = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), byteUnits.length - 1)
  return `${(bytes / 1024 ** unit).toFixed(unit === 0 ? 0 : decimals)} ${byteUnits[unit]}`
}

/** 93784 → "1d 2h", 3700 → "1h 1m", 59 → "59s". */
export function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  const minutes = Math.floor((seconds % 3_600) / 60)
  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m`
  return `${Math.floor(seconds)}s`
}

/**
 * Label of a step count. In a plan (Ansible check mode) `changed` means "would change" and `skipped` means
 * "would run": the step's outcome cannot be predicted without running it.
 */
export function stepCountLabel(status: "changed" | "skipped", plan: boolean) {
  if (!plan) return status
  return status === "changed" ? "would change" : "would run"
}

/** Design date: "16 Nov, 04:15 PM". */
export function formatShortDate(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return (
    date.toLocaleDateString("en-GB", { day: "2-digit", month: "short" }) +
    ", " +
    date.toLocaleTimeString("en-US", { hour: "2-digit", minute: "2-digit" })
  )
}
