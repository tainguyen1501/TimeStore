import { useId } from "react"

import { cn } from "@/lib/utils"

// The bucket's outline on the 24-unit grid; the shading below is clipped to it.
const BUCKET = "M3.5 6.5 5.6 18.6A2.2 2.2 0 0 0 7.8 20.5h8.4a2.2 2.2 0 0 0 2.2-1.9L20.5 6.5z"
const BOLT = "M13.2 9.2 9.7 14.2h2.6l-0.9 3.6 3.6-5h-2.6z"

/**
 * The TimeStore mark: an S3 bucket with a bolt, white and grey with a soft shadow. Colours come from the `--logo-*`
 * tokens; the bolt takes `--primary`, the colour of the tile it sits on. The static copies are `public/favicon.svg`
 * and `public/apple-touch-icon.png`.
 */
export function LogoMark({ className }: { className?: string }) {
  const clip = `logo-${useId().replace(/[^a-zA-Z0-9-]/g, "")}`
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className={cn("shrink-0 overflow-visible", className)}>
      <defs>
        <clipPath id={clip}>
          <path d={BUCKET} />
        </clipPath>
      </defs>
      <g clipPath={`url(#${clip})`}>
        <rect width="24" height="24" fill="var(--logo-shade)" />
        <ellipse cx="9.4" cy="12" rx="8.6" ry="13" fill="var(--logo-body)" />
      </g>
      <ellipse cx="12" cy="6.5" rx="8.5" ry="2.4" fill="var(--logo-rim)" />
      <ellipse cx="12" cy="6.6" rx="6.9" ry="1.45" fill="var(--logo-opening)" />
      <path d="M6.3 10.2 7.3 17" fill="none" stroke="var(--logo-rim)" strokeWidth="1" strokeLinecap="round" />
      <path d={BOLT} fill="var(--primary)" />
    </svg>
  )
}

/** The mark in its violet tile, as the sidebar and the sign-in page show it next to the name. */
export function LogoTile({ className }: { className?: string }) {
  return (
    <span
      className={cn(
        "flex size-8 shrink-0 items-center justify-center rounded-xl bg-primary shadow-[0_6px_16px_rgba(108,92,231,0.35)]",
        className
      )}
    >
      {/* About 51% of the tile's width: the bucket spans ~71% of its 24-unit grid. */}
      <LogoMark className="size-[23px]" />
    </span>
  )
}
