import { useState } from "react"
import { Check, Copy, Globe, Lock } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Spinner } from "@/components/ui/spinner"
import { useCanWrite } from "@/hooks/api/use-auth"
import { useGetObjectAccess, useShareObject } from "@/hooks/api/use-objects"
import { useCopy } from "@/hooks/use-copy"
import { getErrorMessage } from "@/lib/api-response"

const EXPIRY_ITEMS = [
  { value: "3600", label: "1 hour" },
  { value: "86400", label: "1 day" },
  { value: "604800", label: "7 days" },
]

/**
 * How the object is reached outside the admin UI (design: "Presigned Link" card): its S3 URL with the public state,
 * and for admins a share link (presigned URL, signed with the signed-in key) that expires.
 */
export function ObjectLinks({
  bucket,
  objectKey,
  versionId,
  onEditPolicy,
}: {
  bucket: string
  objectKey: string
  versionId?: string
  onEditPolicy?: () => void
}) {
  const { data: access, isLoading } = useGetObjectAccess(bucket, objectKey, versionId)
  const canWrite = useCanWrite()
  const share = useShareObject(bucket, { meta: { skipErrorToast: true } })
  const [expiry, setExpiry] = useState(EXPIRY_ITEMS[0].value)

  return (
    <>
      <div className="space-y-2.5 rounded-2xl bg-surface p-3.5">
        <div className="flex items-center justify-between">
          <span className="font-semibold text-foreground">S3 URL</span>
          {access &&
            (access.isPublic ? (
              <Badge variant="destructive">
                <Globe />
                Public
              </Badge>
            ) : (
              <Badge variant="outline" className="bg-raised">
                <Lock />
                Private
              </Badge>
            ))}
        </div>
        {isLoading && <Skeleton className="h-8 rounded-xl" />}
        {access && <CopyChip value={access.url} label="S3 URL" />}
        {access && (
          <p className="text-[11px] leading-relaxed text-muted-foreground">
            {access.isPublic
              ? "Anyone with this URL can open the object: the bucket policy allows it."
              : "Needs a signature: share a link, or make the bucket public."}{" "}
            {onEditPolicy && (
              <button
                type="button"
                className="font-semibold text-primary-text hover:underline"
                onClick={onEditPolicy}
              >
                Bucket policy
              </button>
            )}
          </p>
        )}
      </div>

      {canWrite && (
        <div className="space-y-2.5 rounded-2xl bg-surface p-3.5">
          <div className="flex items-center justify-between gap-2">
            <span className="font-semibold text-foreground">Share Link</span>
            {share.data ? (
              <Button
                variant="secondary"
                size="xs"
                className="bg-raised"
                onClick={() => share.reset()}
              >
                New link
              </Button>
            ) : (
              <div className="flex items-center gap-1.5">
                <Select
                  items={EXPIRY_ITEMS}
                  value={expiry}
                  onValueChange={(value) => value && setExpiry(value)}
                  disabled={share.isPending}
                >
                  <SelectTrigger
                    size="sm"
                    aria-label="Link expires after"
                    className="h-7 rounded-lg bg-raised px-2 text-[11px]"
                  >
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {EXPIRY_ITEMS.map((item) => (
                      <SelectItem key={item.value} value={item.value}>
                        {item.label}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Button
                  size="xs"
                  disabled={share.isPending}
                  onClick={() =>
                    share.mutate({ key: objectKey, versionId, seconds: Number(expiry) })
                  }
                >
                  {share.isPending && <Spinner />}
                  Create
                </Button>
              </div>
            )}
          </div>
          {share.data ? (
            <>
              <CopyChip value={share.data.url} label="share link" />
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                Works without an account until {new Date(share.data.expiresAt).toLocaleString()}.
                Signed with <span className="font-mono">{share.data.signedBy}</span>: disabling that
                key revokes it. Not stored; copy it now.
              </p>
            </>
          ) : share.error ? (
            <p className="text-[11px] text-destructive">{getErrorMessage(share.error)}</p>
          ) : (
            <p className="text-[11px] leading-relaxed text-muted-foreground">
              A presigned link anyone can open until it expires, even while the bucket is private.
            </p>
          )}
        </div>
      )}
    </>
  )
}

/** Design: the raised chip holding a link; a click copies it. */
function CopyChip({ value, label }: { value: string; label: string }) {
  const { copied, copy } = useCopy()
  return (
    <button
      type="button"
      onClick={() => copy(value)}
      aria-label={`Copy ${label}`}
      title={value}
      className="flex w-full items-center justify-between gap-2 rounded-xl bg-raised px-3 py-2 text-[11px] font-medium text-subtle-foreground transition-colors hover:text-primary-text"
    >
      <span className="truncate font-mono" aria-label={label}>
        {value}
      </span>
      {copied ? (
        <Check className="size-3.5 shrink-0 text-success" />
      ) : (
        <Copy className="size-3.5 shrink-0" />
      )}
    </button>
  )
}
