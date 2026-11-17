import type { components } from "@/@types/api.gen"

// Error body of every /api/v1 endpoint (TimeStore.Server ApiResults; backend .claude/rules/backend.md "Errors").
// Branch on `code` (stable, UPPER_SNAKE_CASE), never on `message`. `traceId` matches the server logs.
export type ApiErrorResponse = components["schemas"]["ErrorResponse"]
