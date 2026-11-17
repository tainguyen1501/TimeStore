import { useEffect, useLayoutEffect, useRef, useState } from "react"
import { useVirtualizer } from "@tanstack/react-virtual"
import { Download, Trash2 } from "lucide-react"

import { FolderPublicButton, PublicBadge } from "@/components/objects/public-access"
import { DataListCard } from "@/components/shared/data-list"
import { FileIcon, FolderIcon } from "@/components/shared/file-icon"
import { Checkbox } from "@/components/ui/checkbox"
import { useIsMobile } from "@/hooks/use-mobile"
import { extensionOf, fileKind } from "@/lib/file-kind"
import { formatBytes, formatShortDate } from "@/lib/format"
import { publicState } from "@/lib/public-access"
import { cn } from "@/lib/utils"
import type { ViewMode } from "@/store/ui"
import {
  objectService,
  type FolderResponse,
  type ObjectItemResponse,
} from "@/services/api/object-service"

/** Rows rendered above and below the visible ones. */
const OVERSCAN = 8
/** The next page loads when the last rendered entry is this close to the end of what is loaded. */
const LOAD_AHEAD = 40
const ROW_HEIGHT: Record<ViewMode, number> = { list: 66, table: 52, grid: 216 }
/** Images larger than this get no thumbnail in the grid (it would download the whole file). */
const THUMBNAIL_MAX_BYTES = 8 * 1024 * 1024

/** Scroll position per bucket, folder and filter, so going back returns to where it was (kept for this tab). */
const scrollMemory = new Map<string, number>()

type Entry =
  | { kind: "folder"; folder: FolderResponse; name: string }
  | { kind: "object"; object: ObjectItemResponse; name: string }

type ObjectBrowserProps = {
  mode: ViewMode
  bucket: string
  /** The folder being shown; names are relative to it. */
  prefix: string
  /** What is listed: the folder plus the filter text. */
  listPrefix: string
  folders: FolderResponse[]
  objects: ObjectItemResponse[]
  canWrite: boolean
  selected: Set<string>
  onToggle: (name: string) => void
  onSelectAll: (all: boolean) => void
  publicPrefixes: readonly string[] | undefined
  publicPending: boolean
  onOpenFolder: (prefix: string) => void
  onDetails: (key: string) => void
  onDelete: (key: string) => void
  onMakePublic: (prefix: string) => void
  onMakePrivate: (prefix: string) => void
  hasNextPage: boolean
  isFetchingNextPage: boolean
  fetchNextPage: () => void
}

/**
 * A folder's entries in the design's list, grid or table view, virtualized: only what is in view is in the page,
 * however many entries are loaded, and the next page loads while scrolling. From `md` up the list scrolls inside the
 * page; on small screens the page's content area scrolls (with a scroll margin). Mount one per listing (`key`): the
 * scroll position is remembered per folder.
 */
export function ObjectBrowser(props: ObjectBrowserProps) {
  const {
    mode,
    bucket,
    listPrefix,
    folders,
    objects,
    selected,
    hasNextPage,
    isFetchingNextPage,
    fetchNextPage,
  } = props
  const entries: Entry[] = [
    ...folders.map((folder) => ({ kind: "folder" as const, folder, name: folder.prefix })),
    ...objects.map((object) => ({ kind: "object" as const, object, name: object.key })),
  ]

  const scroller = useRef<HTMLDivElement>(null)
  const isMobile = useIsMobile()
  const lanes = useGridLanes(scroller, mode)
  const rowCount = mode === "grid" ? Math.ceil(entries.length / lanes) : entries.length
  const rowKey = (index: number) =>
    mode === "grid" ? `${index}:${entries[index * lanes]?.name}` : entries[index]?.name

  const memoryKey = `${bucket}\u0000${listPrefix}\u0000${mode}`
  // Read once, before the first scroll event (at the top) overwrites it.
  const [savedOffset] = useState(() => scrollMemory.get(memoryKey) ?? 0)
  const scrollElement = () =>
    isMobile
      ? (scroller.current?.closest<HTMLElement>("[data-slot=app-scroll]") ?? null)
      : scroller.current
  // On small screens the list starts below the page header inside the scrolling area.
  const [scrollMargin, setScrollMargin] = useState(0)
  useLayoutEffect(() => {
    const area = isMobile ? scrollElement() : null
    const list = scroller.current
    setScrollMargin(
      area && list
        ? list.getBoundingClientRect().top - area.getBoundingClientRect().top + area.scrollTop
        : 0
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps -- measured once per layout mode
  }, [isMobile])

  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual returns fresh functions by design
  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: scrollElement,
    estimateSize: () => ROW_HEIGHT[mode],
    overscan: OVERSCAN,
    getItemKey: rowKey,
    scrollMargin,
    initialOffset: savedOffset,
    onChange: (instance) => {
      if (instance.scrollOffset !== null) scrollMemory.set(memoryKey, instance.scrollOffset)
    },
  })
  // Back in a folder: scroll where it was (initialOffset only sets the virtualizer's idea of the position).
  useLayoutEffect(() => {
    const element = scrollElement()
    if (savedOffset > 0 && element) element.scrollTop = savedOffset
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, when the listing mounts
  }, [])

  const items = virtualizer.getVirtualItems()
  const lastEntry =
    mode === "grid" ? ((items.at(-1)?.index ?? -1) + 1) * lanes - 1 : (items.at(-1)?.index ?? -1)
  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage && lastEntry >= entries.length - LOAD_AHEAD)
      fetchNextPage()
  }, [lastEntry, entries.length, hasNextPage, isFetchingNextPage, fetchNextPage])

  // Spacers stand in for the rows that are not rendered; before the first measurement the bottom one holds the full
  // height, so the list can be scrolled (and a remembered position restored) right away.
  const top = items.length > 0 ? items[0].start - scrollMargin : 0
  const bottom =
    items.length > 0
      ? virtualizer.getTotalSize() - (items.at(-1)!.end - scrollMargin)
      : virtualizer.getTotalSize()

  const footer = (
    <p className="text-xs text-muted-foreground tabular-nums">
      {folders.length > 0 && `${folders.length.toLocaleString()} folders · `}
      {objects.length.toLocaleString()} objects
      {hasNextPage ? (isFetchingNextPage ? " · loading more…" : " loaded · scroll for more") : ""}
      {selected.size > 0 && ` · ${selected.size.toLocaleString()} selected`}
    </p>
  )

  const rendered = items.map((item) => {
    const measure = { key: item.key, "data-index": item.index, ref: virtualizer.measureElement }
    if (mode === "grid") {
      const row = entries.slice(item.index * lanes, item.index * lanes + lanes)
      return (
        <div
          {...measure}
          className="grid gap-4 pb-4"
          style={{ gridTemplateColumns: `repeat(${lanes}, minmax(0, 1fr))` }}
        >
          {row.map((entry) => (
            <GridCard key={entry.name} entry={entry} {...props} />
          ))}
        </div>
      )
    }
    const entry = entries[item.index]
    return mode === "list" ? (
      <ListRow {...measure} entry={entry} {...props} />
    ) : (
      <TableRow {...measure} entry={entry} {...props} />
    )
  })

  if (mode === "table") {
    const columns = (props.canWrite ? 6 : 5) - (isMobile ? 2 : 0)
    const allSelected = entries.length > 0 && entries.every((e) => selected.has(e.name))
    return (
      <DataListCard footer={footer}>
        <div ref={scroller} data-slot="object-scroll" className="h-full overflow-auto">
          {/* Fixed layout: column widths never depend on which rows happen to be rendered. */}
          <table className="w-full table-fixed border-collapse text-left">
            <thead className="sticky top-0 z-10 bg-surface">
              <tr className="text-[11px] font-semibold text-muted-foreground">
                {props.canWrite && (
                  <th className="w-10 px-3 py-2.5">
                    <Checkbox
                      aria-label="Select all loaded"
                      checked={allSelected}
                      indeterminate={selected.size > 0 && !allSelected}
                      onCheckedChange={(checked) => props.onSelectAll(checked)}
                    />
                  </th>
                )}
                <th className="px-3 py-2.5">Name</th>
                <th className="hidden w-20 px-3 py-2.5 md:table-cell">Ext</th>
                <th className="w-24 px-3 py-2.5 text-right">Size</th>
                <th className="hidden w-40 px-3 py-2.5 text-right md:table-cell">Modified</th>
                <th className="w-28 px-3 py-2.5 text-right">Actions</th>
              </tr>
            </thead>
            <tbody>
              {top > 0 && (
                <tr aria-hidden style={{ height: top }}>
                  <td colSpan={columns} />
                </tr>
              )}
              {rendered}
              {bottom > 0 && (
                <tr aria-hidden style={{ height: bottom }}>
                  <td colSpan={columns} />
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </DataListCard>
    )
  }

  return (
    <DataListCard footer={footer} className={mode === "grid" ? "bg-transparent p-0" : undefined}>
      <div ref={scroller} data-slot="object-scroll" className="h-full overflow-auto">
        {top > 0 && <div aria-hidden style={{ height: top }} />}
        {rendered}
        {bottom > 0 && <div aria-hidden style={{ height: bottom }} />}
      </div>
    </DataListCard>
  )
}

/** Grid columns from the list's width (design: 1 / 2 / 3 / 4). */
function useGridLanes(scroller: React.RefObject<HTMLDivElement | null>, mode: ViewMode) {
  const [lanes, setLanes] = useState(4)
  useLayoutEffect(() => {
    if (mode !== "grid" || !scroller.current) return
    const element = scroller.current
    const measure = () => {
      const width = element.clientWidth
      setLanes(width >= 900 ? 4 : width >= 660 ? 3 : width >= 420 ? 2 : 1)
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [scroller, mode])
  return lanes
}

type EntryProps = ObjectBrowserProps & { entry: Entry }

const relative = (prefix: string, name: string) => name.slice(prefix.length)

/** Selection box: shown on hover, always once anything is selected (admins only). */
function SelectBox({
  entry,
  prefix,
  selected,
  onToggle,
  className,
}: EntryProps & { className?: string }) {
  const checked = selected.has(entry.name)
  return (
    <Checkbox
      aria-label={`Select ${relative(prefix, entry.name)}`}
      checked={checked}
      onCheckedChange={() => onToggle(entry.name)}
      onClick={(event) => event.stopPropagation()}
      className={cn(
        "transition-opacity",
        !checked &&
          selected.size === 0 &&
          "opacity-0 group-hover:opacity-100 focus-visible:opacity-100",
        className
      )}
    />
  )
}

function ObjectActions({
  bucket,
  prefix,
  canWrite,
  onDelete,
  object,
}: EntryProps & { object: ObjectItemResponse }) {
  const name = relative(prefix, object.key)
  return (
    <div className="flex items-center gap-1" onClick={(event) => event.stopPropagation()}>
      <a
        href={objectService.downloadUrl(bucket, object.key)}
        download
        aria-label={`Download ${name}`}
        title="Download"
        className="rounded-lg p-1.5 text-muted-foreground transition-colors hover:text-primary-text"
      >
        <Download className="size-3.5" />
      </a>
      {canWrite && (
        <button
          type="button"
          onClick={() => onDelete(object.key)}
          aria-label={`Delete ${name}`}
          title="Delete"
          className="rounded-lg p-1.5 text-muted-foreground transition-colors hover:text-destructive"
        >
          <Trash2 className="size-3.5" />
        </button>
      )}
    </div>
  )
}

function FolderActions(props: EntryProps & { folder: FolderResponse }) {
  const {
    canWrite,
    bucket,
    prefix,
    publicPrefixes,
    publicPending,
    onMakePublic,
    onMakePrivate,
    folder,
  } = props
  if (!canWrite) return null
  return (
    <div onClick={(event) => event.stopPropagation()}>
      <FolderPublicButton
        name={relative(prefix, folder.prefix)}
        state={publicState(publicPrefixes, folder.prefix)}
        bucket={bucket}
        pending={publicPending}
        onMakePublic={() => onMakePublic(folder.prefix)}
        onMakePrivate={() => onMakePrivate(folder.prefix)}
      />
    </div>
  )
}

/** Design: list row — icon tile, name, "bucket/prefix · date", size, actions. */
function ListRow({
  ref,
  ...props
}: EntryProps & { ref: (node: HTMLElement | null) => void; "data-index": number }) {
  const { entry, bucket, prefix, canWrite, selected, onOpenFolder, onDetails, publicPrefixes } =
    props
  const open = () => (entry.kind === "folder" ? onOpenFolder(entry.name) : onDetails(entry.name))
  return (
    <div ref={ref} data-index={props["data-index"]} className="py-[3px]">
      <div
        role="button"
        tabIndex={0}
        onClick={open}
        onKeyDown={(event) => event.key === "Enter" && open()}
        className={cn(
          "group flex cursor-pointer items-center justify-between gap-4 rounded-2xl px-4 py-3 transition-colors hover:bg-raised focus-visible:ring-2 focus-visible:ring-ring/30 focus-visible:outline-none",
          selected.has(entry.name) && "bg-primary-soft/60 hover:bg-primary-soft"
        )}
      >
        <div className="flex min-w-0 flex-1 items-center gap-3.5">
          <div className="relative shrink-0">
            {entry.kind === "folder" ? <FolderIcon /> : <FileIcon ext={extensionOf(entry.name)} />}
            {canWrite && (
              <SelectBox {...props} className="absolute -top-1.5 -left-1.5 bg-raised shadow-xs" />
            )}
          </div>
          <div className="min-w-0">
            <div className="flex min-w-0 items-center gap-2">
              <span className="truncate text-[13.5px] font-semibold text-foreground">
                {relative(prefix, entry.name)}
              </span>
              {entry.kind === "folder" && (
                <PublicBadge state={publicState(publicPrefixes, entry.name)} bucket={bucket} />
              )}
            </div>
            <div className="truncate text-[11px] text-muted-foreground">
              {bucket}/{prefix}
              {entry.kind === "folder"
                ? " · Folder"
                : ` · ${formatShortDate(entry.object.lastModified)}`}
            </div>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-3">
          {entry.kind === "folder" ? (
            <>
              <FolderActions {...props} folder={entry.folder} />
              <span className="text-xs font-semibold text-primary-text">Open</span>
            </>
          ) : (
            <>
              <span
                className="text-xs font-semibold text-subtle-foreground tabular-nums"
                title={`${entry.object.size} bytes`}
              >
                {formatBytes(entry.object.size)}
              </span>
              <ObjectActions {...props} object={entry.object} />
            </>
          )}
        </div>
      </div>
    </div>
  )
}

/** Design: table row — icon and name, extension, size, modified, actions. */
function TableRow({
  ref,
  ...props
}: EntryProps & { ref: (node: HTMLElement | null) => void; "data-index": number }) {
  const { entry, bucket, prefix, canWrite, selected, onOpenFolder, onDetails, publicPrefixes } =
    props
  const open = () => (entry.kind === "folder" ? onOpenFolder(entry.name) : onDetails(entry.name))
  const ext = entry.kind === "object" ? extensionOf(entry.name) : ""
  return (
    <tr
      ref={ref}
      data-index={props["data-index"]}
      onClick={open}
      className={cn(
        "group cursor-pointer transition-colors hover:bg-raised [&>td:first-child]:rounded-l-2xl [&>td:last-child]:rounded-r-2xl",
        selected.has(entry.name) && "bg-primary-soft/60"
      )}
    >
      {canWrite && (
        <td className="px-3 py-2.5">
          <SelectBox {...props} />
        </td>
      )}
      <td className="px-3 py-2.5">
        <div className="flex min-w-0 items-center gap-3">
          {entry.kind === "folder" ? <FolderIcon size="sm" /> : <FileIcon ext={ext} size="sm" />}
          <span
            className="min-w-0 truncate text-xs font-semibold text-foreground"
            title={relative(prefix, entry.name)}
          >
            {relative(prefix, entry.name)}
          </span>
          {entry.kind === "folder" && (
            <PublicBadge state={publicState(publicPrefixes, entry.name)} bucket={bucket} />
          )}
        </div>
      </td>
      <td className="hidden px-3 py-2.5 font-mono text-[11px] font-semibold text-primary-text uppercase md:table-cell">
        {entry.kind === "folder" ? (
          <span className="text-muted-foreground">DIR</span>
        ) : ext ? (
          `.${ext}`
        ) : (
          "—"
        )}
      </td>
      <td className="px-3 py-2.5 text-right text-xs font-semibold whitespace-nowrap text-subtle-foreground tabular-nums">
        {entry.kind === "folder" ? "—" : formatBytes(entry.object.size)}
      </td>
      <td className="hidden px-3 py-2.5 text-right text-[11px] whitespace-nowrap text-muted-foreground tabular-nums md:table-cell">
        {entry.kind === "folder" ? "—" : formatShortDate(entry.object.lastModified)}
      </td>
      <td className="px-3 py-2.5 text-right">
        <div className="inline-flex items-center justify-end gap-1">
          {entry.kind === "folder" ? (
            <>
              <FolderActions {...props} folder={entry.folder} />
              <span className="text-xs font-semibold text-primary-text">Open</span>
            </>
          ) : (
            <ObjectActions {...props} object={entry.object} />
          )}
        </div>
      </td>
    </tr>
  )
}

/** Design: grid card — thumbnail (images) or a large icon, hover actions, name, folder and size. */
function GridCard(props: EntryProps) {
  const { entry, bucket, prefix, canWrite, onOpenFolder, onDetails, publicPrefixes, selected } =
    props
  if (entry.kind === "folder")
    return (
      <div
        role="button"
        tabIndex={0}
        onClick={() => onOpenFolder(entry.name)}
        onKeyDown={(event) => event.key === "Enter" && onOpenFolder(entry.name)}
        className="group relative flex items-center gap-3.5 rounded-2xl bg-surface p-4 text-left transition-all duration-150 hover:bg-surface-hover"
      >
        {canWrite && <SelectBox {...props} className="absolute top-2 left-2" />}
        <FolderIcon className="size-11 rounded-2xl" />
        <div className="min-w-0 flex-1">
          <div className="truncate text-sm font-bold text-foreground">
            {relative(prefix, entry.name)}
          </div>
          <div className="flex items-center gap-1.5 text-[11px] text-muted-foreground">
            Folder prefix
            <PublicBadge state={publicState(publicPrefixes, entry.name)} bucket={bucket} />
          </div>
        </div>
        <FolderActions {...props} folder={entry.folder} />
      </div>
    )

  const object = entry.object
  const ext = extensionOf(object.key)
  const thumbnail = fileKind(ext).glyph === "image" && object.size <= THUMBNAIL_MAX_BYTES
  return (
    <div
      role="button"
      tabIndex={0}
      onClick={() => onDetails(object.key)}
      onKeyDown={(event) => event.key === "Enter" && onDetails(object.key)}
      className={cn(
        "group flex cursor-pointer flex-col justify-between gap-3 rounded-2xl bg-surface p-4 transition-all duration-150 hover:bg-raised hover:shadow-[0_12px_30px_rgba(24,26,36,0.06)] dark:hover:shadow-none",
        selected.has(object.key) && "ring-2 ring-primary/40"
      )}
    >
      <div className="relative flex h-28 items-center justify-center overflow-hidden rounded-xl bg-raised dark:bg-background">
        {thumbnail ? (
          <>
            <img
              src={objectService.downloadUrl(bucket, object.key)}
              alt=""
              loading="lazy"
              className="size-full object-cover transition-transform duration-200 group-hover:scale-105"
            />
            <div className="absolute bottom-2 left-2">
              <FileIcon ext={ext} size="sm" />
            </div>
          </>
        ) : (
          <FileIcon ext={ext} size="lg" />
        )}
        {canWrite && <SelectBox {...props} className="absolute top-2 left-2 bg-raised" />}
        <div className="absolute top-2 right-2 rounded-lg bg-raised/90 opacity-0 shadow-xs transition-opacity group-hover:opacity-100">
          <ObjectActions {...props} object={object} />
        </div>
      </div>
      <div className="min-w-0">
        <div
          className="truncate text-[13.5px] font-bold text-foreground"
          title={relative(prefix, object.key)}
        >
          {relative(prefix, object.key)}
        </div>
        <div className="mt-1 flex items-center justify-between text-[11px] text-muted-foreground tabular-nums">
          <span className="truncate">
            {bucket}/{prefix}
          </span>
          <span className="ml-2 shrink-0 font-semibold">{formatBytes(object.size)}</span>
        </div>
      </div>
    </div>
  )
}
