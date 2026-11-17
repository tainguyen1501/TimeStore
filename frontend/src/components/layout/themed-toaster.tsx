import { Toaster } from "sonner"

import { useThemeStore } from "@/store/theme"

/** Toasts in the current theme. */
export function ThemedToaster() {
  const theme = useThemeStore((s) => s.theme)
  return <Toaster position="top-right" richColors theme={theme} />
}
