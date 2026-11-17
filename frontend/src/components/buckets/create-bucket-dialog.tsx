import { useState } from "react"

import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Spinner } from "@/components/ui/spinner"
import { useCreateBucket } from "@/hooks/api/use-buckets"
import { getErrorMessage } from "@/lib/api-response"

type CreateBucketDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
}

/** Create a bucket. Name rules are checked by the Server (S3 naming rules); its message is shown inline. */
export function CreateBucketDialog({ open, onOpenChange }: CreateBucketDialogProps) {
  const create = useCreateBucket({ meta: { skipErrorToast: true } })

  // Closing (cancel, Escape, success) clears the last error, so reopening starts clean.
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
        {/* Mounted only while open: the form starts empty each time. */}
        <CreateBucketForm
          isPending={create.isPending}
          error={create.error}
          onCancel={close}
          onSubmit={(name) => create.mutate({ name }, { onSuccess: close })}
        />
      </DialogContent>
    </Dialog>
  )
}

function CreateBucketForm({
  isPending,
  error,
  onCancel,
  onSubmit,
}: {
  isPending: boolean
  error: unknown
  onCancel: () => void
  onSubmit: (name: string) => void
}) {
  const [name, setName] = useState("")
  const trimmed = name.trim()

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (trimmed && !isPending) onSubmit(trimmed)
      }}
    >
      <div>
        <DialogTitle>Create bucket</DialogTitle>
        <DialogDescription className="text-xs">
          3–63 characters: lowercase letters, digits, hyphens and dots.
        </DialogDescription>
      </div>

      <Field>
        <FieldLabel htmlFor="bucket-name">Name</FieldLabel>
        <Input
          id="bucket-name"
          autoFocus
          autoComplete="off"
          spellCheck={false}
          className="font-mono"
          placeholder="photos"
          value={name}
          disabled={isPending}
          onChange={(event) => setName(event.target.value)}
        />
        {error ? (
          <FieldError>{getErrorMessage(error)}</FieldError>
        ) : (
          <FieldDescription>Bucket names are unique on this node.</FieldDescription>
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
