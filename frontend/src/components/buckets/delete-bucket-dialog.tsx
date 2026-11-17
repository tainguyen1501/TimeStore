import { ConfirmDialog } from "@/components/shared/confirm-dialog"
import { useDeleteBucket } from "@/hooks/api/use-buckets"

/** Deleting a bucket asks for its name (it must be empty; the server refuses otherwise). */
export function DeleteBucketDialog({
  bucket,
  onClose,
}: {
  bucket: string | null
  onClose: () => void
}) {
  const deleteBucket = useDeleteBucket()
  return (
    <ConfirmDialog
      open={bucket !== null}
      onOpenChange={(open) => !open && onClose()}
      title="Delete bucket"
      subtitle="This action cannot be undone."
      description="The bucket is removed from this node. Only an empty bucket can be deleted."
      confirmLabel="Delete"
      pendingLabel="Deleting…"
      destructive
      confirmText={bucket ?? undefined}
      isPending={deleteBucket.isPending}
      onConfirm={() => bucket && deleteBucket.mutate(bucket, { onSuccess: onClose })}
    />
  )
}
