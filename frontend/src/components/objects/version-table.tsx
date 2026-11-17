import { FileIcon } from "@/components/shared/file-icon"
import { IconButton } from "@/components/shared/icon-button"
import { Badge } from "@/components/ui/badge"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { extensionOf } from "@/lib/file-kind"
import { formatBytes, formatShortDate } from "@/lib/format"
import { objectService, type ObjectVersionItemResponse } from "@/services/api/object-service"

/**
 * The last characters of a version id (its first ones are a timestamp, the same for versions written close
 * together); the full id is in the tooltip.
 */
const shortId = (versionId: string) =>
  versionId.length > 12 ? `…${versionId.slice(-8)}` : versionId

/**
 * Every version and delete marker of the objects in a folder, newest first per key; the key is shown on its newest
 * row. Admins can restore an older version (copy it over the key) or remove a version permanently.
 */
export function VersionTable({
  bucket,
  prefix,
  versions,
  canWrite,
  onDetails,
  onRestore,
  onDelete,
}: {
  bucket: string
  prefix: string
  versions: ObjectVersionItemResponse[]
  canWrite: boolean
  onDetails: (version: ObjectVersionItemResponse) => void
  onRestore: (version: ObjectVersionItemResponse) => void
  onDelete: (version: ObjectVersionItemResponse) => void
}) {
  return (
    <Table containerClassName="h-full overflow-auto">
      <TableHeader className="sticky top-0 z-10 bg-surface">
        <TableRow>
          <TableHead>Name</TableHead>
          <TableHead className="w-40">Version</TableHead>
          <TableHead className="w-28 text-right">Size</TableHead>
          <TableHead className="w-36">Last modified</TableHead>
          <TableHead className="w-28" />
        </TableRow>
      </TableHeader>
      <TableBody>
        {versions.map((version, i) => {
          const firstOfKey = i === 0 || versions[i - 1].key !== version.key
          const name = version.key.slice(prefix.length)
          return (
            <TableRow key={`${version.key}\u0000${version.versionId}`}>
              <TableCell>
                {firstOfKey ? (
                  <span className="flex items-center gap-3 text-xs font-semibold break-all text-foreground">
                    <FileIcon ext={extensionOf(version.key)} size="sm" />
                    {name}
                  </span>
                ) : (
                  <span className="pl-12 text-[11px] text-muted-foreground">older version</span>
                )}
              </TableCell>
              <TableCell>
                <span className="flex flex-wrap items-center gap-1.5">
                  <span className="font-mono text-xs" title={version.versionId}>
                    {shortId(version.versionId)}
                  </span>
                  {version.isLatest && <Badge variant="secondary">Latest</Badge>}
                  {version.isDeleteMarker && <Badge variant="destructive">Delete marker</Badge>}
                </span>
              </TableCell>
              <TableCell className="text-right tabular-nums">
                {version.isDeleteMarker ? "—" : formatBytes(version.size)}
              </TableCell>
              <TableCell
                className="text-muted-foreground tabular-nums"
                title={version.lastModified}
              >
                {formatShortDate(version.lastModified)}
              </TableCell>
              <TableCell className="text-right">
                <span className="inline-flex items-center gap-0.5">
                  {!version.isDeleteMarker && (
                    <>
                      <IconButton
                        icon="detail"
                        label={`Details of ${name} version ${version.versionId}`}
                        onClick={() => onDetails(version)}
                      />
                      <IconButton
                        icon="download"
                        label={`Download ${name} version ${version.versionId}`}
                        render={
                          <a
                            href={objectService.downloadUrl(bucket, version.key, version.versionId)}
                            download
                          />
                        }
                      />
                    </>
                  )}
                  {canWrite && !version.isLatest && !version.isDeleteMarker && (
                    <IconButton
                      icon="undo"
                      label={`Restore ${name} version ${version.versionId}`}
                      onClick={() => onRestore(version)}
                    />
                  )}
                  {canWrite && (
                    <IconButton
                      icon="delete"
                      tone="danger"
                      label={`Delete ${name} version ${version.versionId}`}
                      onClick={() => onDelete(version)}
                    />
                  )}
                </span>
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
  )
}
