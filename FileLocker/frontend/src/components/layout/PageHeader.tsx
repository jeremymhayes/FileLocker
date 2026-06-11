import type { ReactNode } from "react"

type PageHeaderProps = {
  title: string
  description?: string
  actions?: ReactNode
}

export function PageHeader({ title, description, actions }: PageHeaderProps) {
  return (
    <header className="border-b border-border bg-background px-4 py-2.5 xl:px-5">
      <div className="flex w-full flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div className="min-w-0">
          <h1 className="page-title truncate font-display text-base font-semibold uppercase leading-tight tracking-[0.08em] text-primary">{title}</h1>
          {description ? <p className="mt-1 max-w-3xl text-sm leading-snug text-secondary">{description}</p> : null}
        </div>
        {actions ? <div className="app-caption-action-safe flex shrink-0 items-center gap-2">{actions}</div> : null}
      </div>
    </header>
  )
}
