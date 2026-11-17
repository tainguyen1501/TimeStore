// Theme before first paint (no light flash): same key and default as src/lib/theme.ts. A file, not an inline script,
// so the Server's Content-Security-Policy can stay `script-src 'self'`.
;(function () {
  var choice = "dark"
  try {
    choice = localStorage.getItem("timestore.theme") || "dark"
  } catch (e) {}
  var dark =
    choice === "dark" ||
    (choice === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches)
  document.documentElement.classList.toggle("dark", dark)
})()
