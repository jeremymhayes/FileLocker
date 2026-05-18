import { useEffect, useMemo, useState, type DragEvent } from "react"
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
import { fileName, mergeUniquePaths } from "@/lib/format"
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

const hashGuidePoints = [
  "SHA-256 is the recommended general-purpose file fingerprint for integrity checks.",
  "Hashes verify whether file contents changed. They do not hide file contents or encrypt anything.",
  "Compare the generated digest against a trusted expected hash when you want to verify authenticity.",
  "Use a manifest when you need repeatable hashes for multiple files or a whole folder.",
]

export function HashFilesPage({ invoke, onDashboardUpdate, dashboard, droppedPaths = [], onDroppedPathsHandled }: HashFilesPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [algorithm, setAlgorithm] = useState("SHA-256")
  const [expectedHash, setExpectedHash] = useState("")
  const [result, setResult] = useState<HashResponse | null>(null)
  const [manifest, setManifest] = useState<HashManifestResponse | null>(null)
  const [verification, setVerification] = useState("Not verified")
  const [isRunning, setIsRunning] = useState(false)
  const [pathDetails, setPathDetails] = useState<DescribePathsResponse["items"]>([])
  const [pathWarnings, setPathWarnings] = useState<string[]>([])
  const [isDescribingPaths, setIsDescribingPaths] = useState(false)

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

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
      .catch(() => {
        if (!cancelled) {
          setPathDetails([])
          setPathWarnings([])
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
  const canHash = paths.length > 0 && !isRunning
  const canVerify = Boolean(result?.hash) && expectedHash.trim().length > 0
  const verificationGood = verification === "Match"
  const currentStatus = result?.hash ? "Ready" : paths.length > 0 ? "Waiting" : "No file selected"
  const hashOutputTitle = result ? `${result.algorithm} Hash` : `${algorithm} Hash`

  async function pickFiles() {
    const response = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths(response.paths)
  }

  async function pickFolder() {
    const response = await invoke<{ path: string }>("files.pickFolder")
    if (response.path) {
      setPaths((current) => mergeUniquePaths(current, [response.path]))
    }
  }

  async function compute() {
    if (paths.length === 0) {
      toast.error("Select one file to hash.")
      return
    }

    setIsRunning(true)
    try {
      const response = await invoke<HashResponse>("hash.compute", { path: paths[0], algorithm, operationId: crypto.randomUUID() })
      setResult(response)
      setVerification("Not verified")
      if (response.dashboard) {
        onDashboardUpdate(response.dashboard)
      }
      toast.success(`${response.algorithm} hash generated for ${response.fileName}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Hash generation failed.")
    } finally {
      setIsRunning(false)
    }
  }

  async function verify() {
    if (!result?.hash) {
      toast.error("Generate a hash first.")
      return
    }

    const response = await invoke<{ match: boolean; status: string }>("hash.verify", { generatedHash: result.hash, expectedHash })
    setVerification(response.status)
    toast[response.match ? "success" : "error"](response.match ? "Hash matches." : "Hash mismatch.")
  }

  async function copyHash() {
    if (!result?.hash) {
      return
    }
    await navigator.clipboard.writeText(result.hash)
    toast.success("Hash copied.")
  }

  async function generateManifest() {
    if (paths.length === 0) {
      toast.error("Select files or folders for the manifest.")
      return
    }

    setIsRunning(true)
    try {
      const response = await invoke<HashManifestResponse>("hash.manifestCreate", { paths, algorithm })
      setManifest(response)
      if (response.dashboard) {
        onDashboardUpdate(response.dashboard)
      }
      toast.success(`Generated ${response.algorithm} manifest for ${response.fileCount} file(s).`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Manifest generation failed.")
    } finally {
      setIsRunning(false)
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
    await navigator.clipboard.writeText(text)
    toast.success("Hash results copied.")
  }

  function clearSelection() {
    setPaths([])
    setResult(null)
    setManifest(null)
    setExpectedHash("")
    setVerification("Not verified")
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
                  className="flex min-h-[150px] flex-col items-center justify-center rounded-md border border-dashed border-accent/55 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-sky-400/70"
                  onDragOver={(event: DragEvent<HTMLDivElement>) => event.preventDefault()}
                  onDrop={(event: DragEvent<HTMLDivElement>) => {
                    event.preventDefault()
                    const droppedPaths = Array.from(event.dataTransfer.files)
                      .map((file) => (file as File & { path?: string }).path)
                      .filter((path): path is string => Boolean(path))
                    if (droppedPaths.length > 0) {
                      setPaths((current) => mergeUniquePaths(current, droppedPaths))
                    }
                  }}
                >
                  <div className="flex size-8 items-center justify-center rounded-md border border-sky-400/30 bg-sky-400/10 text-sky-300">
                    <FileDigit className="size-5" aria-hidden />
                  </div>
                  <h3 className="mt-3 font-display text-lg font-semibold tracking-tight text-primary">Drop a file here to generate a hash</h3>
                  <p className="mt-1 max-w-2xl text-sm leading-snug text-secondary">
                    Choose a file for a single hash or add multiple files and folders when you want a manifest for broader integrity tracking.
                  </p>
                  <div className="mt-4 flex flex-wrap justify-center gap-2">
                    <Button variant="default" onClick={() => void pickFiles()}>
                      <FolderOpen data-icon="inline-start" />
                      Browse File
                    </Button>
                    <Button variant="secondary" onClick={() => void pickFolder()}>
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
                  <div className="mb-3 rounded-md border border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
                    {pathWarnings[0]}
                  </div>
                ) : null}

                {selectedItem ? (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3">
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
                        <SelectedInfo label="Last modified" value={recentHashes[0] ? new Date(recentHashes[0].timestampUtc).toLocaleDateString() : "Checked at run start"} />
                        <SelectedInfo label="Status" value={paths.length > 0 ? "Ready" : "Waiting"} good={paths.length > 0} />
                      </div>

                      <Button variant="outline" onClick={clearSelection}>
                        <Trash2 data-icon="inline-start" />
                        Remove
                      </Button>
                    </div>
                  </div>
                ) : (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                    No file selected. Drop a file above or choose Browse File.
                  </div>
                )}
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Hash Output</SectionTitle>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="text-sm text-secondary">{hashOutputTitle}</div>
                <div className="rounded-md border border-sky-400/55 bg-background/35 px-3 py-3">
                  <div className={cn("break-all font-mono text-sm leading-snug", result?.hash ? "text-primary" : "text-secondary")}>
                    {result?.hash ?? "Generate a hash to populate the digest output."}
                  </div>
                </div>
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className="rounded-md border border-accent-green/25 bg-accent-green/8 px-3 py-2 text-sm leading-snug text-accent-green">
                    {result?.hash ? "Generated successfully" : "Waiting for file and algorithm selection"}
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Button onClick={() => void copyHash()} disabled={!result?.hash}>
                      <Copy data-icon="inline-start" />
                      Copy Hash
                    </Button>
                    <Button variant="secondary" onClick={() => void generateManifest()} disabled={isRunning || paths.length === 0}>
                      <Hash data-icon="inline-start" />
                      Save Result
                    </Button>
                  </div>
                </div>
                {manifest ? (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
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
                  <Select value={algorithm} onValueChange={setAlgorithm}>
                    <SelectTrigger>
                      <SelectValue placeholder="Algorithm" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        <SelectItem value="SHA-256">SHA-256 Recommended</SelectItem>
                        <SelectItem value="SHA-512">SHA-512</SelectItem>
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
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-accent-teal/30 bg-accent-teal/10 text-accent-teal">
                    <ShieldCheck className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Verify Hash</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="flex gap-3">
                  <Input value={expectedHash} onChange={(event) => setExpectedHash(event.target.value)} placeholder="Paste expected hash to verify" />
                  <Button onClick={() => void verify()} disabled={!canVerify}>
                    Verify
                  </Button>
                </div>
                <div className={cn("rounded-md border px-3 py-3", verificationGood ? "border-accent-green/35 bg-accent-green/8" : verification === "Mismatch" ? "border-accent-red/35 bg-accent-red/8" : "border-border/80 bg-background/35")}>
                  <div className="flex items-start gap-2.5">
                    <div className={cn("mt-0.5 flex size-7 items-center justify-center rounded-md border", verificationGood ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : verification === "Mismatch" ? "border-accent-red/30 bg-accent-red/10 text-accent-red" : "border-border/70 bg-background/40 text-secondary")}>
                      <CheckCircle2 className="size-4" aria-hidden />
                    </div>
                    <div>
                      <div className={cn("font-display text-sm font-semibold tracking-tight", verificationGood ? "text-accent-green" : verification === "Mismatch" ? "text-accent-red" : "text-primary")}>
                        {verificationGood ? "Hash matches" : verification === "Mismatch" ? "Hash mismatch" : "Ready to verify"}
                      </div>
                      <p className="mt-0.5 text-xs leading-snug text-secondary">
                        {verificationGood ? "The file appears authentic." : verification === "Mismatch" ? "The generated hash does not match the value you supplied." : "Paste a known hash to compare against the generated result."}
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
                <SummaryRow icon={Hash} label="Algorithm" value={algorithm} />
                <SummaryRow icon={Clock3} label="Hash length" value={result ? `${result.expectedLength} characters` : algorithm === "SHA-512" ? "128 characters" : "64 characters"} />
                <SummaryRow icon={result?.hash ? CheckCircle2 : Clock3} label="Status" value={currentStatus} good={Boolean(result?.hash)} />
                <SummaryRow icon={verificationGood ? CheckCircle2 : Info} label="Verification" value={verificationGood ? "Match" : verification} good={verificationGood} />
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
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm leading-snug text-secondary">
                    Run a hash or manifest operation to populate recent history here.
                  </div>
                ) : (
                  <div className="flex flex-col gap-2">
                    {recentHashes.map((entry: HistoryEntry) => (
                      <div key={entry.id} className="grid min-h-10 grid-cols-[auto_minmax(0,1fr)_auto] items-center gap-2.5 rounded-md border border-border/80 bg-background/35 px-3 py-2">
                        <div className="flex size-7 items-center justify-center rounded-md border border-accent/20 bg-accent/8 text-accent">
                          <Hash className="size-4" aria-hidden />
                        </div>
                        <div className="min-w-0">
                          <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">
                            {entry.results[0]?.sourcePath ? fileName(entry.results[0].sourcePath) : entry.profileName}
                          </div>
                          <div className="mt-0.5 text-xs text-secondary">{entry.operation === "Hash Manifest" ? "Manifest" : entry.operation}</div>
                        </div>
                        <div className="text-right text-xs text-secondary">{new Date(entry.timestampUtc).toLocaleDateString()}</div>
                      </div>
                    ))}
                  </div>
                )}
              </SectionBody>
              <SectionFooter className="flex gap-2 border-t border-border bg-transparent px-4 py-3">
                <Button className="flex-1" onClick={() => void compute()} disabled={!canHash}>
                  <Hash data-icon="inline-start" />
                  {isRunning ? "Hashing" : "Generate Hash"}
                </Button>
                <Button className="flex-1" variant="outline" onClick={clearSelection} disabled={paths.length === 0}>
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
    <div className="flex min-h-9 items-center justify-between gap-2 rounded-md border border-border/80 bg-background/35 px-3 py-2">
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
