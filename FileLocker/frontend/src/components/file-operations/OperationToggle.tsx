import { Switch } from "@/components/ui/switch"

type OperationToggleProps = {
  label: string
  checked: boolean
  onChange: (value: boolean) => void
  disabled?: boolean
}

export function OperationToggle({ label, checked, onChange, disabled = false }: OperationToggleProps) {
  return (
    <label
      aria-disabled={disabled}
      className={`flex min-h-11 items-center justify-between gap-3 border-b border-border px-3 py-3 text-sm text-secondary ${disabled ? "opacity-60" : ""}`}
    >
      <span>{label}</span>
      <Switch checked={checked} onCheckedChange={onChange} disabled={disabled} />
    </label>
  )
}
