import type { LucideIcon } from "lucide-react"

import { LogoTile } from "@/components/shared/logo"

/**
 * Full-page frame of /login in the app's style: the lavender page, the brand lockup, one borderless rounded card.
 * Colours are theme tokens.
 */
export function AuthLayout({
  icon: Icon,
  title,
  description,
  children,
}: {
  icon: LucideIcon
  title: string
  description: string
  children: React.ReactNode
}) {
  return (
    <div className="flex min-h-svh w-full flex-col items-center justify-between bg-page p-5 text-foreground">
      <header className="flex w-full items-center justify-between px-2 py-2">
        <div className="flex items-center gap-2.5">
          <LogoTile />
          <span className="font-heading text-[19px] font-extrabold tracking-tight">TimeStore</span>
        </div>
        <span className="inline-flex items-center gap-1.5 rounded-full bg-background px-3 py-1.5 text-xs font-medium text-muted-foreground">
          <span className="size-2 animate-pulse rounded-full bg-success" />
          S3-compatible storage
        </span>
      </header>

      <main className="flex w-full flex-1 items-center justify-center py-8">
        <div className="w-full max-w-md rounded-3xl bg-background p-8 shadow-[0_24px_80px_rgba(24,26,36,0.08)] dark:shadow-none">
          <div className="mb-7 text-center">
            <div className="mx-auto mb-4 flex size-14 items-center justify-center rounded-2xl bg-primary-soft text-primary-text">
              <Icon className="size-7" />
            </div>
            <h1 className="text-[22px] font-extrabold tracking-tight">{title}</h1>
            <p className="mt-1 text-xs text-muted-foreground">{description}</p>
          </div>
          {children}
        </div>
      </main>

      <footer className="text-[11px] text-muted-foreground">TimeStore · self-hosted</footer>
    </div>
  )
}

/** Error box above the form; shows the Server's error message. */
export function AuthError({ message }: { message: string | null }) {
  if (!message) return null
  return (
    <div
      role="alert"
      className="mb-4 rounded-2xl bg-destructive-soft p-3 text-xs font-medium text-destructive"
    >
      {message}
    </div>
  )
}
