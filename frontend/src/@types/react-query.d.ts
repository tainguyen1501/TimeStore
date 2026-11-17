import "@tanstack/react-query"

import type { ActionMeta } from "@/lib/action"

// Typed `meta` on mutations: a misspelt option is a type error instead of a toast that silently still shows.
declare module "@tanstack/react-query" {
  interface Register {
    mutationMeta: ActionMeta
  }
}
