import { useEffect, useState, type DragEvent } from "react"
import {
  ArrowRight,
  Binary,
  Clock3,
  FileText,
  Fingerprint,
  FolderOpen,
  HardDrive,
  Hash,
  Lock,
  Plus,
  ShieldCheck,
  Trash2,
  type LucideIcon,
} from "lucide-react"
import { toast } from "sonner"
import { InfoPill } from "@/components/dashboard/InfoPill"
import { PasswordField } from "@/components/dashboard/PasswordField"
import { RecentFileTableRow } from "@/components/dashboard/RecentFileTableRow"
import { SelectedPathList } from "@/components/dashboard/SelectedPathList"
import { SummaryMetric } from "@/components/dashboard/SummaryMetric"
import { StatusBadge } from "@/components/common/StatusBadge"
import { Button } from "@/components/ui/button"
import { ProgressBar } from "@/components/common/ProgressBar"
import { fileName, mergeUniquePaths } from "@/lib/format"
import { cn } from "@/lib/utils"
import type { DashboardState, FileOperationRequest, FileOperationResult, PageKey, ProgressEvent } from "@/types/bridge"

type DashboardPageProps = {
  dashboard: DashboardState
  onNavigate: (page: PageKey) => void
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  progressEvents: ProgressEvent[]
  onDashboardUpdate: (dashboard: DashboardState) => void
  onReveal: (path: string) => void
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

type OperationResultPayload = {
  completed: number
  failed: number
  results: FileOperationResult[]
  dashboard?: DashboardState
}

type EncryptOutputSuggestion = {
  suggestedPath?: string
  hasFolderSelection: boolean
  folderCount: number
}

const quickActions: Array<{ page: PageKey; label: string; detail: string; icon: LucideIcon; tone: string }> = [
  { page: "encrypt", label: "Encrypt Files", detail: "AES-256-GCM", icon: Lock, tone: "text-accent-blue bg-accent-blue/10" },
  { page: "hash", label: "Hash Files", detail: "SHA-256 / SHA-512", icon: Hash, tone: "text-accent-teal bg-accent-teal/10" },
  { page: "encode", label: "Encode Text", detail: "Base64, URL, Hex, HTML", icon: Binary, tone: "text-accent-orange bg-accent-orange/10" },
  { page: "metadata", label: "Metadata Scrambler", detail: "Inspect and scrub", icon: Fingerprint, tone: "text-accent-purple bg-accent-purple/10" },
  { page: "secure-delete", label: "Secure Delete", detail: "Overwrite and remove", icon: Trash2, tone: "text-accent-red bg-accent-red/10" },
]

const dashboardEncryptDefaults = {
  compressFiles: true,
  scrambleNames: false,
  useSteganography: false,
  packageFolders: false,
  removeOriginalsAfterSuccess: false,
  secureDeleteOriginals: false,
  verifyAfterWrite: true,
  saveNextToSource: true,
  saveNextToEncrypted: true,
  restoreOriginalFilenames: true,
  preserveFolderStructure: true,
  outputTimestampPolicy: "Current time",
  profileName: "Dashboard Quick Encrypt",
  randomizeMetadata: false,
}

export function DashboardPage({
  dashboard,
  onNavigate,
  invoke,
  progressEvents,
  onDashboardUpdate,
  onReveal,
  droppedPaths = [],
  onDroppedPathsHandled,
}: DashboardPageProps) {
  const [queuedPaths, setQueuedPaths] = useState<string[]>([])
  const [password, setPassword] = useState("")
  const [confirmPassword, setConfirmPassword] = useState("")
  const [isDragging, setIsDragging] = useState(false)
  const [isEncrypting, setIsEncrypting] = useState(false)
  const [results, setResults] = useState<FileOperationResult[]>([])
  const [encryptAttempted, setEncryptAttempted] = useState(false)
  const [encryptOutputSuggestion, setEncryptOutputSuggestion] = useState<EncryptOutputSuggestion | null>(null)

  const recentFiles = dashboard.incognitoMode ? [] : dashboard.recentFiles
  const latestProgress = progressEvents.at(-1)
  const issueCount = dashboard.incognitoMode ? 0 : dashboard.history.filter((entry) => entry.failureCount > 0 || entry.cancelled).length

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setQueuedPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

  useEffect(() => {
    if (queuedPaths.length === 0) {
      setEncryptOutputSuggestion(null)
      return
    }

    let cancelled = false

    invoke<EncryptOutputSuggestion>("files.suggestEncryptOutput", { paths: queuedPaths })
      .then((suggestion) => {
        if (!cancelled) {
          setEncryptOutputSuggestion(suggestion)
        }
      })
      .catch(() => {
        if (!cancelled) {
          setEncryptOutputSuggestion(null)
        }
      })

    return () => {
      cancelled = true
    }
  }, [invoke, queuedPaths])

  async function pickFiles() {
    try {
      const response = await invoke<{ paths: string[] }>("files.pickFiles")
      setQueuedPaths((current) => mergeUniquePaths(current, response.paths))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick files.")
    }
  }

  async function pickFolder() {
    try {
      const response = await invoke<{ path: string }>("files.pickFolder")
      if (response.path) {
        setQueuedPaths((current) => mergeUniquePaths(current, [response.path]))
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick folder.")
    }
  }

  function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    setIsDragging(false)

    const paths = Array.from(event.dataTransfer.files)
      .map((file) => (file as File & { path?: string }).path)
      .filter((path): path is string => Boolean(path))

    if (paths.length > 0) {
      setQueuedPaths((current) => mergeUniquePaths(current, paths))
    }
  }

  async function encryptQueuedPaths() {
    setEncryptAttempted(true)
    if (queuedPaths.length === 0) {
      toast.error("Add files or folders before encrypting.")
      return
    }

    if (!password) {
      toast.error("Enter a password before encrypting.")
      return
    }

    if (password !== confirmPassword) {
      toast.error("Passwords do not match.")
      return
    }

    setIsEncrypting(true)
    setResults([])
    try {
      const encryptOutputDirectory = encryptOutputSuggestion?.suggestedPath?.trim() ?? ""
      const payload: FileOperationRequest = {
        operationId: crypto.randomUUID(),
        paths: queuedPaths,
        password,
        keyfilePath: "",
        recoveryKey: "",
        encryptOutputDirectory,
        decryptOutputDirectory: "",
        backupFolderPath: "",
        metadataNotes: "Queued from dashboard drag-and-drop zone",
        ...dashboardEncryptDefaults,
        saveNextToSource: encryptOutputDirectory.length === 0,
      }

      const response = await invoke<OperationResultPayload>("crypto.encryptFiles", payload)
      setResults(response.results)
      if (response.dashboard) {
        onDashboardUpdate(response.dashboard)
      }
      toast.success(`Dashboard encrypt finished: ${response.completed} completed, ${response.failed} failed.`)
      if (response.failed === 0) {
        setQueuedPaths([])
        setPassword("")
        setConfirmPassword("")
        setEncryptAttempted(false)
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Dashboard encrypt failed.")
    } finally {
      setIsEncrypting(false)
    }
  }

  return (
    <div className="security-page">
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_300px]">
        <div className="min-w-0 flex flex-col gap-4">
          <section
            className={cn(
              "rounded-md border border-dashed border-border-accent bg-bg-dropzone/80 p-3 transition-[background,border-color,transform] duration-150",
              isDragging && "scale-[1.005] border-solid border-accent-blue bg-bg-surface-hover"
            )}
            onDragOver={(event) => {
              event.preventDefault()
              setIsDragging(true)
            }}
            onDragLeave={() => setIsDragging(false)}
            onDrop={handleDrop}
          >
            <div
              role="button"
              tabIndex={0}
              className="flex min-h-[130px] cursor-pointer flex-col items-center justify-center rounded-md outline-none focus-visible:ring-2 focus-visible:ring-accent"
              onClick={pickFiles}
              onKeyDown={(event) => {
                if (event.key === "Enter" || event.key === " ") {
                  event.preventDefault()
                  void pickFiles()
                }
              }}
            >
              <Lock className="size-6 text-accent-blue" aria-hidden />
              <h2 className="mt-2 text-center font-display text-base font-semibold text-primary">Drop files or folders to encrypt</h2>
              <div className="mt-4 flex flex-wrap justify-center gap-2">
                <Button
                  onClick={(event) => {
                    event.stopPropagation()
                    void pickFiles()
                  }}
                >
                  <Plus data-icon="inline-start" />
                  Browse Files
                </Button>
                <Button
                  variant="secondary"
                  onClick={(event) => {
                    event.stopPropagation()
                    void pickFolder()
                  }}
                >
                  <FolderOpen data-icon="inline-start" />
                  Browse Folder
                </Button>
              </div>
            </div>
          </section>

          <section className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_280px]">
            <div className="border-y border-border py-3">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <h2 className="font-display text-base font-semibold text-primary">Encryption Queue</h2>
                </div>
                <Button variant="outline" onClick={() => setQueuedPaths([])} disabled={queuedPaths.length === 0}>
                  Clear Queue
                </Button>
              </div>

              <SelectedPathList paths={queuedPaths} onRemove={(path) => setQueuedPaths((current) => current.filter((item) => item !== path))} emptyMessage="No files queued. Drag items into the large area above or use Browse Files." />

              {latestProgress ? (
                <div className="mt-3">
                  <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} />
                </div>
              ) : null}
            </div>

            <div className="border-y border-border py-3">
              <div className="font-display text-base font-semibold text-primary">Quick Encrypt</div>
              <p className="mt-1 text-sm leading-snug text-secondary">Safe defaults, local output.</p>
              {encryptOutputSuggestion?.suggestedPath ? (
                <p className="mt-2 text-sm leading-snug text-secondary">
                  Folder selection detected. Output will default to a separate sibling folder:
                  {" "}
                  <span className="font-mono text-xs text-muted">{encryptOutputSuggestion.suggestedPath}</span>
                </p>
              ) : null}
              <div className="mt-3 flex flex-col gap-2">
                <PasswordField label="Password" value={password} onChange={setPassword} placeholder="Password" />
                {encryptAttempted && !password ? <p className="text-sm text-accent-red">Password is required.</p> : null}
                <PasswordField label="Confirm Password" value={confirmPassword} onChange={setConfirmPassword} placeholder="Confirm password" />
                {password && confirmPassword && password !== confirmPassword ? <p className="text-sm text-accent-red">Passwords do not match.</p> : null}
                {encryptAttempted && queuedPaths.length === 0 ? <p className="text-sm text-accent-red">Add files or folders before encrypting.</p> : null}
                <Button className="w-full" onClick={encryptQueuedPaths} disabled={queuedPaths.length === 0 || isEncrypting}>
                  <Lock data-icon="inline-start" />
                  {isEncrypting ? "Encrypting" : "Encrypt Queue"}
                </Button>
              </div>
              <div className="mt-3 grid grid-cols-2 gap-2 text-sm">
                <InfoPill label="Queued" value={queuedPaths.length.toString()} />
                <InfoPill label="Output" value={encryptOutputSuggestion?.suggestedPath ? fileName(encryptOutputSuggestion.suggestedPath) : "Source folders"} />
              </div>
            </div>
          </section>

          <section className="border-y border-border py-3">
            <div className="mb-2 flex items-end justify-between gap-3">
              <div>
                <h2 className="font-display text-base font-semibold leading-[1.3] text-primary">Quick Actions</h2>
              </div>
            </div>
            <div className="divide-y divide-border">
              {quickActions.map((item) => {
                const Icon = item.icon
                return (
                  <button key={item.page} className="flex min-h-10 w-full items-center gap-2.5 px-1 py-2 text-left transition-colors hover:bg-bg-surface-hover/60" onClick={() => onNavigate(item.page)}>
                    <span className={cn("flex size-7 shrink-0 items-center justify-center rounded-md", item.tone)}>
                      <Icon className="size-4" aria-hidden />
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-display text-sm font-semibold text-primary">{item.label}</span>
                      <span className="block truncate text-xs text-secondary">{item.detail}</span>
                    </span>
                    <ArrowRight className="size-4 shrink-0 text-muted" aria-hidden />
                  </button>
                )
              })}
            </div>
          </section>

          <section className="section-surface">
            <div className="mb-3 flex items-center justify-between gap-3">
              <div>
                <h2 className="font-display text-base font-semibold leading-[1.3] text-primary">Recent Files</h2>
              </div>
              <button className="flex items-center gap-1 text-sm font-medium text-accent transition-colors hover:text-accent-hover" onClick={() => onNavigate("settings")}>
                Settings
                <ArrowRight className="size-4" aria-hidden />
              </button>
            </div>
            {recentFiles.length === 0 ? (
              <div className="empty-state flex min-h-[4.5rem] items-center justify-between gap-3 border-y border-border px-3 py-3">
                <div className="flex min-w-0 items-center gap-2.5">
                  <FileText className="size-4 shrink-0 text-muted" aria-hidden />
                  <div className="min-w-0">
                    <p className="font-display text-sm font-semibold leading-tight text-primary">{dashboard.incognitoMode ? "Incognito Mode On" : "No recent files"}</p>
                    <p className="sub mt-0.5 text-xs leading-snug text-secondary">{dashboard.incognitoMode ? "Activity is not saved." : "Run a workflow to populate this list."}</p>
                  </div>
                </div>
                <Button variant="secondary" onClick={() => onNavigate(dashboard.incognitoMode ? "settings" : "encrypt")}>
                  {dashboard.incognitoMode ? "Open Settings" : "Encrypt Files"}
                </Button>
              </div>
            ) : (
              <div className="overflow-hidden border-y border-border">
                <table className="w-full table-fixed border-collapse">
                  <thead className="bg-bg-dropzone text-left">
                    <tr className="font-mono text-xs uppercase tracking-wider text-muted">
                      <th className="w-[36%] px-3 py-2.5 font-semibold">Name</th>
                      <th className="w-[22%] px-3 py-2.5 font-semibold">Type</th>
                      <th className="w-[20%] px-3 py-2.5 font-semibold">Status</th>
                      <th className="w-[17%] px-3 py-2.5 font-semibold">Last Modified</th>
                      <th className="w-[5%] px-2 py-2.5 font-semibold" aria-label="Actions" />
                    </tr>
                  </thead>
                  <tbody>
                    {recentFiles.map((item, index) => (
                      <RecentFileTableRow key={`${item.name}-${item.lastModified}-${index}`} item={item} active={index === 0} onReveal={onReveal} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {results.length > 0 ? (
              <div className="mt-4 border-y border-border py-3">
                <div className="mb-3 font-display text-base font-semibold text-primary">Latest Encryption Result</div>
                <div className="flex flex-col gap-2">
                  {results.slice(0, 5).map((result) => (
                    <div key={`${result.sourcePath}-${result.outputPath ?? result.status}`} className="flex items-center gap-3 text-sm">
                      <StatusBadge status={result.status} />
                      <span className="min-w-0 flex-1 truncate font-mono text-secondary">{result.outputPath ?? result.sourcePath}</span>
                    </div>
                  ))}
                </div>
              </div>
            ) : null}
          </section>
        </div>

        <aside className="border-l border-border pl-4">
          <SummaryMetric icon={ShieldCheck} title="Protected Files" value={dashboard.protectedFilesCount} detail={dashboard.protectedFilesSubtitle || "Files are encrypted"} delta={dashboard.protectedFilesDeltaText} tone="text-accent-green bg-accent-green/10" />
          <SummaryMetric icon={HardDrive} title="Storage Saved" value={dashboard.storageSavedDisplay} detail={dashboard.storageSavedSubtitle || "Space saved by encryption"} delta={dashboard.storageSavedDeltaText} tone="text-accent-blue bg-accent-blue/10" />
          <SummaryMetric icon={Clock3} title={dashboard.incognitoMode ? "Privacy Mode" : "Last Operation"} value={dashboard.incognitoMode ? "Incognito" : dashboard.lastOperationName} detail={dashboard.incognitoMode ? "Activity is not saved" : dashboard.lastOperationFileName || "No file recorded"} delta={dashboard.incognitoMode ? "On" : dashboard.lastOperationTimeDisplay} tone="text-accent-purple bg-accent-purple/10" success />
          <SummaryMetric icon={ShieldCheck} title="Security Status" value={dashboard.securityStatusTitle} detail={dashboard.securityStatusDetail || dashboard.securityStatusSubtitle} delta={`${issueCount} issue${issueCount === 1 ? "" : "s"}`} tone={dashboard.securityStatusTitle.toLowerCase().includes("warning") ? "text-accent-orange bg-accent-orange/10" : "text-accent-green bg-accent-green/10"} />
        </aside>
      </div>
    </div>
  )
}
