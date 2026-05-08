import { Switch } from "@/components/ui/switch"

type OperationToggleProps = {
  label: string
  checked: boolean
  onChange: (value: boolean) => void
}

export function OperationToggle({ label, checked, onChange }: OperationToggleProps) {
  return (
    <label className="flex min-h-11 items-center justify-between gap-3 border-b border-border px-3 py-3 text-sm text-secondary">
      <span>{label}</span>
      <Switch checked={checked} onCheckedChange={onChange} />
    </label>
  )
}
