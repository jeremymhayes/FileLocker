import type { ReactNode } from "react"
import { Sidebar } from "@/components/layout/Sidebar"
import { WindowTitleBar } from "@/components/layout/WindowTitleBar"
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
    <div className="dark flex h-screen flex-col overflow-hidden bg-background text-primary">
      <WindowTitleBar />
      <div className="flex min-h-0 flex-1 overflow-hidden">
        <Sidebar activePage={activePage} onNavigate={onNavigate} version={version} settings={settings} onThemeToggle={onThemeToggle} isThemeToggleBusy={isThemeToggleBusy} />
        <main className="min-h-0 min-w-0 flex-1 overflow-y-auto bg-background">{children}</main>
      </div>
    </div>
  )
}
