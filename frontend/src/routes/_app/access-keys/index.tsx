import { useState } from "react"
import { createFileRoute } from "@tanstack/react-router"
import { Check, Copy, Plus } from "lucide-react"
import { toast } from "sonner"

import { CreateAccessKeyDialog } from "@/components/access-keys/create-access-key-dialog"
import { ConfirmDialog } from "@/components/shared/confirm-dialog"
import { ListPage } from "@/components/shared/data-list"
import { Icon } from "@/components/shared/icons"
import { PageHeader } from "@/components/shared/page-header"
import { EmptyState, QueryState } from "@/components/shared/query-state"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  useDeleteAccessKey,
  useGetAccessKeyList,
  useSetAccessKeyEnabled,
  useSetAccessKeyRole,
  useSetAccessKeyScope,
} from "@/hooks/api/use-access-keys"
import { useCanWrite, useGetMe } from "@/hooks/api/use-auth"
import { useCopy } from "@/hooks/use-copy"
import { formatAgo } from "@/lib/format"
import {
  ACCESS_KEY_ROLES,
  ACCESS_KEY_SCOPES,
  type AccessKeyResponse,
  type AccessKeyRole,
  type AccessKeyScope,
  ROLE_INFO,
  SCOPE_INFO,
} from "@/services/api/access-key-service"

export const Route = createFileRoute("/_app/access-keys/")({
  component: AccessKeysPage,
})

/** An action waiting for confirmation: deletes always, role and disable when they hit the signed-in key. */
type Pending =
  | { kind: "delete"; key: AccessKeyResponse }
  | { kind: "role"; key: AccessKeyResponse; role: AccessKeyRole }
  | { kind: "disable"; key: AccessKeyResponse }

const roleOf = (key: AccessKeyResponse) => key.role as AccessKeyRole
const scopeOf = (key: AccessKeyResponse) => (key.s3Scope ?? "full") as AccessKeyScope

/** The S3 port when the configuration does not change it (TimeStore.Server ServerOptions.DefaultS3Port). */
const DEFAULT_S3_PORT = 9000

function AccessKeysPage() {
  const { data: keys, isLoading, error } = useGetAccessKeyList()
  const { data: session } = useGetMe()
  const canWrite = useCanWrite()
  const [createOpen, setCreateOpen] = useState(false)
  const [pending, setPending] = useState<Pending | null>(null)
  const setRole = useSetAccessKeyRole()
  const setEnabled = useSetAccessKeyEnabled()
  const setScope = useSetAccessKeyScope()
  const deleteKey = useDeleteAccessKey()

  const isOwn = (key: AccessKeyResponse) => key.accessKeyId === session?.accessKeyId

  const changeRole = (key: AccessKeyResponse, role: AccessKeyRole) =>
    setRole.mutate(
      { id: key.accessKeyId, role },
      { onSuccess: () => toast.success(`${key.accessKeyId} is now ${ROLE_INFO[role].label}.`) }
    )
  const disable = (key: AccessKeyResponse) =>
    setEnabled.mutate(
      { id: key.accessKeyId, enabled: false },
      { onSuccess: () => toast.success(`${key.accessKeyId} is disabled.`) }
    )

  const requestRole = (key: AccessKeyResponse, role: AccessKeyRole) => {
    if (role === roleOf(key)) return
    if (isOwn(key)) setPending({ kind: "role", key, role })
    else changeRole(key, role)
  }
  const requestDisable = (key: AccessKeyResponse) => {
    if (isOwn(key)) setPending({ kind: "disable", key })
    else disable(key)
  }

  const confirm = () => {
    if (!pending) return
    const done = { onSuccess: () => setPending(null) }
    if (pending.kind === "delete") deleteKey.mutate(pending.key.accessKeyId, done)
    else if (pending.kind === "role")
      setRole.mutate({ id: pending.key.accessKeyId, role: pending.role }, done)
    else setEnabled.mutate({ id: pending.key.accessKeyId, enabled: false }, done)
  }

  return (
    <ListPage fill={false}>
      <PageHeader
        title="Access Keys & S3"
        description="Keys for S3 clients. The role decides whether a key may also sign in here; changing it or disabling a key ends its admin sessions at once."
        actions={
          canWrite && (
            <Button size="lg" onClick={() => setCreateOpen(true)}>
              <Plus />
              <span className="hidden sm:inline">Generate Access Key</span>
            </Button>
          )
        }
      />

      <EndpointCard />

      <QueryState isLoading={isLoading} error={error} />

      {keys && keys.length === 0 && (
        <EmptyState
          title="No access keys yet"
          description={
            canWrite
              ? "Create a key for each client. The root key from the configuration is not listed here."
              : "An admin can create keys. The root key from the configuration is not listed here."
          }
        />
      )}

      {keys && keys.length > 0 && (
        <section className="space-y-3 rounded-3xl bg-surface p-6">
          <h3 className="mb-2 text-sm font-bold text-foreground">Access keys</h3>
          {keys.map((key) => (
            <div
              key={key.accessKeyId}
              className="flex flex-col justify-between gap-3 rounded-2xl bg-raised p-4 sm:flex-row sm:items-center"
            >
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono text-xs font-bold break-all text-foreground">
                    {key.accessKeyId}
                  </span>
                  {isOwn(key) && <Badge>You</Badge>}
                </div>
                <div
                  className="mt-0.5 truncate text-[11px] text-muted-foreground"
                  title={key.description}
                >
                  {key.description || "No description"} · Created {formatAgo(key.createdAt)}
                </div>
              </div>
              <div className="flex shrink-0 flex-wrap items-center gap-2">
                <Badge
                  variant={scopeOf(key) === "full" ? "secondary" : "default"}
                  title={SCOPE_INFO[scopeOf(key)]?.description}
                >
                  {SCOPE_INFO[scopeOf(key)]?.label ?? key.s3Scope}
                </Badge>
                <Badge
                  variant={roleOf(key) === "none" ? "secondary" : "default"}
                  title={ROLE_INFO[roleOf(key)]?.description}
                >
                  {ROLE_INFO[roleOf(key)]?.label ?? key.role}
                </Badge>
                {key.disabledAt ? (
                  <Badge variant="destructive" title={`Disabled ${key.disabledAt}`}>
                    Disabled
                  </Badge>
                ) : (
                  <Badge variant="success">Active</Badge>
                )}
                {canWrite && (
                  <KeyActions
                    accessKey={key}
                    onRole={(role) => requestRole(key, role)}
                    onScope={(scope) =>
                      scope !== scopeOf(key) &&
                      setScope.mutate(
                        { id: key.accessKeyId, scope },
                        {
                          onSuccess: () =>
                            toast.success(
                              `${key.accessKeyId}: S3 access is now ${SCOPE_INFO[scope].label.toLowerCase()}.`
                            ),
                        }
                      )
                    }
                    onDisable={() => requestDisable(key)}
                    onEnable={() =>
                      setEnabled.mutate(
                        { id: key.accessKeyId, enabled: true },
                        { onSuccess: () => toast.success(`${key.accessKeyId} is enabled.`) }
                      )
                    }
                    onDelete={() => setPending({ kind: "delete", key })}
                  />
                )}
              </div>
            </div>
          ))}
        </section>
      )}

      <CreateAccessKeyDialog open={createOpen} onOpenChange={setCreateOpen} />

      <ConfirmDialog
        open={pending !== null}
        onOpenChange={(open) => !open && setPending(null)}
        {...confirmText(pending)}
        isPending={deleteKey.isPending || setRole.isPending || setEnabled.isPending}
        onConfirm={confirm}
      />
    </ListPage>
  )
}

function confirmText(pending: Pending | null) {
  if (pending?.kind === "delete")
    return {
      title: "Delete access key",
      subtitle: "This action cannot be undone.",
      description:
        "S3 clients using this key fail from their next request, and its admin sessions end. A new key gets a new id and secret.",
      confirmLabel: "Delete",
      pendingLabel: "Deleting…",
      destructive: true,
      confirmText: pending.key.accessKeyId,
    }
  if (pending?.kind === "role")
    return {
      title: "Change your own role",
      description: `You are signed in with this key. Changing its role to ${ROLE_INFO[pending.role].label} ends your session; sign in again (with the root key if this key can no longer sign in).`,
      confirmLabel: "Change role",
      pendingLabel: "Changing…",
      destructive: pending.role !== "admin",
    }
  return {
    title: "Disable your own key",
    description:
      "You are signed in with this key. Disabling it ends your session and stops its S3 access; sign in again with another admin key or the root key.",
    confirmLabel: "Disable",
    pendingLabel: "Disabling…",
    destructive: true,
  }
}

function KeyActions({
  accessKey,
  onRole,
  onScope,
  onDisable,
  onEnable,
  onDelete,
}: {
  accessKey: AccessKeyResponse
  onRole: (role: AccessKeyRole) => void
  onScope: (scope: AccessKeyScope) => void
  onDisable: () => void
  onEnable: () => void
  onDelete: () => void
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        render={
          <Button
            variant="ghost"
            size="icon-sm"
            aria-label={`Actions for ${accessKey.accessKeyId}`}
          />
        }
      >
        <Icon name="more" />
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-64">
        <DropdownMenuGroup>
          <DropdownMenuLabel>Admin UI access</DropdownMenuLabel>
          <DropdownMenuRadioGroup
            value={accessKey.role}
            onValueChange={(value) => onRole(value as AccessKeyRole)}
          >
            {ACCESS_KEY_ROLES.map((role) => (
              <DropdownMenuRadioItem key={role} value={role} closeOnClick>
                <div>
                  <div>{ROLE_INFO[role].label}</div>
                  <div className="text-xs text-muted-foreground">{ROLE_INFO[role].description}</div>
                </div>
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuGroup>
          <DropdownMenuLabel>S3 access</DropdownMenuLabel>
          <DropdownMenuRadioGroup
            value={scopeOf(accessKey)}
            onValueChange={(value) => onScope(value as AccessKeyScope)}
          >
            {ACCESS_KEY_SCOPES.map((scope) => (
              <DropdownMenuRadioItem key={scope} value={scope} closeOnClick>
                <div>
                  <div>{SCOPE_INFO[scope].label}</div>
                  <div className="text-xs text-muted-foreground">
                    {SCOPE_INFO[scope].description}
                  </div>
                </div>
              </DropdownMenuRadioItem>
            ))}
          </DropdownMenuRadioGroup>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        {accessKey.disabledAt ? (
          <DropdownMenuItem onClick={onEnable}>
            <Icon name="play" />
            Enable
          </DropdownMenuItem>
        ) : (
          <DropdownMenuItem onClick={onDisable}>
            <Icon name="pause" />
            Disable
          </DropdownMenuItem>
        )}
        <DropdownMenuItem variant="destructive" onClick={onDelete}>
          <Icon name="delete" />
          Delete
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

/**
 * Design: "S3 API Endpoint & CLI". The S3 port is a server setting the UI does not see: the command uses this host
 * and the default port, and says so.
 */
function EndpointCard() {
  const endpoint = `${window.location.protocol}//${window.location.hostname}:${DEFAULT_S3_PORT}`
  const command = `aws --endpoint-url ${endpoint} s3 ls`
  const { copied, copy } = useCopy()
  return (
    <section className="rounded-3xl bg-surface p-6">
      <div className="mb-5">
        <h2 className="text-base font-bold text-foreground">S3 API Endpoint & CLI</h2>
        <p className="mt-0.5 text-xs text-muted-foreground">
          Path-style addressing on the S3 port ({DEFAULT_S3_PORT} by default, `TimeStore:S3Port`;
          behind a proxy, the address in `TimeStore:S3:PublicUrl`). Use a key below with any S3 SDK
          or CLI.
        </p>
      </div>
      <div className="flex items-center justify-between gap-3 rounded-2xl bg-raised p-4 font-mono text-xs text-foreground">
        <span className="truncate">{command}</span>
        <button
          type="button"
          onClick={() => copy(command)}
          aria-label="Copy command"
          className="shrink-0 rounded-lg bg-field p-1.5 text-subtle-foreground"
        >
          {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5" />}
        </button>
      </div>
    </section>
  )
}
