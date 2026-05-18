import type { ElementType, ReactNode } from "react"
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
