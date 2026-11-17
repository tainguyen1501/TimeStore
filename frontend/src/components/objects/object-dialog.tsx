import { Download, Trash2, X } from "lucide-react"

import { ObjectLinks } from "@/components/objects/object-links"
import { ObjectViewport } from "@/components/objects/object-preview"
import { QueryState } from "@/components/shared/query-state"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { useGetObjectDetails } from "@/hooks/api/use-objects"
import { extensionOf } from "@/lib/file-kind"
import { formatBytes, formatShortDate } from "@/lib/format"
import { objectService } from "@/services/api/object-service"

/**
 * Design: file preview modal — the preview on the left, the object inspector on the right (details, content type,
 * ETag and checksum, links, headers and metadata, download and delete).
 */
export function ObjectDialog({
  bucket,
  objectKey,
  versionId,
  canWrite,
  onClose,
  onDelete,
  onEditPolicy,
}: {
  bucket: string
  objectKey: string | null
  /** A specific version; the latest when absent. */
  versionId?: string
  canWrite: boolean
  onClose: () => void
  /** Asks to delete the object (the page confirms). Not offered for a specific version. */
  onDelete?: (key: string) => void
  /** Opens the bucket policy (from the links card). */
  onEditPolicy?: () => void
}) {
  const { data, isLoading, error } = useGetObjectDetails(bucket, objectKey, versionId)
  const name = objectKey ? objectKey.slice(objectKey.lastIndexOf("/") + 1) || objectKey : ""
  const url = objectKey ? objectService.downloadUrl(bucket, objectKey, versionId) : ""
  const close = (
    <Button
      variant="secondary"
      size="icon"
      aria-label="Close preview"
      className="lg:hidden"
      onClick={onClose}
    >
      <X />
    </Button>
  )

  return (
    <Dialog open={objectKey !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        showCloseButton={false}
        className="flex max-h-[90vh] w-full flex-col gap-0 overflow-hidden p-0 sm:max-w-5xl lg:flex-row"
      >
        <DialogTitle className="sr-only">{objectKey}</DialogTitle>
        <DialogDescription className="sr-only">Object in {bucket}</DialogDescription>
        {data ? (
          <ObjectViewport
            url={url}
            name={name}
            path={`${bucket} / ${data.key}`}
            contentType={data.contentType}
            size={data.size}
            onClose={close}
          />
        ) : (
          <div className="flex min-h-[340px] flex-1 items-center bg-surface p-6">
            <div className="w-full">
              <QueryState isLoading={isLoading} error={error} rows={3} />
            </div>
          </div>
        )}

        <aside className="flex w-full shrink-0 flex-col justify-between gap-6 overflow-y-auto bg-background p-6 lg:w-[300px]">
          <div>
            <div className="mb-6 flex items-center justify-between">
              <span className="text-sm font-bold text-foreground">Object Details</span>
              <Button
                variant="secondary"
                size="icon-sm"
                aria-label="Close preview"
                className="hidden lg:flex"
                onClick={onClose}
              >
                <X />
              </Button>
            </div>
            {data && (
              <div className="space-y-4 text-xs">
                <Facts
                  rows={[
                    ["Bucket", bucket],
                    ["Size", formatBytes(data.size), `${data.size.toLocaleString()} bytes`],
                    [
                      "Extension",
                      extensionOf(data.key) ? `.${extensionOf(data.key)}` : "—",
                      undefined,
                      "font-mono text-primary-text uppercase",
                    ],
                    ["Version", data.versionId ?? "—", undefined, "font-mono"],
                    [
                      "Modified",
                      formatShortDate(data.lastModified),
                      new Date(data.lastModified).toLocaleString(),
                    ],
                    ...(data.partsCount ? ([["Parts", String(data.partsCount)]] as Fact[]) : []),
                  ]}
                />
                <div className="space-y-2 rounded-2xl bg-surface p-3.5">
                  <div className="text-muted-foreground">Content-Type</div>
                  <div className="font-mono text-[11px] break-all text-foreground">
                    {data.contentType}
                  </div>
                  <div className="pt-1 text-muted-foreground">ETag</div>
                  <div className="font-mono text-[11px] break-all text-foreground">{data.eTag}</div>
                  {data.checksumAlgorithm && (
                    <>
                      <div className="pt-1 text-muted-foreground">
                        Checksum ({data.checksumAlgorithm}, {data.checksumType})
                      </div>
                      <div className="font-mono text-[11px] break-all text-foreground">
                        {data.checksum}
                      </div>
                    </>
                  )}
                </div>
                <ObjectLinks
                  bucket={bucket}
                  objectKey={data.key}
                  versionId={versionId}
                  onEditPolicy={onEditPolicy}
                />
                {(data.headers.length > 0 || data.userMetadata.length > 0) && (
                  <div className="space-y-2 rounded-2xl bg-surface p-3.5">
                    {data.headers.length > 0 && (
                      <Pairs title="System headers" entries={data.headers} />
                    )}
                    {data.userMetadata.length > 0 && (
                      <Pairs
                        title="User metadata"
                        entries={data.userMetadata}
                        prefix="x-amz-meta-"
                      />
                    )}
                  </div>
                )}
              </div>
            )}
          </div>

          {data && (
            <div className="space-y-2.5">
              <Button size="lg" className="w-full rounded-xl" render={<a href={url} download />}>
                <Download />
                Download File
              </Button>
              {canWrite && onDelete && !versionId && (
                <Button
                  variant="destructive"
                  size="lg"
                  className="w-full rounded-xl"
                  onClick={() => onDelete(data.key)}
                >
                  <Trash2 />
                  Delete Object
                </Button>
              )}
            </div>
          )}
        </aside>
      </DialogContent>
    </Dialog>
  )
}

type Fact = [label: string, value: string, title?: string, className?: string]

function Facts({ rows }: { rows: Fact[] }) {
  return (
    <div className="space-y-2.5 rounded-2xl bg-surface p-3.5">
      {rows.map(([label, value, title, className]) => (
        <div key={label} className="flex justify-between gap-2">
          <span className="text-muted-foreground">{label}</span>
          <span
            className={
              "truncate text-right font-semibold text-foreground tabular-nums " + (className ?? "")
            }
            title={title ?? value}
          >
            {value}
          </span>
        </div>
      ))}
    </div>
  )
}

function Pairs({
  title,
  entries,
  prefix = "",
}: {
  title: string
  entries: { name: string; value: string }[]
  prefix?: string
}) {
  return (
    <div className="space-y-1.5">
      <div className="text-muted-foreground">{title}</div>
      {entries.map((entry) => (
        <div key={entry.name} className="font-mono text-[11px] break-all text-foreground">
          <span className="text-muted-foreground">{prefix + entry.name}:</span> {entry.value}
        </div>
      ))}
    </div>
  )
}
