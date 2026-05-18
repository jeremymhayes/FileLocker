import type { LucideIcon } from "lucide-react"
import { CheckCircle2 } from "lucide-react"
import { cn } from "@/lib/utils"

type SummaryMetricProps = {
  icon: LucideIcon
  title: string
  value: string
  detail: string
  delta: string
  tone: string
  success?: boolean
}

export function SummaryMetric({
  icon: Icon,
  title,
  value,
  detail,
  delta,
  tone,
  success = false,
}: SummaryMetricProps) {
  return (
    <section className="border-b border-border py-3 last:border-b-0">
      <div className="flex items-start gap-3">
        <div className={cn("flex size-8 shrink-0 items-center justify-center rounded-md", tone)}>
          <Icon className="size-4" aria-hidden />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex items-center justify-between gap-3">
            <div className="security-label">{title}</div>
            {success ? <CheckCircle2 className="size-4 shrink-0 text-accent-green" aria-hidden /> : null}
          </div>
          <div className="mt-1 truncate font-display text-base font-semibold text-primary">{value}</div>
          <div className="mt-0.5 truncate text-xs text-secondary">{detail}</div>
          <div className="mt-2 truncate font-mono text-[11px] uppercase tracking-wider text-muted">{delta || "No change"}</div>
        </div>
      </div>
    </section>
  )
}
