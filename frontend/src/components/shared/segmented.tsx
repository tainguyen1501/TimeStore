import { cn } from "@/lib/utils"

/**
 * A pill of options (design: Grid / List / Table, Light / Dark, Rendered / Markdown). `solid` highlights the chosen
 * option in violet (view switchers); otherwise it is a raised chip (settings, tabs).
 */
export function Segmented<T extends string>({
  value,
  onChange,
  options,
  solid = false,
  className,
  label,
}: {
  value: T
  onChange: (value: T) => void
  options: { value: T; label: string; icon?: React.ReactNode; iconOnly?: boolean }[]
  solid?: boolean
  className?: string
  /** Accessible name of the group. */
  label: string
}) {
  return (
    <div
      role="radiogroup"
      aria-label={label}
      className={cn(
        "flex items-center gap-0.5 rounded-xl p-1",
        solid ? "bg-raised" : "bg-field",
        className
      )}
    >
      {options.map((option) => {
        const selected = option.value === value
        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            aria-checked={selected}
            aria-label={option.iconOnly ? option.label : undefined}
            title={option.iconOnly ? option.label : undefined}
            onClick={() => onChange(option.value)}
            className={cn(
              "inline-flex items-center gap-1.5 rounded-lg text-xs font-semibold transition-colors [&_svg]:size-3.5",
              option.iconOnly ? "p-1.5" : "px-3 py-1.5",
              selected
                ? solid
                  ? "bg-primary text-primary-foreground"
                  : "bg-raised text-foreground shadow-xs dark:bg-surface-hover"
                : "text-muted-foreground hover:text-foreground"
            )}
          >
            {option.icon}
            {!option.iconOnly && <span>{option.label}</span>}
          </button>
        )
      })}
    </div>
  )
}
