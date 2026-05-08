import { Binary, BookOpen, Fingerprint, Gauge, Hash, Info, Lock, Moon, Settings, Trash2, Unlock, type LucideIcon } from "lucide-react"
import { useState } from "react"
import { Switch } from "@/components/ui/switch"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { cn } from "@/lib/utils"
import type { PageKey, SettingsState } from "@/types/bridge"

const primaryNavItems: Array<{ key: PageKey; label: string; icon: LucideIcon }> = [
  { key: "dashboard", label: "Dashboard", icon: Gauge },
  { key: "encrypt", label: "Encrypt Files", icon: Lock },
  { key: "decrypt", label: "Decrypt Files", icon: Unlock },
  { key: "hash", label: "Hash Files", icon: Hash },
  { key: "encode", label: "Encode Text", icon: Binary },
  { key: "metadata", label: "Metadata Scrambler", icon: Fingerprint },
  { key: "secure-delete", label: "Secure Delete", icon: Trash2 },
  { key: "security-guide", label: "Security Guide", icon: BookOpen },
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
}

export function Sidebar({ activePage, onNavigate, version, settings, onThemeToggle }: SidebarProps) {
  const [logoFailed, setLogoFailed] = useState(false)
  const darkModeEnabled = settings?.preferences.themePreference !== "Light"

  return (
    <aside className="flex h-screen w-[220px] shrink-0 flex-col overflow-y-auto border-r border-layout-separator bg-sidebar text-sidebar-foreground">
      <div className="border-b border-sidebar-border px-4 py-5">
        <div className="sidebar-logo-area flex items-center gap-3">
          {logoFailed ? (
            <div className="flex size-9 items-center justify-center rounded-lg bg-accent-blue font-mono text-sm font-semibold text-white">FL</div>
          ) : (
            <img
              src="/assets/logo.png"
              alt="FileLocker"
              onError={(event) => {
                event.currentTarget.style.display = "none"
                setLogoFailed(true)
              }}
              style={{ width: 36, height: 36, objectFit: "contain" }}
            />
          )}
          <div className="min-w-0">
            <span className="app-name block truncate font-display text-base font-semibold tracking-normal text-primary">FileLocker</span>
            <div className="truncate font-mono text-xs uppercase tracking-wider text-muted">v{version ?? "..."}</div>
          </div>
        </div>
      </div>

      <nav className="flex flex-1 flex-col py-3">
        <NavGroup items={primaryNavItems} activePage={activePage} onNavigate={onNavigate} />
      </nav>

      <div className="border-t border-sidebar-border p-3">
        <NavGroup items={secondaryNavItems} activePage={activePage} onNavigate={onNavigate} />
        <label className="mt-3 flex items-center justify-between gap-3 rounded-xl border border-border bg-bg-surface px-3 py-3 text-secondary">
          <span className="flex min-w-0 items-center gap-2">
            <Moon className="size-4 text-muted" aria-hidden />
            <span className="truncate text-sm">Dark Mode</span>
          </span>
          <Switch size="sm" checked={darkModeEnabled} onCheckedChange={onThemeToggle} disabled={!onThemeToggle} />
        </label>
      </div>
    </aside>
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
    <div className="flex flex-col">
      {items.map((item) => {
        const Icon = item.icon
        const isActive = activePage === item.key
        return (
          <Tooltip key={item.key}>
            <TooltipTrigger asChild>
              <button
                className={cn(
                  "flex h-10 w-full items-center gap-3 border-l-2 border-transparent px-4 text-left font-display text-sm text-secondary transition-colors hover:border-border-accent hover:bg-bg-surface/60 hover:text-primary",
                  isActive && "border-nav-active-border bg-nav-active-bg text-primary"
                )}
                onClick={() => onNavigate(item.key)}
              >
                <Icon className={cn("size-4 text-muted", isActive && "text-accent-blue")} aria-hidden />
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
