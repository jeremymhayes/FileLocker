import { useEffect, useRef, useState } from "react"
import { Clock3, Download, EyeOff, FolderCog, FolderOpen, Info, Loader2, MonitorCog, Paintbrush, RefreshCw, RotateCcw, Save, ShieldCheck, Trash2 } from "lucide-react"
import { Field } from "@/components/common/Field"
import { FolderPickerRow } from "@/components/settings/FolderPickerRow"
import { SettingsSection } from "@/components/settings/SettingsSection"
import { SidePanel } from "@/components/settings/SidePanel"
import { SettingsToggle as Toggle } from "@/components/settings/SettingsToggle"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import type { DashboardState, SettingsState, UpdateCheckResult, UpdateRelease } from "@/types/bridge"

type SettingsPageProps = {
  settings: SettingsState
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onSettingsUpdate: (settings: SettingsState) => void
  onDashboardUpdate: (dashboard: DashboardState) => void
}

export function SettingsPage({ settings, invoke, onSettingsUpdate, onDashboardUpdate }: SettingsPageProps) {
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
        dirtyRef.current = false
        setHasUnsavedChanges(false)
        setDraft(loaded)
        draftRef.current = loaded
        onSettingsUpdate(loaded)
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
      if (dirtyRef.current) {
        const pendingSettings = draftRef.current
        dirtyRef.current = false
        void invoke<SettingsState>("settings.save", pendingSettings)
          .then(onSettingsUpdate)
          .catch((error) => {
            toast.error(error instanceof Error ? error.message : "Unsaved settings could not be saved.")
          })
      }
    }
  }, [invoke, onSettingsUpdate])

  const historyEnabled = draft.preferences.historyPrivacyMode !== "Off"

  function updateDraft(updater: (current: SettingsState) => SettingsState) {
    dirtyRef.current = true
    setHasUnsavedChanges(true)
    setDraft((current) => {
      const next = updater(current)
      draftRef.current = next
      return next
    })
  }

  async function save() {
    setIsSavingSettings(true)
    try {
      const response = await invoke<SettingsState>("settings.save", draftRef.current)
      dirtyRef.current = false
      setHasUnsavedChanges(false)
      setDraft(response)
      draftRef.current = response
      onSettingsUpdate(response)
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
      dirtyRef.current = false
      setHasUnsavedChanges(false)
      setDraft(response)
      draftRef.current = response
      onSettingsUpdate(response)
      toast.success("Settings reset.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Settings reset failed.")
    }
  }

  function applySettingsResponse(response: SettingsState) {
    dirtyRef.current = false
    setHasUnsavedChanges(false)
    setDraft(response)
    draftRef.current = response
    onSettingsUpdate(response)
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
      toast.success(`Downloaded ${response.fileName}.`)
      setUpdateStatus(`Downloaded ${response.fileName}`)
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
      toast.success(`Opening ${response.fileName}.`)
      setUpdateStatus(`Opening ${response.fileName}`)
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
      setUpdateStatus(`Skipped version ${availableRelease.displayVersion}`)
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
      toast.success(`Exported ${response.recordCount} history record(s) to ${response.fileName}.`)
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

  return (
    <div className="security-page">
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="min-w-0 space-y-6">
          <SettingsSection icon={Paintbrush} title="Appearance" description="Choose how FileLocker looks and how many options are shown.">
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Theme">
                <Select value={draft.preferences.themePreference} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, themePreference: value } }))}>
                  <SelectTrigger>
                    <SelectValue placeholder="Theme" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Dark">Dark</SelectItem>
                      <SelectItem value="Light">Light</SelectItem>
                      <SelectItem value="System">System</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </Field>
              <Field label="Experience Level">
                <Select value={draft.preferences.experienceLevel} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, experienceLevel: value, hasSelectedExperienceLevel: true } }))}>
                  <SelectTrigger>
                    <SelectValue placeholder="Experience level" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Beginner">Beginner</SelectItem>
                      <SelectItem value="Intermediate">Intermediate</SelectItem>
                      <SelectItem value="Advanced">Advanced</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </Field>
            </div>
          </SettingsSection>

          <SettingsSection icon={ShieldCheck} title="Security Defaults" description="Defaults used when you encrypt, decrypt, hash, or delete files.">
            <div className="grid gap-4 md:grid-cols-2">
              <Field label="Output Timestamp Policy">
                <Select value={draft.preferences.outputTimestampPolicy} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, outputTimestampPolicy: value } }))}>
                  <SelectTrigger>
                    <SelectValue placeholder="Timestamp policy" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Current time">Current time</SelectItem>
                      <SelectItem value="Preserve source timestamps">Preserve source timestamps</SelectItem>
                      <SelectItem value="Randomize">Randomize</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </Field>
              <Field label="History Privacy">
                <Select value={draft.preferences.historyPrivacyMode} onValueChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, historyPrivacyMode: value } }))}>
                  <SelectTrigger>
                    <SelectValue placeholder="History privacy" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Off">Off</SelectItem>
                      <SelectItem value="Redacted">Redacted</SelectItem>
                      <SelectItem value="Full">Full</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
              </Field>
            </div>
          </SettingsSection>

          <SettingsSection icon={FolderCog} title="File Handling" description="Output folders are selected with the native Windows folder picker.">
            <Toggle
              label="Use custom encrypt output folder"
              detail="When enabled, encryption requests can default to this folder."
              checked={draft.preferences.useCustomEncryptOutputDirectory}
              onChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, useCustomEncryptOutputDirectory: value } }))}
            />
            <FolderPickerRow label="Default Encrypt Output Folder" folderPath={draft.preferences.customEncryptOutputDirectory} onBrowse={() => pickOutputFolder("encrypt")} />
            <Toggle
              label="Use custom decrypt output folder"
              detail="When enabled, decrypted files can default to this folder."
              checked={draft.preferences.useCustomDecryptOutputDirectory}
              onChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, useCustomDecryptOutputDirectory: value } }))}
            />
            <FolderPickerRow label="Default Decrypt Output Folder" folderPath={draft.preferences.customDecryptOutputDirectory} onBrowse={() => pickOutputFolder("decrypt")} />
          </SettingsSection>

          <SettingsSection icon={MonitorCog} title="Explorer Integration" description="Add or remove the FileLocker right-click entry for files and folders.">
            <Toggle
              label="Encrypt with FileLocker"
              detail={draft.explorerIntegration.statusMessage}
              checked={draft.explorerIntegration.isRegistered}
              onChange={(value) => void setExplorerIntegration(value)}
            />
          </SettingsSection>

          <SettingsSection icon={EyeOff} title="Privacy And History" description="Local history is stored according to the selected privacy mode.">
            <Toggle
              label="Store local activity history"
              detail="Disable to stop loading and saving operation history."
              checked={historyEnabled}
              onChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, historyPrivacyMode: value ? "Redacted" : "Off" } }))}
            />
            <Toggle
              label="Include full paths in exports"
              detail="When disabled, exported history uses redacted paths."
              checked={draft.preferences.includeFullPathsInExports}
              onChange={(value) => updateDraft((current) => ({ ...current, preferences: { ...current.preferences, includeFullPathsInExports: value } }))}
            />
            <div className="flex flex-wrap gap-3">
              <Button variant="secondary" onClick={() => exportHistory("json")}>
                <Download data-icon="inline-start" />
                Export JSON
              </Button>
              <Button variant="secondary" onClick={() => exportHistory("csv")}>
                <Download data-icon="inline-start" />
                Export CSV
              </Button>
            </div>
            <Button variant="destructive" onClick={clearHistory}>
              <Trash2 data-icon="inline-start" />
              Clear History
            </Button>
          </SettingsSection>

          <SettingsSection icon={RefreshCw} title="Updates" description="Check for newer FileLocker releases.">
            <Toggle
              label="Automatic update checks"
              detail="Allow FileLocker to check for newer GitHub releases."
              checked={draft.updates.autoCheckEnabled}
              onChange={(value) => updateDraft((current) => ({ ...current, updates: { ...current.updates, autoCheckEnabled: value } }))}
            />
            <pre className="terminal-output text-secondary">{updateStatus}</pre>
            {availableRelease ? (
              <div className="rounded-2xl border border-border bg-bg-dropzone p-4 text-sm text-secondary">
                <div className="font-display font-semibold text-primary">{availableRelease.displayVersion}</div>
                <div className="mt-2 grid gap-2 md:grid-cols-2">
                  <span>SHA-256: {availableRelease.sha256DigestHex || availableRelease.sha256DigestDownloadUrl ? "available" : "missing"}</span>
                  <span>Signing: checked after download</span>
                </div>
                {availableRelease.notes ? <pre className="terminal-output mt-3 max-h-36 overflow-auto text-secondary">{availableRelease.notes}</pre> : null}
              </div>
            ) : null}
            <div className="flex flex-wrap gap-3">
              <Button variant="secondary" onClick={checkUpdates}>
                <RefreshCw data-icon="inline-start" />
                Check Now
              </Button>
              {availableRelease ? (
                <>
                  <Button variant="secondary" onClick={downloadUpdate}>
                    <Download data-icon="inline-start" />
                    Download Only
                  </Button>
                  <Button onClick={installUpdate}>
                    <Download data-icon="inline-start" />
                    Install Now
                  </Button>
                  <Button variant="outline" onClick={skipUpdate}>
                    Skip Version
                  </Button>
                </>
              ) : null}
              {downloadedInstallerPath ? (
                <Button variant="outline" onClick={revealDownloadedInstaller}>
                  <FolderOpen data-icon="inline-start" />
                  Show in Folder
                </Button>
              ) : null}
              {draft.updates.skippedVersion ? (
                <Button variant="outline" onClick={clearSkippedUpdate}>
                  Clear Skipped Version
                </Button>
              ) : null}
            </div>
          </SettingsSection>
        </div>

        <aside className="space-y-4">
          <SidePanel icon={MonitorCog} title="Current Theme" value={draft.preferences.themePreference} detail="Applies after settings are saved." />
          <SidePanel icon={Clock3} title="Timestamp Policy" value={draft.preferences.outputTimestampPolicy} detail="Used for new file operations." />
          <SidePanel icon={Info} title="History Mode" value={draft.preferences.historyPrivacyMode} detail="Controls recent activity rows." />
          <div className="surface-card">
            <div className="font-display text-lg font-semibold text-primary">Apply Settings</div>
            <p className="mt-1 text-sm leading-[1.65] text-secondary">
              {isLoadingSettings ? "Loading saved settings..." : hasUnsavedChanges ? "Unsaved changes will be saved automatically when you leave this page." : "Settings are saved."}
            </p>
            <div className="mt-5 flex flex-col gap-3">
              <Button onClick={save} disabled={isSavingSettings}>
                {isSavingSettings ? <Loader2 data-icon="inline-start" className="animate-spin" /> : <Save data-icon="inline-start" />}
                Save Settings
              </Button>
              <Button variant="outline" onClick={reset}>
                <RotateCcw data-icon="inline-start" />
                Reset Defaults
              </Button>
            </div>
          </div>
        </aside>
      </div>
    </div>
  )
}
