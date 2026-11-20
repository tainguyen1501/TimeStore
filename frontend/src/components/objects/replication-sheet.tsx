import { useState } from "react"
import { toast } from "sonner"

import { ConfirmDialog } from "@/components/shared/confirm-dialog"
import { QueryState } from "@/components/shared/query-state"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet"
import { Spinner } from "@/components/ui/spinner"
import {
  useDeleteReplication,
  useGetReplication,
  useResyncReplication,
  useRetryReplication,
  useSetReplication,
  useTestReplication,
} from "@/hooks/api/use-replication"
import { getErrorMessage } from "@/lib/api-response"
import { formatAgo } from "@/lib/format"
import type { ReplicationResponse, SetReplicationRequest } from "@/services/api/replication-service"

const DEFAULT_REGION = "us-east-1"

/** Up to date, catching up, paused (destination down) or with refused changes. */
function Health({ rule }: { rule: ReplicationResponse }) {
  const { pending, failed } = rule.backlog
  if (rule.pausedUntil) return <Badge variant="warning">Paused</Badge>
  if (failed > 0) return <Badge variant="destructive">{failed} failed</Badge>
  if (pending > 0) return <Badge variant="secondary">{pending} waiting</Badge>
  return <Badge variant="success">Up to date</Badge>
}

/**
 * A bucket's replication rule (docs/user-guide/replication.md): every change of the bucket is sent to another S3
 * endpoint within seconds. Shows what waits, what the destination refused and why; admins set it up, test it, retry
 * refused changes, send everything again (resync) or remove it.
 */
export function ReplicationSheet({
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
  const { data: rule, isLoading, error } = useGetReplication(bucket, open)
  const save = useSetReplication(bucket, { meta: { skipErrorToast: true } })
  const remove = useDeleteReplication(bucket)
  const test = useTestReplication(bucket)
  const retry = useRetryReplication(bucket)
  const resync = useResyncReplication(bucket)
  const [editing, setEditing] = useState(false)
  const [confirm, setConfirm] = useState<"remove" | "resync" | null>(null)
  const busy =
    save.isPending || remove.isPending || test.isPending || retry.isPending || resync.isPending

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent className="w-full gap-0 sm:max-w-lg">
        <SheetHeader>
          <SheetTitle>Replication</SheetTitle>
          <SheetDescription>
            Every change in {bucket} is copied to a bucket on another S3 endpoint (another
            TimeStore, MinIO, AWS S3, Cloudflare R2…) a few seconds after it happens. Clients are
            answered first; a destination that is down never fails an upload.
          </SheetDescription>
        </SheetHeader>
        <div className="flex-1 space-y-3 overflow-y-auto px-6 pb-6">
          <QueryState isLoading={isLoading} error={error} rows={3} />

          {rule === null && !editing && (
            <div className="space-y-3">
              <p className="text-sm text-muted-foreground">
                Not replicated: this bucket exists only on this server.
              </p>
              {canWrite && (
                <Button variant="secondary" onClick={() => setEditing(true)}>
                  Set up replication
                </Button>
              )}
            </div>
          )}

          {rule && !editing && <RuleStatus rule={rule} />}

          {rule && rule.backlog.failures.length > 0 && !editing && (
            <div className="space-y-2">
              <div className="text-xs font-medium text-muted-foreground">
                Refused by the destination
                {rule.backlog.failed > rule.backlog.failures.length &&
                  ` (newest ${rule.backlog.failures.length} of ${rule.backlog.failed})`}
              </div>
              {rule.backlog.failures.map((failure) => (
                <div
                  key={`${failure.key}-${failure.changedAt}`}
                  className="space-y-0.5 rounded-xl bg-surface px-3 py-2 text-xs"
                >
                  <div className="truncate font-mono">{failure.key}</div>
                  <div className="text-muted-foreground">
                    {failure.error ?? "Refused"} · {failure.operation} · {failure.attempts}{" "}
                    {failure.attempts === 1 ? "attempt" : "attempts"} ·{" "}
                    {formatAgo(failure.changedAt)}
                  </div>
                </div>
              ))}
            </div>
          )}

          {editing && (
            <RuleForm
              bucket={bucket}
              rule={rule ?? null}
              pending={save.isPending}
              error={save.error}
              onCancel={() => {
                save.reset()
                setEditing(false)
              }}
              onSave={(request) =>
                save.mutate(request, {
                  onSuccess: () => {
                    setEditing(false)
                    toast.success(`Replication of ${bucket} saved.`)
                  },
                })
              }
            />
          )}

          {rule && canWrite && !editing && (
            <div className="flex flex-wrap gap-2 pt-2">
              <Button variant="secondary" disabled={busy} onClick={() => setEditing(true)}>
                Edit
              </Button>
              <Button
                variant="ghost"
                disabled={busy}
                onClick={() =>
                  test.mutate(undefined, {
                    onSuccess: () => toast.success("The destination accepted a test object."),
                  })
                }
              >
                {test.isPending && <Spinner data-icon="inline-start" />}
                Test
              </Button>
              {(rule.backlog.failed > 0 || rule.pausedUntil) && (
                <Button
                  variant="ghost"
                  disabled={busy}
                  onClick={() =>
                    retry.mutate(undefined, {
                      onSuccess: () => toast.success("Failed changes are being sent again."),
                    })
                  }
                >
                  {retry.isPending && <Spinner data-icon="inline-start" />}
                  Retry now
                </Button>
              )}
              <Button variant="ghost" disabled={busy} onClick={() => setConfirm("resync")}>
                Send everything again
              </Button>
              <Button
                variant="ghost"
                className="text-destructive"
                disabled={busy}
                onClick={() => setConfirm("remove")}
              >
                Remove
              </Button>
            </div>
          )}
        </div>
      </SheetContent>

      <ConfirmDialog
        open={confirm === "resync"}
        onOpenChange={(next) => !next && setConfirm(null)}
        title="Send everything again?"
        description={
          <>
            Every object in {bucket}
            {rule?.prefix ? ` under ${rule.prefix}` : ""} is copied to the destination again, for a
            destination that lost data or was replaced. Objects already there are overwritten with
            the same content; nothing is deleted.
          </>
        }
        confirmLabel="Send again"
        pendingLabel="Queueing…"
        isPending={resync.isPending}
        onConfirm={() =>
          resync.mutate(undefined, {
            onSuccess: (result) => {
              setConfirm(null)
              toast.success(`${result?.backlog.pending ?? 0} objects are being sent again.`)
            },
          })
        }
      />
      <ConfirmDialog
        open={confirm === "remove"}
        onOpenChange={(next) => !next && setConfirm(null)}
        title="Remove replication?"
        subtitle="Changes that are still waiting are dropped."
        description={
          <>
            Changes in {bucket} are no longer copied. Nothing is deleted at the destination: its
            bucket keeps what it received.
          </>
        }
        confirmLabel="Remove"
        pendingLabel="Removing…"
        isPending={remove.isPending}
        destructive
        confirmText={bucket}
        onConfirm={() =>
          remove.mutate(undefined, {
            onSuccess: () => {
              setConfirm(null)
              toast.success(`Replication of ${bucket} removed.`)
            },
          })
        }
      />
    </Sheet>
  )
}

function RuleStatus({ rule }: { rule: ReplicationResponse }) {
  const { pending, oldestPendingAt } = rule.backlog
  return (
    <div className="space-y-2 rounded-2xl bg-surface p-3.5 text-sm">
      <div className="flex items-center justify-between gap-2">
        <span className="truncate font-mono text-xs" title={`${rule.endpoint}/${rule.bucket}`}>
          {rule.endpoint}/{rule.bucket}
        </span>
        <Health rule={rule} />
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
        <dt className="text-muted-foreground">Region</dt>
        <dd>{rule.region}</dd>
        <dt className="text-muted-foreground">Access key</dt>
        <dd className="truncate font-mono">{rule.accessKeyId}</dd>
        <dt className="text-muted-foreground">Keys</dt>
        <dd>{rule.prefix ? <span className="font-mono">{rule.prefix}…</span> : "Every key"}</dd>
        <dt className="text-muted-foreground">Deletes</dt>
        <dd>{rule.replicateDeletes ? "Copied" : "Not copied (the destination keeps them)"}</dd>
        <dt className="text-muted-foreground">Waiting</dt>
        <dd>
          {pending === 0
            ? "Nothing"
            : `${pending} ${pending === 1 ? "change" : "changes"}` +
              (oldestPendingAt ? `, oldest ${formatAgo(oldestPendingAt)}` : "")}
        </dd>
        <dt className="text-muted-foreground">Last sent</dt>
        <dd>{rule.lastSuccessAt ? formatAgo(rule.lastSuccessAt) : "Not yet"}</dd>
      </dl>
      {rule.lastError && (
        <p className="text-xs text-destructive">
          {rule.pausedUntil
            ? `The destination failed (${rule.lastError}); trying again ${formatIn(rule.pausedUntil)}.`
            : `Last error: ${rule.lastError}`}
        </p>
      )}
    </div>
  )
}

/** "in 30s", "in 4m" for a time ahead. */
function formatIn(iso: string, now = Date.now()) {
  const seconds = Math.max(0, Math.round((new Date(iso).getTime() - now) / 1000))
  return seconds >= 60 ? `in ${Math.ceil(seconds / 60)}m` : `in ${seconds}s`
}

function RuleForm({
  bucket,
  rule,
  pending,
  error,
  onCancel,
  onSave,
}: {
  bucket: string
  rule: ReplicationResponse | null
  pending: boolean
  error: unknown
  onCancel: () => void
  onSave: (request: SetReplicationRequest) => void
}) {
  const [draft, setDraft] = useState<SetReplicationRequest>({
    endpoint: rule?.endpoint ?? "",
    bucket: rule?.bucket ?? "",
    region: rule?.region ?? DEFAULT_REGION,
    accessKeyId: rule?.accessKeyId ?? "",
    secretKey: "",
    prefix: rule?.prefix ?? "",
    replicateDeletes: rule?.replicateDeletes ?? false,
    existingObjects: true,
  })
  const set = (patch: Partial<SetReplicationRequest>) =>
    setDraft((current) => ({ ...current, ...patch }))
  const text = (
    id: string,
    label: string,
    key: "endpoint" | "bucket" | "region" | "accessKeyId" | "secretKey" | "prefix",
    props: React.ComponentProps<typeof Input> = {}
  ) => (
    <Field>
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      <Input
        id={id}
        value={draft[key] ?? ""}
        onChange={(event) => set({ [key]: event.target.value })}
        {...props}
      />
    </Field>
  )

  return (
    <form
      className="space-y-3 rounded-2xl bg-surface p-4"
      autoComplete="off"
      onSubmit={(event) => {
        event.preventDefault()
        if (pending) return
        onSave({
          ...draft,
          endpoint: draft.endpoint.trim(),
          bucket: draft.bucket.trim(),
          region: draft.region?.trim() || null,
          accessKeyId: draft.accessKeyId.trim(),
          secretKey: draft.secretKey || null,
          prefix: draft.prefix || null,
        })
      }}
    >
      {text("replication-endpoint", "Endpoint", "endpoint", {
        className: "font-mono",
        placeholder: "https://<account>.r2.cloudflarestorage.com",
        required: true,
      })}
      <div className="grid grid-cols-2 gap-3">
        {text("replication-bucket", "Destination bucket", "bucket", {
          className: "font-mono",
          placeholder: `${bucket}-replica`,
          required: true,
        })}
        {text("replication-region", "Region", "region", {
          className: "font-mono",
          placeholder: DEFAULT_REGION,
        })}
        {text("replication-key", "Access key", "accessKeyId", {
          className: "font-mono",
          required: true,
        })}
        {text("replication-secret", "Secret key", "secretKey", {
          type: "password",
          placeholder: rule ? "unchanged" : "",
          required: !rule,
        })}
      </div>
      <Field>
        <FieldLabel htmlFor="replication-prefix">Only keys starting with</FieldLabel>
        <Input
          id="replication-prefix"
          className="font-mono"
          placeholder="every key"
          value={draft.prefix ?? ""}
          onChange={(event) => set({ prefix: event.target.value })}
        />
        <FieldDescription>
          The destination bucket must exist; the key needs to write and delete objects in it.
          Cloudflare R2 uses the region <span className="font-mono">auto</span>.
        </FieldDescription>
      </Field>
      <label className="flex items-center gap-2 text-xs">
        <Checkbox
          checked={draft.replicateDeletes}
          onCheckedChange={(checked) => set({ replicateDeletes: checked })}
        />
        Delete at the destination too (off: a mistake here is not copied)
      </label>
      <label className="flex items-center gap-2 text-xs">
        <Checkbox
          checked={draft.existingObjects}
          onCheckedChange={(checked) => set({ existingObjects: checked })}
        />
        {rule
          ? "Copy the existing objects again if the destination or prefix changes"
          : "Copy the objects already in the bucket too"}
      </label>
      {error ? <FieldError>{getErrorMessage(error)}</FieldError> : null}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={pending}>
          {pending && <Spinner data-icon="inline-start" />}
          Test and save
        </Button>
      </div>
    </form>
  )
}
