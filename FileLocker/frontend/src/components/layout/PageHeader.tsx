import type { ReactNode } from "react"

type PageHeaderProps = {
  title: string
  description: string
  actions?: ReactNode
}

export function PageHeader({ title, description, actions }: PageHeaderProps) {
  return (
    <header className="border-b border-border bg-background px-8 py-6">
      <div className="flex w-full flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="min-w-0">
          <h1 className="page-title truncate font-display text-2xl font-semibold leading-[1.4] text-primary">{title}</h1>
          <p className="mt-1 max-w-3xl text-sm leading-[1.65] text-secondary">{description}</p>
        </div>
        {actions ? <div className="flex shrink-0 items-center gap-2">{actions}</div> : null}
      </div>
    </header>
  )
}
