/** Theme choice: stored per browser; "system" follows the OS. The inline script in index.html applies it before React. */
export type ThemeChoice = "light" | "dark" | "system"
export type Theme = "light" | "dark"

/** Same key as the inline script in index.html. */
export const THEME_STORAGE_KEY = "timestore.theme"
export const DEFAULT_THEME: ThemeChoice = "dark"
const DARK_QUERY = "(prefers-color-scheme: dark)"

export function readThemeChoice(): ThemeChoice {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    return stored === "light" || stored === "dark" || stored === "system" ? stored : DEFAULT_THEME
  } catch {
    return DEFAULT_THEME
  }
}

export const systemTheme = (): Theme => (matchMedia(DARK_QUERY).matches ? "dark" : "light")

export const resolveTheme = (choice: ThemeChoice): Theme =>
  choice === "system" ? systemTheme() : choice

/** Puts the `dark` class (Tailwind's dark variant, the token set in index.css) on <html>. */
export function applyTheme(theme: Theme) {
  document.documentElement.classList.toggle("dark", theme === "dark")
}

/** Calls `onChange` when the OS switches between light and dark. */
export function watchSystemTheme(onChange: () => void) {
  const query = matchMedia(DARK_QUERY)
  query.addEventListener("change", onChange)
  return () => query.removeEventListener("change", onChange)
}
