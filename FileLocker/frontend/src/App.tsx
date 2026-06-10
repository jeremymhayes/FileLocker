import { useEffect, useRef, useState } from "react"
import { Bell, ShieldAlert, ShieldCheck } from "lucide-react"
import { toast } from "sonner"
import { AppShell } from "@/components/layout/AppShell"
import { PageHeader } from "@/components/layout/PageHeader"
import { Button } from "@/components/ui/button"
import { UpdateDialog } from "@/components/common/UpdateDialog"
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
import { isSafeLocalPathForReveal, mergeUniquePaths } from "@/lib/format"
import type { DashboardState, EncryptionAlgorithmOption, FreeSpaceWipeStatus, InitialState, PageKey, ProgressEvent, SettingsState, UpdateCheckResult } from "@/types/bridge"

const pageTitles: Record<PageKey, { title: string; description?: string }> = {
  dashboard: { title: "Home" },
  encrypt: { title: "Encrypt Files" },
  decrypt: { title: "Decrypt Files" },
  hash: { title: "Hash Files" },
  encode: { title: "Encode Text" },
  metadata: { title: "Metadata Scrambler" },
  "secure-delete": { title: "Secure Delete" },
  "custom-clean": { title: "Custom Clean", description: "Remove unnecessary files and free up disk space." },
  "partition-cleaner": { title: "Partition Cleaner" },
  "drive-optimizer": { title: "Drive Optimizer" },
  "registry-fixer": { title: "Registry Fixer", description: "Scan and fix registry issues to improve system stability and performance." },
  "startup-manager": { title: "Startup Manager", description: "Manage programs that run automatically when Windows starts." },
  "app-manager": { title: "App Manager", description: "View installed apps, sort by size or publisher, and remove apps you no longer need." },
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
  const [freeSpaceWipeStatus, setFreeSpaceWipeStatus] = useState<FreeSpaceWipeStatus | null>(null)
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
        setProgressEvents((current) => [...current.slice(-29), event])
      }
      if (event.type === "droppedPaths") {
        const queuedPaths = mergeUniquePaths([], Array.isArray(event.paths) ? event.paths : [])
        if (queuedPaths.length === 0) {
          toast.error("No supported file paths were dropped.")
          return
        }

        const targetPage = acceptsDroppedPaths(activePageRef.current) ? activePageRef.current : "dashboard"
        setDroppedPathsByPage((current) => ({
          ...current,
          [targetPage]: mergeUniquePaths(current[targetPage] ?? [], queuedPaths),
        }))
        if (targetPage === "dashboard" && activePageRef.current !== "dashboard") {
          navigate("dashboard")
        }
        toast.success(`${queuedPaths.length} item${queuedPaths.length === 1 ? "" : "s"} queued from drag and drop.`)
      }
      if (event.type === "dropError") {
        toast.error(event.message || "Drag and drop failed.")
      }
      if (event.type === "updateAvailable") {
        if (event.result.isUpdateAvailable && event.result.release) {
          setStartupUpdate(event.result)
        }
      }
      if (event.type === "maintenanceWipeStatus") {
        setFreeSpaceWipeStatus(event.status)
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
      const targetPath = path.trim()
      if (!isSafeLocalPathForReveal(targetPath)) {
        toast.error("FileLocker can only reveal normal local file or folder paths.")
        return
      }

      await invokeBridge("files.revealPath", { path: targetPath })
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
              activePage === "startup-manager" ? (
                <Button
                  variant={initialState.app.isAdministrator ? "secondary" : "outline"}
                  size="sm"
                  onClick={() => !initialState.app.isAdministrator && void restartAsAdministrator("startup-manager")}
                  disabled={initialState.app.isAdministrator}
                >
                  {initialState.app.isAdministrator ? <ShieldCheck data-icon="inline-start" /> : <ShieldAlert data-icon="inline-start" />}
                  {initialState.app.isAdministrator ? "Administrator Mode" : "Restart as Administrator"}
                </Button>
              ) : activePage === "app-manager" || activePage === "custom-clean" ? null : (
                <Button variant="ghost" size="icon" aria-label="Check notifications" onClick={checkNotifications} disabled={isCheckingNotifications}>
                  <Bell className="size-5" aria-hidden />
                </Button>
              )
            }
          />
        ) : null}
        {activePage === "dashboard" ? <DashboardPage dashboard={dashboard} onNavigate={navigate} invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={setDashboard} onReveal={reveal} privacyModeEnabled={settings.preferences.incognitoMode} encryptionAlgorithms={initialState.app.encryptionAlgorithms as EncryptionAlgorithmOption[]} droppedPaths={droppedPathsByPage.dashboard ?? []} onDroppedPathsHandled={() => clearDroppedPaths("dashboard")} /> : null}
        {activePage === "encrypt" ? <FileOperationPage kind="encrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} encryptionAlgorithms={initialState.app.encryptionAlgorithms as EncryptionAlgorithmOption[]} droppedPaths={droppedPathsByPage.encrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("encrypt")} /> : null}
        {activePage === "decrypt" ? <FileOperationPage kind="decrypt" invoke={invokeBridge} progressEvents={progressEvents} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} encryptionAlgorithms={initialState.app.encryptionAlgorithms as EncryptionAlgorithmOption[]} droppedPaths={droppedPathsByPage.decrypt ?? []} onDroppedPathsHandled={() => clearDroppedPaths("decrypt")} /> : null}
        {activePage === "hash" ? <HashFilesPage invoke={invokeBridge} onDashboardUpdate={setDashboard} dashboard={dashboard} droppedPaths={droppedPathsByPage.hash ?? []} onDroppedPathsHandled={() => clearDroppedPaths("hash")} /> : null}
        {activePage === "encode" ? <EncodeTextPage invoke={invokeBridge} /> : null}
        {activePage === "metadata" ? <MetadataScramblerPage invoke={invokeBridge} droppedPaths={droppedPathsByPage.metadata ?? []} onDroppedPathsHandled={() => clearDroppedPaths("metadata")} /> : null}
        {activePage === "secure-delete" ? <SecureDeletePage invoke={invokeBridge} onDashboardUpdate={(value) => setDashboard(value as DashboardState)} onReveal={reveal} dashboard={dashboard} droppedPaths={droppedPathsByPage["secure-delete"] ?? []} onDroppedPathsHandled={() => clearDroppedPaths("secure-delete")} /> : null}
        {activePage === "partition-cleaner" ? <PartitionCleanerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("partition-cleaner")} wipeStatus={freeSpaceWipeStatus} onWipeStatusChange={setFreeSpaceWipeStatus} /> : null}
        {activePage === "drive-optimizer" ? <DriveOptimizerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("drive-optimizer")} /> : null}
        {activePage === "custom-clean" ? <CustomCleanPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("custom-clean")} /> : null}
        {activePage === "registry-fixer" ? <RegistryFixerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("registry-fixer")} /> : null}
        {activePage === "startup-manager" ? <StartupManagerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("startup-manager")} /> : null}
        {activePage === "app-manager" ? <AppManagerPage invoke={invokeBridge} isAdministrator={initialState.app.isAdministrator} onRestartAsAdministrator={() => void restartAsAdministrator("app-manager")} /> : null}
        {activePage === "settings" ? <SettingsPage app={initialState.app} settings={settings} invoke={invokeBridge} onSettingsUpdate={setSettings} onDashboardUpdate={setDashboard} /> : null}
        {activePage === "about" ? <AboutPage app={initialState.app} onOpenRepository={() => invokeBridge("links.openExternal", { url: initialState.app.repositoryUrl })} /> : null}
        {activePage === "security-guide" ? <SecurityGuidePage encryptionAlgorithms={initialState.app.encryptionAlgorithms as EncryptionAlgorithmOption[]} /> : null}
      </AppShell>
      <UpdateDialog
        update={startupUpdate}
        isInstalling={isInstallingStartupUpdate}
        isSkipping={isSkippingStartupUpdate}
        onInstall={() => void installStartupUpdate()}
        onSkip={() => void skipStartupUpdate()}
        onLater={() => setStartupUpdate(null)}
        onOpenChange={(open) => {
          if (!open && !isInstallingStartupUpdate && !isSkippingStartupUpdate) {
            setStartupUpdate(null)
          }
        }}
      />
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
