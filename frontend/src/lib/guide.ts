/**
 * The user guide shown on the Guide page: `docs/user-guide/*.md`, bundled into the UI at build time (one source for
 * the repository and the app; no server endpoint). The README is the overview; the other pages follow in reading
 * order.
 */
const files = import.meta.glob("../../../docs/user-guide/*.md", {
  query: "?raw",
  import: "default",
  eager: true,
}) as Record<string, string>

export type GuideDoc = {
  /** File name without ".md" ("sign-in"); links between pages use "<id>.md". */
  id: string
  number: string
  title: string
  summary: string
  markdown: string
}

/** Reading order; files not listed come after, by name. */
const ORDER = ["README", "sign-in", "objects", "access-keys", "metrics", "backups", "replication"]

const titleOf = (markdown: string, id: string) =>
  markdown.match(/^# (.+)$/m)?.[1].trim() ?? id.replace(/-/g, " ")

/** The first paragraph after the title, without Markdown marks. */
function summaryOf(markdown: string): string {
  const paragraph = markdown
    .replace(/^# .+$/m, "")
    .split(/\n\s*\n/)
    .map((block) => block.trim())
    .find((block) => block && !/^(#|-|\||```|\d+\.)/.test(block))
  return (paragraph ?? "")
    .replace(/\s+/g, " ")
    .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
    .replace(/[`*]/g, "")
}

export const guideDocs: GuideDoc[] = Object.entries(files)
  .map(([path, markdown]) => ({ id: path.slice(path.lastIndexOf("/") + 1, -3), markdown }))
  .sort((a, b) => {
    const rank = (id: string) => (ORDER.includes(id) ? ORDER.indexOf(id) : ORDER.length)
    return rank(a.id) - rank(b.id) || a.id.localeCompare(b.id)
  })
  .map((doc, i) => ({
    ...doc,
    number: String(i + 1).padStart(2, "0"),
    title: doc.id === "README" ? "Overview" : titleOf(doc.markdown, doc.id),
    summary: summaryOf(doc.markdown),
  }))
