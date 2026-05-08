import * as React from "react"

import { cn } from "@/lib/utils"

function Input({ className, type, ...props }: React.ComponentProps<"input">) {
  return (
    <input
      type={type}
      data-slot="input"
      className={cn(
        "h-11 w-full min-w-0 rounded-xl border border-border bg-bg-dropzone px-3 py-2 font-mono text-sm text-primary transition-colors outline-none file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium file:text-primary placeholder:text-muted focus-visible:border-accent focus-visible:ring-2 focus-visible:ring-accent/20 disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-45 aria-invalid:border-destructive aria-invalid:ring-2 aria-invalid:ring-destructive/25",
        className
      )}
      {...props}
    />
  )
}

export { Input }
