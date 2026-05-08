import { Button } from "@/components/ui/button"
import { StatusBadge } from "@/components/common/StatusBadge"
import { fileName, formatBytes } from "@/lib/format"
import type { FileOperationResult } from "@/types/bridge"

type ResultListProps = {
  results: FileOperationResult[]
  onReveal?: (path: string) => void
}

export function ResultList({ results, onReveal }: ResultListProps) {
  return (
    <section className="security-section">
      <div className="mb-4 flex items-end justify-between gap-3">
        <div>
          <div className="security-section-title">Results</div>
          <p className="security-description">{results.length === 0 ? "No run results yet." : `${results.length} item${results.length === 1 ? "" : "s"} finished`}</p>
        </div>
      </div>
      <div className="max-h-80 overflow-y-auto border-t border-border">
        {results.length === 0 ? (
          <pre className="terminal-output border-t-0 text-secondary">Run output will appear here.</pre>
        ) : (
          results.map((result) => (
            <div key={`${result.sourcePath}-${result.outputPath ?? result.status}`} className="border-b border-border py-3">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate font-mono text-xs text-primary">{fileName(result.outputPath ?? result.sourcePath)}</div>
                  <div className="mt-1 truncate font-mono text-[10px] uppercase tracking-widest text-muted">
                    {formatBytes(result.originalSizeBytes)} / {formatBytes(result.outputSizeBytes)}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-3">
                  <StatusBadge status={result.status} />
                  {result.outputPath && onReveal ? (
                    <Button variant="ghost" size="sm" onClick={() => onReveal(result.outputPath!)}>
                      Open
                    </Button>
                  ) : null}
                </div>
              </div>
              <pre className="mt-3 border border-border bg-background p-3 font-mono text-xs leading-relaxed text-secondary whitespace-pre-wrap break-all">{result.message ?? result.outputPath ?? result.sourcePath}</pre>
            </div>
          ))
        )}
      </div>
    </section>
  )
}
