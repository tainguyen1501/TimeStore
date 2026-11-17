import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { MutationCache, QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { RouterProvider } from "@tanstack/react-router"
import { toast } from "sonner"

import { ThemedToaster } from "@/components/layout/themed-toaster"
import { TooltipProvider } from "@/components/ui/tooltip"

import { actionErrorToast } from "@/lib/action"
import { isUnauthorized, onUnauthorizedResponse } from "@/lib/api"
import { watchSystemTheme } from "@/lib/theme"
import { createAppRouter } from "@/router"
import { AUTH_BASE_URL } from "@/services/api/auth-service"
import { useThemeStore } from "@/store/theme"
import "@/index.css"

const queryClient = new QueryClient({
  mutationCache: new MutationCache({
    // Every failed action is reported here, before the hook's and the caller's own onError (TanStack's order), so a
    // page that shows its own error says so up front with `meta.skipErrorToast` (lib/action.ts).
    onError: (error, _variables, _context, mutation) => {
      // A 401 is handled by the redirect to /login (onUnauthorizedResponse below), not by a toast.
      if (isUnauthorized(error)) return
      const message = actionErrorToast(error, mutation.options.meta)
      if (message) toast.error(message)
    },
  }),
  defaultOptions: {
    queries: { retry: 1, staleTime: 30_000 },
  },
})

const router = createAppRouter(queryClient)

// The session ended while a page was open (expired, signed out elsewhere, key disabled): back to sign-in, then here.
onUnauthorizedResponse(AUTH_BASE_URL, () => {
  const { pathname, href } = router.state.location
  if (pathname === "/login") return
  queryClient.clear()
  void router.navigate({ to: "/login", search: { redirect: href } })
})

// "System" follows the OS while the page is open.
watchSystemTheme(() => useThemeStore.getState().sync())

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <RouterProvider router={router} />
      </TooltipProvider>
      <ThemedToaster />
    </QueryClientProvider>
  </StrictMode>
)
