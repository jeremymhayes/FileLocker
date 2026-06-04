import { useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent as ReactKeyboardEvent } from "react"
import {
  AlertTriangle,
  ArrowUpDown,
  Binary,
  Calendar,
  Check,
  ChevronDown,
  FileText,
  Fingerprint,
  FolderOpen,
  HardDrive,
  Hash,
  LockKeyhole,
  Plus,
  RefreshCw,
  Shield,
  Sparkles,
  TrendingUp,
  Trash2,
  Unlock,
  X,
} from "lucide-react"
import { toast } from "sonner"
import { PasswordField } from "@/components/dashboard/PasswordField"
import { Button } from "@/components/ui/button"
import { ProgressBar } from "@/components/common/ProgressBar"
import { fileName, formatDate, getComparableLocalPath, mergeUniquePaths } from "@/lib/format"
import { getLatestProgressForOperation } from "@/lib/progress"
import { cn } from "@/lib/utils"
import { DEFAULT_ENCRYPTION_ALGORITHM_ID, getDefaultEncryptionAlgorithm, getEncryptionAlgorithmOptions } from "@/lib/encryptionAlgorithms"
import { formatAlgorithmLabel } from "@/lib/algorithmLabels"
import { DEFAULT_OUTPUT_TIMESTAMP_POLICY } from "@/lib/outputTimestampPolicies"
import type {
  DashboardState,
  EncryptionAlgorithmOption,
  FileOperationRequest,
  FileOperationResult,
  HistoryEntry,
  PageKey,
  ProgressEvent,
} from "@/types/bridge"

// ─── Types ─────────────────────────────────────────────────────────────────

type DashboardPageProps = {
  dashboard: DashboardState
  onNavigate: (page: PageKey) => void
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  progressEvents: ProgressEvent[]
  onDashboardUpdate: (dashboard: DashboardState) => void
  onReveal: (path: string) => void
  privacyModeEnabled?: boolean
  encryptionAlgorithms?: EncryptionAlgorithmOption[]
  droppedPaths?: string[]
  onDroppedPathsHandled?: () => void
}

type OperationResultPayload = {
  completed: number
  failed: number
  results: FileOperationResult[]
  dashboard?: DashboardState
}

type EncryptOutputSuggestion = {
  suggestedPath?: string
  hasFolderSelection: boolean
  folderCount: number
}

type SortKey = "date" | "fileType" | "size" | "action"

type ActivityItem = {
  key: string
  name: string
  op: string
  action: "encrypt" | "decrypt" | "hash" | "metadata" | "encode" | "delete"
  tone: "blue" | "teal" | "purple" | "orange" | "red"
  sizeBytes: number
  fileType: string
  algorithmLabel?: string
  ts: number
  timeLabel: string
  failed: boolean
  path?: string
}

type CleanupCategory = {
  id: string
  group: string
  label: string
  sizeBytes: number
  sizeDisplay: string
  fileCount: number
  defaultSelected: boolean
  isEnabled: boolean
  requiresAdministrator: boolean
}

type CleanupScanResult = {
  categories: CleanupCategory[]
  totalBytes: number
  totalDisplay: string
  totalFiles: number
  skippedItems: number
}

// ─── Constants ─────────────────────────────────────────────────────────────

const SORT_OPTIONS: Array<{ value: SortKey; label: string }> = [
  { value: "date",     label: "Date"            },
  { value: "fileType", label: "File type"       },
  { value: "size",     label: "File size"       },
  { value: "action",   label: "Security action" },
]

const ACTION_LABELS: Record<string, string> = {
  encrypt: "Encrypt",
  decrypt: "Decrypt",
  hash: "Hash",
  metadata: "Metadata",
  encode: "Encode",
  delete: "Secure Delete",
}

const TONE_COLORS: Record<string, string> = {
  blue:   "#5d8dff",
  teal:   "#33c0c7",
  purple: "#a974ff",
  orange: "#e6a14a",
  red:    "#ef617d",
  green:  "#5fd19a",
}

const ACTIVITY_ICONS = {
  encrypt:  LockKeyhole,
  decrypt:  Unlock,
  hash:     Hash,
  metadata: Fingerprint,
  encode:   Binary,
  delete:   Trash2,
} as const

const QUICK_ACTIONS: Array<{ page: PageKey; label: string; detail: string; icon: typeof LockKeyhole; tone: "blue" | "teal" | "purple" | "orange" | "red" }> = [
  { page: "encrypt", label: "Encrypt", detail: "Protect files", icon: LockKeyhole, tone: "blue" },
  { page: "hash", label: "Hash", detail: "Verify integrity", icon: Hash, tone: "teal" },
  { page: "encode", label: "Encode", detail: "Convert text", icon: Binary, tone: "orange" },
  { page: "metadata", label: "Metadata", detail: "Inspect privacy", icon: Fingerprint, tone: "purple" },
  { page: "secure-delete", label: "Delete", detail: "Remove safely", icon: Trash2, tone: "red" },
]

const dashboardEncryptDefaults = {
  algorithm: DEFAULT_ENCRYPTION_ALGORITHM_ID,
  compressFiles: true,
  scrambleNames: false,
  useSteganography: false,
  packageFolders: false,
  removeOriginalsAfterSuccess: false,
  secureDeleteOriginals: false,
  verifyAfterWrite: true,
  saveNextToSource: true,
  saveNextToEncrypted: true,
  restoreOriginalFilenames: true,
  preserveFolderStructure: true,
  outputTimestampPolicy: DEFAULT_OUTPUT_TIMESTAMP_POLICY,
  profileName: "Dashboard Quick Encrypt",
  randomizeMetadata: false,
}

// ─── Helpers ───────────────────────────────────────────────────────────────

function formatBytes(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return "0 B"
  if (n >= 1024 * 1024 * 1024) return (n / 1024 / 1024 / 1024).toFixed(1) + " GB"
  if (n >= 1024 * 1024)        return (n / 1024 / 1024).toFixed(1) + " MB"
  if (n >= 1024)               return (n / 1024).toFixed(0) + " KB"
  return n + " B"
}

function getNonNegativeFiniteNumber(value: number): number {
  return Number.isFinite(value) && value > 0 ? value : 0
}

function getStorageBreakdownFlex(percent: number): number {
  return Math.max(getNonNegativeFiniteNumber(percent), 1)
}

function getFileType(path: string): string {
  const ext = path.split(".").pop()?.toLowerCase() ?? ""
  if (["pdf", "doc", "docx", "txt", "md", "key", "ppt", "pptx", "xls", "xlsx", "csv"].includes(ext)) return "Document"
  if (["jpg", "jpeg", "png", "gif", "webp", "svg", "bmp", "heic"].includes(ext)) return "Image"
  if (["zip", "tar", "gz", "rar", "7z", "bz2"].includes(ext)) return "Archive"
  if (!ext || path.endsWith("/") || path.endsWith("\\")) return "Folder"
  return "File"
}

function getTimeLabel(ts: number): string {
  if (!Number.isFinite(ts)) return "Unknown"

  const diff = Date.now() - ts
  const mins = Math.floor(diff / 60_000)
  if (mins < 1)  return "Just now"
  if (mins < 60) return `${mins}m ago`
  const hours = Math.floor(diff / 3_600_000)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(diff / 86_400_000)
  if (days === 1) return "Yesterday"
  if (days < 30)  return `${days} days ago`
  const date = new Date(ts)
  return Number.isFinite(date.getTime()) ? formatDate(date.toISOString()) : "Unknown"
}

function getOpInfo(operation: string): Pick<ActivityItem, "action" | "tone"> & { label: string } {
  const op = operation.toLowerCase()
  if (op.includes("encrypt"))                            return { action: "encrypt",  tone: "blue",   label: "Encrypted" }
  if (op.includes("decrypt"))                            return { action: "decrypt",  tone: "blue",   label: "Decrypted" }
  if (op.includes("hash"))                               return { action: "hash",     tone: "teal",   label: "Hashed" }
  if (op.includes("metadata") || op.includes("scramble")) return { action: "metadata", tone: "purple", label: "Metadata scrubbed" }
  if (op.includes("encode"))                             return { action: "encode",   tone: "orange", label: "Encoded" }
  if (op.includes("delete"))                             return { action: "delete",   tone: "red",    label: "Secure deleted" }
  return { action: "encrypt", tone: "blue", label: operation }
}

function isSuccessfulActivityStatus(status: string): boolean {
  const normalized = status.trim().toLowerCase()
  return normalized === "success" || normalized === "completed" || normalized === "verified" || normalized === "cleaned"
}

function isFailedHistoryEntry(entry: HistoryEntry): boolean {
  return entry.cancelled || entry.failureCount > 0
}

function deriveActivityItems(history: HistoryEntry[]): ActivityItem[] {
  const items: ActivityItem[] = []
  for (const entry of history) {
    const parsedTimestamp = new Date(entry.timestampUtc).getTime()
    const hasValidTimestamp = Number.isFinite(parsedTimestamp)
    const ts = hasValidTimestamp ? parsedTimestamp : 0
    const timeLabel = hasValidTimestamp ? getTimeLabel(parsedTimestamp) : "Unknown"
    const opInfo = getOpInfo(entry.operation)
    const results = entry.results ?? []

    if (results.length === 0) {
      const failed = isFailedHistoryEntry(entry)
      items.push({
        key:      entry.id,
        name:     entry.operation,
        op:       failed ? (entry.cancelled ? "Cancelled" : "Failed") : opInfo.label,
        action:   opInfo.action,
        tone:     failed ? "red" : opInfo.tone,
        sizeBytes: 0,
        fileType: "—",
        algorithmLabel: formatAlgorithmLabel(entry.algorithm, entry.keySizeBits),
        ts,
        timeLabel,
        failed,
      })
    } else {
      results.forEach((result, i) => {
        const failed = !isSuccessfulActivityStatus(result.status)
        const displayPath = result.outputPath || result.sourcePath
        const name = displayPath.split(/[/\\]/).pop() ?? displayPath
        items.push({
          key:       `${entry.id}-${i}`,
          name,
          op:        failed ? result.status : opInfo.label,
          action:    opInfo.action,
          tone:      failed ? "red" : opInfo.tone,
          sizeBytes: getNonNegativeFiniteNumber(result.originalSizeBytes ?? 0),
          fileType:  getFileType(result.sourcePath),
          algorithmLabel: formatAlgorithmLabel(result.algorithm ?? entry.algorithm, result.keySizeBits ?? entry.keySizeBits),
          ts,
          timeLabel,
          failed,
          path: displayPath,
        })
      })
    }
  }
  return items.sort((a, b) => b.ts - a.ts).slice(0, 60)
}

function deriveWeekBuckets(history: HistoryEntry[]) {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return Array.from({ length: 7 }, (_, index) => {
    const date = new Date(today)
    date.setDate(today.getDate() - (6 - index))
    const next = new Date(date)
    next.setDate(date.getDate() + 1)
    const entries = history.filter((entry) => {
      const ts = new Date(entry.timestampUtc)
      return ts >= date && ts < next
    })

    return {
      date: date.toISOString().slice(0, 10),
      label: date.toLocaleDateString("en-US", { weekday: "short" }),
      count: entries.length,
      failedCount: entries.filter(isFailedHistoryEntry).length,
    }
  })
}

function groupByDate(items: ActivityItem[]): Array<{ label: string; items: ActivityItem[] }> {
  const now            = Date.now()
  const todayStart     = new Date(now); todayStart.setHours(0, 0, 0, 0)
  const yesterdayStart = new Date(todayStart.getTime() - 86_400_000)
  const monthAgo       = now - 30 * 86_400_000

  const b = { today: [] as ActivityItem[], yesterday: [] as ActivityItem[], month: [] as ActivityItem[], older: [] as ActivityItem[] }
  for (const item of items) {
    if      (item.ts >= todayStart.getTime())     b.today.push(item)
    else if (item.ts >= yesterdayStart.getTime()) b.yesterday.push(item)
    else if (item.ts >= monthAgo)                 b.month.push(item)
    else                                           b.older.push(item)
  }
  return [
    { label: "Today",              items: b.today     },
    { label: "Yesterday",          items: b.yesterday },
    { label: "Earlier this month", items: b.month     },
    { label: "Older",              items: b.older     },
  ].filter((g) => g.items.length > 0)
}

function sortAndGroup(items: ActivityItem[], sort: SortKey): Array<{ label: string; items: ActivityItem[] }> {
  if (sort === "date")     return groupByDate([...items].sort((a, b) => b.ts - a.ts))
  if (sort === "size")     return [{ label: "Largest first", items: [...items].sort((a, b) => b.sizeBytes - a.sizeBytes) }]
  if (sort === "fileType") {
    const byType: Record<string, ActivityItem[]> = {}
    for (const item of items) (byType[item.fileType] ??= []).push(item)
    const order = ["Document", "Image", "Archive", "Text", "Folder", "File", "—"]
    return Object.entries(byType)
      .sort(([a], [b]) => (order.indexOf(a) < 0 ? 99 : order.indexOf(a)) - (order.indexOf(b) < 0 ? 99 : order.indexOf(b)))
      .map(([label, list]) => ({ label, items: list.sort((a2, b2) => b2.ts - a2.ts) }))
  }
  if (sort === "action") {
    const byAction: Record<string, ActivityItem[]> = {}
    for (const item of items) (byAction[item.action] ??= []).push(item)
    return ["encrypt", "decrypt", "hash", "metadata", "encode", "delete"]
      .filter((a) => byAction[a])
      .map((a) => ({ label: ACTION_LABELS[a], items: byAction[a].sort((a2, b2) => b2.ts - a2.ts) }))
  }
  return [{ label: "All", items }]
}

// ─── Sub-components ────────────────────────────────────────────────────────

function ActivityIconTile({ tone, action, failed }: { tone: string; action: string; failed: boolean }) {
  const c    = TONE_COLORS[tone] ?? TONE_COLORS.blue
  const Icon = failed
    ? AlertTriangle
    : (ACTIVITY_ICONS[action as keyof typeof ACTIVITY_ICONS] ?? LockKeyhole)
  return (
    <div
      style={{
        width: 28, height: 28, borderRadius: 8, flexShrink: 0,
        display: "flex", alignItems: "center", justifyContent: "center",
        background: `${c}1f`, color: c, border: `1px solid ${c}33`,
      }}
    >
      <Icon style={{ width: 14, height: 14 }} aria-hidden />
    </div>
  )
}

function ActivityRow({ item, onReveal }: { item: ActivityItem; onReveal?: (path: string) => void }) {
  const toneClass = {
    blue:   "text-accent-blue",
    teal:   "text-accent-teal",
    purple: "text-accent-purple",
    orange: "text-accent-orange",
    red:    "text-accent-red",
  }[item.tone] ?? "text-secondary"

  const canReveal = Boolean(item.path && onReveal)
  const row = (
    <div
      className="grid items-center gap-3 rounded-lg px-2 py-2.5 cursor-pointer transition-colors hover:bg-bg-surface-hover/55"
      style={{ gridTemplateColumns: "28px minmax(0,1fr) auto auto" }}
    >
      <ActivityIconTile tone={item.tone} action={item.action} failed={item.failed} />
      <div className="min-w-0">
        <div className="truncate font-display text-[13.5px] font-medium text-primary leading-[1.3] mb-0.5">
          {item.name}
        </div>
        <div className="flex items-center gap-1.5 text-xs text-secondary leading-[1.4]">
          <span className={toneClass}>{item.op}</span>
          {item.fileType !== "—" && (
            <>
              <span className="size-[3px] rounded-full bg-muted/50 shrink-0" />
              <span>{item.fileType}</span>
            </>
          )}
          {item.sizeBytes > 0 && (
            <>
              <span className="size-[3px] rounded-full bg-muted/50 shrink-0" />
              <span>{formatBytes(item.sizeBytes)}</span>
            </>
          )}
          {item.algorithmLabel && (
            <>
              <span className="size-[3px] rounded-full bg-muted/50 shrink-0" />
              <span>{item.algorithmLabel}</span>
            </>
          )}
        </div>
      </div>
      <div />
      <div className="text-xs text-muted text-right min-w-[60px]">{item.timeLabel}</div>
    </div>
  )

  if (!canReveal) {
    return row
  }

  return (
    <button
      className="block w-full border-0 bg-transparent p-0 text-left font-[inherit]"
      type="button"
      onClick={() => item.path && onReveal?.(item.path)}
      title="Reveal file"
      aria-label={`Reveal ${item.name}`}
    >
      {row}
    </button>
  )
}

function SortMenu({ value, onChange }: { value: SortKey; onChange: (v: SortKey) => void }) {
  const [open, setOpen] = useState(false)
  const ref             = useRef<HTMLDivElement>(null)
  const triggerRef      = useRef<HTMLButtonElement>(null)
  const optionRefs      = useRef<Record<SortKey, HTMLButtonElement | null>>({
    date: null,
    fileType: null,
    size: null,
    action: null,
  })
  const menuId = "dashboard-sort-menu"

  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener("mousedown", onDoc)
    return () => document.removeEventListener("mousedown", onDoc)
  }, [])

  const active = SORT_OPTIONS.find((o) => o.value === value) ?? SORT_OPTIONS[0]

  const SortIcon = { date: Calendar, fileType: FileText, size: HardDrive, action: Shield }

  function focusSortOption(index: number) {
    const option = SORT_OPTIONS[index]
    if (!option) {
      return
    }

    optionRefs.current[option.value]?.focus()
  }

  function openMenuAndFocus(index: number) {
    setOpen(true)
    window.requestAnimationFrame(() => focusSortOption(index))
  }

  function handleMenuKeyDown(event: ReactKeyboardEvent<HTMLDivElement>) {
    if (!open) {
      return
    }

    if (event.key === "Escape") {
      event.preventDefault()
      setOpen(false)
      triggerRef.current?.focus()
      return
    }

    let nextIndex: number | null = null
    const currentIndex = SORT_OPTIONS.findIndex((option) => optionRefs.current[option.value] === document.activeElement)
    switch (event.key) {
      case "ArrowDown":
        nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % SORT_OPTIONS.length
        break
      case "ArrowUp":
        nextIndex = currentIndex < 0 ? SORT_OPTIONS.length - 1 : (currentIndex - 1 + SORT_OPTIONS.length) % SORT_OPTIONS.length
        break
      case "Home":
        nextIndex = 0
        break
      case "End":
        nextIndex = SORT_OPTIONS.length - 1
        break
      default:
        return
    }

    event.preventDefault()
    focusSortOption(nextIndex)
  }

  function handleTriggerKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>) {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") {
      return
    }

    event.preventDefault()
    openMenuAndFocus(event.key === "ArrowDown" ? 0 : SORT_OPTIONS.length - 1)
  }

  function selectSortOption(nextValue: SortKey) {
    onChange(nextValue)
    setOpen(false)
    triggerRef.current?.focus()
  }

  return (
    <div className="relative" ref={ref} onKeyDown={handleMenuKeyDown}>
      <button
        type="button"
        ref={triggerRef}
        className={cn(
          "inline-flex items-center gap-2 h-8 px-3 rounded-lg border text-[12.5px] cursor-pointer transition-colors",
          "border-border bg-[rgba(26,37,56,0.55)] text-secondary font-[inherit]",
          "hover:bg-bg-surface-hover hover:border-border-strong hover:text-primary",
          open && "bg-bg-surface-hover border-accent-blue text-primary"
        )}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={handleTriggerKeyDown}
        aria-expanded={open}
        aria-haspopup="menu"
        aria-controls={open ? menuId : undefined}
      >
        <ArrowUpDown className="size-3" aria-hidden />
        <span>Sort by</span>
        <span className="text-primary font-medium">{active.label}</span>
        <ChevronDown className="size-3" aria-hidden />
      </button>

      {open && (
        <div id={menuId} className="dash-sort-pop" role="menu">
          {SORT_OPTIONS.map((o) => {
            const ItemIcon = SortIcon[o.value]
            const selected = value === o.value
            return (
              <button
                type="button"
                key={o.value}
                ref={(node) => {
                  optionRefs.current[o.value] = node
                }}
                className={cn(
                  "grid items-center gap-2.5 h-8 px-2.5 rounded-md bg-transparent border-0",
                  "text-secondary font-[inherit] text-[13px] text-left cursor-pointer transition-colors",
                  "hover:bg-bg-surface-hover hover:text-primary",
                  selected && "text-primary"
                )}
                style={{ gridTemplateColumns: "16px 1fr 16px" }}
                onClick={() => selectSortOption(o.value)}
                role="menuitemradio"
                aria-checked={selected}
              >
                <ItemIcon className="size-[13px]" aria-hidden />
                <span>{o.label}</span>
                {selected ? <Check className="size-[13px] text-accent-blue" aria-hidden /> : <span />}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

function ActivitySection({
  history,
  privacyModeEnabled,
  onReveal,
}: {
  history: HistoryEntry[]
  privacyModeEnabled: boolean
  onReveal: (path: string) => void
}) {
  const [sort, setSort] = useState<SortKey>("date")

  const items  = useMemo(() => deriveActivityItems(history), [history])
  const groups = useMemo(() => sortAndGroup(items, sort), [items, sort])

  return (
    <section>
      <div className="mb-3.5 flex items-baseline justify-between gap-4">
        <div>
          <h2 className="font-display text-base font-semibold text-primary leading-[1.3] mb-1">Recent activity</h2>
          <div className="text-[13px] text-secondary leading-[1.4]">
            {privacyModeEnabled ? "Activity recording is off" : "Local-only · Not synced"}
          </div>
        </div>
        {!privacyModeEnabled && <SortMenu value={sort} onChange={setSort} />}
      </div>

      {privacyModeEnabled || items.length === 0 ? (
        <div className="flex min-h-[5rem] items-center justify-center border-y border-border text-sm text-muted">
          {privacyModeEnabled ? "Activity recording is off" : "No activity yet — run a workflow to populate this list"}
        </div>
      ) : (
        groups.map((g) => (
          <div key={g.label} className="[&+&]:mt-4">
            <div className="pb-2.5 text-[11.5px] font-medium text-muted px-0.5">{g.label}</div>
            <div className="flex flex-col [&>*+*]:border-t [&>*+*]:border-[rgba(150,173,205,0.08)]">
              {g.items.map((item) => (
                <ActivityRow key={item.key} item={item} onReveal={onReveal} />
              ))}
            </div>
          </div>
        ))
      )}
    </section>
  )
}

function QuickActionsPanel({ onNavigate }: { onNavigate: (page: PageKey) => void }) {
  return (
    <section className="px-4 py-3">
      <div className="mb-3">
        <h2 className="font-display text-base font-semibold leading-[1.3] text-primary">Quick actions</h2>
        <p className="mt-1 text-[13px] leading-snug text-secondary">Jump straight to the common protection tools.</p>
      </div>
      <div className="grid gap-2">
        {QUICK_ACTIONS.map((item) => {
          const Icon = item.icon
          const tone = TONE_COLORS[item.tone] ?? TONE_COLORS.blue
          return (
            <button
              key={item.page}
              type="button"
              className="grid min-h-11 grid-cols-[auto_minmax(0,1fr)] items-center gap-3 rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2 text-left transition-colors hover:border-border-accent hover:bg-bg-surface-hover/60"
              onClick={() => onNavigate(item.page)}
            >
              <span
                className="flex size-8 shrink-0 items-center justify-center rounded-md border"
                style={{ color: tone, background: `${tone}1f`, borderColor: `${tone}33` }}
              >
                <Icon className="size-4" aria-hidden />
              </span>
              <span className="min-w-0">
                <span className="block truncate font-display text-sm font-semibold leading-tight text-primary">{item.label}</span>
                <span className="mt-0.5 block truncate text-xs text-secondary">{item.detail}</span>
              </span>
            </button>
          )
        })}
      </div>
    </section>
  )
}

function StorageSavedCard({ dashboard }: { dashboard: DashboardState }) {
  const breakdown = dashboard.storageBreakdown ?? []
  const trackedFiles = dashboard.storageTrackedFiles ?? 0
  const compressionRequested = dashboard.compressionRequestedCount ?? 0
  const compressionApplied = dashboard.compressionAppliedCount ?? 0
  const storageAddedBytes = dashboard.storageAddedBytes ?? 0
  const hasTrackedStorage = trackedFiles > 0 || breakdown.length > 0

  return (
    <div className="px-4 py-3">
      <div className="flex items-start justify-between gap-3 mb-3">
        <div>
          <div className="font-display text-[14.5px] font-semibold text-primary leading-[1.3] mb-1">Storage saved</div>
          <div className="text-[12.5px] text-secondary leading-[1.4]">
            {dashboard.storageSavedSubtitle || "Tracked since installation"}
          </div>
        </div>
        <div className="inline-flex items-center gap-1 text-[11.5px] font-medium text-accent-green shrink-0">
          <TrendingUp className="size-3" />
          <span>{dashboard.storageSavedDeltaText || "—"}</span>
        </div>
      </div>
      <div className="font-display text-[30px] font-semibold leading-none tracking-tight text-primary mt-1">
        {dashboard.storageSavedDisplay || "—"}
      </div>
      {hasTrackedStorage ? (
        <>
          <div
            className="flex mt-3.5 h-1.5 rounded-full overflow-hidden gap-0.5"
            style={{ background: "rgba(150,173,205,0.08)" }}
            aria-hidden
          >
            {breakdown.map((item) => (
              <span
                key={item.label}
                style={{
                  flex: getStorageBreakdownFlex(item.percent),
                  background: TONE_COLORS[item.tone] ?? TONE_COLORS.blue,
                }}
                title={`${item.label}: ${item.display}`}
              />
            ))}
          </div>
          <div className="mt-3 flex flex-col gap-1.5">
            {breakdown.map((item) => (
              <span key={item.label} className="inline-flex items-center gap-2 text-xs text-secondary">
                <span className="size-2 rounded-full shrink-0" style={{ background: TONE_COLORS[item.tone] ?? TONE_COLORS.blue }} />
                {item.label}
                <span className="ml-auto font-mono text-[11.5px] text-muted">{item.display}</span>
              </span>
            ))}
            <span className="inline-flex items-center gap-2 text-xs text-secondary">
              <span className="size-2 rounded-full shrink-0 bg-border" />
              Tracked files
              <span className="ml-auto font-mono text-[11.5px] text-muted">{trackedFiles}</span>
            </span>
          </div>
        </>
      ) : (
        <div className="mt-3 rounded-lg border border-dashed border-border/80 px-3 py-2 text-xs leading-[1.45] text-secondary">
          Storage savings appear after an encryption run with compression enabled.
        </div>
      )}
      <div className="mt-3 grid grid-cols-2 gap-2 border-t border-[rgba(150,173,205,0.10)] pt-3">
        <div>
          <div className="text-[11px] uppercase text-muted">Compressed</div>
          <div className="font-mono text-xs text-primary">{compressionApplied}/{compressionRequested}</div>
        </div>
        <div className="text-right">
          <div className="text-[11px] uppercase text-muted">Overhead</div>
          <div className="font-mono text-xs text-primary">{formatBytes(storageAddedBytes)}</div>
        </div>
      </div>
    </div>
  )
}

function CustomCleanCard({
  invoke,
  onNavigate,
}: {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
  onNavigate: (page: PageKey) => void
}) {
  const [scan, setScan] = useState<CleanupScanResult | null>(null)
  const [isScanning, setIsScanning] = useState(true)
  const [scanError, setScanError] = useState("")

  useEffect(() => {
    let cancelled = false
    async function scanCleanup() {
      setIsScanning(true)
      try {
        const response = await invoke<CleanupScanResult>("maintenance.scanCleanup", {})
        if (!cancelled) {
          setScan(response)
          setScanError("")
        }
      } catch (error) {
        if (!cancelled) {
          setScan(null)
          setScanError(error instanceof Error ? error.message : "Cleanup scan failed.")
        }
      } finally {
        if (!cancelled) {
          setIsScanning(false)
        }
      }
    }

    void scanCleanup()
    return () => { cancelled = true }
  }, [invoke])

  const categories = scan?.categories ?? []
  const defaultSelected = categories.filter((category) => category.defaultSelected && category.isEnabled)
  const topCategories = [...categories].sort((a, b) => b.sizeBytes - a.sizeBytes).slice(0, 3)
  const totalDisplay = isScanning ? "Scanning" : scanError ? "Scan failed" : scan?.totalDisplay ?? "Unavailable"
  const cleanupSummaryRows = topCategories.length > 0
    ? topCategories
    : [
        {
          id: scanError ? "scan-error" : isScanning ? "scan-pending" : "scan-empty",
          label: scanError ? "Cleanup scan unavailable" : isScanning ? "Scanning cleanup areas" : "No cleanup areas found",
          sizeDisplay: scanError ? "Retry" : isScanning ? "Scanning" : "0 B",
        },
      ]

  return (
    <div className="px-4 py-3">
      <div className="flex items-start justify-between gap-3 mb-1">
        <div>
          <div className="font-display text-[14.5px] font-semibold text-primary leading-[1.3] mb-1">Custom Clean</div>
          <div className="text-[12.5px] text-secondary leading-[1.4]">Free up space on this device</div>
        </div>
        <div
          className="flex size-7 items-center justify-center rounded-lg shrink-0"
          style={{
            background: "rgba(169,116,255,0.14)",
            color: "#a974ff",
            border: "1px solid rgba(169,116,255,0.3)",
          }}
        >
          <Sparkles className="size-3.5" aria-hidden />
        </div>
      </div>
      <div className="flex items-baseline gap-2 my-3">
        <span className="font-display text-[26px] font-semibold leading-none tracking-tight text-primary">{totalDisplay}</span>
        <span className="text-xs text-secondary">
          {scan ? `${scan.totalFiles} item${scan.totalFiles === 1 ? "" : "s"} found` : "recoverable space"}
        </span>
      </div>
      <div className="flex flex-col border-t border-[rgba(150,173,205,0.10)] mb-3.5">
        {cleanupSummaryRows.map((item) => (
          <div
            key={item.id}
            className="flex justify-between items-center py-1.5 border-b border-[rgba(150,173,205,0.08)] last:border-0 text-[12.5px]"
          >
            <span className="text-secondary">{item.label}</span>
            <span className="font-mono text-xs text-muted">{item.sizeDisplay}</span>
          </div>
        ))}
      </div>
      {scan ? (
        <div className="mb-3 text-xs leading-[1.45] text-secondary">
          {defaultSelected.length} safe area{defaultSelected.length === 1 ? "" : "s"} selected by default.
          {scan.skippedItems > 0 ? ` ${scan.skippedItems} locked item${scan.skippedItems === 1 ? "" : "s"} skipped.` : ""}
        </div>
      ) : null}
      {scanError ? (
        <div className="mb-3 rounded-md border border-amber-400/30 bg-amber-400/8 px-3 py-2 text-xs leading-[1.45] text-secondary" role="status" aria-live="polite">
          {scanError}
        </div>
      ) : null}
      <div className="grid grid-cols-[auto_1fr] gap-2">
        <Button
          variant="outline"
          size="icon"
          aria-label="Rescan cleanup areas"
          onClick={() => {
            setIsScanning(true)
            setScanError("")
            void invoke<CleanupScanResult>("maintenance.scanCleanup", {})
              .then((response) => {
                setScan(response)
                setScanError("")
              })
              .catch((error) => {
                const message = error instanceof Error ? error.message : "Cleanup scan failed."
                setScan(null)
                setScanError(message)
                toast.error(message)
              })
              .finally(() => setIsScanning(false))
          }}
          disabled={isScanning}
        >
          <RefreshCw className={cn("size-3.5", isScanning && "animate-spin")} />
        </Button>
        <Button variant="secondary" className="w-full" onClick={() => onNavigate("custom-clean")}>
          <Sparkles data-icon="inline-start" className="size-3.5" />
          Run Custom Clean
        </Button>
      </div>
    </div>
  )
}

function WeekPanel({ dashboard }: { dashboard: DashboardState }) {
  const buckets = useMemo(() => {
    const bridgeBuckets = dashboard.operationsThisWeek ?? []
    const sourceBuckets = bridgeBuckets.length === 7 ? bridgeBuckets : deriveWeekBuckets(dashboard.history ?? [])
    return sourceBuckets.map((bucket) => ({
      ...bucket,
      count: getNonNegativeFiniteNumber(bucket.count),
      failedCount: getNonNegativeFiniteNumber(bucket.failedCount),
    }))
  }, [dashboard])
  const counts = buckets.map((bucket) => bucket.count)
  const max = Math.max(...counts, 1)
  const total = getNonNegativeFiniteNumber(dashboard.operationsThisWeekCount ?? counts.reduce((a, b) => a + b, 0))
  const failed = Math.min(
    total,
    getNonNegativeFiniteNumber(dashboard.failedOperationsThisWeekCount ?? buckets.reduce((a, b) => a + b.failedCount, 0))
  )
  const completed = dashboard.successfulOperationsThisWeekCount == null
    ? Math.max(total - failed, 0)
    : Math.min(total, getNonNegativeFiniteNumber(dashboard.successfulOperationsThisWeekCount))

  return (
    <div className="px-4 py-3">
      <div className="flex items-start justify-between gap-3 mb-1">
        <div>
          <div className="font-display text-[14.5px] font-semibold text-primary leading-[1.3] mb-1">This week</div>
          <div className="text-[12.5px] text-secondary leading-[1.4]">Operations performed</div>
        </div>
        <TrendingUp className="size-3.5 text-accent-green mt-0.5 shrink-0" />
      </div>
      <div className="font-display text-[30px] font-semibold leading-none tracking-tight text-primary mt-1">
        {total}
      </div>
      <div className="mt-1 text-[12.5px] text-secondary">
        {completed} completed
        {failed > 0 ? `, ${failed} need review` : ""}
      </div>
      <div className="dash-spark mt-3.5">
        {buckets.map((bucket, i) => (
          <div
            key={bucket.date}
            className={cn("dash-spark-bar", i === buckets.length - 1 && "dash-spark-bar--today")}
            style={{ height: `${Math.max((bucket.count / max) * 100, bucket.count > 0 ? 12 : 4)}%` }}
            title={`${bucket.label}: ${bucket.count} ops${bucket.failedCount > 0 ? `, ${bucket.failedCount} failed` : ""}`}
          />
        ))}
      </div>
      <div className="dash-spark-labels mt-1.5">
        {buckets.map((bucket, i) => (
          <span
            key={bucket.date}
            className={cn(
              "block text-center font-mono text-[9.5px] uppercase tracking-wider text-muted",
              i === buckets.length - 1 && "text-accent-blue"
            )}
          >
            {bucket.label}
          </span>
        ))}
      </div>
    </div>
  )
}

function Hero({
  compact,
  dragging,
  algorithmLabel,
  encryptionAvailable,
  onDragEnter,
  onDragOver,
  onDragLeave,
  onDrop,
  onBrowse,
  onBrowseFolder,
  disabled = false,
}: {
  compact: boolean
  dragging: boolean
  algorithmLabel: string
  encryptionAvailable: boolean
  onDragEnter: (e: DragEvent<HTMLDivElement>) => void
  onDragOver: (e: DragEvent<HTMLDivElement>) => void
  onDragLeave: () => void
  onDrop: (e: DragEvent<HTMLDivElement>) => void
  onBrowse: () => void
  onBrowseFolder: () => void
  disabled?: boolean
}) {
  return (
    <div
      className={cn("dash-hero", dragging && "is-dragging", disabled && "opacity-70")}
      role="group"
      aria-labelledby="dashboard-hero-drop-zone-title"
      aria-describedby={compact ? undefined : "dashboard-hero-drop-zone-description"}
      aria-disabled={disabled}
      onDragEnter={onDragEnter}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
    >
      <div
        className={cn(
          "relative z-10",
          compact
            ? "flex flex-row items-center gap-3.5 px-5 py-[18px] text-left"
            : "flex flex-col items-center text-center gap-[18px] px-8 py-[44px]"
        )}
      >
        {/* Lock icon */}
        <div
          className={cn(
            "flex items-center justify-center shrink-0 border border-border-strong bg-accent/10 text-accent",
            compact ? "size-10 rounded-lg" : "size-14 rounded-xl"
          )}
        >
          <LockKeyhole className={compact ? "size-5" : "size-7"} aria-hidden />
        </div>

        {/* Text */}
        <div className={cn(compact ? "flex-1 min-w-0" : "")}>
          <h2
            id="dashboard-hero-drop-zone-title"
            className={cn(
              "font-display font-semibold text-primary tracking-tight m-0",
              compact ? "text-[15px]" : "text-[22px] leading-[1.2] text-balance"
            )}
          >
            {compact ? "Add more files or encrypt the queue" : "Drop files or folders to encrypt"}
          </h2>
          {!compact && (
            <p id="dashboard-hero-drop-zone-description" className="mt-2 text-[13.5px] leading-[1.55] text-secondary max-w-[440px] text-pretty mb-0">
              {encryptionAvailable
                ? `Everything stays on this device. Encrypted with ${algorithmLabel} using a password you control.`
                : "Dashboard quick encryption is unavailable because no supported file-encryption algorithm passed the runtime check."}
            </p>
          )}
        </div>

        {/* Buttons */}
        <div className={cn("flex gap-2.5 flex-wrap", !compact && "mt-1.5 justify-center")}>
          <Button onClick={onBrowse} disabled={disabled}>
            <Plus data-icon="inline-start" />
            Browse Files
          </Button>
          <Button variant="secondary" onClick={onBrowseFolder} disabled={disabled}>
            <FolderOpen data-icon="inline-start" />
            Browse Folder
          </Button>
        </div>
      </div>
    </div>
  )
}

function QueueCard({
  queuedPaths,
  onRemove,
  onClear,
  encryptionAvailable,
  password,
  setPassword,
  confirmPassword,
  setConfirmPassword,
  encryptError,
  isEncrypting,
  encryptAttempted,
  encryptOutputSuggestion,
  progressEvents,
  activeOperationId,
  onEncrypt,
}: {
  queuedPaths: string[]
  onRemove: (path: string) => void
  onClear: () => void
  encryptionAvailable: boolean
  password: string
  setPassword: (v: string) => void
  confirmPassword: string
  setConfirmPassword: (v: string) => void
  encryptError: string
  isEncrypting: boolean
  encryptAttempted: boolean
  encryptOutputSuggestion: EncryptOutputSuggestion | null
  progressEvents: ProgressEvent[]
  activeOperationId: string
  onEncrypt: () => void
}) {
  const latestProgress = useMemo(
    () => getLatestProgressForOperation(progressEvents, activeOperationId),
    [activeOperationId, progressEvents]
  )
  const hasPassword = password.trim().length > 0
  const pwOk = hasPassword && password === confirmPassword

  return (
    <div className="px-4 py-3">
      {/* Header */}
      <div className="flex items-center justify-between gap-3 mb-3">
        <div>
          <div className="font-display text-[14.5px] font-semibold text-primary leading-[1.3]">Encryption queue</div>
          <div className="text-[12.5px] text-secondary mt-0.5">
            {queuedPaths.length} item{queuedPaths.length === 1 ? "" : "s"} ready ·{" "}
            {encryptOutputSuggestion?.suggestedPath
              ? `Output → ${fileName(encryptOutputSuggestion.suggestedPath)}`
              : "Output to source folders"}
          </div>
        </div>
        <button
          type="button"
          className="inline-flex items-center gap-1 bg-transparent border-0 text-[12.5px] text-secondary hover:text-accent-blue transition-colors cursor-pointer px-1 py-0.5 rounded"
          onClick={onClear}
          disabled={!queuedPaths.length || isEncrypting}
        >
          <X className="size-3" /> Clear
        </button>
      </div>

      {/* Queue list */}
      <div className="flex flex-col">
        {queuedPaths.map((path) => (
          <div
            key={path}
            className="grid items-center gap-3 py-2.5 border-b border-[rgba(150,173,205,0.08)] last:border-0"
            style={{ gridTemplateColumns: "16px minmax(0,1fr) auto 24px" }}
          >
            <HardDrive className="size-3.5 text-muted shrink-0" aria-hidden />
            <span className="font-mono text-[12.5px] text-primary truncate">{path}</span>
            <span className="font-mono text-[11.5px] text-muted">—</span>
            <button
              type="button"
              className="size-6 rounded-md inline-flex items-center justify-center bg-transparent border-0 cursor-pointer text-muted hover:bg-[rgba(239,97,125,0.12)] hover:text-accent-red transition-colors"
              onClick={() => onRemove(path)}
              aria-label={`Remove ${fileName(path)}`}
              disabled={isEncrypting}
            >
              <X className="size-3" />
            </button>
          </div>
        ))}
      </div>

      {/* Progress bar */}
      {latestProgress && (
        <div className="mt-3">
          <ProgressBar value={latestProgress.percent} label={`${latestProgress.fileName} / ${latestProgress.status}`} />
        </div>
      )}

      {encryptError ? (
        <div className="mt-3 rounded-md border border-destructive/35 bg-destructive/10 px-3 py-2 text-sm leading-snug text-red-100" role="status" aria-live="polite">
          <div className="flex items-start gap-2.5">
            <AlertTriangle className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
            <span>{encryptError}</span>
          </div>
        </div>
      ) : null}

      {/* Password row + encrypt */}
      <div
        className="grid gap-2.5 mt-3.5 pt-3.5 border-t border-dashed border-[rgba(150,173,205,0.18)] items-end"
        style={{ gridTemplateColumns: "1fr 1fr auto" }}
      >
        <PasswordField label="Password" value={password} onChange={setPassword} placeholder="Password" disabled={isEncrypting || !encryptionAvailable} />
        <PasswordField
          label="Confirm Password"
          value={confirmPassword}
          onChange={setConfirmPassword}
          placeholder="Confirm"
          disabled={isEncrypting || !encryptionAvailable}
        />
        <Button
          className="h-10 px-4"
          onClick={onEncrypt}
          disabled={!pwOk || isEncrypting || !encryptionAvailable}
        >
          <LockKeyhole data-icon="inline-start" />
          {!encryptionAvailable ? "Unavailable" : isEncrypting ? "Encrypting…" : "Encrypt"}
        </Button>
      </div>

      {/* Validation messages */}
      {encryptAttempted && !encryptionAvailable && (
        <p className="mt-1.5 text-sm text-accent-red">No supported file-encryption algorithm is available on this device.</p>
      )}
      {encryptAttempted && !hasPassword && (
        <p className="mt-1.5 text-sm text-accent-red">Password is required.</p>
      )}
      {password && confirmPassword && password !== confirmPassword && (
        <p className="mt-1.5 text-sm text-accent-red">Passwords do not match.</p>
      )}
      {encryptAttempted && queuedPaths.length === 0 && (
        <p className="mt-1.5 text-sm text-accent-red">Add files or folders before encrypting.</p>
      )}
    </div>
  )
}

// ─── Main page ─────────────────────────────────────────────────────────────

export function DashboardPage({
  dashboard,
  onNavigate,
  invoke,
  progressEvents,
  onDashboardUpdate,
  onReveal,
  privacyModeEnabled = false,
  encryptionAlgorithms,
  droppedPaths = [],
  onDroppedPathsHandled,
}: DashboardPageProps) {
  const [queuedPaths,    setQueuedPaths]    = useState<string[]>([])
  const [password,       setPassword]       = useState("")
  const [confirmPassword, setConfirmPassword] = useState("")
  const [isDragging,     setIsDragging]     = useState(false)
  const [isEncrypting,   setIsEncrypting]   = useState(false)
  const [encryptAttempted, setEncryptAttempted] = useState(false)
  const [encryptOutputSuggestion, setEncryptOutputSuggestion] = useState<EncryptOutputSuggestion | null>(null)
  const [activeOperationId, setActiveOperationId] = useState("")
  const [encryptError, setEncryptError] = useState("")
  const dragDepthRef = useRef(0)
  const quickEncryptOptions = useMemo(
    () => getEncryptionAlgorithmOptions(encryptionAlgorithms),
    [encryptionAlgorithms]
  )
  const quickEncryptAlgorithm = getDefaultEncryptionAlgorithm(quickEncryptOptions)
  const quickEncryptAlgorithmLabel = quickEncryptAlgorithm?.label ?? "Unavailable"
  const quickEncryptAvailable = Boolean(quickEncryptAlgorithm)
  const quickEncryptDisabled = isEncrypting || !quickEncryptAvailable

  useEffect(() => {
    if (isEncrypting) {
      dragDepthRef.current = 0
      setIsDragging(false)
    }
  }, [isEncrypting])

  // Handle paths dropped from native shell
  useEffect(() => {
    if (droppedPaths.length === 0) return
    if (!quickEncryptAvailable) {
      toast.error("Dashboard quick encryption is unavailable on this device.")
      onDroppedPathsHandled?.()
      return
    }

    if (isEncrypting) {
      toast.error("Wait for dashboard encryption to finish before changing the queue.")
      onDroppedPathsHandled?.()
      return
    }

    setQueuedPaths((current) => mergeUniquePaths(current, droppedPaths))
    setActiveOperationId("")
    setEncryptError("")
    onDroppedPathsHandled?.()
  }, [droppedPaths, isEncrypting, onDroppedPathsHandled, quickEncryptAvailable])

  // Suggest output directory for folder selections
  useEffect(() => {
    if (queuedPaths.length === 0) { setEncryptOutputSuggestion(null); return }
    let cancelled = false
    invoke<EncryptOutputSuggestion>("files.suggestEncryptOutput", { paths: queuedPaths })
      .then((s) => { if (!cancelled) setEncryptOutputSuggestion(s) })
      .catch(() => { if (!cancelled) setEncryptOutputSuggestion(null) })
    return () => { cancelled = true }
  }, [invoke, queuedPaths])

  async function pickFiles() {
    if (!quickEncryptAvailable) {
      toast.error("Dashboard quick encryption is unavailable on this device.")
      return
    }

    if (isEncrypting) {
      toast.error("Wait for dashboard encryption to finish before changing the queue.")
      return
    }

    try {
      const response = await invoke<{ paths: string[] }>("files.pickFiles")
      setQueuedPaths((current) => mergeUniquePaths(current, response.paths))
      setActiveOperationId("")
      setEncryptError("")
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick files.")
    }
  }

  async function pickFolder() {
    if (!quickEncryptAvailable) {
      toast.error("Dashboard quick encryption is unavailable on this device.")
      return
    }

    if (isEncrypting) {
      toast.error("Wait for dashboard encryption to finish before changing the queue.")
      return
    }

    try {
      const response = await invoke<{ path: string }>("files.pickFolder")
      if (response.path) {
        setQueuedPaths((current) => mergeUniquePaths(current, [response.path]))
        setActiveOperationId("")
        setEncryptError("")
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to pick folder.")
    }
  }

  function handleHeroDragEnter(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()

    if (quickEncryptDisabled) {
      return
    }

    dragDepthRef.current += 1
    setIsDragging(true)
  }

  function handleHeroDragOver(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    event.dataTransfer.dropEffect = quickEncryptDisabled ? "none" : "copy"

    if (!quickEncryptDisabled && !isDragging) {
      setIsDragging(true)
    }
  }

  function handleHeroDragLeave() {
    if (quickEncryptDisabled) {
      dragDepthRef.current = 0
      setIsDragging(false)
      return
    }

    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1)
    if (dragDepthRef.current === 0) {
      setIsDragging(false)
    }
  }

  function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    dragDepthRef.current = 0
    setIsDragging(false)
    if (isEncrypting) {
      toast.error("Wait for dashboard encryption to finish before changing the queue.")
      return
    }

    if (!quickEncryptAvailable) {
      toast.error("Dashboard quick encryption is unavailable on this device.")
      return
    }

    const paths = Array.from(event.dataTransfer.files)
      .map((file) => (file as File & { path?: string }).path)
      .filter((path): path is string => Boolean(path))
    if (paths.length > 0) {
      setQueuedPaths((current) => mergeUniquePaths(current, paths))
      setActiveOperationId("")
      setEncryptError("")
    }
  }

  async function encryptQueuedPaths() {
    if (isEncrypting) {
      return
    }

    setEncryptAttempted(true)
    if (!quickEncryptAlgorithm) { toast.error("No supported file-encryption algorithm is available on this device."); return }
    if (queuedPaths.length === 0) { toast.error("Add files or folders before encrypting."); return }
    if (!password.trim())         { toast.error("Enter a password before encrypting."); return }
    if (password !== confirmPassword) { toast.error("Passwords do not match."); return }

    setIsEncrypting(true)
    const operationId = crypto.randomUUID()
    setActiveOperationId(operationId)
    setEncryptError("")
    try {
      const encryptOutputDirectory = encryptOutputSuggestion?.suggestedPath?.trim() ?? ""
      const payload: FileOperationRequest = {
        operationId,
        paths: queuedPaths,
        password,
        keyfilePath: "",
        recoveryKey: "",
        encryptOutputDirectory,
        decryptOutputDirectory: "",
        backupFolderPath: "",
        metadataNotes: "Queued from dashboard drag-and-drop zone",
        ...dashboardEncryptDefaults,
        algorithm: quickEncryptAlgorithm.id,
        saveNextToSource: encryptOutputDirectory.length === 0,
      }
      const response = await invoke<OperationResultPayload>("crypto.encryptFiles", payload)
      if (response.dashboard) onDashboardUpdate(response.dashboard)
      toast.success(`Encrypt finished: ${response.completed} completed, ${response.failed} failed.`)
      if (response.failed === 0) {
        setQueuedPaths([])
        setActiveOperationId("")
        setPassword("")
        setConfirmPassword("")
        setEncryptAttempted(false)
      }
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Dashboard encrypt failed."
      setEncryptError(message)
      toast.error(message)
    } finally {
      setIsEncrypting(false)
    }
  }

  const hasQueue = queuedPaths.length > 0

  return (
    <div className="w-full px-5 py-6 pb-8">
      <div
        className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]"
        style={{ alignItems: "start" }}
      >
        {/* ── Main column ── */}
        <div className="flex flex-col gap-[22px] min-w-0">
          <Hero
            compact={hasQueue}
            dragging={isDragging}
            algorithmLabel={quickEncryptAlgorithmLabel}
            encryptionAvailable={quickEncryptAvailable}
            onDragEnter={handleHeroDragEnter}
            onDragOver={handleHeroDragOver}
            onDragLeave={handleHeroDragLeave}
            onDrop={handleDrop}
            onBrowse={() => void pickFiles()}
            onBrowseFolder={() => void pickFolder()}
            disabled={quickEncryptDisabled}
          />

          {hasQueue && (
            <QueueCard
              queuedPaths={queuedPaths}
              onRemove={(path) => {
                if (!isEncrypting) {
                  setQueuedPaths((q) => q.filter((p) => getComparableLocalPath(p.trim()) !== getComparableLocalPath(path.trim())))
                  setActiveOperationId("")
                  setEncryptError("")
                }
              }}
              onClear={() => {
                if (!isEncrypting) {
                  setQueuedPaths([])
                  setActiveOperationId("")
                  setEncryptError("")
                }
              }}
              password={password}
              encryptionAvailable={quickEncryptAvailable}
              setPassword={(value) => {
                setPassword(value)
                setEncryptError("")
              }}
              confirmPassword={confirmPassword}
              setConfirmPassword={(value) => {
                setConfirmPassword(value)
                setEncryptError("")
              }}
              encryptError={encryptError}
              isEncrypting={isEncrypting}
              encryptAttempted={encryptAttempted}
              encryptOutputSuggestion={encryptOutputSuggestion}
              progressEvents={progressEvents}
              activeOperationId={activeOperationId}
              onEncrypt={encryptQueuedPaths}
            />
          )}

          <ActivitySection
            history={privacyModeEnabled ? [] : (dashboard.history ?? [])}
            privacyModeEnabled={privacyModeEnabled}
            onReveal={onReveal}
          />
        </div>

        {/* ── Aside ── */}
        <aside className="flex flex-col min-w-0 border-t border-border">
          <div className="border-b border-border">
            <QuickActionsPanel onNavigate={onNavigate} />
          </div>
          <div className="border-b border-border">
            <StorageSavedCard dashboard={dashboard} />
          </div>
          <div className="border-b border-border">
            <CustomCleanCard invoke={invoke} onNavigate={onNavigate} />
          </div>
          <div className="border-b border-border">
            <WeekPanel dashboard={dashboard} />
          </div>
        </aside>
      </div>
    </div>
  )
}
