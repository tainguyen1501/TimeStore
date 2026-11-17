import { getErrorMessage } from "@/lib/api-response"
import { Skeleton } from "@/components/ui/skeleton"

/** Loading and error placeholders shared by pages that wait on a query. */
export function QueryState({
  isLoading,
  error,
  rows = 4,
}: {
  isLoading: boolean
  error: unknown
  rows?: number
}) {
  if (error) {
    return (
      <div className="rounded-2xl bg-destructive-soft px-4 py-3 text-sm font-medium text-destructive">
        {getErrorMessage(error)}
      </div>
    )
  }
  if (isLoading) {
    return (
      <div className="space-y-3">
        {Array.from({ length: rows }, (_, i) => (
          <Skeleton key={i} className="h-12 w-full" />
        ))}
      </div>
    )
  }
  return null
}

/** Design: a tonal block with an icon tile, a bold title and a short line; optional action. */
export function EmptyState({
  title,
  description,
  icon,
  action,
  onClick,
}: {
  title: string
  description?: string
  icon?: React.ReactNode
  action?: React.ReactNode
  /** The whole block is clickable (drop zone: "click to select files"). */
  onClick?: () => void
}) {
  return (
    <div
      onClick={onClick}
      className={
        "flex min-h-[220px] flex-1 flex-col items-center justify-center rounded-3xl bg-surface p-10 text-center" +
        (onClick ? " cursor-pointer transition-colors hover:bg-surface-hover" : "")
      }
    >
      {icon && (
        <div className="mb-4 flex size-14 items-center justify-center rounded-2xl bg-primary-soft text-primary-text [&_svg]:size-7">
          {icon}
        </div>
      )}
      <h3 className="text-base font-bold text-foreground">{title}</h3>
      {description && <p className="mt-1 max-w-sm text-xs text-muted-foreground">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}
