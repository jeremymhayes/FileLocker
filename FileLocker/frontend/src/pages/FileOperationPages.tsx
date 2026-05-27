import { useEffect, useMemo, useState, type DragEvent } from "react"
import {
  AlertTriangle,
  BookOpen,
  CheckCircle2,
  Clock3,
  Files,
  FolderOpen,
  HardDrive,
  History,
  Info,
  Lock,
  ShieldAlert,
  ShieldCheck,
  Trash2,
  Unlock,
  UploadCloud,
} from "lucide-react"
import { Badge } from "@/components/ui/badge"
import { Section, SectionBody, SectionFooter, SectionHeader, SectionTitle } from "@/components/layout/Workspace"
import { Field } from "@/components/common/Field"
import { OperationToggle as Toggle } from "@/components/file-operations/OperationToggle"
import { PasswordInput } from "@/components/file-operations/PasswordInput"
import { toast } from "sonner"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { FileDropZone } from "@/components/common/FileDropZone"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { ProgressBar } from "@/components/common/ProgressBar"
import { ResultList } from "@/components/common/ResultList"
import { cn } from "@/lib/utils"
import { fileName, mergeUniquePaths } from "@/lib/format"
import { getLatestProgressForOperation } from "@/lib/progress"
import type { DashboardState, FileOperationRequest, FileOperationResult, ProgressEvent } from "@/types/bridge"

type OperationResultPayload = {
  operationId: string
  completed: number
  failed: number
  results: FileOperationResult[]
  dashboard?: unknown
}

type EncryptOutputSuggestion = {
  suggestedPath?: string
  hasFolderSelection: boolean
  folderCount: number
}

type SelectedPathDescriptor = {
  fullPath: string
  displayName: string
  itemType: string
  sizeBytes: number
  sizeDisplay: string
  isDirectory: boolean
  details: string
}

type DescribePathsResponse = {
  items: SelectedPathDescriptor[]
  totalSizeBytes: number
  totalSizeDisplay: string
  warnings: string[]
}

type FileOperationPageProps = {
  kind: "encrypt" | "decrypt" | "verify"
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  progressEvents: ProgressEvent[]
  onDashboardUpdate: (dashboard: unknown) => void
  onReveal: (path: string) => void
  dashboard?: DashboardState
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

const defaultOptions = {
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
  profileName: "FileLocker",
  randomizeMetadata: false,
}

const encryptionGuidePoints = [
  "Files are encrypted locally with FileLocker. Nothing is uploaded as part of a normal encryption run.",
  "Use a strong password and test decryption before deleting important originals.",
  "For large folder jobs, a separate output folder keeps `.locked` files out of the original source tree.",
  "Delete originals stays off by default. Turn it on only when you have verified both your password and your output location.",
]

const decryptionGuidePoints = [
  "Only decrypt files you trust and recognize. FileLocker checks the payload format before restoring output.",
  "Use the original password, and add recovery material only if that workflow was already part of the encrypted file.",
  "Choose an output folder when you want restored files kept separate from the encrypted source set.",
  "Delete encrypted files after successful decryption only after you confirm the restored files open correctly.",
]

const secureDeleteGuidePoints = [
  "Choose the overwrite method before removing selected items.",
  "Review every selected file and folder carefully. This workflow is meant for permanent local removal.",
  "Folders are processed recursively, so make sure the selected path only contains content you intend to delete.",
  "Overwrite-based deletion is generally more reliable on traditional hard drives than on SSDs.",
]

const secureDeleteMethods = [
  {
    id: "quick",
    label: "Quick",
    passes: 1,
    detail: "1 pass. Fastest option for low-risk cleanup.",
  },
  {
    id: "dod",
    label: "DoD 5220.22-M",
    passes: 3,
    detail: "3 passes. Balanced default for stronger sanitization.",
  },
  {
    id: "gutmann",
    label: "Gutmann",
    passes: 35,
    detail: "35 passes. Very slow; use only when needed.",
  },
]

export function FileOperationPage({ kind, invoke, progressEvents, onDashboardUpdate, onReveal, dashboard, droppedPaths = [], onDroppedPathsHandled }: FileOperationPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [password, setPassword] = useState("")
  const [confirmPassword, setConfirmPassword] = useState("")
  const [keyfilePath, setKeyfilePath] = useState("")
  const [recoveryKey, setRecoveryKey] = useState("")
  const [encryptOutputDirectory, setEncryptOutputDirectory] = useState("")
  const [decryptOutputDirectory, setDecryptOutputDirectory] = useState("")
  const [backupFolderPath, setBackupFolderPath] = useState("")
  const [metadataNotes, setMetadataNotes] = useState("")
  const [deleteConfirmation, setDeleteConfirmation] = useState("")
  const [options, setOptions] = useState(defaultOptions)
  const [isRunning, setIsRunning] = useState(false)
  const [results, setResults] = useState<FileOperationResult[]>([])
  const [operationError, setOperationError] = useState("")
  const [activeOperationId, setActiveOperationId] = useState("")
  const [submitAttempted, setSubmitAttempted] = useState(false)
  const [encryptOutputSuggestion, setEncryptOutputSuggestion] = useState<EncryptOutputSuggestion | null>(null)
  const [pathDetails, setPathDetails] = useState<SelectedPathDescriptor[]>([])
  const [pathWarnings, setPathWarnings] = useState<string[]>([])
  const [totalSizeDisplay, setTotalSizeDisplay] = useState("Calculated at run start")
  const [isDescribingPaths, setIsDescribingPaths] = useState(false)

  const latestProgress = useMemo(
    () => getLatestProgressForOperation(progressEvents, activeOperationId),
    [activeOperationId, progressEvents]
  )
  const isEncrypt = kind === "encrypt"
  const isDecrypt = kind === "decrypt"
  const title = isEncrypt ? "Encrypt Files" : isDecrypt ? "Decrypt Files" : "Verify Payloads"
  const description = isEncrypt ? "Create encrypted FileLocker files on this device." : isDecrypt ? "Restore files from FileLocker .locked payloads." : "Check encrypted files without writing output."
  const Icon = isEncrypt ? Lock : isDecrypt ? Unlock : ShieldCheck
  const destructiveOptionsEnabled = options.removeOriginalsAfterSuccess || options.secureDeleteOriginals
  const hasOperationError = Boolean(operationError)
  const savesNextToSource = encryptOutputDirectory.trim().length === 0
  const encryptHistory = useMemo(
    () => (dashboard?.history ?? []).filter((entry) => entry.operation === "Encrypt").slice(0, 6),
    [dashboard]
  )
  const decryptHistory = useMemo(
    () => (dashboard?.history ?? []).filter((entry) => entry.operation === "Decrypt").slice(0, 6),
    [dashboard]
  )
  const passwordStrength = calculatePasswordStrength(password)
  const passwordsMatch = password.length > 0 && password === confirmPassword
  const encryptCanStart = paths.length > 0 && password.length > 0 && passwordsMatch && passwordStrength.score >= 70
  const encryptStatusText = hasOperationError
    ? "Failed"
    : paths.length === 0
    ? "Add files or folders"
    : !password
      ? "Password required"
      : !passwordsMatch
        ? "Passwords do not match"
        : passwordStrength.score < 70
          ? "Use a stronger password"
          : "Ready to encrypt"
  const encryptOutputSummary = savesNextToSource
    ? "Next to source files"
    : encryptOutputDirectory.trim()
  const decryptSavesNextToEncrypted = decryptOutputDirectory.trim().length === 0
  const decryptOutputSummary = decryptSavesNextToEncrypted
    ? "Next to encrypted files"
    : decryptOutputDirectory.trim()
  const decryptCanStart = paths.length > 0 && password.length > 0
  const decryptStatusText = hasOperationError
    ? "Failed"
    : paths.length === 0
    ? "Add encrypted files"
    : !password
      ? "Waiting for password"
      : "Ready to decrypt"

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      onDroppedPathsHandled?.()
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, isRunning, onDroppedPathsHandled])

  useEffect(() => {
    if (!isEncrypt) {
      setEncryptOutputSuggestion(null)
      return
    }

    if (paths.length === 0) {
      setEncryptOutputSuggestion(null)
      return
    }

    let cancelled = false

    invoke<EncryptOutputSuggestion>("files.suggestEncryptOutput", { paths })
      .then((suggestion) => {
        if (cancelled) {
          return
        }

        setEncryptOutputSuggestion(suggestion)
        if (suggestion.suggestedPath) {
          setEncryptOutputDirectory((current) => current.trim() ? current : (suggestion.suggestedPath ?? current))
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
  }, [invoke, isEncrypt, paths])

  useEffect(() => {
    if (!(isEncrypt || isDecrypt)) {
      setPathDetails([])
      setPathWarnings([])
      setTotalSizeDisplay("Calculated at run start")
      return
    }

    if (paths.length === 0) {
      setPathDetails([])
      setPathWarnings([])
      setTotalSizeDisplay("Calculated at run start")
      return
    }

    let cancelled = false
    setIsDescribingPaths(true)

    invoke<DescribePathsResponse>("files.describePaths", { paths })
      .then((response) => {
        if (cancelled) {
          return
        }

        setPathDetails(response.items)
        setPathWarnings(response.warnings)
        setTotalSizeDisplay(response.totalSizeDisplay)
      })
      .catch((error) => {
        if (!cancelled) {
          setPathDetails([])
          setPathWarnings([getPathDetailsWarning(error)])
          setTotalSizeDisplay("Calculated at run start")
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsDescribingPaths(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [invoke, isDecrypt, isEncrypt, paths])

  useEffect(() => {
    setResults((current) => current.length > 0 ? [] : current)
    setOperationError("")
    setActiveOperationId("")
  }, [
    paths,
    password,
    confirmPassword,
    keyfilePath,
    recoveryKey,
    encryptOutputDirectory,
    decryptOutputDirectory,
    backupFolderPath,
    metadataNotes,
    deleteConfirmation,
    options.compressFiles,
    options.scrambleNames,
    options.useSteganography,
    options.packageFolders,
    options.removeOriginalsAfterSuccess,
    options.secureDeleteOriginals,
    options.verifyAfterWrite,
    options.restoreOriginalFilenames,
    options.preserveFolderStructure,
    options.outputTimestampPolicy,
  ])

  async function pickFiles() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      return
    }

    try {
      const result = await invoke<{ paths: string[] }>("files.pickFiles")
      setPaths((current) => mergeUniquePaths(current, result.paths))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick files.")
    }
  }

  async function pickFolder() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      return
    }

    try {
      const result = await invoke<{ path: string }>("files.pickFolder")
      if (result.path) {
        setPaths((current) => mergeUniquePaths(current, [result.path]))
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick a folder.")
    }
  }

  function handleTargetDragOver(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    event.dataTransfer.dropEffect = isRunning ? "none" : "copy"
  }

  function handleTargetDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      return
    }

    const droppedPaths = Array.from(event.dataTransfer.files)
      .map((file) => (file as File & { path?: string }).path)
      .filter((path): path is string => Boolean(path))
    if (droppedPaths.length > 0) {
      setPaths((current) => mergeUniquePaths(current, droppedPaths))
    }
  }

function removeTarget(path: string) {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      return
    }

    setPaths((current) => current.filter((item) => !areSameLocalPath(item, path)))
  }

  function clearTargets() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing the selected targets.")
      return
    }

    setPaths([])
  }

  async function pickEncryptOutputFolder() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing run options.")
      return
    }

    try {
      const result = await invoke<{ path: string }>("files.pickFolder")
      if (result.path) {
        setEncryptOutputDirectory(result.path)
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick an encrypt output folder.")
    }
  }

  async function pickDecryptOutputFolder() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing run options.")
      return
    }

    try {
      const result = await invoke<{ path: string }>("files.pickFolder")
      if (result.path) {
        setDecryptOutputDirectory(result.path)
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick a decrypt output folder.")
    }
  }

  async function pickBackupFolder() {
    if (isRunning) {
      toast.error("Wait for the current file operation to finish before changing run options.")
      return
    }

    try {
      const result = await invoke<{ path: string }>("files.pickFolder")
      if (result.path) {
        setBackupFolderPath(result.path)
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick a backup folder.")
    }
  }

  function handleSaveNextToSourceChange(enabled: boolean) {
    if (enabled) {
      setEncryptOutputDirectory("")
      return
    }

    if (encryptOutputSuggestion?.suggestedPath) {
      setEncryptOutputDirectory(encryptOutputSuggestion.suggestedPath)
      return
    }

    setEncryptOutputDirectory((current) => current.trim())
  }

  function handleSaveNextToEncryptedChange(enabled: boolean) {
    if (enabled) {
      setDecryptOutputDirectory("")
    }
  }

  async function run() {
    if (isRunning) {
      return
    }

    setSubmitAttempted(true)
    if (paths.length === 0) {
      toast.error("Select at least one file or folder.")
      return
    }

    if (!password) {
      toast.error(isEncrypt ? "Enter a password before encrypting." : "Enter the unlock password.")
      return
    }

    if (isEncrypt && password !== confirmPassword) {
      toast.error("Passwords do not match.")
      return
    }

    setIsRunning(true)
    setResults([])
    setOperationError("")
    const operationId = crypto.randomUUID()
    setActiveOperationId(operationId)
    try {
      const normalizedEncryptOutputDirectory = encryptOutputDirectory.trim()
      const normalizedDecryptOutputDirectory = decryptOutputDirectory.trim()
      const normalizedBackupFolderPath = backupFolderPath.trim()

      const payload: FileOperationRequest = {
        operationId,
        paths,
        password,
        keyfilePath,
        recoveryKey,
        ...options,
        saveNextToSource: !isEncrypt || normalizedEncryptOutputDirectory.length === 0,
        encryptOutputDirectory: normalizedEncryptOutputDirectory,
        saveNextToEncrypted: !isDecrypt || normalizedDecryptOutputDirectory.length === 0,
        decryptOutputDirectory: normalizedDecryptOutputDirectory,
        backupFolderPath: normalizedBackupFolderPath,
        metadataNotes,
        deleteConfirmation,
      }
      const action = isEncrypt ? "crypto.encryptFiles" : isDecrypt ? "crypto.decryptFiles" : "crypto.verifyPayload"
      const result = await invoke<OperationResultPayload>(action, payload)
      setResults(result.results)
      if (result.dashboard) {
        onDashboardUpdate(result.dashboard)
      }
      toast.success(`${title} finished: ${result.completed} completed, ${result.failed} failed.`)
      setSubmitAttempted(false)
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Operation failed."
      setOperationError(message)
      toast.error(message)
    } finally {
      setIsRunning(false)
    }
  }

  if (isEncrypt) {
    return (
      <div className="security-page">
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
            <div className="min-w-0">
              <div className="flex items-center gap-3">
                <div className="flex size-8 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent ">
                  <Lock className="size-4" aria-hidden />
                </div>
                <div>
                  <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Encrypt Files</h2>
                </div>
              </div>
            </div>

            <div className="flex shrink-0 flex-wrap gap-2">
              <Dialog>
                <DialogTrigger asChild>
                  <Button variant="outline">
                    <BookOpen data-icon="inline-start" />
                    Encryption Guide
                  </Button>
                </DialogTrigger>
                <DialogContent className="sm:max-w-2xl">
                  <DialogHeader>
                    <DialogTitle>Encryption Guide</DialogTitle>
                    <DialogDescription>What to check before you lock files locally with FileLocker.</DialogDescription>
                  </DialogHeader>
                  <div className="flex flex-col gap-3">
                    {encryptionGuidePoints.map((point) => (
                      <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                        {point}
                      </div>
                    ))}
                  </div>
                </DialogContent>
              </Dialog>

              <Dialog>
                <DialogTrigger asChild>
                  <Button variant="outline">
                    <History data-icon="inline-start" />
                    History
                  </Button>
                </DialogTrigger>
                <DialogContent className="sm:max-w-3xl">
                  <DialogHeader>
                    <DialogTitle>Recent Encryption History</DialogTitle>
                    <DialogDescription>Local recent activity from the dashboard history feed.</DialogDescription>
                  </DialogHeader>
                  {encryptHistory.length === 0 ? (
                    <div className="rounded-md border border-border bg-background/40 px-3 py-3 text-sm text-secondary">
                      No local encryption history is available yet.
                    </div>
                  ) : (
                    <div className="flex flex-col gap-3">
                      {encryptHistory.map((entry) => (
                        <div key={entry.id} className="rounded-md border border-border bg-background/35 px-4 py-4">
                          <div className="flex flex-wrap items-center justify-between gap-3">
                            <div>
                              <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{entry.profileName || "FileLocker"}</div>
                              <div className="mt-1 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{new Date(entry.timestampUtc).toLocaleString()}</div>
                            </div>
                            <Badge variant="outline">{entry.cancelled ? "Cancelled" : `${entry.successCount} completed / ${entry.failureCount} failed`}</Badge>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </DialogContent>
              </Dialog>
            </div>
          </div>

          <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
            <div className="flex min-w-0 flex-col gap-4">
              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionBody className="px-4 py-3">
                  <div
                    className={cn(
                      "flex min-h-[130px] flex-col items-center justify-center rounded-md border border-dashed border-accent/55 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-accent/70",
                      isRunning && "cursor-not-allowed opacity-70"
                    )}
                    role="group"
                    aria-label="Encrypt file drop zone"
                    aria-disabled={isRunning}
                    onDragOver={handleTargetDragOver}
                    onDrop={handleTargetDrop}
                  >
                    <div className="flex size-7 items-center justify-center rounded-md bg-accent/10 text-accent">
                      <UploadCloud className="size-4" aria-hidden />
                    </div>
                    <h3 className="mt-2.5 font-display text-base font-semibold tracking-tight text-primary">Drop files or folders to encrypt</h3>
                    <div className="mt-3 flex flex-wrap justify-center gap-2">
                      <Button variant="default" onClick={() => void pickFiles()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse Files
                      </Button>
                      <Button variant="secondary" onClick={() => void pickFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse Folder
                      </Button>
                    </div>
                  </div>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Selected Files</SectionTitle>
                      <p className="mt-1 text-sm leading-snug text-secondary">
                        {paths.length > 0
                          ? isDescribingPaths
                            ? "Reading selected file details for this run."
                            : `${paths.length} item${paths.length === 1 ? "" : "s"} selected for this run.`
                          : "Add files or folders to start an encryption run."}
                      </p>
                    </div>
                    <Badge variant="outline">{paths.length} selected</Badge>
                  </div>
                </SectionHeader>
                <SectionBody className="px-4 py-3">
                  {pathWarnings.length > 0 ? (
                    <div className="mb-3 rounded-md border border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
                      {pathWarnings[0]}
                    </div>
                  ) : null}

                  {paths.length === 0 ? (
                    <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                      No files selected. Drop files above or choose files to continue.
                    </div>
                  ) : (
                    <div className="overflow-hidden rounded-md border border-border/80 bg-background/35">
                      <div className="grid grid-cols-[minmax(0,1.8fr)_minmax(150px,0.9fr)_100px_120px_56px] gap-3 border-b border-border/80 px-3 py-2.5 font-mono text-[10px] uppercase tracking-[0.16em] text-muted">
                        <span>Name</span>
                        <span>Type</span>
                        <span>Size</span>
                        <span>Status</span>
                        <span>Remove</span>
                      </div>
                      <div className="divide-y divide-border/80">
                        {(pathDetails.length > 0 ? pathDetails : paths.map((path) => ({
                          fullPath: path,
                          displayName: fileName(path),
                          itemType: "File",
                          sizeBytes: 0,
                          sizeDisplay: "Checked at run start",
                          isDirectory: false,
                          details: "Preparing details",
                        }))).map((item) => {
                          const itemStatus = isDescribingPaths
                            ? { label: "Waiting", dotClass: "bg-accent-blue", textClass: "text-accent-blue" }
                            : item.isDirectory
                              ? { label: "Waiting", dotClass: "bg-accent-blue", textClass: "text-accent-blue" }
                              : { label: "Ready", dotClass: "bg-accent-green", textClass: "text-accent-green" }

                          return (
                          <div key={item.fullPath} className="grid min-h-10 grid-cols-[minmax(0,1.8fr)_minmax(150px,0.9fr)_100px_120px_56px] gap-3 px-3 py-2">
                            <div className="min-w-0">
                              <div className="flex items-center gap-3">
                                <FileTypeIcon filename={item.displayName} />
                                <div className="min-w-0">
                                  <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{item.displayName}</div>
                                  <div className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{item.details}</div>
                                </div>
                              </div>
                            </div>
                            <div className="truncate text-sm text-secondary">{item.itemType}</div>
                            <div className="text-sm text-secondary">{item.sizeDisplay}</div>
                            <div className="flex items-center gap-2">
                              <span className={cn("size-2.5 rounded-md", itemStatus.dotClass)} />
                              <span className={cn("text-sm", itemStatus.textClass)}>{itemStatus.label}</span>
                            </div>
                            <div className="flex items-center">
                              <button
                                type="button"
                                className="rounded-md p-1.5 text-muted transition-colors hover:bg-background/40 hover:text-primary disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-transparent disabled:hover:text-muted"
                                aria-label={`Remove ${item.displayName}`}
                                disabled={isRunning}
                                onClick={() => removeTarget(item.fullPath)}
                              >
                                <Trash2 className="size-4" aria-hidden />
                              </button>
                            </div>
                          </div>
                          )
                        })}
                      </div>
                    </div>
                  )}
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Password</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <div className="grid gap-3">
                    <Field label="Password">
                      <PasswordInput value={password} onChange={setPassword} placeholder="Enter a strong password" label="Password" disabled={isRunning} />
                    </Field>
                    <Field label="Confirm Password">
                      <PasswordInput value={confirmPassword} onChange={setConfirmPassword} placeholder="Confirm password" label="Confirm Password" disabled={isRunning} />
                    </Field>
                  </div>

                  <div className="flex flex-col gap-3">
                    <div className="flex items-center justify-between gap-3 text-sm">
                      <span className="text-secondary">Password Strength</span>
                      <span className={cn("font-display font-semibold", passwordStrength.tone)}>{passwordStrength.label}</span>
                    </div>
                    <div className="h-2 overflow-hidden rounded-md bg-background/50">
                      <div
                        className={cn("h-full rounded-md transition-[width] duration-200", passwordStrength.barClass)}
                        style={{ width: `${passwordStrength.score}%` }}
                      />
                    </div>
                    <p className="text-sm text-secondary">{passwordStrength.feedback}</p>
                  </div>

                  <div className="rounded-md border border-accent/20 bg-accent/6 px-3 py-2 text-sm leading-snug text-secondary">
                    <div className="flex items-start gap-2.5">
                      <Info className="mt-0.5 size-4 text-accent" aria-hidden />
                      <span>Use a strong password. FileLocker cannot recover lost passwords for you.</span>
                    </div>
                  </div>

                  <div className="app-inline-notice app-inline-notice-warning">
                    <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                    <span>Your password is never stored or transmitted. You are solely responsible for keeping it safe.</span>
                  </div>
                </SectionBody>
              </Section>

              {(results.length > 0 || latestProgress || operationError) ? (
                <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                  <SectionHeader className="border-b border-border/80 px-4 py-3">
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Run Progress</SectionTitle>
                  </SectionHeader>
                  <SectionBody className="flex flex-col gap-3 px-4 py-3">
                    {latestProgress ? (
                      <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} />
                    ) : null}
                    {operationError ? <OperationErrorNotice message={operationError} /> : null}
                    {results.length > 0 ? <ResultList results={results} onReveal={onReveal} /> : null}
                  </SectionBody>
                </Section>
              ) : null}
            </div>

            <aside className="flex min-w-0 flex-col gap-4">
              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Encryption Options</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <Field label="Algorithm">
                    <Select value="AES-256-GCM" onValueChange={() => undefined} disabled={isRunning}>
                      <SelectTrigger>
                        <SelectValue placeholder="Algorithm" />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectGroup>
                          <SelectItem value="AES-256-GCM">AES-256-GCM</SelectItem>
                        </SelectGroup>
                      </SelectContent>
                    </Select>
                  </Field>

                  <Field label="Output Location">
                    <div className="flex flex-col gap-2 sm:flex-row">
                      <Input value={encryptOutputDirectory} onChange={(event) => setEncryptOutputDirectory(event.target.value)} placeholder="Leave blank to save next to source files" disabled={isRunning} />
                      <Button type="button" variant="secondary" onClick={() => void pickEncryptOutputFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse
                      </Button>
                    </div>
                  </Field>

                  {encryptOutputSuggestion?.suggestedPath ? (
                    <div className="rounded-md border border-accent/20 bg-accent/6 px-3 py-2 text-sm leading-snug text-secondary">
                      Suggested folder: <span className="font-mono text-primary">{encryptOutputSuggestion.suggestedPath}</span>
                    </div>
                  ) : null}

                  <div className="flex flex-col gap-3">
                    <Toggle label="Save output next to source files" checked={savesNextToSource} onChange={handleSaveNextToSourceChange} disabled={isRunning} />
                    <Toggle label="Compress before encryption" checked={options.compressFiles} onChange={(value) => setOptions({ ...options, compressFiles: value })} disabled={isRunning} />
                    <Toggle label="Delete originals after successful encryption" checked={options.removeOriginalsAfterSuccess} onChange={(value) => setOptions({ ...options, removeOriginalsAfterSuccess: value })} disabled={isRunning} />
                    <Toggle label="Package folders into one locked file" checked={options.packageFolders} onChange={(value) => setOptions({ ...options, packageFolders: value })} disabled={isRunning} />
                  </div>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Advanced Controls</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <Field label="Keyfile Path">
                    <Input value={keyfilePath} onChange={(event) => setKeyfilePath(event.target.value)} placeholder="Optional keyfile path" disabled={isRunning} />
                  </Field>
                  <Field label="Recovery Key">
                    <Input value={recoveryKey} onChange={(event) => setRecoveryKey(event.target.value)} placeholder="Optional recovery key" disabled={isRunning} />
                  </Field>
                  <Field label="Backup Folder">
                    <div className="flex flex-col gap-2 sm:flex-row">
                      <Input value={backupFolderPath} onChange={(event) => setBackupFolderPath(event.target.value)} placeholder="Optional backup folder" disabled={isRunning} />
                      <Button type="button" variant="secondary" onClick={() => void pickBackupFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse
                      </Button>
                    </div>
                  </Field>
                  <Field label="Metadata Note">
                    <Input value={metadataNotes} onChange={(event) => setMetadataNotes(event.target.value)} placeholder="Optional metadata note" disabled={isRunning} />
                  </Field>
                  <Field label="Timestamp Policy">
                    <Select value={options.outputTimestampPolicy} onValueChange={(value) => setOptions({ ...options, outputTimestampPolicy: value })} disabled={isRunning}>
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
                  <div className="flex flex-col gap-3">
                    <Toggle label="Verify after write" checked={options.verifyAfterWrite} onChange={(value) => setOptions({ ...options, verifyAfterWrite: value })} disabled={isRunning} />
                    <Toggle label="Scramble output names" checked={options.scrambleNames} onChange={(value) => setOptions({ ...options, scrambleNames: value })} disabled={isRunning} />
                    <Toggle label="PNG carrier output" checked={options.useSteganography} onChange={(value) => setOptions({ ...options, useSteganography: value })} disabled={isRunning} />
                    <Toggle label="Use secure delete for removals" checked={options.secureDeleteOriginals} onChange={(value) => setOptions({ ...options, secureDeleteOriginals: value })} disabled={isRunning} />
                  </div>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-2 px-4 py-3">
                  <SummaryRow icon={Files} label="Files selected" value={String(paths.length)} />
                  <SummaryRow icon={HardDrive} label="Total size" value={paths.length > 0 ? totalSizeDisplay : "Calculated at run start"} />
                  <SummaryRow icon={FolderOpen} label="Output" value={encryptOutputSummary} />
                  <SummaryRow icon={Lock} label="Mode" value="AES-256-GCM" />
                  <SummaryRow icon={hasOperationError ? AlertTriangle : encryptCanStart ? CheckCircle2 : Clock3} label="Status" value={encryptStatusText} good={encryptCanStart && !hasOperationError} />
                </SectionBody>
                <SectionFooter className="flex flex-col gap-2 border-t border-border bg-transparent px-4 py-3">
                  {destructiveOptionsEnabled ? (
                    <AlertDialog>
                      <AlertDialogTrigger asChild>
                        <Button className="w-full" disabled={isRunning || !encryptCanStart}>
                          <Lock data-icon="inline-start" />
                          {isRunning ? "Encrypting" : "Start Encryption"}
                        </Button>
                      </AlertDialogTrigger>
                      <AlertDialogContent>
                        <AlertDialogHeader>
                          <AlertDialogTitle>Confirm destructive options</AlertDialogTitle>
                          <AlertDialogDescription>
                            Selected options can remove original source files after the run succeeds. Type DELETE to continue.
                          </AlertDialogDescription>
                        </AlertDialogHeader>
                        <Input value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} placeholder="DELETE" disabled={isRunning} />
                        <AlertDialogFooter>
                          <AlertDialogCancel>Cancel</AlertDialogCancel>
                          <AlertDialogAction disabled={deleteConfirmation !== "DELETE" || isRunning} onClick={run}>Continue</AlertDialogAction>
                        </AlertDialogFooter>
                      </AlertDialogContent>
                    </AlertDialog>
                  ) : (
                    <Button className="w-full" onClick={() => void run()} disabled={isRunning || !encryptCanStart}>
                      <Lock data-icon="inline-start" />
                      {isRunning ? "Encrypting" : "Start Encryption"}
                    </Button>
                  )}
                  <Button className="w-full" variant="outline" onClick={clearTargets} disabled={paths.length === 0 || isRunning}>
                    <Trash2 data-icon="inline-start" />
                    Clear Selection
                  </Button>
                </SectionFooter>
              </Section>

              <div className="app-inline-notice app-inline-notice-warning">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <p className="text-sm leading-snug text-secondary">Delete originals stays off by default. If you enable it, verify your password and output location first.</p>
              </div>
            </aside>
          </div>
        </div>
      </div>
    )
  }

  if (isDecrypt) {
    return (
      <div className="security-page">
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
            <div className="min-w-0">
              <div className="flex items-center gap-3">
                <div className="flex size-8 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent ">
                  <Unlock className="size-4" aria-hidden />
                </div>
                <div>
                  <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Decrypt Files</h2>
                </div>
              </div>
            </div>

            <div className="flex shrink-0 flex-wrap gap-2">
              <Dialog>
                <DialogTrigger asChild>
                  <Button variant="outline">
                    <BookOpen data-icon="inline-start" />
                    Decryption Guide
                  </Button>
                </DialogTrigger>
                <DialogContent className="sm:max-w-2xl">
                  <DialogHeader>
                    <DialogTitle>Decryption Guide</DialogTitle>
                    <DialogDescription>What to check before you restore locked files locally with FileLocker.</DialogDescription>
                  </DialogHeader>
                  <div className="flex flex-col gap-3">
                    {decryptionGuidePoints.map((point) => (
                      <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                        {point}
                      </div>
                    ))}
                  </div>
                </DialogContent>
              </Dialog>

              <Dialog>
                <DialogTrigger asChild>
                  <Button variant="outline">
                    <History data-icon="inline-start" />
                    History
                  </Button>
                </DialogTrigger>
                <DialogContent className="sm:max-w-3xl">
                  <DialogHeader>
                    <DialogTitle>Recent Decryption History</DialogTitle>
                    <DialogDescription>Local recent restore activity from the dashboard history feed.</DialogDescription>
                  </DialogHeader>
                  {decryptHistory.length === 0 ? (
                    <div className="rounded-md border border-border bg-background/40 px-3 py-3 text-sm text-secondary">
                      No local decryption history is available yet.
                    </div>
                  ) : (
                    <div className="flex flex-col gap-3">
                      {decryptHistory.map((entry) => (
                        <div key={entry.id} className="rounded-md border border-border bg-background/35 px-4 py-4">
                          <div className="flex flex-wrap items-center justify-between gap-3">
                            <div>
                              <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{entry.profileName || "FileLocker"}</div>
                              <div className="mt-1 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{new Date(entry.timestampUtc).toLocaleString()}</div>
                            </div>
                            <Badge variant="outline">{entry.cancelled ? "Cancelled" : `${entry.successCount} completed / ${entry.failureCount} failed`}</Badge>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </DialogContent>
              </Dialog>
            </div>
          </div>

          <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
            <div className="flex min-w-0 flex-col gap-4">
              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionBody className="px-4 py-3">
                  <div
                    className={cn(
                      "flex min-h-[130px] flex-col items-center justify-center rounded-md border border-dashed border-accent/55 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-accent/70",
                      isRunning && "cursor-not-allowed opacity-70"
                    )}
                    role="group"
                    aria-label="Decrypt file drop zone"
                    aria-disabled={isRunning}
                    onDragOver={handleTargetDragOver}
                    onDrop={handleTargetDrop}
                  >
                    <div className="flex size-7 items-center justify-center rounded-md bg-accent/10 text-accent">
                      <UploadCloud className="size-4" aria-hidden />
                    </div>
                    <h3 className="mt-2.5 font-display text-base font-semibold tracking-tight text-primary">Drop encrypted files to decrypt</h3>
                    <div className="mt-3 flex flex-wrap justify-center gap-2">
                      <Button variant="default" onClick={() => void pickFiles()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse Files
                      </Button>
                      <Button variant="secondary" onClick={() => void pickFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse Folder
                      </Button>
                    </div>
                  </div>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Selected Encrypted Files</SectionTitle>
                      <p className="mt-1 text-sm leading-snug text-secondary">
                        {paths.length > 0
                          ? isDescribingPaths
                            ? "Reading selected encrypted file details for this run."
                            : `${paths.length} item${paths.length === 1 ? "" : "s"} selected for decryption.`
                          : "Add encrypted files or folders to start a restore run."}
                      </p>
                    </div>
                    <Badge variant="outline">{paths.length} selected</Badge>
                  </div>
                </SectionHeader>
                <SectionBody className="px-4 py-3">
                  {pathWarnings.length > 0 ? (
                    <div className="mb-3 rounded-md border border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
                      {pathWarnings[0]}
                    </div>
                  ) : null}

                  {paths.length === 0 ? (
                    <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                      No encrypted files selected. Drop locked files above or choose files to continue.
                    </div>
                  ) : (
                    <div className="overflow-hidden rounded-md border border-border/80 bg-background/35">
                      <div className="grid grid-cols-[minmax(0,1.9fr)_minmax(150px,0.8fr)_100px_150px_56px] gap-3 border-b border-border/80 px-3 py-2.5 font-mono text-[10px] uppercase tracking-[0.16em] text-muted">
                        <span>Name</span>
                        <span>Type</span>
                        <span>Size</span>
                        <span>Status</span>
                        <span>Remove</span>
                      </div>
                      <div className="divide-y divide-border/80">
                        {(pathDetails.length > 0 ? pathDetails : paths.map((path) => ({
                          fullPath: path,
                          displayName: fileName(path),
                          itemType: "Locked File",
                          sizeBytes: 0,
                          sizeDisplay: "Checked at run start",
                          isDirectory: false,
                          details: "Preparing details",
                        }))).map((item) => {
                          const itemStatus = isDescribingPaths
                            ? { label: "Waiting", dotClass: "bg-accent-blue", textClass: "text-accent-blue" }
                            : !password
                              ? { label: "Password required", dotClass: "bg-accent-orange", textClass: "text-accent-orange" }
                              : item.isDirectory
                                ? { label: "Waiting", dotClass: "bg-accent-blue", textClass: "text-accent-blue" }
                                : { label: "Ready", dotClass: "bg-accent-green", textClass: "text-accent-green" }

                          return (
                            <div key={item.fullPath} className="grid min-h-10 grid-cols-[minmax(0,1.9fr)_minmax(150px,0.8fr)_100px_150px_56px] gap-3 px-3 py-2">
                              <div className="min-w-0">
                                <div className="flex items-center gap-3">
                                  <FileTypeIcon filename={item.displayName} />
                                  <div className="min-w-0">
                                    <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{item.displayName}</div>
                                    <div className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{item.details}</div>
                                  </div>
                                </div>
                              </div>
                              <div className="truncate text-sm text-secondary">{item.itemType}</div>
                              <div className="text-sm text-secondary">{item.sizeDisplay}</div>
                              <div className="flex items-center gap-2">
                                <span className={cn("size-2.5 rounded-md", itemStatus.dotClass)} />
                                <span className={cn("text-sm", itemStatus.textClass)}>{itemStatus.label}</span>
                              </div>
                              <div className="flex items-center">
                                <button
                                  type="button"
                                  className="rounded-md p-1.5 text-muted transition-colors hover:bg-background/40 hover:text-primary disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-transparent disabled:hover:text-muted"
                                  aria-label={`Remove ${item.displayName}`}
                                  disabled={isRunning}
                                  onClick={() => removeTarget(item.fullPath)}
                                >
                                  <Trash2 className="size-4" aria-hidden />
                                </button>
                              </div>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )}
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Password</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <Field label="Password">
                    <PasswordInput value={password} onChange={setPassword} placeholder="Enter the password used for these files" label="Password" disabled={isRunning} />
                  </Field>

                  <p className="text-sm leading-snug text-secondary">
                    Enter the password used when these files were encrypted. If this job also uses a recovery key or keyfile, you can add them below.
                  </p>

                  <div className="app-inline-notice app-inline-notice-warning">
                    <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                    <span>FileLocker cannot recover files if the password is incorrect or lost.</span>
                  </div>
                </SectionBody>
              </Section>

              {(results.length > 0 || latestProgress || operationError) ? (
                <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                  <SectionHeader className="border-b border-border/80 px-4 py-3">
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Run Progress</SectionTitle>
                  </SectionHeader>
                  <SectionBody className="flex flex-col gap-3 px-4 py-3">
                    {latestProgress ? (
                      <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} />
                    ) : null}
                    {operationError ? <OperationErrorNotice message={operationError} /> : null}
                    {results.length > 0 ? <ResultList results={results} onReveal={onReveal} /> : null}
                  </SectionBody>
                </Section>
              ) : null}
            </div>

            <aside className="flex min-w-0 flex-col gap-4">
              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Decryption Options</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <Field label="Output Location">
                    <div className="flex flex-col gap-2 sm:flex-row">
                      <Input value={decryptOutputDirectory} onChange={(event) => setDecryptOutputDirectory(event.target.value)} placeholder="Leave blank to save next to encrypted files" disabled={isRunning} />
                      <Button type="button" variant="secondary" onClick={() => void pickDecryptOutputFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse
                      </Button>
                    </div>
                  </Field>

                  <div className="flex flex-col gap-3">
                    <Toggle label="Save output next to encrypted files" checked={decryptSavesNextToEncrypted} onChange={handleSaveNextToEncryptedChange} disabled={isRunning} />
                    <Toggle label="Restore original filenames" checked={options.restoreOriginalFilenames} onChange={(value) => setOptions({ ...options, restoreOriginalFilenames: value })} disabled={isRunning} />
                    <Toggle label="Preserve folder structure" checked={options.preserveFolderStructure} onChange={(value) => setOptions({ ...options, preserveFolderStructure: value })} disabled={isRunning} />
                    <Toggle label="Delete encrypted files after successful decryption" checked={options.removeOriginalsAfterSuccess} onChange={(value) => setOptions({ ...options, removeOriginalsAfterSuccess: value })} disabled={isRunning} />
                  </div>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Additional Unlock Options</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-3 px-4 py-3">
                  <Field label="Keyfile Path">
                    <Input value={keyfilePath} onChange={(event) => setKeyfilePath(event.target.value)} placeholder="Optional keyfile path" disabled={isRunning} />
                  </Field>
                  <Field label="Recovery Key">
                    <Input value={recoveryKey} onChange={(event) => setRecoveryKey(event.target.value)} placeholder="Optional recovery key" disabled={isRunning} />
                  </Field>
                  <Field label="Backup Folder">
                    <div className="flex flex-col gap-2 sm:flex-row">
                      <Input value={backupFolderPath} onChange={(event) => setBackupFolderPath(event.target.value)} placeholder="Optional backup folder" disabled={isRunning} />
                      <Button type="button" variant="secondary" onClick={() => void pickBackupFolder()} disabled={isRunning}>
                        <FolderOpen data-icon="inline-start" />
                        Browse
                      </Button>
                    </div>
                  </Field>
                </SectionBody>
              </Section>

              <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
                <SectionHeader className="border-b border-border/80 px-4 py-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
                </SectionHeader>
                <SectionBody className="flex flex-col gap-2 px-4 py-3">
                  <SummaryRow icon={Files} label="Files selected" value={String(paths.length)} />
                  <SummaryRow icon={HardDrive} label="Total size" value={paths.length > 0 ? totalSizeDisplay : "Calculated at run start"} />
                  <SummaryRow icon={FolderOpen} label="Output" value={decryptOutputSummary} />
                  <SummaryRow icon={Unlock} label="Mode" value="AES-256-GCM" />
                  <SummaryRow icon={hasOperationError ? AlertTriangle : decryptCanStart ? CheckCircle2 : Clock3} label="Status" value={decryptStatusText} good={decryptCanStart && !hasOperationError} />
                </SectionBody>
                <SectionFooter className="flex flex-col gap-2 border-t border-border bg-transparent px-4 py-3">
                  {destructiveOptionsEnabled ? (
                    <AlertDialog>
                      <AlertDialogTrigger asChild>
                        <Button className="w-full" disabled={isRunning || !decryptCanStart}>
                          <Unlock data-icon="inline-start" />
                          {isRunning ? "Decrypting" : "Start Decryption"}
                        </Button>
                      </AlertDialogTrigger>
                      <AlertDialogContent>
                        <AlertDialogHeader>
                          <AlertDialogTitle>Confirm destructive options</AlertDialogTitle>
                          <AlertDialogDescription>
                            Selected options can remove encrypted source files after the run succeeds. Type DELETE to continue.
                          </AlertDialogDescription>
                        </AlertDialogHeader>
                        <Input value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} placeholder="DELETE" disabled={isRunning} />
                        <AlertDialogFooter>
                          <AlertDialogCancel>Cancel</AlertDialogCancel>
                          <AlertDialogAction disabled={deleteConfirmation !== "DELETE" || isRunning} onClick={run}>Continue</AlertDialogAction>
                        </AlertDialogFooter>
                      </AlertDialogContent>
                    </AlertDialog>
                  ) : (
                    <Button className="w-full" onClick={() => void run()} disabled={isRunning || !decryptCanStart}>
                      <Unlock data-icon="inline-start" />
                      {isRunning ? "Decrypting" : "Start Decryption"}
                    </Button>
                  )}
                  <Button className="w-full" variant="outline" onClick={clearTargets} disabled={paths.length === 0 || isRunning}>
                    <Trash2 data-icon="inline-start" />
                    Clear Selection
                  </Button>
                </SectionFooter>
              </Section>

              <div className="app-inline-notice app-inline-notice-warning">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <p className="text-sm leading-snug text-secondary">If you turn on encrypted-file removal, confirm your password and output location first so you do not lose the only copy you can still open.</p>
              </div>
            </aside>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="security-page">
      <section className="security-section">
        <div className="mb-4 flex items-start gap-3">
          <Icon className="mt-1 size-4 text-accent" aria-hidden />
          <div>
            <div className="security-section-title">{title}</div>
            <p className="security-description">{description}</p>
          </div>
        </div>
        <FileDropZone
          paths={paths}
          onPathsChange={setPaths}
          onPickFiles={pickFiles}
          onPickFolder={pickFolder}
          title={isEncrypt ? "Source Files" : "Encrypted Payloads"}
          description={isEncrypt ? "Pick files or folders to protect." : "Pick .locked files or PNG carriers."}
          disabled={isRunning}
        />
        {submitAttempted && paths.length === 0 ? <p className="mt-3 text-sm text-accent-red">Select at least one file or folder.</p> : null}
      </section>

      <section className="security-section">
        <div className="mb-4">
          <div className="security-section-title">Secret Material</div>
          <p className="security-description">Passwords, recovery keys, and keyfile paths stay inside FileLocker.</p>
        </div>
        <div className="grid gap-4 md:grid-cols-2">
          <Field label={isEncrypt ? "Password" : "Unlock Password"}>
            <PasswordInput value={password} onChange={setPassword} placeholder={isEncrypt ? "Password" : "Unlock password"} label={isEncrypt ? "Password" : "Unlock Password"} disabled={isRunning} />
            {submitAttempted && !password ? <p className="mt-2 text-sm text-accent-red">{isEncrypt ? "Password is required." : "Unlock password is required."}</p> : null}
          </Field>
          {isEncrypt ? (
            <Field label="Confirm Password">
              <PasswordInput value={confirmPassword} onChange={setConfirmPassword} placeholder="Confirm password" label="Confirm Password" disabled={isRunning} />
              {password && confirmPassword && password !== confirmPassword ? <p className="mt-2 text-sm text-accent-red">Passwords do not match.</p> : null}
            </Field>
          ) : null}
          <Field label="Keyfile Path">
            <Input value={keyfilePath} onChange={(event) => setKeyfilePath(event.target.value)} placeholder="Optional keyfile path" disabled={isRunning} />
          </Field>
          <Field label="Recovery Key">
            <Input value={recoveryKey} onChange={(event) => setRecoveryKey(event.target.value)} placeholder="Optional recovery key" disabled={isRunning} />
          </Field>
        </div>
      </section>

      <section className="security-section">
        <div className="mb-4">
          <div className="security-section-title">Run Options</div>
          <p className="security-description">FileLocker checks every option before touching your files.</p>
        </div>
        <div className="grid border-t border-border md:grid-cols-2 md:[&>*:nth-child(odd)]:border-r">
          {isEncrypt ? <Toggle label="Compress before encryption" checked={options.compressFiles} onChange={(value) => setOptions({ ...options, compressFiles: value })} disabled={isRunning} /> : null}
          {isEncrypt ? <Toggle label="Scramble output names" checked={options.scrambleNames} onChange={(value) => setOptions({ ...options, scrambleNames: value })} disabled={isRunning} /> : null}
          {isEncrypt ? <Toggle label="PNG carrier output" checked={options.useSteganography} onChange={(value) => setOptions({ ...options, useSteganography: value })} disabled={isRunning} /> : null}
          {isEncrypt ? <Toggle label="Package folders" checked={options.packageFolders} onChange={(value) => setOptions({ ...options, packageFolders: value })} disabled={isRunning} /> : null}
          <Toggle label="Verify after write" checked={options.verifyAfterWrite} onChange={(value) => setOptions({ ...options, verifyAfterWrite: value })} disabled={isRunning} />
          <Toggle label={isEncrypt ? "Delete originals after success" : "Delete encrypted files after success"} checked={options.removeOriginalsAfterSuccess} onChange={(value) => setOptions({ ...options, removeOriginalsAfterSuccess: value })} disabled={isRunning} />
          <Toggle label="Use secure delete for removals" checked={options.secureDeleteOriginals} onChange={(value) => setOptions({ ...options, secureDeleteOriginals: value })} disabled={isRunning} />
          {isDecrypt ? <Toggle label="Restore original filenames" checked={options.restoreOriginalFilenames} onChange={(value) => setOptions({ ...options, restoreOriginalFilenames: value })} disabled={isRunning} /> : null}
          {isDecrypt ? <Toggle label="Preserve folder structure" checked={options.preserveFolderStructure} onChange={(value) => setOptions({ ...options, preserveFolderStructure: value })} disabled={isRunning} /> : null}
        </div>
      </section>

      <section className="security-section">
        <div className="mb-4">
          <div className="security-section-title">Output Control</div>
          <p className="security-description">
            Leave the output folder blank to write beside each source file. For folder selections, using a separate sibling folder keeps `.locked` copies out of the source tree.
          </p>
        </div>
        <div className="grid gap-4 md:grid-cols-2">
          {isEncrypt ? (
            <Field label="Encrypt Output Folder">
              <div className="flex flex-col gap-2">
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input value={encryptOutputDirectory} onChange={(event) => setEncryptOutputDirectory(event.target.value)} placeholder="Leave blank to save beside the source file" disabled={isRunning} />
                  <Button type="button" variant="secondary" onClick={pickEncryptOutputFolder} disabled={isRunning}>
                    <FolderOpen data-icon="inline-start" />
                    Browse...
                  </Button>
                </div>
                {encryptOutputSuggestion?.suggestedPath ? (
                  <div className="flex flex-wrap gap-2">
                    <Button type="button" variant="outline" onClick={() => setEncryptOutputDirectory(encryptOutputSuggestion.suggestedPath ?? "")} disabled={isRunning}>
                      Use Suggested Folder
                    </Button>
                    {encryptOutputDirectory ? (
                      <Button type="button" variant="ghost" onClick={() => setEncryptOutputDirectory("")} disabled={isRunning}>
                        Use Source Folders Instead
                      </Button>
                    ) : null}
                  </div>
                ) : null}
                {encryptOutputSuggestion?.suggestedPath ? (
                  <p className="text-xs text-muted">
                    Suggested output: <span className="font-mono">{encryptOutputSuggestion.suggestedPath}</span>
                  </p>
                ) : null}
              </div>
            </Field>
          ) : null}
          {isDecrypt || kind === "verify" ? (
            <Field label="Decrypt Output Folder">
              <div className="flex flex-col gap-2 sm:flex-row">
                <Input value={decryptOutputDirectory} onChange={(event) => setDecryptOutputDirectory(event.target.value)} placeholder="Leave blank to save beside the encrypted file" disabled={isRunning} />
                <Button type="button" variant="secondary" onClick={pickDecryptOutputFolder} disabled={isRunning}>
                  <FolderOpen data-icon="inline-start" />
                  Browse...
                </Button>
              </div>
            </Field>
          ) : null}
          <Field label="Backup Folder">
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input value={backupFolderPath} onChange={(event) => setBackupFolderPath(event.target.value)} placeholder="Optional backup folder" disabled={isRunning} />
              <Button type="button" variant="secondary" onClick={pickBackupFolder} disabled={isRunning}>
                <FolderOpen data-icon="inline-start" />
                Browse...
              </Button>
            </div>
          </Field>
          <Field label="Metadata Note">
            <Input value={metadataNotes} onChange={(event) => setMetadataNotes(event.target.value)} placeholder="Optional metadata note" disabled={isRunning} />
          </Field>
          <Field label="Timestamp Policy">
            <Select value={options.outputTimestampPolicy} onValueChange={(value) => setOptions({ ...options, outputTimestampPolicy: value })} disabled={isRunning}>
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
        </div>
      </section>

      <section className="security-section">
        {latestProgress ? <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} /> : null}
        {operationError ? <OperationErrorNotice message={operationError} /> : null}
        {destructiveOptionsEnabled ? (
          <AlertDialog>
            <AlertDialogTrigger asChild>
              <Button className="mt-4 w-full" disabled={isRunning || paths.length === 0}>
                <Icon data-icon="inline-start" />
                {isRunning ? "Running" : title}
              </Button>
            </AlertDialogTrigger>
            <AlertDialogContent>
              <AlertDialogHeader>
                <AlertDialogTitle>Confirm destructive options</AlertDialogTitle>
                <AlertDialogDescription>Selected options can remove original or encrypted source files after the run succeeds. Type DELETE to continue.</AlertDialogDescription>
              </AlertDialogHeader>
              <Input value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} placeholder="DELETE" disabled={isRunning} />
              <AlertDialogFooter>
                <AlertDialogCancel>Cancel</AlertDialogCancel>
                <AlertDialogAction disabled={deleteConfirmation !== "DELETE" || isRunning} onClick={run}>Continue</AlertDialogAction>
              </AlertDialogFooter>
            </AlertDialogContent>
          </AlertDialog>
        ) : (
          <Button className="mt-4 w-full" onClick={run} disabled={isRunning || paths.length === 0}>
            <Icon data-icon="inline-start" />
            {isRunning ? "Running" : title}
          </Button>
        )}
      </section>

      <ResultList results={results} onReveal={onReveal} />
    </div>
  )
}

export function SecureDeletePage({
  invoke,
  onDashboardUpdate,
  onReveal,
  dashboard,
  droppedPaths = [],
  onDroppedPathsHandled,
}: Pick<FileOperationPageProps, "invoke" | "onDashboardUpdate" | "onReveal" | "dashboard" | "droppedPaths" | "onDroppedPathsHandled">) {
  const [paths, setPaths] = useState<string[]>([])
  const [confirmed, setConfirmed] = useState(false)
  const [results, setResults] = useState<FileOperationResult[]>([])
  const [actionError, setActionError] = useState("")
  const [isRunning, setIsRunning] = useState(false)
  const [pathDetails, setPathDetails] = useState<SelectedPathDescriptor[]>([])
  const [pathWarnings, setPathWarnings] = useState<string[]>([])
  const [totalSizeDisplay, setTotalSizeDisplay] = useState("Calculated at run start")
  const [isDescribingPaths, setIsDescribingPaths] = useState(false)
  const [deleteMethodId, setDeleteMethodId] = useState("dod")

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    if (isRunning) {
      toast.error("Wait for secure delete to finish before changing the selected targets.")
      onDroppedPathsHandled?.()
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, isRunning, onDroppedPathsHandled])

  useEffect(() => {
    if (paths.length === 0) {
      setPathDetails([])
      setPathWarnings([])
      setTotalSizeDisplay("Calculated at run start")
      return
    }

    let cancelled = false
    setIsDescribingPaths(true)

    invoke<DescribePathsResponse>("files.describePaths", { paths })
      .then((response) => {
        if (cancelled) {
          return
        }

        setPathDetails(response.items)
        setPathWarnings(response.warnings)
        setTotalSizeDisplay(response.totalSizeDisplay)
      })
      .catch((error) => {
        if (!cancelled) {
          setPathDetails([])
          setPathWarnings([getPathDetailsWarning(error)])
          setTotalSizeDisplay("Calculated at run start")
        }
      })
      .finally(() => {
        if (!cancelled) {
          setIsDescribingPaths(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [invoke, paths])

  useEffect(() => {
    setConfirmed(false)
    setResults((current) => current.length > 0 ? [] : current)
    setActionError("")
  }, [paths, deleteMethodId])

  const recentDeletes = useMemo(
    () => (dashboard?.history ?? []).filter((entry) => entry.operation === "Secure Delete").slice(0, 6),
    [dashboard]
  )
  const resolvedItems = pathDetails.length > 0
    ? pathDetails
    : paths.map((path) => ({
      fullPath: path,
      displayName: fileName(path),
      itemType: "File",
      sizeBytes: 0,
      sizeDisplay: "Checked at run start",
      isDirectory: false,
      details: path,
    }))
  const fileCount = resolvedItems.filter((item) => !item.isDirectory).length
  const folderCount = resolvedItems.filter((item) => item.isDirectory).length
  const completedCount = results.filter((item) => item.status === "Completed").length
  const failedCount = results.filter((item) => item.status !== "Completed").length
  const canStart = paths.length > 0 && confirmed
  const hasResults = results.length > 0
  const hasActionError = Boolean(actionError)
  const statusText = paths.length === 0
    ? "Add files or folders"
    : isRunning
      ? "Deleting"
      : hasResults
        ? failedCount > 0
          ? "Completed with issues"
          : "Completed"
        : hasActionError
          ? "Failed"
        : confirmed
          ? "Ready"
          : "Confirmation required"
  const statusGood = !hasActionError && ((confirmed && !isRunning && paths.length > 0) || (hasResults && failedCount === 0))
  const stateProgress = hasResults || hasActionError ? 100 : isRunning ? 58 : confirmed ? 18 : 0
  const progressMessage = isRunning
    ? "Secure delete is processing the selected targets."
    : hasActionError
      ? "Secure delete did not start."
    : hasResults
      ? `${completedCount} item${completedCount === 1 ? "" : "s"} completed${failedCount > 0 ? `, ${failedCount} failed` : ""}.`
      : "Ready to start secure deletion."
  const selectedMethod = secureDeleteMethods.find((method) => method.id === deleteMethodId) ?? secureDeleteMethods[1]

  async function pickFiles() {
    if (isRunning) {
      toast.error("Wait for secure delete to finish before changing the selected targets.")
      return
    }

    try {
      const result = await invoke<{ paths: string[] }>("files.pickFiles")
      setPaths((current) => mergeUniquePaths(current, result.paths))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick files.")
    }
  }

  async function pickFolder() {
    if (isRunning) {
      toast.error("Wait for secure delete to finish before changing the selected targets.")
      return
    }

    try {
      const result = await invoke<{ path: string }>("files.pickFolder")
      if (result.path) {
        setPaths((current) => mergeUniquePaths(current, [result.path]))
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick a folder.")
    }
  }

  async function run() {
    if (isRunning) {
      return
    }

    if (paths.length === 0) {
      toast.error("Select at least one file or folder.")
      return
    }

    if (!confirmed) {
      toast.error("Confirm secure delete before deleting selected files or folders.")
      return
    }

    setIsRunning(true)
    setActionError("")
    setResults([])
    try {
      const result = await invoke<{ results: FileOperationResult[]; dashboard?: unknown }>("secureDelete.delete", {
        paths,
        method: selectedMethod.id,
        overwritePasses: selectedMethod.passes,
        confirmation: "DELETE",
      })
      setResults(result.results)
      if (result.dashboard) {
        onDashboardUpdate(result.dashboard)
      }
      toast.success("Secure delete completed.")
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Secure delete failed."
      setActionError(message)
      toast.error(message)
    } finally {
      setIsRunning(false)
    }
  }

  function clearSelection() {
    if (isRunning) {
      return
    }

    setPaths([])
    setResults([])
    setActionError("")
    setConfirmed(false)
  }

  return (
    <div className="security-page">
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-3">
              <div className="flex size-8 items-center justify-center rounded-md border border-destructive/30 bg-destructive/10 text-destructive ">
                <Trash2 className="size-4" aria-hidden />
              </div>
              <div>
                <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Secure Delete</h2>
              </div>
            </div>
          </div>

          <div className="flex shrink-0 flex-wrap gap-2">
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline">
                  <BookOpen data-icon="inline-start" />
                  Deletion Guide
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-2xl">
                <DialogHeader>
                  <DialogTitle>Deletion Guide</DialogTitle>
                  <DialogDescription>What to review before you permanently remove local files with FileLocker.</DialogDescription>
                </DialogHeader>
                <div className="flex flex-col gap-3">
                  {secureDeleteGuidePoints.map((point) => (
                    <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                      {point}
                    </div>
                  ))}
                </div>
              </DialogContent>
            </Dialog>

            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline">
                  <History data-icon="inline-start" />
                  History
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-3xl">
                <DialogHeader>
                  <DialogTitle>Recent Secure Delete History</DialogTitle>
                  <DialogDescription>Local recent deletion activity from the dashboard history feed.</DialogDescription>
                </DialogHeader>
                {recentDeletes.length === 0 ? (
                  <div className="rounded-md border border-border bg-background/40 px-3 py-3 text-sm text-secondary">
                    No local secure delete history is available yet.
                  </div>
                ) : (
                  <div className="flex flex-col gap-3">
                    {recentDeletes.map((entry) => (
                      <div key={entry.id} className="rounded-md border border-border bg-background/35 px-4 py-4">
                        <div className="flex flex-wrap items-center justify-between gap-3">
                          <div>
                            <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{entry.profileName || "FileLocker"}</div>
                            <div className="mt-1 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{new Date(entry.timestampUtc).toLocaleString()}</div>
                          </div>
                          <Badge variant="outline">{entry.cancelled ? "Cancelled" : `${entry.successCount} completed / ${entry.failureCount} failed`}</Badge>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </DialogContent>
            </Dialog>
          </div>
        </div>

        <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
          <div className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-destructive/45 bg-transparent py-0 shadow-none ring-0">
              <SectionBody className="px-4 py-3">
                <div className="flex items-start gap-3">
                  <div className="flex size-8 shrink-0 items-center justify-center rounded-md border border-destructive/35 bg-destructive/12 text-destructive">
                    <AlertTriangle className="size-5" aria-hidden />
                  </div>
                  <div>
                    <div className="font-display text-base font-semibold tracking-tight text-primary">This action cannot be undone.</div>
                    <p id="secure-delete-drop-zone-description" className="mt-1 max-w-3xl text-sm leading-snug text-secondary">
                      Selected files and folders are removed permanently after FileLocker attempts a local overwrite pass. Review the selected paths carefully before you start.
                    </p>
                  </div>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionBody className="px-4 py-3">
                <div
                  className={cn(
                    "flex min-h-[150px] flex-col items-center justify-center rounded-md border border-dashed border-destructive/60 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-destructive/75",
                    isRunning && "cursor-not-allowed opacity-70"
                  )}
                  role="group"
                  aria-label="Secure delete target drop zone"
                  aria-describedby="secure-delete-drop-zone-description"
                  aria-disabled={isRunning}
                  onDragOver={(event: DragEvent<HTMLDivElement>) => {
                    event.preventDefault()
                    event.dataTransfer.dropEffect = isRunning ? "none" : "copy"
                  }}
                  onDrop={(event: DragEvent<HTMLDivElement>) => {
                    event.preventDefault()
                    if (isRunning) {
                      toast.error("Wait for secure delete to finish before changing the selected targets.")
                      return
                    }

                    const droppedPaths = Array.from(event.dataTransfer.files)
                      .map((file) => (file as File & { path?: string }).path)
                      .filter((path): path is string => Boolean(path))
                    if (droppedPaths.length > 0) {
                      setPaths((current) => mergeUniquePaths(current, droppedPaths))
                    }
                  }}
                >
                  <div className="flex size-8 items-center justify-center rounded-md border border-destructive/35 bg-destructive/10 text-destructive">
                    <Trash2 className="size-5" aria-hidden />
                  </div>
                  <h3 className="mt-3 font-display text-lg font-semibold tracking-tight text-primary">Drag &amp; drop files or folders to securely delete</h3>
                  <div className="mt-4 flex flex-wrap justify-center gap-2">
                    <Button variant="violet" onClick={() => void pickFiles()} disabled={isRunning}>
                      <FolderOpen data-icon="inline-start" />
                      Browse Files
                    </Button>
                    <Button variant="secondary" onClick={() => void pickFolder()} disabled={isRunning}>
                      <FolderOpen data-icon="inline-start" />
                      Browse Folder
                    </Button>
                  </div>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Selected Items</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">
                      {paths.length > 0
                        ? isDescribingPaths
                          ? "Reading selected target details for this delete run."
                          : `${paths.length} item${paths.length === 1 ? "" : "s"} selected for secure deletion.`
                        : "Add files or folders to start a secure delete run."}
                    </p>
                  </div>
                  <Badge variant="outline">{paths.length} selected</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                {pathWarnings.length > 0 ? (
                  <div className="mb-3 rounded-md border border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
                    {pathWarnings[0]}
                  </div>
                ) : null}

                {paths.length === 0 ? (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                    No delete targets selected. Drop files above or choose files to continue.
                  </div>
                ) : (
                  <div className="overflow-x-auto rounded-md border border-border/80 bg-background/35">
                    <div className="min-w-[920px]">
                      <div className="grid grid-cols-[minmax(0,1.65fr)_140px_100px_minmax(0,1.35fr)_110px_56px] gap-3 border-b border-border/80 px-3 py-2.5 font-mono text-[10px] uppercase tracking-[0.16em] text-muted">
                        <span>Name</span>
                        <span>Type</span>
                        <span>Size</span>
                        <span>Path</span>
                        <span>Status</span>
                        <span>Remove</span>
                      </div>
                      <div className="divide-y divide-border/80">
                        {resolvedItems.map((item) => {
                          const itemStatus = isDescribingPaths
                            ? { label: "Reading", dotClass: "bg-accent-blue", textClass: "text-accent-blue" }
                            : { label: "Ready", dotClass: "bg-accent-green", textClass: "text-accent-green" }

                          return (
                            <div key={item.fullPath} className="grid min-h-10 grid-cols-[minmax(0,1.65fr)_140px_100px_minmax(0,1.35fr)_110px_56px] gap-3 px-3 py-2">
                              <div className="min-w-0">
                                <div className="flex items-center gap-3">
                                  <FileTypeIcon filename={item.displayName} />
                                  <div className="min-w-0">
                                    <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{item.displayName}</div>
                                    <div className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{item.isDirectory ? "Folder target" : "File target"}</div>
                                  </div>
                                </div>
                              </div>
                              <div className="truncate text-sm text-secondary">{item.itemType}</div>
                              <div className="text-sm text-secondary">{item.sizeDisplay}</div>
                              <div className="truncate text-sm text-secondary">{item.fullPath}</div>
                              <div className="flex items-center gap-2">
                                <span className={cn("size-2.5 rounded-md", itemStatus.dotClass)} />
                                <span className={cn("text-sm", itemStatus.textClass)}>{itemStatus.label}</span>
                              </div>
                              <div className="flex items-center">
                                <button
                                  type="button"
                                  className="rounded-md p-1.5 text-muted transition-colors hover:bg-background/40 hover:text-primary"
                                  aria-label={`Remove ${item.displayName}`}
                                  disabled={isRunning}
                                  onClick={() => setPaths((current) => current.filter((path) => !areSameLocalPath(path, item.fullPath)))}
                                >
                                  <Trash2 className="size-4" aria-hidden />
                                </button>
                              </div>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  </div>
                )}
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Deletion Status</SectionTitle>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="text-sm leading-snug text-secondary">{progressMessage}</div>
                  <Badge variant={hasActionError ? "destructive" : hasResults ? "secondary" : "outline"}>{statusText}</Badge>
                </div>

                <div className="flex flex-col gap-3">
                  <div className="h-2 overflow-hidden rounded-md bg-background/45">
                    <div
                      className={cn(
                        "h-full transition-[width] duration-300",
                        hasActionError ? "bg-destructive" : hasResults ? "bg-accent-green" : isRunning ? "bg-accent-blue" : "bg-border"
                      )}
                      style={{ width: `${stateProgress}%` }}
                    />
                  </div>
                  <div className="grid grid-cols-3 gap-3 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">
                    <span className={cn(confirmed && "text-primary")}>Confirmed</span>
                    <span className={cn(isRunning && "text-primary")}>Deleting</span>
                    <span className={cn(hasResults && "text-primary")}>Completed</span>
                  </div>
                </div>

                {actionError ? (
                  <div className="rounded-md border border-destructive/35 bg-destructive/10 px-4 py-3 text-sm leading-snug text-red-100" role="status" aria-live="polite">
                    <div className="flex items-start gap-2.5">
                      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
                      <span>{actionError}</span>
                    </div>
                  </div>
                ) : null}

                <div className="rounded-md border border-accent/20 bg-accent/6 px-4 py-3 text-sm leading-[1.6] text-secondary">
                  <div className="flex items-start gap-3">
                    <Info className="mt-0.5 size-4 text-accent" aria-hidden />
                    <span>Secure delete uses the existing FileLocker overwrite path before final removal where the filesystem and storage device allow it.</span>
                  </div>
                </div>

                {results.length > 0 ? <ResultList results={results} onReveal={onReveal} /> : null}
              </SectionBody>
            </Section>
          </div>

          <aside className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-accent-purple/25 bg-accent-purple/10 text-accent-purple">
                    <ShieldCheck className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Deletion Method</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                {secureDeleteMethods.map((method) => {
                  const isSelected = selectedMethod.id === method.id
                  return (
                    <button
                      key={method.id}
                      type="button"
                      className={cn(
                        "flex w-full items-start gap-2.5 rounded-md border border-border/80 bg-background/35 px-3 py-2 text-left transition-colors hover:border-accent/60 hover:bg-background/55",
                        isSelected && "border-accent/70 bg-accent/10",
                        isRunning && "cursor-not-allowed opacity-60"
                      )}
                      disabled={isRunning}
                      onClick={() => setDeleteMethodId(method.id)}
                    >
                      <span className={cn("mt-0.5 flex size-5 items-center justify-center rounded-md border border-border text-transparent", isSelected && "border-accent bg-accent text-white")}>
                        <CheckCircle2 className="size-3.5" aria-hidden />
                      </span>
                      <span className="min-w-0 flex-1">
                        <span className="flex items-center justify-between gap-3">
                          <span className="font-display text-sm font-semibold tracking-tight text-primary">{method.label}</span>
                          <span className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{method.passes} pass{method.passes === 1 ? "" : "es"}</span>
                        </span>
                        <span className="mt-0.5 block text-xs leading-snug text-secondary">{method.detail}</span>
                      </span>
                    </button>
                  )
                })}
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-2 px-4 py-3">
                <SummaryRow icon={Files} label="Items selected" value={String(paths.length)} />
                <SummaryRow icon={HardDrive} label="Total size" value={paths.length > 0 ? totalSizeDisplay : "Calculated at run start"} />
                <SummaryRow
                  icon={FolderOpen}
                  label="Target mix"
                  value={
                    paths.length === 0
                      ? "No targets"
                      : folderCount > 0 && fileCount > 0
                        ? `${fileCount} files / ${folderCount} folders`
                        : folderCount > 0
                          ? `${folderCount} folder${folderCount === 1 ? "" : "s"}`
                          : `${fileCount} file${fileCount === 1 ? "" : "s"}`
                  }
                />
                <SummaryRow icon={ShieldCheck} label="Method" value={`${selectedMethod.label} (${selectedMethod.passes} pass${selectedMethod.passes === 1 ? "" : "es"})`} />
                <SummaryRow icon={hasActionError ? AlertTriangle : statusGood ? CheckCircle2 : Clock3} label="Status" value={statusText} good={statusGood} />
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-amber-500/35 bg-transparent py-0 shadow-none ring-0">
              <SectionBody className="px-4 py-3">
                <div className="flex items-start gap-2.5">
                  <ShieldAlert className="mt-0.5 size-4 text-amber-400" aria-hidden />
                  <p className="text-sm leading-snug text-secondary">
                    Overwrite methods work best on storage the operating system can rewrite directly. Full-disk encryption gives stronger whole-device protection.
                  </p>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Ready Check</SectionTitle>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="rounded-md border border-border/80 bg-background/35">
                  <Toggle label="I understand these selected items will be removed" checked={confirmed} onChange={setConfirmed} disabled={isRunning} />
                </div>
                <p className="text-sm leading-snug text-secondary">
                  Review the selected paths and confirm before starting. Once the run completes, those targets are intended to be permanently removed.
                </p>
              </SectionBody>
              <SectionFooter className="flex flex-col gap-2 border-t border-border bg-transparent px-4 py-3">
                <AlertDialog>
                  <AlertDialogTrigger asChild>
                    <Button className="w-full" variant="destructive" disabled={!canStart || isRunning}>
                      <AlertTriangle data-icon="inline-start" />
                      {isRunning ? "Deleting" : "Start Secure Delete"}
                    </Button>
                  </AlertDialogTrigger>
                  <AlertDialogContent>
                    <AlertDialogHeader>
                      <AlertDialogTitle>Confirm secure delete</AlertDialogTitle>
                      <AlertDialogDescription>
                        FileLocker will run {selectedMethod.label} with {selectedMethod.passes} overwrite pass{selectedMethod.passes === 1 ? "" : "es"}, then remove the selected targets.
                      </AlertDialogDescription>
                    </AlertDialogHeader>
                    <AlertDialogFooter>
                      <AlertDialogCancel>Cancel</AlertDialogCancel>
                      <AlertDialogAction variant="destructive" onClick={run}>Delete</AlertDialogAction>
                    </AlertDialogFooter>
                  </AlertDialogContent>
                </AlertDialog>
                <Button className="w-full" variant="outline" onClick={clearSelection} disabled={(paths.length === 0 && results.length === 0) || isRunning}>
                  <Trash2 data-icon="inline-start" />
                  Clear Selection
                </Button>
              </SectionFooter>
            </Section>
          </aside>
        </div>
      </div>
    </div>
  )
}

type SummaryRowProps = {
  icon: typeof Files
  label: string
  value: string
  good?: boolean
}

function OperationErrorNotice({ message }: { message: string }) {
  return (
    <div className="rounded-md border border-destructive/35 bg-destructive/10 px-4 py-3 text-sm leading-snug text-red-100" role="status" aria-live="polite">
      <div className="flex items-start gap-2.5">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
        <span>{message}</span>
      </div>
    </div>
  )
}

function areSameLocalPath(left: string, right: string) {
  return normalizeComparablePath(left) === normalizeComparablePath(right)
}

function normalizeComparablePath(path: string) {
  return path
    .trim()
    .replaceAll("/", "\\")
    .replace(/[\\]+$/, "")
    .toLowerCase()
}

function SummaryRow({ icon: Icon, label, value, good = false }: SummaryRowProps) {
  return (
    <div className="flex min-h-9 items-center justify-between gap-2 rounded-md border border-border/80 bg-background/35 px-3 py-2">
      <div className="flex min-w-0 items-center gap-2.5">
        <div className={cn("flex size-7 shrink-0 items-center justify-center rounded-md border", good ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : "border-border/70 bg-background/35 text-secondary")}>
          <Icon className="size-4" aria-hidden />
        </div>
        <span className="text-sm text-secondary">{label}</span>
      </div>
      <span className={cn("text-right font-display text-sm font-semibold tracking-tight", good ? "text-accent-green" : "text-primary")}>{value}</span>
    </div>
  )
}

function getPathDetailsWarning(error: unknown) {
  return error instanceof Error && error.message
    ? `Unable to read selected item details. ${error.message}`
    : "Unable to read selected item details."
}

type PasswordStrengthState = {
  score: number
  label: string
  feedback: string
  tone: string
  barClass: string
}

function calculatePasswordStrength(password: string): PasswordStrengthState {
  if (!password) {
    return {
      score: 0,
      label: "Waiting",
      feedback: "Enter a password to check strength before you start the run.",
      tone: "text-secondary",
      barClass: "bg-border",
    }
  }

  let score = Math.min(password.length * 6, 40)
  if (/[a-z]/.test(password)) score += 10
  if (/[A-Z]/.test(password)) score += 10
  if (/\d/.test(password)) score += 10
  if (/[^A-Za-z0-9]/.test(password)) score += 15
  if (password.length >= 14) score += 10

  const commonFragments = ["password", "qwerty", "letmein", "welcome", "admin"]
  if (commonFragments.some((fragment) => password.toLowerCase().includes(fragment))) {
    score = Math.min(score, 25)
  }

  const finalScore = Math.max(0, Math.min(score, 100))

  if (finalScore < 35) {
    return {
      score: finalScore,
      label: "Weak",
      feedback: "Use more length and a mix of upper, lower, numbers, and symbols.",
      tone: "text-accent-red",
      barClass: "bg-accent-red",
    }
  }

  if (finalScore < 70) {
    return {
      score: finalScore,
      label: "Fair",
      feedback: "Add more length for better protection before encrypting important files.",
      tone: "text-accent-orange",
      barClass: "bg-accent-orange",
    }
  }

  return {
    score: finalScore,
    label: "Strong",
    feedback: "Good strength for a local encryption run.",
    tone: "text-accent-green",
    barClass: "bg-accent-green",
  }
}
