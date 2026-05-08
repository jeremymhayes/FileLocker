import * as React from "react"
import { Switch as SwitchPrimitive } from "radix-ui"

import { cn } from "@/lib/utils"

function Switch({
  className,
  size = "default",
  ...props
}: React.ComponentProps<typeof SwitchPrimitive.Root> & {
  size?: "sm" | "default"
}) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch"
      data-size={size}
      className={cn(
        "peer group/switch relative inline-flex shrink-0 items-center rounded-full border border-border transition-colors outline-none after:absolute after:-inset-x-3 after:-inset-y-2 focus-visible:border-accent focus-visible:ring-2 focus-visible:ring-accent/30 aria-invalid:border-destructive aria-invalid:ring-2 aria-invalid:ring-destructive/30 data-[size=default]:h-6 data-[size=default]:w-11 data-[size=sm]:h-5 data-[size=sm]:w-9 data-checked:border-accent data-checked:bg-accent data-unchecked:bg-bg-dropzone data-disabled:cursor-not-allowed data-disabled:opacity-45",
        className
      )}
      {...props}
    >
      <SwitchPrimitive.Thumb
        data-slot="switch-thumb"
        className="pointer-events-none block rounded-full bg-primary ring-0 transition-transform group-data-[size=default]/switch:size-5 group-data-[size=sm]/switch:size-4 group-data-[size=default]/switch:data-checked:translate-x-[20px] group-data-[size=sm]/switch:data-checked:translate-x-[16px] group-data-[size=default]/switch:data-unchecked:translate-x-0 group-data-[size=sm]/switch:data-unchecked:translate-x-0 group-data-checked/switch:bg-white"
      />
    </SwitchPrimitive.Root>
  )
}

export { Switch }
