import { useEffect, useMemo, useState, type DragEvent } from "react"
import {
  BookOpen,
  CalendarDays,
  Camera,
  CheckCircle2,
  Eye,
  FilePenLine,
  Fingerprint,
  FolderOpen,
  Globe,
  Info,
  MapPin,
  Monitor,
  ShieldAlert,
  Sparkles,
  Trash2,
  UserRound,
  X,
} from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Section, SectionBody, SectionHeader, SectionTitle } from "@/components/layout/Workspace"
import { Checkbox } from "@/components/ui/checkbox"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { cn } from "@/lib/utils"
import { mergeUniquePaths } from "@/lib/format"

type MetadataPreviewItem = {
  label: string
  beforeValue: string
  afterValue: string
}

type MetadataCategory = {
  name: string
  description: string
  isSelected: boolean
  isSupported: boolean
  detectedCount: number
}

type MetadataFile = {
  displayName: string
  fullPath: string
  fileType: string
  sizeDisplay: string
  metadataTagCount: number
  metadataCountDisplay: string
  statusDisplay: string
  isSupported: boolean
}

type MetadataResponse = {
  files: MetadataFile[]
  activeFilePath: string
  file: {
    displayName: string
    fullPath: string
    fileType: string
    sizeDisplay: string
    metadataTagCount: number
    statusDisplay: string
    isSupported: boolean
  }
  mode: string
  writeSupportEnabled: boolean
  categories: MetadataCategory[]
  preview: MetadataPreviewItem[]
  summary: {
    filesSelected: number
    tagsFound: number
    categoriesSelected: number
    mode: string
    output: string
    status: string
  }
  warnings: string[]
  report: string
}

type MetadataScramblerPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

const metadataGuidePoints = [
  "FileLocker keeps metadata work local and previews changes before writing anything.",
  "Support varies by file type. Images, documents, and supported media can expose different fields.",
  "Preview mode does not change files. Use it to inspect what a cleaned copy might affect later.",
  "Metadata can include timestamps, author names, camera or device details, GPS data, and application information.",
]

export function MetadataScramblerPage({ invoke, droppedPaths = [], onDroppedPathsHandled }: MetadataScramblerPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [mode, setMode] = useState("Remove metadata")
  const [preview, setPreview] = useState<MetadataResponse | null>(null)
  const [categories, setCategories] = useState<MetadataCategory[]>([])
  const [activeFilePath, setActiveFilePath] = useState("")
  const [isRunning, setIsRunning] = useState(false)

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths((current) => mergeUniquePaths(current, droppedPaths))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

  useEffect(() => {
    if (paths.length === 0) {
      setPreview(null)
      setCategories([])
      setActiveFilePath("")
      return
    }

    void inspect(activeFilePath, true)
    // Intentionally only keyed to selection changes. Category changes are applied on explicit preview.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paths])

  const selectedCategoryNames = useMemo(
    () => categories.filter((category) => category.isSelected).map((category) => category.name),
    [categories]
  )

  const selectedFileCount = preview?.summary.filesSelected ?? paths.length
  const selectedTagCount = preview?.summary.tagsFound ?? 0
  const selectedCategoryCount = preview?.summary.categoriesSelected ?? selectedCategoryNames.length
  const compatibilityMessage = preview?.warnings[0]
    ?? "Metadata support varies by file type. FileLocker previews detected fields before any future cleanup work."

  async function pickFiles() {
    const response = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths((current) => mergeUniquePaths(current, response.paths))
  }

  async function pickFolder() {
    const response = await invoke<{ path: string }>("files.pickFolder")
    if (response.path) {
      setPaths((current) => mergeUniquePaths(current, [response.path]))
    }
  }

  function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()

    const droppedPaths = Array.from(event.dataTransfer.files)
      .map((file) => (file as File & { path?: string }).path)
      .filter((path): path is string => Boolean(path))

    if (droppedPaths.length > 0) {
      setPaths((current) => mergeUniquePaths(current, droppedPaths))
    }
  }

  async function inspect(focusPath?: string, silent = false) {
    if (paths.length === 0) {
      if (!silent) {
        toast.error("Select one or more files or folders to inspect.")
      }
      return
    }

    setIsRunning(true)
    try {
      const response = await invoke<MetadataResponse>("metadata.inspect", {
        path: focusPath || activeFilePath || paths[0],
        paths,
        mode,
        selectedCategories: selectedCategoryNames,
      })

      setPreview(response)
      setCategories(response.categories)
      setActiveFilePath(response.activeFilePath)

      if (!silent) {
        toast.success(`Metadata preview ready for ${response.file.displayName}.`)
      }
    } catch (error) {
      if (!silent) {
        toast.error(error instanceof Error ? error.message : "Metadata preview failed.")
      }
    } finally {
      setIsRunning(false)
    }
  }

  function toggleCategory(name: string, checked: boolean) {
    setCategories((current) =>
      current.map((category) =>
        category.name === name
          ? { ...category, isSelected: checked }
          : category
      )
    )
  }

  function selectAllCategories() {
    setCategories((current) => current.map((category) => ({ ...category, isSelected: category.isSupported })))
  }

  function clearCategorySelection() {
    setCategories((current) => current.map((category) => ({ ...category, isSelected: false })))
  }

  function clearSelection() {
    setPaths([])
    setPreview(null)
    setCategories([])
    setActiveFilePath("")
  }

  const activeFile = preview?.files.find((file) => file.fullPath === activeFilePath) ?? preview?.files[0] ?? null

  return (
    <div className="security-page">
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-3">
              <div className="flex size-8 items-center justify-center rounded-md border border-accent-purple/30 bg-accent-purple/10 text-accent-purple ">
                <Fingerprint className="size-4" aria-hidden />
              </div>
              <div>
                <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Metadata Scrambler</h2>
                <p className="mt-1 max-w-3xl text-sm leading-snug text-secondary">
                  Remove or randomize hidden file metadata before sharing, while previewing the exact fields FileLocker can see.
                </p>
              </div>
            </div>
          </div>

          <div className="flex shrink-0 flex-wrap gap-2">
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline" size="lg">
                  <BookOpen data-icon="inline-start" />
                  Metadata Guide
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-2xl">
                <DialogHeader>
                  <DialogTitle>Metadata Guide</DialogTitle>
                  <DialogDescription>What FileLocker previews before any metadata cleanup work.</DialogDescription>
                </DialogHeader>
                <div className="flex flex-col gap-3">
                  {metadataGuidePoints.map((point) => (
                    <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                      {point}
                    </div>
                  ))}
                </div>
              </DialogContent>
            </Dialog>

            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline" size="lg" disabled={!preview}>
                  <FilePenLine data-icon="inline-start" />
                  View Report
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-3xl">
                <DialogHeader>
                  <DialogTitle>Metadata Preview Report</DialogTitle>
                  <DialogDescription>No files have been changed. This is a local preview only.</DialogDescription>
                </DialogHeader>
                <pre className="terminal-output max-h-[32rem] overflow-auto">{preview?.report ?? "Preview a file to generate a report."}</pre>
              </DialogContent>
            </Dialog>
          </div>
        </div>

        <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
          <div className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionBody className="px-4 py-3">
                <div
                  className="flex min-h-[150px] flex-col items-center justify-center rounded-md border border-dashed border-accent-purple/55 bg-bg-dropzone px-4 py-4 text-center transition-colors hover:border-accent-purple/70"
                  onDragOver={(event) => event.preventDefault()}
                  onDrop={handleDrop}
                >
                  <div className="flex size-8 items-center justify-center rounded-md border border-accent-purple/35 bg-accent-purple/12 text-accent-purple">
                    <Fingerprint className="size-5" aria-hidden />
                  </div>
                  <h3 className="mt-3 font-display text-lg font-semibold tracking-tight text-primary">Drop files here to inspect metadata</h3>
                  <p className="mt-1 max-w-2xl text-sm leading-snug text-secondary">
                    Browse files or folders from your device, then review detected metadata before you decide how it should be handled.
                  </p>
                  <div className="mt-4 flex flex-wrap justify-center gap-2">
                    <Button variant="violet" onClick={() => void pickFiles()}>
                      <FolderOpen data-icon="inline-start" />
                      Browse Files
                    </Button>
                    <Button variant="secondary" onClick={() => void pickFolder()}>
                      <FolderOpen data-icon="inline-start" />
                      Browse Folder
                    </Button>
                  </div>
                  <p className="mt-3 text-xs text-secondary">Works best with images, PDFs, documents, and supported media files.</p>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Selected Files</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">
                      {selectedFileCount > 0 ? `${selectedFileCount} file${selectedFileCount === 1 ? "" : "s"} ready for metadata preview.` : "Add files or folders to build a metadata preview."}
                    </p>
                  </div>
                  <Badge
                    variant="outline"
                    className="border-accent-purple/30 bg-accent-purple/10 text-accent-purple"
                  >
                    {selectedFileCount} selected
                  </Badge>
                </div>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                {preview?.files.length ? (
                  <div className="overflow-hidden rounded-md border border-border/80 bg-background/35">
                    <div className="grid grid-cols-[minmax(0,1.6fr)_minmax(120px,0.8fr)_100px_130px_110px_56px] gap-3 border-b border-border/80 px-3 py-2.5 font-mono text-[10px] uppercase tracking-[0.16em] text-muted">
                      <span>Name</span>
                      <span>Type</span>
                      <span>Size</span>
                      <span>Metadata</span>
                      <span>Status</span>
                      <span>Remove</span>
                    </div>
                    <div className="divide-y divide-border/80">
                      {preview.files.map((file) => (
                        <button
                          key={file.fullPath}
                          type="button"
                          className={cn(
                            "grid min-h-10 w-full grid-cols-[minmax(0,1.6fr)_minmax(120px,0.8fr)_100px_130px_110px_56px] gap-3 px-3 py-2 text-left transition-colors hover:bg-accent-purple/6",
                            file.fullPath === activeFilePath && "bg-accent-purple/8"
                          )}
                          onClick={() => {
                            setActiveFilePath(file.fullPath)
                            void inspect(file.fullPath, true)
                          }}
                        >
                          <div className="min-w-0">
                            <div className="flex items-center gap-3">
                              <FileTypeIcon filename={file.displayName} />
                              <div className="min-w-0">
                                <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{file.displayName}</div>
                                <div className="truncate font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{file.fullPath}</div>
                              </div>
                            </div>
                          </div>
                          <div className="truncate text-sm text-secondary">{file.fileType}</div>
                          <div className="text-sm text-secondary">{file.sizeDisplay}</div>
                          <div className="text-sm text-accent-purple">{file.metadataTagCount} tags</div>
                          <div className="flex items-center gap-2">
                            <span className={cn("size-2.5 rounded-md", file.isSupported ? "bg-accent-green" : "bg-accent-orange")} />
                            <span className={cn("text-sm", file.isSupported ? "text-accent-green" : "text-accent-orange")}>{file.statusDisplay}</span>
                          </div>
                          <div className="flex items-center">
                            <span
                              className="rounded-md p-1.5 text-muted transition-colors hover:bg-background/40 hover:text-primary"
                              onClick={(event) => {
                                event.stopPropagation()
                                setPaths((current) => current.filter((item) => item !== file.fullPath))
                              }}
                            >
                              <Trash2 className="size-4" aria-hidden />
                            </span>
                          </div>
                        </button>
                      ))}
                    </div>
                  </div>
                ) : (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                    No files selected. Drop files above or choose files to inspect metadata.
                  </div>
                )}
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Metadata Preview</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">
                      {activeFile ? `${activeFile.fileType} · ${activeFile.sizeDisplay}` : "Choose a file to inspect before changing anything."}
                    </p>
                  </div>
                  <Badge
                    variant="outline"
                    className="border-accent-purple/30 bg-accent-purple/10 text-accent-purple"
                  >
                    {preview?.writeSupportEnabled ? "Writable" : "Preview only"}
                  </Badge>
                </div>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                {activeFile && preview ? (
                  <div className="rounded-md border border-border/80 bg-background/35 p-3">
                    <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_48px_minmax(0,1fr)]">
                      <div className="rounded-md border border-border/70 bg-bg-dropzone/80 p-3">
                        <div className="mb-2 font-display text-sm font-semibold tracking-tight text-primary">Before</div>
                        <div className="flex flex-col gap-2">
                          {preview.preview.map((item) => (
                            <MetadataPreviewRow key={`before-${item.label}`} label={item.label} value={item.beforeValue} />
                          ))}
                        </div>
                      </div>

                      <div className="flex items-center justify-center">
                        <div className="flex size-8 items-center justify-center rounded-md border border-accent-purple/30 bg-accent-purple/10 text-accent-purple">
                          <Sparkles className="size-4" aria-hidden />
                        </div>
                      </div>

                      <div className="rounded-md border border-border/70 bg-bg-dropzone/80 p-3">
                        <div className="mb-2 font-display text-sm font-semibold tracking-tight text-primary">After</div>
                        <div className="flex flex-col gap-2">
                          {preview.preview.map((item) => (
                            <MetadataPreviewRow key={`after-${item.label}`} label={item.label} value={item.afterValue} highlight />
                          ))}
                        </div>
                      </div>
                    </div>
                  </div>
                ) : (
                  <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm text-secondary">
                    No preview selected. Inspect a file to view metadata changes.
                  </div>
                )}
              </SectionBody>
            </Section>
          </div>

          <aside className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Metadata Categories</SectionTitle>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                <div className="flex flex-col gap-2">
                  {categories.length ? (
                    categories.map((category) => (
                      <label key={category.name} className="flex items-start gap-2.5 rounded-md border border-border/80 bg-background/35 px-3 py-2 transition-colors hover:border-accent-purple/30 hover:bg-accent-purple/6">
                        <Checkbox
                          checked={category.isSelected}
                          disabled={!category.isSupported}
                          onCheckedChange={(checked) => toggleCategory(category.name, checked === true)}
                          className="mt-1 border-accent-purple/35 data-checked:border-accent-purple data-checked:bg-accent-purple focus-visible:border-accent-purple focus-visible:ring-accent-purple/30"
                        />
                        <span className="min-w-0 flex-1">
                          <span className="block font-display text-sm font-semibold tracking-tight text-primary">{category.name}</span>
                          <span className="mt-0.5 block text-xs leading-snug text-secondary">{category.description}</span>
                        </span>
                        <Badge
                          variant="outline"
                          className={cn(
                            "shrink-0 border-border/80 bg-transparent text-secondary",
                            category.detectedCount > 0 && "border-accent-purple/30 bg-accent-purple/10 text-accent-purple"
                          )}
                        >
                          {category.detectedCount}
                        </Badge>
                      </label>
                    ))
                  ) : (
                    <div className="rounded-md border border-border/80 bg-background/35 px-3 py-3 text-sm leading-snug text-secondary">
                      No categories yet. Inspect selected files to load metadata groups.
                    </div>
                  )}
                </div>

                <div className="mt-3 grid gap-2 sm:grid-cols-2">
                  <Button variant="outline" onClick={selectAllCategories} disabled={categories.length === 0}>
                    Select All
                  </Button>
                  <Button variant="outline" onClick={clearCategorySelection} disabled={categories.length === 0}>
                    Clear Selection
                  </Button>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Scramble Mode</SectionTitle>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                <Select value={mode} onValueChange={setMode}>
                  <SelectTrigger>
                    <SelectValue placeholder="Mode" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectGroup>
                      <SelectItem value="Remove metadata">Remove metadata</SelectItem>
                      <SelectItem value="Randomize metadata">Randomize metadata</SelectItem>
                      <SelectItem value="Preview only">Preview only</SelectItem>
                    </SelectGroup>
                  </SelectContent>
                </Select>
                <p className="mt-2 text-sm leading-snug text-secondary">
                  Some metadata depends on file type and may not be available for every file. FileLocker previews what it can detect first.
                </p>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
              </SectionHeader>
              <SectionBody className="px-4 py-3">
                <div className="grid gap-2 text-sm">
                  <SummaryRow label="Files selected" value={String(selectedFileCount)} />
                  <SummaryRow label="Tags found" value={String(selectedTagCount)} />
                  <SummaryRow label="Categories selected" value={String(selectedCategoryCount)} />
                  <SummaryRow label="Mode" value={mode} />
                  <SummaryRow label="Output" value={preview?.summary.output ?? "Preview only"} />
                  <SummaryRow
                    label="Status"
                    value={preview?.summary.status ?? "Waiting for files"}
                    tone={preview ? "good" : "neutral"}
                  />
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-amber-500/40 bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-amber-500/30 px-4 py-3">
                <div className="flex items-start gap-2.5">
                  <div className="flex size-8 items-center justify-center rounded-md border border-amber-500/30 bg-amber-500/10 text-amber-400">
                    <ShieldAlert className="size-4" aria-hidden />
                  </div>
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Compatibility Notice</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">{compatibilityMessage}</p>
                  </div>
                </div>
              </SectionHeader>
            </Section>

            <div className="grid gap-2">
              <Button variant="violet" onClick={() => void inspect()} disabled={paths.length === 0 || isRunning}>
                <Eye data-icon="inline-start" />
                {isRunning ? "Previewing" : "Preview Changes"}
              </Button>
              <Button variant="secondary" disabled>
                <Sparkles data-icon="inline-start" />
                Scramble Metadata
              </Button>
              <Button variant="outline" onClick={clearSelection} disabled={paths.length === 0}>
                <X data-icon="inline-start" />
                Clear Selection
              </Button>
            </div>
          </aside>
        </div>
      </div>
    </div>
  )
}

type MetadataPreviewRowProps = {
  label: string
  value: string
  highlight?: boolean
}

function MetadataPreviewRow({ label, value, highlight = false }: MetadataPreviewRowProps) {
  const Icon = getMetadataIcon(label)

  return (
    <div className="flex items-start gap-2.5 rounded-md border border-border/70 bg-background/30 px-3 py-2">
      <div className={cn("mt-0.5 flex size-7 shrink-0 items-center justify-center rounded-md border text-secondary", highlight ? "border-accent-purple/25 bg-accent-purple/10 text-accent-purple" : "border-border/70 bg-background/35")}>
        <Icon className="size-4" aria-hidden />
      </div>
      <div className="min-w-0 flex-1">
        <div className="security-label">{label}</div>
        <div className={cn("mt-0.5 text-sm leading-snug", highlight ? "text-accent-purple" : "text-primary")}>{value}</div>
      </div>
    </div>
  )
}

type SummaryRowProps = {
  label: string
  value: string
  tone?: "neutral" | "good"
}

function SummaryRow({ label, value, tone = "neutral" }: SummaryRowProps) {
  return (
    <div className="flex min-h-9 items-center justify-between gap-2 rounded-md border border-border/80 bg-background/35 px-3 py-2">
      <span className="text-sm text-secondary">{label}</span>
      <span className={cn("font-display text-sm font-semibold tracking-tight", tone === "good" ? "text-accent-green" : "text-primary")}>{value}</span>
    </div>
  )
}

function getMetadataIcon(label: string) {
  switch (label) {
    case "Author information":
    case "File name":
      return UserRound
    case "GPS/location data":
    case "Last accessed":
      return MapPin
    case "Camera/device data":
      return Camera
    case "Created":
    case "Modified":
      return CalendarDays
    case "Application metadata":
      return Monitor
    case "Document properties":
    case "File type":
      return FilePenLine
    case "Custom metadata":
    case "Attributes":
      return CheckCircle2
    case "Size":
      return Globe
    default:
      return Info
  }
}
