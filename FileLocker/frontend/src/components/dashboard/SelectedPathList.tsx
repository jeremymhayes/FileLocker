import { FileText, X } from "lucide-react"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { fileName } from "@/lib/format"

type SelectedPathListProps = {
  paths: string[]
  onRemove: (path: string) => void
  emptyMessage: string
}

export function SelectedPathList({ paths, onRemove, emptyMessage }: SelectedPathListProps) {
  if (paths.length === 0) {
    return (
      <div className="mt-5 flex min-h-32 flex-col items-center justify-center rounded-2xl border border-border bg-bg-dropzone p-5 text-center">
        <FileText className="size-7 text-muted" aria-hidden />
        <p className="mt-2 text-sm text-secondary">{emptyMessage}</p>
      </div>
    )
  }

  return (
    <div className="mt-5 max-h-56 overflow-y-auto rounded-2xl border border-border bg-bg-dropzone">
      {paths.map((path) => (
        <div key={path} className="flex items-center gap-3 border-b border-border px-4 py-3 last:border-b-0">
          <FileTypeIcon filename={path} />
          <div className="min-w-0 flex-1">
            <div className="truncate font-mono text-sm text-primary">{fileName(path)}</div>
            <div className="flex gap-2 text-xs text-muted">
              <span className="truncate">{path}</span>
              <span className="shrink-0">Size checked when run starts</span>
            </div>
          </div>
          <button
            className="rounded-lg p-1 text-muted transition-colors hover:bg-bg-surface-hover hover:text-primary focus-visible:ring-2 focus-visible:ring-accent"
            aria-label={`Remove ${path}`}
            onClick={() => onRemove(path)}
          >
            <X className="size-4" aria-hidden />
          </button>
        </div>
      ))}
    </div>
  )
}
