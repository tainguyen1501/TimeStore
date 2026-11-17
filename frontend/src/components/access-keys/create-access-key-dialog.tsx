import { useState } from "react"

import { IconButton } from "@/components/shared/icon-button"
import { Icon } from "@/components/shared/icons"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Spinner } from "@/components/ui/spinner"
import { useCreateAccessKey } from "@/hooks/api/use-access-keys"
import { useCopy } from "@/hooks/use-copy"
import { getErrorMessage } from "@/lib/api-response"
import {
  ACCESS_KEY_ROLES,
  ACCESS_KEY_SCOPES,
  type AccessKeyRole,
  type AccessKeyScope,
  type CreatedAccessKeyResponse,
  DESCRIPTION_MAX_LENGTH,
  ROLE_INFO,
  SCOPE_INFO,
} from "@/services/api/access-key-service"

const ROLE_ITEMS = ACCESS_KEY_ROLES.map((role) => ({ value: role, label: ROLE_INFO[role].label }))
const SCOPE_ITEMS = ACCESS_KEY_SCOPES.map((scope) => ({
  value: scope,
  label: SCOPE_INFO[scope].label,
}))

/**
 * Create an access key, then show its secret once. The secret lives only in the mutation's state while the dialog
 * is open; closing resets the mutation, so it is gone from the page.
 */
export function CreateAccessKeyDialog({
  open,
  onOpenChange,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const create = useCreateAccessKey({ meta: { skipErrorToast: true } })

  const close = () => {
    create.reset()
    onOpenChange(false)
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => !create.isPending && (next ? onOpenChange(true) : close())}
    >
      <DialogContent className="sm:max-w-lg">
        {create.data ? (
          <CreatedKey created={create.data} onDone={close} />
        ) : (
          <CreateForm
            isPending={create.isPending}
            error={create.error}
            onCancel={close}
            onSubmit={(description, role, s3Scope) => create.mutate({ description, role, s3Scope })}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

function CreateForm({
  isPending,
  error,
  onCancel,
  onSubmit,
}: {
  isPending: boolean
  error: unknown
  onCancel: () => void
  onSubmit: (description: string, role: AccessKeyRole, scope: AccessKeyScope) => void
}) {
  const [description, setDescription] = useState("")
  const [role, setRole] = useState<AccessKeyRole>("none")
  const [scope, setScope] = useState<AccessKeyScope>("full")

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (!isPending) onSubmit(description.trim(), role, scope)
      }}
    >
      <div>
        <DialogTitle>Create access key</DialogTitle>
        <DialogDescription className="text-xs">
          The server generates the key and its secret. The secret is shown once, right after this
          step.
        </DialogDescription>
      </div>

      <Field>
        <FieldLabel htmlFor="key-description">Description</FieldLabel>
        <Input
          id="key-description"
          autoFocus
          autoComplete="off"
          placeholder="backup job on nas-01"
          maxLength={DESCRIPTION_MAX_LENGTH}
          value={description}
          disabled={isPending}
          onChange={(event) => setDescription(event.target.value)}
        />
        <FieldDescription>Optional: who or what uses the key.</FieldDescription>
      </Field>

      <Field>
        <FieldLabel htmlFor="key-scope">S3 access</FieldLabel>
        <Select
          items={SCOPE_ITEMS}
          value={scope}
          onValueChange={(value) => value && setScope(value as AccessKeyScope)}
          disabled={isPending}
        >
          <SelectTrigger id="key-scope" className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {SCOPE_ITEMS.map((item) => (
              <SelectItem key={item.value} value={item.value}>
                {item.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <FieldDescription>{SCOPE_INFO[scope].description}</FieldDescription>
      </Field>

      <Field>
        <FieldLabel htmlFor="key-role">Admin UI access</FieldLabel>
        <Select
          items={ROLE_ITEMS}
          value={role}
          onValueChange={(value) => value && setRole(value as AccessKeyRole)}
          disabled={isPending}
        >
          <SelectTrigger id="key-role" className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {ROLE_ITEMS.map((item) => (
              <SelectItem key={item.value} value={item.value}>
                {item.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {error ? (
          <FieldError>{getErrorMessage(error)}</FieldError>
        ) : (
          <FieldDescription>{ROLE_INFO[role].description}</FieldDescription>
        )}
      </Field>

      <div className="flex items-center justify-end gap-2.5 pt-2">
        <Button type="button" variant="secondary" disabled={isPending} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={isPending}>
          {isPending && <Spinner data-icon="inline-start" />}
          {isPending ? "Creating…" : "Create"}
        </Button>
      </div>
    </form>
  )
}

function CreatedKey({
  created,
  onDone,
}: {
  created: CreatedAccessKeyResponse
  onDone: () => void
}) {
  const snippet = `export AWS_ACCESS_KEY_ID=${created.accessKeyId}\nexport AWS_SECRET_ACCESS_KEY=${created.secretKey}`

  // min-w-0: the long snippet line scrolls inside its box instead of widening the dialog.
  return (
    <div className="min-w-0 space-y-4">
      <div>
        <DialogTitle>Access key created</DialogTitle>
        <DialogDescription className="text-xs">
          Copy the secret now. It is stored encrypted and cannot be shown again; a lost secret means
          a new key.
        </DialogDescription>
      </div>

      <CopyField label="Access key" value={created.accessKeyId} />
      <CopyField label="Secret key" value={created.secretKey} secret />
      <CopyField label="Environment for S3 clients" value={snippet} secret multiline />

      <div className="flex items-center gap-2 rounded-2xl bg-warning-soft p-3 text-xs text-warning">
        <Icon name="warning" className="size-4 shrink-0" />
        After you close this dialog the secret is gone from this page.
      </div>

      <div className="flex justify-end pt-2">
        <Button onClick={onDone}>I have saved the secret</Button>
      </div>
    </div>
  )
}

function CopyField({
  label,
  value,
  secret = false,
  multiline = false,
}: {
  label: string
  value: string
  secret?: boolean
  multiline?: boolean
}) {
  const { copied, copy } = useCopy()
  const [visible, setVisible] = useState(!secret)

  return (
    <Field className="min-w-0">
      <FieldLabel>{label}</FieldLabel>
      <div className="flex items-start gap-1.5">
        <pre
          aria-label={label}
          className="min-w-0 flex-1 overflow-x-auto rounded-xl bg-field px-3 py-2 font-mono text-xs whitespace-pre"
        >
          {visible
            ? value
            : value
                .split("\n")
                .map((line) => "•".repeat(Math.min(line.length, 40)))
                .join("\n")}
        </pre>
        {secret && (
          <IconButton
            icon={visible ? "hide" : "show"}
            label={visible ? `Hide ${label.toLowerCase()}` : `Show ${label.toLowerCase()}`}
            onClick={() => setVisible((v) => !v)}
          />
        )}
        <IconButton
          icon={copied ? "copied" : "copy"}
          label={`Copy ${label.toLowerCase()}`}
          onClick={() => copy(value)}
        />
      </div>
      {multiline && (
        <FieldDescription>Paste into a shell for aws, rclone or an SDK.</FieldDescription>
      )}
    </Field>
  )
}
