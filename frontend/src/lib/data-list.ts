/** Rows-per-page choices of every paged list (also the only values accepted in the URL). */
export const PAGE_SIZES = [10, 25, 50, 100] as const
export const DEFAULT_PAGE_SIZE = 25

export const isPageSize = (value: number) => (PAGE_SIZES as readonly number[]).includes(value)

/** Classes for a sticky table header inside a `DataListCard`. */
export const STICKY_HEADER =
  "sticky top-0 z-10 bg-card backdrop-blur-md shadow-[inset_0_-1px_0_var(--border)]"

/** The Api's upper bound for `page` (`RunListRequest.MaxPage`); a larger one in the URL is ignored. */
export const MAX_PAGE = 100_000
