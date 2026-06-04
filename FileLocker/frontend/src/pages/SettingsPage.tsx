import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react"
import {
  AlertTriangle,
  ArrowUpRight,
  Download,
  EyeOff,
  FolderCog,
  FolderOpen,
  GitBranch,
  Info,
  Loader2,
  MonitorCog,
  Paintbrush,
  RefreshCw,
  RotateCcw,
  Save,
  ShieldCheck,
  Trash2,
  type LucideIcon,
} from "lucide-react"
import { toast } from "sonner"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { cn } from "@/lib/utils"
import { OUTPUT_TIMESTAMP_POLICIES, normalizeOutputTimestampPolicy } from "@/lib/outputTimestampPolicies"
import type { DashboardState, InitialState, SettingsState, UpdateCheckResult, UpdateRelease } from "@/types/bridge"

type SettingsPageProps = {
  app: InitialState["app"]
  settings: SettingsState
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onSettingsUpdate: (settings: SettingsState) => void
  onDashboardUpdate: (dashboard: DashboardState) => void
}

type SettingsTab = "general" | "files" | "privacy" | "updates" | "integration" | "about"
type HistoryAction = "json" | "csv" | "clear"
type UpdateAction = "check" | "download" | "install" | "skip" | "clear-skip" | "test-dialog" | "test-startup"

const tabs: Array<{ key: SettingsTab; label: string; icon: LucideIcon }> = [
  { key: "general", label: "General", icon: Paintbrush },
  { key: "files", label: "Files", icon: FolderCog },
  { key: "privacy", label: "Privacy", icon: EyeOff },
  { key: "updates", label: "Updates", icon: RefreshCw },
  { key: "integration", label: "Integration", icon: MonitorCog },
  { key: "about", label: "About", icon: Info },
]

const settingsTabId = (key: SettingsTab) => `settings-tab-${key}`
const settingsPanelId = (key: SettingsTab) => `settings-panel-${key}`
const initialTabRefs: Record<SettingsTab, HTMLButtonElement | null> = {
  general: null,
  files: null,
  privacy: null,
  updates: null,
  integration: null,
  about: null,
}

export function SettingsPage({ app, settings, invoke, onSettingsUpdate, onDashboardUpdate }: SettingsPageProps) {
  const [activeTab, setActiveTab] = useState<SettingsTab>("general")
  const [draft, setDraft] = useState(settings)
  const [hasUnsavedChanges, setHasUnsavedChanges] = useState(false)
  const [isLoadingSettings, setIsLoadingSettings] = useState(false)
  const [isSavingSettings, setIsSavingSettings] = useState(false)
  const [isResettingSettings, setIsResettingSettings] = useState(false)
  const [settingsLoadError, setSettingsLoadError] = useState("")
  const [settingsActionError, setSettingsActionError] = useState("")
  const [updateStatus, setUpdateStatus] = useState("Not checked")
  const [availableRelease, setAvailableRelease] = useState<UpdateRelease | null>(null)
  const [downloadedInstallerPath, setDownloadedInstallerPath] = useState("")
  const [updaterTestStatus, setUpdaterTestStatus] = useState("")
  const [historyAction, setHistoryAction] = useState<HistoryAction | null>(null)
  const [updateAction, setUpdateAction] = useState<UpdateAction | null>(null)
  const [isUpdatingExplorerIntegration, setIsUpdatingExplorerIntegration] = useState(false)
  const tabRefs = useRef<Record<SettingsTab, HTMLButtonElement | null>>({ ...initialTabRefs })
  const dirtyRef = useRef(false)
  const draftRef = useRef(settings)

  useEffect(() => {
    draftRef.current = draft
  }, [draft])

  useEffect(() => {
    if (!dirtyRef.current) {
      setDraft(settings)
      draftRef.current = settings
    }
  }, [settings])

  useEffect(() => {
    let mounted = true
    setIsLoadingSettings(true)
    setSettingsLoadError("")

    invoke<SettingsState>("settings.get")
      .then((loaded) => {
        if (!mounted) {
          return
        }

        setSettingsLoadError("")
        applySettingsResponse(loaded)
      })
      .catch((error) => {
        if (!mounted) {
          return
        }

        const message = getErrorMessage(error, "Settings load failed.")
        setSettingsLoadError(message)
        toast.error(message)
      })
      .finally(() => {
        if (mounted) {
          setIsLoadingSettings(false)
        }
      })

    return () => {
      mounted = false
    }
  }, [invoke])

  function updateDraft(updater: (current: SettingsState) => SettingsState) {
    dirtyRef.current = true
    setHasUnsavedChanges(true)
    setDraft((current) => {
      const next = updater(current)
      draftRef.current = next
      return next
    })
  }

  function handleTabKeyDown(event: KeyboardEvent<HTMLButtonElement>, key: SettingsTab) {
    const currentIndex = tabs.findIndex((tab) => tab.key === key)
    if (currentIndex < 0) {
      return
    }

    let nextIndex: number | null = null
    switch (event.key) {
      case "ArrowRight":
        nextIndex = (currentIndex + 1) % tabs.length
        break
      case "ArrowLeft":
        nextIndex = (currentIndex - 1 + tabs.length) % tabs.length
        break
      case "Home":
        nextIndex = 0
        break
      case "End":
        nextIndex = tabs.length - 1
        break
      default:
        return
    }

    event.preventDefault()
    const nextTab = tabs[nextIndex]
    setActiveTab(nextTab.key)
    window.requestAnimationFrame(() => {
      const nextTabButton = tabRefs.current[nextTab.key]
      nextTabButton?.focus()
      nextTabButton?.scrollIntoView({ block: "nearest", inline: "nearest" })
    })
  }

  function applySettingsResponse(response: SettingsState) {
    dirtyRef.current = false
    setHasUnsavedChanges(false)
    setSettingsLoadError("")
    setSettingsActionError("")
    setDraft(response)
    draftRef.current = response
    onSettingsUpdate(response)
  }

  function validateOutputPreferences(candidate: SettingsState) {
    const preferences = candidate.preferences
    if (preferences.useCustomEncryptOutputDirectory && preferences.customEncryptOutputDirectory.trim().length === 0) {
      return "Choose an encrypt output folder or turn off custom encrypt output."
    }

    if (preferences.useCustomDecryptOutputDirectory && preferences.customDecryptOutputDirectory.trim().length === 0) {
      return "Choose a decrypt output folder or turn off custom decrypt output."
    }

    return ""
  }

  function normalizeSettingsDraft(candidate: SettingsState): SettingsState {
    return {
      ...candidate,
      preferences: {
        ...candidate.preferences,
        outputTimestampPolicy: normalizeOutputTimestampPolicy(candidate.preferences.outputTimestampPolicy),
      },
    }
  }

  async function save() {
    if (!hasUnsavedChanges || isLoadingSettings || isSavingSettings || isResettingSettings) {
      return
    }

    const candidate = normalizeSettingsDraft(draftRef.current)
    const validationMessage = validateOutputPreferences(candidate)
    if (validationMessage) {
      setActiveTab("files")
      setSettingsActionError(validationMessage)
      toast.error(validationMessage)
      return
    }

    setIsSavingSettings(true)
    setSettingsActionError("")
    try {
      const response = await invoke<SettingsState>("settings.save", candidate)
      applySettingsResponse(response)
      let historyClearFailed = false

      if (response.preferences.incognitoMode) {
        try {
          const cleared = await invoke<{ dashboard: DashboardState }>("history.clear")
          onDashboardUpdate(cleared.dashboard)
        } catch (error) {
          historyClearFailed = true
          toast.error(error instanceof Error ? `Settings saved, but history could not be cleared: ${error.message}` : "Settings saved, but history could not be cleared.")
        }
      }

      if (!historyClearFailed) {
        toast.success("Settings saved.")
      }
    } catch (error) {
      const message = getErrorMessage(error, "Settings save failed.")
      setSettingsActionError(message)
      toast.error(message)
    } finally {
      setIsSavingSettings(false)
    }
  }

  async function reset() {
    if (isLoadingSettings || isSavingSettings || isResettingSettings) {
      return
    }

    setIsResettingSettings(true)
    setSettingsActionError("")
    try {
      const response = await invoke<SettingsState>("settings.reset")
      applySettingsResponse(response)
      toast.success("Settings reset.")
    } catch (error) {
      const message = getErrorMessage(error, "Settings reset failed.")
      setSettingsActionError(message)
      toast.error(message)
    } finally {
      setIsResettingSettings(false)
    }
  }

  async function pickOutputFolder(target: "encrypt" | "decrypt") {
    try {
      const response = await invoke<{ path: string }>("files.pickFolder")
      if (!response.path) {
        return
      }

      updateDraft((current) => ({
        ...current,
        preferences: {
          ...current.preferences,
          ...(target === "encrypt"
            ? { customEncryptOutputDirectory: response.path, useCustomEncryptOutputDirectory: true }
            : { customDecryptOutputDirectory: response.path, useCustomDecryptOutputDirectory: true }),
        },
      }))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Folder selection failed.")
    }
  }

  async function checkUpdates() {
    if (updateAction) {
      return
    }

    setUpdateAction("check")
    try {
      const response = await invoke<UpdateCheckResult>("updates.check")
      setUpdateStatus(response.statusMessage)
      setAvailableRelease(response.isUpdateAvailable ? response.release ?? null : null)
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      const message = getErrorMessage(error, "Update check failed.")
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setUpdateAction(null)
    }
  }

  async function downloadUpdate() {
    if (!availableRelease || updateAction) {
      return
    }

    setUpdateAction("download")
    try {
      const response = await invoke<{ installerPath: string; fileName: string }>("updates.download")
      setDownloadedInstallerPath(response.installerPath)
      setUpdateStatus(`Downloaded ${response.fileName}`)
      toast.success(`Downloaded ${response.fileName}.`)
    } catch (error) {
      const message = getErrorMessage(error, "Update download failed.")
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setUpdateAction(null)
    }
  }

  async function installUpdate() {
    if (!availableRelease || updateAction) {
      return
    }

    setUpdateAction("install")
    try {
      const response = await invoke<{ installerPath: string; fileName: string }>("updates.install")
      setDownloadedInstallerPath(response.installerPath)
      setUpdateStatus(`Launching ${response.fileName}`)
      toast.success(`Launching ${response.fileName}. FileLocker will close while the installer updates the app.`)
    } catch (error) {
      const message = getErrorMessage(error, "Update install failed.")
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setUpdateAction(null)
    }
  }

  async function skipUpdate() {
    if (!availableRelease || updateAction) {
      return
    }

    setUpdateAction("skip")
    try {
      const response = await invoke<SettingsState>("updates.skip", { version: availableRelease.displayVersion })
      applySettingsResponse(response)
      setAvailableRelease(null)
      setUpdateStatus(`Skipped ${availableRelease.displayVersion}`)
      toast.message(`Skipped version ${availableRelease.displayVersion}.`)
    } catch (error) {
      const message = getErrorMessage(error, "Update skip failed.")
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setUpdateAction(null)
    }
  }

  async function clearSkippedUpdate() {
    if (updateAction) {
      return
    }

    setUpdateAction("clear-skip")
    try {
      const response = await invoke<SettingsState>("updates.clearSkip")
      applySettingsResponse(response)
      setUpdateStatus("Skipped version cleared")
      toast.success("Skipped update cleared.")
    } catch (error) {
      const message = getErrorMessage(error, "Skipped update could not be cleared.")
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setUpdateAction(null)
    }
  }

  async function testUpdateDialog() {
    if (updateAction) {
      return
    }

    setUpdateAction("test-dialog")
    try {
      setUpdaterTestStatus("Opening mock update popup")
      await invoke<{ tested: boolean }>("updates.testDialog")
      setUpdaterTestStatus("Mock update popup completed")
      toast.success("Mock update popup completed.")
    } catch (error) {
      setUpdaterTestStatus("Mock update popup failed")
      toast.error(error instanceof Error ? error.message : "Mock update popup failed.")
    } finally {
      setUpdateAction(null)
    }
  }

  async function testStartupUpdateCheck() {
    if (updateAction) {
      return
    }

    setUpdateAction("test-startup")
    try {
      setUpdaterTestStatus("Running startup update check")
      const response = await invoke<UpdateCheckResult>("updates.testStartupCheck")
      setUpdateStatus(response.statusMessage)
      setAvailableRelease(response.isUpdateAvailable ? response.release ?? null : null)
      setUpdaterTestStatus(response.statusMessage)
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      setUpdaterTestStatus("Startup update check failed")
      toast.error(error instanceof Error ? error.message : "Startup update check failed.")
    } finally {
      setUpdateAction(null)
    }
  }

  async function revealDownloadedInstaller() {
    if (!downloadedInstallerPath) {
      return
    }

    try {
      await invoke("files.revealPath", { path: downloadedInstallerPath })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not open the update installer folder.")
    }
  }

  async function exportHistory(format: "json" | "csv") {
    if (historyAction) {
      return
    }

    setHistoryAction(format)
    try {
      const response = await invoke<{ exportPath: string; fileName: string; recordCount: number }>("history.export", { format })
      toast.success(`Exported ${response.recordCount} record(s) to ${response.fileName}.`)
      try {
        await invoke("files.revealPath", { path: response.exportPath })
      } catch (error) {
        toast.error(error instanceof Error ? `History exported, but FileLocker could not reveal it: ${error.message}` : "History exported, but FileLocker could not reveal it.")
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "History export failed.")
    } finally {
      setHistoryAction(null)
    }
  }

  async function clearHistory() {
    if (historyAction) {
      return
    }

    setHistoryAction("clear")
    try {
      const response = await invoke<{ dashboard: DashboardState }>("history.clear")
      onDashboardUpdate(response.dashboard)
      toast.success("History cleared.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "History clear failed.")
    } finally {
      setHistoryAction(null)
    }
  }

  async function setExplorerIntegration(enabled: boolean) {
    if (isUpdatingExplorerIntegration) {
      return
    }

    setIsUpdatingExplorerIntegration(true)
    try {
      const response = await invoke<SettingsState>("shell.setExplorerIntegration", { enabled })
      applySettingsResponse(response)
      toast.success(enabled ? "Explorer integration installed." : "Explorer integration removed.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Explorer integration update failed.")
    } finally {
      setIsUpdatingExplorerIntegration(false)
    }
  }

  async function openExternal(url: string, fallbackMessage: string) {
    try {
      await invoke("links.openExternal", { url })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : fallbackMessage)
    }
  }

  const explorer = draft.explorerIntegration
  const explorerStatus = isUpdatingExplorerIntegration ? "Updating Explorer integration..." : explorer.statusMessage
  const settingsBusy = isLoadingSettings || isSavingSettings || isResettingSettings
  const settingsError = settingsLoadError || settingsActionError
  const updateBusy = Boolean(updateAction)

  return (
    <div className="security-page">
      <div className="border-y border-border bg-transparent">
        <header className="sticky top-0 z-20 flex flex-col gap-3 border-b border-border bg-background/95 py-3 backdrop-blur supports-[backdrop-filter]:bg-background/85 xl:flex-row xl:items-center xl:justify-between">
          <div className="min-w-0">
            <h1 className="font-display text-lg font-semibold leading-tight text-primary">Settings</h1>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {isLoadingSettings ? (
              <span className="inline-flex items-center gap-2 rounded-md border border-border bg-bg-dropzone px-3 py-1.5 text-xs text-secondary">
                <Loader2 className="size-3.5 animate-spin" aria-hidden />
                Loading
              </span>
            ) : null}
            {hasUnsavedChanges ? <span className="rounded-md border border-accent/40 bg-accent/10 px-3 py-1.5 text-xs font-semibold uppercase tracking-wider text-accent">Unsaved</span> : null}
            <Button variant="secondary" size="sm" onClick={reset} disabled={settingsBusy}>
              {isResettingSettings ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <RotateCcw data-icon="inline-start" />}
              {isResettingSettings ? "Resetting" : "Reset"}
            </Button>
            <Button size="sm" onClick={save} disabled={!hasUnsavedChanges || settingsBusy}>
              {isSavingSettings ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Save data-icon="inline-start" />}
              Save
            </Button>
          </div>
        </header>

        <div className="border-b border-border">
          <div className="flex gap-1 overflow-x-auto" role="tablist" aria-label="Settings sections">
            {tabs.map((tab) => {
              const Icon = tab.icon
              const isActive = activeTab === tab.key
              return (
                <button
                  type="button"
                  key={tab.key}
                  id={settingsTabId(tab.key)}
                  ref={(node) => {
                    tabRefs.current[tab.key] = node
                  }}
                  role="tab"
                  aria-selected={isActive}
                  aria-controls={settingsPanelId(tab.key)}
                  className={cn(
                    "flex h-10 items-center gap-2 border-b-2 border-transparent px-3 text-sm font-semibold text-secondary transition-colors hover:text-primary",
                    isActive && "border-accent text-primary"
                  )}
                  onClick={() => setActiveTab(tab.key)}
                  onKeyDown={(event) => handleTabKeyDown(event, tab.key)}
                >
                  <Icon className="size-4" aria-hidden />
                  {tab.label}
                </button>
              )
            })}
          </div>
        </div>

        <div className="py-3">
          {settingsError ? (
            <div className="mx-3 mb-3 rounded-md border border-amber-400/30 bg-amber-400/8 px-3 py-2 text-sm text-secondary" role="status" aria-live="polite">
              <div className="flex items-start gap-2.5">
                <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <span>{settingsError}</span>
              </div>
            </div>
          ) : null}

          {activeTab === "general" ? (
            <SettingsPanel id={settingsPanelId("general")} labelledBy={settingsTabId("general")}>
              <SettingRow label="Theme">
                <Select value={draft.preferences.themePreference} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, themePreference: value } }))}>
                  <SelectTrigger size="sm">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Dark">Dark</SelectItem>
                      <SelectItem value="Light">Light</SelectItem>
                      <SelectItem value="System">System</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </SettingRow>
              <SettingRow label="Output timestamps">
                <Select value={normalizeOutputTimestampPolicy(draft.preferences.outputTimestampPolicy)} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, outputTimestampPolicy: value } }))}>
                  <SelectTrigger size="sm">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      {OUTPUT_TIMESTAMP_POLICIES.map((policy) => (
                        <SelectItem key={policy.id} value={policy.id}>{policy.label}</SelectItem>
                      ))}
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "files" ? (
            <SettingsPanel id={settingsPanelId("files")} labelledBy={settingsTabId("files")}>
              <SettingRow label="Encrypt output">
                <Switch size="sm" checked={draft.preferences.useCustomEncryptOutputDirectory} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, useCustomEncryptOutputDirectory: value } }))} />
              </SettingRow>
              {draft.preferences.useCustomEncryptOutputDirectory ? (
                <PathRow value={draft.preferences.customEncryptOutputDirectory} placeholder="Choose encrypt folder" onBrowse={() => void pickOutputFolder("encrypt")} />
              ) : null}
              <SettingRow label="Decrypt output">
                <Switch size="sm" checked={draft.preferences.useCustomDecryptOutputDirectory} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, useCustomDecryptOutputDirectory: value } }))} />
              </SettingRow>
              {draft.preferences.useCustomDecryptOutputDirectory ? (
                <PathRow value={draft.preferences.customDecryptOutputDirectory} placeholder="Choose decrypt folder" onBrowse={() => void pickOutputFolder("decrypt")} />
              ) : null}
            </SettingsPanel>
          ) : null}

          {activeTab === "privacy" ? (
            <SettingsPanel id={settingsPanelId("privacy")} labelledBy={settingsTabId("privacy")}>
              <SettingRow label="Incognito Mode" detail="Do not save history or recent files.">
                <Switch size="sm" checked={draft.preferences.incognitoMode} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, incognitoMode: value } }))} />
              </SettingRow>
              <SettingRow label="Include full file paths in exports" detail="When disabled, exported history uses redacted paths so local file locations stay private.">
                <Switch size="sm" checked={draft.preferences.includeFullPathsInExports} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, includeFullPathsInExports: value } }))} />
              </SettingRow>
              <SettingRow label="History export">
                <div className="flex flex-wrap justify-end gap-2">
                  <Button variant="secondary" size="sm" onClick={() => void exportHistory("json")} disabled={draft.preferences.incognitoMode || Boolean(historyAction)}>
                    {historyAction === "json" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Download data-icon="inline-start" />}
                    {historyAction === "json" ? "Exporting" : "JSON"}
                  </Button>
                  <Button variant="secondary" size="sm" onClick={() => void exportHistory("csv")} disabled={draft.preferences.incognitoMode || Boolean(historyAction)}>
                    {historyAction === "csv" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Download data-icon="inline-start" />}
                    {historyAction === "csv" ? "Exporting" : "CSV"}
                  </Button>
                  <AlertDialog>
                    <AlertDialogTrigger asChild>
                      <Button variant="outline" size="sm" disabled={Boolean(historyAction)}>
                        {historyAction === "clear" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Trash2 data-icon="inline-start" />}
                        {historyAction === "clear" ? "Clearing" : "Clear"}
                      </Button>
                    </AlertDialogTrigger>
                    <AlertDialogContent>
                      <AlertDialogHeader>
                        <AlertDialogTitle>Clear activity history?</AlertDialogTitle>
                        <AlertDialogDescription>
                          This permanently deletes locally stored FileLocker history. Export your history first if you need a copy.
                        </AlertDialogDescription>
                      </AlertDialogHeader>
                      <AlertDialogFooter>
                        <AlertDialogCancel disabled={historyAction === "clear"}>Cancel</AlertDialogCancel>
                        <AlertDialogAction variant="destructive" disabled={historyAction === "clear"} onClick={clearHistory}>
                          {historyAction === "clear" ? "Clearing" : "Clear History"}
                        </AlertDialogAction>
                      </AlertDialogFooter>
                    </AlertDialogContent>
                  </AlertDialog>
                </div>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "updates" ? (
            <SettingsPanel id={settingsPanelId("updates")} labelledBy={settingsTabId("updates")}>
              <SettingRow label="Automatic checks">
                <Switch size="sm" checked={draft.updates.autoCheckEnabled} onCheckedChange={(value) => updateDraft((current) => ({ ...current, updates: { ...current.updates, autoCheckEnabled: value } }))} />
              </SettingRow>
              <SettingRow label="Status" detail={updateStatus}>
                <div className="flex flex-wrap justify-end gap-2">
                  <Button variant="secondary" size="sm" onClick={checkUpdates} disabled={updateBusy}>
                    {updateAction === "check" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <RefreshCw data-icon="inline-start" />}
                    {updateAction === "check" ? "Checking" : "Check"}
                  </Button>
                  <Button variant="outline" size="sm" onClick={clearSkippedUpdate} disabled={!draft.updates.skippedVersion || updateBusy}>
                    {updateAction === "clear-skip" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : null}
                    Clear Skip
                  </Button>
                </div>
              </SettingRow>
              {availableRelease ? (
                <SettingRow label={`Version ${availableRelease.displayVersion}`}>
                  <div className="flex flex-wrap justify-end gap-2">
                    <Button size="sm" onClick={downloadUpdate} disabled={updateBusy}>
                      {updateAction === "download" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Download data-icon="inline-start" />}
                      {updateAction === "download" ? "Downloading" : "Download"}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={installUpdate} disabled={updateBusy}>
                      {updateAction === "install" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : null}
                      {updateAction === "install" ? "Installing" : "Install"}
                    </Button>
                    <Button variant="ghost" size="sm" onClick={skipUpdate} disabled={updateBusy}>
                      {updateAction === "skip" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : null}
                      {updateAction === "skip" ? "Skipping" : "Skip"}
                    </Button>
                  </div>
                </SettingRow>
              ) : null}
              {downloadedInstallerPath ? (
                <SettingRow label="Update installer">
                  <Button variant="outline" size="sm" onClick={revealDownloadedInstaller}>
                    <FolderOpen data-icon="inline-start" />
                    Open Folder
                  </Button>
                </SettingRow>
              ) : null}
              {app.isDebug ? (
                <SettingRow label="Updater tests" detail={updaterTestStatus || "Debug build only"}>
                  <div className="flex flex-wrap justify-end gap-2">
                    <Button variant="secondary" size="sm" onClick={testUpdateDialog} disabled={updateBusy}>
                      {updateAction === "test-dialog" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Info data-icon="inline-start" />}
                      {updateAction === "test-dialog" ? "Opening" : "Popup"}
                    </Button>
                    <Button variant="secondary" size="sm" onClick={testStartupUpdateCheck} disabled={updateBusy}>
                      {updateAction === "test-startup" ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <RefreshCw data-icon="inline-start" />}
                      {updateAction === "test-startup" ? "Checking" : "Check"}
                    </Button>
                  </div>
                </SettingRow>
              ) : null}
            </SettingsPanel>
          ) : null}

          {activeTab === "integration" ? (
            <SettingsPanel id={settingsPanelId("integration")} labelledBy={settingsTabId("integration")}>
              <SettingRow label="Explorer menu" detail={explorerStatus}>
                <span className="inline-flex items-center gap-2">
                  {isUpdatingExplorerIntegration ? <Loader2 className="size-3.5 animate-spin text-secondary" aria-hidden /> : null}
                  <Switch
                    size="sm"
                    checked={explorer.isRegistered}
                    disabled={!explorer.canManage || isUpdatingExplorerIntegration}
                    aria-label="Toggle Explorer integration"
                    onCheckedChange={(value) => void setExplorerIntegration(value)}
                  />
                </span>
              </SettingRow>
              <SettingRow label="Administrator">
                <span className="justify-self-end rounded-md border border-border bg-bg-dropzone px-3 py-1.5 text-xs font-semibold uppercase tracking-wider text-secondary">
                  {app.isAdministrator ? "Running as admin" : app.canRestartAsAdministrator ? "Standard user" : "Unavailable"}
                </span>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "about" ? (
            <SettingsPanel id={settingsPanelId("about")} labelledBy={settingsTabId("about")}>
              <SettingRow label="Version">
                <span className="font-mono text-sm text-primary">{app.version}</span>
              </SettingRow>
              <SettingRow label="Repository">
                <Button variant="secondary" size="sm" onClick={() => void openExternal(app.repositoryUrl, "Could not open repository.")}>
                  <GitBranch data-icon="inline-start" />
                  Open
                  <ArrowUpRight data-icon="inline-end" />
                </Button>
              </SettingRow>
              <SettingRow label="Protection model">
                <span className="flex items-center justify-end gap-2 text-sm text-secondary">
                  <ShieldCheck className="size-4 text-accent-green" aria-hidden />
                  Local-first
                </span>
              </SettingRow>
            </SettingsPanel>
          ) : null}
        </div>
      </div>
    </div>
  )
}

function SettingsPanel({ children, id, labelledBy }: { children: ReactNode; id: string; labelledBy: string }) {
  return (
    <div id={id} role="tabpanel" aria-labelledby={labelledBy} className="border-y border-border">
      {children}
    </div>
  )
}

function SettingRow({ label, detail, children }: { label: string; detail?: string; children: ReactNode }) {
  return (
    <div className="grid gap-3 border-b border-border/80 px-3 py-2.5 last:border-b-0 md:grid-cols-[minmax(0,1fr)_minmax(220px,340px)] md:items-center">
      <div className="min-w-0">
        <div className="font-display text-sm font-semibold text-primary">{label}</div>
        {detail ? <div className="mt-1 truncate text-xs text-secondary">{detail}</div> : null}
      </div>
      <div className="flex min-w-0 justify-start md:justify-end">{children}</div>
    </div>
  )
}

function PathRow({ value, placeholder, onBrowse }: { value: string; placeholder: string; onBrowse: () => void }) {
  return (
    <div className="grid gap-3 border-b border-border/80 px-3 py-2.5 last:border-b-0 md:grid-cols-[minmax(0,1fr)_auto] md:items-center">
      <Input className="h-9" readOnly value={value} placeholder={placeholder} />
      <Button variant="secondary" size="sm" onClick={onBrowse}>
        <FolderOpen data-icon="inline-start" />
        Browse
      </Button>
    </div>
  )
}

function getErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback
}
