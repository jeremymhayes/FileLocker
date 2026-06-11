import { Binary, BookOpen, Database, Fingerprint, Gauge, HardDrive, Hash, Info, Lock, Moon, Package, PanelLeftClose, PanelLeftOpen, Power, RefreshCcw, Settings, Sparkles, Trash2, Unlock, type LucideIcon } from "lucide-react"
import { useState } from "react"
import { Switch } from "@/components/ui/switch"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { cn } from "@/lib/utils"
import type { PageKey, SettingsState } from "@/types/bridge"

const navSections: Array<{ label: string; items: Array<{ key: PageKey; label: string; icon: LucideIcon }> }> = [
  {
    label: "Home",
    items: [
      { key: "dashboard", label: "Home", icon: Gauge },
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
  const [collapsed, setCollapsed] = useState(false)
  const darkModeEnabled = settings?.preferences.themePreference !== "Light"

  return (
    <aside
      className={cn(
        "flex h-full shrink-0 flex-col overflow-y-auto border-r border-layout-separator bg-sidebar text-sidebar-foreground transition-[width] duration-200",
        collapsed ? "w-[64px]" : "w-[var(--app-sidebar-width)]"
      )}
    >
      <div className={cn("border-b border-sidebar-border py-3", collapsed ? "px-2" : "px-3")}>
        <div className={cn("sidebar-logo-area flex items-center gap-3", collapsed && "justify-center")}>
          {logoFailed ? (
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent font-mono text-xs font-semibold text-accent-foreground">FL</div>
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
          <div className={cn("min-w-0", (logoFailed || collapsed) && "hidden")}>
            <span className="app-name block truncate font-display text-sm font-semibold tracking-normal text-primary">FileLocker</span>
            <div className="truncate font-mono text-xs uppercase tracking-wider text-muted">v{version ?? "..."}</div>
          </div>
        </div>
      </div>

      <nav className={cn("flex flex-1 flex-col gap-3 py-3", collapsed ? "px-2" : "px-2.5")}>
        {navSections.map((section) => (
          <NavSection key={section.label} label={section.label} items={section.items} activePage={activePage} onNavigate={onNavigate} collapsed={collapsed} />
        ))}
      </nav>

      <div className={cn("border-t border-sidebar-border py-3", collapsed ? "px-2" : "px-3")}>
        <NavGroup items={secondaryNavItems} activePage={activePage} onNavigate={onNavigate} collapsed={collapsed} />
        <label
          aria-disabled={!onThemeToggle || isThemeToggleBusy}
          className={cn(
            "mt-3 flex items-center justify-between gap-3 border-t border-border px-1 py-2.5 text-secondary",
            collapsed && "justify-center px-0",
            isThemeToggleBusy && "opacity-60"
          )}
        >
          <span className="flex min-w-0 items-center gap-2">
            <Moon className="size-4 text-muted" aria-hidden />
            {!collapsed ? <span className="truncate text-sm">Dark Mode</span> : null}
          </span>
          {!collapsed ? <Switch size="sm" checked={darkModeEnabled} onCheckedChange={onThemeToggle} disabled={!onThemeToggle || isThemeToggleBusy} /> : null}
        </label>
        <button
          type="button"
          onClick={() => setCollapsed((value) => !value)}
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          className={cn(
            "mt-2 flex h-9 w-full items-center gap-2 rounded-md border border-transparent px-2.5 text-sm text-muted transition-colors hover:border-border hover:bg-bg-surface/60 hover:text-primary",
            collapsed && "justify-center px-0"
          )}
        >
          {collapsed ? <PanelLeftOpen className="size-4" aria-hidden /> : <PanelLeftClose className="size-4" aria-hidden />}
          {!collapsed ? <span className="truncate">Collapse</span> : null}
        </button>
      </div>
    </aside>
  )
}

function NavSection({
  label,
  items,
  activePage,
  onNavigate,
  collapsed,
}: {
  label: string
  items: Array<{ key: PageKey; label: string; icon: LucideIcon }>
  activePage: PageKey
  onNavigate: (page: PageKey) => void
  collapsed: boolean
}) {
  return (
    <section>
      {!collapsed ? <div className="mb-1.5 px-2.5 font-mono text-[10px] font-semibold uppercase tracking-[0.16em] text-muted">{label}</div> : null}
      <NavGroup items={items} activePage={activePage} onNavigate={onNavigate} collapsed={collapsed} />
    </section>
  )
}

function NavGroup({
  items,
  activePage,
  onNavigate,
  collapsed,
}: {
  items: Array<{ key: PageKey; label: string; icon: LucideIcon }>
  activePage: PageKey
  onNavigate: (page: PageKey) => void
  collapsed: boolean
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
                  "flex min-h-9 w-full items-center rounded-md border border-transparent py-1.5 text-left font-display text-sm font-medium text-secondary transition-colors hover:bg-bg-surface/70 hover:text-primary",
                  collapsed ? "justify-center px-0" : "gap-2.5 px-2.5",
                  isActive && "nav-item-active text-primary"
                )}
                onClick={() => onNavigate(item.key)}
                aria-current={isActive ? "page" : undefined}
              >
                <span className={cn("flex size-6 shrink-0 items-center justify-center rounded-md text-muted", isActive && "text-accent")}>
                  <Icon className="size-4" aria-hidden />
                </span>
                {!collapsed ? <span className="truncate">{item.label}</span> : null}
              </button>
            </TooltipTrigger>
            <TooltipContent side="right">{item.label}</TooltipContent>
          </Tooltip>
        )
      })}
    </div>
  )
}
