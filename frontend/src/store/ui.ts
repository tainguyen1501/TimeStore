import { create } from "zustand"

/** How a list shows its entries (design: grid cards, list rows, table). */
export type ViewMode = "grid" | "list" | "table"

const VIEW_KEY = "timestore.views"

type Views = { buckets: ViewMode; objects: ViewMode }
const DEFAULT_VIEWS: Views = { buckets: "grid", objects: "list" }

function readViews(): Views {
  try {
    const stored = JSON.parse(localStorage.getItem(VIEW_KEY) ?? "{}") as Partial<Views>
    const valid = (v: unknown): v is ViewMode => v === "grid" || v === "list" || v === "table"
    return {
      buckets: valid(stored.buckets) ? stored.buckets : DEFAULT_VIEWS.buckets,
      objects: valid(stored.objects) ? stored.objects : DEFAULT_VIEWS.objects,
    }
  } catch {
    return DEFAULT_VIEWS
  }
}

type UiState = {
  /** The navigation drawer on small screens. */
  menuOpen: boolean
  setMenuOpen: (open: boolean) => void
  /** View modes, remembered per browser (a convenience: nothing breaks when storage is blocked). */
  views: Views
  setView: (list: keyof Views, mode: ViewMode) => void
  /**
   * Where "Upload files" in the sidebar sends files: the folder open on a bucket page, or none (the card then points
   * to the buckets).
   */
  uploadTarget: { bucket: string; prefix: string; onUploaded: () => void } | null
  setUploadTarget: (target: UiState["uploadTarget"]) => void
}

export const useUiStore = create<UiState>((set, get) => ({
  menuOpen: false,
  setMenuOpen: (menuOpen) => set({ menuOpen }),
  views: readViews(),
  setView: (list, mode) => {
    const views = { ...get().views, [list]: mode }
    try {
      localStorage.setItem(VIEW_KEY, JSON.stringify(views))
    } catch {
      // Storage blocked: the choice lasts for this page.
    }
    set({ views })
  },
  uploadTarget: null,
  setUploadTarget: (uploadTarget) => set({ uploadTarget }),
}))
