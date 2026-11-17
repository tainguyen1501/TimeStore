import { ArrowRight } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Spinner } from "@/components/ui/spinner"

/** Full-width primary button of the auth form: arrow when idle, spinner + pending text while the call runs. */
export function SubmitButton({
  pending,
  disabled,
  label,
  pendingLabel,
}: {
  pending: boolean
  /** Not pending but not allowed either (e.g. while sign-in is throttled). */
  disabled?: boolean
  label: string
  pendingLabel: string
}) {
  return (
    <Button
      type="submit"
      size="lg"
      className="w-full rounded-xl shadow-lg shadow-primary/25"
      disabled={pending || disabled}
    >
      {pending ? (
        <>
          <Spinner data-icon="inline-start" />
          {pendingLabel}
        </>
      ) : (
        <>
          {label}
          <ArrowRight data-icon="inline-end" />
        </>
      )}
    </Button>
  )
}
