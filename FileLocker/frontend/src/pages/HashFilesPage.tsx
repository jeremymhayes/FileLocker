import { useCallback, useEffect, useMemo, useState, type DragEvent } from "react"
import {
  BookOpen,
  CheckCircle2,
  Clock3,
  Copy,
  FileDigit,
  FileText,
  FolderOpen,
  Hash,
  Info,
  ShieldCheck,
  Trash2,
  TriangleAlertIcon,
} from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Section, SectionBody, SectionFooter, SectionHeader, SectionTitle } from "@/components/layout/Workspace"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Field } from "@/components/common/Field"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { cn } from "@/lib/utils"
import { fileName, formatDate, mergeUniquePaths } from "@/lib/format"
import {
  DEFAULT_HASH_ALGORITHM_ID,
  HASH_ALGORITHMS,
  describeSupportedHashAlgorithms,
  getHashAlgorithm,
  isSupportedHashLength,
} from "@/lib/hashAlgorithms"
import type { DashboardState, HistoryEntry } from "@/types/bridge"

type HashResponse = {
  operationId: string
  path: string
  fileName: string
  algorithm: string
  hash: string
  digestBits: number
  expectedLength: number
  dashboard?: DashboardState
}

type HashManifestResponse = {
  manifestPath: string
  fileName: string
  algorithm: string
  fileCount: number
  dashboard?: DashboardState
}

type DescribePathsResponse = {
  items: Array<{
    fullPath: string
    displayName: string
    itemType: string
    sizeBytes: number
    sizeDisplay: string
    isDirectory: boolean
    details: string
  }>
  totalSizeBytes: number
  totalSizeDisplay: string
  warnings: string[]
}

type HashFilesPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onDashboardUpdate: (dashboard: DashboardState) => void
  dashboard?: DashboardState | null
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

type RunningHashAction = "hash" | "manifest" | null
type ExpectedHashState = {
  isPresent: boolean
  isSupported: boolean
  normalized: string
}

const supportedHashPattern = /^[0-9a-fA-F]+$/
const expectedHashStatusId = "hash-expected-status"
const supportedHashAlgorithmList = describeSupportedHashAlgorithms()
const MAX_EXPECTED_HASH_INPUT_CHARS = 8 * 1024
const MAX_HASH_SUFFIX_CANDIDATE_CHARS = 256

const hashGuidePoints = [
  "SHA-256 is the recommended general-purpose file fingerprint for integrity checks.",
  "Hashes verify whether file contents changed. They do not hide file contents or encrypt anything.",
  "Compare the generated digest against a trusted expected hash when you want to verify authenticity.",
  "Use a manifest when you need repeatable hashes for multiple files or a whole folder.",
]

export function HashFilesPage({ invoke, onDashboardUpdate, dashboard, droppedPaths = [], onDroppedPathsHandled }: HashFilesPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [algorithm, setAlgorithm] = useState(DEFAULT_HASH_ALGORITHM_ID)
  const [expectedHash, setExpectedHash] = useState("")
  const [result, setResult] = useState<HashResponse | null>(null)
  const [hashError, setHashError] = useState("")
  const [manifest, setManifest] = useState<HashManifestResponse | null>(null)
  const [manifestError, setManifestError] = useState("")
  const [verification, setVerification] = useState("Not verified")
  const [verificationError, setVerificationError] = useState("")
  const [runningAction, setRunningAction] = useState<RunningHashAction>(null)
  const [pathDetails, setPathDetails] = useState<DescribePathsResponse["items"]>([])
  const [pathWarnings, setPathWarnings] = useState<string[]>([])
  const [isDescribingPaths, setIsDescribingPaths] = useState(false)
  const isRunning = runningAction !== null

  const resetGeneratedOutputs = useCallback(() => {
    setResult(null)
    setHashError("")
    setManifest(null)
    setManifestError("")
    setVerification("Not verified")
    setVerificationError("")
  }, [])

  const replacePaths = useCallback((nextPaths: string[]) => {
    if (isRunning) {
      toast.error("Wait for the current hash operation to finish before changing the selection.")
      return
    }

    resetGeneratedOutputs()
    setPaths(mergeUniquePaths([], nextPaths))
  }, [isRunning, resetGeneratedOutputs])

  const addPaths = useCallback((nextPaths: string[]) => {
    if (nextPaths.length === 0) {
      return
    }

    if (isRunning) {
      toast.error("Wait for the current hash operation to finish before changing the selection.")
      return
    }

    resetGeneratedOutputs()
    setPaths((current) => mergeUniquePaths(current, nextPaths))
  }, [isRunning, resetGeneratedOutputs])

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    addPaths(droppedPaths)
    onDroppedPathsHandled?.()
  }, [addPaths, droppedPaths, onDroppedPathsHandled])

  useEffect(() => {
    if (paths.length === 0) {
      setPathDetails([])
      setPathWarnings([])
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
      })
      .catch((error) => {
        if (!cancelled) {
          setPathDetails([])
          setPathWarnings([error instanceof Error && error.message
            ? `Unable to read selected file details. ${error.message}`
            : "Unable to read selected file details."])
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

  const selectedItem = pathDetails[0] ?? (paths[0] ? {
    fullPath: paths[0],
    displayName: fileName(paths[0]),
    itemType: "File",
    sizeBytes: 0,
    sizeDisplay: "Checked at run start",
    isDirectory: false,
    details: "Preparing details",
  } : null)
  const recentHashes = useMemo(
    () => (dashboard?.history ?? []).filter((entry) => entry.operation === "Hash" || entry.operation === "Hash Manifest").slice(0, 5),
    [dashboard]
  )
  const selectedHashAlgorithm = getHashAlgorithm(algorithm)
  const selectedFile = pathDetails.find((item) => !item.isDirectory) ?? (pathDetails.length === 0 && selectedItem ? selectedItem : null)
  const isHashing = runningAction === "hash"
  const isSavingManifest = runningAction === "manifest"
  const expectedHashState = useMemo(() => getExpectedHashState(expectedHash), [expectedHash])
  const expectedHashLengthMismatch = Boolean(
    result?.expectedLength &&
      expectedHashState.isSupported &&
      expectedHashState.normalized.length !== result.expectedLength
  )
  const expectedHashInvalid = expectedHashState.isPresent && (!expectedHashState.isSupported || expectedHashLengthMismatch)
  const canHash = Boolean(selectedFile) && Boolean(selectedHashAlgorithm) && !isRunning && !isDescribingPaths
  const canSaveManifest = Boolean(selectedHashAlgorithm) && !isRunning && paths.length > 0
  const canVerify = Boolean(result?.hash) && expectedHashState.isSupported && !expectedHashLengthMismatch && !isRunning
  const verificationGood = verification === "Match"
  const verificationHasIssue = verification === "Mismatch" || expectedHashInvalid || Boolean(verificationError)
  const VerificationStatusIcon = verificationGood ? CheckCircle2 : verificationHasIssue ? TriangleAlertIcon : Info
  const expectedHashLengthMessage = result
    ? `Paste a ${result.algorithm} hash (${result.expectedLength} hex characters).`
    : `Paste a ${supportedHashAlgorithmList} hash before verifying.`
  const verificationReadyTitle = verificationError
    ? "Verification failed"
    : expectedHashLengthMismatch
      ? "Check hash length"
      : expectedHashInvalid
        ? "Check expected hash"
        : "Ready to verify"
  const verificationReadyDescription = expectedHashLengthMismatch
    ? expectedHashLengthMessage
    : expectedHashInvalid
      ? `Paste a ${supportedHashAlgorithmList} hash before verifying.`
      : verificationError || "Paste a known hash to compare against the generated result."
  const currentStatus = isHashing ? "Hashing" : isSavingManifest ? "Saving manifest" : hashError ? "Failed" : result?.hash ? "Ready" : paths.length > 0 ? "Waiting" : "No file selected"
  const hashOutputStatus = isHashing
    ? "Generating file hash"
    : isSavingManifest
      ? "Saving manifest for selected paths"
      : hashError
        ? "Hash generation failed"
      : result?.hash
        ? "Generated successfully"
        : canHash
          ? selectedHashAlgorithm ? "Waiting for file and algorithm selection" : "Select a supported hash algorithm"
          : "Select a file target to generate a single hash"
  const hashOutputTitle = result ? `${result.algorithm} Hash` : `${selectedHashAlgorithm?.label ?? "Unsupported"} Hash`

  async function pickFiles() {
    try {
      const response = await invoke<{ paths: string[] }>("files.pickFiles")
      replacePaths(response.paths)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick files.")
    }
  }

  async function pickFolder() {
    try {
      const response = await invoke<{ path: string }>("files.pickFolder")
      if (response.path) {
        addPaths([response.path])
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick a folder.")
    }
  }

  function handleDropZoneDragOver(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    event.dataTransfer.dropEffect = isRunning ? "none" : "copy"
  }

  function handleAlgorithmChange(nextAlgorithm: string) {
    if (isRunning) {
      return
    }

    if (nextAlgorithm === algorithm) {
      return
    }

    resetGeneratedOutputs()
    setAlgorithm(nextAlgorithm)
  }

  function handleExpectedHashChange(nextExpectedHash: string) {
    setExpectedHash(nextExpectedHash)
    setVerification("Not verified")
    setVerificationError("")
  }

  async function compute() {
    if (isRunning) {
      return
    }

    if (!selectedFile) {
      toast.error("Select one file to hash.")
      return
    }

    if (!selectedHashAlgorithm) {
      toast.error("Select a supported hash algorithm.")
      return
    }

    setRunningAction("hash")
    setResult(null)
    setHashError("")
    setManifestError("")
    setVerification("Not verified")
    setVerificationError("")
    try {
      const response = await invoke<HashResponse>("hash.compute", { path: selectedFile.fullPath, algorithm: selectedHashAlgorithm.id, operationId: crypto.randomUUID() })
      setResult(response)
      setVerification("Not verified")
      if (response.dashboard) {
        onDashboardUpdate(response.dashboard)
      }
      toast.success(`${response.algorithm} hash generated for ${response.fileName}.`)
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Hash generation failed."
      setHashError(message)
      toast.error(message)
    } finally {
      setRunningAction(null)
    }
  }

  async function verify() {
    if (isRunning) {
      return
    }

    if (!result?.hash) {
      toast.error("Generate a hash first.")
      return
    }

    if (!expectedHashState.isSupported) {
      toast.error(expectedHashState.isPresent ? `Paste a ${supportedHashAlgorithmList} hash before verifying.` : "Paste an expected hash before verifying.")
      return
    }

    if (expectedHashLengthMismatch) {
      toast.error(expectedHashLengthMessage)
      return
    }

    try {
      setVerificationError("")
      const response = await invoke<{ match: boolean; status: string }>("hash.verify", { generatedHash: result.hash, expectedHash: expectedHashState.normalized })
      setVerification(response.status)
      toast[response.match ? "success" : "error"](response.match ? "Hash matches." : "Hash mismatch.")
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Hash verification failed."
      setVerification("Failed")
      setVerificationError(message)
      toast.error(message)
    }
  }

  async function copyHash() {
    if (!result?.hash) {
      return
    }

    try {
      await navigator.clipboard.writeText(result.hash)
      toast.success("Hash copied.")
    } catch {
      toast.error("Hash could not be copied.")
    }
  }

  async function generateManifest() {
    if (isRunning) {
      return
    }

    if (paths.length === 0) {
      toast.error("Select files or folders for the manifest.")
      return
    }

    if (!selectedHashAlgorithm) {
      toast.error("Select a supported hash algorithm.")
      return
    }

    setRunningAction("manifest")
    setManifest(null)
    setManifestError("")
    try {
      const response = await invoke<HashManifestResponse>("hash.manifestCreate", { paths, algorithm: selectedHashAlgorithm.id })
      setManifest(response)
      setManifestError("")
      if (response.dashboard) {
        onDashboardUpdate(response.dashboard)
      }
      toast.success(`Generated ${response.algorithm} manifest for ${response.fileCount} file(s).`)
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Manifest generation failed."
      setManifestError(message)
      toast.error(message)
    } finally {
      setRunningAction(null)
    }
  }

  async function revealManifest() {
    if (!manifest?.manifestPath) {
      return
    }

    try {
      await invoke("files.revealPath", { path: manifest.manifestPath })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Could not open the manifest folder.")
    }
  }

  async function copyResults() {
    if (!result?.hash) {
      toast.error("Generate a hash before copying results.")
      return
    }

    const text = `${result.algorithm}: ${result.hash}`
    try {
      await navigator.clipboard.writeText(text)
      toast.success("Hash results copied.")
    } catch {
      toast.error("Hash results could not be copied.")
    }
  }

  function clearSelection() {
    if (isRunning) {
      return
    }

    setPaths([])
    resetGeneratedOutputs()
    setExpectedHash("")
  }

  return (
    <div className="security-page">
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-3">
              <div className="flex size-8 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent ">
                <Hash className="size-4" aria-hidden />
              </div>
              <div>
                <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Hash Files</h2>
                <p className="mt-1 max-w-3xl text-sm leading-snug text-secondary">
                  Generate and verify cryptographic hashes for your files, then compare the output against trusted values.
                </p>
              </div>
            </div>
          </div>

          <div className="flex shrink-0 flex-wrap gap-2">
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline" size="lg">
                  <BookOpen data-icon="inline-start" />
                  Hash Guide
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-2xl">
                <DialogHeader>
                  <DialogTitle>Hash Guide</DialogTitle>
                  <DialogDescription>What to check when you use FileLocker for integrity verification.</DialogDescription>
                </DialogHeader>
                <div className="flex flex-col gap-3">
                  {hashGuidePoints.map((point) => (
                    <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                      {point}
                    </div>
                  ))}
                </div>
              </DialogContent>
            </Dialog>

            <Button variant="outline" size="lg" onClick={() => void copyResults()} disabled={!result?.hash}>
              <Copy data-icon="inline-start" />
              Copy Results
            </Button>
          </div>
        </div>

        <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
          <div className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionBody className="px-4 py-3">
                <div
                  className={cn(
                    "flex min-h-[150px] flex-col items-center justify-center rounded-md border border-dashed border-accent/55 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-sky-400/70",
                    isRunning && "cursor-not-allowed opacity-70"
                  )}
                  role="group"
                  aria-label="Hash file drop zone"
                  aria-describedby="hash-drop-zone-description"
                  aria-disabled={isRunning}
                  onDragOver={handleDropZoneDragOver}
                  onDrop={(event: DragEvent<HTMLDivElement>) => {
                    event.preventDefault()
                    const droppedPaths = Array.from(event.dataTransfer.files)
                      .map((file) => (file as File & { path?: string }).path)
                      .filter((path): path is string => Boolean(path))
                    addPaths(droppedPaths)
                  }}
                >
                  <div className="flex size-8 items-center justify-center rounded-md border border-sky-400/30 bg-sky-400/10 text-sky-300">
                    <FileDigit className="size-5" aria-hidden />
                  </div>
                  <h3 className="mt-3 font-display text-lg font-semibold tracking-tight text-primary">Drop a file here to generate a hash</h3>
                  <p id="hash-drop-zone-description" className="mt-1 max-w-2xl text-sm leading-snug text-secondary">
                    Choose a file for a single hash or add multiple files and folders when you want a manifest for broader integrity tracking.
                  </p>
                  <div className="mt-4 flex flex-wrap justify-center gap-2">
                    <Button variant="default" onClick={() => void pickFiles()} disabled={isRunning}>
                      <FolderOpen data-icon="inline-start" />
                      Browse File
                    </Button>
                    <Button variant="secondary" onClick={() => void pickFolder()} disabled={isRunning}>
                      <FolderOpen data-icon="inline-start" />
                      Browse Folder
                    </Button>
                  </div>
                  <p className="mt-3 text-xs text-secondary">Useful for checking file integrity and verifying downloads.</p>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Selected File</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">
                      {paths.length > 0
                        ? isDescribingPaths
                          ? "Reading selected file details."
                          : selectedItem?.displayName ?? "Selected file ready for hashing."
                        : "Select a file to generate a hash or compare against a known digest."}
                    </p>
                  </div>
                  <Badge variant="outline">{paths.length > 0 ? `${paths.length} selected` : "No file"}</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                {pathWarnings.length > 0 ? (
                  <div className="mb-3 rounded-md border border-amber-400/30 bg-amber-400/8 px-3 py-2 text-sm leading-snug text-secondary">
                    {pathWarnings[0]}
                  </div>
                ) : null}

                {selectedItem ? (
                  <div className="rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-3">
                    <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
                      <div className="flex min-w-0 items-center gap-3">
                        <div className="flex size-9 shrink-0 items-center justify-center rounded-md border border-border/80 bg-background/40">
                          <FileTypeIcon filename={selectedItem.displayName} />
                        </div>
                        <div className="min-w-0">
                          <div className="truncate font-display text-base font-semibold tracking-tight text-primary">{selectedItem.displayName}</div>
                          <div className="mt-0.5 text-xs text-secondary">{selectedItem.itemType}</div>
                        </div>
                      </div>

                      <div className="grid gap-3 sm:grid-cols-4 lg:min-w-[32rem]">
                        <SelectedInfo label="Type" value={selectedItem.itemType} />
                        <SelectedInfo label="Size" value={selectedItem.sizeDisplay} />
                        <SelectedInfo label="Last modified" value={recentHashes[0] ? formatDate(recentHashes[0].timestampUtc) : "Checked at run start"} />
                        <SelectedInfo label="Status" value={paths.length > 0 ? "Ready" : "Waiting"} good={paths.length > 0} />
                      </div>

                      <Button variant="outline" onClick={clearSelection} disabled={isRunning}>
                        <Trash2 data-icon="inline-start" />
                        Remove
                      </Button>
                    </div>
                  </div>
                ) : (
                  <div className="rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-3 text-sm text-secondary">
                    No file selected. Drop a file above or choose Browse File.
                  </div>
                )}
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Generate Hash</SectionTitle>
                  <Badge variant="outline">Step 1</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="text-sm text-secondary">{hashOutputTitle}</div>
                <div className={cn("rounded-md border bg-background/35 px-3 py-3", hashError ? "border-destructive/55" : "border-sky-400/55")}>
                  <div className={cn("break-all font-mono text-sm leading-snug", hashError ? "text-red-100" : result?.hash ? "text-primary" : "text-secondary")}>
                    {hashError || result?.hash || "Generate a hash to populate the digest output."}
                  </div>
                </div>
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className={cn("rounded-md border px-3 py-2 text-sm leading-snug", hashError ? "border-destructive/25 bg-destructive/10 text-red-100" : "border-accent-green/25 bg-accent-green/8 text-accent-green")}>
                    {hashOutputStatus}
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Button onClick={() => void copyHash()} disabled={!result?.hash}>
                      <Copy data-icon="inline-start" />
                      Copy Hash
                    </Button>
                    <Button variant="secondary" onClick={() => void generateManifest()} disabled={!canSaveManifest}>
                      <Hash data-icon="inline-start" />
                      {isSavingManifest ? "Saving" : "Save Manifest"}
                    </Button>
                  </div>
                </div>
                {manifest ? (
                  <div className="rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-3 text-sm text-secondary">
                    <div className="font-display text-sm font-semibold tracking-tight text-primary">{manifest.fileName}</div>
                    <div className="mt-1">{manifest.algorithm} manifest for {manifest.fileCount} file(s).</div>
                    <div className="mt-2">
                      <Button variant="outline" onClick={() => void revealManifest()}>
                        <FolderOpen data-icon="inline-start" />
                        Show Manifest
                      </Button>
                    </div>
                  </div>
                ) : null}
                {manifestError ? (
                  <div className="rounded-md border border-amber-400/30 bg-amber-400/8 px-3 py-2 text-sm leading-snug text-secondary" role="status" aria-live="polite">
                    {manifestError}
                  </div>
                ) : null}
              </SectionBody>
            </Section>
          </div>

          <aside className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-sky-400/30 bg-sky-400/10 text-sky-300">
                    <Hash className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Hash Algorithm</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <Field label="Select algorithm">
                  <Select value={algorithm} onValueChange={handleAlgorithmChange} disabled={isRunning}>
                    <SelectTrigger>
                      <SelectValue placeholder="Algorithm" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        {HASH_ALGORITHMS.map((hashAlgorithm) => (
                          <SelectItem key={hashAlgorithm.id} value={hashAlgorithm.id}>
                            {hashAlgorithm.recommendation ? `${hashAlgorithm.label} ${hashAlgorithm.recommendation}` : hashAlgorithm.label}
                          </SelectItem>
                        ))}
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                </Field>
                <p className="text-sm leading-snug text-secondary">
                  SHA-256 is recommended for general file integrity checks and download verification.
                </p>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div className="flex items-center gap-3">
                    <div className="flex size-8 items-center justify-center rounded-md border border-accent-teal/30 bg-accent-teal/10 text-accent-teal">
                      <ShieldCheck className="size-4" aria-hidden />
                    </div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Compare Trusted Hash</SectionTitle>
                  </div>
                  <Badge variant="outline">Step 2</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="flex gap-3">
                  <Input value={expectedHash} onChange={(event) => handleExpectedHashChange(event.target.value)} placeholder="Paste expected hash to verify" maxLength={MAX_EXPECTED_HASH_INPUT_CHARS} aria-label="Expected hash" aria-invalid={expectedHashInvalid || undefined} aria-describedby={expectedHashStatusId} />
                  <Button onClick={() => void verify()} disabled={!canVerify}>
                    Verify
                  </Button>
                </div>
                <div className={cn("rounded-md border px-3 py-3", verificationGood ? "border-accent-green/35 bg-accent-green/8" : verificationHasIssue ? "border-accent-red/35 bg-accent-red/8" : "border-border/80 bg-background/35")}>
                  <div className="flex items-start gap-2.5">
                    <div className={cn("mt-0.5 flex size-7 items-center justify-center rounded-md border", verificationGood ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : verificationHasIssue ? "border-accent-red/30 bg-accent-red/10 text-accent-red" : "border-border/70 bg-background/40 text-secondary")}>
                      <VerificationStatusIcon className="size-4" aria-hidden />
                    </div>
                    <div>
                      <div className={cn("font-display text-sm font-semibold tracking-tight", verificationGood ? "text-accent-green" : verificationHasIssue ? "text-accent-red" : "text-primary")}>
                        {verificationGood ? "Hash matches" : verification === "Mismatch" ? "Hash mismatch" : verificationReadyTitle}
                      </div>
                      <p id={expectedHashStatusId} className="mt-0.5 text-xs leading-snug text-secondary" aria-live="polite">
                        {verificationGood ? "The file appears authentic." : verification === "Mismatch" ? "The generated hash does not match the value you supplied." : verificationReadyDescription}
                      </p>
                    </div>
                  </div>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-border/70 bg-background/35 text-secondary">
                    <FileText className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-2 px-4 py-3">
                <SummaryRow icon={FileText} label="File selected" value={selectedItem?.displayName ?? "None"} />
                <SummaryRow icon={Hash} label="Algorithm" value={selectedHashAlgorithm?.label ?? algorithm} />
                <SummaryRow icon={Clock3} label="Hash length" value={result ? `${result.expectedLength} characters` : selectedHashAlgorithm ? `${selectedHashAlgorithm.expectedLength} characters` : "Unsupported"} />
                <SummaryRow icon={hashError ? TriangleAlertIcon : result?.hash ? CheckCircle2 : Clock3} label="Status" value={currentStatus} good={Boolean(result?.hash)} />
                <SummaryRow icon={verificationError ? TriangleAlertIcon : verificationGood ? CheckCircle2 : Info} label="Verification" value={verificationGood ? "Match" : verification} good={verificationGood} />
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Recent Hashes</SectionTitle>
                  <span className="text-xs text-accent">{recentHashes.length > 0 ? "Local history" : "No recent hashes"}</span>
                </div>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                {recentHashes.length === 0 ? (
                  <div className="rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-3 text-sm leading-snug text-secondary">
                    Run a hash or manifest operation to populate recent history here.
                  </div>
                ) : (
                  <div className="flex flex-col gap-2">
                    {recentHashes.map((entry: HistoryEntry) => (
                      <div key={entry.id} className="grid min-h-10 grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-2.5 rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2">
                        <div className="flex size-7 items-center justify-center rounded-md border border-accent/20 bg-accent/8 text-accent">
                          <Hash className="size-4" aria-hidden />
                        </div>
                        <div className="min-w-0">
                          <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">
                            {entry.results[0]?.sourcePath ? fileName(entry.results[0].sourcePath) : entry.profileName}
                          </div>
                          <div className="mt-0.5 text-xs text-secondary">{entry.operation === "Hash Manifest" ? "Manifest" : entry.operation}</div>
                        </div>
                        <div className="text-right text-xs text-secondary">{formatDate(entry.timestampUtc)}</div>
                      </div>
                    ))}
                  </div>
                )}
              </SectionBody>
              <SectionFooter className="flex gap-2 border-t border-border bg-transparent px-4 py-3">
                <Button className="flex-1" onClick={() => void compute()} disabled={!canHash}>
                  <Hash data-icon="inline-start" />
                  {isHashing ? "Hashing" : "Generate Hash"}
                </Button>
                <Button className="flex-1" variant="outline" onClick={clearSelection} disabled={paths.length === 0 || isRunning}>
                  <Trash2 data-icon="inline-start" />
                  Clear
                </Button>
              </SectionFooter>
            </Section>
          </aside>
        </div>
      </div>
    </div>
  )
}

type SelectedInfoProps = {
  label: string
  value: string
  good?: boolean
}

function SelectedInfo({ label, value, good = false }: SelectedInfoProps) {
  return (
    <div className="min-w-0">
      <div className="security-label">{label}</div>
      <div className={cn("mt-1 truncate text-sm", good ? "text-accent-green" : "text-primary")}>{value}</div>
    </div>
  )
}

type SummaryRowProps = {
  icon: typeof FileText
  label: string
  value: string
  good?: boolean
}

function SummaryRow({ icon: Icon, label, value, good = false }: SummaryRowProps) {
  return (
    <div className="flex min-h-9 items-center justify-between gap-2 rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2">
      <div className="flex min-w-0 items-center gap-2.5">
        <div className={cn("flex size-7 shrink-0 items-center justify-center rounded-md border", good ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : "border-border/70 bg-background/35 text-secondary")}>
          <Icon className="size-4" aria-hidden />
        </div>
        <span className="text-sm text-secondary">{label}</span>
      </div>
      <span className={cn("max-w-[13rem] truncate text-right font-display text-sm font-semibold tracking-tight", good ? "text-accent-green" : "text-primary")}>{value}</span>
    </div>
  )
}

function getExpectedHashState(input: string): ExpectedHashState {
  const normalized = normalizeSupportedHash(input)
  return {
    isPresent: input.trim().length > 0,
    isSupported: Boolean(normalized),
    normalized,
  }
}

function normalizeSupportedHash(input: string) {
  if (!input.trim()) {
    return ""
  }

  if (input.length > MAX_EXPECTED_HASH_INPUT_CHARS) {
    return ""
  }

  for (const candidate of enumerateHexRuns(input)) {
    if (isSupportedHash(candidate)) {
      return candidate.toLowerCase()
    }
  }

  const compact = removeHashSeparators(input)
  if (isSupportedHash(compact)) {
    return compact.toLowerCase()
  }

  for (const candidate of enumerateSeparatedHexGroups(input)) {
    const hash = getSupportedHashSuffix(candidate)
    if (hash) {
      return hash.toLowerCase()
    }
  }

  return ""
}

function removeHashSeparators(input: string) {
  let compact = ""
  for (const character of input) {
    if (/\s/.test(character) || character === "-" || character === ":") {
      continue
    }

    if (!/[0-9a-fA-F]/.test(character)) {
      return ""
    }

    compact += character
  }

  return compact
}

function enumerateHexRuns(input: string) {
  return input.match(/[0-9a-fA-F]+/g) ?? []
}

function enumerateSeparatedHexGroups(input: string) {
  const groups: string[] = []
  let current = ""
  for (const character of input) {
    if (/[0-9a-fA-F]/.test(character)) {
      current += character
      continue
    }

    if (/\s/.test(character) || character === "-" || character === ":") {
      continue
    }

    if (current.length > 0) {
      groups.push(current)
      current = ""
    }
  }

  if (current.length > 0) {
    groups.push(current)
  }

  return groups
}

function getSupportedHashSuffix(candidate: string) {
  if (isSupportedHash(candidate)) {
    return candidate
  }

  if (candidate.length > MAX_HASH_SUFFIX_CANDIDATE_CHARS) {
    return ""
  }

  if (candidate.length > 128) {
    const sha512 = candidate.slice(-128)
    if (isSupportedHash(sha512)) {
      return sha512
    }
  }

  if (candidate.length > 64) {
    const sha256 = candidate.slice(-64)
    if (isSupportedHash(sha256)) {
      return sha256
    }
  }

  return ""
}

function isSupportedHash(value: string) {
  return isSupportedHashLength(value.length) && supportedHashPattern.test(value)
}
