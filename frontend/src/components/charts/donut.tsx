import { cn } from "@/lib/utils"

export type DonutSegment = { key: string; value: number; color: string }

const SIZE = 172
const RADIUS = 64
const STROKE = 16
const GAP = 4

const MIN_ARC = 12

/**
 * Arc lengths and offsets: segments share the ring (minus the gaps) in proportion to their values; a tiny value still
 * gets `MIN_ARC`, taken back from the largest arc.
 */
function layoutArcs(segments: DonutSegment[], circumference: number) {
  const visible = segments.filter((s) => s.value > 0)
  const total = visible.reduce((sum, s) => sum + s.value, 0)
  const usable = circumference - visible.length * GAP
  const lengths = visible.map((s) =>
    total > 0 ? Math.max(MIN_ARC, (s.value / total) * usable) : 0
  )
  const overflow = lengths.reduce((sum, l) => sum + l, 0) - usable
  if (overflow > 0 && lengths.length > 0) {
    const largest = lengths.indexOf(Math.max(...lengths))
    lengths[largest] -= overflow
  }
  const offsets = lengths.map((_, i) => lengths.slice(0, i).reduce((sum, l) => sum + l + GAP, 0))
  return visible.map((segment, i) => ({ ...segment, length: lengths[i], offset: offsets[i] }))
}

/**
 * The design's storage ring: rounded segments on a quiet track, a big number in the middle. Segments share the whole
 * ring in proportion to their values (a small value still gets a visible arc).
 */
export function Donut({
  segments,
  center,
  caption,
  className,
  onSelect,
}: {
  segments: DonutSegment[]
  center: React.ReactNode
  caption?: React.ReactNode
  className?: string
  onSelect?: (key: string) => void
}) {
  const circumference = 2 * Math.PI * RADIUS
  const arcs = layoutArcs(segments, circumference)

  return (
    <div className={cn("relative flex items-center justify-center", className)}>
      <svg className="size-[172px] -rotate-90" viewBox={`0 0 ${SIZE} ${SIZE}`} aria-hidden>
        <circle
          cx={SIZE / 2}
          cy={SIZE / 2}
          r={RADIUS}
          fill="none"
          strokeWidth={STROKE}
          className="stroke-muted"
        />
        {arcs.map((arc) => (
          <circle
            key={arc.key}
            cx={SIZE / 2}
            cy={SIZE / 2}
            r={RADIUS}
            fill="none"
            stroke={arc.color}
            strokeWidth={STROKE}
            strokeLinecap="round"
            strokeDasharray={`${Math.max(4, arc.length - GAP)} ${circumference}`}
            strokeDashoffset={-arc.offset}
            className={cn(
              "transition-all duration-500",
              onSelect && "cursor-pointer hover:opacity-85"
            )}
            onClick={onSelect ? () => onSelect(arc.key) : undefined}
          />
        ))}
      </svg>
      <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center text-center">
        <span className="font-heading text-[22px] font-extrabold tracking-tight text-foreground tabular-nums">
          {center}
        </span>
        {caption && (
          <span className="mt-0.5 text-xs text-muted-foreground tabular-nums">{caption}</span>
        )}
      </div>
    </div>
  )
}
