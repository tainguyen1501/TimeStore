/**
 * Where to go after signing in. Only same-origin relative paths are followed ("/buckets?x=1"); anything else
 * ("https://evil.com", "//evil.com", "/\\evil.com", non-strings) falls back to "/" so the sign-in page cannot be used
 * as an open redirect.
 */
export function safeRedirect(value: unknown): string {
  if (typeof value !== "string") return "/"
  if (!value.startsWith("/") || value.startsWith("//") || value.startsWith("/\\")) return "/"
  return value
}
