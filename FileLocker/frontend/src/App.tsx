import { useEffect, useRef, useState } from "react"
import { Bell, Download } from "lucide-react"
import { toast } from "sonner"
import { AppShell } from "@/components/layout/AppShell"
import { PageHeader } from "@/components/layout/PageHeader"
import { Button } from "@/components/ui/button"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogMedia,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
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
import { AppManagerPage, CustomCleanPage, DriveOptimizerPage, PartitionCleanerPage, RegistryFixerPage, StartupManagerPage } from "@/pages/SystemMaintenancePages"
import type { DashboardState, InitialState, PageKey, ProgressEvent, SettingsState, UpdateCheckResult } from "@/types/bridge"

const pageTitles: Record<PageKey, { title: string; description?: string }> = {
  dashboard: { title: "Dashboard" },
  encrypt: { title: "Encrypt Files" },
  decrypt: { title: "Decrypt Files" },
  hash: { title: "Hash Files" },
  encode: { title: "Encode Text" },
  metadata: { title: "Metadata Scrambler" },
  "secure-delete": { title: "Secure Delete" },
  "custom-clean": { title: "Custom Clean" },
  "partition-cleaner": { title: "Partition Cleaner" },
  "drive-optimizer": { title: "Drive Optimizer" },
  "registry-fixer": { title: "Registry Fixer" },
  "startup-manager": { title: "Startup Manager" },
  "app-manager": { title: "App Manager" },
  settings: { title: "Settings" },
  about: { title: "About" },
  "security-guide": { title: "Security Guide" },
}

const startupUpdateCheckIntervalMs = 24 * 60 * 60 * 1000

export function App() {
  const [activePage, setActivePage] = useState<PageKey>(() => parseHashPage())
  const [initialState, setInitialState] = useState<InitialState | null>(null)
  const [dashboard, setDashboard] = useState<DashboardState | null>(null)
  const [settings, setSettings] = useState<SettingsState | null>(null)
  const [progressEvents, setProgressEvents] = useState<ProgressEvent[]>([])
  const [droppedPathsByPage, setDroppedPathsByPage] = useState<Partial<Record<PageKey, string[]>>>({})
  const [startupUpdate, setStartupUpdate] = useState<UpdateCheckResult | null>(null)
  const [isInstallingStartupUpdate, setIsInstallingStartupUpdate] = useState(false)
  const [isSkippingStartupUpdate, setIsSkippingStartupUpdate] = useState(false)
  const [isCheckingNotifications, setIsCheckingNotifications] = useState(false)
  const [isSavingTheme, setIsSavingTheme] = useState(false)
  const [startupError, setStartupError] = useState("")
  const activePageRef = useRef(activePage)
  const startupUpdateCheckStartedRef = useRef(false)

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
        setStartupError("")
        setInitialState(state)
        setDashboard(state.dashboard)
        setSettings(state.settings)
        const launchPage = parseLaunchPage(state.app.launchAction)
        if (launchPage) {
          navigate(launchPage)
        }
      })
      .catch((error) => {
        const message = error instanceof Error ? error.message : "Unable to load FileLocker state."
        setStartupError(message)
        toast.error(message)
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
      if (event.type === "dropError") {
        toast.error(event.message || "Drag and drop failed.")
      }
    })
    return () => {
      unsubscribe()
    }
  }, [])

  useEffect(() => {
    if (!shouldRunStartupUpdateCheck(settings) || startupUpdateCheckStartedRef.current) {
      return
    }

    startupUpdateCheckStartedRef.current = true
    invokeBridge<UpdateCheckResult>("updates.check")
      .then((response) => {
        if (response.isUpdateAvailable && response.release) {
          setStartupUpdate(response)
        }
      })
      .catch((error) => {
        toast.error(error instanceof Error ? error.message : "Unable to check for updates.")
      })
  }, [settings])

  const pageMeta = pageTitles[activePage]
  const showSharedPageHeader = activePage !== "settings" && activePage !== "metadata" && activePage !== "encrypt" && activePage !== "decrypt" && activePage !== "hash" && activePage !== "encode" && activePage !== "secure-delete"

  useEffect(() => {
    void invokeBridge("app.setTitlePage", { pageName: pageMeta.title }).catch(() => undefined)
  }, [pageMeta.title])

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
    if (!settings || isSavingTheme) {
      return
    }

    const nextSettings: SettingsState = {
      ...settings,
      preferences: {
        ...settings.preferences,
        themePreference: enabled ? "Dark" : "Light",
      },
    }

    setIsSavingTheme(true)
    try {
      const response = await invokeBridge<SettingsState>("settings.save", nextSettings)
      setSettings(response)
      toast.success(enabled ? "Dark mode enabled." : "Light mode enabled.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to update theme.")
    } finally {
      setIsSavingTheme(false)
    }
  }

  async function checkNotifications() {
    if (isCheckingNotifications) {
      return
    }

    setIsCheckingNotifications(true)
    try {
      const response = await invokeBridge<UpdateCheckResult>("updates.check")
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to check notifications.")
    } finally {
      setIsCheckingNotifications(false)
    }
  }

  async function installStartupUpdate() {
    if (!startupUpdate?.release || isInstallingStartupUpdate) {
      return
    }

    setIsInstallingStartupUpdate(true)
    try {
      await invokeBridge("updates.install")
      toast.success("Launching the FileLocker installer.")
    } catch (error) {
      setIsInstallingStartupUpdate(false)
      toast.error(error instanceof Error ? error.message : "Unable to install the update.")
    }
  }

  async function skipStartupUpdate() {
    if (isSkippingStartupUpdate || isInstallingStartupUpdate) {
      return
    }

    if (!startupUpdate?.release) {
      setStartupUpdate(null)
      return
    }

    setIsSkippingStartupUpdate(true)
    try {
      const response = await invokeBridge<SettingsState>("updates.skip", { version: startupUpdate.release.displayVersion })
      setSettings(response)
      setStartupUpdate(null)
      toast.message(`Skipped FileLocker ${startupUpdate.release.displayVersion}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to skip this update.")
    } finally {
      setIsSkippingStartupUpdate(false)
    }
  }

  async function restartAsAdministrator(targetPage: PageKey) {
    try {
      await invokeBridge("app.restartAsAdministrator", { targetPage })
      toast.message("Approve the Windows administrator prompt to continue.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to restart as administrator.")
    }
  }

  if (!initialState || !dashboard || !settings) {
    return (
      <TooltipProvider>
        <AppShell activePage={activePage} onNavigate={navigate}>
          <PageHeader title="FileLocker" />
          <div className="security-page">
            <div className="border-y border-border py-4">
              <div className="security-label">{startupError ? "Startup issue" : "Startup"}</div>
              <pre className="terminal-output mt-3 text-secondary">
                {startupError || "Loading FileLocker..."}
              </pre>
            </div>
          </div>
        </AppShell>
        <Toaster />
      </TooltipProvider>
    )
  }

  return (
    <TooltipProvider>
      <AppShell activePage={activePage} version={initialState.app.version} settings={settings} onNavigate={navigate} onThemeToggle={toggleDarkMode} isThemeToggleBusy={isSavingTheme}>
        {showSharedPageHeader ? (
          <PageHeader
            title={pageMeta.title}
            description={pageMeta.description}
            actions={
              <>
                <Button variant="ghost" size="icon" aria-label="Check notifications" onClick={checkNotifications} disabled={isCheckingNotifications}>
                  <Bell className="size-5" aria-hidden />
                </Button>
              </>
            }
          />
        ) : null}
        {activePage === "dashboard" ? <DashboardPage dashboard={dashboard} onNavigate={navigate} invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={setDashboard} onReveal={reveal} privacyModeEnabled={settings.preferences.incognitoMode} droppedPaths={droppedPathsByPage.dashboard ?? []} onDroppedPathsHandled={() => clearDroppedPaths("dashboard")} /> : null}
        {activePage === "encrypt" ? <FileOperationPage kind="encrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} droppedPaths={droppedPathsByPage.encrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("encrypt")} /> : null}
        {activePage === "decrypt" ? <FileOperationPage kind="decrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} droppedPaths={droppedPathsByPage.decrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("decrypt")} /> : null}
        {activePage === "hash" ? <HashFilesPage invoke={invokeBridge} onDashboardUpdate={setDashboard} dashboard={dashboard} droppedPaths={droppedPathsByPage.hash ?? []} onDroppedPathsHandled={() => clearDroppedPaths("hash")} /> : null}
        {activePage === "encode" ? <EncodeTextPage invoke={invokeBridge} /> : null}
        {activePage === "metadata" ? <MetadataScramblerPage invoke={invokeBridge} droppedPaths={droppedPathsByPage.metadata ?? []} onDroppedPathsHandled={() => clearDroppedPaths("metadata")} /> : null}
        {activePage === "secure-delete" ? <SecureDeletePage invoke={invokeBridge} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} droppedPaths={droppedPathsByPage["secure-delete"] ?? []} onDroppedPathsHandled={() => clearDroppedPaths("secure-delete")} /> : null}
        {activePage === "partition-cleaner" ? <PartitionCleanerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("partition-cleaner")} /> : null}
        {activePage === "drive-optimizer" ? <DriveOptimizerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("drive-optimizer")} /> : null}
        {activePage === "custom-clean" ? <CustomCleanPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("custom-clean")} /> : null}
        {activePage === "registry-fixer" ? <RegistryFixerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("registry-fixer")} /> : null}
        {activePage === "startup-manager" ? <StartupManagerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("startup-manager")} /> : null}
        {activePage === "app-manager" ? <AppManagerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("app-manager")} /> : null}
        {activePage === "settings" ? <SettingsPage app={initialState.app} settings={settings} invoke={invokeBridge} onSettingsUpdate={setSettings} onDashboardUpdate={setDashboard} /> : null}
        {activePage === "about" ? <AboutPage app={initialState.app} onOpenRepository={() => invokeBridge("links.openExternal", { url: initialState.app.repositoryUrl })} /> : null}
        {activePage === "security-guide" ? <SecurityGuidePage /> : null}
      </AppShell>
      <AlertDialog
        open={Boolean(startupUpdate?.isUpdateAvailable && startupUpdate.release)}
        onOpenChange={(open) => {
          if (!open && !isInstallingStartupUpdate && !isSkippingStartupUpdate) {
            setStartupUpdate(null)
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogMedia>
              <Download className="size-4" />
            </AlertDialogMedia>
            <AlertDialogTitle>FileLocker {startupUpdate?.release?.displayVersion} is available</AlertDialogTitle>
            <AlertDialogDescription>
              You are running {startupUpdate?.currentVersion}. Download and install the update now?
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="max-h-48 overflow-y-auto rounded-md border border-border/70 bg-bg-surface/45 p-3 text-sm leading-[1.55] text-secondary">
            {startupUpdate?.release?.notes?.trim() || startupUpdate?.statusMessage || "No release notes were provided for this release."}
          </div>
          <AlertDialogFooter>
            <Button variant="ghost" onClick={() => void skipStartupUpdate()} disabled={isInstallingStartupUpdate || isSkippingStartupUpdate}>
              {isSkippingStartupUpdate ? "Skipping" : "Skip Version"}
            </Button>
            <AlertDialogCancel disabled={isInstallingStartupUpdate || isSkippingStartupUpdate} onClick={() => setStartupUpdate(null)}>
              Later
            </AlertDialogCancel>
            <Button onClick={() => void installStartupUpdate()} disabled={isInstallingStartupUpdate || isSkippingStartupUpdate}>
              <Download data-icon="inline-start" />
              {isInstallingStartupUpdate ? "Launching" : "Download and Install"}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <Toaster />
    </TooltipProvider>
  )
}

function shouldRunStartupUpdateCheck(settings: SettingsState | null) {
  if (!settings?.updates.autoCheckEnabled) {
    return false
  }

  const lastCheckedUtc = settings.updates.lastCheckedUtc
  if (!lastCheckedUtc) {
    return true
  }

  const lastCheckedMs = Date.parse(lastCheckedUtc)
  if (Number.isNaN(lastCheckedMs)) {
    return true
  }

  const elapsedMs = Date.now() - lastCheckedMs
  return elapsedMs < 0 || elapsedMs >= startupUpdateCheckIntervalMs
}

function parseHashPage(): PageKey {
  const value = window.location.hash.replace(/^#/, "")
  return isPageKey(value) ? value : "dashboard"
}

function parseLaunchPage(action?: string): PageKey | null {
  const prefix = "--page="
  if (!action?.startsWith(prefix)) {
    return null
  }

  const value = action.slice(prefix.length)
  return isPageKey(value) ? value : null
}

function acceptsDroppedPaths(page: PageKey) {
  return page === "dashboard" || page === "encrypt" || page === "decrypt" || page === "hash" || page === "metadata" || page === "secure-delete"
}

function isPageKey(value: string): value is PageKey {
  return Object.prototype.hasOwnProperty.call(pageTitles, value)
}
