import { ReactQueryDevtools } from "@tanstack/react-query-devtools"
import { Outlet, createRootRouteWithContext } from "@tanstack/react-router"
import { TanStackRouterDevtools } from "@tanstack/react-router-devtools"

import { EmptyState } from "@/components/shared/query-state"
import type { RouterContext } from "@/router"

// The shell is rendered by the `_app` layout.
export const Route = createRootRouteWithContext<RouterContext>()({
  component: RootLayout,
  notFoundComponent: () => (
    <div className="p-6">
      <EmptyState title="Page not found" description="The page you opened does not exist." />
    </div>
  ),
})

function RootLayout() {
  return (
    <>
      <Outlet />
      {import.meta.env.DEV && (
        <>
          <TanStackRouterDevtools
            position="bottom-right"
            toggleButtonProps={{ style: { right: "4.5rem" } }}
          />
          <ReactQueryDevtools buttonPosition="bottom-right" />
        </>
      )}
    </>
  )
}
