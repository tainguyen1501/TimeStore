---
paths:
  - "frontend/**"
---

# Frontend rules (`frontend/`)

English only, no i18n.

## Stack

| Concern | Choice |
|---|---|
| Build | Vite 8, TypeScript 6 (strict), pnpm 10.34.9 (`packageManager` field) |
| Routing | TanStack Router, file-based (`src/routes/`, generated `src/routeTree.gen.ts`) |
| Server state | TanStack Query |
| HTTP | axios instance in `src/lib/api.ts` (`baseURL: "/api/v1"`, relative) |
| UI | shadcn/ui with **Base UI** (`style: base-nova`), Tailwind v4, lucide icons, sonner |
| Forms | react-hook-form + zod for larger forms, laid out with shadcn `Field`; small dialogs may use plain state |
| Client state | zustand (`src/store/`) |
| Long lists | TanStack Virtual (`@tanstack/react-virtual`): lists that grow with the data (objects in a folder) render only the rows in view and load the next page while scrolling (`components/objects/object-table.tsx`) |

Not Radix: never add `radix-ui` / `@radix-ui/*`. Add shadcn components with `pnpm dlx shadcn@latest add <name>`;
Base UI components use `render={<Link … />}` instead of `asChild`. TypeScript and pnpm versions are pinned in `package.json`.

## Layout

```
src/
  routes/              file routes; _app.tsx = shell layout
  config/navigation.ts sidebar menu (planned modules show a "Soon" badge)
  components/ui/       shadcn components (generated, edit sparingly)
  components/layout/   app shell, theme toggle, toaster
  components/shared/   page header, icons registry, icon button, data list, confirm dialog, query/empty states
  components/<area>/   feature components (buckets/)
  services/api/        one service per resource (HTTP)
  hooks/api/           one file of TanStack Query hooks per resource + `QUERY_KEYS`
  @types/              `api.gen.ts` (generated, never edited), `common.ts` (error body alias)
  lib/                 api.ts, api-response.ts, action.ts, format.ts, theme.ts, utils.ts
```

## API types and calls

- The admin API answers resources directly (no envelope) and errors as `{ code, message, traceId }`
  (`@types/common.ts`). Read errors with `getErrorMessage` / `getErrorCode`; branch on `code`.
- Types come from the Server's OpenAPI document: `pnpm gen:api` reads the committed `docs/api/openapi.v1.json`.
  After an admin API change: run the Server tests with `TIMESTORE_UPDATE_OPENAPI=1` (rewrites the snapshot), then
  `pnpm gen:api`. Services export aliases (`type BucketResponse = components["schemas"]["BucketResponse"]`); never
  hand-write a type the document has.
- The built UI is served by the Server under a strict `Content-Security-Policy` (`AdminUi.cs`): **no inline
  `<script>`** (put it in `public/` as a file), no third-party origins; inline styles and `data:` fonts/images are
  allowed.
- **Action errors toast globally, queries never do** (`MutationCache.onError` in `main.tsx`); mutation hooks take
  `options?: ActionOptions` and pass `meta`; a page that shows the error itself (dialog field) sets
  `skipErrorToast`. Mutations invalidate the keys they affect.
- Deletes go through `ConfirmDialog` with `destructive` and `confirmText` = the resource's name.
- Pages use `ListPage` and `PageHeader`; lists `Toolbar` + `DataListCard` (tonal, sized to their rows); view switchers
  `Segmented`; file icons `FileIcon` / `FolderIcon`.
- Look and feel (`docs/research/admin-ui.md` §14): lavender
  page, rounded three-column shell, **borderless tonal surfaces** (`surface`, `raised`, `field`), violet accent,
  DM Sans / Plus Jakarta Sans (headings) / JetBrains Mono, self-hosted. Colours come only from the tokens in
  `src/index.css` (`--file-*` and `--palette-*` for file kinds and bucket tints, used through the `tint` utility);
  no hex values in components. On a dialog, sheet or the shell use `secondary` buttons; `outline` is the white chip
  for tonal toolbars.
- No mock layer: the Server runs with zero setup (`dotnet run --project Src/TimeStore.Server`), so pages always call
  it.

## Auth

- Every page but `/login` lives under the `_app` layout, whose guard loads `auth/me` (`meQuery`, never retried).
  Never add a page outside it; never store a secret or session data in the browser (the session is the server's
  `HttpOnly` cookie).
- A 401 outside `/auth` sends the user to `/login?redirect=…` (`main.tsx`); follow `redirect` only through
  `safeRedirect`.
- **Viewers see no write actions**: gate every create, edit, delete or other state-changing control with
  `useCanWrite()` (hide it, do not disable it). The server still refuses writes; the UI only avoids offering them.

## Checks

```bash
cd frontend
pnpm install
pnpm dev          # Vite on :5173, proxies /api to VITE_API_PROXY_TARGET (default http://localhost:9001)
pnpm lint && pnpm typecheck && pnpm build
pnpm exec prettier --check "src/**/*.{ts,tsx,css}"
```

`pnpm-workspace.yaml` sets `minimumReleaseAge: 1440`; do not add exclusions, pick an older version instead.
