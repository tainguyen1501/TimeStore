import { useState } from "react"
import { AlertTriangle } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog"
import { IconButton } from "@/components/shared/icon-button"
import { Field } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { Spinner } from "@/components/ui/spinner"
import { useCopy } from "@/hooks/use-copy"
import { cn } from "@/lib/utils"

export type ConfirmDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onConfirm: () => void
  title: string
  /** Main text; say what happens and what does not. */
  description: React.ReactNode
  /** Small line under the title (e.g. "This action cannot be undone."). */
  subtitle?: string
  confirmLabel: string
  pendingLabel?: string
  isPending?: boolean
  /** Red icon and button (deletes, irreversible actions). */
  destructive?: boolean
  /**
   * When set, the user must type exactly this (usually the resource's name, GitHub-style) before confirming.
   * Leave unset for low-risk confirmations.
   */
  confirmText?: string
}

/** Confirmation for risky actions; optionally "type the name to confirm". */
export function ConfirmDialog(props: ConfirmDialogProps) {
  const { open, onOpenChange, isPending = false } = props
  return (
    <Dialog open={open} onOpenChange={(next) => !isPending && onOpenChange(next)}>
      <DialogContent showCloseButton={false} className="gap-4 p-6 sm:max-w-md">
        {/* Mounted only while open: the typed text starts empty each time. */}
        <ConfirmBody {...props} isPending={isPending} />
      </DialogContent>
    </Dialog>
  )
}

function ConfirmBody({
  onOpenChange,
  onConfirm,
  title,
  description,
  subtitle,
  confirmLabel,
  pendingLabel,
  isPending,
  destructive = false,
  confirmText,
}: ConfirmDialogProps) {
  const [typed, setTyped] = useState("")
  const { copied, copy } = useCopy()
  const matches = confirmText === undefined || typed === confirmText

  return (
    <form
      className="space-y-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (matches && !isPending) onConfirm()
      }}
    >
      <div className="flex items-center gap-3">
        <div
          className={cn(
            "flex size-11 shrink-0 items-center justify-center rounded-2xl",
            destructive
              ? "bg-destructive-soft text-destructive"
              : "bg-primary-soft text-primary-text"
          )}
        >
          <AlertTriangle className="size-5" />
        </div>
        <div>
          <DialogTitle>{title}</DialogTitle>
          {subtitle && <p className="text-xs text-muted-foreground">{subtitle}</p>}
        </div>
      </div>

      <DialogDescription className="text-xs leading-relaxed">{description}</DialogDescription>

      {confirmText !== undefined && (
        <Field>
          {/* Plain text, not a <label>: a label is select-none and steals the mouse to focus the input, so the
              name could not be selected. The copy button only copies; the user still pastes or types it. */}
          <p id="confirm-text-hint" className="text-xs text-muted-foreground">
            Type{" "}
            <strong className="font-mono font-semibold break-all text-foreground select-all">
              {confirmText}
            </strong>{" "}
            <IconButton
              icon={copied ? "copied" : "copy"}
              label={copied ? "Copied" : `Copy ${confirmText}`}
              size="xs"
              iconClassName={copied ? "text-success" : undefined}
              className="align-middle"
              onClick={() => copy(confirmText)}
            />{" "}
            to confirm
          </p>
          <Input
            id="confirm-text"
            aria-label={`Type ${confirmText} to confirm`}
            aria-describedby="confirm-text-hint"
            autoFocus
            autoComplete="off"
            spellCheck={false}
            className="font-mono"
            value={typed}
            disabled={isPending}
            onChange={(event) => setTyped(event.target.value)}
          />
        </Field>
      )}

      <div className="flex items-center justify-end gap-2.5 pt-2">
        <Button
          type="button"
          variant="secondary"
          disabled={isPending}
          onClick={() => onOpenChange(false)}
        >
          Cancel
        </Button>
        <Button
          type="submit"
          variant={destructive ? "destructive" : "default"}
          className={cn(
            destructive && "bg-destructive text-white hover:bg-destructive/90 hover:brightness-100"
          )}
          disabled={!matches || isPending}
        >
          {isPending && <Spinner data-icon="inline-start" />}
          {isPending ? (pendingLabel ?? confirmLabel) : confirmLabel}
        </Button>
      </div>
    </form>
  )
}
