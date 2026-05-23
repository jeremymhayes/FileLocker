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
      <div className="mt-3 flex min-h-[4rem] items-center gap-2.5 border-y border-border px-3 py-3 text-left">
        <FileText className="size-4 text-muted" aria-hidden />
        <p className="text-sm leading-snug text-secondary">{emptyMessage}</p>
      </div>
    )
  }

  return (
    <div className="mt-3 max-h-56 overflow-y-auto border-y border-border bg-transparent">
      {paths.map((path) => (
        <div key={path} className="flex min-h-10 items-center gap-2.5 border-b border-border px-3 py-2 last:border-b-0">
          <FileTypeIcon filename={path} />
          <div className="min-w-0 flex-1">
            <div className="truncate font-mono text-sm text-primary">{fileName(path)}</div>
            <div className="flex gap-2 text-xs text-muted">
              <span className="truncate">{path}</span>
              <span className="shrink-0">Size checked when run starts</span>
            </div>
          </div>
          <button
            type="button"
            className="rounded-md p-1 text-muted transition-colors hover:bg-bg-surface-hover hover:text-primary focus-visible:ring-2 focus-visible:ring-accent"
            aria-label={`Remove ${fileName(path)}`}
            onClick={() => onRemove(path)}
          >
            <X className="size-4" aria-hidden />
          </button>
        </div>
      ))}
    </div>
  )
}
