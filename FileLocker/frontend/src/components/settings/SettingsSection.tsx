import type { ReactNode } from "react"
import type { LucideIcon } from "lucide-react"

type SettingsSectionProps = {
  icon: LucideIcon
  title: string
  description: string
  children: ReactNode
}

export function SettingsSection({ icon: Icon, title, description, children }: SettingsSectionProps) {
  return (
    <section className="surface-card">
      <div className="mb-5 flex items-start gap-3">
        <div className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent-blue/10 text-accent-blue">
          <Icon className="size-5" aria-hidden />
        </div>
        <div className="min-w-0">
          <h2 className="font-display text-lg font-semibold text-primary">{title}</h2>
          <p className="mt-1 text-sm leading-[1.65] text-secondary">{description}</p>
        </div>
      </div>
      <div className="space-y-4">{children}</div>
    </section>
  )
}
