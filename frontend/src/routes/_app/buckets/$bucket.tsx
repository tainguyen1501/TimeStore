import { useCallback, useEffect, useRef, useState } from "react"
import { Link, createFileRoute, useNavigate } from "@tanstack/react-router"
import {
  ArrowLeft,
  ChevronRight,
  Clock,
  FolderPlus,
  FolderUp,
  Globe,
  History,
  LayoutGrid,
  List,
  Lock,
  MoreHorizontal,
  Plus,
  RefreshCw,
  Search,
  Settings,
  Table2,
  Trash2,
  UploadCloud,
  X,
} from "lucide-react"
import { toast } from "sonner"
import { z } from "zod"

import { CreateBucketDialog } from "@/components/buckets/create-bucket-dialog"
import { LifecycleSheet } from "@/components/objects/lifecycle-sheet"
import { NewFolderDialog } from "@/components/objects/new-folder-dialog"
import { ObjectBrowser } from "@/components/objects/object-browser"
import { ObjectDialog } from "@/components/objects/object-dialog"
import { PolicySheet } from "@/components/objects/policy-sheet"
import { PublicBadge } from "@/components/objects/public-access"
import { VersionTable } from "@/components/objects/version-table"
import { ConfirmDialog } from "@/components/shared/confirm-dialog"
import { DataListCard, ListPage, Toolbar } from "@/components/shared/data-list"
import { PageHeader } from "@/components/shared/page-header"
import { EmptyState, QueryState } from "@/components/shared/query-state"
import { Segmented } from "@/components/shared/segmented"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { InputGroup, InputGroupAddon, InputGroupInput } from "@/components/ui/input-group"
import { useCanWrite } from "@/hooks/api/use-auth"
import { useSetBucketVersioning } from "@/hooks/api/use-buckets"
import {
  useDeleteObjects,
  useGetObjectList,
  useGetObjectVersionList,
  useInvalidateObjects,
  useRestoreVersion,
} from "@/hooks/api/use-objects"
import { useGetPublicPrefixes, useSetPublic } from "@/hooks/api/use-policy"
import { useBucketStats, versioningLabel } from "@/hooks/use-bucket-stats"
import { useDebouncedValue } from "@/hooks/use-debounced-value"
import { formatBytes } from "@/lib/format"
import { publicState } from "@/lib/public-access"
import { cn } from "@/lib/utils"
import { VERSIONING } from "@/services/api/bucket-service"
import { DELIMITER, type ObjectVersionItemResponse } from "@/services/api/object-service"
import { useUiStore, type ViewMode } from "@/store/ui"
import { useUploadStore } from "@/store/uploads"

export const Route = createFileRoute("/_app/buckets/$bucket")({
  validateSearch: z.object({ prefix: z.string().optional() }),
  component: BucketBrowserPage,
})

/** What AWS asks for before a folder delete: it removes everything under the prefix. */
const FOLDER_DELETE_CONFIRMATION = "permanently delete"

const nameIn = (prefix: string, fullName: string) => fullName.slice(prefix.length)
/** Typing pause before the filter asks the server. */
const FILTER_DELAY_MS = 250

/** Design: bucket explorer — header with facts and search, a toolbar, the objects in list / grid / table view. */
function BucketBrowserPage() {
  const { bucket } = Route.useParams()
  const { prefix = "" } = Route.useSearch()
  const navigate = useNavigate()
  const canWrite = useCanWrite()
  // The filter narrows the listing to names starting with the typed text in this folder (a server-side prefix, so
  // it works however many objects the folder holds). It belongs to the folder: opening another one clears it.
  const [filterState, setFilterState] = useState({ prefix, text: "" })
  const filter = filterState.prefix === prefix ? filterState.text : ""
  const setFilter = (text: string) => setFilterState({ prefix, text })
  const query = useDebouncedValue(filter, FILTER_DELAY_MS)
  const listPrefix = prefix + (filterState.prefix === prefix ? query : "")
  const list = useGetObjectList(bucket, listPrefix)
  const fetchMore = useCallback(() => void list.fetchNextPage(), [list])
  const invalidate = useInvalidateObjects(bucket)
  const deleteObjects = useDeleteObjects(bucket)
  const enqueue = useUploadStore((state) => state.enqueue)
  const view = useUiStore((state) => state.views.objects)
  const setView = useUiStore((state) => state.setView)
  const setUploadTarget = useUiStore((state) => state.setUploadTarget)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [details, setDetails] = useState<{ key: string; versionId?: string } | null>(null)
  const [showVersions, setShowVersions] = useState(false)
  const [versionToDelete, setVersionToDelete] = useState<ObjectVersionItemResponse | null>(null)
  const [objectToDelete, setObjectToDelete] = useState<string | null>(null)
  const [confirmSuspend, setConfirmSuspend] = useState(false)
  const [lifecycleOpen, setLifecycleOpen] = useState(false)
  const [policyOpen, setPolicyOpen] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const publicPrefixes = useGetPublicPrefixes(bucket).data
  const setPublic = useSetPublic(bucket)
  /** A "Make public" waiting for confirmation ("" is the bucket); the path stays while the dialog closes. */
  const [confirmPublic, setConfirmPublic] = useState({ open: false, path: "" })
  const askPublic = (path: string) => setConfirmPublic({ open: true, path })
  const publicTarget = confirmPublic.path === "" ? bucket : confirmPublic.path
  const bucketPublic = publicState(publicPrefixes, "")
  const changePublic = (path: string, isPublic: boolean) =>
    setPublic.mutate(
      { prefix: path, isPublic },
      {
        onSuccess: () => {
          setConfirmPublic((current) => ({ ...current, open: false }))
          const what = path === "" ? bucket : path
          toast.success(isPublic ? `${what} is public.` : `${what} is private.`)
        },
      }
    )
  const stats = useBucketStats().data?.find((b) => b.name === bucket)
  const versioning = stats?.versioning ?? VERSIONING.unversioned
  const setVersioning = useSetBucketVersioning()
  const restore = useRestoreVersion(bucket)
  const versionList = useGetObjectVersionList(bucket, listPrefix, showVersions)
  const versionPages = versionList.data?.pages ?? []
  const versions = versionPages.flatMap((page) => page.versions)
  const [newFolderOpen, setNewFolderOpen] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)
  const filesInput = useRef<HTMLInputElement>(null)
  const folderInput = useRef<HTMLInputElement>(null)

  // The sidebar's "Upload Files" sends files to the folder open here.
  useEffect(() => {
    if (!canWrite) return
    setUploadTarget({ bucket, prefix, onUploaded: invalidate })
    return () => setUploadTarget(null)
  }, [bucket, prefix, canWrite, invalidate, setUploadTarget])

  // While a new filter loads, the previous listing of the same folder stays on screen; a listing of another folder
  // (its names would be read against the wrong folder) never does.
  const shownPrefix = list.data?.pages[0]?.prefix
  const sameFolder =
    !list.isPlaceholderData ||
    (shownPrefix !== undefined &&
      shownPrefix.startsWith(prefix) &&
      !shownPrefix.slice(prefix.length).includes(DELIMITER))
  const pages = sameFolder ? (list.data?.pages ?? []) : []
  const folders = showVersions
    ? versionPages.flatMap((page) => page.folders)
    : pages.flatMap((page) => page.folders)
  const objects = pages.flatMap((page) => page.objects)
  const entries = [...folders.map((f) => f.prefix), ...objects.map((o) => o.key)]
  const selectedFolders = [...selected].filter((name) => name.endsWith(DELIMITER))
  const selectedObjects = [...selected].filter((name) => !name.endsWith(DELIMITER))

  const open = (next: string) => {
    setSelected(new Set())
    void navigate({
      to: "/buckets/$bucket",
      params: { bucket },
      search: next ? { prefix: next } : {},
    })
  }
  const toggle = (name: string) =>
    setSelected((current) => {
      const next = new Set(current)
      if (next.has(name)) next.delete(name)
      else next.add(name)
      return next
    })
  const upload = (files: FileList | File[] | null) => {
    if (!files || files.length === 0) return
    enqueue(bucket, prefix, [...files], invalidate)
  }
  const remove = () =>
    deleteObjects.mutate(
      { keys: selectedObjects, prefixes: selectedFolders },
      {
        onSuccess: ({ deleted }) => {
          toast.success(`Deleted ${deleted} object${deleted === 1 ? "" : "s"}.`)
          setSelected(new Set())
          setConfirmDelete(false)
        },
      }
    )

  const segments = prefix.split(DELIMITER).filter(Boolean)

  return (
    <ListPage
      onDragEnter={(event) => {
        if (!canWrite || !event.dataTransfer.types.includes("Files")) return
        dragDepth.current++
        setDragging(true)
      }}
      onDragOver={(event) => {
        if (canWrite && event.dataTransfer.types.includes("Files")) event.preventDefault()
      }}
      onDragLeave={() => {
        dragDepth.current = Math.max(0, dragDepth.current - 1)
        if (dragDepth.current === 0) setDragging(false)
      }}
      onDrop={(event) => {
        if (!canWrite) return
        event.preventDefault()
        dragDepth.current = 0
        setDragging(false)
        upload([...event.dataTransfer.files].filter((file) => file.size > 0 || file.type))
      }}
    >
      <PageHeader
        title={bucket}
        meta={<PublicBadge state={bucketPublic} bucket={bucket} />}
        description={
          stats
            ? `${versioningLabel(versioning)} · ${formatBytes(stats.totalBytes)} · ${stats.objects.toLocaleString()} objects`
            : versioningLabel(versioning)
        }
        actions={
          <>
            <InputGroup className="flex-1 sm:w-[270px]">
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
              <InputGroupInput
                aria-label="Filter by name"
                placeholder={prefix ? `Names in ${prefix} starting with…` : "Names starting with…"}
                autoComplete="off"
                spellCheck={false}
                value={filter}
                onChange={(event) => {
                  setSelected(new Set())
                  setFilter(event.target.value)
                }}
              />
              {filter && (
                <InputGroupAddon align="inline-end">
                  <button
                    type="button"
                    aria-label="Clear filter"
                    onClick={() => setFilter("")}
                    className="rounded-md px-1 text-[11px] font-semibold text-muted-foreground hover:text-foreground"
                  >
                    <X className="size-3.5" />
                  </button>
                </InputGroupAddon>
              )}
            </InputGroup>
            {canWrite && (
              <Button size="lg" onClick={() => setCreateOpen(true)}>
                <Plus />
                <span className="hidden sm:inline">New Bucket</span>
              </Button>
            )}
          </>
        }
      />

      <Toolbar>
        <nav
          aria-label="Folder"
          className="flex min-w-0 flex-wrap items-center gap-1.5 text-xs font-semibold"
        >
          <Link
            to="/buckets"
            className="mr-1 inline-flex items-center gap-1 rounded-lg bg-raised px-2.5 py-1 text-subtle-foreground transition-colors hover:text-primary-text"
          >
            <ArrowLeft className="size-3.5" />
            <span>Buckets</span>
          </Link>
          <ChevronRight className="size-3.5 text-muted-foreground/70" />
          <button
            type="button"
            onClick={() => open("")}
            className={
              segments.length === 0
                ? "text-primary-text"
                : "text-subtle-foreground hover:text-foreground"
            }
          >
            {bucket}
          </button>
          {segments.map((segment, i) => (
            <span key={i} className="inline-flex items-center gap-1.5">
              <ChevronRight className="size-3.5 text-muted-foreground/70" />
              <button
                type="button"
                onClick={() => open(segments.slice(0, i + 1).join(DELIMITER) + DELIMITER)}
                className={
                  i === segments.length - 1
                    ? "text-primary-text"
                    : "text-subtle-foreground hover:text-foreground"
                }
              >
                {segment}
              </button>
            </span>
          ))}
          {showVersions && (
            <span className="ml-2 rounded-lg bg-primary-soft px-2 py-0.5 text-[11px] text-primary-text">
              Versions
            </span>
          )}
        </nav>

        <div className="flex flex-wrap items-center gap-2">
          {canWrite && selected.size > 0 && !showVersions && (
            <Button variant="destructive" size="sm" onClick={() => setConfirmDelete(true)}>
              <Trash2 />
              Delete {selected.size}
            </Button>
          )}
          <Button variant="outline" size="sm" onClick={() => setPolicyOpen(true)}>
            <Settings />
            Policy
          </Button>
          {canWrite && (
            <Button variant="outline" size="sm" onClick={() => setNewFolderOpen(true)}>
              <FolderPlus />
              New Folder
            </Button>
          )}
          {canWrite && (
            <Button size="sm" onClick={() => filesInput.current?.click()}>
              <UploadCloud />
              Upload
            </Button>
          )}
          <DropdownMenu>
            <DropdownMenuTrigger
              render={<Button variant="outline" size="icon-sm" aria-label="More bucket actions" />}
            >
              <MoreHorizontal />
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              {canWrite && (
                <DropdownMenuItem onClick={() => folderInput.current?.click()}>
                  <FolderUp />
                  Upload folder
                </DropdownMenuItem>
              )}
              <DropdownMenuItem onClick={() => setLifecycleOpen(true)}>
                <Clock />
                Lifecycle rules
              </DropdownMenuItem>
              {canWrite &&
                (bucketPublic?.kind === "direct" ? (
                  <DropdownMenuItem
                    onClick={() => changePublic("", false)}
                    disabled={setPublic.isPending}
                  >
                    <Lock />
                    Make bucket private
                  </DropdownMenuItem>
                ) : (
                  <DropdownMenuItem onClick={() => askPublic("")} disabled={setPublic.isPending}>
                    <Globe />
                    Make bucket public
                  </DropdownMenuItem>
                ))}
              {canWrite && (
                <DropdownMenuItem
                  disabled={setVersioning.isPending}
                  onClick={() =>
                    versioning === VERSIONING.enabled
                      ? setConfirmSuspend(true)
                      : setVersioning.mutate({ name: bucket, status: VERSIONING.enabled })
                  }
                >
                  <History />
                  {versioning === VERSIONING.enabled ? "Suspend versioning" : "Enable versioning"}
                </DropdownMenuItem>
              )}
              {versioning !== VERSIONING.unversioned && (
                <DropdownMenuCheckboxItem
                  checked={showVersions}
                  onCheckedChange={(checked) => {
                    setSelected(new Set())
                    setShowVersions(checked)
                  }}
                >
                  Show versions
                </DropdownMenuCheckboxItem>
              )}
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => void list.refetch()}>
                <RefreshCw className={cn(list.isFetching && "animate-spin")} />
                Refresh
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          {!showVersions && (
            <Segmented<ViewMode>
              label="Object layout"
              solid
              value={view}
              onChange={(mode) => setView("objects", mode)}
              options={[
                { value: "grid", label: "Grid view", icon: <LayoutGrid />, iconOnly: true },
                { value: "list", label: "List view", icon: <List />, iconOnly: true },
                { value: "table", label: "Table view", icon: <Table2 />, iconOnly: true },
              ]}
            />
          )}
          <input
            ref={filesInput}
            type="file"
            multiple
            hidden
            aria-label="Files to upload"
            onChange={(event) => {
              upload(event.target.files)
              event.target.value = ""
            }}
          />
          <input
            ref={folderInput}
            type="file"
            hidden
            aria-label="Folder to upload"
            // Not in React's typings: the folder picker of every current browser.
            {...{ webkitdirectory: "" }}
            onChange={(event) => {
              upload(event.target.files)
              event.target.value = ""
            }}
          />
        </div>
      </Toolbar>

      <QueryState isLoading={list.isLoading || !sameFolder} error={list.error} />

      {showVersions && (
        <>
          <QueryState isLoading={versionList.isLoading} error={versionList.error} />
          {(folders.length > 0 || versions.length > 0) && (
            <DataListCard
              footer={
                versionList.hasNextPage && (
                  <div className="flex justify-center">
                    <Button
                      variant="ghost"
                      disabled={versionList.isFetchingNextPage}
                      onClick={() => void versionList.fetchNextPage()}
                    >
                      {versionList.isFetchingNextPage ? "Loading…" : "Load more"}
                    </Button>
                  </div>
                )
              }
            >
              {folders.length > 0 && (
                <div className="flex flex-wrap gap-2 px-1 pb-3">
                  {folders.map((folder) => (
                    <Button
                      key={folder.prefix}
                      variant="outline"
                      size="sm"
                      onClick={() => open(folder.prefix)}
                    >
                      {nameIn(prefix, folder.prefix)}
                    </Button>
                  ))}
                </div>
              )}
              <VersionTable
                bucket={bucket}
                prefix={prefix}
                versions={versions}
                canWrite={canWrite}
                onDetails={(v) => setDetails({ key: v.key, versionId: v.versionId })}
                onRestore={(v) =>
                  restore.mutate(
                    { key: v.key, versionId: v.versionId },
                    { onSuccess: () => toast.success(`Restored ${nameIn(prefix, v.key)}.`) }
                  )
                }
                onDelete={setVersionToDelete}
              />
            </DataListCard>
          )}
        </>
      )}

      {!showVersions && list.data && entries.length === 0 && listPrefix !== prefix && (
        <EmptyState
          icon={<Search />}
          title={`No names start with “${query}”`}
          description={
            prefix
              ? `Nothing in ${prefix} matches. Names are case-sensitive.`
              : "Names are case-sensitive."
          }
        />
      )}

      {!showVersions && list.data && entries.length === 0 && listPrefix === prefix && (
        <EmptyState
          icon={<UploadCloud />}
          title={
            canWrite
              ? "Drag and drop files here"
              : prefix
                ? "This folder is empty"
                : "This bucket is empty"
          }
          description={
            canWrite
              ? "Drop one or multiple files to batch upload with live progress tracking, or click to select files."
              : "An admin can upload files."
          }
          onClick={canWrite ? () => filesInput.current?.click() : undefined}
        />
      )}

      {!showVersions && entries.length > 0 && (
        <ObjectBrowser
          key={`${listPrefix}\u0000${view}`}
          mode={view}
          bucket={bucket}
          prefix={prefix}
          listPrefix={listPrefix}
          folders={folders}
          objects={objects}
          canWrite={canWrite}
          selected={selected}
          onToggle={toggle}
          onSelectAll={(all) => setSelected(all ? new Set(entries) : new Set())}
          publicPrefixes={publicPrefixes}
          publicPending={setPublic.isPending}
          onOpenFolder={open}
          onDetails={(key) => setDetails({ key })}
          onDelete={setObjectToDelete}
          onMakePublic={askPublic}
          onMakePrivate={(path) => changePublic(path, false)}
          hasNextPage={list.hasNextPage && !list.isPlaceholderData}
          isFetchingNextPage={list.isFetchingNextPage}
          fetchNextPage={fetchMore}
        />
      )}

      {dragging && (
        <div className="pointer-events-none fixed inset-0 z-50 flex items-center justify-center bg-primary/12 p-6 backdrop-blur-[2px] dark:bg-primary/20">
          <div className="max-w-md rounded-3xl bg-popover px-10 py-8 text-center shadow-[0_24px_70px_rgba(108,92,231,0.28)]">
            <div className="mx-auto mb-4 flex size-14 items-center justify-center rounded-2xl bg-primary-soft text-primary-text">
              <UploadCloud className="size-7" />
            </div>
            <h3 className="text-lg font-bold text-foreground">Drop files for batch upload</h3>
            <p className="mt-1 text-xs text-muted-foreground">
              Uploading directly into{" "}
              <span className="font-semibold text-primary-text">
                {bucket}/{prefix}
              </span>
            </p>
          </div>
        </div>
      )}

      <LifecycleSheet
        bucket={bucket}
        open={lifecycleOpen}
        canWrite={canWrite}
        onOpenChange={setLifecycleOpen}
      />
      <PolicySheet
        bucket={bucket}
        open={policyOpen}
        canWrite={canWrite}
        onOpenChange={setPolicyOpen}
      />
      <CreateBucketDialog open={createOpen} onOpenChange={setCreateOpen} />
      <ObjectDialog
        bucket={bucket}
        objectKey={details?.key ?? null}
        versionId={details?.versionId}
        canWrite={canWrite}
        onClose={() => setDetails(null)}
        onDelete={(key) => setObjectToDelete(key)}
        onEditPolicy={() => {
          setDetails(null)
          setPolicyOpen(true)
        }}
      />
      <ConfirmDialog
        open={objectToDelete !== null}
        onOpenChange={(isOpen) => !isOpen && setObjectToDelete(null)}
        title={`Delete ${objectToDelete ? nameIn(prefix, objectToDelete) : ""}`}
        subtitle={
          versioning === VERSIONING.enabled
            ? "A delete marker hides it; older versions stay."
            : "This action cannot be undone."
        }
        description="The object is deleted from the bucket."
        confirmLabel="Delete"
        pendingLabel="Deleting…"
        destructive
        isPending={deleteObjects.isPending}
        onConfirm={() =>
          objectToDelete &&
          deleteObjects.mutate(
            { keys: [objectToDelete], prefixes: null },
            {
              onSuccess: () => {
                toast.success(`Deleted ${nameIn(prefix, objectToDelete)}.`)
                if (details?.key === objectToDelete) setDetails(null)
                setObjectToDelete(null)
              },
            }
          )
        }
      />
      <ConfirmDialog
        open={versionToDelete !== null}
        onOpenChange={(isOpen) => !isOpen && setVersionToDelete(null)}
        title={
          versionToDelete?.isDeleteMarker ? "Remove delete marker" : "Delete version permanently"
        }
        subtitle="This action cannot be undone."
        description={
          versionToDelete?.isDeleteMarker
            ? "The object becomes visible again if an older version exists."
            : "This version and its data are removed; other versions stay."
        }
        confirmLabel="Delete"
        pendingLabel="Deleting…"
        destructive
        confirmText={versionToDelete?.isDeleteMarker ? undefined : FOLDER_DELETE_CONFIRMATION}
        isPending={deleteObjects.isPending}
        onConfirm={() =>
          versionToDelete &&
          deleteObjects.mutate(
            {
              keys: null,
              prefixes: null,
              versions: [{ key: versionToDelete.key, versionId: versionToDelete.versionId }],
            },
            { onSuccess: () => setVersionToDelete(null) }
          )
        }
      />
      <ConfirmDialog
        open={confirmPublic.open}
        onOpenChange={(next) =>
          !next && setConfirmPublic((current) => ({ ...current, open: false }))
        }
        title={`Make ${publicTarget} public`}
        description={`Anyone who can reach the S3 port can read every object ${confirmPublic.path === "" ? "in" : "under"} ${publicTarget}, without signing in, if they know its key. Listing and writing stay private. This adds a statement to the bucket policy; Make private removes it again.`}
        confirmLabel="Make public"
        pendingLabel="Saving…"
        destructive
        isPending={setPublic.isPending}
        onConfirm={() => changePublic(confirmPublic.path, true)}
      />
      <ConfirmDialog
        open={confirmSuspend}
        onOpenChange={setConfirmSuspend}
        title="Suspend versioning"
        description="New writes replace the null version instead of adding versions; the versions kept so far stay. You can enable versioning again at any time."
        confirmLabel="Suspend"
        pendingLabel="Suspending…"
        isPending={setVersioning.isPending}
        onConfirm={() =>
          setVersioning.mutate(
            { name: bucket, status: VERSIONING.suspended },
            { onSuccess: () => setConfirmSuspend(false) }
          )
        }
      />
      <NewFolderDialog
        bucket={bucket}
        prefix={prefix}
        open={newFolderOpen}
        onOpenChange={setNewFolderOpen}
      />
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete ${selected.size} item${selected.size === 1 ? "" : "s"}`}
        subtitle="This action cannot be undone."
        description={
          selectedFolders.length > 0
            ? `Folders are deleted with every object under them (${selectedFolders.map((f) => nameIn(prefix, f)).join(", ")}).`
            : "The selected objects are deleted from the bucket."
        }
        confirmLabel="Delete"
        pendingLabel="Deleting…"
        destructive
        confirmText={selectedFolders.length > 0 ? FOLDER_DELETE_CONFIRMATION : undefined}
        isPending={deleteObjects.isPending}
        onConfirm={remove}
      />
    </ListPage>
  )
}
