type ProgressBarProps = {
  value: number
  label?: string
}

export function ProgressBar({ value, label }: ProgressBarProps) {
  const clamped = Math.min(100, Math.max(0, value))

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between font-mono text-xs uppercase tracking-wider text-muted">
        <span>{label ?? "Progress"}</span>
        <span>{Math.round(clamped)}%</span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded-md bg-bg-dropzone">
        <div className="h-2 rounded-md bg-accent transition-[width]" style={{ width: `${clamped}%` }} />
      </div>
    </div>
  )
}
