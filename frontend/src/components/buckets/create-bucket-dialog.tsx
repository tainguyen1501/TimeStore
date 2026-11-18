import { useState } from "react"

import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Spinner } from "@/components/ui/spinner"
import { useCreateBucket } from "@/hooks/api/use-buckets"
import { getErrorMessage } from "@/lib/api-response"
import { BUCKET_NAME_MAX, bucketNameProblem, normalizeBucketName } from "@/lib/bucket-name"

type CreateBucketDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
}

/**
 * Create a bucket. The name is normalized while typing (lowercase, no accents, spaces → hyphens) and checked against
 * the S3 naming rules before it is sent; the Server checks again, and its message is shown inline.
 */
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
  // An input method (Vietnamese Telex, Chinese, ...) composes characters in place: normalize when it is done.
  const [composing, setComposing] = useState(false)
  const problem = name ? bucketNameProblem(name) : null
  const valid = name !== "" && problem === null

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (valid && !isPending) onSubmit(name)
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
          maxLength={BUCKET_NAME_MAX}
          aria-invalid={problem !== null || Boolean(error)}
          value={name}
          disabled={isPending}
          onCompositionStart={() => setComposing(true)}
          onCompositionEnd={(event) => {
            setComposing(false)
            setName(normalizeBucketName(event.currentTarget.value))
          }}
          onChange={(event) =>
            setName(composing ? event.target.value : normalizeBucketName(event.target.value))
          }
        />
        {error ? (
          <FieldError>{getErrorMessage(error)}</FieldError>
        ) : problem && !composing ? (
          <FieldError>{problem}</FieldError>
        ) : (
          <FieldDescription>
            Spaces become hyphens and letters lowercase. Bucket names are unique on this node.
          </FieldDescription>
        )}
      </Field>

      <div className="flex items-center justify-end gap-2.5 pt-2">
        <Button type="button" variant="secondary" disabled={isPending} onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={!valid || isPending}>
          {isPending && <Spinner data-icon="inline-start" />}
          {isPending ? "Creating…" : "Create"}
        </Button>
      </div>
    </form>
  )
}
