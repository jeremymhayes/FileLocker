import type { LucideIcon } from "lucide-react"

type SidePanelProps = {
  icon: LucideIcon
  title: string
  value: string
  detail: string
}

export function SidePanel({ icon: Icon, title, value, detail }: SidePanelProps) {
  return (
    <section className="surface-card">
      <div className="flex items-start gap-3">
        <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-accent-blue/10 text-accent-blue">
          <Icon className="size-5" aria-hidden />
        </div>
        <div className="min-w-0">
          <div className="security-label">{title}</div>
          <div className="mt-2 truncate font-display text-lg font-semibold text-primary">{value}</div>
          <div className="mt-1 text-sm text-secondary">{detail}</div>
        </div>
      </div>
    </section>
  )
}
