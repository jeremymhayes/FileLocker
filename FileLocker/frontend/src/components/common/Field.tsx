import type { ReactNode } from "react"

type FieldProps = {
  label: string
  children: ReactNode
}

export function Field({ label, children }: FieldProps) {
  return (
    <label className="flex min-w-0 flex-col gap-2">
      <span className="security-label">{label}</span>
      {children}
    </label>
  )
}
