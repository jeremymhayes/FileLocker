type ProgressBarProps = {
  value: number
  label?: string
}

export function ProgressBar({ value, label }: ProgressBarProps) {
  const clamped = Math.min(100, Math.max(0, value))
  const rounded = Math.round(clamped)

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between font-mono text-xs uppercase tracking-wider text-muted">
        <span>{label ?? "Progress"}</span>
        <span>{rounded}%</span>
      </div>
      <div
        className="h-2 w-full overflow-hidden rounded-md bg-bg-dropzone"
        role="progressbar"
        aria-label={label ?? "Progress"}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={rounded}
        aria-valuetext={`${rounded}%`}
      >
        <div className="h-2 rounded-md bg-accent transition-[width]" style={{ width: `${clamped}%` }} />
      </div>
    </div>
  )
}
