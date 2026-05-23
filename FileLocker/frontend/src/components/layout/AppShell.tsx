import type { ReactNode } from "react"
import { Sidebar } from "@/components/layout/Sidebar"
import type { PageKey, SettingsState } from "@/types/bridge"

type AppShellProps = {
  activePage: PageKey
  version?: string
  settings?: SettingsState
  onNavigate: (page: PageKey) => void
  onThemeToggle?: (enabled: boolean) => void
  isThemeToggleBusy?: boolean
  children: ReactNode
}

export function AppShell({ activePage, version, settings, onNavigate, onThemeToggle, isThemeToggleBusy = false, children }: AppShellProps) {
  return (
    <div className="dark h-screen overflow-hidden bg-background text-primary">
      <div className="flex h-screen overflow-hidden">
        <Sidebar activePage={activePage} onNavigate={onNavigate} version={version} settings={settings} onThemeToggle={onThemeToggle} isThemeToggleBusy={isThemeToggleBusy} />
        <main className="h-screen min-w-0 flex-1 overflow-y-auto bg-background">{children}</main>
      </div>
    </div>
  )
}
