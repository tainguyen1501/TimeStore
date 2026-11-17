import { cn } from "@/lib/utils"
import { fileKind, type FileGlyph } from "@/lib/file-kind"

const SIZES = {
  sm: { box: "size-9 rounded-xl", glyph: "size-4", badge: "px-1 py-px text-[7px]" },
  md: { box: "size-11 rounded-[14px]", glyph: "size-5", badge: "px-1.5 py-px text-[8px]" },
  lg: { box: "size-14 rounded-2xl", glyph: "size-6", badge: "px-1.5 py-0.5 text-[9px]" },
} as const

/**
 * A tinted tile with the file kind's glyph and a coloured extension badge (design: FileExtensionIcon). The colour
 * comes from the kind's `--file-*` token; the tile is that colour mixed into the background (`tint`).
 */
export function FileIcon({
  ext,
  size = "md",
  className,
}: {
  ext: string
  size?: keyof typeof SIZES
  className?: string
}) {
  const kind = fileKind(ext)
  const s = SIZES[size]
  return (
    <div
      className={cn(
        "relative flex shrink-0 items-center justify-center tint select-none",
        s.box,
        className
      )}
      style={{ "--tint-color": kind.color } as React.CSSProperties}
      title={ext ? `.${ext} file` : "File"}
      aria-hidden
    >
      <Glyph glyph={kind.glyph} className={cn("-mt-1", s.glyph)} />
      <span
        className={cn(
          "absolute -bottom-1 rounded-md font-mono leading-none font-bold tracking-tight text-white shadow-xs",
          s.badge
        )}
        style={{ backgroundColor: kind.color }}
      >
        {kind.label}
      </span>
    </div>
  )
}

/** The design's hand-drawn glyphs (24×24, currentColor). */
function Glyph({ glyph, className }: { glyph: FileGlyph; className?: string }) {
  const common = { viewBox: "0 0 24 24", fill: "none", className }
  const stroke = {
    stroke: "currentColor",
    strokeWidth: 1.9,
    strokeLinecap: "round",
    strokeLinejoin: "round",
  } as const
  switch (glyph) {
    case "image":
      return (
        <svg {...common}>
          <rect x="3" y="4" width="18" height="16" rx="3" {...stroke} />
          <circle cx="8.5" cy="9.5" r="1.5" fill="currentColor" />
          <path d="M5 17L9.5 12.5L13 15.5L15.5 13L19 17" {...stroke} />
        </svg>
      )
    case "video":
      return (
        <svg {...common}>
          <rect x="3" y="5" width="18" height="14" rx="3" {...stroke} />
          <path d="M10 9.5L15 12L10 14.5V9.5Z" fill="currentColor" />
        </svg>
      )
    case "audio":
      return (
        <svg {...common}>
          <path d="M9 17.5V7.5L18 5.5V15.5" {...stroke} />
          <circle cx="7" cy="17.5" r="2" fill="currentColor" />
          <circle cx="16" cy="15.5" r="2" fill="currentColor" />
        </svg>
      )
    case "pdf":
      return (
        <svg {...common}>
          <path
            d="M7 3.5H14L18.5 8V20.5H7C5.9 20.5 5 19.6 5 18.5V5.5C5 4.4 5.9 3.5 7 3.5Z"
            {...stroke}
          />
          <path d="M14 3.5V8H18.5" {...stroke} />
          <path d="M8.5 13.5H15.5M8.5 16.5H13" {...stroke} />
        </svg>
      )
    case "doc":
      return (
        <svg {...common}>
          <path
            d="M7 3.5H14.5L18.5 7.5V20.5H7C5.9 20.5 5 19.6 5 18.5V5.5C5 4.4 5.9 3.5 7 3.5Z"
            {...stroke}
          />
          <path d="M8.5 11H15.5M8.5 14.5H15.5M8.5 18H12.5" {...stroke} />
        </svg>
      )
    case "sheet":
      return (
        <svg {...common}>
          <rect x="4" y="4" width="16" height="16" rx="2.5" {...stroke} />
          <path d="M4 10H20M4 15H20M10 10V20" {...stroke} strokeWidth={1.8} />
        </svg>
      )
    case "code":
      return (
        <svg {...common}>
          <path d="M8.5 8L4.5 12L8.5 16" {...stroke} strokeWidth={2} />
          <path d="M15.5 8L19.5 12L15.5 16" {...stroke} strokeWidth={2} />
          <path d="M13 6.5L11 17.5" {...stroke} />
        </svg>
      )
    case "web":
      return (
        <svg {...common}>
          <circle cx="12" cy="12" r="8" {...stroke} />
          <path
            d="M4 12H20M12 4C14.5 6.8 14.5 17.2 12 20M12 4C9.5 6.8 9.5 17.2 12 20"
            {...stroke}
            strokeWidth={1.7}
          />
        </svg>
      )
    case "archive":
      return (
        <svg {...common}>
          <rect x="5" y="4" width="14" height="16" rx="2.5" {...stroke} />
          <path d="M12 4V14M10.5 7H13.5M10.5 10H13.5M10.5 13H13.5" {...stroke} />
        </svg>
      )
    default:
      return (
        <svg {...common}>
          <path
            d="M7 3.5H14.5L18.5 7.5V20.5H7C5.9 20.5 5 19.6 5 18.5V5.5C5 4.4 5.9 3.5 7 3.5Z"
            {...stroke}
          />
        </svg>
      )
  }
}

/** The amber folder tile (design: folders in the explorer). */
export function FolderIcon({ size = "md", className }: { size?: "sm" | "md"; className?: string }) {
  return (
    <div
      className={cn(
        "flex shrink-0 items-center justify-center tint",
        size === "sm" ? "size-9 rounded-xl" : "size-10 rounded-xl",
        className
      )}
      style={{ "--tint-color": "var(--file-folder)" } as React.CSSProperties}
      aria-hidden
    >
      <svg viewBox="0 0 24 24" className="size-4" fill="currentColor">
        <path d="M3 7.5C3 6.4 3.9 5.5 5 5.5H9.2C9.7 5.5 10.2 5.7 10.6 6.1L12 7.5H19C20.1 7.5 21 8.4 21 9.5V17.5C21 18.6 20.1 19.5 19 19.5H5C3.9 19.5 3 18.6 3 17.5V7.5Z" />
      </svg>
    </div>
  )
}
