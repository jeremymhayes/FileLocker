import { useEffect, useId, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from "react"
import { FileText, FolderOpen, MousePointer2, Plus, UploadCloud, X } from "lucide-react"
import { Button } from "@/components/ui/button"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { fileName, formatBytes, getComparableLocalPath, mergeUniquePaths } from "@/lib/format"
import { cn } from "@/lib/utils"

type FileDropZoneProps = {
  paths: string[]
  onPathsChange: (paths: string[]) => void
  onPickFiles: () => void
  onPickFolder: () => void
  title: string
  description: string
  allowFolder?: boolean
  onDropPaths?: (paths: string[]) => void
  disabled?: boolean
}

export function FileDropZone({
  paths,
  onPathsChange,
  onPickFiles,
  onPickFolder,
  title,
  description,
  allowFolder = true,
  onDropPaths,
  disabled = false,
}: FileDropZoneProps) {
  const [dragging, setDragging] = useState(false)
  const [sizeByPath, setSizeByPath] = useState<Record<string, number>>({})
  const descriptionId = useId()
  const dragDepthRef = useRef(0)

  useEffect(() => {
    if (disabled) {
      dragDepthRef.current = 0
      setDragging(false)
    }
  }, [disabled])

  function handleDragEnter(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()

    if (disabled) {
      return
    }

    dragDepthRef.current += 1
    setDragging(true)
  }

  function handleDragOver(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    event.dataTransfer.dropEffect = disabled ? "none" : "copy"

    if (disabled) {
      return
    }

    if (!dragging) {
      setDragging(true)
    }
  }

  function handleDragLeave() {
    if (disabled) {
      dragDepthRef.current = 0
      setDragging(false)
      return
    }

    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1)
    if (dragDepthRef.current === 0) {
      setDragging(false)
    }
  }

  function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    dragDepthRef.current = 0
    setDragging(false)

    if (disabled) {
      return
    }

    const droppedFiles = Array.from(event.dataTransfer.files)
    const droppedPaths = droppedFiles
      .map((file) => (file as File & { path?: string }).path)
      .filter((path): path is string => Boolean(path))

    if (droppedPaths.length > 0) {
      const nextSizes = { ...sizeByPath }
      for (const file of droppedFiles) {
        const path = (file as File & { path?: string }).path
        if (path) {
          nextSizes[path] = file.size
        }
      }
      setSizeByPath(nextSizes)
      onPathsChange(mergeUniquePaths(paths, droppedPaths))
      onDropPaths?.(droppedPaths)
    }
  }

  function handleZoneClick() {
    if (disabled) {
      return
    }

    onPickFiles()
  }

  function handleZoneKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.target !== event.currentTarget) {
      return
    }

    if (disabled) {
      return
    }

    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault()
      onPickFiles()
    }
  }

  function stop(event: MouseEvent) {
    event.stopPropagation()
  }

  return (
    <div className="flex flex-col gap-3">
      <div
        role="button"
        tabIndex={disabled ? -1 : 0}
        aria-disabled={disabled}
        aria-label={title}
        aria-describedby={descriptionId}
        className={cn(
          "flex min-h-[7.5rem] cursor-pointer flex-col items-center justify-center rounded-md border border-dashed border-border-accent bg-bg-dropzone/80 p-4 text-center transition-[background,border-color,transform] duration-150 outline-none focus-visible:ring-2 focus-visible:ring-accent",
          dragging && "scale-[1.005] border-solid border-accent bg-bg-surface-hover",
          disabled && "cursor-not-allowed opacity-60"
        )}
        onClick={handleZoneClick}
        onKeyDown={handleZoneKeyDown}
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <UploadCloud className="size-6 text-accent" aria-hidden />
        <div className="mt-3 security-section-title">{title}</div>
        <div id={descriptionId} className="security-description max-w-xl">{description}</div>
        <div className="mt-4 flex flex-wrap justify-center gap-2" onClick={stop}>
          <Button variant="secondary" onClick={onPickFiles} disabled={disabled}>
            <Plus data-icon="inline-start" />
            Browse Files
          </Button>
          {allowFolder ? (
            <Button variant="outline" onClick={onPickFolder} disabled={disabled}>
              <FolderOpen data-icon="inline-start" />
              Browse Folder
            </Button>
          ) : null}
        </div>
      </div>

      <div className="border-y border-border bg-transparent">
        {paths.length === 0 ? (
          <div className="flex min-h-[4rem] items-center gap-2.5 px-3 py-3 text-sm text-secondary">
            <FileText className="size-4 text-muted" aria-hidden />
            <div className="min-w-0">
              <p className="leading-tight">No files selected.</p>
              <p className="text-xs leading-snug text-muted">Drop files above or choose files to continue.</p>
            </div>
          </div>
        ) : (
          <div className="max-h-56 overflow-y-auto">
            {paths.map((path) => (
              <div key={path} className="flex min-h-10 items-center gap-2.5 border-b border-border px-3 py-2 last:border-b-0">
                <FileTypeIcon filename={path} />
                <div className="min-w-0 flex-1 text-left">
                  <div className="truncate font-mono text-sm text-primary">{fileName(path)}</div>
                  <div className="flex min-w-0 gap-2 text-xs text-muted">
                    <span className="truncate">{path}</span>
                    <span className="shrink-0">{formatSize(sizeByPath[path])}</span>
                  </div>
                </div>
                <button
                  type="button"
                  className="rounded-md p-1 text-muted transition-colors hover:bg-bg-surface-hover hover:text-primary focus-visible:ring-2 focus-visible:ring-accent disabled:cursor-not-allowed disabled:opacity-50 disabled:hover:bg-transparent disabled:hover:text-muted"
                  aria-label={`Remove ${fileName(path)}`}
                  disabled={disabled}
                  onClick={() => onPathsChange(paths.filter((item) => getComparableLocalPath(item.trim()) !== getComparableLocalPath(path.trim())))}
                >
                  <X className="size-4" aria-hidden />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="flex items-center gap-2 font-mono text-xs uppercase tracking-wider text-muted">
        <MousePointer2 className="size-3.5" aria-hidden />
        <span>{paths.length} selected</span>
      </div>
    </div>
  )
}

function formatSize(size?: number) {
  if (typeof size !== "number" || !Number.isFinite(size)) {
    return "Size checked when run starts"
  }

  return formatBytes(size)
}
