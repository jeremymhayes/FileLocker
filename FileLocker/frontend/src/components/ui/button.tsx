import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { Slot } from "radix-ui"

import { cn } from "@/lib/utils"

const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-xl border bg-clip-padding font-mono text-[13px] font-semibold uppercase tracking-wider whitespace-nowrap transition-colors outline-none select-none focus-visible:border-accent focus-visible:ring-2 focus-visible:ring-accent/30 active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-45 aria-invalid:border-destructive aria-invalid:ring-2 aria-invalid:ring-destructive/30 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "border-accent bg-accent text-white [a]:hover:bg-accent-hover hover:border-accent-hover hover:bg-accent-hover",
        outline:
          "border-border bg-transparent text-secondary hover:border-accent hover:bg-bg-surface-hover hover:text-primary aria-expanded:border-accent aria-expanded:text-primary",
        secondary:
          "border-border bg-bg-surface-hover text-primary hover:border-accent hover:bg-bg-dropzone aria-expanded:border-accent aria-expanded:text-primary",
        ghost:
          "border-transparent bg-transparent text-secondary hover:bg-bg-surface-hover hover:text-primary aria-expanded:text-primary",
        destructive:
          "border-destructive bg-destructive text-white hover:bg-destructive/85 focus-visible:border-destructive focus-visible:ring-destructive/30",
        link: "border-transparent bg-transparent text-accent underline-offset-4 hover:text-accent-hover hover:underline",
      },
      size: {
        default:
          "h-10 gap-2 px-4 py-2 has-data-[icon=inline-end]:pr-3 has-data-[icon=inline-start]:pl-3",
        xs: "h-7 gap-1 px-2 text-xs has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3",
        sm: "h-9 gap-1.5 px-3 text-xs has-data-[icon=inline-end]:pr-2 has-data-[icon=inline-start]:pl-2 [&_svg:not([class*='size-'])]:size-3.5",
        lg: "h-11 gap-2 px-5 has-data-[icon=inline-end]:pr-4 has-data-[icon=inline-start]:pl-4",
        icon: "size-10",
        "icon-xs": "size-7 [&_svg:not([class*='size-'])]:size-3",
        "icon-sm": "size-8",
        "icon-lg": "size-11",
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
  asChild = false,
  ...props
}: React.ComponentProps<"button"> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean
  }) {
  const Comp = asChild ? Slot.Root : "button"

  return (
    <Comp
      data-slot="button"
      data-variant={variant}
      data-size={size}
      className={cn(buttonVariants({ variant, size, className }))}
      {...props}
    />
  )
}

export { Button, buttonVariants }
