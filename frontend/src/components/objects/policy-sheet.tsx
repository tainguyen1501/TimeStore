import { useState } from "react"
import { toast } from "sonner"

import { QueryState } from "@/components/shared/query-state"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Field, FieldDescription, FieldError, FieldLabel } from "@/components/ui/field"
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet"
import { Spinner } from "@/components/ui/spinner"
import { Textarea } from "@/components/ui/textarea"
import { useGetBucketPolicy, useSetBucketPolicy } from "@/hooks/api/use-policy"
import { getErrorMessage } from "@/lib/api-response"

/** A starting point: everyone may read objects under public/. */
const TEMPLATE = (bucket: string) =>
  JSON.stringify(
    {
      Version: "2012-10-17",
      Statement: [
        {
          Sid: "PublicRead",
          Effect: "Allow",
          Principal: "*",
          Action: "s3:GetObject",
          Resource: `arn:aws:s3:::${bucket}/public/*`,
        },
      ],
    },
    null,
    2
  )

/**
 * A bucket's policy (IAM JSON): who may do what. "Public" means it lets anyone, without signing, do something.
 * The root key always passes; full-scope keys pass unless a statement denies them; policy-scope keys and anonymous
 * callers only get what a statement allows.
 */
export function PolicySheet({
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
  const { data: policy, isLoading, error } = useGetBucketPolicy(bucket, open)
  const save = useSetBucketPolicy(bucket, { meta: { skipErrorToast: true } })
  const [draft, setDraft] = useState<string | null>(null)

  const text = draft ?? policy?.document ?? ""
  const close = (next: boolean) => {
    if (!next) {
      setDraft(null)
      save.reset()
    }
    onOpenChange(next)
  }

  const persist = (document: string | null) =>
    save.mutate(document, {
      onSuccess: () => {
        setDraft(null)
        toast.success(document === null ? "Bucket policy removed." : "Bucket policy saved.")
      },
    })

  return (
    <Sheet open={open} onOpenChange={close}>
      <SheetContent className="w-full gap-0 sm:max-w-xl">
        <SheetHeader>
          <SheetTitle className="flex items-center gap-2">
            Bucket policy
            {policy?.isPublic && <Badge variant="destructive">Public</Badge>}
          </SheetTitle>
          <SheetDescription>
            Who may do what in {bucket}. A Deny statement stops every access key but the root key;
            keys with the “policy” scope and unsigned (anonymous) requests only get what an Allow
            statement grants.
          </SheetDescription>
        </SheetHeader>
        <div className="flex-1 space-y-3 overflow-y-auto px-6 pb-6">
          <QueryState isLoading={isLoading} error={error} rows={3} />
          {!isLoading && !error && (
            <>
              {policy === null && draft === null && (
                <p className="text-sm text-muted-foreground">
                  No policy: every full-scope key has access, nobody else does.
                </p>
              )}
              {(policy || draft !== null) && (
                <Field data-invalid={save.error ? true : undefined}>
                  <FieldLabel htmlFor="bucket-policy">Policy document (JSON)</FieldLabel>
                  <Textarea
                    id="bucket-policy"
                    spellCheck={false}
                    readOnly={!canWrite}
                    aria-invalid={save.error ? true : undefined}
                    className="min-h-80 font-mono text-xs"
                    value={text}
                    onChange={(event) => setDraft(event.target.value)}
                  />
                  <FieldDescription>
                    Principals are access key ids or “*”; resources are arn:aws:s3:::{bucket} and
                    arn:aws:s3:::{bucket}/key-pattern. At most 20 KB.
                  </FieldDescription>
                  {save.error ? <FieldError>{getErrorMessage(save.error)}</FieldError> : null}
                </Field>
              )}
            </>
          )}
        </div>
        {canWrite && !isLoading && !error && (
          <SheetFooter className="flex-row justify-end gap-2">
            {policy === null && draft === null ? (
              <Button onClick={() => setDraft(TEMPLATE(bucket))}>Add policy</Button>
            ) : (
              <>
                {policy && (
                  <Button
                    variant="ghost"
                    className="mr-auto text-destructive"
                    disabled={save.isPending}
                    onClick={() => persist(null)}
                  >
                    Remove policy
                  </Button>
                )}
                {draft !== null && (
                  <Button
                    variant="secondary"
                    disabled={save.isPending}
                    onClick={() => {
                      setDraft(null)
                      save.reset()
                    }}
                  >
                    Cancel
                  </Button>
                )}
                <Button disabled={save.isPending || draft === null} onClick={() => persist(text)}>
                  {save.isPending && <Spinner data-icon="inline-start" />}
                  Save
                </Button>
              </>
            )}
          </SheetFooter>
        )}
      </SheetContent>
    </Sheet>
  )
}
