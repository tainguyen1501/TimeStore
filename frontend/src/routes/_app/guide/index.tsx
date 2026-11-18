import { useState } from "react"
import { createFileRoute } from "@tanstack/react-router"
import { BookOpen, Check, Code2, Copy, Download, Eye, FileText } from "lucide-react"
import { z } from "zod"

import { MarkdownArticle, MarkdownSource } from "@/components/guide/markdown"
import { ListPage } from "@/components/shared/data-list"
import { PageHeader } from "@/components/shared/page-header"
import { Segmented } from "@/components/shared/segmented"
import { Button } from "@/components/ui/button"
import { useCopy } from "@/hooks/use-copy"
import { guideDocs } from "@/lib/guide"
import { cn } from "@/lib/utils"

export const Route = createFileRoute("/_app/guide/")({
  validateSearch: z.object({ doc: z.string().optional() }),
  component: GuidePage,
})

/** The user guide (docs/user-guide), in the design's doc viewer: cards to pick a page, rendered or raw Markdown. */
function GuidePage() {
  const { doc: docId } = Route.useSearch()
  const navigate = Route.useNavigate()
  const [mode, setMode] = useState<"rendered" | "markdown">("rendered")
  const { copied, copy } = useCopy()
  const active = guideDocs.find((d) => d.id === docId) ?? guideDocs[0]
  const open = (id: string) => {
    void navigate({ search: { doc: id } })
    document.querySelector("[data-slot=app-scroll]")?.scrollTo({ top: 0 })
  }

  const download = () => {
    const url = URL.createObjectURL(
      new Blob([active.markdown], { type: "text/markdown;charset=utf-8" })
    )
    const link = document.createElement("a")
    link.href = url
    link.download = `${active.id}.md`
    link.click()
    URL.revokeObjectURL(url)
  }

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Documentation & Guide"
        description="How to sign in, browse and share objects, manage access keys and read the metrics."
      />

      <div className="grid grid-cols-1 gap-3.5 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-5">
        {guideDocs.map((doc) => {
          const selected = doc.id === active.id
          return (
            <button
              key={doc.id}
              type="button"
              onClick={() => open(doc.id)}
              aria-current={selected ? "page" : undefined}
              className={cn(
                "rounded-2xl p-4 text-left transition-all duration-150",
                selected
                  ? "-translate-y-0.5 bg-primary-soft ring-2 ring-primary/40"
                  : "bg-surface hover:bg-surface-hover"
              )}
            >
              <div className="mb-2 flex items-center justify-between">
                <span
                  className={cn(
                    "font-mono text-xs font-bold tabular-nums",
                    selected ? "text-primary-text" : "text-muted-foreground"
                  )}
                >
                  {doc.number}.md
                </span>
                <BookOpen
                  className={cn(
                    "size-4",
                    selected ? "text-primary-text" : "text-muted-foreground/70"
                  )}
                />
              </div>
              <div className="text-[13.5px] leading-snug font-bold text-foreground">
                {doc.title}
              </div>
              <p className="mt-1 line-clamp-2 text-[11px] text-muted-foreground">{doc.summary}</p>
            </button>
          )
        })}
      </div>

      <article className="rounded-3xl bg-card p-6 shadow-card sm:p-7">
        <div className="mb-5 flex flex-wrap items-center justify-between gap-3 pb-5">
          <div className="flex items-center gap-2.5">
            <div className="flex size-9 shrink-0 items-center justify-center rounded-xl bg-primary-soft text-primary-text">
              <FileText className="size-4" />
            </div>
            <div>
              <div className="font-mono text-xs font-semibold text-primary-text">
                {active.id}.md
              </div>
              <div className="text-[11px] text-muted-foreground">docs/user-guide</div>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Segmented
              label="Document view"
              value={mode}
              onChange={setMode}
              options={[
                { value: "rendered", label: "Rendered", icon: <Eye /> },
                { value: "markdown", label: "Markdown", icon: <Code2 /> },
              ]}
            />
            <Button variant="secondary" onClick={() => copy(active.markdown)}>
              {copied ? <Check className="text-success" /> : <Copy />}
              {copied ? "Copied .md" : "Copy .md"}
            </Button>
            <Button onClick={download}>
              <Download />
              Download .md
            </Button>
          </div>
        </div>

        {mode === "rendered" ? (
          <MarkdownArticle markdown={active.markdown} onOpenDoc={open} />
        ) : (
          <MarkdownSource markdown={active.markdown} />
        )}
      </article>
    </ListPage>
  )
}
