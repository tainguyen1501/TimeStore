import { useMemo, useState } from "react"
import { Check, Copy } from "lucide-react"

import { useCopy } from "@/hooks/use-copy"

type Block =
  | { type: "h1" | "h2" | "h3" | "p" | "quote"; text: string }
  | { type: "code"; lang: string; code: string }
  | { type: "ul" | "ol"; items: string[] }
  | { type: "table"; head: string[]; rows: string[][] }

const LIST_ITEM = /^(\s*)([-*]|\d+\.)\s+/
const isBlockStart = (line: string) =>
  line.startsWith("```") ||
  line.startsWith("#") ||
  line.startsWith("> ") ||
  line.startsWith("|") ||
  LIST_ITEM.test(line)

const cells = (line: string) =>
  line
    .trim()
    .replace(/^\||\|$/g, "")
    .split(/(?<!\\)\|/)
    .map((cell) => cell.trim().replace(/\\\|/g, "|"))

/** The Markdown the user guide uses: headings, paragraphs, lists (with wrapped lines), tables, code, quotes. */
function parse(markdown: string): Block[] {
  const lines = markdown.replace(/\r\n/g, "\n").split("\n")
  const out: Block[] = []
  let i = 0
  while (i < lines.length) {
    const line = lines[i]
    if (!line.trim()) {
      i++
      continue
    }
    if (line.startsWith("```")) {
      const lang = line.slice(3).trim()
      const code: string[] = []
      i++
      while (i < lines.length && !lines[i].startsWith("```")) code.push(lines[i++])
      out.push({ type: "code", lang, code: code.join("\n") })
      i++
      continue
    }
    const heading = line.match(/^(#{1,3}) (.+)$/)
    if (heading) {
      out.push({
        type: (["h1", "h2", "h3"] as const)[heading[1].length - 1],
        text: heading[2].trim(),
      })
      i++
      continue
    }
    if (line.startsWith("|")) {
      const rows: string[][] = []
      while (i < lines.length && lines[i].startsWith("|")) {
        if (!/^\|[\s:|-]+\|?$/.test(lines[i].trim())) rows.push(cells(lines[i]))
        i++
      }
      out.push({ type: "table", head: rows[0] ?? [], rows: rows.slice(1) })
      continue
    }
    if (line.startsWith("> ")) {
      const quote: string[] = []
      while (i < lines.length && lines[i].startsWith(">"))
        quote.push(lines[i++].replace(/^>\s?/, ""))
      out.push({ type: "quote", text: quote.join(" ") })
      continue
    }
    const list = line.match(LIST_ITEM)
    if (list) {
      const ordered = /\d/.test(list[2])
      const items: string[] = []
      while (i < lines.length && lines[i].trim()) {
        const item = lines[i].match(LIST_ITEM)
        if (item) items.push(lines[i].slice(item[0].length))
        else if (/^\s+/.test(lines[i]) && items.length)
          items[items.length - 1] += " " + lines[i].trim()
        else break
        i++
      }
      out.push({ type: ordered ? "ol" : "ul", items })
      continue
    }
    const paragraph: string[] = []
    while (i < lines.length && lines[i].trim() && !isBlockStart(lines[i]))
      paragraph.push(lines[i++].trim())
    out.push({ type: "p", text: paragraph.join(" ") })
  }
  return out
}

/** Inline marks: `code`, **bold**, *italic*, [links](target). Guide pages link to each other as "<page>.md". */
function Inline({ text, onOpenDoc }: { text: string; onOpenDoc: (id: string) => void }) {
  const parts: React.ReactNode[] = []
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*|\*[^*\s][^*]*\*|\[[^\]]+\]\([^)]+\))/g
  let last = 0
  let key = 0
  for (const match of text.matchAll(pattern)) {
    const raw = match[0]
    if (match.index > last) parts.push(text.slice(last, match.index))
    if (raw.startsWith("`"))
      parts.push(
        <code
          key={key++}
          className="mx-0.5 rounded-md bg-field px-1.5 py-0.5 font-mono text-[12px] text-warning"
        >
          {raw.slice(1, -1)}
        </code>
      )
    else if (raw.startsWith("**"))
      parts.push(
        <strong key={key++} className="font-bold text-foreground">
          {raw.slice(2, -2)}
        </strong>
      )
    else if (raw.startsWith("["))
      parts.push(<DocLink key={key++} raw={raw} onOpenDoc={onOpenDoc} />)
    else parts.push(<em key={key++}>{raw.slice(1, -1)}</em>)
    last = match.index + raw.length
  }
  if (last < text.length) parts.push(text.slice(last))
  return <>{parts}</>
}

function DocLink({ raw, onOpenDoc }: { raw: string; onOpenDoc: (id: string) => void }) {
  const [, label, target] = raw.match(/^\[([^\]]+)\]\(([^)]+)\)$/) ?? []
  const className = "font-semibold text-primary-text underline-offset-4 hover:underline"
  if (/^https?:\/\//.test(target))
    return (
      <a href={target} target="_blank" rel="noreferrer" className={className}>
        {label}
      </a>
    )
  const page = target.match(/^(?:\.\/)?([\w-]+)\.md(#.*)?$/)
  if (page)
    return (
      <button type="button" onClick={() => onOpenDoc(page[1])} className={className}>
        {label}
      </button>
    )
  return <span className="font-semibold text-foreground">{label}</span> // a repository path: not part of the app
}

/** The design's rendered document (MarkdownArticle) over the user guide's Markdown. */
export function MarkdownArticle({
  markdown,
  onOpenDoc,
}: {
  markdown: string
  onOpenDoc: (id: string) => void
}) {
  const blocks = useMemo(() => parse(markdown), [markdown])
  const inline = (text: string) => <Inline text={text} onOpenDoc={onOpenDoc} />

  return (
    <div className="space-y-3 text-[13.5px] leading-relaxed text-subtle-foreground">
      {blocks.map((block, i) => {
        switch (block.type) {
          case "h1":
            return (
              <h2
                key={i}
                className="pb-1 text-[22px] font-extrabold tracking-tight text-foreground"
              >
                {block.text}
              </h2>
            )
          case "h2":
            return (
              <h3
                key={i}
                className="pt-3 pb-0.5 text-[18px] font-extrabold tracking-tight text-foreground"
              >
                {inline(block.text)}
              </h3>
            )
          case "h3":
            return (
              <h4 key={i} className="pt-2 text-[14.5px] font-bold text-foreground">
                {inline(block.text)}
              </h4>
            )
          case "code":
            return <CodeBlock key={i} lang={block.lang} code={block.code} />
          case "ul":
            return (
              <ul key={i} className="space-y-1.5 pl-1">
                {block.items.map((item, j) => (
                  <li key={j} className="flex items-start gap-2.5">
                    <span className="mt-2 size-1.5 shrink-0 rounded-full bg-primary-text" />
                    <span>{inline(item)}</span>
                  </li>
                ))}
              </ul>
            )
          case "ol":
            return (
              <ol key={i} className="space-y-1.5 pl-1">
                {block.items.map((item, j) => (
                  <li key={j} className="flex items-start gap-2.5">
                    <span className="mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-md bg-primary-soft text-[11px] font-bold text-primary-text tabular-nums">
                      {j + 1}
                    </span>
                    <span>{inline(item)}</span>
                  </li>
                ))}
              </ol>
            )
          case "table":
            return (
              <div key={i} className="overflow-x-auto rounded-2xl bg-surface p-2">
                <table className="w-full border-collapse text-left text-xs">
                  <thead>
                    <tr className="text-[11px] font-semibold text-muted-foreground">
                      {block.head.map((cell, j) => (
                        <th key={j} className="px-3 py-2">
                          {inline(cell)}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {block.rows.map((row, j) => (
                      <tr key={j} className="align-top">
                        {row.map((cell, k) => (
                          <td
                            key={k}
                            className="bg-raised px-3 py-2 first:rounded-l-xl last:rounded-r-xl [tr:not(:last-child)>&]:border-b"
                          >
                            {inline(cell)}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          case "quote":
            return (
              <div
                key={i}
                className="rounded-2xl bg-primary-soft p-4 text-[13px] text-primary-text"
              >
                {inline(block.text)}
              </div>
            )
          default:
            return <p key={i}>{inline(block.text)}</p>
        }
      })}
    </div>
  )
}

function CodeBlock({ lang, code }: { lang: string; code: string }) {
  const { copied, copy } = useCopy()
  return (
    <div className="my-3.5 overflow-hidden rounded-2xl bg-code text-code-foreground">
      <div className="flex items-center justify-between bg-code-bar px-4 py-2">
        <span className="font-mono text-[11px] font-medium text-muted-foreground uppercase">
          {lang || "text"}
        </span>
        <button
          type="button"
          onClick={() => copy(code)}
          className="inline-flex items-center gap-1.5 rounded-lg bg-white/5 px-2.5 py-1 text-[11px] font-medium text-code-foreground/80 transition-colors hover:text-code-foreground"
        >
          {copied ? <Check className="size-3 text-success" /> : <Copy className="size-3" />}
          <span>{copied ? "Copied" : "Copy"}</span>
        </button>
      </div>
      <pre className="overflow-x-auto p-4 font-mono text-[12.5px] leading-6">{code}</pre>
    </div>
  )
}

/** Raw Markdown view (design: the "Markdown" switch). */
export function MarkdownSource({ markdown }: { markdown: string }) {
  const [wrap] = useState(true)
  return (
    <pre
      className={
        "overflow-x-auto rounded-2xl bg-code p-5 font-mono text-[12.5px] leading-6 text-code-foreground" +
        (wrap ? " whitespace-pre-wrap" : "")
      }
    >
      {markdown}
    </pre>
  )
}
