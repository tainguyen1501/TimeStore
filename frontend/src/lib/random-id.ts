/**
 * A random id for client-side bookkeeping (upload rows, not security). `crypto.randomUUID` exists only in secure
 * contexts (HTTPS or localhost); the admin UI also runs over plain HTTP on a private network, where
 * `crypto.getRandomValues` is still available.
 */
export function randomId(): string {
  if (typeof crypto.randomUUID === "function") return crypto.randomUUID()
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("")
}
