import { useState } from "react"

import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Spinner } from "@/components/ui/spinner"
import { useCreateFolder } from "@/hooks/api/use-objects"
import { getErrorMessage } from "@/lib/api-response"
import { DELIMITER } from "@/services/api/object-service"

/** Creates `prefix + name + "/"` (a zero-byte folder marker, so the empty folder stays visible). */
export function NewFolderDialog({
  bucket,
  prefix,
  open,
  onOpenChange,
}: {
  bucket: string
  prefix: string
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const create = useCreateFolder(bucket, { meta: { skipErrorToast: true } })
  const close = () => {
    create.reset()
    onOpenChange(false)
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => !create.isPending && (next ? onOpenChange(true) : close())}
    >
      <DialogContent className="sm:max-w-md">
        <NewFolderForm
          prefix={prefix}
          isPending={create.isPending}
          error={create.error}
          onCancel={close}
          onSubmit={(name) => create.mutate(prefix + name + DELIMITER, { onSuccess: close })}
        />
      </DialogContent>
    </Dialog>
  )
}

function NewFolderForm({
  prefix,
  isPending,
  error,
  onCancel,
  onSubmit,
}: {
  prefix: string
  isPending: boolean
  error: unknown
  onCancel: () => void
  onSubmit: (name: string) => void
}) {
  const [name, setName] = useState("")
  const trimmed = name.trim().replace(/^\/+|\/+$/g, "")

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (trimmed && !isPending) onSubmit(trimmed)
      }}
    >
      <div>
        <DialogTitle>New folder</DialogTitle>
        <DialogDescription className="text-xs">
          Folders are prefixes of object keys; this one stays visible while it is empty.
        </DialogDescription>
      </div>
      <Field>
        <FieldLabel htmlFor="folder-name">Name</FieldLabel>
        <Input
          id="folder-name"
          autoFocus
          autoComplete="off"
          spellCheck={false}
          className="font-mono"
          placeholder="invoices"
          value={name}
          disabled={isPending}
          onChange={(event) => setName(event.target.value)}
        />
        {error ? (
          <FieldError>{getErrorMessage(error)}</FieldError>
        ) : (
          <FieldDescription className="font-mono break-all">
            {prefix}
            {trimmed || "…"}
            {DELIMITER}
          </FieldDescription>
        )}
      </Field>
      <div className="flex items-center justify-end gap-2.5 pt-2">
        <Button type="button" variant="secondary" disabled={isPending} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={!trimmed || isPending}>
          {isPending && <Spinner data-icon="inline-start" />}
          {isPending ? "Creating…" : "Create"}
        </Button>
      </div>
    </form>
  )
}
