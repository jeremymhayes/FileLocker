import { useEffect, useState, type MouseEvent } from "react"
import { Eye, EyeOff } from "lucide-react"
import { Input } from "@/components/ui/input"

type PasswordFieldProps = {
  label: string
  value: string
  onChange: (value: string) => void
  placeholder: string
  disabled?: boolean
}

export function PasswordField({ label, value, onChange, placeholder, disabled = false }: PasswordFieldProps) {
  const [visible, setVisible] = useState(false)
  const isRevealed = visible && !disabled

  useEffect(() => {
    if (disabled) {
      setVisible(false)
    }
  }, [disabled])

  function handleToggleMouseDown(event: MouseEvent<HTMLButtonElement>) {
    event.preventDefault()
    event.stopPropagation()
  }

  function handleToggleClick(event: MouseEvent<HTMLButtonElement>) {
    event.preventDefault()
    event.stopPropagation()
    setVisible((current) => !current)
  }

  return (
    <label className="block">
      <span className="security-label">{label}</span>
      <span className="relative mt-2 block">
        <Input className="pr-11" type={isRevealed ? "text" : "password"} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} disabled={disabled} />
        <button
          type="button"
          className="absolute right-2 top-1/2 -translate-y-1/2 rounded-md p-1.5 text-muted transition-colors hover:text-primary focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:text-muted"
          aria-label={isRevealed ? `Hide ${label}` : `Show ${label}`}
          disabled={disabled}
          onMouseDown={handleToggleMouseDown}
          onClick={handleToggleClick}
        >
          {isRevealed ? <EyeOff className="size-4" aria-hidden /> : <Eye className="size-4" aria-hidden />}
        </button>
      </span>
    </label>
  )
}
