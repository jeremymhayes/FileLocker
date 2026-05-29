import { MoreHorizontal } from "lucide-react"
import { toast } from "sonner"
import { FileTypeIcon } from "@/components/common/FileTypeIcon"
import { Button } from "@/components/ui/button"
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu"
import { StatusBadge } from "@/components/common/StatusBadge"
import { isSafeLocalPathForReveal } from "@/lib/format"
import { cn } from "@/lib/utils"
import type { RecentFile } from "@/types/bridge"

type RecentFileTableRowProps = {
  item: RecentFile
  active: boolean
  onReveal: (path: string) => void
}

export function RecentFileTableRow({ item, active, onReveal }: RecentFileTableRowProps) {
  const canReveal = isSafeLocalPathForReveal(item.name)

  async function copyName() {
    await navigator.clipboard.writeText(item.name)
    toast.success("File name copied.")
  }

  return (
    <tr className={cn("border-t border-border bg-surface text-sm transition-colors hover:bg-bg-surface-hover", active && "border-l-2 border-l-accent-blue")}>
      <td className="min-w-0 px-3 py-2">
        <div className="flex items-center gap-2.5">
          <FileTypeIcon filename={item.name} />
          <span className="truncate font-medium text-primary">{item.name}</span>
        </div>
      </td>
      <td className="truncate px-3 py-2 text-secondary">{item.type}</td>
      <td className="px-3 py-2">
        <StatusBadge status={item.status} />
      </td>
      <td className="truncate px-3 py-2 font-mono text-xs text-muted">{item.lastModified}</td>
      <td className="px-2 py-2 text-right">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Open actions for ${item.name}`}>
              <MoreHorizontal className="size-4" aria-hidden />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={copyName}>Copy file name</DropdownMenuItem>
            <DropdownMenuItem disabled={!canReveal} onSelect={() => onReveal(item.name)}>
              Reveal in Explorer
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </td>
    </tr>
  )
}
