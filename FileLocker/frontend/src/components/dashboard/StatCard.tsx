import type { LucideIcon } from "lucide-react"
import { CheckCircle2 } from "lucide-react"
import { cn } from "@/lib/utils"

type StatCardProps = {
  icon: LucideIcon
  title: string
  value: string
  detail: string
  delta: string
  tone: string
  success?: boolean
}

export function StatCard({
  icon: Icon,
  title,
  value,
  detail,
  delta,
  tone,
  success = false,
}: StatCardProps) {
  return (
    <section className="surface-card">
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <div className="security-label">{title}</div>
          <div className="mt-3 truncate font-display text-2xl font-semibold leading-[1.3] text-primary">{value}</div>
          <div className="mt-1 truncate text-sm text-secondary">{detail}</div>
        </div>
        <div className={cn("flex size-11 shrink-0 items-center justify-center rounded-xl", tone)}>
          <Icon className="size-5" aria-hidden />
        </div>
      </div>
      <div className="mt-5 flex items-center justify-between gap-3 rounded-xl bg-bg-dropzone px-3 py-2">
        <span className="truncate font-mono text-xs uppercase tracking-wider text-muted">{delta || "No weekly change"}</span>
        {success ? <CheckCircle2 className="size-4 shrink-0 text-accent-green" aria-hidden /> : null}
      </div>
    </section>
  )
}
