import { useEffect, useMemo, useState } from "react"
import { Check, Copy, Download, Maximize2, Minimize2 } from "lucide-react"

import { FileIcon } from "@/components/shared/file-icon"
import { Segmented } from "@/components/shared/segmented"
import { Button } from "@/components/ui/button"
import { useCopy } from "@/hooks/use-copy"
import { extensionOf } from "@/lib/file-kind"
import { formatBytes } from "@/lib/format"
import { cn } from "@/lib/utils"

/** Text previews read at most this much (the rest is never downloaded). */
const TEXT_PREVIEW_BYTES = 64 * 1024
/** Larger images are not loaded just to preview them. */
const IMAGE_PREVIEW_MAX_BYTES = 25 * 1024 * 1024
const CSV_ROWS = 120

const TEXT_TYPES = [
  "application/json",
  "application/xml",
  "application/yaml",
  "application/x-yaml",
  "application/javascript",
  "application/x-sh",
  "application/toml",
]

type PreviewKind = "image" | "video" | "audio" | "text" | null

/**
 * What the object can be shown as. Nothing is ever rendered as a page: images go into <img> (SVG scripts do not run
 * there), video and audio into media elements, text into <pre> or a table (CSV). HTML, PDF and everything else are
 * download only (docs/research/admin-ui.md §11).
 */
function previewKind(contentType: string, size: number): PreviewKind {
  const type = contentType.split(";")[0].trim().toLowerCase()
  if (type.startsWith("image/")) return size <= IMAGE_PREVIEW_MAX_BYTES ? "image" : null
  if (type.startsWith("video/")) return "video"
  if (type.startsWith("audio/")) return "audio"
  if (type === "text/html") return null
  if (type.startsWith("text/") || TEXT_TYPES.includes(type) || type.endsWith("+json")) return "text"
  return null
}

/** The first 64 KB of a text object (the rest is never downloaded). */
function useTextPreview(url: string, enabled: boolean) {
  const [state, setState] = useState<{ url: string; text?: string; failed?: boolean }>({ url })
  useEffect(() => {
    if (!enabled) return
    const abort = new AbortController()
    void (async () => {
      try {
        const response = await fetch(url, { signal: abort.signal, credentials: "same-origin" })
        if (!response.ok || !response.body) throw new Error(String(response.status))
        const reader = response.body.getReader()
        const chunks: Uint8Array[] = []
        let total = 0
        while (total < TEXT_PREVIEW_BYTES) {
          const { done, value } = await reader.read()
          if (done) break
          chunks.push(value)
          total += value.length
        }
        await reader.cancel()
        const bytes = new Uint8Array(Math.min(total, TEXT_PREVIEW_BYTES))
        let offset = 0
        for (const chunk of chunks) {
          const part = chunk.subarray(0, bytes.length - offset)
          bytes.set(part, offset)
          offset += part.length
          if (offset >= bytes.length) break
        }
        setState({ url, text: new TextDecoder("utf-8").decode(bytes) })
      } catch {
        if (!abort.signal.aborted) setState({ url, failed: true })
      }
    })()
    return () => abort.abort()
  }, [url, enabled])
  return state.url === url ? state : { url }
}

function parseCsv(text: string): string[][] {
  return text
    .trim()
    .split(/\r?\n/)
    .slice(0, CSV_ROWS)
    .map((line) => {
      const cells: string[] = []
      let current = ""
      let quoted = false
      for (const ch of line) {
        if (ch === '"') quoted = !quoted
        else if (ch === "," && !quoted) {
          cells.push(current.trim())
          current = ""
        } else current += ch
      }
      cells.push(current.trim())
      return cells
    })
}

/**
 * Design: the preview viewport of the file preview modal — a header with the file's icon, name and path, view
 * controls, and the content in the middle.
 */
export function ObjectViewport({
  url,
  name,
  path,
  contentType,
  size,
  onClose,
}: {
  url: string
  name: string
  /** "bucket / key" under the name. */
  path: string
  contentType: string
  size: number
  onClose?: React.ReactNode
}) {
  const kind = previewKind(contentType, size)
  const ext = extensionOf(name)
  const isCsv = kind === "text" && (ext === "csv" || contentType.startsWith("text/csv"))
  const [tab, setTab] = useState<"rendered" | "raw">("rendered")
  const [zoomed, setZoomed] = useState(false)
  const text = useTextPreview(url, kind === "text")
  const { copied, copy } = useCopy()
  const rows = useMemo(() => (isCsv && text.text ? parseCsv(text.text) : []), [isCsv, text.text])

  return (
    <div className="flex min-w-0 flex-1 flex-col bg-surface">
      <div className="flex items-center justify-between gap-3 bg-background/80 px-6 py-4">
        <div className="flex min-w-0 items-center gap-3">
          <FileIcon ext={ext} size="sm" />
          <div className="min-w-0">
            <h3 className="truncate font-sans text-[15px] font-bold text-foreground">{name}</h3>
            <div className="truncate text-xs text-muted-foreground">{path}</div>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {isCsv && (
            <Segmented
              label="CSV view"
              value={tab}
              onChange={setTab}
              options={[
                { value: "rendered", label: "Preview" },
                { value: "raw", label: "Source" },
              ]}
            />
          )}
          {kind === "image" && (
            <Button
              variant="secondary"
              size="icon"
              aria-label={zoomed ? "Fit to view" : "Zoom"}
              title={zoomed ? "Fit to view" : "Zoom"}
              onClick={() => setZoomed((z) => !z)}
            >
              {zoomed ? <Minimize2 /> : <Maximize2 />}
            </Button>
          )}
          {kind === "text" && text.text !== undefined && (
            <Button variant="secondary" size="sm" onClick={() => copy(text.text ?? "")}>
              {copied ? <Check className="text-success" /> : <Copy />}
              {copied ? "Copied" : "Copy"}
            </Button>
          )}
          {onClose}
        </div>
      </div>

      <div className="flex min-h-[340px] flex-1 items-center justify-center overflow-auto p-6">
        {kind === "image" && (
          <div className="flex size-full items-center justify-center overflow-auto">
            <img
              src={url}
              alt={name}
              onClick={() => setZoomed((z) => !z)}
              className={cn(
                "rounded-2xl shadow-sm transition-transform duration-200",
                zoomed
                  ? "max-w-none scale-125 cursor-zoom-out"
                  : "max-h-[60vh] max-w-full cursor-zoom-in object-contain"
              )}
            />
          </div>
        )}
        {kind === "video" && (
          <video
            src={url}
            controls
            className="max-h-[60vh] w-full rounded-2xl bg-black shadow-md"
          />
        )}
        {kind === "audio" && (
          <div className="w-full max-w-md rounded-3xl bg-raised p-8 text-center shadow-sm">
            <div className="mb-4 flex justify-center">
              <FileIcon ext={ext} size="lg" />
            </div>
            <div className="mb-1 truncate text-base font-bold text-foreground">{name}</div>
            <div className="mb-6 text-xs text-muted-foreground">
              Audio stream · {formatBytes(size)}
            </div>
            <audio src={url} controls className="w-full" />
          </div>
        )}
        {kind === "text" && (
          <div className="h-[60vh] w-full overflow-auto rounded-2xl bg-raised p-5 shadow-xs">
            {text.failed ? (
              <div className="flex h-full items-center justify-center text-sm text-destructive">
                The preview could not be loaded.
              </div>
            ) : text.text === undefined ? (
              <div className="flex h-full items-center justify-center text-sm text-muted-foreground">
                Loading object content…
              </div>
            ) : isCsv && tab === "rendered" ? (
              <div className="overflow-x-auto">
                <table className="w-full border-collapse text-left text-xs">
                  {rows[0] && (
                    <thead>
                      <tr className="bg-surface">
                        {rows[0].map((cell, i) => (
                          <th
                            key={i}
                            className="px-3 py-2.5 font-semibold whitespace-nowrap text-foreground"
                          >
                            {cell}
                          </th>
                        ))}
                      </tr>
                    </thead>
                  )}
                  <tbody className="divide-y divide-border">
                    {rows.slice(1).map((row, i) => (
                      <tr key={i} className="hover:bg-surface">
                        {row.map((cell, j) => (
                          <td
                            key={j}
                            className="px-3 py-2 whitespace-nowrap text-subtle-foreground tabular-nums"
                          >
                            {cell}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <pre className="overflow-x-auto font-mono text-xs leading-6 text-foreground">
                {text.text.split("\n").map((line, i) => (
                  <div key={i} className="flex gap-4">
                    <span className="w-8 shrink-0 text-right text-muted-foreground/70 tabular-nums select-none">
                      {i + 1}
                    </span>
                    <span className="whitespace-pre">{line || " "}</span>
                  </div>
                ))}
              </pre>
            )}
            {text.text !== undefined && size > TEXT_PREVIEW_BYTES && (
              <p className="mt-3 text-[11px] text-muted-foreground">
                First {formatBytes(TEXT_PREVIEW_BYTES)} of {formatBytes(size)}.
              </p>
            )}
          </div>
        )}
        {kind === null && (
          <div className="w-full max-w-md rounded-3xl bg-raised p-8 text-center shadow-xs">
            <div className="mb-4 flex justify-center">
              <FileIcon ext={ext} size="lg" />
            </div>
            <h4 className="truncate text-base font-bold text-foreground">{name}</h4>
            <p className="mt-1 mb-6 text-xs text-muted-foreground">
              {contentType || "Binary"} · {formatBytes(size)} · no preview for this type
            </p>
            <Button render={<a href={url} download />}>
              <Download />
              Download Object
            </Button>
          </div>
        )}
      </div>
    </div>
  )
}
