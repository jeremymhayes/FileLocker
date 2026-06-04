import { Binary, BookOpen, Database, Fingerprint, Gauge, HardDrive, Hash, Info, Lock, Moon, Package, Power, RefreshCcw, Settings, Sparkles, Trash2, Unlock, type LucideIcon } from "lucide-react"
import { useState } from "react"
import { Switch } from "@/components/ui/switch"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { cn } from "@/lib/utils"
import type { PageKey, SettingsState } from "@/types/bridge"

const navSections: Array<{ label: string; items: Array<{ key: PageKey; label: string; icon: LucideIcon }> }> = [
  {
    label: "Home",
    items: [
      { key: "dashboard", label: "Dashboard", icon: Gauge },
      { key: "security-guide", label: "Security Guide", icon: BookOpen },
    ],
  },
  {
    label: "File Security",
    items: [
      { key: "encrypt", label: "Encrypt Files", icon: Lock },
      { key: "decrypt", label: "Decrypt Files", icon: Unlock },
      { key: "secure-delete", label: "Secure Delete", icon: Trash2 },
      { key: "hash", label: "Hash Files", icon: Hash },
      { key: "encode", label: "Encode Text", icon: Binary },
      { key: "metadata", label: "Metadata Scrambler", icon: Fingerprint },
    ],
  },
  {
    label: "System Care",
    items: [
      { key: "custom-clean", label: "Custom Clean", icon: Sparkles },
      { key: "partition-cleaner", label: "Partition Cleaner", icon: HardDrive },
      { key: "drive-optimizer", label: "Drive Optimizer", icon: RefreshCcw },
      { key: "registry-fixer", label: "Registry Fixer", icon: Database },
      { key: "startup-manager", label: "Startup Manager", icon: Power },
      { key: "app-manager", label: "App Manager", icon: Package },
    ],
  },
]

const secondaryNavItems: Array<{ key: PageKey; label: string; icon: LucideIcon }> = [
  { key: "settings", label: "Settings", icon: Settings },
  { key: "about", label: "About", icon: Info },
]

type SidebarProps = {
  activePage: PageKey
  onNavigate: (page: PageKey) => void
  version?: string
  settings?: SettingsState
  onThemeToggle?: (enabled: boolean) => void
  isThemeToggleBusy?: boolean
}

export function Sidebar({ activePage, onNavigate, version, settings, onThemeToggle, isThemeToggleBusy = false }: SidebarProps) {
  const [logoFailed, setLogoFailed] = useState(false)
  const darkModeEnabled = settings?.preferences.themePreference !== "Light"

  return (
    <aside className="flex h-full w-[var(--app-sidebar-width)] shrink-0 flex-col overflow-y-auto border-r border-layout-separator bg-sidebar text-sidebar-foreground">
      <div className="border-b border-sidebar-border px-3 py-3">
        <div className="sidebar-logo-area flex items-center gap-3">
          {logoFailed ? (
            <div className="font-display text-base font-semibold text-primary">FileLocker</div>
          ) : (
            <img
              src="/assets/logo.png"
              alt="FileLocker"
              onError={(event) => {
                event.currentTarget.style.display = "none"
                setLogoFailed(true)
              }}
              className="size-8 rounded-md object-contain"
            />
          )}
          <div className={cn("min-w-0", logoFailed && "hidden")}>
            <span className="app-name block truncate font-display text-sm font-semibold tracking-normal text-primary">FileLocker</span>
            <div className="truncate font-mono text-xs uppercase tracking-wider text-muted">v{version ?? "..."}</div>
          </div>
        </div>
      </div>

      <nav className="flex flex-1 flex-col gap-3 px-2.5 py-3">
        {navSections.map((section) => (
          <NavSection key={section.label} label={section.label} items={section.items} activePage={activePage} onNavigate={onNavigate} />
        ))}
      </nav>

      <div className="border-t border-sidebar-border p-3">
        <NavGroup items={secondaryNavItems} activePage={activePage} onNavigate={onNavigate} />
        <label
          aria-disabled={!onThemeToggle || isThemeToggleBusy}
          className={cn(
            "mt-3 flex items-center justify-between gap-3 border-t border-border px-1 py-2.5 text-secondary",
            isThemeToggleBusy && "opacity-60"
          )}
        >
          <span className="flex min-w-0 items-center gap-2">
            <Moon className="size-4 text-muted" aria-hidden />
            <span className="truncate text-sm">Dark Mode</span>
          </span>
          <Switch size="sm" checked={darkModeEnabled} onCheckedChange={onThemeToggle} disabled={!onThemeToggle || isThemeToggleBusy} />
        </label>
      </div>
    </aside>
  )
}

function NavSection({
  label,
  items,
  activePage,
  onNavigate,
}: {
  label: string
  items: Array<{ key: PageKey; label: string; icon: LucideIcon }>
  activePage: PageKey
  onNavigate: (page: PageKey) => void
}) {
  return (
    <section>
      <div className="mb-1.5 px-2.5 font-mono text-[10px] font-semibold uppercase tracking-[0.16em] text-muted">{label}</div>
      <NavGroup items={items} activePage={activePage} onNavigate={onNavigate} />
    </section>
  )
}

function NavGroup({
  items,
  activePage,
  onNavigate,
}: {
  items: Array<{ key: PageKey; label: string; icon: LucideIcon }>
  activePage: PageKey
  onNavigate: (page: PageKey) => void
}) {
  return (
    <div className="flex flex-col gap-1">
      {items.map((item) => {
        const Icon = item.icon
        const isActive = activePage === item.key
        return (
          <Tooltip key={item.key}>
            <TooltipTrigger asChild>
              <button
                type="button"
                className={cn(
                  "flex min-h-9 w-full items-center gap-2.5 rounded-md border border-transparent px-2.5 py-1.5 text-left font-display text-sm font-medium text-secondary transition-colors hover:border-border-accent hover:bg-bg-surface/70 hover:text-primary",
                  isActive && "border-nav-active-border bg-nav-active-bg text-primary"
                )}
                onClick={() => onNavigate(item.key)}
                aria-current={isActive ? "page" : undefined}
              >
                <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-md text-muted", isActive && "bg-accent/15 text-accent-blue")}>
                  <Icon className="size-4" aria-hidden />
                </span>
                <span className="truncate">{item.label}</span>
              </button>
            </TooltipTrigger>
            <TooltipContent side="right">{item.label}</TooltipContent>
          </Tooltip>
        )
      })}
    </div>
  )
}
