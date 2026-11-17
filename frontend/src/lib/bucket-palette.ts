/**
 * Bucket cards are tinted (design: rose, sky, amber, slate, violet). TimeStore stores no colour per bucket, so the
 * palette follows from the name: stable across pages and sessions. Colours are the `--palette-*` theme tokens.
 */
const PALETTES = ["violet", "rose", "sky", "amber", "slate"] as const

export type BucketPalette = (typeof PALETTES)[number]

export function bucketPalette(name: string): BucketPalette {
  let hash = 0
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) | 0
  return PALETTES[Math.abs(hash) % PALETTES.length]
}

/** `style` for an element using the `tint` utility in a bucket's colour. */
export const paletteStyle = (palette: BucketPalette) =>
  ({ "--tint-color": `var(--palette-${palette})` }) as React.CSSProperties

export const paletteColor = (palette: BucketPalette) => `var(--palette-${palette})`
