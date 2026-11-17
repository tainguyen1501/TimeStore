import { Menu } from "lucide-react"

import { cn } from "@/lib/utils"
import { useUiStore } from "@/store/ui"

type PageHeaderProps = {
  title: React.ReactNode
  description?: React.ReactNode
  meta?: React.ReactNode
  /** Right side: search, primary action. */
  actions?: React.ReactNode
  className?: string
}

/** Design: large extra-bold title with a one-line subtitle; actions on the right; the menu button on small screens. */
export function PageHeader({ title, description, meta, actions, className }: PageHeaderProps) {
  const setMenuOpen = useUiStore((state) => state.setMenuOpen)
  return (
    <div
      className={cn("flex flex-col justify-between gap-4 sm:flex-row sm:items-center", className)}
    >
      <div className="flex min-w-0 items-center gap-3">
        <button
          type="button"
          aria-label="Open navigation"
          onClick={() => setMenuOpen(true)}
          className="shrink-0 rounded-xl bg-field p-2 text-subtle-foreground lg:hidden"
        >
          <Menu className="size-5" />
        </button>
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-[24px] font-extrabold tracking-tight text-foreground sm:text-[26px]">
              {title}
            </h1>
            {meta}
          </div>
          {description && (
            <p className="mt-0.5 truncate text-xs text-muted-foreground">{description}</p>
          )}
        </div>
      </div>
      {actions && (
        <div className="flex w-full shrink-0 items-center gap-2.5 sm:w-auto">{actions}</div>
      )}
    </div>
  )
}
