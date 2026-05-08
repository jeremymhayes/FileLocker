import { useEffect, useState } from "react"
import { AlertTriangle, FolderOpen, Lock, ShieldCheck, Trash2, Unlock } from "lucide-react"
import { Field } from "@/components/common/Field"
import { OperationToggle as Toggle } from "@/components/file-operations/OperationToggle"
import { PasswordInput } from "@/components/file-operations/PasswordInput"
import { toast } from "sonner"
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle, AlertDialogTrigger } from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { FileDropZone } from "@/components/common/FileDropZone"
import { ProgressBar } from "@/components/common/ProgressBar"
import { ResultList } from "@/components/common/ResultList"
import { mergeUniquePaths } from "@/lib/format"
import type { FileOperationRequest, FileOperationResult, ProgressEvent } from "@/types/bridge"

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

type FileOperationPageProps = {
  kind: "encrypt" | "decrypt" | "verify"
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  progressEvents: ProgressEvent[]
  onDashboardUpdate: (dashboard: unknown) => void
  onReveal: (path: string) => void
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

export function FileOperationPage({ kind, invoke, progressEvents, onDashboardUpdate, onReveal, droppedPaths = [], onDroppedPathsHandled }: FileOperationPageProps) {
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
  const [submitAttempted, setSubmitAttempted] = useState(false)
  const [encryptOutputSuggestion, setEncryptOutputSuggestion] = useState<EncryptOutputSuggestion | null>(null)

  const latestProgress = progressEvents.at(-1)
  const isEncrypt = kind === "encrypt"
  const isDecrypt = kind === "decrypt"
  const title = isEncrypt ? "Encrypt Files" : isDecrypt ? "Decrypt Files" : "Verify Payloads"
  const description = isEncrypt ? "Create encrypted FileLocker files on this device." : isDecrypt ? "Restore files from FileLocker .locked payloads." : "Check encrypted files without writing output."
  const Icon = isEncrypt ? Lock : isDecrypt ? Unlock : ShieldCheck
  const destructiveOptionsEnabled = options.removeOriginalsAfterSuccess || options.secureDeleteOriginals

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

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

  async function pickFiles() {
    const result = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths((current) => mergeUniquePaths(current, result.paths))
  }

  async function pickFolder() {
    const result = await invoke<{ path: string }>("files.pickFolder")
    if (result.path) {
      setPaths((current) => mergeUniquePaths(current, [result.path]))
    }
  }

  async function pickEncryptOutputFolder() {
    const result = await invoke<{ path: string }>("files.pickFolder")
    if (result.path) {
      setEncryptOutputDirectory(result.path)
    }
  }

  async function pickDecryptOutputFolder() {
    const result = await invoke<{ path: string }>("files.pickFolder")
    if (result.path) {
      setDecryptOutputDirectory(result.path)
    }
  }

  async function pickBackupFolder() {
    const result = await invoke<{ path: string }>("files.pickFolder")
    if (result.path) {
      setBackupFolderPath(result.path)
    }
  }

  async function run() {
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
    try {
      const normalizedEncryptOutputDirectory = encryptOutputDirectory.trim()
      const normalizedDecryptOutputDirectory = decryptOutputDirectory.trim()
      const normalizedBackupFolderPath = backupFolderPath.trim()

      const payload: FileOperationRequest = {
        operationId: crypto.randomUUID(),
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
      toast.error(error instanceof Error ? error.message : "Operation failed.")
    } finally {
      setIsRunning(false)
    }
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
            <PasswordInput value={password} onChange={setPassword} placeholder={isEncrypt ? "Password" : "Unlock password"} label={isEncrypt ? "Password" : "Unlock Password"} />
            {submitAttempted && !password ? <p className="mt-2 text-sm text-accent-red">{isEncrypt ? "Password is required." : "Unlock password is required."}</p> : null}
          </Field>
          {isEncrypt ? (
            <Field label="Confirm Password">
              <PasswordInput value={confirmPassword} onChange={setConfirmPassword} placeholder="Confirm password" label="Confirm Password" />
              {password && confirmPassword && password !== confirmPassword ? <p className="mt-2 text-sm text-accent-red">Passwords do not match.</p> : null}
            </Field>
          ) : null}
          <Field label="Keyfile Path">
            <Input value={keyfilePath} onChange={(event) => setKeyfilePath(event.target.value)} placeholder="Optional keyfile path" />
          </Field>
          <Field label="Recovery Key">
            <Input value={recoveryKey} onChange={(event) => setRecoveryKey(event.target.value)} placeholder="Optional recovery key" />
          </Field>
        </div>
      </section>

      <section className="security-section">
        <div className="mb-4">
          <div className="security-section-title">Run Options</div>
          <p className="security-description">FileLocker checks every option before touching your files.</p>
        </div>
        <div className="grid border-t border-border md:grid-cols-2 md:[&>*:nth-child(odd)]:border-r">
          {isEncrypt ? <Toggle label="Compress before encryption" checked={options.compressFiles} onChange={(value) => setOptions({ ...options, compressFiles: value })} /> : null}
          {isEncrypt ? <Toggle label="Scramble output names" checked={options.scrambleNames} onChange={(value) => setOptions({ ...options, scrambleNames: value })} /> : null}
          {isEncrypt ? <Toggle label="PNG carrier output" checked={options.useSteganography} onChange={(value) => setOptions({ ...options, useSteganography: value })} /> : null}
          {isEncrypt ? <Toggle label="Package folders" checked={options.packageFolders} onChange={(value) => setOptions({ ...options, packageFolders: value })} /> : null}
          <Toggle label="Verify after write" checked={options.verifyAfterWrite} onChange={(value) => setOptions({ ...options, verifyAfterWrite: value })} />
          <Toggle label={isEncrypt ? "Delete originals after success" : "Delete encrypted files after success"} checked={options.removeOriginalsAfterSuccess} onChange={(value) => setOptions({ ...options, removeOriginalsAfterSuccess: value })} />
          <Toggle label="Use secure delete for removals" checked={options.secureDeleteOriginals} onChange={(value) => setOptions({ ...options, secureDeleteOriginals: value })} />
          {isDecrypt ? <Toggle label="Restore original filenames" checked={options.restoreOriginalFilenames} onChange={(value) => setOptions({ ...options, restoreOriginalFilenames: value })} /> : null}
          {isDecrypt ? <Toggle label="Preserve folder structure" checked={options.preserveFolderStructure} onChange={(value) => setOptions({ ...options, preserveFolderStructure: value })} /> : null}
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
              <div className="space-y-2">
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input value={encryptOutputDirectory} onChange={(event) => setEncryptOutputDirectory(event.target.value)} placeholder="Leave blank to save beside the source file" />
                  <Button type="button" variant="secondary" onClick={pickEncryptOutputFolder}>
                    <FolderOpen data-icon="inline-start" />
                    Browse...
                  </Button>
                </div>
                {encryptOutputSuggestion?.suggestedPath ? (
                  <div className="flex flex-wrap gap-2">
                    <Button type="button" variant="outline" onClick={() => setEncryptOutputDirectory(encryptOutputSuggestion.suggestedPath ?? "")}>
                      Use Suggested Folder
                    </Button>
                    {encryptOutputDirectory ? (
                      <Button type="button" variant="ghost" onClick={() => setEncryptOutputDirectory("")}>
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
                <Input value={decryptOutputDirectory} onChange={(event) => setDecryptOutputDirectory(event.target.value)} placeholder="Leave blank to save beside the encrypted file" />
                <Button type="button" variant="secondary" onClick={pickDecryptOutputFolder}>
                  <FolderOpen data-icon="inline-start" />
                  Browse...
                </Button>
              </div>
            </Field>
          ) : null}
          <Field label="Backup Folder">
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input value={backupFolderPath} onChange={(event) => setBackupFolderPath(event.target.value)} placeholder="Optional backup folder" />
              <Button type="button" variant="secondary" onClick={pickBackupFolder}>
                <FolderOpen data-icon="inline-start" />
                Browse...
              </Button>
            </div>
          </Field>
          <Field label="Metadata Note">
            <Input value={metadataNotes} onChange={(event) => setMetadataNotes(event.target.value)} placeholder="Optional metadata note" />
          </Field>
          <Field label="Timestamp Policy">
            <Select value={options.outputTimestampPolicy} onValueChange={(value) => setOptions({ ...options, outputTimestampPolicy: value })}>
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
              <Input value={deleteConfirmation} onChange={(event) => setDeleteConfirmation(event.target.value)} placeholder="DELETE" />
              <AlertDialogFooter>
                <AlertDialogCancel>Cancel</AlertDialogCancel>
                <AlertDialogAction disabled={deleteConfirmation !== "DELETE"} onClick={run}>Continue</AlertDialogAction>
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

export function SecureDeletePage({ invoke, onDashboardUpdate, onReveal, droppedPaths = [], onDroppedPathsHandled }: Pick<FileOperationPageProps, "invoke" | "onDashboardUpdate" | "onReveal" | "droppedPaths" | "onDroppedPathsHandled">) {
  const [paths, setPaths] = useState<string[]>([])
  const [confirmed, setConfirmed] = useState(false)
  const [results, setResults] = useState<FileOperationResult[]>([])
  const [isRunning, setIsRunning] = useState(false)

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

  async function pickFiles() {
    const result = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths((current) => mergeUniquePaths(current, result.paths))
  }

  async function pickFolder() {
    const result = await invoke<{ path: string }>("files.pickFolder")
    if (result.path) {
      setPaths((current) => mergeUniquePaths(current, [result.path]))
    }
  }

  async function run() {
    setIsRunning(true)
    try {
      const result = await invoke<{ results: FileOperationResult[]; dashboard?: unknown }>("secureDelete.delete", { paths })
      setResults(result.results)
      if (result.dashboard) {
        onDashboardUpdate(result.dashboard)
      }
      toast.success("Secure delete completed.")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Secure delete failed.")
    } finally {
      setIsRunning(false)
    }
  }

  return (
    <div className="security-page">
      <section className="security-section">
        <div className="mb-4 flex items-start gap-3">
          <Trash2 className="mt-1 size-4 text-destructive" aria-hidden />
          <div>
            <div className="security-section-title">Secure Delete</div>
            <p className="security-description">Overwrite files where possible, then remove the selected items.</p>
          </div>
        </div>
        <FileDropZone paths={paths} onPathsChange={setPaths} onPickFiles={pickFiles} onPickFolder={pickFolder} title="Delete Targets" description="Pick the files or folders you want to remove." />
      </section>

      <section className="security-section">
        <Toggle label="I understand this removes selected targets" checked={confirmed} onChange={setConfirmed} />
        <AlertDialog>
          <AlertDialogTrigger asChild>
            <Button className="mt-4 w-full" variant="destructive" disabled={paths.length === 0 || !confirmed || isRunning}>
              <AlertTriangle data-icon="inline-start" />
              {isRunning ? "Deleting" : "Secure Delete"}
            </Button>
          </AlertDialogTrigger>
          <AlertDialogContent>
            <AlertDialogHeader>
              <AlertDialogTitle>Confirm secure delete</AlertDialogTitle>
              <AlertDialogDescription>FileLocker will overwrite files where possible, then remove the selected targets.</AlertDialogDescription>
            </AlertDialogHeader>
            <AlertDialogFooter>
              <AlertDialogCancel>Cancel</AlertDialogCancel>
              <AlertDialogAction variant="destructive" onClick={run}>Delete</AlertDialogAction>
            </AlertDialogFooter>
          </AlertDialogContent>
        </AlertDialog>
      </section>

      <ResultList results={results} onReveal={onReveal} />
    </div>
  )
}
