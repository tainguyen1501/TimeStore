import { useRef } from "react"
import { Link, useLocation } from "@tanstack/react-router"
import { UploadCloud, X } from "lucide-react"

import { RightPanel } from "@/components/layout/right-panel"
import { LogoTile } from "@/components/shared/logo"
import { UploadPanel } from "@/components/objects/upload-panel"
import { Button } from "@/components/ui/button"
import { Sheet, SheetContent, SheetTitle } from "@/components/ui/sheet"
import { navigation, type NavItem } from "@/config/navigation"
import { useGetAccessKeyList } from "@/hooks/api/use-access-keys"
import { useCanWrite } from "@/hooks/api/use-auth"
import { useGetBucketList } from "@/hooks/api/use-buckets"
import { cn } from "@/lib/utils"
import { useUiStore } from "@/store/ui"
import { useUploadStore } from "@/store/uploads"

/**
 * The design's three-column workspace, edge to edge (no page margin, after review): navigation on the left, the page in the middle
 * (the only part that scrolls), account and storage details on the right (from `xl`). Below `lg` the navigation is a
 * drawer opened from the page header.
 */
export function AppShell({ children }: { children: React.ReactNode }) {
  const menuOpen = useUiStore((state) => state.menuOpen)
  const setMenuOpen = useUiStore((state) => state.setMenuOpen)

  return (
    <div className="min-h-svh bg-background">
      <div className="flex h-svh w-full overflow-hidden bg-background">
        <aside className="hidden w-[228px] shrink-0 flex-col justify-between bg-background p-5 lg:flex">
          <SidebarContent />
        </aside>

        <main
          data-slot="app-scroll"
          className="flex min-w-0 flex-1 flex-col overflow-y-auto bg-main"
        >
          {children}
        </main>

        <aside className="hidden w-[310px] shrink-0 flex-col justify-between gap-6 overflow-y-auto bg-background p-6 xl:flex">
          <RightPanel />
        </aside>
      </div>

      <Sheet open={menuOpen} onOpenChange={setMenuOpen}>
        <SheetContent
          side="left"
          showCloseButton={false}
          className="w-[260px] justify-between p-5 data-[side=left]:sm:max-w-[260px]"
        >
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <SidebarContent onNavigate={() => setMenuOpen(false)} />
        </SheetContent>
      </Sheet>

      <UploadPanel />
    </div>
  )
}

function SidebarContent({ onNavigate }: { onNavigate?: () => void }) {
  const { pathname } = useLocation()
  const buckets = useGetBucketList().data
  const keys = useGetAccessKeyList().data
  const counts = { buckets: buckets?.length, keys: keys?.length }
  const isActive = (to: string) => (to === "/" ? pathname === "/" : pathname.startsWith(to))

  return (
    <>
      <div>
        {/* Brand lockup */}
        <div className="flex items-center justify-between px-2 pt-1 pb-8">
          <Link to="/" onClick={onNavigate} className="flex items-center gap-2.5">
            <LogoTile />
            <span className="font-heading text-[19px] font-extrabold tracking-tight whitespace-nowrap text-foreground">
              TimeStore
            </span>
          </Link>
          {onNavigate && (
            <button
              type="button"
              aria-label="Close navigation"
              onClick={onNavigate}
              className="rounded-xl bg-field p-1.5 text-muted-foreground"
            >
              <X className="size-4" />
            </button>
          )}
        </div>

        <nav className="space-y-1" aria-label="Main">
          {navigation.map((item) => (
            <NavButton
              key={item.to}
              item={item}
              active={isActive(item.to)}
              count={item.count ? counts[item.count] : undefined}
              onNavigate={onNavigate}
            />
          ))}
        </nav>
      </div>

      <BatchUploadCard onNavigate={onNavigate} />
    </>
  )
}

function NavButton({
  item,
  active,
  count,
  onNavigate,
}: {
  item: NavItem
  active: boolean
  count?: number
  onNavigate?: () => void
}) {
  return (
    <Link
      to={item.to}
      onClick={onNavigate}
      aria-current={active ? "page" : undefined}
      className={cn(
        "relative flex w-full items-center justify-between gap-3 rounded-xl px-3.5 py-2.5 text-[13.5px] whitespace-nowrap transition-colors",
        active
          ? "bg-nav-active font-bold text-primary-text"
          : "font-medium text-sidebar-foreground hover:bg-sidebar-accent hover:text-foreground"
      )}
    >
      {active && (
        <span className="absolute top-2.5 bottom-2.5 left-0 w-[3.5px] rounded-r-full bg-primary-text" />
      )}
      <span className="flex min-w-0 items-center gap-3">
        <item.icon className="size-[18px]" />
        <span className="truncate">{item.title}</span>
      </span>
      {typeof count === "number" && (
        <span
          className={cn(
            "text-[11px] tabular-nums",
            active ? "font-bold text-primary-text" : "text-muted-foreground/70"
          )}
        >
          {count}
        </span>
      )}
    </Link>
  )
}

/**
 * Design: "Batch Upload Ready". Files go into the folder open on a bucket page; elsewhere the card leads to the
 * buckets. Admins only (viewers see no write actions).
 */
function BatchUploadCard({ onNavigate }: { onNavigate?: () => void }) {
  const canWrite = useCanWrite()
  const target = useUiStore((state) => state.uploadTarget)
  const enqueue = useUploadStore((state) => state.enqueue)
  const input = useRef<HTMLInputElement>(null)
  if (!canWrite) return null

  return (
    <div className="mt-6 rounded-3xl bg-surface p-4">
      <div className="mb-2 flex items-start justify-between gap-2">
        <div>
          <div className="text-[13px] leading-snug font-bold text-foreground">
            Batch Upload Ready
          </div>
          <p className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
            {target
              ? `Drop files on the page or browse: they go to ${target.bucket}/${target.prefix}`
              : "Open a bucket, then drop files anywhere or browse from disk"}
          </p>
        </div>
        <div className="flex size-8 shrink-0 items-center justify-center rounded-xl bg-primary-soft text-primary-text">
          <UploadCloud className="size-4" />
        </div>
      </div>
      {target ? (
        <>
          <input
            ref={input}
            type="file"
            multiple
            hidden
            onChange={(event) => {
              const files = event.target.files
              if (files?.length)
                enqueue(target.bucket, target.prefix, [...files], target.onUploaded)
              event.target.value = ""
            }}
          />
          <Button size="sm" className="mt-2 w-full" onClick={() => input.current?.click()}>
            Upload Files
          </Button>
        </>
      ) : (
        <Button
          size="sm"
          className="mt-2 w-full"
          render={<Link to="/buckets" onClick={onNavigate} />}
        >
          Choose a bucket
        </Button>
      )}
    </div>
  )
}
