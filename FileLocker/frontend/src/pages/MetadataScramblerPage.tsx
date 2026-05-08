import { useEffect, useState } from "react"
import { Fingerprint } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { FileDropZone } from "@/components/common/FileDropZone"
import { StatusBadge } from "@/components/common/StatusBadge"

type MetadataPreviewItem = {
  label: string
  beforeValue: string
  afterValue: string
}

type MetadataResponse = {
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
  preview: MetadataPreviewItem[]
  report: string
}

type MetadataScramblerPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

export function MetadataScramblerPage({ invoke, droppedPaths = [], onDroppedPathsHandled }: MetadataScramblerPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [mode, setMode] = useState("Remove metadata")
  const [preview, setPreview] = useState<MetadataResponse | null>(null)
  const [isRunning, setIsRunning] = useState(false)

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths(droppedPaths.slice(0, 1))
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

  async function pickFiles() {
    const response = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths(response.paths.slice(0, 1))
  }

  async function inspect() {
    if (paths.length === 0) {
      toast.error("Select one file to inspect.")
      return
    }

    setIsRunning(true)
    try {
      const response = await invoke<MetadataResponse>("metadata.inspect", { path: paths[0], mode })
      setPreview(response)
      toast.success(`Metadata preview ready for ${response.file.displayName}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Metadata preview failed.")
    } finally {
      setIsRunning(false)
    }
  }

  return (
    <div className="security-page">
      <section className="security-section">
        <div className="mb-4 flex items-start gap-3">
          <Fingerprint className="mt-1 size-4 text-accent" aria-hidden />
          <div>
            <div className="security-section-title">Metadata Scrambler</div>
            <p className="security-description">Preview metadata changes before modifying a file.</p>
          </div>
        </div>
        <FileDropZone paths={paths} onPathsChange={(value) => setPaths(value.slice(0, 1))} onPickFiles={pickFiles} onPickFolder={() => undefined} allowFolder={false} title="Inspection Target" description="One file per metadata preview." />
      </section>

      <section className="security-section">
        <label className="flex flex-col gap-2">
          <span className="security-label">Mode</span>
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
        </label>
        <Button className="mt-4 w-full" onClick={inspect} disabled={paths.length === 0 || isRunning}>
          {isRunning ? "Previewing" : "Preview Changes"}
        </Button>
      </section>

      <section className="security-section">
        <div className="mb-4 flex items-end justify-between gap-3">
          <div>
            <div className="security-section-title">Preview</div>
            <p className="security-description">{preview ? `${preview.file.fileType} / ${preview.file.sizeDisplay}` : "No file inspected."}</p>
          </div>
          {preview ? <StatusBadge status={preview.writeSupportEnabled ? "Writable" : "Preview only"} /> : null}
        </div>

        {preview ? (
          <div className="flex flex-col">
            <div className="border-y border-border py-3">
              <div className="truncate font-mono text-xs text-primary">{preview.file.displayName}</div>
              <div className="mt-1 truncate font-mono text-[10px] uppercase tracking-widest text-muted">{preview.file.fullPath}</div>
            </div>
            {preview.preview.map((item) => (
              <div key={item.label} className="grid gap-2 border-b border-border py-3 text-sm md:grid-cols-[0.35fr_0.65fr]">
                <div className="security-label">{item.label}</div>
                <div className="min-w-0 font-mono text-xs text-secondary">
                  <div className="truncate">Before: {item.beforeValue}</div>
                  <div className="truncate text-primary">After: {item.afterValue}</div>
                </div>
              </div>
            ))}
            <Dialog>
              <DialogTrigger asChild>
                <Button className="mt-4" variant="secondary">View Report</Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-2xl">
                <DialogHeader>
                  <DialogTitle>Metadata preview report</DialogTitle>
                  <DialogDescription>No files were changed.</DialogDescription>
                </DialogHeader>
                <pre className="terminal-output max-h-96">{preview.report}</pre>
              </DialogContent>
            </Dialog>
          </div>
        ) : (
          <pre className="terminal-output text-secondary">Preview details will appear here.</pre>
        )}
      </section>
    </div>
  )
}
