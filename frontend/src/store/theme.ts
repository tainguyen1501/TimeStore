import { create } from "zustand"

import {
  THEME_STORAGE_KEY,
  applyTheme,
  readThemeChoice,
  resolveTheme,
  type Theme,
  type ThemeChoice,
} from "@/lib/theme"

type ThemeState = {
  choice: ThemeChoice
  /** What is shown now ("system" resolved). */
  theme: Theme
  setChoice: (choice: ThemeChoice) => void
  /** Light ↔ dark (leaves "system"). */
  toggle: () => void
  /** Re-resolves "system" after the OS changed. */
  sync: () => void
}

const initial = readThemeChoice()

export const useThemeStore = create<ThemeState>((set, get) => ({
  choice: initial,
  theme: resolveTheme(initial),
  setChoice: (choice) => {
    try {
      localStorage.setItem(THEME_STORAGE_KEY, choice)
    } catch {
      // Storage blocked (private mode): the choice lasts for this page only.
    }
    const theme = resolveTheme(choice)
    applyTheme(theme)
    set({ choice, theme })
  },
  toggle: () => get().setChoice(get().theme === "dark" ? "light" : "dark"),
  sync: () => {
    const theme = resolveTheme(get().choice)
    applyTheme(theme)
    set({ theme })
  },
}))
