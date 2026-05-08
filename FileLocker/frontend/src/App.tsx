import { useEffect, useRef, useState } from "react"
import { Bell, BookOpen } from "lucide-react"
import { toast } from "sonner"
import { AppShell } from "@/components/layout/AppShell"
import { PageHeader } from "@/components/layout/PageHeader"
import { Button } from "@/components/ui/button"
import { Toaster } from "@/components/ui/sonner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { invokeBridge, subscribeToBridgeEvents } from "@/services/bridge"
import { AboutPage } from "@/pages/AboutPage"
import { DashboardPage } from "@/pages/DashboardPage"
import { EncodeTextPage } from "@/pages/EncodeTextPage"
import { FileOperationPage, SecureDeletePage } from "@/pages/FileOperationPages"
import { HashFilesPage } from "@/pages/HashFilesPage"
import { MetadataScramblerPage } from "@/pages/MetadataScramblerPage"
import { SecurityGuidePage } from "@/pages/SecurityGuidePage"
import { SettingsPage } from "@/pages/SettingsPage"
import type { DashboardState, InitialState, PageKey, ProgressEvent, SettingsState, UpdateCheckResult } from "@/types/bridge"

const pageTitles: Record<PageKey, { title: string; description: string }> = {
  dashboard: {
    title: "Dashboard",
    description: "Recent local activity, shortcuts, and security status.",
  },
  encrypt: {
    title: "Encrypt Files",
    description: "Protect files with strong local encryption.",
  },
  decrypt: {
    title: "Decrypt Files",
    description: "Restore files from FileLocker .locked files.",
  },
  hash: {
    title: "Hash Files",
    description: "Generate and compare SHA fingerprints for local files.",
  },
  encode: {
    title: "Encode Text",
    description: "Convert text between common safe transport formats.",
  },
  metadata: {
    title: "Metadata Scrambler",
    description: "Preview metadata fields without modifying files.",
  },
  "secure-delete": {
    title: "Secure Delete",
    description: "Overwrite selected files where possible before removing them.",
  },
  settings: {
    title: "Settings",
    description: "Preferences, history privacy, update checks, and defaults.",
  },
  about: {
    title: "About",
    description: "Version, project details, and local protection model.",
  },
  "security-guide": {
    title: "Security Guide",
    description: "Practical handling guidance for local file protection.",
  },
}

export function App() {
  const [activePage, setActivePage] = useState<PageKey>(() => parseHashPage())
  const [initialState, setInitialState] = useState<InitialState | null>(null)
  const [dashboard, setDashboard] = useState<DashboardState | null>(null)
  const [settings, setSettings] = useState<SettingsState | null>(null)
  const [progressEvents, setProgressEvents] = useState<ProgressEvent[]>([])
  const [droppedPathsByPage, setDroppedPathsByPage] = useState<Partial<Record<PageKey, string[]>>>({})
  const activePageRef = useRef(activePage)

  useEffect(() => {
    const onHash = () => setActivePage(parseHashPage())
    window.addEventListener("hashchange", onHash)
    return () => window.removeEventListener("hashchange", onHash)
  }, [])

  useEffect(() => {
    activePageRef.current = activePage
  }, [activePage])

  useEffect(() => {
    invokeBridge<InitialState>("app.getInitialState")
      .then((state) => {
        setInitialState(state)
        setDashboard(state.dashboard)
        setSettings(state.settings)
      })
      .catch((error) => {
        toast.error(error instanceof Error ? error.message : "Unable to load FileLocker state.")
      })
  }, [])

  useEffect(() => {
    const unsubscribe = subscribeToBridgeEvents((event) => {
      if (event.type === "progress") {
        setProgressEvents((current) => [...current.slice(-30), event])
      }
      if (event.type === "droppedPaths") {
        const targetPage = acceptsDroppedPaths(activePageRef.current) ? activePageRef.current : "dashboard"
        setDroppedPathsByPage((current) => ({
          ...current,
          [targetPage]: [...(current[targetPage] ?? []), ...event.paths],
        }))
        if (targetPage === "dashboard" && activePageRef.current !== "dashboard") {
          navigate("dashboard")
        }
        toast.success(`${event.paths.length} item${event.paths.length === 1 ? "" : "s"} queued from drag and drop.`)
      }
    })
    return () => {
      unsubscribe()
    }
  }, [])

  const pageMeta = pageTitles[activePage]

  function navigate(page: PageKey) {
    window.location.hash = page
    setActivePage(page)
  }

  async function reveal(path: string) {
    try {
      await invokeBridge("files.revealPath", { path })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to open path.")
    }
  }

  function clearDroppedPaths(page: PageKey) {
    setDroppedPathsByPage((current) => ({
      ...current,
      [page]: [],
    }))
  }

  async function toggleDarkMode(enabled: boolean) {
    if (!settings) {
      return
    }

    const nextSettings: SettingsState = {
      ...settings,
      preferences: {
        ...settings.preferences,
        themePreference: enabled ? "Dark" : "Light",
      },
    }

    try {
      const response = await invokeBridge<SettingsState>("settings.save", nextSettings)
      setSettings(response)
      toast.success(enabled ? "Dark mode enabled." : "Light mode enabled.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to update theme.")
    }
  }

  async function checkNotifications() {
    try {
      const response = await invokeBridge<UpdateCheckResult>("updates.check")
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to check notifications.")
    }
  }

  if (!initialState || !dashboard || !settings) {
    return (
      <TooltipProvider>
        <AppShell activePage={activePage} onNavigate={navigate}>
          <PageHeader title="FileLocker" description="Preparing your local security tools." />
          <div className="security-page">
            <div className="border-y border-border py-4">
              <div className="security-label">Startup</div>
              <pre className="terminal-output mt-3 text-secondary">Loading FileLocker...</pre>
            </div>
          </div>
        </AppShell>
        <Toaster />
      </TooltipProvider>
    )
  }

  return (
    <TooltipProvider>
      <AppShell activePage={activePage} version={initialState.app.version} settings={settings} onNavigate={navigate} onThemeToggle={toggleDarkMode}>
        <PageHeader
          title={pageMeta.title}
          description={pageMeta.description}
          actions={
            <>
              <Button variant="secondary" onClick={() => navigate("security-guide")}>
                <BookOpen data-icon="inline-start" />
                Quick Start Guide
              </Button>
              <Button variant="ghost" size="icon" aria-label="Check notifications" onClick={checkNotifications}>
                <Bell className="size-5" aria-hidden />
              </Button>
            </>
          }
        />
        {activePage === "dashboard" ? <DashboardPage dashboard={dashboard} onNavigate={navigate} invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={setDashboard} onReveal={reveal} droppedPaths={droppedPathsByPage.dashboard ?? []} onDroppedPathsHandled={() => clearDroppedPaths("dashboard")} /> : null}
        {activePage === "encrypt" ? <FileOperationPage kind="encrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} droppedPaths={droppedPathsByPage.encrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("encrypt")} /> : null}
        {activePage === "decrypt" ? <FileOperationPage kind="decrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} droppedPaths={droppedPathsByPage.decrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("decrypt")} /> : null}
        {activePage === "hash" ? <HashFilesPage invoke={invokeBridge} onDashboardUpdate={setDashboard} droppedPaths={droppedPathsByPage.hash ?? []} onDroppedPathsHandled={() => clearDroppedPaths("hash")} /> : null}
        {activePage === "encode" ? <EncodeTextPage invoke={invokeBridge} /> : null}
        {activePage === "metadata" ? <MetadataScramblerPage invoke={invokeBridge} droppedPaths={droppedPathsByPage.metadata ?? []} onDroppedPathsHandled={() => clearDroppedPaths("metadata")} /> : null}
        {activePage === "secure-delete" ? <SecureDeletePage invoke={invokeBridge} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} droppedPaths={droppedPathsByPage["secure-delete"] ?? []} onDroppedPathsHandled={() => clearDroppedPaths("secure-delete")} /> : null}
        {activePage === "settings" ? <SettingsPage settings={settings} invoke={invokeBridge} onSettingsUpdate={setSettings} onDashboardUpdate={setDashboard} /> : null}
        {activePage === "about" ? <AboutPage app={initialState.app} onOpenRepository={() => invokeBridge("links.openExternal", { url: initialState.app.repositoryUrl })} /> : null}
        {activePage === "security-guide" ? <SecurityGuidePage /> : null}
      </AppShell>
      <Toaster />
    </TooltipProvider>
  )
}

function parseHashPage(): PageKey {
  const value = window.location.hash.replace(/^#/, "") as PageKey
  return value in pageTitles ? value : "dashboard"
}

function acceptsDroppedPaths(page: PageKey) {
  return page === "dashboard" || page === "encrypt" || page === "decrypt" || page === "hash" || page === "metadata" || page === "secure-delete"
}
