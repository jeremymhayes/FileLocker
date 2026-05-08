import { Switch } from "@/components/ui/switch"

type SettingsToggleProps = {
  label: string
  detail: string
  checked: boolean
  onChange: (value: boolean) => void
}

export function SettingsToggle({ label, detail, checked, onChange }: SettingsToggleProps) {
  return (
    <label className="flex min-h-16 items-center justify-between gap-4 rounded-2xl border border-border bg-bg-dropzone px-4 py-3">
      <span className="min-w-0">
        <span className="block font-display text-sm font-semibold text-primary">{label}</span>
        <span className="mt-1 block text-sm leading-[1.55] text-secondary">{detail}</span>
      </span>
      <Switch checked={checked} onCheckedChange={onChange} />
    </label>
  )
}
