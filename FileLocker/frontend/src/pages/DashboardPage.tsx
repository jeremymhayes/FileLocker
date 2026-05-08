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
import { StatCard } from "@/components/dashboard/StatCard"
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
  { page: "encrypt", label: "Encrypt Files", detail: "Protect files with AES-256-GCM", icon: Lock, tone: "text-accent-blue bg-accent-blue/10" },
  { page: "hash", label: "Hash Files", detail: "Generate SHA-256 or SHA-512 digests", icon: Hash, tone: "text-accent-teal bg-accent-teal/10" },
  { page: "encode", label: "Encode Text", detail: "Convert Base64, URL, Hex, HTML", icon: Binary, tone: "text-accent-orange bg-accent-orange/10" },
  { page: "metadata", label: "Metadata Scrambler", detail: "Inspect and scrub file metadata", icon: Fingerprint, tone: "text-accent-purple bg-accent-purple/10" },
  { page: "secure-delete", label: "Secure Delete", detail: "Overwrite and remove selected files", icon: Trash2, tone: "text-accent-red bg-accent-red/10" },
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

  const recentFiles = dashboard.recentFiles
  const latestProgress = progressEvents.at(-1)
  const issueCount = dashboard.history.filter((entry) => entry.failureCount > 0 || entry.cancelled).length

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
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <div className="min-w-0 space-y-6">
          <section
            className={cn(
              "rounded-3xl border border-dashed border-border-accent bg-bg-dropzone p-5 transition-[background,border-color,transform] duration-150",
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
              className="flex min-h-[220px] cursor-pointer flex-col items-center justify-center rounded-2xl outline-none focus-visible:ring-2 focus-visible:ring-accent"
              onClick={pickFiles}
              onKeyDown={(event) => {
                if (event.key === "Enter" || event.key === " ") {
                  event.preventDefault()
                  void pickFiles()
                }
              }}
            >
              <Lock className="size-[52px] text-accent-blue" aria-hidden />
              <h2 className="mt-5 text-center font-display text-xl font-semibold text-primary">Drag & drop files or folders to encrypt</h2>
              <p className="mt-2 text-center text-sm text-secondary">or click anywhere in this area to browse</p>
              <div className="mt-6 flex flex-wrap justify-center gap-3">
                <Button
                  className="h-10"
                  onClick={(event) => {
                    event.stopPropagation()
                    void pickFiles()
                  }}
                >
                  <Plus data-icon="inline-start" />
                  Browse Files
                </Button>
                <Button
                  className="h-10"
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

          <section className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
            <div className="rounded-2xl border border-border bg-bg-surface p-5">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <h2 className="font-display text-lg font-semibold text-primary">Encryption Queue</h2>
                  <p className="mt-1 text-sm text-secondary">Selected files and folders ready to encrypt.</p>
                </div>
                <Button variant="outline" onClick={() => setQueuedPaths([])} disabled={queuedPaths.length === 0}>
                  Clear Queue
                </Button>
              </div>

              <SelectedPathList paths={queuedPaths} onRemove={(path) => setQueuedPaths((current) => current.filter((item) => item !== path))} emptyMessage="No files queued. Drag items into the large area above or use Browse Files." />

              {latestProgress ? (
                <div className="mt-5">
                  <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} />
                </div>
              ) : null}
            </div>

            <div className="rounded-2xl border border-border bg-bg-surface p-5">
              <div className="font-display text-base font-semibold text-primary">Quick Encrypt</div>
              <p className="mt-1 text-sm leading-[1.65] text-secondary">Encrypt selected items with the same safe defaults used on the Encrypt Files page.</p>
              {encryptOutputSuggestion?.suggestedPath ? (
                <p className="mt-2 text-sm leading-[1.65] text-secondary">
                  Folder selection detected. Output will default to a separate sibling folder:
                  {" "}
                  <span className="font-mono text-xs text-muted">{encryptOutputSuggestion.suggestedPath}</span>
                </p>
              ) : null}
              <div className="mt-4 space-y-3">
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
              <div className="mt-4 grid grid-cols-2 gap-3 text-sm">
                <InfoPill label="Queued" value={queuedPaths.length.toString()} />
                <InfoPill label="Output" value={encryptOutputSuggestion?.suggestedPath ? fileName(encryptOutputSuggestion.suggestedPath) : "Source folders"} />
              </div>
            </div>
          </section>

          <section>
            <div className="mb-4 flex items-end justify-between gap-4">
              <div>
                <h2 className="font-display text-lg font-semibold leading-[1.3] text-primary">Quick Actions</h2>
                <p className="mt-1 text-sm text-secondary">Open the most common FileLocker workflows.</p>
              </div>
            </div>
            <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-5">
              {quickActions.map((item) => {
                const Icon = item.icon
                return (
                  <button key={item.page} className="surface-card surface-card-hover min-h-36 text-left" onClick={() => onNavigate(item.page)}>
                    <span className={cn("flex size-12 items-center justify-center rounded-2xl", item.tone)}>
                      <Icon className="size-6" aria-hidden />
                    </span>
                    <span className="mt-5 block font-display text-base font-semibold text-primary">{item.label}</span>
                    <span className="mt-2 block text-sm leading-[1.55] text-secondary">{item.detail}</span>
                  </button>
                )
              })}
            </div>
          </section>

          <section className="surface-card">
            <div className="mb-4 flex items-center justify-between gap-4">
              <div>
                <h2 className="font-display text-lg font-semibold leading-[1.3] text-primary">Recent Files</h2>
                <p className="mt-1 text-sm text-secondary">Latest activity tracked by the selected privacy mode.</p>
              </div>
              <button className="flex items-center gap-1 text-sm font-medium text-accent transition-colors hover:text-accent-hover" onClick={() => onNavigate("settings")}>
                View all activity
                <ArrowRight className="size-4" aria-hidden />
              </button>
            </div>
            {recentFiles.length === 0 ? (
              <div className="empty-state flex min-h-48 flex-col items-center justify-center rounded-2xl border border-border bg-bg-dropzone p-6 text-center">
                <FileText className="size-8 text-muted" aria-hidden />
                <p className="mt-3 font-display text-base font-semibold text-primary">No recent activity yet.</p>
                <p className="sub mt-1 text-sm text-secondary">Files you encrypt, hash, or process will appear here.</p>
                <Button className="mt-4" variant="secondary" onClick={() => onNavigate("encrypt")}>
                  Encrypt Files
                </Button>
              </div>
            ) : (
              <div className="overflow-hidden rounded-2xl border border-border">
                <table className="w-full table-fixed border-collapse">
                  <thead className="bg-bg-dropzone text-left">
                    <tr className="font-mono text-xs uppercase tracking-wider text-muted">
                      <th className="w-[36%] px-4 py-3 font-semibold">Name</th>
                      <th className="w-[22%] px-4 py-3 font-semibold">Type</th>
                      <th className="w-[20%] px-4 py-3 font-semibold">Status</th>
                      <th className="w-[17%] px-4 py-3 font-semibold">Last Modified</th>
                      <th className="w-[5%] px-2 py-3 font-semibold" aria-label="Actions" />
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
              <div className="mt-5 rounded-2xl border border-border bg-bg-dropzone p-4">
                <div className="mb-3 font-display text-base font-semibold text-primary">Latest Encryption Result</div>
                <div className="space-y-2">
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

        <aside className="space-y-4">
          <StatCard icon={ShieldCheck} title="Protected Files" value={dashboard.protectedFilesCount} detail={dashboard.protectedFilesSubtitle || "Files are encrypted"} delta={dashboard.protectedFilesDeltaText} tone="text-accent-green bg-accent-green/10" />
          <StatCard icon={HardDrive} title="Storage Saved" value={dashboard.storageSavedDisplay} detail={dashboard.storageSavedSubtitle || "Space saved by encryption"} delta={dashboard.storageSavedDeltaText} tone="text-accent-blue bg-accent-blue/10" />
          <StatCard icon={Clock3} title="Last Operation" value={dashboard.lastOperationName} detail={dashboard.lastOperationFileName || "No file recorded"} delta={dashboard.lastOperationTimeDisplay} tone="text-accent-purple bg-accent-purple/10" success />
          <StatCard icon={ShieldCheck} title="Security Status" value={dashboard.securityStatusTitle} detail={dashboard.securityStatusDetail || dashboard.securityStatusSubtitle} delta={`${issueCount} issue${issueCount === 1 ? "" : "s"}`} tone={dashboard.securityStatusTitle.toLowerCase().includes("warning") ? "text-accent-orange bg-accent-orange/10" : "text-accent-green bg-accent-green/10"} />
        </aside>
      </div>
    </div>
  )
}
