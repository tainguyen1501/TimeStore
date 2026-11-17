import { type IconName, Icon } from "@/components/shared/icons"
import { Button } from "@/components/ui/button"
import { Spinner } from "@/components/ui/spinner"
import { cn } from "@/lib/utils"

const SIZES = { xs: "icon-xs", sm: "icon-sm", md: "icon" } as const

type IconButtonProps = Omit<React.ComponentProps<typeof Button>, "children" | "size"> & {
  /** Registry name (`components/shared/icons.tsx`). */
  icon: IconName
  /** Accessible name; also the tooltip unless `title` is given. Say what it does to what ("Delete db"). */
  label: string
  size?: keyof typeof SIZES
  /** `danger`: turns red on hover (delete, remove). */
  tone?: "default" | "danger"
  /** Spins the icon (refresh while fetching). */
  spin?: boolean
  /** Shows a spinner instead of the icon (the action is loading). */
  loading?: boolean
  /** Toggle buttons: highlighted while on (also sets `aria-pressed`). */
  active?: boolean
  iconClassName?: string
}

/** A button showing only an icon: the one way to render icon actions, so they look and behave the same everywhere. */
export function IconButton({
  icon,
  label,
  title,
  size = "sm",
  variant = "ghost",
  tone = "default",
  spin = false,
  loading = false,
  active,
  iconClassName,
  className,
  type = "button",
  ...props
}: IconButtonProps) {
  return (
    <Button
      type={type}
      variant={variant}
      size={SIZES[size]}
      aria-label={label}
      aria-pressed={active}
      title={title ?? label}
      className={cn(
        tone === "danger" && "hover:text-destructive",
        active && "bg-primary/10 text-primary",
        className
      )}
      {...props}
    >
      {loading ? (
        <Spinner />
      ) : (
        <Icon name={icon} className={cn(spin && "animate-spin", iconClassName)} />
      )}
    </Button>
  )
}
