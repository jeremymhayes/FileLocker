import { useId, useState } from "react"
import type { ElementType, ReactNode } from "react"
import { ChevronDown, SlidersHorizontal } from "lucide-react"
import type { LucideIcon } from "lucide-react"
import { cn } from "@/lib/utils"

type PrimitiveProps = {
  children: ReactNode
  className?: string
}

type SectionProps = PrimitiveProps & {
  as?: ElementType
}

export function Workspace({ children, className }: PrimitiveProps) {
  return <div className={cn("security-page", className)}>{children}</div>
}

export function Workbench({ children, className }: PrimitiveProps) {
  return <div className={cn("flex min-w-0 flex-col gap-3", className)}>{children}</div>
}

export function SplitWorkspace({ children, className }: PrimitiveProps) {
  return (
    <div className={cn("grid min-w-0 gap-3 xl:grid-cols-[minmax(0,1fr)_330px]", className)}>
      {children}
    </div>
  )
}

export function DetailRail({ children, className }: PrimitiveProps) {
  return <aside className={cn("min-w-0 border-l border-border pl-4", className)}>{children}</aside>
}

export function Section({ children, className, as: Component = "section" }: SectionProps) {
  return (
    <Component className={cn("min-w-0 rounded-none border-y border-x-0 border-border bg-transparent px-0 py-3 shadow-none", className)}>
      {children}
    </Component>
  )
}

export function SectionHeader({ children, className }: PrimitiveProps) {
  return (
    <div className={cn("flex flex-wrap items-center justify-between gap-2.5 border-b border-border bg-transparent px-0 pb-2.5 pt-0", className)}>
      {children}
    </div>
  )
}

export function SectionBody({ children, className }: PrimitiveProps) {
  return <div className={cn("px-0 py-3", className)}>{children}</div>
}

export function SectionFooter({ children, className }: PrimitiveProps) {
  return <div className={cn("flex flex-wrap items-center gap-2 border-t border-border bg-transparent px-0 pb-0 pt-2.5", className)}>{children}</div>
}

export function SectionTitle({ children, className }: PrimitiveProps) {
  return <h2 className={cn(className, "font-display text-base font-semibold leading-tight text-primary")}>{children}</h2>
}

export function Toolbar({ children, className }: PrimitiveProps) {
  return <div className={cn("flex flex-wrap items-center gap-2", className)}>{children}</div>
}

export function EmptyState({ children, className }: PrimitiveProps) {
  return (
    <div className={cn("flex min-h-20 flex-col items-start justify-center border-y border-border px-3 py-3 text-left text-sm leading-snug text-secondary", className)}>
      {children}
    </div>
  )
}

export function KeyValueRow({ label, value, className }: { label: string; value: ReactNode; className?: string }) {
  return (
    <div className={cn("grid gap-2 border-b border-border py-2 last:border-b-0 md:grid-cols-[minmax(0,1fr)_minmax(160px,240px)] md:items-center", className)}>
      <div className="min-w-0 text-sm text-secondary">{label}</div>
      <div className="min-w-0 text-left font-mono text-sm text-primary md:text-right">{value}</div>
    </div>
  )
}

export function SettingsGroup({ children, className }: PrimitiveProps) {
  return <div className={cn("border-y border-border", className)}>{children}</div>
}

export function SettingsRow({ label, detail, children }: { label: string; detail?: string; children: ReactNode }) {
  return (
    <div className="grid gap-3 border-b border-border px-0 py-2.5 last:border-b-0 md:grid-cols-[minmax(0,1fr)_minmax(220px,340px)] md:items-center">
      <div className="min-w-0">
        <div className="text-sm font-medium text-primary">{label}</div>
        {detail ? <div className="mt-0.5 truncate text-xs text-secondary">{detail}</div> : null}
      </div>
      <div className="flex min-w-0 justify-start md:justify-end">{children}</div>
    </div>
  )
}

type AdvancedSectionProps = {
  children: ReactNode
  summary?: ReactNode
  hint?: ReactNode
  icon?: LucideIcon
  defaultOpen?: boolean
  className?: string
}

export function AdvancedSection({ children, summary = "Advanced", hint, icon: Icon = SlidersHorizontal, defaultOpen = false, className }: AdvancedSectionProps) {
  const [open, setOpen] = useState(defaultOpen)
  const bodyId = useId()

  return (
    <div className={cn("border-t border-border pt-3", className)}>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={bodyId}
        onClick={() => setOpen((value) => !value)}
        className="flex w-full items-center gap-2 rounded-md py-1.5 text-left text-sm font-semibold text-secondary transition-colors hover:text-primary"
      >
        <Icon className="size-4 shrink-0" aria-hidden />
        <span className="min-w-0 flex-1">{summary}</span>
        <ChevronDown className={cn("size-4 shrink-0 transition-transform", open && "rotate-180")} aria-hidden />
      </button>
      {hint ? <p className="ml-6 text-xs leading-snug text-muted">{hint}</p> : null}
      <div id={bodyId} hidden={!open} className={cn(open && "mt-3 space-y-3")}>
        {open ? children : null}
      </div>
    </div>
  )
}

export const Disclosure = AdvancedSection

type ScanEmptyStateProps = {
  title: ReactNode
  description?: ReactNode
  eyebrow?: ReactNode
  icon?: LucideIcon
  action?: ReactNode
  className?: string
}

export function ScanEmptyState({ eyebrow, title, description, icon: Icon, action, className }: ScanEmptyStateProps) {
  return (
    <div className={cn("app-empty-state min-h-40 items-center gap-3 px-6 py-8 text-center", className)}>
      {Icon ? (
        <div className="flex size-12 items-center justify-center rounded-full border border-accent/25 bg-accent/10 text-accent">
          <Icon className="size-6" aria-hidden />
        </div>
      ) : null}
      <div>
        {eyebrow ? <p className="security-label mb-1 justify-center text-center">{eyebrow}</p> : null}
        <p className="font-display text-lg font-semibold text-primary">{title}</p>
        {description ? <p className="mx-auto mt-1 max-w-lg text-sm leading-snug text-secondary">{description}</p> : null}
      </div>
      {action ? <div>{action}</div> : null}
    </div>
  )
}
