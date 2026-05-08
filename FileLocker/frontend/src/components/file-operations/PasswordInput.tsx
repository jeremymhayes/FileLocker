import { useState } from "react"
import { Eye, EyeOff } from "lucide-react"
import { Input } from "@/components/ui/input"

type PasswordInputProps = {
  label: string
  value: string
  onChange: (value: string) => void
  placeholder: string
}

export function PasswordInput({ label, value, onChange, placeholder }: PasswordInputProps) {
  const [visible, setVisible] = useState(false)

  return (
    <span className="relative block">
      <Input className="pr-11" type={visible ? "text" : "password"} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} />
      <button
        type="button"
        className="absolute right-2 top-1/2 -translate-y-1/2 rounded-lg p-2 text-muted transition-colors hover:text-primary focus-visible:ring-2 focus-visible:ring-accent"
        aria-label={visible ? `Hide ${label}` : `Show ${label}`}
        onClick={() => setVisible((current) => !current)}
      >
        {visible ? <EyeOff className="size-4" aria-hidden /> : <Eye className="size-4" aria-hidden />}
      </button>
    </span>
  )
}
