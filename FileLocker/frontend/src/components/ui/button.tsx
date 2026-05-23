import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { Slot } from "radix-ui"

import { cn } from "@/lib/utils"

const buttonVariants = cva(
  "group/button inline-flex shrink-0 items-center justify-center rounded-md border bg-clip-padding text-sm font-medium whitespace-nowrap transition-[background-color,border-color,color,box-shadow,transform] duration-150 ease-out outline-none select-none focus-visible:border-accent focus-visible:ring-2 focus-visible:ring-accent/30 active:not-aria-[haspopup]:translate-y-px disabled:pointer-events-none disabled:opacity-45 aria-invalid:border-destructive aria-invalid:ring-2 aria-invalid:ring-destructive/30 [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
  {
    variants: {
      variant: {
        default: "border-accent bg-accent text-[#08111f] [a]:hover:bg-accent-hover hover:border-accent-hover hover:bg-accent-hover",
        outline:
          "border-border/80 bg-transparent text-secondary hover:border-border-strong hover:bg-bg-surface-hover hover:text-primary aria-expanded:border-accent aria-expanded:text-primary",
        secondary:
          "border-border/80 bg-bg-surface-hover text-primary hover:border-border-strong hover:bg-bg-surface-raised aria-expanded:border-accent aria-expanded:text-primary",
        violet:
          "border-accent-purple/50 bg-accent-purple/16 text-primary hover:border-accent-purple hover:bg-accent-purple/22 focus-visible:border-accent-purple focus-visible:ring-accent-purple/30",
        ghost:
          "border-transparent bg-transparent text-secondary hover:bg-bg-surface-hover hover:text-primary aria-expanded:text-primary",
        destructive:
          "border-destructive/45 bg-destructive/14 text-red-100 hover:border-destructive/60 hover:bg-destructive/20 focus-visible:border-destructive focus-visible:ring-destructive/30",
        link: "border-transparent bg-transparent text-accent underline-offset-4 hover:text-accent-hover hover:underline",
      },
      size: {
        default:
          "h-9 gap-2 px-3 py-2 has-data-[icon=inline-end]:pr-2.5 has-data-[icon=inline-start]:pl-2.5",
        xs: "h-7 gap-1 px-2 text-xs has-data-[icon=inline-end]:pr-1.5 has-data-[icon=inline-start]:pl-1.5 [&_svg:not([class*='size-'])]:size-3",
        sm: "h-8 gap-1.5 px-2.5 text-sm has-data-[icon=inline-end]:pr-2 has-data-[icon=inline-start]:pl-2 [&_svg:not([class*='size-'])]:size-3.5",
        lg: "h-10 gap-2 px-4 has-data-[icon=inline-end]:pr-3 has-data-[icon=inline-start]:pl-3",
        icon: "size-9",
        "icon-xs": "size-7 [&_svg:not([class*='size-'])]:size-3",
        "icon-sm": "size-8",
        "icon-lg": "size-9",
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
  type,
  ...props
}: React.ComponentProps<"button"> &
  VariantProps<typeof buttonVariants> & {
    asChild?: boolean
  }) {
  const Comp = asChild ? Slot.Root : "button"
  const typeProps = asChild ? {} : { type: type ?? "button" }

  return (
    <Comp
      data-slot="button"
      data-variant={variant}
      data-size={size}
      className={cn(buttonVariants({ variant, size, className }))}
      {...typeProps}
      {...props}
    />
  )
}

export { Button, buttonVariants }
