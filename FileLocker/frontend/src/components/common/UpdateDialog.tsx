import { Download } from "lucide-react"
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogMedia,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Button } from "@/components/ui/button"
import type { UpdateCheckResult } from "@/types/bridge"

type UpdateDialogProps = {
  update: UpdateCheckResult | null
  isInstalling: boolean
  isSkipping: boolean
  onInstall: () => void
  onSkip: () => void
  onLater: () => void
  onOpenChange: (open: boolean) => void
}

/**
 * FileLocker's update prompt. This replaces the former WinUI ContentDialog so
 * the entire updater surface is rendered in the WebView and shares the app's
 * design language. It is driven either by the React startup check or by an
 * `updateAvailable` bridge event pushed from the host.
 */
export function UpdateDialog({ update, isInstalling, isSkipping, onInstall, onSkip, onLater, onOpenChange }: UpdateDialogProps) {
  const release = update?.release
  const open = Boolean(update?.isUpdateAvailable && release)
  const busy = isInstalling || isSkipping
  const notes = release?.notes?.trim() || update?.statusMessage || "No release notes were provided for this release."

  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogMedia>
            <Download className="size-4" />
          </AlertDialogMedia>
          <AlertDialogTitle>FileLocker {release?.displayVersion} is available</AlertDialogTitle>
          <AlertDialogDescription>
            You are running {update?.currentVersion}. Download and install the update now? Everything happens on this
            device.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <div className="max-h-56 overflow-y-auto whitespace-pre-wrap break-words rounded-md border border-border/60 bg-bg-subtle/45 p-3 text-sm leading-[1.55] text-secondary">
          {notes}
        </div>
        <AlertDialogFooter>
          <Button variant="ghost" onClick={onSkip} disabled={busy}>
            {isSkipping ? "Skipping" : "Skip Version"}
          </Button>
          <AlertDialogCancel disabled={busy} onClick={onLater}>
            Later
          </AlertDialogCancel>
          <Button onClick={onInstall} disabled={busy}>
            <Download data-icon="inline-start" />
            {isInstalling ? "Launching" : "Download and Install"}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
