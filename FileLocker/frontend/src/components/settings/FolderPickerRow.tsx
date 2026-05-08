import { FolderOpen } from "lucide-react"
import { Button } from "@/components/ui/button"

type FolderPickerRowProps = {
  label: string
  folderPath: string
  onBrowse: () => void
}

export function FolderPickerRow({ label, folderPath, onBrowse }: FolderPickerRowProps) {
  return (
    <div>
      <div className="security-label mb-2">{label}</div>
      <div className="folder-picker-row flex min-h-12 items-center gap-3 rounded-2xl border border-border bg-bg-dropzone px-4 py-2">
        <span className="folder-path-display min-w-0 flex-1 truncate font-mono text-sm text-primary [direction:rtl] [text-align:left]" title={folderPath || "No folder selected"}>
          {folderPath || "No folder selected"}
        </span>
        <Button className="browse-btn" variant="secondary" onClick={onBrowse}>
          <FolderOpen data-icon="inline-start" />
          Browse...
        </Button>
      </div>
    </div>
  )
}
