import { createFileRoute } from "@tanstack/react-router"
import { LayoutGrid, List, Monitor, Moon, Sun, Table2 } from "lucide-react"

import { ListPage } from "@/components/shared/data-list"
import { PageHeader } from "@/components/shared/page-header"
import { Segmented } from "@/components/shared/segmented"
import type { ThemeChoice } from "@/lib/theme"
import { useThemeStore } from "@/store/theme"
import { useUiStore, type ViewMode } from "@/store/ui"

export const Route = createFileRoute("/_app/settings/")({
  component: SettingsPage,
})

const VIEW_OPTIONS: { value: ViewMode; label: string; icon: React.ReactNode }[] = [
  { value: "grid", label: "Grid", icon: <LayoutGrid /> },
  { value: "list", label: "List", icon: <List /> },
  { value: "table", label: "Table", icon: <Table2 /> },
]

/** Preferences of this browser (design: "Interface preferences"). Nothing here changes the server. */
function SettingsPage() {
  const { choice, setChoice } = useThemeStore()
  const views = useUiStore((state) => state.views)
  const setView = useUiStore((state) => state.setView)

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Settings"
        description="Preferences of this browser: appearance and default layouts."
      />

      <section className="space-y-5 rounded-3xl bg-surface p-6">
        <h2 className="text-base font-bold text-foreground">Interface preferences</h2>

        <Row
          title="Appearance"
          description="Light workspace, dark mode, or follow the operating system."
        >
          <Segmented<ThemeChoice>
            label="Appearance"
            value={choice}
            onChange={setChoice}
            options={[
              { value: "light", label: "Light", icon: <Sun /> },
              { value: "dark", label: "Dark", icon: <Moon /> },
              { value: "system", label: "System", icon: <Monitor /> },
            ]}
          />
        </Row>

        <Row title="Buckets layout" description="Grid cards, compact list rows, or a table.">
          <Segmented
            label="Buckets layout"
            value={views.buckets}
            onChange={(v) => setView("buckets", v)}
            options={VIEW_OPTIONS}
          />
        </Row>

        <Row title="Objects layout" description="How a bucket's folders and objects are shown.">
          <Segmented
            label="Objects layout"
            value={views.objects}
            onChange={(v) => setView("objects", v)}
            options={VIEW_OPTIONS}
          />
        </Row>
      </section>
    </ListPage>
  )
}

function Row({
  title,
  description,
  children,
}: {
  title: string
  description: string
  children: React.ReactNode
}) {
  return (
    <div className="flex flex-col justify-between gap-4 rounded-2xl bg-raised p-4 sm:flex-row sm:items-center">
      <div>
        <div className="text-sm font-semibold text-foreground">{title}</div>
        <div className="text-xs text-muted-foreground">{description}</div>
      </div>
      {children}
    </div>
  )
}
