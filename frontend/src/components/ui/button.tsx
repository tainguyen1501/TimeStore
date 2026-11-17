import { Button as ButtonPrimitive } from "@base-ui/react/button"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "cn"

const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-xl border border-transparent bg-clip-padding text-xs font-semibold whitespace-nowrap transition-colors outline-none select-none focus-visible:ring-2 focus-visible:ring-ring/30 active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-50 aria-invalid:border-destructive aria-invalid:ring-3 aria-invalid:ring-destructive/20 dark:aria-invalid:border-destructive/50 dark:aria-invalid:ring-destructive/40 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        // Design: violet fill; "outline" is the white chip on a tonal toolbar (no border); "secondary" the field fill.
        default: "bg-primary text-primary-foreground hover:bg-primary-hover",
        outline:
          "bg-raised text-subtle-foreground hover:text-foreground aria-expanded:text-foreground",
        secondary:
          "bg-field text-subtle-foreground hover:text-foreground aria-expanded:text-foreground",
        ghost:
          "text-subtle-foreground hover:bg-surface-hover hover:text-foreground aria-expanded:bg-surface-hover aria-expanded:text-foreground",
        destructive:
          "bg-destructive-soft text-destructive hover:brightness-95 focus-visible:ring-destructive/30 dark:hover:brightness-110",
        link: "text-primary-text underline-offset-4 hover:underline",
      },
      size: {
        default: "h-9 gap-1.5 px-4 [&_svg:not([class*='size-'])]:size-4",
        xs: "h-6 gap-1 rounded-lg px-2 text-[11px] [&_svg:not([class*='size-'])]:size-3",
        sm: "h-8 gap-1.5 px-3 [&_svg:not([class*='size-'])]:size-3.5",
        lg: "h-10 gap-2 rounded-2xl px-4 [&_svg:not([class*='size-'])]:size-4",
        icon: "size-9",
        "icon-xs": "size-6 rounded-lg [&_svg:not([class*='size-'])]:size-3.5",
        "icon-sm": "size-8 rounded-lg [&_svg:not([class*='size-'])]:size-4",
        "icon-lg": "size-10 [&_svg:not([class*='size-'])]:size-4",
      },
    },
    defaultVariants: {
      variant: "default",
      size: "default",
    },
  }
)

function Button({
  className,
  variant = "default",
  size = "default",
  ...props
}: ButtonPrimitive.Props & VariantProps<typeof buttonVariants>) {
  return (
    <ButtonPrimitive
      data-slot="button"
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  )
}

export { Button, buttonVariants }
