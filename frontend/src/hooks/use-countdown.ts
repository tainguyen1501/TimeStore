import { useEffect, useState } from "react"

/**
 * Seconds left until a moment, ticking once a second; 0 when there is none or it has passed. `start(seconds)` sets
 * the moment, e.g. from a `Retry-After` header.
 */
export function useCountdown() {
  const [until, setUntil] = useState<number | null>(null)
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    if (until === null) return
    const timer = window.setInterval(() => {
      const current = Date.now()
      setNow(current)
      if (current >= until) setUntil(null)
    }, 1000)
    return () => window.clearInterval(timer)
  }, [until])

  const remaining = until === null ? 0 : Math.max(0, Math.ceil((until - now) / 1000))
  const start = (seconds: number) => {
    const current = Date.now()
    setNow(current)
    setUntil(current + seconds * 1000)
  }
  return { remaining, start }
}
