import { Outlet, createFileRoute, redirect } from "@tanstack/react-router"

import { AppShell } from "@/components/layout/app-shell"
import { meQuery } from "@/hooks/api/use-auth"
import { isUnauthorized } from "@/lib/api"

// Every app page lives under this pathless layout: no page renders without a session.
export const Route = createFileRoute("/_app")({
  beforeLoad: async ({ context: { queryClient }, location }) => {
    try {
      await queryClient.ensureQueryData(meQuery)
    } catch (error) {
      if (isUnauthorized(error))
        throw redirect({ to: "/login", search: { redirect: location.href } })
      throw error
    }
  },
  component: () => (
    <AppShell>
      <Outlet />
    </AppShell>
  ),
})
