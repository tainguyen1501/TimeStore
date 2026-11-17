/**
 * What a file is, from its name: drives the extension icon (glyph, colour, badge) and the preview. Colours are the
 * `--file-*` theme tokens (index.css).
 */
export type FileGlyph =
  "image" | "video" | "audio" | "pdf" | "doc" | "sheet" | "code" | "web" | "archive" | "file"

export type FileKind = {
  glyph: FileGlyph
  /** CSS colour of the glyph and the badge. */
  color: string
  /** Up to four letters on the badge ("JSON", "PNG"). */
  label: string
}

const token = (name: string) => `var(--file-${name})`

const IMAGES = ["jpg", "jpeg", "png", "webp", "gif", "avif", "bmp", "ico", "heic"]
const VIDEOS = ["mp4", "mov", "webm", "mkv", "avi", "m4v"]
const AUDIO = ["mp3", "wav", "flac", "ogg", "m4a", "aac", "opus"]
const DOCS = ["doc", "docx", "rtf", "txt", "md", "odt", "log"]
const SHEETS = ["csv", "tsv", "xls", "xlsx", "ods", "parquet"]
const CODE = [
  "ts",
  "tsx",
  "js",
  "jsx",
  "mjs",
  "py",
  "go",
  "rs",
  "cs",
  "java",
  "kt",
  "rb",
  "php",
  "c",
  "h",
  "cpp",
  "sh",
  "env",
  "toml",
  "ini",
  "conf",
]
const WEB = ["html", "htm", "css", "xml"]
const ARCHIVES = ["zip", "tar", "gz", "tgz", "rar", "7z", "bz2", "xz", "zst"]

/** The extension of a key's last segment, lower case, without the dot ("" when there is none). */
export function extensionOf(name: string): string {
  const base = name.slice(name.lastIndexOf("/") + 1)
  const dot = base.lastIndexOf(".")
  return dot <= 0 || dot === base.length - 1 ? "" : base.slice(dot + 1).toLowerCase()
}

export function fileKind(ext: string): FileKind {
  const label = (ext || "file").slice(0, 4).toUpperCase()
  if (IMAGES.includes(ext)) return { glyph: "image", color: token("image"), label }
  if (ext === "svg") return { glyph: "image", color: token("vector"), label: "SVG" }
  if (VIDEOS.includes(ext)) return { glyph: "video", color: token("video"), label }
  if (AUDIO.includes(ext)) return { glyph: "audio", color: token("audio"), label }
  if (ext === "pdf") return { glyph: "pdf", color: token("pdf"), label: "PDF" }
  if (DOCS.includes(ext)) return { glyph: "doc", color: token("doc"), label }
  if (SHEETS.includes(ext)) return { glyph: "sheet", color: token("sheet"), label }
  if (ext === "json") return { glyph: "code", color: token("json"), label }
  if (ext === "sql") return { glyph: "code", color: token("sql"), label }
  if (ext === "yaml" || ext === "yml") return { glyph: "code", color: token("yaml"), label }
  if (CODE.includes(ext)) return { glyph: "code", color: token("code"), label }
  if (WEB.includes(ext)) return { glyph: "web", color: token("web"), label }
  if (ARCHIVES.includes(ext)) return { glyph: "archive", color: token("archive"), label }
  return { glyph: "file", color: token("other"), label }
}
