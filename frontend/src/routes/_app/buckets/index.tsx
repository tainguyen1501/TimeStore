import { useState } from "react"
import { createFileRoute } from "@tanstack/react-router"
import { HardDrive, LayoutGrid, List, Plus, Search, SlidersHorizontal, Table2 } from "lucide-react"

import { BucketCard, BucketRow, BucketTable } from "@/components/buckets/bucket-views"
import { useBucketStats, type BucketStats } from "@/hooks/use-bucket-stats"
import { CreateBucketDialog } from "@/components/buckets/create-bucket-dialog"
import { DeleteBucketDialog } from "@/components/buckets/delete-bucket-dialog"
import { ListPage, Toolbar } from "@/components/shared/data-list"
import { PageHeader } from "@/components/shared/page-header"
import { EmptyState, QueryState } from "@/components/shared/query-state"
import { Segmented } from "@/components/shared/segmented"
import { Button } from "@/components/ui/button"
import { InputGroup, InputGroupAddon, InputGroupInput } from "@/components/ui/input-group"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { useCanWrite } from "@/hooks/api/use-auth"
import { useUiStore, type ViewMode } from "@/store/ui"

export const Route = createFileRoute("/_app/buckets/")({
  component: BucketsPage,
})

type SortKey = "name" | "used" | "objects"

const SORTS: { value: SortKey; label: string }[] = [
  { value: "name", label: "Name" },
  { value: "used", label: "Used Storage" },
  { value: "objects", label: "Object Count" },
]

const sorters: Record<SortKey, (a: BucketStats, b: BucketStats) => number> = {
  name: (a, b) => a.name.localeCompare(b.name),
  used: (a, b) => b.totalBytes - a.totalBytes || a.name.localeCompare(b.name),
  objects: (a, b) => b.objects - a.objects || a.name.localeCompare(b.name),
}

/** Design: buckets directory with grid / list / table modes; a bucket opens its explorer. */
function BucketsPage() {
  const { data: buckets, isLoading, error } = useBucketStats()
  const canWrite = useCanWrite()
  const view = useUiStore((state) => state.views.buckets)
  const setView = useUiStore((state) => state.setView)
  const [sort, setSort] = useState<SortKey>("name")
  const [search, setSearch] = useState("")
  const [createOpen, setCreateOpen] = useState(false)
  const [toDelete, setToDelete] = useState<string | null>(null)
  const onDelete = canWrite ? setToDelete : undefined

  const shown = (buckets ?? [])
    .filter((b) => b.name.includes(search.trim().toLowerCase()))
    .sort(sorters[sort])

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Buckets"
        description="Manage buckets and their versioning. Select a bucket to inspect its objects."
        actions={
          <>
            <InputGroup className="flex-1 sm:w-[270px]">
              <InputGroupAddon>
                <Search />
              </InputGroupAddon>
              <InputGroupInput
                aria-label="Search buckets"
                placeholder="Search buckets…"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
              />
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
        <div className="text-xs font-semibold text-subtle-foreground tabular-nums">
          {shown.length} {shown.length === 1 ? "bucket" : "buckets"} · Select a bucket to inspect
          files
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Select
            items={SORTS}
            value={sort}
            onValueChange={(value) => value && setSort(value as SortKey)}
          >
            <SelectTrigger size="sm" aria-label="Sort buckets" className="bg-raised">
              <SlidersHorizontal className="text-muted-foreground" />
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {SORTS.map((item) => (
                <SelectItem key={item.value} value={item.value}>
                  {item.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Segmented<ViewMode>
            label="Bucket layout"
            solid
            value={view}
            onChange={(mode) => setView("buckets", mode)}
            options={[
              { value: "grid", label: "Grid view", icon: <LayoutGrid />, iconOnly: true },
              { value: "list", label: "List view", icon: <List />, iconOnly: true },
              { value: "table", label: "Table view", icon: <Table2 />, iconOnly: true },
            ]}
          />
        </div>
      </Toolbar>

      <QueryState isLoading={isLoading} error={error} />

      {buckets && shown.length === 0 && (
        <EmptyState
          icon={<HardDrive />}
          title={search ? "No bucket matches" : "No buckets created"}
          description={
            search
              ? `No bucket name contains “${search}”.`
              : canWrite
                ? "Create your first bucket to start organizing objects."
                : "An admin can create buckets."
          }
          action={
            canWrite &&
            !search && <Button onClick={() => setCreateOpen(true)}>+ Create Bucket</Button>
          }
        />
      )}

      {shown.length > 0 && view === "grid" && (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {shown.map((bucket) => (
            <BucketCard key={bucket.id} bucket={bucket} onDelete={onDelete} />
          ))}
        </div>
      )}
      {shown.length > 0 && view === "list" && (
        <div className="space-y-1.5 rounded-3xl bg-surface p-3">
          {shown.map((bucket) => (
            <BucketRow key={bucket.id} bucket={bucket} onDelete={onDelete} />
          ))}
        </div>
      )}
      {shown.length > 0 && view === "table" && <BucketTable buckets={shown} onDelete={onDelete} />}

      <CreateBucketDialog open={createOpen} onOpenChange={setCreateOpen} />
      <DeleteBucketDialog bucket={toDelete} onClose={() => setToDelete(null)} />
    </ListPage>
  )
}
