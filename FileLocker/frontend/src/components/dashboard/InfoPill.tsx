type InfoPillProps = {
  label: string
  value: string
}

export function InfoPill({ label, value }: InfoPillProps) {
  return (
    <div className="rounded-xl border border-border bg-bg-dropzone px-3 py-2">
      <div className="font-mono text-xs uppercase tracking-wider text-muted">{label}</div>
      <div className="mt-1 font-mono text-sm text-primary">{value}</div>
    </div>
  )
}
