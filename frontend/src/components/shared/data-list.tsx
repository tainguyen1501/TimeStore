import { ArrowDown, ArrowUp, ArrowUpDown } from "lucide-react"

import { IconButton } from "@/components/shared/icon-button"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { TableHead } from "@/components/ui/table"
import { PAGE_SIZES } from "@/lib/data-list"
import { cn } from "@/lib/utils"

/**
 * A page in the centre column (design: 20–32 px padding, 24 px between blocks). `fill` lets the last block take the
 * remaining height from `md` up (lists that scroll inside themselves); on small screens the page scrolls as usual.
 */
export function ListPage({
  children,
  className,
  fill = true,
  ...props
}: React.ComponentProps<"div"> & { fill?: boolean }) {
  return (
    <div
      className={cn(
        "flex flex-col gap-6 px-5 py-6 sm:px-8",
        fill && "md:h-full md:min-h-0",
        className
      )}
      {...props}
    >
      {children}
    </div>
  )
}

/**
 * A borderless tonal container for a list (design: rounded-3xl, `surface`, p-3). It is as tall as its rows and, on a
 * `fill` page, stops at the bottom of the page: then its content scrolls (give the scrolling element `h-full
 * overflow-auto`).
 */
export function DataListCard({
  children,
  footer,
  className,
}: {
  children: React.ReactNode
  footer?: React.ReactNode
  className?: string
}) {
  return (
    <div
      data-slot="data-list-card"
      className={cn(
        "flex flex-col rounded-3xl bg-surface p-3 md:min-h-0 md:flex-initial",
        className
      )}
    >
      <div className="md:min-h-0 md:flex-1">{children}</div>
      {footer && <div className="shrink-0 px-3 pt-3 pb-1">{footer}</div>}
    </div>
  )
}

/** The tonal bar above a list (design: count or breadcrumbs left, actions and view switcher right). */
export function Toolbar({
  children,
  className,
}: {
  children: React.ReactNode
  className?: string
}) {
  return (
    <div
      className={cn(
        "flex flex-wrap items-center justify-between gap-3 rounded-2xl bg-surface p-3.5",
        className
      )}
    >
      {children}
    </div>
  )
}

type TablePaginationProps = {
  /** 1-based. */
  page: number
  pageSize: number
  total: number
  onPageChange: (page: number) => void
  onPageSizeChange: (pageSize: number) => void
  /** Plural noun for the count ("servers"). */
  noun: string
}

export function TablePagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  noun,
}: TablePaginationProps) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  const from = total === 0 ? 0 : (page - 1) * pageSize + 1
  const to = Math.min(total, page * pageSize)
  const sizes = PAGE_SIZES.map((size) => ({ value: String(size), label: String(size) }))

  return (
    <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-muted-foreground">
      <span>
        {total === 0 ? (
          `No ${noun}`
        ) : (
          <>
            <span className="font-medium text-foreground">
              {from}–{to}
            </span>{" "}
            of <span className="font-medium text-foreground">{total}</span> {noun}
          </>
        )}
      </span>
      <div className="flex items-center gap-4">
        <div className="flex items-center gap-2">
          <span>Rows per page</span>
          <Select
            items={sizes}
            value={String(pageSize)}
            onValueChange={(value) => value && onPageSizeChange(Number(value))}
          >
            <SelectTrigger size="sm" className="w-18">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {sizes.map((size) => (
                <SelectItem key={size.value} value={size.value}>
                  {size.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <span>
          Page <span className="font-medium text-foreground">{Math.min(page, pages)}</span> of{" "}
          <span className="font-medium text-foreground">{pages}</span>
        </span>
        <div className="flex items-center gap-1">
          <IconButton
            icon="first"
            label="First page"
            variant="secondary"
            disabled={page <= 1}
            onClick={() => onPageChange(1)}
          />
          <IconButton
            icon="previous"
            label="Previous page"
            variant="secondary"
            disabled={page <= 1}
            onClick={() => onPageChange(page - 1)}
          />
          <IconButton
            icon="next"
            label="Next page"
            variant="secondary"
            disabled={page >= pages}
            onClick={() => onPageChange(page + 1)}
          />
          <IconButton
            icon="last"
            label="Last page"
            variant="secondary"
            disabled={page >= pages}
            onClick={() => onPageChange(pages)}
          />
        </div>
      </div>
    </div>
  )
}

export type SortState<F extends string> = { sortBy?: F; sortDesc?: boolean }

/**
 * A column header that sorts the list: click → ascending, again → descending, again → back to the list's default
 * order (no `sortBy`).
 */
export function SortableHead<F extends string>({
  field,
  sort,
  onSort,
  children,
  className,
}: {
  field: F
  sort: SortState<F>
  onSort: (next: SortState<F>) => void
  children: React.ReactNode
  className?: string
}) {
  const active = sort.sortBy === field
  const next: SortState<F> = !active
    ? { sortBy: field, sortDesc: undefined }
    : !sort.sortDesc
      ? { sortBy: field, sortDesc: true }
      : { sortBy: undefined, sortDesc: undefined }
  const Icon = !active ? ArrowUpDown : sort.sortDesc ? ArrowDown : ArrowUp

  return (
    <TableHead
      className={className}
      aria-sort={!active ? "none" : sort.sortDesc ? "descending" : "ascending"}
    >
      <button
        type="button"
        className={cn(
          "-mx-1 inline-flex items-center gap-1 rounded-md px-1 hover:text-foreground",
          active && "text-foreground"
        )}
        onClick={() => onSort(next)}
      >
        {children}
        <Icon className={cn("size-3", !active && "opacity-40")} />
      </button>
    </TableHead>
  )
}
