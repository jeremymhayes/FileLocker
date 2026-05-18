import { useEffect, useRef, useState, type ReactNode } from "react"
import {
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
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { cn } from "@/lib/utils"
import type { DashboardState, InitialState, SettingsState, UpdateCheckResult, UpdateRelease } from "@/types/bridge"

type SettingsPageProps = {
  app: InitialState["app"]
  settings: SettingsState
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onSettingsUpdate: (settings: SettingsState) => void
  onDashboardUpdate: (dashboard: DashboardState) => void
}

type SettingsTab = "general" | "files" | "privacy" | "updates" | "integration" | "about"

const tabs: Array<{ key: SettingsTab; label: string; icon: LucideIcon }> = [
  { key: "general", label: "General", icon: Paintbrush },
  { key: "files", label: "Files", icon: FolderCog },
  { key: "privacy", label: "Privacy", icon: EyeOff },
  { key: "updates", label: "Updates", icon: RefreshCw },
  { key: "integration", label: "Integration", icon: MonitorCog },
  { key: "about", label: "About", icon: Info },
]

export function SettingsPage({ app, settings, invoke, onSettingsUpdate, onDashboardUpdate }: SettingsPageProps) {
  const [activeTab, setActiveTab] = useState<SettingsTab>("general")
  const [draft, setDraft] = useState(settings)
  const [hasUnsavedChanges, setHasUnsavedChanges] = useState(false)
  const [isLoadingSettings, setIsLoadingSettings] = useState(false)
  const [isSavingSettings, setIsSavingSettings] = useState(false)
  const [updateStatus, setUpdateStatus] = useState("Not checked")
  const [availableRelease, setAvailableRelease] = useState<UpdateRelease | null>(null)
  const [downloadedInstallerPath, setDownloadedInstallerPath] = useState("")
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

    invoke<SettingsState>("settings.get")
      .then((loaded) => {
        if (!mounted) {
          return
        }

        applySettingsResponse(loaded)
      })
      .catch((error) => {
        toast.error(error instanceof Error ? error.message : "Settings load failed.")
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

  function applySettingsResponse(response: SettingsState) {
    dirtyRef.current = false
    setHasUnsavedChanges(false)
    setDraft(response)
    draftRef.current = response
    onSettingsUpdate(response)
  }

  async function save() {
    setIsSavingSettings(true)
    try {
      const response = await invoke<SettingsState>("settings.save", draftRef.current)
      applySettingsResponse(response)

      if (response.preferences.incognitoMode) {
        const cleared = await invoke<{ dashboard: DashboardState }>("history.clear")
        onDashboardUpdate(cleared.dashboard)
      }

      toast.success("Settings saved.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Settings save failed.")
    } finally {
      setIsSavingSettings(false)
    }
  }

  async function reset() {
    try {
      const response = await invoke<SettingsState>("settings.reset")
      applySettingsResponse(response)
      toast.success("Settings reset.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Settings reset failed.")
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
    try {
      const response = await invoke<UpdateCheckResult>("updates.check")
      setUpdateStatus(response.statusMessage)
      setAvailableRelease(response.isUpdateAvailable ? response.release ?? null : null)
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Update check failed.")
    }
  }

  async function downloadUpdate() {
    if (!availableRelease) {
      return
    }

    try {
      const response = await invoke<{ installerPath: string; fileName: string }>("updates.download")
      setDownloadedInstallerPath(response.installerPath)
      setUpdateStatus(`Downloaded ${response.fileName}`)
      toast.success(`Downloaded ${response.fileName}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Update download failed.")
    }
  }

  async function installUpdate() {
    if (!availableRelease) {
      return
    }

    try {
      const response = await invoke<{ installerPath: string; fileName: string }>("updates.install")
      setDownloadedInstallerPath(response.installerPath)
      setUpdateStatus(`Opening ${response.fileName}`)
      toast.success(`Opening ${response.fileName}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Update install failed.")
    }
  }

  async function skipUpdate() {
    if (!availableRelease) {
      return
    }

    try {
      const response = await invoke<SettingsState>("updates.skip", { version: availableRelease.displayVersion })
      applySettingsResponse(response)
      setAvailableRelease(null)
      setUpdateStatus(`Skipped ${availableRelease.displayVersion}`)
      toast.message(`Skipped version ${availableRelease.displayVersion}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Update skip failed.")
    }
  }

  async function clearSkippedUpdate() {
    try {
      const response = await invoke<SettingsState>("updates.clearSkip")
      applySettingsResponse(response)
      setUpdateStatus("Skipped version cleared")
      toast.success("Skipped update cleared.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Skipped update could not be cleared.")
    }
  }

  async function revealDownloadedInstaller() {
    if (!downloadedInstallerPath) {
      return
    }

    try {
      await invoke("files.revealPath", { path: downloadedInstallerPath })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not open the installer folder.")
    }
  }

  async function exportHistory(format: "json" | "csv") {
    try {
      const response = await invoke<{ exportPath: string; fileName: string; recordCount: number }>("history.export", { format })
      toast.success(`Exported ${response.recordCount} record(s) to ${response.fileName}.`)
      await invoke("files.revealPath", { path: response.exportPath })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "History export failed.")
    }
  }

  async function clearHistory() {
    try {
      const response = await invoke<{ dashboard: DashboardState }>("history.clear")
      onDashboardUpdate(response.dashboard)
      toast.success("History cleared.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "History clear failed.")
    }
  }

  async function setExplorerIntegration(enabled: boolean) {
    try {
      const response = await invoke<SettingsState>("shell.setExplorerIntegration", { enabled })
      applySettingsResponse(response)
      toast.success(enabled ? "Explorer integration installed." : "Explorer integration removed.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Explorer integration update failed.")
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

  return (
    <div className="security-page">
      <div className="border-y border-border bg-transparent">
        <header className="flex flex-col gap-3 border-b border-border py-3 xl:flex-row xl:items-center xl:justify-between">
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
            <Button variant="secondary" size="sm" onClick={reset}>
              <RotateCcw data-icon="inline-start" />
              Reset
            </Button>
            <Button size="sm" onClick={save} disabled={!hasUnsavedChanges || isSavingSettings}>
              {isSavingSettings ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Save data-icon="inline-start" />}
              Save
            </Button>
          </div>
        </header>

        <div className="border-b border-border">
          <div className="flex gap-1 overflow-x-auto">
            {tabs.map((tab) => {
              const Icon = tab.icon
              const isActive = activeTab === tab.key
              return (
                <button
                  key={tab.key}
                  className={cn(
                    "flex h-10 items-center gap-2 border-b-2 border-transparent px-3 text-sm font-semibold text-secondary transition-colors hover:text-primary",
                    isActive && "border-accent text-primary"
                  )}
                  onClick={() => setActiveTab(tab.key)}
                >
                  <Icon className="size-4" aria-hidden />
                  {tab.label}
                </button>
              )
            })}
          </div>
        </div>

        <div className="py-3">
          {activeTab === "general" ? (
            <SettingsPanel>
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
                <Select value={draft.preferences.outputTimestampPolicy} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, outputTimestampPolicy: value } }))}>
                  <SelectTrigger size="sm">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Current time">Current time</SelectItem>
                      <SelectItem value="Preserve source timestamps">Preserve source timestamps</SelectItem>
                      <SelectItem value="Randomize">Randomize</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "files" ? (
            <SettingsPanel>
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
            <SettingsPanel>
              <SettingRow label="Incognito Mode" detail="Do not save history or recent files.">
                <Switch size="sm" checked={draft.preferences.incognitoMode} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, incognitoMode: value } }))} />
              </SettingRow>
              <SettingRow label="Full paths in exports">
                <Switch size="sm" checked={draft.preferences.includeFullPathsInExports} onCheckedChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, includeFullPathsInExports: value } }))} />
              </SettingRow>
              <SettingRow label="History export">
                <div className="flex flex-wrap justify-end gap-2">
                  <Button variant="secondary" size="sm" onClick={() => void exportHistory("json")} disabled={draft.preferences.incognitoMode}>
                    <Download data-icon="inline-start" />
                    JSON
                  </Button>
                  <Button variant="secondary" size="sm" onClick={() => void exportHistory("csv")} disabled={draft.preferences.incognitoMode}>
                    <Download data-icon="inline-start" />
                    CSV
                  </Button>
                  <Button variant="outline" size="sm" onClick={clearHistory}>
                    <Trash2 data-icon="inline-start" />
                    Clear
                  </Button>
                </div>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "updates" ? (
            <SettingsPanel>
              <SettingRow label="Automatic checks">
                <Switch size="sm" checked={draft.updates.autoCheckEnabled} onCheckedChange={(value) => updateDraft((current) => ({ ...current, updates: { ...current.updates, autoCheckEnabled: value } }))} />
              </SettingRow>
              <SettingRow label="Status" detail={updateStatus}>
                <div className="flex flex-wrap justify-end gap-2">
                  <Button variant="secondary" size="sm" onClick={checkUpdates}>
                    <RefreshCw data-icon="inline-start" />
                    Check
                  </Button>
                  <Button variant="outline" size="sm" onClick={clearSkippedUpdate} disabled={!draft.updates.skippedVersion}>
                    Clear Skip
                  </Button>
                </div>
              </SettingRow>
              {availableRelease ? (
                <SettingRow label={`Version ${availableRelease.displayVersion}`}>
                  <div className="flex flex-wrap justify-end gap-2">
                    <Button size="sm" onClick={downloadUpdate}>
                      <Download data-icon="inline-start" />
                      Download
                    </Button>
                    <Button variant="secondary" size="sm" onClick={installUpdate}>
                      Install
                    </Button>
                    <Button variant="ghost" size="sm" onClick={skipUpdate}>
                      Skip
                    </Button>
                  </div>
                </SettingRow>
              ) : null}
              {downloadedInstallerPath ? (
                <SettingRow label="Installer">
                  <Button variant="outline" size="sm" onClick={revealDownloadedInstaller}>
                    <FolderOpen data-icon="inline-start" />
                    Open Folder
                  </Button>
                </SettingRow>
              ) : null}
            </SettingsPanel>
          ) : null}

          {activeTab === "integration" ? (
            <SettingsPanel>
              <SettingRow label="Explorer menu" detail={explorer.statusMessage}>
                <Switch size="sm" checked={explorer.isRegistered} disabled={!explorer.canManage} onCheckedChange={(value) => void setExplorerIntegration(value)} />
              </SettingRow>
              <SettingRow label="Administrator">
                <span className="justify-self-end rounded-md border border-border bg-bg-dropzone px-3 py-1.5 text-xs font-semibold uppercase tracking-wider text-secondary">
                  {app.isAdministrator ? "Running as admin" : app.canRestartAsAdministrator ? "Standard user" : "Unavailable"}
                </span>
              </SettingRow>
            </SettingsPanel>
          ) : null}

          {activeTab === "about" ? (
            <SettingsPanel>
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

function SettingsPanel({ children }: { children: ReactNode }) {
  return <div className="border-y border-border">{children}</div>
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
