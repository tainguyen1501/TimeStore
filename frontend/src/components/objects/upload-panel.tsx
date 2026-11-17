import { useState } from "react"
import {
  AlertCircle,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Trash2,
  UploadCloud,
  X,
} from "lucide-react"

import { FileIcon } from "@/components/shared/file-icon"
import { extensionOf } from "@/lib/file-kind"
import { formatBytes, percent } from "@/lib/format"
import { cn } from "@/lib/utils"
import { useUploadStore, type UploadItem } from "@/store/uploads"

const isActive = (item: UploadItem) => item.status === "queued" || item.status === "uploading"

/** Design: "Batch Upload Queue" drawer, bottom right of every page while uploads exist. */
export function UploadPanel() {
  const items = useUploadStore((state) => state.items)
  const cancel = useUploadStore((state) => state.cancel)
  const clearFinished = useUploadStore((state) => state.clearFinished)
  const [collapsed, setCollapsed] = useState(false)
  if (items.length === 0) return null

  const active = items.filter(isActive)
  const done = items.filter((item) => item.status === "done").length
  const total = items.reduce((sum, item) => sum + item.size, 0)
  const loaded = items.reduce(
    (sum, item) => sum + (item.status === "done" ? item.size : item.loaded),
    0
  )
  const overall = total > 0 ? percent(loaded, total) : active.length === 0 ? 100 : 0
  const allDone = active.length === 0 && done === items.length

  return (
    <section
      aria-label="Uploads"
      className="fixed right-5 bottom-5 z-40 w-[370px] max-w-[calc(100vw-2rem)] overflow-hidden rounded-3xl bg-popover shadow-[0_18px_50px_rgba(17,24,39,0.14)] dark:shadow-[0_18px_50px_rgba(0,0,0,0.5)]"
    >
      <div className="flex items-center justify-between gap-3 bg-surface px-5 py-4">
        <div className="flex min-w-0 items-center gap-3">
          <div className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-primary-soft text-primary-text">
            <UploadCloud className="size-4" />
          </div>
          <div className="min-w-0">
            <h2 className="truncate font-sans text-[13px] font-bold text-foreground">
              {active.length > 0
                ? `Uploading ${active.length} of ${items.length} files`
                : `Uploaded ${done} of ${items.length} files`}
            </h2>
            <div className="text-[11px] text-muted-foreground tabular-nums">
              {overall}% · {formatBytes(loaded)} / {formatBytes(total)}
            </div>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <button
            type="button"
            onClick={() => setCollapsed((c) => !c)}
            aria-label={collapsed ? "Expand uploads" : "Minimize uploads"}
            title={collapsed ? "Expand uploads" : "Minimize uploads"}
            className="rounded-xl p-1.5 text-muted-foreground transition-colors hover:text-foreground"
          >
            {collapsed ? <ChevronUp className="size-4" /> : <ChevronDown className="size-4" />}
          </button>
          {active.length < items.length && (
            <button
              type="button"
              onClick={clearFinished}
              aria-label={active.length === 0 ? "Dismiss uploads" : "Clear finished"}
              title={active.length === 0 ? "Dismiss" : "Clear finished"}
              className="rounded-xl p-1.5 text-muted-foreground transition-colors hover:text-foreground"
            >
              {active.length === 0 ? <X className="size-4" /> : <Trash2 className="size-3.5" />}
            </button>
          )}
        </div>
      </div>

      <div className="h-1.5 w-full bg-muted">
        <div
          style={{ width: `${overall}%` }}
          className={cn(
            "h-full rounded-r-full transition-all duration-200",
            allDone ? "bg-success" : "bg-primary"
          )}
        />
      </div>

      {!collapsed && (
        <ul className="max-h-[280px] space-y-2 overflow-y-auto p-3">
          {items.map((item) => {
            const progress = item.status === "done" ? 100 : percent(item.loaded, item.size)
            const name = item.key.slice(item.key.lastIndexOf("/") + 1)
            const folder = item.key.slice(0, item.key.length - name.length)
            return (
              <li key={item.id} className="flex items-center gap-3 rounded-2xl bg-surface p-2.5">
                <FileIcon ext={extensionOf(name)} size="sm" />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center justify-between gap-2">
                    <span
                      className="truncate text-xs font-semibold text-foreground"
                      title={`${item.bucket}/${item.key}`}
                    >
                      {name}
                    </span>
                    <span
                      className={cn(
                        "shrink-0 text-[11px] font-semibold tabular-nums",
                        item.status === "failed" || item.status === "cancelled"
                          ? "text-destructive"
                          : "text-primary-text"
                      )}
                      title={item.error}
                    >
                      {item.status === "done"
                        ? "Done"
                        : item.status === "failed"
                          ? "Failed"
                          : item.status === "cancelled"
                            ? "Cancelled"
                            : item.status === "queued"
                              ? "Waiting"
                              : `${progress}%`}
                    </span>
                  </div>
                  <div className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-muted">
                    <div
                      style={{ width: `${progress}%` }}
                      className={cn(
                        "h-full rounded-full transition-all duration-150",
                        item.status === "done"
                          ? "bg-success"
                          : item.status === "failed" || item.status === "cancelled"
                            ? "bg-destructive"
                            : "bg-primary"
                      )}
                    />
                  </div>
                  <div className="mt-1 flex items-center justify-between text-[10px] text-muted-foreground tabular-nums">
                    <span className="truncate">
                      → {item.bucket}/{folder}
                    </span>
                    <span className="shrink-0">
                      {formatBytes(item.status === "done" ? item.size : item.loaded)} /{" "}
                      {formatBytes(item.size)}
                    </span>
                  </div>
                  {item.status === "failed" && item.error && (
                    <p className="mt-1 text-[11px] text-destructive">{item.error}</p>
                  )}
                </div>
                <div className="shrink-0">
                  {item.status === "done" && <CheckCircle2 className="size-4 text-success" />}
                  {isActive(item) && (
                    <button
                      type="button"
                      onClick={() => cancel(item.id)}
                      aria-label={`Cancel upload of ${item.key}`}
                      title="Cancel upload"
                      className="rounded-lg p-1 text-muted-foreground transition-colors hover:text-destructive"
                    >
                      <X className="size-4" />
                    </button>
                  )}
                  {item.status === "failed" && (
                    <AlertCircle className="size-4 text-warning" aria-label={item.error} />
                  )}
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}
