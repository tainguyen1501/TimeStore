import { IconButton } from "@/components/shared/icon-button"
import { Icon } from "@/components/shared/icons"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import type { PublicState } from "@/lib/public-access"

const describeParent = (bucket: string, from: string) => (from === "" ? `bucket ${bucket}` : from)

export function PublicBadge({ state, bucket }: { state: PublicState; bucket: string }) {
  if (!state) return null
  return (
    <Badge
      variant="destructive"
      title={
        state.kind === "direct"
          ? "Anyone can read the objects here without signing in."
          : `Public through ${describeParent(bucket, state.from)}.`
      }
    >
      <Icon name="public" />
      Public
    </Badge>
  )
}

/** The bucket's toggle in the page header. */
export function BucketPublicButton({
  isPublic,
  pending,
  onMakePublic,
  onMakePrivate,
}: {
  isPublic: boolean
  pending: boolean
  onMakePublic: () => void
  onMakePrivate: () => void
}) {
  return isPublic ? (
    <Button variant="ghost" size="sm" disabled={pending} onClick={onMakePrivate}>
      <Icon name="lock" data-icon="inline-start" />
      Make private
    </Button>
  ) : (
    <Button variant="ghost" size="sm" disabled={pending} onClick={onMakePublic}>
      <Icon name="public" data-icon="inline-start" />
      Make public
    </Button>
  )
}

/** A folder row's toggle; disabled when the folder is public through a parent. */
export function FolderPublicButton({
  name,
  state,
  bucket,
  pending,
  onMakePublic,
  onMakePrivate,
}: {
  name: string
  state: PublicState
  bucket: string
  pending: boolean
  onMakePublic: () => void
  onMakePrivate: () => void
}) {
  if (state?.kind === "inherited")
    return (
      <IconButton
        icon="public"
        label={`${name} is public through ${describeParent(bucket, state.from)}`}
        disabled
      />
    )
  return state?.kind === "direct" ? (
    <IconButton
      icon="lock"
      label={`Make ${name} private`}
      disabled={pending}
      onClick={onMakePrivate}
    />
  ) : (
    <IconButton
      icon="public"
      label={`Make ${name} public`}
      disabled={pending}
      onClick={onMakePublic}
    />
  )
}
