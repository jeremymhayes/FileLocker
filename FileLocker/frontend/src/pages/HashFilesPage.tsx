import { useEffect, useState } from "react"
import { Copy, FolderOpen, Hash } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { FileDropZone } from "@/components/common/FileDropZone"
import { StatusBadge } from "@/components/common/StatusBadge"
import type { DashboardState } from "@/types/bridge"

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

type HashFilesPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onDashboardUpdate: (dashboard: DashboardState) => void
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

export function HashFilesPage({ invoke, onDashboardUpdate, droppedPaths = [], onDroppedPathsHandled }: HashFilesPageProps) {
  const [paths, setPaths] = useState<string[]>([])
  const [algorithm, setAlgorithm] = useState("SHA-256")
  const [expectedHash, setExpectedHash] = useState("")
  const [result, setResult] = useState<HashResponse | null>(null)
  const [manifest, setManifest] = useState<HashManifestResponse | null>(null)
  const [verification, setVerification] = useState("Not verified")
  const [isRunning, setIsRunning] = useState(false)

  useEffect(() => {
    if (droppedPaths.length === 0) {
      return
    }

    setPaths(droppedPaths)
    onDroppedPathsHandled?.()
  }, [droppedPaths, onDroppedPathsHandled])

  async function pickFiles() {
    const response = await invoke<{ paths: string[] }>("files.pickFiles")
    setPaths(response.paths)
  }

  async function pickFolder() {
    const response = await invoke<{ path: string }>("files.pickFolder")
    if (response.path) {
      setPaths((current) => [...new Set([...current, response.path])])
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

  return (
    <div className="security-page">
      <section className="security-section">
        <div className="mb-4 flex items-start gap-3">
          <Hash className="mt-1 size-4 text-accent" aria-hidden />
          <div>
            <div className="security-section-title">Hash Files</div>
            <p className="security-description">Generate a SHA fingerprint or manifest for selected files.</p>
          </div>
        </div>
        <FileDropZone paths={paths} onPathsChange={setPaths} onPickFiles={pickFiles} onPickFolder={pickFolder} title="Selected Files" description="Use one file for a single digest, or multiple files and folders for a manifest." />
      </section>

      <section className="security-section">
        <div className="grid gap-4 md:grid-cols-[1fr_auto]">
          <label className="flex min-w-0 flex-col gap-2">
            <span className="security-label">Algorithm</span>
            <Select value={algorithm} onValueChange={setAlgorithm}>
              <SelectTrigger>
                <SelectValue placeholder="Algorithm" />
              </SelectTrigger>
              <SelectContent>
                <SelectGroup>
                  <SelectItem value="SHA-256">SHA-256</SelectItem>
                  <SelectItem value="SHA-512">SHA-512</SelectItem>
                </SelectGroup>
              </SelectContent>
            </Select>
          </label>
          <Button className="self-end" onClick={compute} disabled={isRunning || paths.length === 0}>
            <Hash data-icon="inline-start" />
            {isRunning ? "Hashing" : "Generate Hash"}
          </Button>
        </div>
        <div className="mt-4 flex flex-wrap gap-3">
          <Button variant="secondary" onClick={generateManifest} disabled={isRunning || paths.length === 0}>
            <Hash data-icon="inline-start" />
            Generate Manifest
          </Button>
          {manifest ? (
            <Button variant="outline" onClick={revealManifest}>
              <FolderOpen data-icon="inline-start" />
              Show Manifest
            </Button>
          ) : null}
        </div>
      </section>

      {manifest ? (
        <section className="security-section">
          <div className="security-section-title">Manifest</div>
          <p className="security-description">{manifest.algorithm} manifest for {manifest.fileCount} file(s).</p>
          <pre className="terminal-output mt-4">{manifest.fileName}</pre>
        </section>
      ) : null}

      <section className="security-section">
        <div className="mb-4 flex items-end justify-between gap-3">
          <div>
            <div className="security-section-title">Digest</div>
            <p className="security-description">{result ? `${result.digestBits}-bit digest for ${result.fileName}` : "Generate a hash to populate output."}</p>
          </div>
          <StatusBadge status={verification} />
        </div>
        <pre className={`terminal-output min-h-28 ${result?.hash ? "" : "text-secondary"}`}>{result?.hash ?? "No digest generated."}</pre>
        <div className="mt-4 flex gap-2">
          <Button variant="secondary" onClick={copyHash} disabled={!result?.hash}>
            <Copy data-icon="inline-start" />
            Copy
          </Button>
        </div>
      </section>

      <section className="security-section">
        <label className="flex flex-col gap-2">
          <span className="security-label">Expected Hash</span>
          <Input value={expectedHash} onChange={(event) => setExpectedHash(event.target.value)} placeholder="Expected hash" />
        </label>
        <Button className="mt-4" variant="outline" onClick={verify} disabled={!result?.hash || !expectedHash}>
          Verify
        </Button>
      </section>
    </div>
  )
}
