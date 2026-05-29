import { useEffect, useMemo, useRef, useState } from "react"
import {
  AlertTriangle,
  CheckCircle2,
  Copy,
  Database,
  Download,
  FileJson,
  ExternalLink,
  FolderOpen,
  HardDrive,
  Info,
  MoreVertical,
  Package,
  Play,
  Power,
  PowerOff,
  RefreshCcw,
  ScanLine,
  Search,
  Settings2,
  ShieldAlert,
  Trash2,
  Wrench,
} from "lucide-react"
import { toast } from "sonner"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
import {
  filterStartupItems,
  sortStartupItems,
  type StartupBucketFilter,
  type StartupEnabledFilter,
  type StartupIgnoredFilter,
  type StartupSortKey,
  type StartupTrustFilter,
} from "@/lib/startupListUtils"
import { cn } from "@/lib/utils"
import type {
  AppLeftoverCleanResult,
  AppLeftoverScanResult,
  InstalledApp,
  InstalledAppsScanResult,
  StartupItem,
  StartupExportResult,
  StartupIgnoreResult,
  StartupOpenLocationResult,
  StartupScanResult,
  StartupRestoreRecord,
  StartupToggleResult,
  UninstallerLaunchResult,
} from "@/types/bridge"

type BridgeInvoke = <T>(action: string, payload?: unknown) => Promise<T>

type MaintenancePageProps = {
  invoke: BridgeInvoke
  isAdministrator: boolean
  onRestartAsAdministrator: () => void
}

const MAX_STORED_STRING_ARRAY_JSON_CHARS = 32 * 1024

type MaintenanceDrive = {
  id: string
  name: string
  rootPath: string
  driveType: string
  driveFormat: string
  totalSizeBytes: number
  totalSizeDisplay: string
  freeSpaceBytes: number
  freeSpaceDisplay: string
  isReady: boolean
}

type MaintenanceDriveList = {
  drives: MaintenanceDrive[]
}

type MaintenanceToolResult = {
  ok: boolean
  title: string
  message: string
  driveRoot: string
  output: string
  startedAtUtc: string
  completedAtUtc: string
}

type CleanupCategory = {
  id: string
  group: string
  label: string
  description: string
  path: string
  sizeBytes: number
  sizeDisplay: string
  fileCount: number
  skippedCount: number
  isEnabled: boolean
  requiresAdministrator: boolean
  defaultSelected: boolean
  status: string
  warnings: string[]
  safetyLevel: string
  sizeKnown: boolean
  locations: string[]
  removes: string[]
  keeps: string[]
  recommendation: string
  unavailableReason: string
}

type CleanupScanResult = {
  categories: CleanupCategory[]
  totalBytes: number
  totalDisplay: string
  totalFiles: number
  skippedItems: number
}

type CleanupRunResult = CleanupScanResult & {
  freedBytes: number
  freedDisplay: string
  deletedFiles: number
  warnings: string[]
}

type CleanupSortKey = "size" | "name" | "category" | "safety"

const cleanupPageSize = 25
const cleanupCategoryTabs = ["All", "Windows", "Browsers", "Applications", "Gaming", "Developer Tools", "Privacy", "Advanced"]

type RegistryIssue = {
  id: string
  hive: string
  keyPath: string
  valueName?: string
  subKeyName?: string
  kind: string
  displayName: string
  targetPath: string
  reason: string
  severity: string
  canClean: boolean
  category?: string
}

type RegistryScanResult = {
  issues: RegistryIssue[]
  issueCount: number
  status: string
  warnings: string[]
}

type RegistryCleanResult = {
  cleanedCount: number
  failedCount: number
  backupPath: string
  cleanedIssues: RegistryIssue[]
  failures: Array<{ issueId: string; displayName: string; message: string }>
  scan: RegistryScanResult
}

type RegistrySortKey = "severity" | "category" | "issue" | "key"

const registryIssuePageSize = 25
const registryIssueTabs = [
  { key: "all", label: "All Issues" },
  { key: "ActiveX / COM", label: "ActiveX / COM" },
  { key: "File Types", label: "File Types" },
  { key: "Application Paths", label: "Application Paths" },
  { key: "Startup", label: "Startup" },
  { key: "Uninstall", label: "Uninstall" },
  { key: "Other", label: "Other" },
]

export function PartitionCleanerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [drives, setDrives] = useState<MaintenanceDrive[]>([])
  const [selectedDriveRoot, setSelectedDriveRoot] = useState("")
  const [showWipeConfirmation, setShowWipeConfirmation] = useState(false)
  const [skipWipeConfirmation, setSkipWipeConfirmation] = useStoredBoolean("filelocker.skipWipeFreeSpaceConfirmation")
  const [isLoading, setIsLoading] = useState(true)
  const [driveLoadError, setDriveLoadError] = useState("")
  const [isRunning, setIsRunning] = useState(false)
  const [result, setResult] = useState<MaintenanceToolResult | null>(null)
  const [wipeError, setWipeError] = useState("")
  const selectedDrive = useSelectedDrive(drives, selectedDriveRoot)
  const canStart = isAdministrator && Boolean(selectedDrive?.isReady) && !isRunning

  useEffect(() => {
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }, [invoke])

  function selectDrive(root: string) {
    if (isRunning) { toast.error("Wait for the current free-space wipe to finish before changing drives."); return }
    setSelectedDriveRoot(root)
    setWipeError("")
  }

  function refreshDrives() {
    if (isRunning) return
    setWipeError("")
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }

  function requestWipe() {
    if (isRunning) return
    if (!isAdministrator) { toast.error("Restart FileLocker as administrator before starting a free-space wipe."); return }
    if (!selectedDrive) { toast.error("Select a drive first."); return }
    if (!selectedDrive.isReady) { toast.error("Select a ready drive before starting a free-space wipe."); return }
    if (skipWipeConfirmation) { void runWipe(); return }
    setShowWipeConfirmation(true)
  }

  async function runWipe() {
    if (isRunning) return
    if (!isAdministrator) { toast.error("Restart FileLocker as administrator before starting a free-space wipe."); return }
    if (!selectedDrive) { toast.error("Select a drive first."); return }
    if (!selectedDrive.isReady) { toast.error("Select a ready drive before starting a free-space wipe."); return }
    setIsRunning(true)
    setResult(null)
    setWipeError("")
    try {
      const response = await invoke<MaintenanceToolResult>("maintenance.wipeFreeSpace", {
        driveRoot: selectedDrive.rootPath,
        confirmation: "WIPE FREE SPACE",
      })
      setResult(response)
      setWipeError("")
      toast[response.ok ? "success" : "error"](response.message)
    } catch (error) {
      setWipeError(showMaintenanceError(error, "Free-space wipe failed."))
    } finally {
      setIsRunning(false)
    }
  }

  return (
    <MaintenanceFrame>
      <AdminStatusBanner
        isAdministrator={isAdministrator}
        onRestartAsAdministrator={onRestartAsAdministrator}
        description="Partition Cleaner uses Windows cipher against the selected drive. Restart in administrator mode before starting the wipe."
      />
      <DrivePicker
        drives={drives}
        selectedDriveRoot={selectedDriveRoot}
        onSelect={selectDrive}
        isLoading={isLoading || isRunning}
        loadError={driveLoadError}
        onRefresh={refreshDrives}
      />
      <section className="section-surface">
        <div className="flex items-center gap-2.5">
          <HardDrive className="size-4 shrink-0 text-muted" aria-hidden />
          <span className="font-display text-sm font-semibold tracking-tight text-primary">Free-Space Wipe</span>
        </div>
        <div className="mt-3 grid grid-cols-3 divide-x divide-border border-y border-border">
          <MetricTile label="Drive" value={selectedDrive?.rootPath ?? "None"} />
          <MetricTile label="Free space" value={selectedDrive?.freeSpaceDisplay ?? "Unknown"} />
          <MetricTile label="Format" value={selectedDrive?.driveFormat ?? "Unknown"} />
        </div>
        <div className="app-inline-notice app-inline-notice-warning mt-3">
          <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <span>Wipes free space with Windows cipher so deleted data is harder to recover. Does not rewrite the partition table or delete existing files.</span>
        </div>
        <div className="mt-3">
          <Button variant="destructive" onClick={requestWipe} disabled={!canStart}>
            <Play data-icon="inline-start" />
            {isRunning ? "Wiping..." : "Start Wipe"}
          </Button>
        </div>
      </section>
      <ToolOutput result={result} runningLabel={isRunning ? "Windows cipher is running. This can take a long time on large drives." : undefined} errorMessage={wipeError} />
      <MaintenanceConfirmDialog
        open={showWipeConfirmation}
        onOpenChange={setShowWipeConfirmation}
        title="Start free-space wipe?"
        description={`This will run Windows cipher against ${selectedDrive?.rootPath ?? "the selected drive"} and can take a long time. Existing files are kept in place, but deleted-file traces in free space are overwritten where Windows allows it.`}
        confirmLabel="Start Wipe"
        onConfirm={() => void runWipe()}
        onDontShowAgain={() => { setSkipWipeConfirmation(true); void runWipe() }}
        isBusy={isRunning}
      />
    </MaintenanceFrame>
  )
}

export function DriveOptimizerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [drives, setDrives] = useState<MaintenanceDrive[]>([])
  const [selectedDriveRoot, setSelectedDriveRoot] = useState("")
  const [isLoading, setIsLoading] = useState(true)
  const [driveLoadError, setDriveLoadError] = useState("")
  const [isRunning, setIsRunning] = useState(false)
  const [result, setResult] = useState<MaintenanceToolResult | null>(null)
  const [optimizationError, setOptimizationError] = useState("")
  const selectedDrive = useSelectedDrive(drives, selectedDriveRoot)

  useEffect(() => {
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }, [invoke])

  function selectDrive(root: string) {
    if (isRunning) { toast.error("Wait for the current drive operation to finish before changing drives."); return }
    setSelectedDriveRoot(root)
    setOptimizationError("")
  }

  function refreshDrives() {
    if (isRunning) return
    setOptimizationError("")
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }

  async function runOptimization(mode: "analyze" | "optimize") {
    if (isRunning) return
    if (!isAdministrator) { toast.error("Restart FileLocker as administrator before running drive optimization."); return }
    if (!selectedDrive) { toast.error("Select a drive first."); return }
    if (!selectedDrive.isReady) { toast.error("Select a ready drive before running drive optimization."); return }
    setIsRunning(true)
    setResult(null)
    setOptimizationError("")
    try {
      const response = await invoke<MaintenanceToolResult>("maintenance.optimizeDrive", { driveRoot: selectedDrive.rootPath, mode })
      setResult(response)
      setOptimizationError("")
      toast[response.ok ? "success" : "error"](response.message)
    } catch (error) {
      setOptimizationError(showMaintenanceError(error, "Drive optimization failed."))
    } finally {
      setIsRunning(false)
    }
  }

  return (
    <MaintenanceFrame>
      <AdminStatusBanner
        isAdministrator={isAdministrator}
        onRestartAsAdministrator={onRestartAsAdministrator}
        description="Drive analysis and optimization call Windows defrag/trim tools, which require administrator mode on many systems."
      />
      <DrivePicker
        drives={drives}
        selectedDriveRoot={selectedDriveRoot}
        onSelect={selectDrive}
        isLoading={isLoading || isRunning}
        loadError={driveLoadError}
        onRefresh={refreshDrives}
      />
      <section className="section-surface">
        <div className="flex items-center gap-2.5">
          <RefreshCcw className="size-4 shrink-0 text-muted" aria-hidden />
          <span className="font-display text-sm font-semibold tracking-tight text-primary">Windows Drive Optimization</span>
        </div>
        <div className="mt-3 grid grid-cols-3 divide-x divide-border border-y border-border">
          <MetricTile label="Drive" value={selectedDrive?.rootPath ?? "None"} />
          <MetricTile label="Total size" value={selectedDrive?.totalSizeDisplay ?? "Unknown"} />
          <MetricTile label="Drive type" value={selectedDrive?.driveType ?? "Unknown"} />
        </div>
        <div className="mt-3 flex gap-2">
          <Button variant="secondary" onClick={() => void runOptimization("analyze")} disabled={!isAdministrator || !selectedDrive || isRunning}>
            <ScanLine data-icon="inline-start" />
            Analyze
          </Button>
          <Button onClick={() => void runOptimization("optimize")} disabled={!isAdministrator || !selectedDrive || isRunning}>
            <Wrench data-icon="inline-start" />
            Optimize / Trim
          </Button>
        </div>
      </section>
      <ToolOutput result={result} runningLabel={isRunning ? "Windows defrag is running with the selected mode." : undefined} errorMessage={optimizationError} />
    </MaintenanceFrame>
  )
}

export function CustomCleanPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<CleanupScanResult | null>(null)
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [activeGroup, setActiveGroup] = useState("All")
  const [safetyFilter, setSafetyFilter] = useState("all")
  const [sortKey, setSortKey] = useState<CleanupSortKey>("size")
  const [query, setQuery] = useState("")
  const [selectedItemId, setSelectedItemId] = useState("")
  const [ignoredIds, setIgnoredIds] = useState<string[]>([])
  const [pendingCleanupIds, setPendingCleanupIds] = useState<string[]>([])
  const [pageIndex, setPageIndex] = useState(0)
  const [showCleanupConfirmation, setShowCleanupConfirmation] = useState(false)
  const [skipCleanupConfirmation, setSkipCleanupConfirmation] = useStoredBoolean("filelocker.skipCustomCleanConfirmation")
  const [isScanning, setIsScanning] = useState(false)
  const [cleanupScanError, setCleanupScanError] = useState("")
  const [cleanupRunError, setCleanupRunError] = useState("")
  const [isCleaning, setIsCleaning] = useState(false)
  const [lastRun, setLastRun] = useState<CleanupRunResult | null>(null)
  const [savedSelectionLoaded, setSavedSelectionLoaded] = useState(false)
  const categories = scan?.categories ?? []
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds])
  const ignoredIdSet = useMemo(() => new Set(ignoredIds), [ignoredIds])
  const selectedCategories = useMemo(() => categories.filter((c) => selectedIdSet.has(c.id) && c.isEnabled), [categories, selectedIdSet])
  const cleanupNeedsAdministrator = selectedCategories.some((c) => c.requiresAdministrator)
  const canRunCleanup = selectedCategories.length > 0 && !isScanning && !isCleaning && (!cleanupNeedsAdministrator || isAdministrator)
  const visibleCategories = useMemo(
    () => sortCleanupCategories(
      filterCleanupCategories(categories, {
        group: activeGroup,
        safety: safetyFilter,
        query,
        ignoredIds: ignoredIdSet,
      }),
      sortKey
    ),
    [activeGroup, categories, ignoredIdSet, query, safetyFilter, sortKey]
  )
  const pageCount = Math.max(1, Math.ceil(visibleCategories.length / cleanupPageSize))
  const safePageIndex = Math.min(pageIndex, pageCount - 1)
  const pageStartIndex = safePageIndex * cleanupPageSize
  const pagedCategories = visibleCategories.slice(pageStartIndex, pageStartIndex + cleanupPageSize)
  const selectedItem = visibleCategories.find((category) => category.id === selectedItemId) ?? categories.find((category) => category.id === selectedItemId) ?? null
  const selectableCategories = useMemo(() => categories.filter((category) => category.isEnabled), [categories])
  const visibleSelectableCategories = useMemo(() => visibleCategories.filter((category) => category.isEnabled), [visibleCategories])
  const pageSelectableCategories = useMemo(() => pagedCategories.filter((category) => category.isEnabled), [pagedCategories])
  const allPageItemsSelected = pageSelectableCategories.length > 0 && pageSelectableCategories.every((category) => selectedIdSet.has(category.id))
  const allSelectableItemsSelected = selectableCategories.length > 0 && selectableCategories.every((category) => selectedIdSet.has(category.id))
  const cleanupCounts = useMemo(() => buildCleanupTabCounts(categories), [categories])
  const selectedSummary = useMemo(() => buildCleanupSelectionSummary(selectedCategories), [selectedCategories])
  const safetySummary = useMemo(() => buildCleanupSafetySummary(selectedCategories.length > 0 ? selectedCategories : categories), [categories, selectedCategories])
  const cleanupWarnings = useMemo(
    () => [...(cleanupScanError ? [cleanupScanError] : []), ...(cleanupRunError ? [cleanupRunError] : []), ...(lastRun?.warnings ?? [])],
    [cleanupRunError, cleanupScanError, lastRun]
  )
  const hasResults = scan !== null || cleanupScanError
  const pendingCleanupCount = pendingCleanupIds.length > 0 ? pendingCleanupIds.length : selectedCategories.length

  useEffect(() => {
    setPageIndex(0)
  }, [activeGroup, query, safetyFilter, sortKey])

  useEffect(() => {
    if (pageIndex > pageCount - 1) {
      setPageIndex(pageCount - 1)
    }
  }, [pageCount, pageIndex])

  useEffect(() => {
    if (selectedItemId && categories.some((category) => category.id === selectedItemId) && !ignoredIdSet.has(selectedItemId)) {
      return
    }

    setSelectedItemId(visibleCategories[0]?.id ?? "")
  }, [categories, ignoredIdSet, selectedItemId, visibleCategories])

  async function scanCleanup(options: { preserveLastRun?: boolean; clearSelection?: boolean } = {}) {
    if ((isScanning && scan !== null) || (isCleaning && !options.preserveLastRun)) return
    setIsScanning(true)
    setCleanupScanError("")
    setCleanupRunError("")
    setPendingCleanupIds([])
    setIgnoredIds([])
    if (!options.preserveLastRun) setLastRun(null)
    try {
      const response = await invoke<CleanupScanResult>("maintenance.scanCleanup", {})
      setScan(response)
      const saved = savedSelectionLoaded ? [] : readStoredStringArray("filelocker.customCleanSelection")
      if (!savedSelectionLoaded) setSavedSelectionLoaded(true)
      setSelectedItemId((current) => response.categories.some((c) => c.id === current) ? current : response.categories.find((c) => c.group !== "Advanced")?.id ?? response.categories[0]?.id ?? "")
      setSelectedIds((current) => {
        const enabled = new Set(response.categories.filter((c) => c.isEnabled).map((c) => c.id))
        if (options.clearSelection) return []
        if (current.length > 0) return current.filter((id) => enabled.has(id))
        if (saved.length > 0) return saved.filter((id) => enabled.has(id))
        return response.categories.filter((c) => c.defaultSelected && c.isEnabled).map((c) => c.id)
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : "Cleanup scan failed."
      setCleanupScanError(message)
      toast.error(message)
    } finally {
      setIsScanning(false)
    }
  }

  function updateSelectedIds(updater: (current: string[]) => string[]) {
    setSelectedIds((current) => {
      const next = updater(current)
      writeStoredStringArray("filelocker.customCleanSelection", next)
      return next
    })
  }

  function setCategorySelected(categoryId: string, selected: boolean) {
    if (isScanning || isCleaning) return
    updateSelectedIds((current) =>
      selected ? Array.from(new Set([...current, categoryId])) : current.filter((id) => id !== categoryId)
    )
  }

  function setCleanupItemsSelected(targetCategories: CleanupCategory[], selected: boolean) {
    if (isScanning || isCleaning) return
    const ids = targetCategories.filter((category) => category.isEnabled).map((category) => category.id)
    if (ids.length === 0) return
    const idSet = new Set(ids)
    updateSelectedIds((current) => selected ? Array.from(new Set([...current, ...ids])) : current.filter((id) => !idSet.has(id)))
  }

  function getCleanableCategoriesForIds(categoryIds: string[]) {
    const ids = new Set(categoryIds)
    return categories.filter((category) => ids.has(category.id) && category.isEnabled)
  }

  function requestCleanup(categoryIds = selectedIds) {
    if (isScanning || isCleaning) return
    const targetIds = Array.from(new Set(categoryIds)).filter(Boolean)
    const targetCategories = getCleanableCategoriesForIds(targetIds)
    if (targetCategories.length === 0) { toast.error("Select at least one cleanup item."); return }
    if (targetCategories.some((category) => category.requiresAdministrator) && !isAdministrator) { toast.error("Restart FileLocker as administrator before cleaning selected protected areas."); return }
    setPendingCleanupIds(targetCategories.map((category) => category.id))
    if (skipCleanupConfirmation) { void runCleanup(targetCategories.map((category) => category.id)); return }
    setShowCleanupConfirmation(true)
  }

  async function runCleanup(categoryIds = pendingCleanupIds.length > 0 ? pendingCleanupIds : selectedIds) {
    if (isScanning || isCleaning) return
    const targetCategories = getCleanableCategoriesForIds(categoryIds)
    if (targetCategories.length === 0) { toast.error("Select at least one cleanup item."); return }
    if (targetCategories.some((category) => category.requiresAdministrator) && !isAdministrator) { toast.error("Restart FileLocker as administrator before cleaning selected protected areas."); return }
    setShowCleanupConfirmation(false)
    setIsCleaning(true)
    setCleanupRunError("")
    try {
      const targetIds = targetCategories.map((category) => category.id)
      const response = await invoke<CleanupRunResult>("maintenance.runCleanup", { categoryIds: targetIds, confirmation: "CLEAN SELECTED" })
      setLastRun(response)
      setCleanupRunError("")
      updateSelectedIds(() => [])
      setPendingCleanupIds([])
      await scanCleanup({ preserveLastRun: true, clearSelection: true })
      if (response.warnings.length > 0 || response.skippedItems > 0) {
        toast.warning(`Cleaned ${response.freedDisplay}; ${response.skippedItems} item(s) skipped.`)
      } else {
        toast.success(`Cleaned ${response.freedDisplay}.`)
      }
    } catch (error) {
      setCleanupRunError(showMaintenanceError(error, "Cleanup failed."))
    } finally {
      setIsCleaning(false)
    }
  }

  async function revealCleanupLocation(category: CleanupCategory) {
    const path = category.path || category.locations[0] || ""
    if (!path) {
      toast.error("No cleanup location is available for this item.")
      return
    }

    try {
      await invoke("files.revealPath", { path })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to open cleanup location.")
    }
  }

  function ignoreCleanupItem(category: CleanupCategory) {
    setCategorySelected(category.id, false)
    setIgnoredIds((current) => Array.from(new Set([...current, category.id])))
    toast.message(`${category.label} ignored for this scan.`)
  }

  const visibleRangeStart = visibleCategories.length === 0 ? 0 : pageStartIndex + 1
  const visibleRangeEnd = Math.min(pageStartIndex + pagedCategories.length, visibleCategories.length)

  return (
    <MaintenanceFrame>
      {cleanupNeedsAdministrator && !isAdministrator ? (
        <AdminStatusBanner
          isAdministrator={isAdministrator}
          onRestartAsAdministrator={onRestartAsAdministrator}
          description="Some cleanup locations need administrator access. Restart as administrator to include protected Windows areas."
        />
      ) : null}

      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button variant="outline" onClick={() => setCleanupItemsSelected(selectableCategories, !allSelectableItemsSelected)} disabled={selectableCategories.length === 0 || isScanning || isCleaning}>
          <CheckCircle2 data-icon="inline-start" />
          {allSelectableItemsSelected ? "Clear All" : "Select All"}
        </Button>
        <Button variant="outline" onClick={() => void scanCleanup()} disabled={isScanning || isCleaning}>
          <RefreshCcw data-icon="inline-start" />
          {isScanning ? "Scanning..." : "Rescan"}
        </Button>
        <Button onClick={() => requestCleanup()} disabled={!canRunCleanup}>
          <Trash2 data-icon="inline-start" />
          {isCleaning ? "Cleaning..." : "Clean Selected"}
        </Button>
      </div>

      {!hasResults ? (
        <section className="rounded-md border border-border bg-transparent px-4 py-10">
          <div className="mx-auto flex max-w-md flex-col items-center text-center">
            <div className="flex size-11 items-center justify-center rounded-md border border-border-strong bg-accent/10 text-accent">
              <Trash2 className="size-5" aria-hidden />
            </div>
            <p className="mt-3 text-sm text-secondary">Run a scan to find temporary files, browser caches, app logs, privacy traces, and other safe cleanup opportunities.</p>
            <Button className="mt-4" onClick={() => void scanCleanup()} disabled={isScanning}>
              <ScanLine data-icon="inline-start" />
              {isScanning ? "Scanning..." : "Run Scan"}
            </Button>
          </div>
        </section>
      ) : (
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_330px]">
          <div className="min-w-0 space-y-4">
            <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-5">
              <CleanupSummaryCard label="Space Recoverable" value={cleanupScanError ? "Scan failed" : scan?.totalDisplay ?? "Unknown"} description="Estimated cleanup space" icon={<FolderOpen className="size-5" aria-hidden />} tone="blue" />
              <CleanupSummaryCard label="Items Found" value={scan ? String(scan.totalFiles) : "0"} description="Files and entries" icon={<Database className="size-5" aria-hidden />} tone="blue" />
              <CleanupSummaryCard label="Selected" value={String(selectedCategories.length)} description={selectedSummary.sizeDisplay} icon={<CheckCircle2 className="size-5" aria-hidden />} tone="amber" />
              <CleanupSummaryCard label="Last Cleaned" value={lastRun?.freedDisplay ?? "Not run"} description={lastRun ? `${lastRun.deletedFiles} file${lastRun.deletedFiles === 1 ? "" : "s"} removed` : "Never cleaned"} icon={<RefreshCcw className="size-5" aria-hidden />} tone="green" />
              <CleanupSummaryCard label="Safety / Risk" value={safetySummary.label} description={safetySummary.description} icon={<ShieldAlert className="size-5" aria-hidden />} tone={safetySummary.tone} />
            </div>

            {cleanupWarnings.length > 0 ? <WarningList warnings={cleanupWarnings} /> : null}

            <section className="overflow-hidden rounded-md border border-border bg-transparent">
              <div className="border-b border-border/70 px-4 py-3">
                <div className="flex flex-wrap gap-2">
                  {cleanupCategoryTabs.map((group) => (
                    <button
                      key={group}
                      type="button"
                      className={cn(
                        "h-8 rounded-md border px-3 text-sm font-medium transition-colors",
                        activeGroup === group ? "border-accent bg-accent text-[#08111f]" : "border-border/80 bg-bg-subtle/60 text-secondary hover:border-border-strong hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => setActiveGroup(group)}
                    >
                      {group} ({cleanupCounts[group] ?? 0})
                    </button>
                  ))}
                </div>

                <div className="mt-3 grid gap-2 xl:grid-cols-[minmax(220px,1fr)_180px_180px_220px]">
                  <div className="relative min-w-0">
                    <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" aria-hidden />
                    <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search cleanup items..." className="bg-bg-subtle/80 pl-9" disabled={isScanning || isCleaning} />
                  </div>
                  <AppManagerSelect value={activeGroup} onChange={setActiveGroup} ariaLabel="Filter cleanup category">
                    {cleanupCategoryTabs.map((group) => <option key={group} value={group}>{group}</option>)}
                  </AppManagerSelect>
                  <AppManagerSelect value={safetyFilter} onChange={setSafetyFilter} ariaLabel="Filter safety level">
                    <option value="all">All Safety Levels</option>
                    {(["Safe", "Review", "Advanced", "Risky", "Privacy"] as const).map((level) => <option key={level} value={level}>{level}</option>)}
                  </AppManagerSelect>
                  <AppManagerSelect value={sortKey} onChange={(value) => setSortKey(value as CleanupSortKey)} ariaLabel="Sort cleanup items">
                    <option value="size">Sort by: Size</option>
                    <option value="name">Sort by: Name</option>
                    <option value="category">Sort by: Category</option>
                    <option value="safety">Sort by: Safety</option>
                  </AppManagerSelect>
                </div>

                <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs text-muted">
                  <span>{selectedCategories.length} of {selectableCategories.length} selectable item{selectableCategories.length === 1 ? "" : "s"} selected</span>
                  <div className="flex flex-wrap items-center gap-2">
                    <Button variant="outline" size="sm" disabled={selectableCategories.length === 0 || isScanning || isCleaning} onClick={() => setCleanupItemsSelected(selectableCategories, true)}>Select All</Button>
                    <Button variant="outline" size="sm" disabled={visibleSelectableCategories.length === 0 || isScanning || isCleaning} onClick={() => setCleanupItemsSelected(visibleSelectableCategories, true)}>Select Filtered</Button>
                    <Button variant="outline" size="sm" disabled={selectedCategories.length === 0 || isScanning || isCleaning} onClick={() => updateSelectedIds(() => [])}>Clear Selection</Button>
                  </div>
                </div>
              </div>

              <div className="overflow-x-auto">
                <div className="min-w-[980px]">
                  <div className="grid grid-cols-[36px_minmax(220px,1.1fr)_minmax(260px,1.2fr)_130px_92px_110px_112px_70px] gap-3 border-b border-border/70 bg-bg-subtle/65 px-4 py-2 text-xs font-medium text-muted">
                    <Checkbox checked={allPageItemsSelected} disabled={pageSelectableCategories.length === 0 || isScanning || isCleaning} aria-label="Select all cleanup items on this page" onCheckedChange={(checked) => setCleanupItemsSelected(pageSelectableCategories, checked === true)} />
                    <span>Item</span>
                    <span>Description</span>
                    <span>Category</span>
                    <span>Files</span>
                    <span>Size</span>
                    <span>Safety</span>
                    <span>Details</span>
                  </div>

                  {pagedCategories.map((category) => {
                    const selected = selectedIdSet.has(category.id)
                    const rowSelected = selectedItem?.id === category.id
                    return (
                      <div
                        key={category.id}
                        role="button"
                        tabIndex={0}
                        className={cn(
                          "grid min-h-[58px] grid-cols-[36px_minmax(220px,1.1fr)_minmax(260px,1.2fr)_130px_92px_110px_112px_70px] items-center gap-3 border-b border-border/60 px-4 py-2.5 transition-colors last:border-b-0 hover:bg-bg-surface-hover/55",
                          rowSelected && "bg-accent/8 ring-1 ring-inset ring-accent/45",
                          !category.isEnabled && "opacity-70"
                        )}
                        onClick={() => setSelectedItemId(category.id)}
                        onKeyDown={(event) => {
                          if (event.key === "Enter" || event.key === " ") {
                            event.preventDefault()
                            setSelectedItemId(category.id)
                          }
                        }}
                      >
                        <div onClick={(event) => event.stopPropagation()}>
                          <Checkbox checked={selected} disabled={!category.isEnabled || isScanning || isCleaning} aria-label={`Select ${category.label}`} onCheckedChange={(checked) => setCategorySelected(category.id, checked === true)} />
                        </div>
                        <div className="min-w-0">
                          <div className="truncate text-sm font-semibold text-primary">{category.label}</div>
                          <div className={cn("mt-0.5 truncate text-xs", category.isEnabled ? "text-secondary" : "text-muted")}>{getCleanupStatusText(category)}</div>
                        </div>
                        <div className="min-w-0 truncate text-sm text-secondary">{category.description}</div>
                        <div className="text-sm text-secondary">{category.group}</div>
                        <div className="text-sm font-medium text-primary">{category.sizeKnown ? category.fileCount.toLocaleString() : "—"}</div>
                        <div className="text-sm font-semibold text-primary">{formatCleanupSize(category)}</div>
                        <CleanupSafetyBadge level={category.safetyLevel} />
                        <div className="flex justify-end">
                          <Button variant="outline" size="icon-sm" aria-label={`Show details for ${category.label}`} title="Details" onClick={(event) => { event.stopPropagation(); setSelectedItemId(category.id) }}>
                            <MoreVertical className="size-4" aria-hidden />
                          </Button>
                        </div>
                      </div>
                    )
                  })}

                  {visibleCategories.length === 0 ? (
                    <div className="px-4 py-5 text-sm text-secondary">
                      {cleanupScanError ? "Cleanup scan failed. Review the warning and try again." : isScanning ? "Scanning cleanup items..." : categories.length === 0 ? "No cleanup items were returned by the scan." : "No cleanup items match these filters."}
                    </div>
                  ) : null}
                </div>
              </div>

              <div className="flex flex-col gap-3 border-t border-border/70 px-4 py-3 text-xs text-muted lg:flex-row lg:items-center lg:justify-between">
                <span>Showing {visibleRangeStart} to {visibleRangeEnd} of {visibleCategories.length} item{visibleCategories.length === 1 ? "" : "s"}</span>
                <div className="flex items-center gap-1.5">
                  <Button variant="outline" size="sm" disabled={safePageIndex === 0} onClick={() => setPageIndex((current) => Math.max(0, current - 1))}>Previous</Button>
                  {buildPaginationLabels(safePageIndex, pageCount).map((label) => (
                    <button
                      key={label}
                      type="button"
                      disabled={label === "..."}
                      className={cn(
                        "flex h-8 min-w-8 items-center justify-center rounded-md px-2 text-sm font-medium transition-colors disabled:cursor-default",
                        label === String(safePageIndex + 1) ? "bg-accent text-[#08111f]" : label === "..." ? "text-muted" : "text-secondary hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => label !== "..." && setPageIndex(Number(label) - 1)}
                    >
                      {label}
                    </button>
                  ))}
                  <Button variant="outline" size="sm" disabled={safePageIndex >= pageCount - 1} onClick={() => setPageIndex((current) => Math.min(pageCount - 1, current + 1))}>Next</Button>
                </div>
              </div>
            </section>

            <div className="rounded-md border border-border bg-bg-surface/35 px-3 py-2 text-sm text-secondary">
              <div className="flex items-start gap-2.5">
                <Info className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden />
                <div>Regular cleaning helps maintain system performance. Privacy, developer, and advanced items are left unselected until you review them.</div>
              </div>
            </div>
          </div>

          <CleanupItemDetailsPanel
            category={selectedItem}
            selected={Boolean(selectedItem && selectedIdSet.has(selectedItem.id))}
            isBusy={isScanning || isCleaning}
            isAdministrator={isAdministrator}
            onClean={() => selectedItem && requestCleanup([selectedItem.id])}
            onSelect={(selected) => selectedItem && setCategorySelected(selectedItem.id, selected)}
            onReveal={() => selectedItem && void revealCleanupLocation(selectedItem)}
            onIgnore={() => selectedItem && ignoreCleanupItem(selectedItem)}
          />
        </div>
      )}

      <MaintenanceConfirmDialog
        open={showCleanupConfirmation}
        onOpenChange={setShowCleanupConfirmation}
        title="Clean selected areas?"
        description={`FileLocker will delete files from ${pendingCleanupCount} selected cleanup item${pendingCleanupCount === 1 ? "" : "s"}. Locked, missing, or unavailable files are skipped and reported.`}
        confirmLabel="Clean Selected"
        onConfirm={() => void runCleanup()}
        onDontShowAgain={() => { setSkipCleanupConfirmation(true); void runCleanup() }}
        isBusy={isCleaning}
      />
    </MaintenanceFrame>
  )
}

function CleanupItemDetailsPanel({
  category,
  selected,
  isBusy,
  isAdministrator,
  onClean,
  onSelect,
  onReveal,
  onIgnore,
}: {
  category: CleanupCategory | null
  selected: boolean
  isBusy: boolean
  isAdministrator: boolean
  onClean: () => void
  onSelect: (selected: boolean) => void
  onReveal: () => void
  onIgnore: () => void
}) {
  const adminBlocked = Boolean(category?.requiresAdministrator && !isAdministrator)

  return (
    <aside className="min-w-0 overflow-hidden rounded-md border border-border bg-transparent xl:sticky xl:top-4 xl:self-start">
      <div className="flex items-center justify-between gap-3 border-b border-border/70 px-4 py-3">
        <div className="font-display text-sm font-semibold tracking-tight text-primary">Item Details</div>
        <Info className="size-4 text-muted" aria-hidden />
      </div>

      {!category ? (
        <div className="px-4 py-8 text-sm text-secondary">Select a cleanup item to see what FileLocker found and what will be removed.</div>
      ) : (
        <>
          <div className="border-b border-border/70 px-4 py-4">
            <div className="flex items-center gap-3">
              <div className={cn("flex size-9 shrink-0 items-center justify-center rounded-md border", getCleanupSafetyMarkClass(category.safetyLevel))} aria-hidden>
                <FolderOpen className="size-4" />
              </div>
              <div className="min-w-0">
                <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{category.label}</div>
                <div className="mt-1 flex flex-wrap items-center gap-2">
                  <CleanupSafetyBadge level={category.safetyLevel} />
                  <span className="text-xs text-secondary">{category.group}</span>
                </div>
              </div>
            </div>
            <p className="mt-3 text-sm leading-snug text-secondary">{category.description}</p>
          </div>

          <div className="grid grid-cols-3 divide-x divide-border border-b border-border">
            <MetricTile label="Files" value={category.sizeKnown ? category.fileCount.toLocaleString() : "—"} />
            <MetricTile label="Size" value={formatCleanupSize(category)} />
            <MetricTile label="Locations" value={String(category.locations.length)} />
          </div>

          <div className="divide-y divide-border/65 px-4">
            <CleanupDetailList label="Locations Scanned" values={category.locations.length > 0 ? category.locations : [category.unavailableReason || "No cleanup location is available."]} mono />
            <CleanupDetailList label="What Will Be Removed" values={category.removes.length > 0 ? category.removes : ["Files matching this cleanup item."]} />
            <CleanupDetailList label="What Will NOT Be Removed" values={category.keeps.length > 0 ? category.keeps : ["Personal files outside this cleanup location."]} />
            <InstalledAppDetailRow label="Recommendation" value={category.unavailableReason || category.recommendation || getCleanupRecommendation(category)} />
          </div>

          <div className="border-t border-border/70 px-4 py-3">
            <div className="grid gap-2">
              <Button onClick={onClean} disabled={!category.isEnabled || isBusy || adminBlocked}>
                <Trash2 data-icon="inline-start" />
                Clean This Item
              </Button>
              <Button variant="outline" onClick={onReveal} disabled={!category.path || isBusy || !category.isEnabled}>
                <FolderOpen data-icon="inline-start" />
                View Files
              </Button>
              <Button variant="outline" onClick={() => onSelect(!selected)} disabled={!category.isEnabled || isBusy}>
                <CheckCircle2 data-icon="inline-start" />
                {selected ? "Remove from Selection" : "Select Item"}
              </Button>
              <Button variant="outline" onClick={onIgnore} disabled={isBusy}>
                <MoreVertical data-icon="inline-start" />
                Ignore Item
              </Button>
            </div>

            {adminBlocked ? (
              <div className="app-inline-notice app-inline-notice-warning mt-3">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <span>Administrator mode is required to clean this protected location.</span>
              </div>
            ) : !category.isEnabled ? (
              <div className="mt-3 rounded-md border border-border bg-bg-subtle/65 px-3 py-3 text-sm text-secondary">
                <div className="font-display text-sm font-semibold tracking-tight text-primary">{category.status}</div>
                <div className="mt-1">{category.unavailableReason || "This item has no files to clean right now."}</div>
              </div>
            ) : (
              <div className={cn("mt-3 rounded-md border px-3 py-3 text-sm text-secondary", getCleanupSafetyNoticeClass(category.safetyLevel))}>
                <div className="font-display text-sm font-semibold tracking-tight">{getCleanupSafetyNoticeTitle(category.safetyLevel)}</div>
                <div className="mt-1">{category.recommendation || getCleanupRecommendation(category)}</div>
              </div>
            )}
          </div>
        </>
      )}
    </aside>
  )
}

function CleanupSummaryCard({ label, value, description, icon, tone }: { label: string; value: string; description: string; icon: React.ReactNode; tone: "blue" | "green" | "amber" | "red" }) {
  return (
    <div className="rounded-md border border-border bg-transparent px-4 py-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-sm font-medium text-secondary">{label}</div>
          <div className="mt-1 truncate font-display text-2xl font-semibold tracking-tight text-primary">{value}</div>
          <div className="mt-1 truncate text-sm text-secondary">{description}</div>
        </div>
        <div className={cn("flex size-11 shrink-0 items-center justify-center rounded-md border", getCleanupSummaryToneClass(tone))}>
          {icon}
        </div>
      </div>
    </div>
  )
}

function CleanupDetailList({ label, values, mono = false }: { label: string; values: string[]; mono?: boolean }) {
  const visible = values.filter(Boolean).slice(0, 5)
  return (
    <div className="grid gap-2 py-3">
      <div className="text-xs text-muted">{label}</div>
      <div className="grid gap-1.5">
        {visible.map((value) => (
          <div key={value} className={cn("min-w-0 break-words text-sm text-secondary", mono && "break-all font-mono text-xs")}>{value}</div>
        ))}
        {values.length > visible.length ? <div className="text-xs text-muted">+ {values.length - visible.length} more</div> : null}
      </div>
    </div>
  )
}

function CleanupSafetyBadge({ level }: { level: string }) {
  return (
    <Badge variant="outline" className={cn("h-6 w-fit justify-center px-2 text-xs", getCleanupSafetyBadgeClass(level))}>
      {level || "Review"}
    </Badge>
  )
}

function filterCleanupCategories(categories: CleanupCategory[], filters: { group: string; safety: string; query: string; ignoredIds: Set<string> }) {
  const term = filters.query.trim().toLowerCase()
  return categories
    .filter((category) => !filters.ignoredIds.has(category.id))
    .filter((category) => filters.group === "All" ? category.group !== "Advanced" : category.group === filters.group)
    .filter((category) => filters.safety === "all" || category.safetyLevel === filters.safety)
    .filter((category) => {
      if (!term) return true
      return [
        category.label,
        category.description,
        category.group,
        category.safetyLevel,
        category.status,
        category.unavailableReason,
        ...category.locations,
      ].some((value) => value.toLowerCase().includes(term))
    })
}

function sortCleanupCategories(categories: CleanupCategory[], sortKey: CleanupSortKey) {
  return [...categories].sort((a, b) => {
    if (sortKey === "size") return b.sizeBytes - a.sizeBytes || a.label.localeCompare(b.label)
    if (sortKey === "category") return a.group.localeCompare(b.group) || a.label.localeCompare(b.label)
    if (sortKey === "safety") return getCleanupSafetyRank(a.safetyLevel) - getCleanupSafetyRank(b.safetyLevel) || a.label.localeCompare(b.label)
    return a.label.localeCompare(b.label)
  })
}

function buildCleanupTabCounts(categories: CleanupCategory[]) {
  const counts: Record<string, number> = { All: categories.filter((category) => category.group !== "Advanced").length }
  for (const group of cleanupCategoryTabs) {
    if (group === "All") continue
    counts[group] = categories.filter((category) => category.group === group).length
  }
  return counts
}

function buildCleanupSelectionSummary(categories: CleanupCategory[]) {
  const totalBytes = categories.reduce((sum, category) => sum + (category.sizeKnown ? category.sizeBytes : 0), 0)
  return { sizeDisplay: categories.length === 0 ? "No items selected" : `${formatBytes(totalBytes)} selected` }
}

function buildCleanupSafetySummary(categories: CleanupCategory[]) {
  if (categories.some((category) => /risky/i.test(category.safetyLevel))) return { label: "Risky", description: "Risky item selected", tone: "red" as const }
  if (categories.some((category) => /advanced/i.test(category.safetyLevel))) return { label: "Advanced", description: "Advanced review needed", tone: "amber" as const }
  if (categories.some((category) => /review|privacy/i.test(category.safetyLevel))) return { label: "Review", description: "Some items need review", tone: "amber" as const }
  return { label: "Safe", description: "No system-critical issues", tone: "green" as const }
}

function formatCleanupSize(category: CleanupCategory) {
  return category.sizeKnown ? category.sizeDisplay : "Unknown size"
}

function getCleanupStatusText(category: CleanupCategory) {
  if (!category.isEnabled && category.status === "Not found") return "Not found"
  if (!category.isEnabled && category.status === "Unavailable") return "Unavailable"
  return category.status
}

function getCleanupSafetyRank(level: string) {
  if (/safe/i.test(level)) return 0
  if (/privacy/i.test(level)) return 1
  if (/review/i.test(level)) return 2
  if (/advanced/i.test(level)) return 3
  return 4
}

function getCleanupSafetyBadgeClass(level: string) {
  if (/safe/i.test(level)) return "border-accent-green/35 bg-accent-green/10 text-accent-green"
  if (/privacy/i.test(level)) return "border-accent/35 bg-accent/12 text-accent"
  if (/advanced|review/i.test(level)) return "border-amber-400/45 bg-amber-400/12 text-amber-200"
  return "border-red-400/45 bg-red-500/14 text-red-200"
}

function getCleanupSafetyMarkClass(level: string) {
  if (/safe/i.test(level)) return "border-accent-green/35 bg-accent-green/10 text-accent-green"
  if (/privacy/i.test(level)) return "border-accent/35 bg-accent/12 text-accent"
  if (/advanced|review/i.test(level)) return "border-amber-400/35 bg-amber-400/12 text-amber-200"
  return "border-red-400/35 bg-red-500/12 text-red-200"
}

function getCleanupSummaryToneClass(tone: "blue" | "green" | "amber" | "red") {
  if (tone === "green") return "border-accent-green/30 bg-accent-green/10 text-accent-green"
  if (tone === "amber") return "border-amber-400/35 bg-amber-400/12 text-amber-200"
  if (tone === "red") return "border-red-400/35 bg-red-500/12 text-red-200"
  return "border-accent/30 bg-accent/12 text-accent"
}

function getCleanupSafetyNoticeClass(level: string) {
  if (/safe/i.test(level)) return "border-accent-green/25 bg-accent-green/10"
  if (/privacy|review|advanced/i.test(level)) return "border-amber-400/25 bg-amber-400/10"
  return "border-red-400/25 bg-red-500/10"
}

function getCleanupSafetyNoticeTitle(level: string) {
  if (/safe/i.test(level)) return "Safe to clean"
  if (/privacy/i.test(level)) return "Privacy cleanup"
  if (/advanced/i.test(level)) return "Advanced item"
  if (/review/i.test(level)) return "Review before cleaning"
  return "Risky cleanup"
}

function getCleanupRecommendation(category: CleanupCategory) {
  if (!category.isEnabled) return category.unavailableReason || "No cleanup is available for this item right now."
  if (/safe/i.test(category.safetyLevel)) return "Safe to clean regularly when related apps are closed."
  if (/privacy/i.test(category.safetyLevel)) return "Use when you want to remove local history or traces."
  if (/advanced|risky/i.test(category.safetyLevel)) return "Clean only after you understand the impact."
  return "Review this item before cleaning."
}

export function RegistryFixerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<RegistryScanResult | null>(null)
  const [registryScanError, setRegistryScanError] = useState("")
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [pendingFixIds, setPendingFixIds] = useState<string[]>([])
  const [showRegistryConfirmation, setShowRegistryConfirmation] = useState(false)
  const [skipRegistryConfirmation, setSkipRegistryConfirmation] = useStoredBoolean("filelocker.skipRegistryFixConfirmation")
  const [isScanning, setIsScanning] = useState(false)
  const [isFixing, setIsFixing] = useState(false)
  const [lastClean, setLastClean] = useState<RegistryCleanResult | null>(null)
  const [query, setQuery] = useState("")
  const [issueTab, setIssueTab] = useState("all")
  const [severityFilter, setSeverityFilter] = useState("all")
  const [sortKey, setSortKey] = useState<RegistrySortKey>("severity")
  const [issuePageIndex, setIssuePageIndex] = useState(0)
  const [selectedIssueId, setSelectedIssueId] = useState("")
  const [lastScanTime, setLastScanTime] = useState("")
  const issues = scan?.issues ?? []
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds])
  const selectedIssues = useMemo(() => issues.filter((issue) => selectedIdSet.has(issue.id)), [issues, selectedIdSet])
  const registryNeedsAdministrator = selectedIssues.some((issue) => issue.hive === "HKLM")
  const canFix = selectedIssues.length > 0 && !isScanning && !isFixing && (!registryNeedsAdministrator || isAdministrator)
  const issueCounts = useMemo(() => buildRegistryIssueCounts(issues), [issues])
  const visibleIssues = useMemo(
    () => sortRegistryIssues(filterRegistryIssues(issues, { query, category: issueTab, severity: severityFilter }), sortKey),
    [issueTab, issues, query, severityFilter, sortKey]
  )
  const issuePageCount = Math.max(1, Math.ceil(visibleIssues.length / registryIssuePageSize))
  const safeIssuePageIndex = Math.min(issuePageIndex, issuePageCount - 1)
  const pageStartIndex = safeIssuePageIndex * registryIssuePageSize
  const pagedIssues = visibleIssues.slice(pageStartIndex, pageStartIndex + registryIssuePageSize)
  const selectedIssue = visibleIssues.find((issue) => issue.id === selectedIssueId) ?? pagedIssues[0] ?? issues[0] ?? null
  const highIssues = issues.filter((issue) => getRegistrySeverityTone(issue.severity) === "high").length
  const mediumIssues = issues.filter((issue) => getRegistrySeverityTone(issue.severity) === "medium").length
  const visibleRangeStart = visibleIssues.length === 0 ? 0 : pageStartIndex + 1
  const visibleRangeEnd = Math.min(pageStartIndex + pagedIssues.length, visibleIssues.length)
  const registryWarnings = useMemo(
    () => [
      ...(registryScanError ? [registryScanError] : []),
      ...(scan?.warnings ?? []),
      ...(lastClean?.failures.map((f) => `${f.displayName}: ${f.message}`) ?? []),
    ],
    [lastClean, registryScanError, scan]
  )
  const hasResults = scan !== null || registryScanError

  useEffect(() => {
    setIssuePageIndex(0)
  }, [issueTab, query, severityFilter, sortKey])

  useEffect(() => {
    if (issuePageIndex > issuePageCount - 1) {
      setIssuePageIndex(issuePageCount - 1)
    }
  }, [issuePageCount, issuePageIndex])

  useEffect(() => {
    if (!selectedIssueId || !issues.some((issue) => issue.id === selectedIssueId)) {
      setSelectedIssueId(issues[0]?.id ?? "")
    }
  }, [issues, selectedIssueId])

  async function scanRegistry() {
    if ((isScanning && scan !== null) || isFixing) return
    setIsScanning(true)
    setRegistryScanError("")
    setShowRegistryConfirmation(false)
    setPendingFixIds([])
    setLastClean(null)
    try {
      const response = await invoke<RegistryScanResult>("maintenance.scanRegistry", {})
      setScan(response)
      setLastScanTime(formatRegistryScanTime(new Date()))
      setIssuePageIndex(0)
      setSelectedIssueId((current) => response.issues.some((issue) => issue.id === current) ? current : response.issues[0]?.id ?? "")
      setSelectedIds((current) => {
        const cleanableIds = response.issues.filter((i) => i.canClean).map((i) => i.id)
        const available = new Set(cleanableIds)
        return current.filter((id) => available.has(id))
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : "Registry scan failed."
      setRegistryScanError(message)
      toast.error(message)
    } finally {
      setIsScanning(false)
    }
  }

  function getIssuesForIds(issueIds: string[]) {
    const ids = new Set(issueIds)
    return issues.filter((issue) => ids.has(issue.id) && issue.canClean)
  }

  function requestFixRegistry(issueIds = selectedIds) {
    if (isScanning || isFixing) return
    const targetIds = Array.from(new Set(issueIds)).filter(Boolean)
    const targetIssues = getIssuesForIds(targetIds)
    if (targetIssues.length === 0) { toast.error("Select at least one registry issue."); return }
    if (targetIssues.some((issue) => issue.hive === "HKLM") && !isAdministrator) { toast.error("Restart FileLocker as administrator before fixing selected registry issues."); return }
    setPendingFixIds(targetIds)
    if (skipRegistryConfirmation) { void fixRegistry(targetIds); return }
    setShowRegistryConfirmation(true)
  }

  async function fixRegistry(issueIds = pendingFixIds.length > 0 ? pendingFixIds : selectedIds) {
    if (isScanning || isFixing) return
    const targetIds = Array.from(new Set(issueIds)).filter(Boolean)
    const targetIssues = getIssuesForIds(targetIds)
    if (targetIssues.length === 0) { toast.error("Select at least one registry issue."); return }
    if (targetIssues.some((issue) => issue.hive === "HKLM") && !isAdministrator) { toast.error("Restart FileLocker as administrator before fixing selected registry issues."); return }
    setShowRegistryConfirmation(false)
    setIsFixing(true)
    try {
      const response = await invoke<RegistryCleanResult>("maintenance.cleanRegistry", { issueIds: targetIds, confirmation: "FIX REGISTRY" })
      setLastClean(response)
      setScan(response.scan)
      setLastScanTime(formatRegistryScanTime(new Date()))
      setSelectedIds([])
      setPendingFixIds([])
      setSelectedIssueId((current) => response.scan.issues.some((issue) => issue.id === current) ? current : response.scan.issues[0]?.id ?? "")
      toast[response.failedCount > 0 ? "error" : "success"](
        response.failedCount > 0
          ? `Fixed ${response.cleanedCount} issue(s); ${response.failedCount} failed.`
          : `Fixed ${response.cleanedCount} registry issue(s).`
      )
    } catch (error) {
      showMaintenanceError(error, "Registry cleanup failed.")
    } finally {
      setIsFixing(false)
    }
  }

  function setIssueSelected(issueId: string, selected: boolean) {
    if (isScanning || isFixing) return
    setSelectedIds((current) =>
      selected ? Array.from(new Set([...current, issueId])) : current.filter((id) => id !== issueId)
    )
  }

  async function copyIssueDetails(issue: RegistryIssue) {
    try {
      await navigator.clipboard.writeText(formatRegistryIssueDetails(issue))
      toast.success("Registry issue details copied.")
    } catch {
      toast.error("Registry issue details could not be copied.")
    }
  }

  const pendingFixCount = pendingFixIds.length > 0 ? pendingFixIds.length : selectedIds.length

  return (
    <MaintenanceFrame>
      {registryNeedsAdministrator && !isAdministrator ? (
        <AdminStatusBanner
          isAdministrator={isAdministrator}
          onRestartAsAdministrator={onRestartAsAdministrator}
          description="Local-machine registry cleanup needs administrator mode. FileLocker creates a backup before any fix."
        />
      ) : null}

      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button variant="outline" onClick={() => void scanRegistry()} disabled={isScanning || isFixing}>
          <RefreshCcw data-icon="inline-start" />
          {isScanning ? "Scanning..." : "Rescan"}
        </Button>
        <Button onClick={() => requestFixRegistry()} disabled={!canFix}>
          <Wrench data-icon="inline-start" />
          {isFixing ? "Fixing..." : "Fix Selected Issues"}
        </Button>
      </div>

      {!hasResults ? (
        <section className="rounded-md border border-border bg-transparent px-4 py-10">
          <div className="mx-auto flex max-w-md flex-col items-center text-center">
            <div className="flex size-11 items-center justify-center rounded-md border border-border-strong bg-accent/10 text-accent">
              <Database className="size-5" aria-hidden />
            </div>
            <p className="mt-3 text-sm text-secondary">Scan for invalid file references, uninstall entries, COM servers, file associations, app paths, and shared DLL records.</p>
            <Button className="mt-4" onClick={() => void scanRegistry()} disabled={isScanning}>
              <ScanLine data-icon="inline-start" />
              {isScanning ? "Scanning..." : "Run Scan"}
            </Button>
          </div>
        </section>
      ) : (
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_330px]">
          <div className="min-w-0 space-y-4">
            <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-4">
              <InstalledAppSummaryCard label="Issues Found" value={registryScanError ? "Failed" : String(scan?.issueCount ?? 0)} description="Registry issues detected" icon={<AlertTriangle className="size-5" aria-hidden />} tone="blue" />
              <InstalledAppSummaryCard label="Selected for Fix" value={String(selectedIssues.length)} description={selectedIssues.length === 1 ? "One issue selected" : "Issues selected"} icon={<Wrench className="size-5" aria-hidden />} tone="blue" />
              <InstalledAppSummaryCard label="Last Scan" value={lastScanTime || "Not run"} description={isScanning ? "Scanning registry" : "Quick scan"} icon={<ScanLine className="size-5" aria-hidden />} tone="blue" />
              <InstalledAppSummaryCard label="Registry Health" value={getRegistryHealthLabel(highIssues, mediumIssues)} description={highIssues > 0 ? `${highIssues} high severity issue${highIssues === 1 ? "" : "s"}` : "No critical issues found"} icon={<CheckCircle2 className="size-5" aria-hidden />} tone={highIssues > 0 ? "blue" : "green"} />
            </div>

            {registryWarnings.length > 0 ? <WarningList warnings={registryWarnings} /> : null}
            {lastClean?.backupPath ? (
              <div className="rounded-md border border-accent-green/25 bg-accent-green/10 px-3 py-3 text-sm text-secondary">
                <div className="flex items-start gap-2.5">
                  <ShieldAlert className="mt-0.5 size-4 shrink-0 text-accent-green" aria-hidden />
                  <div className="min-w-0">
                    <div className="font-display text-sm font-semibold tracking-tight text-accent-green">Registry backup created</div>
                    <div className="mt-1 truncate font-mono text-xs">{lastClean.backupPath}</div>
                  </div>
                </div>
              </div>
            ) : null}

            <section className="overflow-hidden rounded-md border border-border bg-transparent">
              <div className="border-b border-border/70 px-4 py-3">
                <div className="flex flex-wrap gap-2">
                  {registryIssueTabs.map((tab) => (
                    <button
                      key={tab.key}
                      type="button"
                      className={cn(
                        "h-8 rounded-md border px-3 text-sm font-medium transition-colors",
                        issueTab === tab.key ? "border-accent bg-accent text-[#08111f]" : "border-border/80 bg-bg-subtle/60 text-secondary hover:border-border-strong hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => setIssueTab(tab.key)}
                    >
                      {tab.label} ({tab.key === "all" ? issues.length : issueCounts[tab.key] ?? 0})
                    </button>
                  ))}
                </div>

                <div className="mt-3 grid gap-2 xl:grid-cols-[minmax(220px,1fr)_220px_220px]">
                  <div className="relative min-w-0">
                    <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" aria-hidden />
                    <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search issues..." className="bg-bg-subtle/80 pl-9" disabled={isScanning || isFixing} />
                  </div>
                  <AppManagerSelect value={severityFilter} onChange={setSeverityFilter} ariaLabel="Filter registry severity">
                    <option value="all">All Severity</option>
                    <option value="High">High Severity</option>
                    <option value="Medium">Medium Severity</option>
                    <option value="Low">Low Severity</option>
                  </AppManagerSelect>
                  <AppManagerSelect value={sortKey} onChange={(value) => setSortKey(value as RegistrySortKey)} ariaLabel="Sort registry issues">
                    <option value="severity">Sort by: Severity</option>
                    <option value="category">Sort by: Category</option>
                    <option value="issue">Sort by: Issue</option>
                    <option value="key">Sort by: Registry Key</option>
                  </AppManagerSelect>
                </div>
              </div>

              <div className="overflow-x-auto">
                <div className="min-w-[980px]">
                  <div className="grid grid-cols-[36px_minmax(240px,1.15fr)_minmax(260px,1fr)_110px_minmax(180px,.8fr)_120px] gap-3 border-b border-border/70 bg-bg-subtle/65 px-4 py-2 text-xs font-medium text-muted">
                    <span />
                    <span>Issue</span>
                    <span>Registry Key</span>
                    <span>Severity</span>
                    <span>Data</span>
                    <span>Action</span>
                  </div>

                  {pagedIssues.map((issue) => {
                    const selected = selectedIdSet.has(issue.id)
                    const issueSelected = selectedIssue?.id === issue.id
                    return (
                      <div
                        key={issue.id}
                        role="button"
                        tabIndex={0}
                        className={cn(
                          "grid min-h-[58px] grid-cols-[36px_minmax(240px,1.15fr)_minmax(260px,1fr)_110px_minmax(180px,.8fr)_120px] items-center gap-3 border-b border-border/60 px-4 py-2.5 transition-colors last:border-b-0 hover:bg-bg-surface-hover/55",
                          issueSelected && "bg-accent/8 ring-1 ring-inset ring-accent/50"
                        )}
                        onClick={() => setSelectedIssueId(issue.id)}
                        onKeyDown={(event) => {
                          if (event.key === "Enter" || event.key === " ") {
                            event.preventDefault()
                            setSelectedIssueId(issue.id)
                          }
                        }}
                      >
                        <div onClick={(event) => event.stopPropagation()}>
                          <Checkbox checked={selected} disabled={!issue.canClean || isScanning || isFixing} aria-label={`Select ${issue.kind}`} onCheckedChange={(checked) => setIssueSelected(issue.id, checked === true)} />
                        </div>
                        <div className="flex min-w-0 items-center gap-3">
                          <RegistryIssueMark issue={issue} />
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-primary">{issue.kind}</div>
                            <div className="mt-0.5 truncate text-xs text-secondary">{issue.displayName}</div>
                          </div>
                        </div>
                        <div className="min-w-0 truncate font-mono text-xs text-secondary">{formatRegistryIssueKey(issue)}</div>
                        <RegistrySeverityBadge severity={issue.severity} />
                        <div className="min-w-0 truncate text-sm text-secondary">{getRegistryIssueData(issue)}</div>
                        <div className="flex items-center gap-1.5" onClick={(event) => event.stopPropagation()}>
                          <Button variant="outline" size="sm" onClick={() => requestFixRegistry([issue.id])} disabled={!issue.canClean || isScanning || isFixing || (issue.hive === "HKLM" && !isAdministrator)}>Fix</Button>
                          <Button variant="outline" size="icon-sm" aria-label={`Show details for ${issue.kind}`} title="Details" onClick={() => setSelectedIssueId(issue.id)}>
                            <MoreVertical className="size-4" aria-hidden />
                          </Button>
                        </div>
                      </div>
                    )
                  })}

                  {visibleIssues.length === 0 ? (
                    <div className="px-4 py-5 text-sm text-secondary">
                      {registryScanError ? "Registry scan failed." : isScanning ? "Scanning registry..." : scan?.issueCount === 0 ? scan.status : "No registry issues match these filters."}
                    </div>
                  ) : null}
                </div>
              </div>

              <div className="flex flex-col gap-3 border-t border-border/70 px-4 py-3 text-xs text-muted lg:flex-row lg:items-center lg:justify-between">
                <span>Showing {visibleRangeStart} to {visibleRangeEnd} of {visibleIssues.length} issue{visibleIssues.length === 1 ? "" : "s"}</span>
                <div className="flex items-center gap-1.5">
                  <Button variant="outline" size="sm" disabled={safeIssuePageIndex === 0} onClick={() => setIssuePageIndex((current) => Math.max(0, current - 1))}>Previous</Button>
                  {buildPaginationLabels(safeIssuePageIndex, issuePageCount).map((label) => (
                    <button
                      key={label}
                      type="button"
                      disabled={label === "..."}
                      className={cn(
                        "flex h-8 min-w-8 items-center justify-center rounded-md px-2 text-sm font-medium transition-colors disabled:cursor-default",
                        label === String(safeIssuePageIndex + 1) ? "bg-accent text-[#08111f]" : label === "..." ? "text-muted" : "text-secondary hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => label !== "..." && setIssuePageIndex(Number(label) - 1)}
                    >
                      {label}
                    </button>
                  ))}
                  <Button variant="outline" size="sm" disabled={safeIssuePageIndex >= issuePageCount - 1} onClick={() => setIssuePageIndex((current) => Math.min(issuePageCount - 1, current + 1))}>Next</Button>
                </div>
              </div>
            </section>
          </div>

          <RegistryIssueDetailsPanel
            issue={selectedIssue}
            selected={Boolean(selectedIssue && selectedIdSet.has(selectedIssue.id))}
            isBusy={isScanning || isFixing}
            isAdministrator={isAdministrator}
            onFix={() => selectedIssue && requestFixRegistry([selectedIssue.id])}
            onSelect={(selected) => selectedIssue && setIssueSelected(selectedIssue.id, selected)}
            onCopy={() => selectedIssue && void copyIssueDetails(selectedIssue)}
          />
        </div>
      )}

      <MaintenanceConfirmDialog
        open={showRegistryConfirmation}
        onOpenChange={setShowRegistryConfirmation}
        title="Fix selected registry items?"
        description={`FileLocker will create a .reg backup first, then remove ${pendingFixCount} selected registry issue${pendingFixCount === 1 ? "" : "s"}.`}
        confirmLabel="Fix Selected"
        onConfirm={() => void fixRegistry()}
        onDontShowAgain={() => { setSkipRegistryConfirmation(true); void fixRegistry() }}
        isBusy={isFixing}
      />
    </MaintenanceFrame>
  )
}

function RegistryIssueDetailsPanel({
  issue,
  selected,
  isBusy,
  isAdministrator,
  onFix,
  onSelect,
  onCopy,
}: {
  issue: RegistryIssue | null
  selected: boolean
  isBusy: boolean
  isAdministrator: boolean
  onFix: () => void
  onSelect: (selected: boolean) => void
  onCopy: () => void
}) {
  const adminBlocked = Boolean(issue?.hive === "HKLM" && !isAdministrator)

  return (
    <aside className="min-w-0 overflow-hidden rounded-md border border-border bg-transparent xl:sticky xl:top-4 xl:self-start">
      <div className="flex items-center justify-between gap-3 border-b border-border/70 px-4 py-3">
        <div className="font-display text-sm font-semibold tracking-tight text-primary">Issue Details</div>
        <Info className="size-4 text-muted" aria-hidden />
      </div>

      {!issue ? (
        <div className="px-4 py-8 text-sm text-secondary">Select a registry issue to view its details.</div>
      ) : (
        <>
          <div className="border-b border-border/70 px-4 py-4">
            <div className="flex items-center gap-3">
              <RegistryIssueMark issue={issue} />
              <div className="min-w-0">
                <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{issue.kind}</div>
                <div className="mt-0.5 truncate text-xs text-secondary">{issue.displayName}</div>
              </div>
            </div>
          </div>

          <div className="divide-y divide-border/65 px-4">
            <div className="grid gap-1 py-3">
              <div className="text-xs text-muted">Severity</div>
              <RegistrySeverityBadge severity={issue.severity} />
            </div>
            <InstalledAppDetailRow label="Category" value={getRegistryIssueCategory(issue)} />
            <InstalledAppDetailRow label="Registry Key" value={formatRegistryIssueKey(issue)} mono />
            <InstalledAppDetailRow label="Data" value={getRegistryIssueData(issue)} mono />
            <InstalledAppDetailRow label="Problem" value={issue.reason} />
            <InstalledAppDetailRow label="Recommendation" value={getRegistryIssueRecommendation(issue)} />
          </div>

          <div className="border-t border-border/70 px-4 py-3">
            <div className="grid gap-2">
              <Button onClick={onFix} disabled={!issue.canClean || isBusy || adminBlocked}>
                <Wrench data-icon="inline-start" />
                Fix Issue
              </Button>
              <Button variant="outline" onClick={() => onSelect(!selected)} disabled={!issue.canClean || isBusy}>
                <CheckCircle2 data-icon="inline-start" />
                {selected ? "Remove from Selection" : "Select Issue"}
              </Button>
              <Button variant="outline" onClick={onCopy}>
                <Copy data-icon="inline-start" />
                Copy Details
              </Button>
            </div>

            {adminBlocked ? (
              <div className="app-inline-notice app-inline-notice-warning mt-3">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <span>Administrator mode is required to fix this local-machine entry.</span>
              </div>
            ) : (
              <div className="mt-3 rounded-md border border-accent-green/25 bg-accent-green/10 px-3 py-3 text-sm text-secondary">
                <div className="font-display text-sm font-semibold tracking-tight text-accent-green">Backup first</div>
                <div className="mt-1">FileLocker saves a .reg backup before removing this registry entry.</div>
              </div>
            )}
          </div>
        </>
      )}
    </aside>
  )
}

function RegistryIssueMark({ issue }: { issue: RegistryIssue }) {
  const category = getRegistryIssueCategory(issue)
  return (
    <div className={cn("flex size-8 shrink-0 items-center justify-center rounded-md border", getRegistryIssueMarkClass(issue))} aria-hidden>
      {category === "ActiveX / COM" ? <Database className="size-4" /> : category === "File Types" ? <FileJson className="size-4" /> : <AlertTriangle className="size-4" />}
    </div>
  )
}

function RegistrySeverityBadge({ severity }: { severity: string }) {
  const tone = getRegistrySeverityTone(severity)
  return (
    <Badge variant="outline" className={cn(
      "h-6 w-fit justify-center px-2 text-xs",
      tone === "high" && "border-red-400/45 bg-red-500/14 text-red-200",
      tone === "medium" && "border-amber-400/45 bg-amber-400/12 text-amber-200",
      tone === "low" && "border-accent-green/35 bg-accent-green/10 text-accent-green"
    )}>
      {severity || "Medium"}
    </Badge>
  )
}

function buildRegistryIssueCounts(issues: RegistryIssue[]) {
  return issues.reduce<Record<string, number>>((counts, issue) => {
    const category = getRegistryIssueCategory(issue)
    counts[category] = (counts[category] ?? 0) + 1
    return counts
  }, {})
}

function filterRegistryIssues(issues: RegistryIssue[], filters: { query: string; category: string; severity: string }) {
  const term = filters.query.trim().toLowerCase()
  return issues
    .filter((issue) => filters.category === "all" || getRegistryIssueCategory(issue) === filters.category)
    .filter((issue) => filters.severity === "all" || issue.severity.toLowerCase() === filters.severity.toLowerCase())
    .filter((issue) => {
      if (!term) return true
      return [
        issue.kind,
        issue.displayName,
        issue.hive,
        issue.keyPath,
        issue.valueName ?? "",
        issue.subKeyName ?? "",
        issue.targetPath,
        issue.reason,
        getRegistryIssueCategory(issue),
      ].some((value) => value.toLowerCase().includes(term))
    })
}

function sortRegistryIssues(issues: RegistryIssue[], sortKey: RegistrySortKey) {
  return [...issues].sort((a, b) => {
    if (sortKey === "severity") {
      const severity = getRegistrySeverityRank(a.severity) - getRegistrySeverityRank(b.severity)
      if (severity !== 0) return severity
    }
    if (sortKey === "category") {
      const category = getRegistryIssueCategory(a).localeCompare(getRegistryIssueCategory(b), undefined, { sensitivity: "base" })
      if (category !== 0) return category
    }
    if (sortKey === "key") {
      const key = formatRegistryIssueKey(a).localeCompare(formatRegistryIssueKey(b), undefined, { sensitivity: "base" })
      if (key !== 0) return key
    }
    return `${a.kind} ${a.displayName}`.localeCompare(`${b.kind} ${b.displayName}`, undefined, { sensitivity: "base" })
  })
}

function getRegistryIssueCategory(issue: RegistryIssue) {
  if (issue.category) return issue.category
  if (/activex|com/i.test(issue.kind)) return "ActiveX / COM"
  if (/extension|file type/i.test(issue.kind)) return "File Types"
  if (/application path|app path/i.test(issue.kind)) return "Application Paths"
  if (/startup/i.test(issue.kind) || /\\Run/i.test(issue.keyPath)) return "Startup"
  if (/uninstall/i.test(issue.kind) || /\\Uninstall/i.test(issue.keyPath)) return "Uninstall"
  return "Other"
}

function getRegistryIssueData(issue: RegistryIssue) {
  return issue.targetPath || issue.valueName || issue.subKeyName || issue.reason
}

function formatRegistryIssueKey(issue: RegistryIssue) {
  const basePath = `${issue.hive}\\${issue.keyPath}${issue.subKeyName ? `\\${issue.subKeyName}` : ""}`
  return issue.valueName != null ? `${basePath} : ${issue.valueName || "(Default)"}` : basePath
}

function getRegistryIssueRecommendation(issue: RegistryIssue) {
  if (/uninstall/i.test(issue.kind)) return "Remove this invalid uninstall registry entry after confirming the app is no longer installed."
  if (/activex|com/i.test(issue.kind)) return "Remove the stale COM server reference that points to the missing component."
  if (/application path/i.test(issue.kind)) return "Remove the invalid Application Paths entry."
  if (/extension/i.test(issue.kind)) return "Remove the unused file association entry."
  if (/shared dll/i.test(issue.kind)) return "Remove the missing SharedDLLs reference."
  if (/help/i.test(issue.kind)) return "Remove the stale help file reference."
  if (/orphaned/i.test(issue.kind)) return "Remove the leftover registry key."
  return "Remove this invalid registry entry."
}

function getRegistryIssueMarkClass(issue: RegistryIssue) {
  const tone = getRegistrySeverityTone(issue.severity)
  if (tone === "high") return "border-red-400/35 bg-red-500/12 text-red-200"
  if (tone === "medium") return "border-amber-400/35 bg-amber-400/12 text-amber-200"
  return "border-accent-green/35 bg-accent-green/10 text-accent-green"
}

function getRegistrySeverityTone(severity: string) {
  if (/high/i.test(severity)) return "high"
  if (/low/i.test(severity)) return "low"
  return "medium"
}

function getRegistrySeverityRank(severity: string) {
  const tone = getRegistrySeverityTone(severity)
  return tone === "high" ? 0 : tone === "medium" ? 1 : 2
}

function getRegistryHealthLabel(highIssues: number, mediumIssues: number) {
  if (highIssues > 0) return "Needs Review"
  if (mediumIssues > 0) return "Fair"
  return "Good"
}

function formatRegistryScanTime(date: Date) {
  return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
}

function formatRegistryIssueDetails(issue: RegistryIssue) {
  return [
    `Issue: ${issue.kind}`,
    `Item: ${issue.displayName}`,
    `Category: ${getRegistryIssueCategory(issue)}`,
    `Severity: ${issue.severity}`,
    `Registry Key: ${formatRegistryIssueKey(issue)}`,
    `Data: ${getRegistryIssueData(issue)}`,
    `Problem: ${issue.reason}`,
    `Recommendation: ${getRegistryIssueRecommendation(issue)}`,
  ].join("\n")
}

export function StartupManagerPage({ invoke, isAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<StartupScanResult | null>(null)
  const [startupScanError, setStartupScanError] = useState("")
  const [startupActionError, setStartupActionError] = useState("")
  const [query, setQuery] = useState("")
  const bucketFilter: StartupBucketFilter = "all"
  const [enabledFilter, setEnabledFilter] = useState<StartupEnabledFilter>("all")
  const [trustFilter] = useState<StartupTrustFilter>("all")
  const [locationFilter, setLocationFilter] = useState("all")
  const [sourceFilter, setSourceFilter] = useState("all")
  const [impactFilter, setImpactFilter] = useState("all")
  const [ignoredFilter, setIgnoredFilter] = useState<StartupIgnoredFilter>("active")
  const [sortKey, setSortKey] = useState<StartupSortKey>("risk")
  const [isScanning, setIsScanning] = useState(false)
  const [updatingId, setUpdatingId] = useState("")
  const [pendingToggle, setPendingToggle] = useState<StartupItem | null>(null)
  const [pendingRemove, setPendingRemove] = useState<StartupItem | null>(null)
  const [selectedItemId, setSelectedItemId] = useState("")
  const items = scan?.items ?? []
  const activeItems = useMemo(() => items.filter((item) => !item.isIgnored), [items])
  const sourceOptions = useMemo(() => Array.from(new Set(items.map((item) => item.sourceType || item.source).filter(Boolean))).sort(), [items])
  const locationOptions = useMemo(() => Array.from(new Set(items.map(getStartupLocationValue).filter(Boolean))).sort(), [items])
  const impactSummary = useMemo(() => countStartupImpacts(items), [items])
  const visibleItems = useMemo(
    () => sortStartupItems(
      filterStartupItems(items, { query, bucketFilter, enabledFilter, trustFilter, riskFilter: "all", sourceFilter, ignoredFilter })
        .filter((item) => locationFilter === "all" || getStartupLocationValue(item) === locationFilter)
        .filter((item) => impactFilter === "all" || getStartupImpactLevel(item) === impactFilter),
      sortKey
    ),
    [bucketFilter, enabledFilter, ignoredFilter, impactFilter, items, locationFilter, query, sortKey, sourceFilter, trustFilter]
  )
  const selectedItem = items.find((item) => item.id === selectedItemId) ?? null
  const startupToggleBusy = Boolean(updatingId)
  const startupWarnings = useMemo(
    () => [...(startupScanError ? [startupScanError] : []), ...(startupActionError ? [startupActionError] : []), ...(scan?.warnings ?? [])],
    [scan, startupActionError, startupScanError]
  )
  const hasResults = scan !== null || startupScanError
  const activeTotalCount = activeItems.length
  const enabledActiveCount = activeItems.filter((item) => item.isEnabled).length
  const disabledActiveCount = activeItems.filter((item) => !item.isEnabled).length
  const ignoredCount = scan?.ignoredCount ?? items.filter((item) => item.isIgnored).length
  const activeStartupTab: StartupEnabledFilter | "ignored" = ignoredFilter === "ignored" ? "ignored" : enabledFilter

  async function scanStartup() {
    if ((isScanning && scan !== null) || updatingId) return
    setIsScanning(true)
    setStartupScanError("")
    setStartupActionError("")
    setPendingToggle(null)
    setPendingRemove(null)
    try {
      const response = await invoke<StartupScanResult>("maintenance.scanStartup", {})
      setScan(response)
      setSelectedItemId((current) => current && response.items.some((item) => item.id === current) ? current : response.items[0]?.id ?? "")
    } catch (error) {
      const message = error instanceof Error ? error.message : "Startup scan failed."
      setStartupScanError(message)
      toast.error(message)
    } finally {
      setIsScanning(false)
    }
  }

  async function setIgnored(item: StartupItem, ignored: boolean) {
    setStartupActionError("")
    setUpdatingId(item.id)
    try {
      const response = await invoke<StartupIgnoreResult>("maintenance.setStartupIgnored", { itemId: item.id, ignored })
      toast.success(response.message)
      await scanStartup()
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, ignored ? "Ignore failed." : "Restore to review failed."))
    } finally {
      setUpdatingId("")
    }
  }

  async function exportItem(item: StartupItem) {
    setStartupActionError("")
    try {
      const response = await invoke<StartupExportResult>("maintenance.exportStartupItem", { itemId: item.id })
      toast.success(response.fullPathsIncluded ? `Exported ${response.fileName}.` : `Exported redacted details to ${response.fileName}.`)
      await invoke("files.revealPath", { path: response.exportPath })
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, "Startup export failed."))
    }
  }

  async function copyCommand(item: StartupItem) {
    try {
      await navigator.clipboard.writeText(item.commandRaw || item.command)
      toast.success("Startup command copied.")
    } catch {
      toast.error("Command could not be copied.")
    }
  }

  async function revealPath(path: string, fallback: string) {
    if (!path) { toast.error(fallback); return }
    try {
      await invoke("files.revealPath", { path })
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, "Location could not be opened."))
    }
  }

  async function openStartupSource(item: StartupItem) {
    setStartupActionError("")
    try {
      const response = await invoke<StartupOpenLocationResult>("maintenance.openStartupSource", { itemId: item.id })
      toast.success(`Opened ${response.targetKind}.`)
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, "Source location could not be opened."))
    }
  }

  async function removeBrokenItem(item: StartupItem) {
    setPendingRemove(null)
    setStartupActionError("")
    setUpdatingId(item.id)
    try {
      const response = await invoke<StartupToggleResult>("maintenance.removeBrokenStartupItem", {
        itemId: item.id,
        confirmation: "REMOVE BROKEN STARTUP",
      })
      toast.success(response.message)
      await scanStartup()
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, "Broken startup removal failed."))
    } finally {
      setUpdatingId("")
    }
  }

  async function toggleStartupItem(item: StartupItem) {
    if (isScanning || updatingId) return
    if (!item.canToggle) { toast.error("This startup item cannot be changed by FileLocker."); return }
    if (item.requiresAdministrator && !isAdministrator) { toast.error("Restart FileLocker as administrator before changing this startup item."); return }
    setPendingToggle(null)
    setStartupActionError("")
    setUpdatingId(item.id)
    try {
      const response = await invoke<StartupToggleResult>("maintenance.setStartupEnabled", { itemId: item.id, enabled: !item.isEnabled })
      toast.success(response.message)
      await scanStartup()
    } catch (error) {
      setStartupActionError(showMaintenanceError(error, item.isEnabled ? "Startup disable failed." : "Startup restore failed."))
    } finally {
      setUpdatingId("")
    }
  }

  return (
    <MaintenanceFrame>
      <StartupSummaryPanel
        scan={scan}
        impactSummary={impactSummary}
        isScanning={isScanning}
        isBusy={Boolean(updatingId)}
        onScan={() => void scanStartup()}
      />

      {hasResults ? <StartupNotice warnings={startupWarnings} /> : null}

      {!hasResults ? (
        <section className="rounded-md border border-border bg-transparent px-4 py-10">
          <div className="mx-auto flex max-w-md flex-col items-center text-center">
            <div className="flex size-11 items-center justify-center rounded-md border border-border-strong bg-accent/10 text-accent">
              <Power className="size-5" aria-hidden />
            </div>
            <p className="mt-3 text-sm text-secondary">Run a startup scan to load startup entries and review their status.</p>
            <Button className="mt-4" onClick={() => void scanStartup()} disabled={isScanning}>
              <ScanLine data-icon="inline-start" />
              {isScanning ? "Scanning..." : "Run Scan"}
            </Button>
          </div>
        </section>
      ) : (
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_330px]">
          <section className="min-w-0 overflow-hidden rounded-md border border-border bg-transparent">
            <div className="border-b border-border/70 px-4 py-3">
              <div className="flex flex-col gap-3 2xl:flex-row 2xl:items-center 2xl:justify-between">
                <div className="flex min-w-0 flex-wrap gap-1 rounded-md border border-border/70 bg-bg-subtle/80 p-1">
                  {([
                    { key: "all", label: "All", count: activeTotalCount },
                    { key: "enabled", label: "Enabled", count: enabledActiveCount },
                    { key: "disabled", label: "Disabled", count: disabledActiveCount },
                    { key: "ignored", label: "Ignored", count: ignoredCount },
                  ] as const).map((tab) => (
                    <button
                      key={tab.key}
                      type="button"
                      className={cn(
                        "h-8 rounded-md px-3 text-sm font-medium transition-colors",
                        activeStartupTab === tab.key ? "bg-accent text-[#08111f]" : "text-secondary hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => {
                        if (tab.key === "ignored") {
                          setIgnoredFilter("ignored")
                          setEnabledFilter("all")
                        } else {
                          setIgnoredFilter("active")
                          setEnabledFilter(tab.key)
                        }
                      }}
                    >
                      {tab.label} ({tab.count})
                    </button>
                  ))}
                </div>

                <div className="flex flex-1 flex-wrap gap-2 2xl:justify-end">
                  <div className="relative min-w-[230px] flex-1 2xl:max-w-[360px]">
                    <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" aria-hidden />
                    <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search startup items..." className="bg-bg-subtle/80 pl-9" />
                  </div>
                  <StartupFilterSelect value={sortKey} onChange={(value) => setSortKey(value as StartupSortKey)} ariaLabel="Sort startup items">
                    <option value="impact">Sort by: Impact</option>
                    <option value="risk">Sort by: Risk</option>
                    <option value="enabled">Sort by: Status</option>
                    <option value="name">Sort by: Name</option>
                    <option value="publisher">Sort by: Publisher</option>
                  </StartupFilterSelect>
                </div>
              </div>

              <div className="mt-3 grid gap-2 lg:grid-cols-3">
                <StartupFilterSelect value={locationFilter} onChange={setLocationFilter} ariaLabel="Filter by startup location">
                  <option value="all">All Locations</option>
                  {locationOptions.map((location) => <option key={location} value={location}>{formatStartupLocationLabel(location)}</option>)}
                </StartupFilterSelect>
                <StartupFilterSelect value={sourceFilter} onChange={setSourceFilter} ariaLabel="Filter by startup source">
                  <option value="all">All Sources</option>
                  {sourceOptions.map((source) => <option key={source} value={source}>{source}</option>)}
                </StartupFilterSelect>
                <StartupFilterSelect value={impactFilter} onChange={setImpactFilter} ariaLabel="Filter by impact level">
                  <option value="all">All Impact Levels</option>
                  {(["High", "Medium", "Low"] as const).map((impact) => <option key={impact} value={impact}>{impact} Impact</option>)}
                </StartupFilterSelect>
              </div>
            </div>

            <div className="overflow-x-auto">
              <div className="min-w-[780px]">
                <div className="grid grid-cols-[minmax(250px,1.35fr)_minmax(150px,.85fr)_110px_145px_96px] gap-3 border-b border-border/70 bg-bg-subtle/65 px-4 py-2 text-xs font-medium text-muted">
                  <span>Startup Item</span>
                  <span>Publisher</span>
                  <span>Impact</span>
                  <span>Status</span>
                  <span>Actions</span>
                </div>

                {visibleItems.map((item) => {
                  const adminBlocked = item.requiresAdministrator && !isAdministrator
                  const isSelected = selectedItem?.id === item.id
                  return (
                    <div
                      key={item.id}
                      role="button"
                      tabIndex={0}
                      className={cn(
                        "grid min-h-[58px] grid-cols-[minmax(250px,1.35fr)_minmax(150px,.85fr)_110px_145px_96px] items-center gap-3 border-b border-border/60 px-4 py-2.5 transition-colors last:border-b-0 hover:bg-bg-surface-hover/55",
                        isSelected && "bg-accent/8"
                      )}
                      onClick={() => setSelectedItemId(item.id)}
                      onKeyDown={(event) => {
                        if (event.key === "Enter" || event.key === " ") {
                          event.preventDefault()
                          setSelectedItemId(item.id)
                        }
                      }}
                    >
                      <div className="flex min-w-0 items-center gap-3">
                        <StartupAppMark item={item} />
                        <div className="min-w-0">
                          <div className="flex min-w-0 items-center gap-1.5">
                            <span className="truncate text-sm font-semibold text-primary">{item.name}</span>
                            {item.requiresAdministrator ? <Badge variant="destructive" className="h-5 px-1.5 text-[10px]">Admin</Badge> : null}
                            {item.isIgnored ? <Badge variant="outline" className="h-5 px-1.5 text-[10px]">Ignored</Badge> : null}
                          </div>
                          <div className="mt-0.5 truncate text-xs text-secondary">{formatStartupLocationLabel(getStartupLocationValue(item))}</div>
                        </div>
                      </div>
                      <div className="min-w-0 truncate text-sm text-secondary">{item.publisher || "Unknown publisher"}</div>
                      <StartupImpactBadge item={item} />
                      <div className="flex items-center gap-2" onClick={(event) => event.stopPropagation()}>
                        <span className={cn("min-w-[58px] text-sm", item.isEnabled ? "text-primary" : "text-secondary")}>
                          {item.isEnabled ? "Enabled" : "Disabled"}
                        </span>
                        <Switch
                          size="sm"
                          checked={item.isEnabled}
                          disabled={!item.canToggle || adminBlocked || isScanning || startupToggleBusy}
                          aria-label={`${item.isEnabled ? "Disable" : "Enable"} ${item.name}`}
                          onCheckedChange={() => setPendingToggle(item)}
                        />
                      </div>
                      <div className="flex items-center gap-1.5" onClick={(event) => event.stopPropagation()}>
                        <Button variant="outline" size="icon-sm" aria-label={`Show details for ${item.name}`} title="Details" onClick={() => setSelectedItemId(item.id)}>
                          <Settings2 className="size-4" aria-hidden />
                        </Button>
                        <Button variant="outline" size="icon-sm" aria-label={`Open source for ${item.name}`} title="Open source" onClick={() => void openStartupSource(item)}>
                          <MoreVertical className="size-4" aria-hidden />
                        </Button>
                      </div>
                    </div>
                  )
                })}

                {visibleItems.length === 0 ? (
                  <div className="px-4 py-5 text-sm text-secondary">
                    {startupScanError ? "Startup scan failed. Review the warning and try again." : isScanning ? "Scanning startup entries..." : "No startup items match these filters."}
                  </div>
                ) : null}
              </div>
            </div>

            <div className="flex items-center justify-between gap-3 border-t border-border/70 px-4 py-2 text-xs text-muted">
              <span>{visibleItems.length} item{visibleItems.length === 1 ? "" : "s"} shown</span>
              <span>{items.length} total</span>
            </div>
          </section>

          <StartupDetailsPanel
            item={selectedItem}
            restoreRecords={scan?.restoreRecords ?? []}
            isAdministrator={isAdministrator}
            isBusy={isScanning || startupToggleBusy}
            updatingId={updatingId}
            onToggle={() => selectedItem && setPendingToggle(selectedItem)}
            onRemove={() => selectedItem && setPendingRemove(selectedItem)}
            onOpenFile={() => selectedItem && void revealPath(selectedItem.executableResolved || selectedItem.targetPath || "", "No resolved file target is available.")}
            onOpenSource={() => selectedItem && void openStartupSource(selectedItem)}
            onCopy={() => selectedItem && void copyCommand(selectedItem)}
            onExport={() => selectedItem && void exportItem(selectedItem)}
            onIgnore={() => selectedItem && void setIgnored(selectedItem, !selectedItem.isIgnored)}
          />
        </div>
      )}

      <AlertDialog open={Boolean(pendingToggle)} onOpenChange={(open) => !open && setPendingToggle(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{pendingToggle?.isEnabled ? "Disable startup item?" : "Restore startup item?"}</AlertDialogTitle>
            <AlertDialogDescription>
              {pendingToggle?.isEnabled
                ? `FileLocker will save restore metadata first, then disable ${pendingToggle.name}.`
                : `FileLocker will restore ${pendingToggle?.name ?? "this item"} to its original startup location.`}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isScanning || startupToggleBusy}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant={pendingToggle?.isEnabled ? "destructive" : "secondary"}
              onClick={() => pendingToggle && void toggleStartupItem(pendingToggle)}
              disabled={isScanning || startupToggleBusy}
            >
              {startupToggleBusy ? "Updating..." : pendingToggle?.isEnabled ? "Disable" : "Restore"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <AlertDialog open={Boolean(pendingRemove)} onOpenChange={(open) => !open && setPendingRemove(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove broken startup item?</AlertDialogTitle>
            <AlertDialogDescription>
              FileLocker will save restore metadata first, then remove {pendingRemove?.name ?? "this item"} from active startup. This is only available for entries classified as broken.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isScanning || startupToggleBusy}>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => pendingRemove && void removeBrokenItem(pendingRemove)} disabled={isScanning || startupToggleBusy}>
              {startupToggleBusy ? "Removing..." : "Remove Broken Item"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </MaintenanceFrame>
  )
}

function StartupSummaryPanel({
  scan,
  impactSummary,
  isScanning,
  isBusy,
  onScan,
}: {
  scan: StartupScanResult | null
  impactSummary: Record<"High" | "Medium" | "Low", number>
  isScanning: boolean
  isBusy: boolean
  onScan: () => void
}) {
  return (
    <section className="rounded-md border border-border bg-transparent px-4 py-3.5">
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-center">
        <div className="min-w-0">
          <div className="font-display text-base font-semibold tracking-tight text-primary">Startup Impact Summary</div>
          <div className="mt-1 text-sm text-secondary">
            {scan ? `${scan.items.length} programs found` : "No scan has run"} <span className="px-1 text-muted">•</span> {scan ? `${scan.enabledCount} enabled` : "Run a scan to load entries"}
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-4">
          <StartupSummaryMetric label="High Impact" value={impactSummary.High} tone="high" />
          <StartupSummaryMetric label="Medium Impact" value={impactSummary.Medium} tone="medium" />
          <StartupSummaryMetric label="Low Impact" value={impactSummary.Low} tone="low" />
          <Button onClick={onScan} disabled={isScanning || isBusy} className="min-w-[132px]">
            <RefreshCcw data-icon="inline-start" />
            {isScanning ? "Scanning..." : "Run Scan"}
          </Button>
        </div>
      </div>
    </section>
  )
}

function StartupSummaryMetric({ label, value, tone }: { label: string; value: number; tone: "high" | "medium" | "low" }) {
  return (
    <div className="border-l border-border/80 pl-4 first:border-l-0 first:pl-0">
      <div className="text-xs text-muted">{label}</div>
      <div className={cn(
        "mt-1 font-display text-base font-semibold",
        tone === "high" && "text-red-300",
        tone === "medium" && "text-amber-300",
        tone === "low" && "text-accent-green"
      )}>
        {value}
      </div>
    </div>
  )
}

function StartupNotice({ warnings }: { warnings: string[] }) {
  const unique = Array.from(new Set(warnings.map((warning) => warning.trim()).filter(Boolean))).slice(0, 2)
  return (
    <div className="rounded-md border border-amber-400/25 bg-amber-400/10 px-4 py-3 text-sm text-secondary" role="status" aria-live="polite">
      <div className="flex items-start gap-2.5">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
        <div className="min-w-0">
          <div className="font-display text-sm font-semibold tracking-tight text-amber-200">Some startup programs can slow down your computer.</div>
          <div className="mt-1 leading-snug">
            {unique.length > 0 ? unique.join(" ") : "Review and disable programs you do not need to improve boot time and system performance."}
          </div>
        </div>
      </div>
    </div>
  )
}

function StartupFilterSelect({
  value,
  onChange,
  ariaLabel,
  children,
}: {
  value: string
  onChange: (value: string) => void
  ariaLabel: string
  children: React.ReactNode
}) {
  return (
    <select
      className="h-9 min-w-[160px] rounded-md border border-border/80 bg-bg-subtle px-3 text-sm text-primary outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
      value={value}
      aria-label={ariaLabel}
      onChange={(event) => onChange(event.target.value)}
    >
      {children}
    </select>
  )
}

function StartupDetailsPanel({
  item,
  restoreRecords,
  isAdministrator,
  isBusy,
  updatingId,
  onToggle,
  onRemove,
  onOpenFile,
  onOpenSource,
  onCopy,
  onExport,
  onIgnore,
}: {
  item: StartupItem | null
  restoreRecords: StartupRestoreRecord[]
  isAdministrator: boolean
  isBusy: boolean
  updatingId: string
  onToggle: () => void
  onRemove: () => void
  onOpenFile: () => void
  onOpenSource: () => void
  onCopy: () => void
  onExport: () => void
  onIgnore: () => void
}) {
  const itemRestoreRecords = item ? restoreRecords.filter((record) => record.id === item.id) : []
  const adminBlocked = Boolean(item?.requiresAdministrator && !isAdministrator)
  const toggleBlocked = !item || !item.canToggle || adminBlocked || isBusy

  return (
    <aside className="min-w-0 overflow-hidden rounded-md border border-border bg-transparent xl:sticky xl:top-4 xl:self-start">
      <div className="flex items-center justify-between gap-3 border-b border-border/70 px-4 py-3">
        <div className="font-display text-sm font-semibold tracking-tight text-primary">Startup Details</div>
        <Info className="size-4 text-muted" aria-hidden />
      </div>

      {!item ? (
        <div className="px-4 py-8 text-sm text-secondary">Select a startup item to view its details.</div>
      ) : (
        <>
          <div className="border-b border-border/70 px-4 py-4">
            <div className="flex items-center gap-3">
              <StartupAppMark item={item} />
              <div className="min-w-0">
                <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{item.name}</div>
                <div className="mt-0.5 truncate text-xs text-secondary">{item.publisher || "Unknown publisher"}</div>
              </div>
            </div>
          </div>

          <div className="divide-y divide-border/65 px-4">
            <StartupDetailRow label="Location" value={formatStartupLocationLabel(getStartupLocationValue(item))} />
            <StartupDetailRow label="Status" value={item.isEnabled ? "Enabled" : "Disabled"} valueClassName={item.isEnabled ? "text-accent-green" : "text-secondary"} />
            <StartupDetailRow label="Impact" value={`${getStartupImpactLevel(item)} impact`} />
            <StartupDetailRow label="File Path" value={item.executableResolved || item.targetPath || item.commandRaw || item.command || "No resolved file"} mono />
            <StartupDetailRow label="Description" value={item.notes || item.name} />
            <StartupDetailRow label="Publisher" value={item.publisher || "Unknown publisher"} />
            <StartupDetailRow label="Startup Type" value={`${item.sourceType || item.source || "Startup"}${item.scope ? ` / ${item.scope}` : ""}`} />
            <StartupDetailRow label="Signature" value={item.isMicrosoftSigned ? "Microsoft signed" : item.signatureStatus || "Unknown"} />
          </div>

          <div className="border-t border-border/70 px-4 py-3">
            {adminBlocked ? (
              <div className="app-inline-notice app-inline-notice-warning mb-3">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <span>Administrator mode is required to change this item.</span>
              </div>
            ) : null}

            <div className="grid gap-2">
              <Button variant={item.isEnabled ? "default" : "secondary"} onClick={onToggle} disabled={toggleBlocked}>
                {item.isEnabled ? <PowerOff data-icon="inline-start" /> : <Power data-icon="inline-start" />}
                {updatingId === item.id ? "Updating..." : item.isEnabled ? "Disable" : "Enable"}
              </Button>
              {item.category === "Broken Startup Items" && item.canToggle ? (
                <Button variant="destructive" onClick={onRemove} disabled={adminBlocked || isBusy}>
                  <Trash2 data-icon="inline-start" />
                  Remove Broken Item
                </Button>
              ) : null}
              <Button variant="outline" onClick={onOpenFile}><FolderOpen data-icon="inline-start" />Open File Location</Button>
              <Button variant="outline" onClick={onOpenSource}><ExternalLink data-icon="inline-start" />Open Startup Source</Button>
            </div>

            <div className="mt-2 flex flex-wrap gap-2">
              <Button variant="ghost" size="sm" onClick={onCopy}><Copy data-icon="inline-start" />Copy</Button>
              <Button variant="ghost" size="sm" onClick={onExport}><FileJson data-icon="inline-start" />Export</Button>
              <Button variant={item.isIgnored ? "secondary" : "ghost"} size="sm" onClick={onIgnore}>{item.isIgnored ? "Return to Review" : "Ignore"}</Button>
            </div>

            {itemRestoreRecords.length > 0 ? (
              <div className="mt-3 text-xs text-muted">
                {itemRestoreRecords.length} restore record{itemRestoreRecords.length === 1 ? "" : "s"} saved by FileLocker.
              </div>
            ) : null}
          </div>
        </>
      )}
    </aside>
  )
}

function StartupDetailRow({ label, value, valueClassName, mono = false }: { label: string; value: string; valueClassName?: string; mono?: boolean }) {
  return (
    <div className="grid gap-1 py-3">
      <div className="text-xs text-muted">{label}</div>
      <div className={cn("min-w-0 break-words text-sm text-secondary", mono && "break-all font-mono text-xs", valueClassName)}>{value}</div>
    </div>
  )
}

function StartupAppMark({ item }: { item: StartupItem }) {
  const initial = (item.name.trim()[0] || "?").toUpperCase()
  return (
    <div className={cn("flex size-8 shrink-0 items-center justify-center rounded-md border font-display text-sm font-semibold", getStartupMarkClass(item))} aria-hidden>
      {initial}
    </div>
  )
}

function StartupImpactBadge({ item }: { item: StartupItem }) {
  const impact = getStartupImpactLevel(item)
  return (
    <Badge variant="outline" className={cn("h-6 justify-center", getStartupImpactBadgeClass(impact))}>
      {impact}
    </Badge>
  )
}

function getStartupImpactLevel(item: StartupItem) {
  const value = (item.startupImpact || item.riskLevel || "").trim().toLowerCase()
  if (value === "high") return "High"
  if (value === "medium") return "Medium"
  if (value === "low") return "Low"
  return "Low"
}

function countStartupImpacts(items: StartupItem[]) {
  return items.reduce<Record<"High" | "Medium" | "Low", number>>(
    (counts, item) => {
      counts[getStartupImpactLevel(item)] += 1
      return counts
    },
    { High: 0, Medium: 0, Low: 0 }
  )
}

function getStartupLocationValue(item: StartupItem) {
  return item.location || item.sourceLocation || item.source || item.sourceType || "Unknown"
}

function formatStartupLocationLabel(value: string) {
  const normalized = value.replaceAll("\\", "/")
  if (/HKCU/i.test(value) && /RunOnce/i.test(value)) return "HKCU RunOnce"
  if (/HKLM/i.test(value) && /RunOnce/i.test(value)) return "HKLM RunOnce"
  if (/HKCU/i.test(value) && /Run/i.test(value)) return "HKCU Run"
  if (/HKLM/i.test(value) && /Run/i.test(value)) return "HKLM Run"
  if (/Startup/i.test(value)) return "Startup Folder"
  if (/Task/i.test(value)) return "Task Scheduler"
  if (/WMI/i.test(value)) return "WMI"
  const label = normalized.split("/").filter(Boolean).slice(-2).join("/")
  return label.length > 44 ? `${label.slice(0, 41)}...` : label || "Unknown"
}

function getStartupImpactBadgeClass(impact: string) {
  if (impact === "High") return "border-destructive/45 bg-destructive/12 text-red-200"
  if (impact === "Medium") return "border-amber-400/45 bg-amber-400/10 text-amber-300"
  return "border-accent-green/40 bg-accent-green/10 text-accent-green"
}

function getStartupMarkClass(item: StartupItem) {
  const impact = getStartupImpactLevel(item)
  if (item.isMicrosoftSigned) return "border-accent/35 bg-accent/12 text-accent"
  if (impact === "High") return "border-destructive/35 bg-destructive/14 text-red-200"
  if (impact === "Medium") return "border-amber-400/35 bg-amber-400/12 text-amber-200"
  return "border-accent-green/35 bg-accent-green/12 text-accent-green"
}

type InstalledAppTab = "all" | "large" | "recent" | "microsoft" | "store" | "desktop"
type InstalledAppTypeFilter = "all" | "desktop" | "microsoft" | "system" | "user"
type InstalledAppSizeFilter = "all" | "large" | "medium" | "small" | "unknown"

const installedAppPageSize = 10

export function AppManagerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [apps, setApps] = useState<InstalledApp[]>([])
  const [appWarnings, setAppWarnings] = useState<string[]>([])
  const [appScanError, setAppScanError] = useState("")
  const [appActionError, setAppActionError] = useState("")
  const [query, setQuery] = useState("")
  const [appTab, setAppTab] = useState<InstalledAppTab>("all")
  const [appTypeFilter, setAppTypeFilter] = useState<InstalledAppTypeFilter>("all")
  const [appSizeFilter, setAppSizeFilter] = useState<InstalledAppSizeFilter>("all")
  const [sortKey, setSortKey] = useState<"name" | "publisher" | "size" | "date">("size")
  const [appPageIndex, setAppPageIndex] = useState(0)
  const [selectedAppId, setSelectedAppId] = useState("")
  const [isScanningApps, setIsScanningApps] = useState(false)
  const [pendingUninstall, setPendingUninstall] = useState<InstalledApp | null>(null)
  const [isLaunchingUninstaller, setIsLaunchingUninstaller] = useState(false)
  const [leftoverScan, setLeftoverScan] = useState<AppLeftoverScanResult | null>(null)
  const [leftoverScanError, setLeftoverScanError] = useState("")
  const [leftoverCleanError, setLeftoverCleanError] = useState("")
  const [selectedLeftoverIds, setSelectedLeftoverIds] = useState<string[]>([])
  const [isScanningLeftovers, setIsScanningLeftovers] = useState(false)
  const [isCleaningLeftovers, setIsCleaningLeftovers] = useState(false)
  const [lastLeftoverClean, setLastLeftoverClean] = useState<AppLeftoverCleanResult | null>(null)
  const [showLeftoverConfirmation, setShowLeftoverConfirmation] = useState(false)
  const [skipLeftoverConfirmation, setSkipLeftoverConfirmation] = useStoredBoolean("filelocker.skipAppLeftoverCleanupConfirmation")
  const leftoverScanRequestId = useRef(0)
  const appStats = useMemo(() => buildInstalledAppStats(apps), [apps])
  const appTabCounts = useMemo(() => buildInstalledAppTabCounts(apps), [apps])
  const visibleApps = useMemo(
    () => sortInstalledApps(filterInstalledAppsByView(apps, { query, tab: appTab, typeFilter: appTypeFilter, sizeFilter: appSizeFilter }), sortKey),
    [appSizeFilter, appTab, appTypeFilter, apps, query, sortKey]
  )
  const appPageCount = Math.max(1, Math.ceil(visibleApps.length / installedAppPageSize))
  const safeAppPageIndex = Math.min(appPageIndex, appPageCount - 1)
  const pageStartIndex = safeAppPageIndex * installedAppPageSize
  const pagedApps = visibleApps.slice(pageStartIndex, pageStartIndex + installedAppPageSize)
  const selectedApp = visibleApps.find((app) => app.id === selectedAppId) ?? pagedApps[0] ?? null
  const leftovers = leftoverScan?.categories ?? []
  const selectedLeftoverIdSet = useMemo(() => new Set(selectedLeftoverIds), [selectedLeftoverIds])
  const selectedLeftovers = leftovers.filter((c) => selectedLeftoverIdSet.has(c.id))
  const leftoversNeedAdministrator = selectedLeftovers.some((c) => c.requiresAdministrator)
  const canCleanLeftovers = selectedLeftovers.length > 0 && !isScanningLeftovers && !isCleaningLeftovers && (!leftoversNeedAdministrator || isAdministrator)
  const uninstallerNotice = selectedApp ? getUninstallerNotice(selectedApp) : ""
  const isAppManagerBusy = isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller
  const canUninstallSelected = Boolean(selectedApp?.canLaunchUninstaller) && !isAppManagerBusy
  const visibleRangeStart = visibleApps.length === 0 ? 0 : pageStartIndex + 1
  const visibleRangeEnd = Math.min(pageStartIndex + pagedApps.length, visibleApps.length)
  const leftoverWarnings = useMemo(
    () => [
      ...(leftoverScanError ? [leftoverScanError] : []),
      ...(leftoverCleanError ? [leftoverCleanError] : []),
      ...(leftoverScan?.warnings ?? []),
      ...(lastLeftoverClean?.failures.map((f) => `${f.appDisplayName}: ${f.path} - ${f.message}`) ?? []),
    ],
    [lastLeftoverClean, leftoverCleanError, leftoverScan, leftoverScanError]
  )
  const appStatusWarnings = useMemo(
    () => [...(appScanError ? [appScanError] : []), ...appWarnings],
    [appScanError, appWarnings]
  )
  const hasResults = apps.length > 0 || appScanError

  useEffect(() => {
    setAppActionError("")
    resetLeftoverReview()
  }, [selectedApp?.id])

  useEffect(() => {
    setAppPageIndex(0)
  }, [appSizeFilter, appTab, appTypeFilter, query, sortKey])

  useEffect(() => {
    if (appPageIndex > appPageCount - 1) {
      setAppPageIndex(appPageCount - 1)
    }
  }, [appPageCount, appPageIndex])

  async function scanInstalledApps() {
    if ((isScanningApps && apps.length > 0) || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) return
    resetLeftoverReview()
    setIsScanningApps(true)
    setAppScanError("")
    setAppActionError("")
    setPendingUninstall(null)
    try {
      const response = await invoke<InstalledAppsScanResult>("maintenance.scanInstalledApps", {})
      setApps(response.apps)
      setAppWarnings(response.warnings)
      setAppPageIndex(0)
      setSelectedAppId((current) => response.apps.some((app) => app.id === current) ? current : response.apps[0]?.id || "")
    } catch (error) {
      const message = error instanceof Error ? error.message : "Installed app scan failed."
      setAppScanError(message)
      toast.error(message)
    } finally {
      setIsScanningApps(false)
    }
  }

  function selectApp(app: InstalledApp) {
    if (isScanningApps || isCleaningLeftovers) return
    setSelectedAppId(app.id)
    setAppActionError("")
    resetLeftoverReview()
  }

  async function copyAppDetails(app: InstalledApp) {
    try {
      await navigator.clipboard.writeText(formatInstalledAppDetails(app))
      toast.success("App details copied.")
    } catch {
      toast.error("App details could not be copied.")
    }
  }

  async function exportVisibleApps() {
    if (visibleApps.length === 0) {
      toast.error("No apps are visible to export.")
      return
    }

    try {
      await navigator.clipboard.writeText(formatInstalledAppListExport(visibleApps))
      toast.success(`Exported ${visibleApps.length} app${visibleApps.length === 1 ? "" : "s"} to the clipboard.`)
    } catch {
      toast.error("Installed app list could not be exported.")
    }
  }

  function resetLeftoverReview() {
    leftoverScanRequestId.current += 1
    setLeftoverScan(null)
    setLeftoverScanError("")
    setLeftoverCleanError("")
    setSelectedLeftoverIds([])
    setLastLeftoverClean(null)
    setShowLeftoverConfirmation(false)
    setIsScanningLeftovers(false)
  }

  async function revealInstallLocation(app: InstalledApp) {
    if (isScanningApps || isCleaningLeftovers) return
    if (!app.installLocation) {
      const message = "This app does not publish an install location."
      setAppActionError(message)
      toast.error(message)
      return
    }
    setAppActionError("")
    try {
      await invoke("files.revealPath", { path: app.installLocation })
      setAppActionError("")
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unable to open install location."
      setAppActionError(message)
      toast.error(message)
    }
  }

  async function launchUninstaller(app: InstalledApp) {
    if (isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) return
    if (!app.canLaunchUninstaller) {
      const message = "This app does not publish a safe visible uninstaller command."
      setAppActionError(message)
      toast.error(message)
      return
    }
    setPendingUninstall(null)
    setIsLaunchingUninstaller(true)
    setAppActionError("")
    try {
      const response = await invoke<UninstallerLaunchResult>("maintenance.launchUninstaller", { appId: app.id, confirmation: "UNINSTALL" })
      setAppActionError("")
      toast.success(response.message)
    } catch (error) {
      setAppActionError(showMaintenanceError(error, "Unable to launch uninstaller."))
    } finally {
      setIsLaunchingUninstaller(false)
    }
  }

  async function scanLeftovers(app = selectedApp) {
    if (isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) return
    if (!app) { toast.error("Select an app first."); return }
    const requestId = leftoverScanRequestId.current + 1
    leftoverScanRequestId.current = requestId
    setIsScanningLeftovers(true)
    setShowLeftoverConfirmation(false)
    setLastLeftoverClean(null)
    setLeftoverScan(null)
    setLeftoverScanError("")
    setLeftoverCleanError("")
    setSelectedLeftoverIds([])
    try {
      const response = await invoke<AppLeftoverScanResult>("maintenance.scanAppLeftovers", { appIds: [app.id] })
      if (leftoverScanRequestId.current !== requestId) return
      setLeftoverScanError("")
      setLeftoverScan(response)
      setSelectedLeftoverIds(response.categories.filter((c) => c.defaultSelected && c.isEnabled).map((c) => c.id))
    } catch (error) {
      if (leftoverScanRequestId.current !== requestId) return
      const message = error instanceof Error ? error.message : "App leftover scan failed."
      setLeftoverScanError(message)
      toast.error(message)
    } finally {
      if (leftoverScanRequestId.current === requestId) setIsScanningLeftovers(false)
    }
  }

  function setLeftoverSelected(categoryId: string, selected: boolean) {
    if (isScanningLeftovers || isCleaningLeftovers) return
    setSelectedLeftoverIds((current) =>
      selected ? Array.from(new Set([...current, categoryId])) : current.filter((id) => id !== categoryId)
    )
  }

  function requestCleanLeftovers() {
    if (isScanningLeftovers || isCleaningLeftovers) return
    if (selectedLeftovers.length === 0) { toast.error("Select at least one leftover category."); return }
    if (leftoversNeedAdministrator && !isAdministrator) { toast.error("Restart FileLocker as administrator before cleaning selected ProgramData leftovers."); return }
    if (skipLeftoverConfirmation) { void cleanLeftovers(); return }
    setShowLeftoverConfirmation(true)
  }

  async function cleanLeftovers() {
    if (isScanningLeftovers || isCleaningLeftovers) return
    if (!selectedApp) { toast.error("Select an app first."); return }
    if (selectedLeftovers.length === 0) { toast.error("Select at least one leftover category."); return }
    if (leftoversNeedAdministrator && !isAdministrator) { toast.error("Restart FileLocker as administrator before cleaning selected ProgramData leftovers."); return }
    setShowLeftoverConfirmation(false)
    setIsCleaningLeftovers(true)
    setLeftoverCleanError("")
    try {
      const response = await invoke<AppLeftoverCleanResult>("maintenance.cleanAppLeftovers", {
        appIds: [selectedApp.id],
        categoryIds: selectedLeftovers.map((c) => c.id),
        confirmation: "CLEAN LEFTOVERS",
      })
      setLastLeftoverClean(response)
      setLeftoverScan(response.scan)
      setLeftoverCleanError("")
      setSelectedLeftoverIds([])
      toast[response.failedCount > 0 ? "error" : "success"](
        response.failedCount > 0
          ? `Cleaned ${response.cleanedCount} area(s); ${response.failedCount} failed.`
          : `Cleaned ${response.freedDisplay}.`
      )
    } catch (error) {
      setLeftoverCleanError(showMaintenanceError(error, "App leftover cleanup failed."))
    } finally {
      setIsCleaningLeftovers(false)
    }
  }

  return (
    <MaintenanceFrame>
      <div className="flex flex-wrap items-center justify-end gap-2">
        <Button variant="outline" onClick={() => void scanInstalledApps()} disabled={isAppManagerBusy}>
          <RefreshCcw data-icon="inline-start" />
          {isScanningApps ? "Scanning..." : "Rescan"}
        </Button>
        <Button variant="destructive" onClick={() => selectedApp && setPendingUninstall(selectedApp)} disabled={!canUninstallSelected}>
          <Trash2 data-icon="inline-start" />
          Uninstall Selected
        </Button>
        <Button variant="outline" onClick={() => void exportVisibleApps()} disabled={visibleApps.length === 0 || isAppManagerBusy}>
          <Download data-icon="inline-start" />
          Export List
        </Button>
      </div>

      {!hasResults ? (
        <section className="rounded-md border border-border bg-transparent px-4 py-10">
          <div className="mx-auto flex max-w-md flex-col items-center text-center">
            <div className="flex size-11 items-center justify-center rounded-md border border-border-strong bg-accent/10 text-accent">
              <Package className="size-5" aria-hidden />
            </div>
            <p className="mt-3 text-sm text-secondary">Scan to list installed apps, inspect uninstall details, and find leftover cleanup opportunities.</p>
            <Button className="mt-4" onClick={() => void scanInstalledApps()} disabled={isScanningApps}>
              <ScanLine data-icon="inline-start" />
              {isScanningApps ? "Scanning..." : "Run Scan"}
            </Button>
          </div>
        </section>
      ) : (
        <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_330px]">
          <div className="min-w-0 space-y-4">
            <div className="grid gap-3 md:grid-cols-2 2xl:grid-cols-4">
              <InstalledAppSummaryCard label="Installed Apps" value={String(apps.length)} description="Total applications installed" icon={<Package className="size-5" aria-hidden />} tone="blue" />
              <InstalledAppSummaryCard label="Visible Apps" value={String(visibleApps.length)} description="Currently showing" icon={<Search className="size-5" aria-hidden />} tone="blue" />
              <InstalledAppSummaryCard label="Total Known Size" value={formatBytes(appStats.totalKnownSizeBytes)} description="Size of apps with known data" icon={<Database className="size-5" aria-hidden />} tone="green" />
              <InstalledAppSummaryCard label="Large Apps" value={String(appStats.largeCount)} description="Apps larger than 1 GB" icon={<Info className="size-5" aria-hidden />} tone="blue" />
            </div>

            {appStatusWarnings.length > 0 ? <WarningList warnings={appStatusWarnings} /> : null}

            <section className="overflow-hidden rounded-md border border-border bg-transparent">
              <div className="border-b border-border/70 px-4 py-3">
                <div className="grid gap-2 xl:grid-cols-[minmax(220px,1fr)_180px_180px_260px]">
                  <div className="relative min-w-0">
                    <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" aria-hidden />
                    <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search installed apps..." className="bg-bg-subtle/80 pl-9" disabled={isScanningApps || isCleaningLeftovers} />
                  </div>
                  <AppManagerSelect value={appTypeFilter} onChange={(value) => setAppTypeFilter(value as InstalledAppTypeFilter)} ariaLabel="Filter app type">
                    <option value="all">All Types</option>
                    <option value="desktop">Desktop Apps</option>
                    <option value="microsoft">Microsoft Apps</option>
                    <option value="system">System-wide Apps</option>
                    <option value="user">Current User Apps</option>
                  </AppManagerSelect>
                  <AppManagerSelect value={appSizeFilter} onChange={(value) => setAppSizeFilter(value as InstalledAppSizeFilter)} ariaLabel="Filter app size">
                    <option value="all">All Sizes</option>
                    <option value="large">Large Apps</option>
                    <option value="medium">Medium Apps</option>
                    <option value="small">Small Apps</option>
                    <option value="unknown">Unknown Size</option>
                  </AppManagerSelect>
                  <AppManagerSelect value={sortKey} onChange={(value) => setSortKey(value as typeof sortKey)} ariaLabel="Sort installed apps">
                    <option value="size">Sort by: Size (High to Low)</option>
                    <option value="date">Sort by: Recently Installed</option>
                    <option value="name">Sort by: Name</option>
                    <option value="publisher">Sort by: Publisher</option>
                  </AppManagerSelect>
                </div>

                <div className="mt-3 flex flex-wrap gap-2">
                  {([
                    { key: "all", label: "All", count: appTabCounts.all },
                    { key: "large", label: "Large Apps", count: appTabCounts.large },
                    { key: "recent", label: "Recently Installed", count: appTabCounts.recent },
                    { key: "microsoft", label: "Microsoft Apps", count: appTabCounts.microsoft },
                    { key: "store", label: "Store Apps", count: appTabCounts.store },
                    { key: "desktop", label: "Desktop Apps", count: appTabCounts.desktop },
                  ] as const).map((tab) => (
                    <button
                      key={tab.key}
                      type="button"
                      className={cn(
                        "h-8 rounded-md border px-3 text-sm font-medium transition-colors",
                        appTab === tab.key ? "border-accent bg-accent text-[#08111f]" : "border-border/80 bg-bg-subtle/60 text-secondary hover:border-border-strong hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => setAppTab(tab.key)}
                    >
                      {tab.label} ({tab.count})
                    </button>
                  ))}
                </div>
              </div>

              <div className="overflow-x-auto">
                <div className="min-w-[900px]">
                  <div className="grid grid-cols-[minmax(240px,1.25fr)_minmax(160px,.85fr)_130px_110px_130px_210px] gap-3 border-b border-border/70 bg-bg-subtle/65 px-4 py-2 text-xs font-medium text-muted">
                    <span>App</span>
                    <span>Publisher</span>
                    <span>Version</span>
                    <span>Size</span>
                    <span>Installed On</span>
                    <span>Actions</span>
                  </div>

                  {pagedApps.map((app) => {
                    const selected = selectedApp?.id === app.id
                    return (
                      <div
                        key={app.id}
                        role="button"
                        tabIndex={0}
                        className={cn(
                          "grid min-h-[58px] grid-cols-[minmax(240px,1.25fr)_minmax(160px,.85fr)_130px_110px_130px_210px] items-center gap-3 border-b border-border/60 px-4 py-2.5 transition-colors last:border-b-0 hover:bg-bg-surface-hover/55",
                          selected && "bg-accent/8 ring-1 ring-inset ring-accent/60"
                        )}
                        onClick={() => selectApp(app)}
                        onKeyDown={(event) => {
                          if (event.key === "Enter" || event.key === " ") {
                            event.preventDefault()
                            selectApp(app)
                          }
                        }}
                      >
                        <div className="flex min-w-0 items-center gap-3">
                          <InstalledAppMark app={app} />
                          <div className="min-w-0">
                            <div className="truncate text-sm font-semibold text-primary">{app.displayName}</div>
                            <div className="mt-0.5 truncate text-xs text-secondary">{getInstalledAppKind(app)}</div>
                          </div>
                        </div>
                        <div className="min-w-0 truncate text-sm text-secondary">{app.publisher || "Unknown publisher"}</div>
                        <div className="min-w-0 truncate text-sm text-secondary">{app.version || "Unknown"}</div>
                        <div className="text-sm font-semibold text-primary">{formatInstalledAppSize(app)}</div>
                        <div className="text-sm text-secondary">{formatAppInstallDate(app.installDate)}</div>
                        <div className="flex items-center gap-1.5" onClick={(event) => event.stopPropagation()}>
                          <Button variant="outline" size="sm" onClick={() => selectApp(app)}>Details</Button>
                          <Button variant="destructive" size="sm" onClick={() => setPendingUninstall(app)} disabled={!app.canLaunchUninstaller || isAppManagerBusy}>
                            <Trash2 data-icon="inline-start" />
                            Uninstall
                          </Button>
                          <Button variant="outline" size="icon-sm" aria-label={`Open install location for ${app.displayName}`} title="Open install location" onClick={() => void revealInstallLocation(app)} disabled={!app.installLocation || isAppManagerBusy}>
                            <MoreVertical className="size-4" aria-hidden />
                          </Button>
                        </div>
                      </div>
                    )
                  })}

                  {visibleApps.length === 0 ? (
                    <div className="px-4 py-5 text-sm text-secondary">
                      {appScanError ? "Installed app scan failed." : isScanningApps ? "Scanning installed apps..." : "No apps match these filters."}
                    </div>
                  ) : null}
                </div>
              </div>

              <div className="flex flex-col gap-3 border-t border-border/70 px-4 py-3 text-xs text-muted lg:flex-row lg:items-center lg:justify-between">
                <span>Showing {visibleRangeStart} to {visibleRangeEnd} of {visibleApps.length} app{visibleApps.length === 1 ? "" : "s"}</span>
                <div className="flex items-center gap-1.5">
                  <Button variant="outline" size="sm" disabled={safeAppPageIndex === 0} onClick={() => setAppPageIndex((current) => Math.max(0, current - 1))}>Previous</Button>
                  {buildPaginationLabels(safeAppPageIndex, appPageCount).map((label) => (
                    <button
                      key={label}
                      type="button"
                      disabled={label === "..."}
                      className={cn(
                        "flex h-8 min-w-8 items-center justify-center rounded-md px-2 text-sm font-medium transition-colors disabled:cursor-default",
                        label === String(safeAppPageIndex + 1) ? "bg-accent text-[#08111f]" : label === "..." ? "text-muted" : "text-secondary hover:bg-bg-surface-hover hover:text-primary"
                      )}
                      onClick={() => label !== "..." && setAppPageIndex(Number(label) - 1)}
                    >
                      {label}
                    </button>
                  ))}
                  <Button variant="outline" size="sm" disabled={safeAppPageIndex >= appPageCount - 1} onClick={() => setAppPageIndex((current) => Math.min(appPageCount - 1, current + 1))}>Next</Button>
                </div>
              </div>
            </section>

            {leftoverScan || isScanningLeftovers || leftoverScanError ? (
              <section className="overflow-hidden rounded-md border border-border bg-transparent">
                <div className="flex items-center justify-between gap-3 border-b border-border/70 px-4 py-3">
                  <div className="flex items-center gap-2.5">
                    <Trash2 className="size-4 shrink-0 text-muted" aria-hidden />
                    <span className="font-display text-sm font-semibold tracking-tight text-primary">Leftover Cleanup</span>
                  </div>
                  <Button size="sm" onClick={requestCleanLeftovers} disabled={!canCleanLeftovers}>
                    <Trash2 data-icon="inline-start" />
                    {isCleaningLeftovers ? "Cleaning..." : "Clean Selected"}
                  </Button>
                </div>
                <div className="grid grid-cols-3 divide-x divide-border border-b border-border">
                  <MetricTile label="Recoverable" value={leftoverScanError ? "Scan failed" : leftoverScan?.totalDisplay ?? "..."} />
                  <MetricTile label="Files" value={leftoverScan ? String(leftoverScan.totalFiles) : "0"} />
                  <MetricTile label="Selected" value={String(selectedLeftovers.length)} />
                </div>
                <div className="px-4 pb-3">
                  <WarningList warnings={leftoverWarnings} />
                </div>
                <div className="divide-y divide-border/60">
                  {leftovers.map((category) => {
                    const selected = selectedLeftoverIdSet.has(category.id)
                    return (
                      <label
                        key={category.id}
                        className={cn(
                          "flex cursor-pointer items-start gap-3 px-4 py-3",
                          selected && "bg-bg-surface-hover/40",
                          (isScanningLeftovers || isCleaningLeftovers || !category.isEnabled) && "cursor-not-allowed opacity-60"
                        )}
                      >
                        <Checkbox checked={selected} disabled={!category.isEnabled || isScanningLeftovers || isCleaningLeftovers} aria-label={`Select ${category.group} leftover area`} onCheckedChange={(checked) => setLeftoverSelected(category.id, checked === true)} />
                        <div className="min-w-0 flex-1">
                          <div className="flex items-start justify-between gap-3">
                            <span className="text-sm font-medium text-primary">{category.group}</span>
                            <div className="flex shrink-0 items-center gap-3 text-sm">
                              <span className="text-secondary">{category.fileCount} files</span>
                              <span className="font-semibold text-primary">{category.sizeDisplay}</span>
                            </div>
                          </div>
                          <p className="mt-0.5 text-xs leading-snug text-secondary">{category.description}</p>
                          {category.requiresAdministrator ? <span className="mt-1 inline-block text-[11px] text-amber-400">Requires administrator</span> : null}
                          {category.warnings.length > 0 ? <span className="mt-1 inline-block text-xs text-amber-400">{category.warnings[0]}</span> : null}
                        </div>
                      </label>
                    )
                  })}
                  {leftovers.length === 0 ? (
                    <div className="px-4 py-3 text-sm text-secondary">
                      {leftoverScanError ? "Leftover scan failed." : isScanningLeftovers ? "Scanning leftovers..." : "No leftovers found."}
                    </div>
                  ) : null}
                </div>
                {leftoversNeedAdministrator && !isAdministrator ? (
                  <div className="border-t border-border/70 px-4 py-3">
                    <Button variant="secondary" size="sm" onClick={onRestartAsAdministrator}>
                      <ShieldAlert data-icon="inline-start" />
                      Restart as Administrator
                    </Button>
                  </div>
                ) : null}
              </section>
            ) : null}
          </div>

          <InstalledAppDetailsPanel
            app={selectedApp}
            warning={uninstallerNotice || appActionError}
            isBusy={isAppManagerBusy}
            isScanningLeftovers={isScanningLeftovers}
            onUninstall={() => selectedApp && setPendingUninstall(selectedApp)}
            onReveal={() => selectedApp && void revealInstallLocation(selectedApp)}
            onCopy={() => selectedApp && void copyAppDetails(selectedApp)}
            onScanLeftovers={() => selectedApp && void scanLeftovers(selectedApp)}
          />
        </div>
      )}

      <AlertDialog open={Boolean(pendingUninstall)} onOpenChange={(open) => !open && setPendingUninstall(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Launch app uninstaller?</AlertDialogTitle>
            <AlertDialogDescription>
              FileLocker will open the vendor uninstall command for {pendingUninstall?.displayName ?? "this app"}. It will not run a silent uninstall.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isScanningApps || isCleaningLeftovers || isLaunchingUninstaller}>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" disabled={isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller} onClick={() => pendingUninstall && void launchUninstaller(pendingUninstall)}>
              {isLaunchingUninstaller ? "Opening..." : "Launch Uninstaller"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
      <MaintenanceConfirmDialog
        open={showLeftoverConfirmation}
        onOpenChange={setShowLeftoverConfirmation}
        title="Clean selected app leftovers?"
        description={`FileLocker will delete ${selectedLeftovers.length} selected AppData or ProgramData cleanup area${selectedLeftovers.length === 1 ? "" : "s"}. Program Files and Windows folders are excluded.`}
        confirmLabel="Clean Selected"
        onConfirm={() => void cleanLeftovers()}
        onDontShowAgain={() => { setSkipLeftoverConfirmation(true); void cleanLeftovers() }}
        isBusy={isCleaningLeftovers}
      />
    </MaintenanceFrame>
  )
}

function InstalledAppSummaryCard({ label, value, description, icon, tone }: { label: string; value: string; description: string; icon: React.ReactNode; tone: "blue" | "green" }) {
  return (
    <div className="rounded-md border border-border bg-transparent px-4 py-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-sm font-medium text-secondary">{label}</div>
          <div className="mt-1 font-display text-2xl font-semibold tracking-tight text-primary">{value}</div>
          <div className="mt-1 text-sm text-secondary">{description}</div>
        </div>
        <div className={cn(
          "flex size-11 shrink-0 items-center justify-center rounded-md border",
          tone === "green" ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : "border-accent/30 bg-accent/12 text-accent"
        )}>
          {icon}
        </div>
      </div>
    </div>
  )
}

function AppManagerSelect({ value, onChange, ariaLabel, children }: { value: string; onChange: (value: string) => void; ariaLabel: string; children: React.ReactNode }) {
  return (
    <select
      className="h-9 min-w-[150px] rounded-md border border-border/80 bg-bg-subtle px-3 text-sm text-primary outline-none transition-colors focus:border-accent focus:ring-2 focus:ring-accent/20"
      value={value}
      aria-label={ariaLabel}
      onChange={(event) => onChange(event.target.value)}
    >
      {children}
    </select>
  )
}

function InstalledAppDetailsPanel({
  app,
  warning,
  isBusy,
  isScanningLeftovers,
  onUninstall,
  onReveal,
  onCopy,
  onScanLeftovers,
}: {
  app: InstalledApp | null
  warning: string
  isBusy: boolean
  isScanningLeftovers: boolean
  onUninstall: () => void
  onReveal: () => void
  onCopy: () => void
  onScanLeftovers: () => void
}) {
  return (
    <aside className="min-w-0 overflow-hidden rounded-md border border-border bg-transparent xl:sticky xl:top-4 xl:self-start">
      <div className="flex items-center justify-between gap-3 border-b border-border/70 px-4 py-3">
        <div className="font-display text-sm font-semibold tracking-tight text-primary">App Details</div>
        <Info className="size-4 text-muted" aria-hidden />
      </div>

      {!app ? (
        <div className="px-4 py-8 text-sm text-secondary">Select an installed app to view its details.</div>
      ) : (
        <>
          <div className="border-b border-border/70 px-4 py-4">
            <div className="flex items-center gap-3">
              <InstalledAppMark app={app} />
              <div className="min-w-0">
                <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{app.displayName}</div>
                <div className="mt-0.5 text-xs text-secondary">{getInstalledAppKind(app)}</div>
              </div>
            </div>
          </div>

          <div className="divide-y divide-border/65 px-4">
            <InstalledAppDetailRow label="Publisher" value={app.publisher || "Unknown publisher"} />
            <InstalledAppDetailRow label="Version" value={app.version || "Unknown"} />
            <InstalledAppDetailRow label="Size" value={formatInstalledAppSize(app)} />
            <InstalledAppDetailRow label="Installed On" value={formatAppInstallDate(app.installDate)} />
            <InstalledAppDetailRow label="Install Location" value={app.installLocation || "No install location published"} mono />
            <InstalledAppDetailRow label="Install Source" value={getInstalledAppInstallSource(app)} />
            <InstalledAppDetailRow label="Uninstall Command" value={app.uninstallCommand || "No visible uninstall command"} mono />
            <InstalledAppDetailRow label="Registry Entry" value={app.registryKeyPath || "Unknown"} mono />
          </div>

          <div className="border-t border-border/70 px-4 py-3">
            <div className="grid gap-2">
              <Button variant="destructive" onClick={onUninstall} disabled={!app.canLaunchUninstaller || isBusy}>
                <Trash2 data-icon="inline-start" />
                Uninstall
              </Button>
              <Button variant="outline" onClick={onReveal} disabled={!app.installLocation || isBusy}>
                <FolderOpen data-icon="inline-start" />
                Open Install Location
              </Button>
              <Button variant="outline" onClick={onCopy}>
                <Copy data-icon="inline-start" />
                Copy Details
              </Button>
              <Button variant="outline" onClick={onScanLeftovers} disabled={isBusy}>
                <ScanLine data-icon="inline-start" />
                {isScanningLeftovers ? "Scanning Leftovers..." : "Scan Leftovers"}
              </Button>
            </div>

            {warning ? (
              <div className="app-inline-notice app-inline-notice-warning mt-3">
                <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                <span>{warning}</span>
              </div>
            ) : (
              <div className="mt-3 rounded-md border border-accent-green/25 bg-accent-green/10 px-3 py-3 text-sm text-secondary">
                <div className="font-display text-sm font-semibold tracking-tight text-accent-green">This app looks safe</div>
                <div className="mt-1">No issues were detected in the current scan.</div>
              </div>
            )}
          </div>
        </>
      )}
    </aside>
  )
}

function InstalledAppDetailRow({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="grid gap-1 py-3">
      <div className="text-xs text-muted">{label}</div>
      <div className={cn("min-w-0 break-words text-sm text-secondary", mono && "break-all font-mono text-xs")}>{value}</div>
    </div>
  )
}

function InstalledAppMark({ app }: { app: InstalledApp }) {
  const token = app.displayName.trim().slice(0, 2).toUpperCase() || "AP"
  if (app.iconDataUri) {
    return (
      <div className="flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-md border border-border/60 bg-bg-subtle/40" aria-hidden>
        <img src={app.iconDataUri} alt="" className="size-5 object-contain" loading="lazy" decoding="async" />
      </div>
    )
  }
  return (
    <div className={cn("flex size-8 shrink-0 items-center justify-center rounded-md border font-display text-xs font-semibold", getInstalledAppMarkClass(app))} aria-hidden>
      {token}
    </div>
  )
}

function buildInstalledAppStats(apps: InstalledApp[]) {
  return apps.reduce(
    (stats, app) => {
      if (app.estimatedSizeBytes > 0) {
        stats.totalKnownSizeBytes += app.estimatedSizeBytes
      }
      if (isLargeInstalledApp(app)) {
        stats.largeCount += 1
      }
      return stats
    },
    { totalKnownSizeBytes: 0, largeCount: 0 }
  )
}

function buildInstalledAppTabCounts(apps: InstalledApp[]) {
  return {
    all: apps.length,
    large: apps.filter(isLargeInstalledApp).length,
    recent: apps.filter(isRecentlyInstalledApp).length,
    microsoft: apps.filter(isMicrosoftInstalledApp).length,
    store: apps.filter((app) => getInstalledAppKind(app) === "Store App").length,
    desktop: apps.filter((app) => getInstalledAppKind(app) === "Desktop App").length,
  }
}

function filterInstalledAppsByView(apps: InstalledApp[], filters: { query: string; tab: InstalledAppTab; typeFilter: InstalledAppTypeFilter; sizeFilter: InstalledAppSizeFilter }) {
  return filterInstalledApps(apps, filters.query)
    .filter((app) => {
      if (filters.tab === "large" && !isLargeInstalledApp(app)) return false
      if (filters.tab === "recent" && !isRecentlyInstalledApp(app)) return false
      if (filters.tab === "microsoft" && !isMicrosoftInstalledApp(app)) return false
      if (filters.tab === "store" && getInstalledAppKind(app) !== "Store App") return false
      if (filters.tab === "desktop" && getInstalledAppKind(app) !== "Desktop App") return false
      return true
    })
    .filter((app) => {
      if (filters.typeFilter === "desktop") return getInstalledAppKind(app) === "Desktop App"
      if (filters.typeFilter === "microsoft") return isMicrosoftInstalledApp(app)
      if (filters.typeFilter === "system") return app.sourceHive === "HKLM"
      if (filters.typeFilter === "user") return app.sourceHive === "HKCU"
      return true
    })
    .filter((app) => {
      if (filters.sizeFilter === "large") return isLargeInstalledApp(app)
      if (filters.sizeFilter === "medium") return app.estimatedSizeBytes >= 100 * 1024 * 1024 && app.estimatedSizeBytes < 1024 * 1024 * 1024
      if (filters.sizeFilter === "small") return app.estimatedSizeBytes > 0 && app.estimatedSizeBytes < 100 * 1024 * 1024
      if (filters.sizeFilter === "unknown") return app.estimatedSizeBytes <= 0
      return true
    })
}

function buildPaginationLabels(pageIndex: number, pageCount: number) {
  if (pageCount <= 5) {
    return Array.from({ length: pageCount }, (_, index) => String(index + 1))
  }

  const labels = new Set<number>([0, pageIndex, pageCount - 1])
  if (pageIndex > 0) labels.add(pageIndex - 1)
  if (pageIndex < pageCount - 1) labels.add(pageIndex + 1)
  const sorted = Array.from(labels).sort((a, b) => a - b)
  return sorted.flatMap((page, index) => index > 0 && page - sorted[index - 1] > 1 ? ["...", String(page + 1)] : [String(page + 1)])
}

function formatInstalledAppDetails(app: InstalledApp) {
  return [
    `Name: ${app.displayName}`,
    `Publisher: ${app.publisher || "Unknown publisher"}`,
    `Version: ${app.version || "Unknown"}`,
    `Size: ${formatInstalledAppSize(app)}`,
    `Installed On: ${formatAppInstallDate(app.installDate)}`,
    `Install Location: ${app.installLocation || "No install location published"}`,
    `Install Source: ${getInstalledAppInstallSource(app)}`,
    `Uninstall Command: ${app.uninstallCommand || "No visible uninstall command"}`,
    `Registry Entry: ${app.registryKeyPath || "Unknown"}`,
  ].join("\n")
}

function formatInstalledAppListExport(apps: InstalledApp[]) {
  return [
    ["Name", "Publisher", "Version", "Size", "Installed On", "Install Location", "Registry Entry"].join("\t"),
    ...apps.map((app) => [
      app.displayName,
      app.publisher || "Unknown publisher",
      app.version || "Unknown",
      formatInstalledAppSize(app),
      formatAppInstallDate(app.installDate),
      app.installLocation || "",
      app.registryKeyPath || "",
    ].map((value) => value.replaceAll("\t", " ")).join("\t")),
  ].join("\n")
}

function getInstalledAppKind(app: InstalledApp) {
  return /appx|windowsapps|msix/i.test(`${app.registryKeyPath} ${app.installLocation}`) ? "Store App" : "Desktop App"
}

function getInstalledAppInstallSource(app: InstalledApp) {
  if (getInstalledAppKind(app) === "Store App") return "Microsoft Store"
  return app.sourceHive === "HKLM" ? "System-wide registry" : "Current user registry"
}

function getInstalledAppMarkClass(app: InstalledApp) {
  if (isMicrosoftInstalledApp(app)) return "border-accent/35 bg-accent/12 text-accent"
  if (isLargeInstalledApp(app)) return "border-amber-400/35 bg-amber-400/12 text-amber-200"
  return "border-border-strong bg-bg-subtle text-primary"
}

function isLargeInstalledApp(app: InstalledApp) {
  return app.estimatedSizeBytes >= 1024 * 1024 * 1024
}

function isMicrosoftInstalledApp(app: InstalledApp) {
  return /microsoft|windows/i.test(`${app.publisher} ${app.displayName}`)
}

function isRecentlyInstalledApp(app: InstalledApp) {
  const time = Date.parse(app.installDate)
  if (Number.isNaN(time)) return false
  return Date.now() - time <= 30 * 24 * 60 * 60 * 1000
}

function formatInstalledAppSize(app: InstalledApp) {
  return app.estimatedSizeBytes > 0 ? app.estimatedSizeDisplay : "Unknown"
}

function formatAppInstallDate(value: string) {
  return value || "Unknown"
}

function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B"
  const units = ["B", "KB", "MB", "GB", "TB"]
  let value = bytes
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }
  return `${value >= 10 || unitIndex === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[unitIndex]}`
}

function AdminStatusBanner({
  isAdministrator,
  onRestartAsAdministrator,
  description,
}: {
  isAdministrator: boolean
  onRestartAsAdministrator: () => void
  description: string
}) {
  if (isAdministrator) return null

  return (
    <div className="border-y border-amber-400/30 bg-amber-400/8 px-3 py-2 text-sm leading-snug text-secondary">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex min-w-0 flex-1 items-start gap-2.5">
          <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <div className="min-w-0 flex-1">
            <div className="font-display text-sm font-semibold tracking-tight text-primary">Administrator access required</div>
            <div className="mt-0.5">{description}</div>
          </div>
        </div>
        <Button variant="secondary" size="sm" onClick={onRestartAsAdministrator} className="shrink-0 self-start sm:self-center">
          <ShieldAlert data-icon="inline-start" />
          Restart as Administrator
        </Button>
      </div>
    </div>
  )
}

function MaintenanceConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  onConfirm,
  onDontShowAgain,
  isBusy = false,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description: string
  confirmLabel: string
  onConfirm: () => void
  onDontShowAgain: () => void
  isBusy?: boolean
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent className="sm:max-w-md">
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription className="leading-[1.6]">{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter className="sm:flex-wrap">
          <AlertDialogCancel disabled={isBusy}>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="secondary" disabled={isBusy} onClick={onDontShowAgain}>Don't show again</AlertDialogAction>
          <AlertDialogAction variant="destructive" disabled={isBusy} onClick={onConfirm}>{confirmLabel}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

function MaintenanceFrame({ children }: { children: React.ReactNode }) {
  return (
    <div className="security-page">
      <div className="flex flex-col gap-3">{children}</div>
    </div>
  )
}

function DrivePicker({
  drives,
  selectedDriveRoot,
  onSelect,
  isLoading,
  loadError,
  onRefresh,
}: {
  drives: MaintenanceDrive[]
  selectedDriveRoot: string
  onSelect: (root: string) => void
  isLoading: boolean
  loadError: string
  onRefresh: () => void
}) {
  return (
    <section className="section-surface">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2.5">
          <HardDrive className="size-4 shrink-0 text-muted" aria-hidden />
          <span className="font-display text-sm font-semibold tracking-tight text-primary">Drive Selection</span>
        </div>
        <Button variant="outline" size="sm" onClick={onRefresh} disabled={isLoading}>
          <RefreshCcw data-icon="inline-start" />
          Refresh
        </Button>
      </div>
      {loadError ? (
        <div className="app-inline-notice app-inline-notice-warning mt-3">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <span>{loadError}</span>
        </div>
      ) : null}
      <div className="mt-3 app-list-surface">
        {drives.map((drive) => {
          const selected = drive.rootPath === selectedDriveRoot
          return (
            <button
              key={drive.id}
              type="button"
              className={cn(
                "app-list-row w-full text-left flex items-center justify-between gap-3",
                selected && "bg-accent/8",
                !drive.isReady && "cursor-not-allowed opacity-50"
              )}
              onClick={() => onSelect(drive.rootPath)}
              aria-pressed={selected}
              disabled={!drive.isReady}
            >
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-primary">{drive.name}</span>
                  <Badge variant={selected ? "secondary" : "outline"} className="text-xs">{drive.driveType}</Badge>
                </div>
                <div className="mt-1 h-1 w-full overflow-hidden rounded-sm bg-border/60">
                  <div className="h-full bg-accent/60" style={{ width: `${getDriveUsedPercent(drive)}%` }} />
                </div>
              </div>
              <div className="shrink-0 text-right">
                <div className="text-xs text-secondary">{drive.freeSpaceDisplay} free</div>
                <div className="text-xs text-muted">{drive.totalSizeDisplay} · {drive.driveFormat}</div>
              </div>
            </button>
          )
        })}
        {drives.length === 0 ? (
          <div className="px-3 py-3 text-sm text-secondary">
            {loadError ? "Drive list could not be loaded." : isLoading ? "Loading drives..." : "No fixed or removable drives are available."}
          </div>
        ) : null}
      </div>
    </section>
  )
}

function MetricTile({ label, value, good = false }: { label: string; value: string; good?: boolean }) {
  return (
    <div className="px-3 py-2.5">
      <div className="text-[11px] font-medium tracking-[0.01em] text-muted">{label}</div>
      <div className={cn("mt-1 truncate font-display text-base font-semibold tracking-tight", good ? "text-accent-green" : "text-primary")}>
        {value}
      </div>
    </div>
  )
}

function WarningList({ warnings }: { warnings: string[] }) {
  const unique = Array.from(new Set(warnings.map((w) => w.trim()).filter(Boolean))).slice(0, 4)
  if (unique.length === 0) return null
  return (
    <div className="app-inline-notice app-inline-notice-warning mt-3" role="status" aria-live="polite">
      <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
      <div className="flex flex-col gap-1">
        {unique.map((w) => <span key={w}>{w}</span>)}
      </div>
    </div>
  )
}

function ToolOutput({ result, runningLabel, errorMessage }: { result: MaintenanceToolResult | null; runningLabel?: string; errorMessage?: string }) {
  if (!result && !runningLabel && !errorMessage) return null

  return (
    <section className="section-surface">
      <div className="flex items-center gap-2.5">
        <Download className="size-4 shrink-0 text-muted" aria-hidden />
        <span className="font-display text-sm font-semibold tracking-tight text-primary">Output</span>
      </div>
      <div className="mt-3 flex flex-col gap-3">
        {runningLabel ? (
          <div className="app-inline-notice app-inline-notice-info">
            <Info className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden />
            <span>{runningLabel}</span>
          </div>
        ) : null}
        {errorMessage ? (
          <div className="app-inline-notice app-inline-notice-warning">
            <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
            <span>{errorMessage}</span>
          </div>
        ) : null}
        {result ? (
          <>
            <div className="flex items-center justify-between gap-3">
              <span className="text-sm text-secondary">{result.message}</span>
              <Badge variant={result.ok ? "secondary" : "destructive"}>{result.ok ? "Completed" : "Needs review"}</Badge>
            </div>
            <pre className="terminal-output max-h-[360px]">{result.output || "No command output was returned."}</pre>
          </>
        ) : null}
      </div>
    </section>
  )
}

function useSelectedDrive(drives: MaintenanceDrive[], selectedDriveRoot: string) {
  return useMemo(
    () => drives.find((d) => d.rootPath.toLowerCase() === selectedDriveRoot.toLowerCase()) ?? drives.find((d) => d.isReady) ?? null,
    [drives, selectedDriveRoot]
  )
}

function filterInstalledApps(apps: InstalledApp[], query: string) {
  const normalized = query.trim().toLowerCase()
  if (!normalized) return apps
  return apps.filter((app) =>
    app.displayName.toLowerCase().includes(normalized) ||
    app.publisher.toLowerCase().includes(normalized) ||
    app.version.toLowerCase().includes(normalized)
  )
}

function sortInstalledApps(apps: InstalledApp[], sortKey: "name" | "publisher" | "size" | "date") {
  const sorted = [...apps]
  sorted.sort((a, b) => {
    if (sortKey === "size") return getInstalledAppSizeBytes(b) - getInstalledAppSizeBytes(a) || a.displayName.localeCompare(b.displayName)
    if (sortKey === "date") return (b.installDate || "").localeCompare(a.installDate || "") || a.displayName.localeCompare(b.displayName)
    if (sortKey === "publisher") return (a.publisher || "").localeCompare(b.publisher || "") || a.displayName.localeCompare(b.displayName)
    return a.displayName.localeCompare(b.displayName)
  })
  return sorted
}

function getInstalledAppSizeBytes(app: InstalledApp) {
  return Number.isFinite(app.estimatedSizeBytes) && app.estimatedSizeBytes > 0 ? app.estimatedSizeBytes : 0
}

function getUninstallerNotice(app: InstalledApp) {
  if (!app.uninstallCommand) return "This app does not publish a vendor uninstall command."
  if (!app.canLaunchUninstaller) return "This uninstall command appears to include quiet or silent switches, so FileLocker will not launch it."
  return ""
}

function useStoredBoolean(key: string) {
  const [value, setValue] = useState(() => {
    try { return window.localStorage.getItem(key) === "true" } catch { return false }
  })

  function updateValue(nextValue: boolean) {
    setValue(nextValue)
    try { window.localStorage.setItem(key, nextValue ? "true" : "false") } catch { }
  }

  return [value, updateValue] as const
}

function readStoredStringArray(key: string) {
  try {
    const raw = window.localStorage.getItem(key)
    if (!raw || raw.length > MAX_STORED_STRING_ARRAY_JSON_CHARS) return []
    const value = raw ? JSON.parse(raw) : []
    return normalizeStoredStringArray(value)
  } catch { return [] }
}

function writeStoredStringArray(key: string, value: string[]) {
  try { window.localStorage.setItem(key, JSON.stringify(normalizeStoredStringArray(value))) } catch { }
}

function normalizeStoredStringArray(value: unknown) {
  if (!Array.isArray(value)) return []
  const next: string[] = []
  const seen = new Set<string>()
  for (const item of value) {
    if (typeof item !== "string") continue
    const trimmed = item.trim()
    if (trimmed.length === 0 || trimmed.length > 200 || seen.has(trimmed)) continue
    seen.add(trimmed)
    next.push(trimmed)
    if (next.length >= 100) break
  }
  return next
}

async function loadDrives(
  invoke: BridgeInvoke,
  setDrives: (drives: MaintenanceDrive[]) => void,
  setSelectedDriveRoot: React.Dispatch<React.SetStateAction<string>>,
  setIsLoading: (value: boolean) => void,
  setLoadError: (value: string) => void
) {
  setIsLoading(true)
  setLoadError("")
  try {
    const response = await invoke<MaintenanceDriveList>("maintenance.getDrives", {})
    setDrives(response.drives)
    setSelectedDriveRoot((current) => {
      if (current && response.drives.some((d) => d.rootPath.toLowerCase() === current.toLowerCase())) return current
      return response.drives.find((d) => d.isReady)?.rootPath || response.drives[0]?.rootPath || ""
    })
  } catch (error) {
    const message = error instanceof Error ? error.message : "Unable to load drives."
    setLoadError(message)
    toast.error(message)
  } finally {
    setIsLoading(false)
  }
}

function getDriveUsedPercent(drive: MaintenanceDrive) {
  const totalSizeBytes = Number.isFinite(drive.totalSizeBytes) ? drive.totalSizeBytes : 0
  if (totalSizeBytes <= 0) return 0

  const freeSpaceBytes = Number.isFinite(drive.freeSpaceBytes) ? drive.freeSpaceBytes : 0
  const usedBytes = Math.max(0, Math.min(totalSizeBytes, totalSizeBytes - freeSpaceBytes))
  return Math.max(0, Math.min(100, (usedBytes / totalSizeBytes) * 100))
}

function showMaintenanceError(error: unknown, fallback: string) {
  const message = error instanceof Error ? error.message : fallback
  const permissionRelated = /administrator|elevat|access is denied|insufficient/i.test(message)
  const alreadyActionable = /restart .*administrator|use restart as administrator/i.test(message)
  const displayMessage = permissionRelated && !alreadyActionable ? `${message} Restart FileLocker as Administrator and try again.` : message
  toast.error(displayMessage)
  return displayMessage
}
