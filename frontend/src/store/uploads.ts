import { isCancel } from "axios"
import { create } from "zustand"

import { getErrorMessage } from "@/lib/api-response"
import { uploadFile } from "@/lib/upload"

export type UploadStatus = "queued" | "uploading" | "done" | "failed" | "cancelled"

export type UploadItem = {
  id: string
  bucket: string
  key: string
  size: number
  loaded: number
  status: UploadStatus
  error?: string
}

/** Files uploading at once (each may run several parts in parallel). */
const FILE_CONCURRENCY = 2

type UploadState = {
  items: UploadItem[]
  /** Queues files under `prefix`; `onUploaded` runs after each successful file (refresh a listing). */
  enqueue: (bucket: string, prefix: string, files: File[], onUploaded: () => void) => void
  cancel: (id: string) => void
  /** Removes finished, failed and cancelled entries. */
  clearFinished: () => void
}

// Outside the store: files and abort controllers are not state to render.
const files = new Map<string, File>()
const controllers = new Map<string, AbortController>()
const callbacks = new Map<string, () => void>()

/** Uploads keep running while the user moves between pages of the app; the panel shows them. */
export const useUploadStore = create<UploadState>((set, get) => {
  const update = (id: string, patch: Partial<UploadItem>) =>
    set((state) => ({
      items: state.items.map((item) => (item.id === id ? { ...item, ...patch } : item)),
    }))

  const pump = () => {
    const { items } = get()
    let running = items.filter((item) => item.status === "uploading").length
    for (const item of items) {
      if (running >= FILE_CONCURRENCY) break
      if (item.status !== "queued") continue
      running++
      void run(item)
    }
  }

  const run = async (item: UploadItem) => {
    const file = files.get(item.id)
    if (!file) return
    const controller = new AbortController()
    controllers.set(item.id, controller)
    update(item.id, { status: "uploading" })
    try {
      await uploadFile(
        item.bucket,
        item.key,
        file,
        (loaded) => update(item.id, { loaded }),
        controller.signal
      )
      update(item.id, { status: "done", loaded: item.size })
      callbacks.get(item.id)?.()
    } catch (error) {
      if (isCancel(error) || controller.signal.aborted) update(item.id, { status: "cancelled" })
      else update(item.id, { status: "failed", error: getErrorMessage(error) })
    } finally {
      files.delete(item.id)
      controllers.delete(item.id)
      callbacks.delete(item.id)
      pump()
    }
  }

  return {
    items: [],
    enqueue: (bucket, prefix, list, onUploaded) => {
      const added = list.map((file) => {
        const id = crypto.randomUUID()
        files.set(id, file)
        callbacks.set(id, onUploaded)
        // A dropped folder keeps its structure (webkitRelativePath: "folder/sub/file.txt").
        const relative = file.webkitRelativePath || file.name
        return {
          id,
          bucket,
          key: prefix + relative,
          size: file.size,
          loaded: 0,
          status: "queued" as const,
        }
      })
      set((state) => ({ items: [...state.items, ...added] }))
      pump()
    },
    cancel: (id) => {
      const controller = controllers.get(id)
      if (controller) controller.abort()
      else {
        files.delete(id)
        callbacks.delete(id)
        update(id, { status: "cancelled" })
      }
    },
    clearFinished: () =>
      set((state) => ({
        items: state.items.filter(
          (item) => item.status === "queued" || item.status === "uploading"
        ),
      })),
  }
})
