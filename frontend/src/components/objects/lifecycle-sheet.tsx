import { useState } from "react"
import { toast } from "sonner"

import { IconButton } from "@/components/shared/icon-button"
import { QueryState } from "@/components/shared/query-state"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Field, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet"
import { Spinner } from "@/components/ui/spinner"
import { useGetLifecycle, useRunLifecycle, useSetLifecycle } from "@/hooks/api/use-lifecycle"
import { getErrorMessage } from "@/lib/api-response"
import type { LifecycleRuleDto } from "@/services/api/lifecycle-service"

const EMPTY_RULE: LifecycleRuleDto = {
  id: "",
  enabled: true,
  prefix: "",
  sizeGreaterThan: null,
  sizeLessThan: null,
  expirationDays: null,
  expirationDate: null,
  expiredObjectDeleteMarker: false,
  noncurrentDays: null,
  newerNoncurrentVersions: null,
  abortIncompleteUploadDays: null,
}

/** "Delete after 30 days · versions after 7 days (keep 2) · uploads after 3 days" */
function describe(rule: LifecycleRuleDto) {
  const parts: string[] = []
  if (rule.expirationDays) parts.push(`objects after ${rule.expirationDays} days`)
  if (rule.expirationDate) parts.push(`objects from ${rule.expirationDate.slice(0, 10)}`)
  if (rule.expiredObjectDeleteMarker) parts.push("lone delete markers")
  if (rule.noncurrentDays)
    parts.push(
      `old versions after ${rule.noncurrentDays} days` +
        (rule.newerNoncurrentVersions ? ` (keep ${rule.newerNoncurrentVersions})` : "")
    )
  if (rule.abortIncompleteUploadDays)
    parts.push(`unfinished uploads after ${rule.abortIncompleteUploadDays} days`)
  return parts.join(" · ") || "no action"
}

const numberOrNull = (text: string) => (text.trim() === "" ? null : Number(text))

/**
 * A bucket's lifecycle rules: what expires when (in whole days, at midnight UTC). Admins edit them and can apply
 * them at once; the server also applies them every hour.
 */
export function LifecycleSheet({
  bucket,
  open,
  canWrite,
  onOpenChange,
}: {
  bucket: string
  open: boolean
  canWrite: boolean
  onOpenChange: (open: boolean) => void
}) {
  const { data: rules, isLoading, error } = useGetLifecycle(bucket, open)
  const save = useSetLifecycle(bucket, { meta: { skipErrorToast: true } })
  const run = useRunLifecycle()
  const [editing, setEditing] = useState<{ index: number | null; rule: LifecycleRuleDto } | null>(
    null
  )

  const persist = (next: LifecycleRuleDto[]) =>
    save.mutate(next, {
      onSuccess: () => {
        setEditing(null)
        toast.success("Lifecycle rules saved.")
      },
    })

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full gap-0 sm:max-w-lg">
        <SheetHeader>
          <SheetTitle>Lifecycle rules</SheetTitle>
          <SheetDescription>
            Objects, old versions and unfinished uploads in {bucket} are removed once they are old
            enough. Days end at midnight UTC; the server applies the rules every hour.
          </SheetDescription>
        </SheetHeader>
        <div className="flex-1 space-y-3 overflow-y-auto px-6 pb-6">
          <QueryState isLoading={isLoading} error={error} rows={2} />
          {rules && rules.length === 0 && !editing && (
            <p className="text-sm text-muted-foreground">
              No rules: nothing is removed automatically.
            </p>
          )}
          {rules?.map((rule, index) => (
            <div
              key={rule.id ?? index}
              className="flex items-start gap-2 rounded-2xl bg-surface p-3.5 text-sm"
            >
              <div className="min-w-0 flex-1 space-y-1">
                <div className="flex items-center gap-2">
                  <span className="truncate font-mono text-xs">{rule.id}</span>
                  {!rule.enabled && <Badge variant="outline">Disabled</Badge>}
                </div>
                <div className="text-xs text-muted-foreground">
                  {rule.prefix ? `Keys starting with ${rule.prefix}` : "Every key"}:{" "}
                  {describe(rule)}
                </div>
              </div>
              {canWrite && (
                <>
                  <IconButton
                    icon="edit"
                    label={`Edit rule ${rule.id}`}
                    onClick={() => setEditing({ index, rule })}
                  />
                  <IconButton
                    icon="delete"
                    tone="danger"
                    label={`Delete rule ${rule.id}`}
                    disabled={save.isPending}
                    onClick={() => persist(rules.filter((_, i) => i !== index))}
                  />
                </>
              )}
            </div>
          ))}

          {editing && (
            <RuleForm
              rule={editing.rule}
              pending={save.isPending}
              error={save.error}
              onCancel={() => {
                save.reset()
                setEditing(null)
              }}
              onSave={(rule) => {
                const next = [...(rules ?? [])]
                if (editing.index === null) next.push(rule)
                else next[editing.index] = rule
                persist(next)
              }}
            />
          )}

          {canWrite && !editing && (
            <div className="flex flex-wrap gap-2 pt-2">
              <Button
                variant="secondary"
                onClick={() => setEditing({ index: null, rule: EMPTY_RULE })}
              >
                Add rule
              </Button>
              <Button
                variant="ghost"
                disabled={run.isPending}
                onClick={() =>
                  run.mutate(undefined, {
                    onSuccess: (result) =>
                      toast.success(
                        `Lifecycle applied: ${result.expired} expired, ${result.noncurrentRemoved} old versions, ` +
                          `${result.deleteMarkersRemoved} delete markers, ${result.uploadsAborted} uploads.`
                      ),
                  })
                }
              >
                {run.isPending && <Spinner data-icon="inline-start" />}
                Run now
              </Button>
            </div>
          )}
        </div>
      </SheetContent>
    </Sheet>
  )
}

function RuleForm({
  rule,
  pending,
  error,
  onCancel,
  onSave,
}: {
  rule: LifecycleRuleDto
  pending: boolean
  error: unknown
  onCancel: () => void
  onSave: (rule: LifecycleRuleDto) => void
}) {
  const [draft, setDraft] = useState(rule)
  const set = (patch: Partial<LifecycleRuleDto>) =>
    setDraft((current) => ({ ...current, ...patch }))
  const numberField = (label: string, key: keyof LifecycleRuleDto, id: string) => (
    <Field>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      <Input
        id={id}
        type="number"
        min={1}
        value={(draft[key] as number | null) ?? ""}
        onChange={(event) =>
          set({ [key]: numberOrNull(event.target.value) } as Partial<LifecycleRuleDto>)
        }
      />
    </Field>
  )

  return (
    <form
      className="space-y-3 rounded-2xl bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (!pending)
          onSave({ ...draft, id: draft.id?.trim() || null, prefix: draft.prefix || null })
      }}
    >
      <div className="grid grid-cols-2 gap-3">
        <Field>
          <FieldLabel htmlFor="rule-id">Name</FieldLabel>
          <Input
            id="rule-id"
            placeholder="generated if empty"
            value={draft.id ?? ""}
            onChange={(event) => set({ id: event.target.value })}
          />
        </Field>
        <Field>
          <FieldLabel htmlFor="rule-prefix">Key prefix</FieldLabel>
          <Input
            id="rule-prefix"
            className="font-mono"
            placeholder="logs/"
            value={draft.prefix ?? ""}
            onChange={(event) => set({ prefix: event.target.value })}
          />
        </Field>
        {numberField("Delete objects after (days)", "expirationDays", "rule-days")}
        {numberField("Delete old versions after (days)", "noncurrentDays", "rule-noncurrent")}
        {numberField("Keep newest old versions", "newerNoncurrentVersions", "rule-keep")}
        {numberField(
          "Abort unfinished uploads after (days)",
          "abortIncompleteUploadDays",
          "rule-abort"
        )}
      </div>
      <label className="flex items-center gap-2 text-xs">
        <Checkbox
          checked={draft.expiredObjectDeleteMarker}
          onCheckedChange={(checked) => set({ expiredObjectDeleteMarker: checked })}
        />
        Remove delete markers that have no versions left
      </label>
      <label className="flex items-center gap-2 text-xs">
        <Checkbox
          checked={draft.enabled}
          onCheckedChange={(checked) => set({ enabled: checked })}
        />
        Enabled
      </label>
      {error ? <FieldError>{getErrorMessage(error)}</FieldError> : null}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={pending}>
          {pending && <Spinner data-icon="inline-start" />}
          Save rule
        </Button>
      </div>
    </form>
  )
}
