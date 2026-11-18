import { useState } from "react"

import { cn } from "@/lib/utils"

export type Bar = { key: string; label: string; value: number; tooltipTitle?: string }

/** Bar fills in the design's order: soft lavender, coral, mist, sky, amber (theme tokens). */
const FILLS = ["bg-primary-soft", "bg-chart-2", "bg-muted", "bg-chart-3", "bg-chart-4"]

/**
 * The design's rounded pill bars with a value axis on the left and a hover card above the active bar. Purely
 * presentational: values and their formatting come from the caller.
 */
export function BarChart({
  bars,
  format,
  initialKey,
  className,
}: {
  bars: Bar[]
  format: (value: number) => string
  initialKey?: string
  className?: string
}) {
  const [active, setActive] = useState(initialKey ?? bars.at(-1)?.key)
  const max = Math.max(1, ...bars.map((b) => b.value))
  // Value labels top to bottom; counts are whole numbers, so small maxima would repeat a label ("1, 1, 0, 0").
  const axis = [...new Set([max, max * 0.6, max * 0.25, 0].map((v) => Math.round(v)))]

  return (
    <div className={cn("flex flex-col", className)}>
      {/* The plot fills the height the caller gives it, at least the design's 160 px of bars. */}
      <div className="flex min-h-[210px] flex-1 gap-4 pt-8">
        <div className="flex shrink-0 flex-col justify-between pb-1 text-[11px] text-muted-foreground tabular-nums">
          {axis.map((value, i) => (
            <span key={i}>{format(value)}</span>
          ))}
        </div>
        <div
          className="grid flex-1 gap-3"
          style={{ gridTemplateColumns: `repeat(${bars.length}, minmax(0, 1fr))` }}
        >
          {bars.map((bar, i) => {
            const selected = bar.key === active
            const height = bar.value > 0 ? Math.max(8, Math.round((bar.value / max) * 100)) : 4
            return (
              <div
                key={bar.key}
                className="group relative flex h-full flex-col items-center justify-end"
                onMouseEnter={() => setActive(bar.key)}
              >
                {selected && (
                  <div className="pointer-events-none absolute -top-11 left-1/2 z-10 -translate-x-1/2 rounded-xl bg-popover px-3 py-1.5 whitespace-nowrap shadow-[0_8px_24px_rgba(17,24,39,0.12)] dark:shadow-[0_8px_24px_rgba(0,0,0,0.45)]">
                    <div className="text-[10px] leading-tight text-muted-foreground">
                      {bar.tooltipTitle ?? bar.label}
                    </div>
                    <div className="mt-0.5 text-xs leading-tight font-bold text-chart-3 tabular-nums">
                      {format(bar.value)}
                    </div>
                  </div>
                )}
                <div className="flex h-full w-full items-end justify-center pb-1">
                  <div
                    style={{ height: `${height}%` }}
                    className={cn(
                      "w-full max-w-[26px] rounded-full transition-all duration-300",
                      FILLS[i % FILLS.length],
                      selected
                        ? "scale-y-105 shadow-md brightness-105"
                        : "opacity-90 group-hover:opacity-100"
                    )}
                  />
                </div>
              </div>
            )
          })}
        </div>
      </div>
      <div className="mt-2 flex gap-4">
        <div className="w-[38px] shrink-0" />
        <div
          className="grid flex-1 gap-3 text-center"
          style={{ gridTemplateColumns: `repeat(${bars.length}, minmax(0, 1fr))` }}
        >
          {bars.map((bar) => (
            <button
              key={bar.key}
              type="button"
              onClick={() => setActive(bar.key)}
              className={cn(
                "truncate text-xs transition-colors",
                bar.key === active
                  ? "font-bold text-chart-3"
                  : "text-muted-foreground hover:text-foreground"
              )}
            >
              {bar.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}
