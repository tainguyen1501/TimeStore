import { useId } from "react"

import { cn } from "@/lib/utils"

// A sparkline and a usage bar without a chart library.
// Colours come from the theme's chart tokens through `text-*` / `bg-*` classes.

export type Accent = "chart-1" | "chart-2" | "chart-3" | "chart-4" | "chart-5"

const TEXT: Record<Accent, string> = {
  "chart-1": "text-chart-1",
  "chart-2": "text-chart-2",
  "chart-3": "text-chart-3",
  "chart-4": "text-chart-4",
  "chart-5": "text-chart-5",
}

const BG: Record<Accent, string> = {
  "chart-1": "bg-chart-1",
  "chart-2": "bg-chart-2",
  "chart-3": "bg-chart-3",
  "chart-4": "bg-chart-4",
  "chart-5": "bg-chart-5",
}

/** A number with a label, an optional line and an optional usage bar. */
export function StatCard({
  title,
  value,
  sub,
  history,
  usage,
  accent = "chart-1",
}: {
  title: string
  value: React.ReactNode
  sub?: React.ReactNode
  history?: number[]
  /** 0–100: a bar under the value (disk use). */
  usage?: number
  accent?: Accent
}) {
  return (
    <div className="flex flex-col justify-between gap-3 rounded-3xl bg-surface p-5">
      <span className="text-[13px] font-bold text-foreground">{title}</span>
      {/* The value keeps its own line (a unit never wraps under its number); the line and the chart share the next. */}
      <div className="min-w-0">
        <div className="truncate font-heading text-[22px] font-extrabold tracking-tight whitespace-nowrap text-foreground tabular-nums">
          {value}
        </div>
        <div className="mt-1 flex items-end justify-between gap-3">
          <p className="min-w-0 text-[11px] text-muted-foreground">{sub}</p>
          {history && (
            <Sparkline values={history} className={cn("h-9 w-24 shrink-0", TEXT[accent])} />
          )}
        </div>
      </div>
      {usage !== undefined && <UsageBar value={usage} className={BG[accent]} />}
    </div>
  )
}

const WIDTH = 160
const HEIGHT = 48
const PADDING = 4

/** Line + gradient area + last point. Uses `currentColor`, so set the colour with a text-* class. */
export function Sparkline({ values, className }: { values: number[]; className?: string }) {
  // useId() contains characters (":", "«") that break a url(#id) reference; keep only safe ones.
  const gradientId = `spark-${useId().replace(/[^a-zA-Z0-9_-]/g, "")}`
  if (values.length === 0) return <div className={className} />

  const series = values.length === 1 ? [values[0], values[0]] : values
  const min = Math.min(...series, 0)
  const max = Math.max(...series, 1)
  const range = max - min || 1
  const points = series.map((value, i) => {
    const x = PADDING + (i / Math.max(series.length - 1, 1)) * (WIDTH - 2 * PADDING)
    const y = HEIGHT - PADDING - ((value - min) / range) * (HEIGHT - 2 * PADDING)
    return [x, y] as const
  })
  const coords = points.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`)
  const line = `M ${coords.join(" L ")}`
  const area = `M ${coords[0]} L ${coords.join(" L ")} L ${WIDTH - PADDING},${HEIGHT} L ${PADDING},${HEIGHT} Z`
  const [lastX, lastY] = points[points.length - 1]

  return (
    <div className={className}>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        className="h-full w-full overflow-visible"
        aria-hidden
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.4" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <path d={area} fill={`url(#${gradientId})`} />
        <path
          d={line}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.8"
          strokeLinecap="round"
          strokeLinejoin="round"
          vectorEffect="non-scaling-stroke"
        />
        <circle cx={lastX} cy={lastY} r="2.5" fill="currentColor" />
      </svg>
    </div>
  )
}

export function UsageBar({ value, className }: { value: number; className?: string }) {
  const width = Math.min(100, Math.max(0, value))
  return (
    <div
      className="h-1.5 w-full overflow-hidden rounded-full bg-muted"
      role="meter"
      aria-valuenow={Math.round(width)}
      aria-valuemin={0}
      aria-valuemax={100}
    >
      <div
        className={cn("h-full rounded-full bg-primary transition-all duration-500", className)}
        style={{ width: `${width}%` }}
      />
    </div>
  )
}
