/**
 * How a bucket ("") or folder is public: made public itself, through a parent (the bucket or an outer folder),
 * or not at all.
 */
export type PublicState = { kind: "direct" } | { kind: "inherited"; from: string } | null

export function publicState(prefixes: readonly string[] | undefined, path: string): PublicState {
  if (!prefixes) return null
  if (prefixes.includes(path)) return { kind: "direct" }
  const parent = prefixes.find((p) => p !== path && path.startsWith(p))
  return parent === undefined ? null : { kind: "inherited", from: parent }
}
