import { useEffect, useRef, useState } from "react"
import { toast } from "sonner"

/** How long the "copied" state (check icon) shows. */
const COPIED_MS = 1500
const UNAVAILABLE =
  "Copy is not available here (the browser allows it on HTTPS or localhost only). Select the text and copy it instead."

/**
 * Copies text to the clipboard; `copied` is true for a moment after a copy. A browser that refuses (plain http on
 * another host, permissions) gets a toast instead of a silent failure.
 */
export function useCopy() {
  const [copied, setCopied] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  useEffect(() => () => clearTimeout(timer.current), [])

  /** `onCopied` runs after a successful copy (e.g. a toast with the next step). */
  const copy = (text: string, onCopied?: () => void) => {
    if (!navigator.clipboard) {
      toast.error(UNAVAILABLE)
      return
    }
    navigator.clipboard.writeText(text).then(
      () => {
        setCopied(true)
        clearTimeout(timer.current)
        timer.current = setTimeout(() => setCopied(false), COPIED_MS)
        onCopied?.()
      },
      () => toast.error(UNAVAILABLE)
    )
  }

  return { copied, copy }
}
