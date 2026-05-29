import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"

type StatusBadgeProps = {
  status: string
}

// Status pills reuse the shared Badge geometry (height, radius, focus ring) so
// result rows match badges elsewhere in the app, and only layer on a tone +
// the monospace/uppercase ledger treatment that suits a run status.
export function StatusBadge({ status }: StatusBadgeProps) {
  const normalized = status.toLowerCase()
  const tone =
    normalized.includes("fail") || normalized.includes("mismatch") || normalized.includes("attention")
      ? "border-accent-red/30 bg-accent-red/10 text-accent-red"
      : normalized.includes("hash")
        ? "border-accent-teal/30 bg-accent-teal/10 text-accent-teal"
        : normalized.includes("metadata")
          ? "border-accent-purple/30 bg-accent-purple/10 text-accent-purple"
          : normalized.includes("encoded")
            ? "border-accent-orange/30 bg-accent-orange/10 text-accent-orange"
            : normalized.includes("complete") || normalized.includes("match") || normalized.includes("secure") || normalized.includes("encrypted") || normalized.includes("decrypted")
              ? "border-accent-green/30 bg-accent-green/10 text-accent-green"
              : normalized.includes("cancel") || normalized.includes("not") || normalized.includes("warning")
                ? "border-accent-orange/30 bg-accent-orange/10 text-accent-orange"
                : "border-border bg-bg-surface-hover text-secondary"

  return (
    <Badge variant="outline" className={cn("font-mono font-semibold uppercase tracking-wider", tone)}>
      {status}
    </Badge>
  )
}
