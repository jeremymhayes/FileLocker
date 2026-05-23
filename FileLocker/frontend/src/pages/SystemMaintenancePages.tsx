import { useEffect, useMemo, useRef, useState } from "react"
import {
  AlertTriangle,
  ArrowUpDown,
  CheckCircle2,
  Database,
  Download,
  ExternalLink,
  FolderOpen,
  HardDrive,
  Info,
  Package,
  Play,
  Power,
  PowerOff,
  RefreshCcw,
  ScanLine,
  Search,
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
import { Section, SectionBody, SectionFooter, SectionHeader, SectionTitle } from "@/components/layout/Workspace"
import { Checkbox } from "@/components/ui/checkbox"
import { Input } from "@/components/ui/input"
import { cn } from "@/lib/utils"
import type {
  AppLeftoverCleanResult,
  AppLeftoverScanResult,
  InstalledApp,
  InstalledAppsScanResult,
  StartupItem,
  StartupScanResult,
  StartupToggleResult,
  UninstallerLaunchResult,
} from "@/types/bridge"

type BridgeInvoke = <T>(action: string, payload?: unknown) => Promise<T>

type MaintenancePageProps = {
  invoke: BridgeInvoke
  isAdministrator: boolean
  onRestartAsAdministrator: () => void
}

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
    if (isRunning) {
      toast.error("Wait for the current free-space wipe to finish before changing drives.")
      return
    }

    setSelectedDriveRoot(root)
    setWipeError("")
  }

  function refreshDrives() {
    if (isRunning) {
      return
    }

    setWipeError("")
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }

  function requestWipe() {
    if (isRunning) {
      return
    }

    if (!isAdministrator) {
      toast.error("Restart FileLocker as administrator before starting a free-space wipe.")
      return
    }

    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    if (!selectedDrive.isReady) {
      toast.error("Select a ready drive before starting a free-space wipe.")
      return
    }

    if (skipWipeConfirmation) {
      void runWipe()
      return
    }

    setShowWipeConfirmation(true)
  }

  async function runWipe() {
    if (isRunning) {
      return
    }

    if (!isAdministrator) {
      toast.error("Restart FileLocker as administrator before starting a free-space wipe.")
      return
    }

    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    if (!selectedDrive.isReady) {
      toast.error("Select a ready drive before starting a free-space wipe.")
      return
    }

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
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_370px]">
        <div className="flex min-w-0 flex-col gap-4">
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

          <MaintenanceSection title="Free-Space Wipe" icon={HardDrive}>
            <div className="grid gap-4 md:grid-cols-3">
              <MetricTile label="Selected drive" value={selectedDrive?.rootPath ?? "None"} />
              <MetricTile label="Free space" value={selectedDrive?.freeSpaceDisplay ?? "Unknown"} />
              <MetricTile label="Format" value={selectedDrive?.driveFormat ?? "Unknown"} />
            </div>
            <div className="mt-4 rounded-md border border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
              <div className="flex items-start gap-2.5">
                <ShieldAlert className="mt-0.5 size-4 text-amber-400" aria-hidden />
                <span>This wipes free space with Windows cipher so deleted data is harder to recover. It does not rewrite the live partition table or delete existing files.</span>
              </div>
            </div>
            <div className="mt-4 flex flex-wrap gap-2">
              <Button variant="destructive" onClick={requestWipe} disabled={!canStart}>
                <Play data-icon="inline-start" />
                {isRunning ? "Wiping" : "Start Wipe"}
              </Button>
            </div>
          </MaintenanceSection>

          <ToolOutput result={result} runningLabel={isRunning ? "Windows cipher is running. This can take a long time on large drives." : undefined} errorMessage={wipeError} />
        </div>

        <SafetyPanel
          title="Partition Cleaner Guardrails"
          points={[
            "Uses free-space wiping instead of partition-table rewriting.",
            "Keeps live files in place and targets only recoverable deleted-file traces.",
            "May be less complete on SSDs because wear leveling can move physical blocks.",
            "Can run for a long time and should not be interrupted unless necessary.",
          ]}
        />
      </div>
      <MaintenanceConfirmDialog
        open={showWipeConfirmation}
        onOpenChange={setShowWipeConfirmation}
        title="Start free-space wipe?"
        description={`This will run Windows cipher against ${selectedDrive?.rootPath ?? "the selected drive"} and can take a long time. Existing files are kept in place, but deleted-file traces in free space are overwritten where Windows allows it.`}
        confirmLabel="Start Wipe"
        onConfirm={() => void runWipe()}
        onDontShowAgain={() => {
          setSkipWipeConfirmation(true)
          void runWipe()
        }}
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
    if (isRunning) {
      toast.error("Wait for the current drive operation to finish before changing drives.")
      return
    }

    setSelectedDriveRoot(root)
    setOptimizationError("")
  }

  function refreshDrives() {
    if (isRunning) {
      return
    }

    setOptimizationError("")
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading, setDriveLoadError)
  }

  async function runOptimization(mode: "analyze" | "optimize") {
    if (isRunning) {
      return
    }

    if (!isAdministrator) {
      toast.error("Restart FileLocker as administrator before running drive optimization.")
      return
    }

    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    if (!selectedDrive.isReady) {
      toast.error("Select a ready drive before running drive optimization.")
      return
    }

    setIsRunning(true)
    setResult(null)
    setOptimizationError("")
    try {
      const response = await invoke<MaintenanceToolResult>("maintenance.optimizeDrive", {
        driveRoot: selectedDrive.rootPath,
        mode,
      })
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
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_370px]">
        <div className="flex min-w-0 flex-col gap-4">
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

          <MaintenanceSection title="Windows Drive Optimization" icon={RefreshCcw}>
            <div className="grid gap-4 md:grid-cols-3">
              <MetricTile label="Selected drive" value={selectedDrive?.rootPath ?? "None"} />
              <MetricTile label="Total size" value={selectedDrive?.totalSizeDisplay ?? "Unknown"} />
              <MetricTile label="Drive type" value={selectedDrive?.driveType ?? "Unknown"} />
            </div>
            <div className="mt-4 flex flex-wrap gap-2">
              <Button variant="secondary" onClick={() => void runOptimization("analyze")} disabled={!isAdministrator || !selectedDrive || isRunning}>
                <ScanLine data-icon="inline-start" />
                Analyze
              </Button>
              <Button onClick={() => void runOptimization("optimize")} disabled={!isAdministrator || !selectedDrive || isRunning}>
                <Wrench data-icon="inline-start" />
                Optimize / Trim
              </Button>
            </div>
          </MaintenanceSection>

          <ToolOutput result={result} runningLabel={isRunning ? "Windows defrag is running with the selected mode." : undefined} errorMessage={optimizationError} />
        </div>

        <SafetyPanel
          title="Drive Optimizer Notes"
          points={[
            "Analysis uses Windows defrag /A.",
            "Optimization uses Windows defrag /O so Windows can choose defrag or trim based on the device.",
            "Some operations may require elevation or may report that no action is needed.",
            "SSD optimization is not forced into old-style defragmentation.",
          ]}
        />
      </div>
    </MaintenanceFrame>
  )
}

export function CustomCleanPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<CleanupScanResult | null>(null)
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [activeGroup, setActiveGroup] = useState("All")
  const [showCleanupConfirmation, setShowCleanupConfirmation] = useState(false)
  const [skipCleanupConfirmation, setSkipCleanupConfirmation] = useStoredBoolean("filelocker.skipCustomCleanConfirmation")
  const [isScanning, setIsScanning] = useState(true)
  const [cleanupScanError, setCleanupScanError] = useState("")
  const [cleanupRunError, setCleanupRunError] = useState("")
  const [isCleaning, setIsCleaning] = useState(false)
  const [lastRun, setLastRun] = useState<CleanupRunResult | null>(null)
  const [savedSelectionLoaded, setSavedSelectionLoaded] = useState(false)
  const categories = scan?.categories ?? []
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds])
  const selectedCategories = categories.filter((category) => selectedIdSet.has(category.id))
  const cleanupNeedsAdministrator = selectedCategories.some((category) => category.requiresAdministrator)
  const canRunCleanup = selectedIds.length > 0 && !isScanning && !isCleaning && (!cleanupNeedsAdministrator || isAdministrator)
  const groupSummaries = useMemo(() => buildCleanupGroupSummaries(categories, selectedIdSet), [categories, selectedIdSet])
  const visibleCategories = activeGroup === "All"
    ? categories
    : categories.filter((category) => category.group === activeGroup)
  const cleanupWarnings = useMemo(
    () => [
      ...(cleanupScanError ? [cleanupScanError] : []),
      ...(cleanupRunError ? [cleanupRunError] : []),
      ...(lastRun?.warnings ?? []),
    ],
    [cleanupRunError, cleanupScanError, lastRun]
  )

  useEffect(() => {
    void scanCleanup()
  }, [])

  async function scanCleanup(options: { preserveLastRun?: boolean } = {}) {
    if ((isScanning && scan !== null) || (isCleaning && !options.preserveLastRun)) {
      return
    }

    setIsScanning(true)
    setCleanupScanError("")
    setCleanupRunError("")
    if (!options.preserveLastRun) {
      setLastRun(null)
    }

    try {
      const response = await invoke<CleanupScanResult>("maintenance.scanCleanup", {})
      setScan(response)
      const saved = savedSelectionLoaded ? [] : readStoredStringArray("filelocker.customCleanSelection")
      if (!savedSelectionLoaded) {
        setSavedSelectionLoaded(true)
      }
      setSelectedIds((current) => {
        if (current.length > 0) {
          return current.filter((id) => response.categories.some((category) => category.id === id))
        }

        if (saved.length > 0) {
          return saved.filter((id) => response.categories.some((category) => category.id === id))
        }

        return response.categories.filter((category) => category.defaultSelected && category.isEnabled).map((category) => category.id)
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
    if (isScanning || isCleaning) {
      return
    }

    updateSelectedIds((current) => selected
      ? Array.from(new Set([...current, categoryId]))
      : current.filter((id) => id !== categoryId))
  }

  function selectGroup(group: string, selected: boolean) {
    if (isScanning || isCleaning) {
      return
    }

    const groupIds = categories
      .filter((category) => group === "All" || category.group === group)
      .filter((category) => category.isEnabled)
      .map((category) => category.id)
    const groupIdSet = new Set(groupIds)

    updateSelectedIds((current) => selected
      ? Array.from(new Set([...current, ...groupIds]))
      : current.filter((id) => !groupIdSet.has(id)))
  }

  function requestCleanup() {
    if (isScanning || isCleaning) {
      return
    }

    if (selectedIds.length === 0) {
      toast.error("Select at least one cleanup category.")
      return
    }

    if (cleanupNeedsAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before cleaning selected protected areas.")
      return
    }

    if (skipCleanupConfirmation) {
      void runCleanup()
      return
    }

    setShowCleanupConfirmation(true)
  }

  async function runCleanup() {
    if (isScanning || isCleaning) {
      return
    }

    if (selectedIds.length === 0) {
      toast.error("Select at least one cleanup category.")
      return
    }

    if (cleanupNeedsAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before cleaning selected protected areas.")
      return
    }

    setShowCleanupConfirmation(false)
    setIsCleaning(true)
    setCleanupRunError("")
    try {
      const response = await invoke<CleanupRunResult>("maintenance.runCleanup", { categoryIds: selectedIds, confirmation: "CLEAN SELECTED" })
      setLastRun(response)
      setCleanupRunError("")
      await scanCleanup({ preserveLastRun: true })
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

  return (
    <MaintenanceFrame>
      <div className="flex min-h-[calc(100vh-7rem)] flex-col gap-4">
        {cleanupNeedsAdministrator && !isAdministrator ? (
          <AdminStatusBanner
            isAdministrator={isAdministrator}
            onRestartAsAdministrator={onRestartAsAdministrator}
            description="Some cleanup locations need administrator access. Restart as administrator to include protected Windows areas."
          />
        ) : null}

        <div className="grid min-h-0 gap-4 xl:grid-cols-[300px_minmax(0,1fr)]">
          <aside className="flex min-w-0 flex-col gap-4">
          <MaintenanceSection title="Custom Clean" icon={CheckCircle2}>
            <div className="grid gap-3">
              <MetricTile label="Recoverable" value={cleanupScanError ? "Scan failed" : scan?.totalDisplay ?? (isScanning ? "Scanning" : "Not scanned")} />
              <MetricTile label="Selected areas" value={String(selectedIds.length)} />
              <MetricTile label="Last cleaned" value={lastRun?.freedDisplay ?? "Not run"} good={Boolean(lastRun?.freedBytes)} />
            </div>
            <WarningList title={cleanupScanError ? "Cleanup scan issue" : cleanupRunError ? "Cleanup issue" : "Cleanup warnings"} warnings={cleanupWarnings} />
            <div className="mt-4 flex flex-wrap gap-2">
              <Button variant="secondary" onClick={() => void scanCleanup()} disabled={isScanning || isCleaning}>
                <ScanLine data-icon="inline-start" />
                {isScanning ? "Scanning" : "Scan"}
              </Button>
              <Button onClick={requestCleanup} disabled={!canRunCleanup}>
                <Trash2 data-icon="inline-start" />
                {isCleaning ? "Cleaning" : "Clean Selected"}
              </Button>
            </div>
          </MaintenanceSection>

          <MaintenanceSection title="Areas" icon={Wrench}>
            <div className="flex flex-col gap-2">
              <CleanupGroupButton
                label="All"
                selected={activeGroup === "All"}
                total={categories.length}
                checked={selectedIds.length}
                onClick={() => setActiveGroup("All")}
                onToggle={(checked) => selectGroup("All", checked)}
                disabled={isScanning || isCleaning}
              />
              {groupSummaries.map((group) => (
                <CleanupGroupButton
                  key={group.group}
                  label={group.group}
                  selected={activeGroup === group.group}
                  total={group.total}
                  checked={group.checked}
                  onClick={() => setActiveGroup(group.group)}
                  onToggle={(checked) => selectGroup(group.group, checked)}
                  disabled={isScanning || isCleaning}
                />
              ))}
            </div>
          </MaintenanceSection>
        </aside>

        <div className="flex min-w-0 flex-col gap-4">
          <MaintenanceSection
            title={activeGroup === "All" ? "Selected Cleanup Areas" : activeGroup}
            icon={Trash2}
            action={<Badge variant="outline">{selectedCategories.length} selected</Badge>}
          >
            <div className="overflow-hidden rounded-md border border-border/80">
              <div className="grid grid-cols-[minmax(0,1fr)_120px_120px] border-b border-border/80 bg-background/45 px-4 py-3 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">
                <div>Name</div>
                <div className="text-right">Items</div>
                <div className="text-right">Size</div>
              </div>
              <div className="max-h-[560px] overflow-y-auto">
                {visibleCategories.map((category) => {
                  const selected = selectedIdSet.has(category.id)
                  return (
                    <label
                      key={category.id}
                      className={cn(
                        "grid cursor-pointer grid-cols-[minmax(0,1fr)_120px_120px] items-start gap-3 border-b border-border/70 px-3 py-3 transition-colors last:border-b-0 hover:bg-bg-surface-hover/45",
                        selected && "bg-background/35",
                        (isScanning || isCleaning || !category.isEnabled) && "cursor-not-allowed opacity-60 hover:bg-transparent"
                      )}
                    >
                      <div className="flex min-w-0 items-start gap-3">
                        <Checkbox
                          checked={selected}
                          disabled={!category.isEnabled || isScanning || isCleaning}
                          aria-label={`Select ${category.label} cleanup area`}
                          onCheckedChange={(checked) => setCategorySelected(category.id, checked === true)}
                        />
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{category.label}</div>
                            <Badge variant="outline">{category.group}</Badge>
                            {category.requiresAdministrator ? <Badge variant="destructive">Admin</Badge> : null}
                          </div>
                          <p className="mt-1 text-sm leading-[1.55] text-secondary">{category.description}</p>
                          <div className="mt-2 truncate font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{category.path || "Windows shell location"}</div>
                          {category.warnings.length > 0 ? (
                            <div className="mt-2 text-xs text-amber-400">{category.warnings[0]}</div>
                          ) : null}
                        </div>
                      </div>
                      <div className="text-right text-sm text-secondary">{category.fileCount}</div>
                      <div className="text-right font-display text-sm font-semibold text-primary">{category.sizeDisplay}</div>
                    </label>
                  )
                })}
                {visibleCategories.length === 0 ? (
                  <div className="px-3 py-3 text-sm text-secondary">
                    {cleanupScanError ? "Cleanup scan failed. Review the warning and try again." : isScanning ? "Scanning cleanup areas..." : "No cleanup areas are available for this group."}
                  </div>
                ) : null}
              </div>
            </div>
          </MaintenanceSection>

          <SafetyPanel
            title="Health Check Guardrails"
            points={[
              "Custom Clean only touches approved temporary/cache locations.",
              "Browser cleanup targets cache and code cache, not saved passwords or form data.",
              "Locked or running-app files are skipped and reported.",
              "Protected Windows locations require administrator mode before cleaning.",
            ]}
          />
          </div>
        </div>
      </div>
      <MaintenanceConfirmDialog
        open={showCleanupConfirmation}
        onOpenChange={setShowCleanupConfirmation}
        title="Clean selected areas?"
        description={`FileLocker will delete files from ${selectedCategories.length} selected temporary or cache cleanup area${selectedCategories.length === 1 ? "" : "s"}. Locked or unavailable files are skipped and reported.`}
        confirmLabel="Clean Selected"
        onConfirm={() => void runCleanup()}
        onDontShowAgain={() => {
          setSkipCleanupConfirmation(true)
          void runCleanup()
        }}
        isBusy={isCleaning}
      />
    </MaintenanceFrame>
  )
}

export function RegistryFixerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<RegistryScanResult | null>(null)
  const [registryScanError, setRegistryScanError] = useState("")
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [showRegistryConfirmation, setShowRegistryConfirmation] = useState(false)
  const [skipRegistryConfirmation, setSkipRegistryConfirmation] = useStoredBoolean("filelocker.skipRegistryFixConfirmation")
  const [isScanning, setIsScanning] = useState(true)
  const [isFixing, setIsFixing] = useState(false)
  const [lastClean, setLastClean] = useState<RegistryCleanResult | null>(null)
  const selectedIdSet = useMemo(() => new Set(selectedIds), [selectedIds])
  const registryNeedsAdministrator = scan?.issues.some((issue) => selectedIdSet.has(issue.id) && issue.hive === "HKLM") ?? false
  const canFix = selectedIds.length > 0 && !isScanning && !isFixing && (!registryNeedsAdministrator || isAdministrator)
  const registryWarnings = useMemo(
    () => [
      ...(registryScanError ? [registryScanError] : []),
      ...(scan?.warnings ?? []),
      ...(lastClean?.failures.map((failure) => `${failure.displayName}: ${failure.message}`) ?? []),
    ],
    [lastClean, registryScanError, scan]
  )

  useEffect(() => {
    void scanRegistry()
  }, [])

  async function scanRegistry() {
    if ((isScanning && scan !== null) || isFixing) {
      return
    }

    setIsScanning(true)
    setRegistryScanError("")
    setShowRegistryConfirmation(false)
    setLastClean(null)

    try {
      const response = await invoke<RegistryScanResult>("maintenance.scanRegistry", {})
      setScan(response)
      setSelectedIds((current) => {
        const cleanableIds = response.issues.filter((issue) => issue.canClean).map((issue) => issue.id)
        if (current.length === 0) {
          return cleanableIds
        }

        const availableIds = new Set(cleanableIds)
        const retainedIds = current.filter((id) => availableIds.has(id))
        return retainedIds.length > 0 ? retainedIds : cleanableIds
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : "Registry scan failed."
      setRegistryScanError(message)
      toast.error(message)
    } finally {
      setIsScanning(false)
    }
  }

  function requestFixRegistry() {
    if (isScanning || isFixing) {
      return
    }

    if (selectedIds.length === 0) {
      toast.error("Select at least one registry item.")
      return
    }

    if (registryNeedsAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before fixing selected registry items.")
      return
    }

    if (skipRegistryConfirmation) {
      void fixRegistry()
      return
    }

    setShowRegistryConfirmation(true)
  }

  async function fixRegistry() {
    if (isScanning || isFixing) {
      return
    }

    if (selectedIds.length === 0) {
      toast.error("Select at least one registry item.")
      return
    }

    if (registryNeedsAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before fixing selected registry items.")
      return
    }

    setIsFixing(true)
    try {
      const response = await invoke<RegistryCleanResult>("maintenance.cleanRegistry", {
        issueIds: selectedIds,
        confirmation: "FIX REGISTRY",
      })
      setLastClean(response)
      setScan(response.scan)
      setSelectedIds([])
      toast[response.failedCount > 0 ? "error" : "success"](
        response.failedCount > 0
          ? `Fixed ${response.cleanedCount} item(s); ${response.failedCount} failed.`
          : `Fixed ${response.cleanedCount} registry item(s).`
      )
    } catch (error) {
      showMaintenanceError(error, "Registry cleanup failed.")
    } finally {
      setIsFixing(false)
    }
  }

  return (
    <MaintenanceFrame>
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_370px]">
        <div className="flex min-w-0 flex-col gap-4">
          <AdminStatusBanner
            isAdministrator={isAdministrator}
            onRestartAsAdministrator={onRestartAsAdministrator}
            description="Current-user registry fixes can run normally. Local-machine registry cleanup needs administrator mode and always creates a backup first."
          />

          <MaintenanceSection
            title="Bounded Registry Scan"
            icon={Database}
            action={
              <Button variant="outline" onClick={() => void scanRegistry()} disabled={isScanning || isFixing}>
                <RefreshCcw data-icon="inline-start" />
                Rescan
              </Button>
            }
          >
            <div className="grid gap-4 md:grid-cols-3">
              <MetricTile label="Issues found" value={registryScanError ? "Scan failed" : scan ? String(scan.issueCount) : "..."} />
              <MetricTile label="Selected fixes" value={String(selectedIds.length)} />
              <MetricTile label="Last fixed" value={lastClean ? String(lastClean.cleanedCount) : "0"} good={Boolean(lastClean?.cleanedCount)} />
            </div>
            <WarningList title={registryScanError ? "Registry scan issue" : "Registry warnings"} warnings={registryWarnings} />

            <div className="mt-5 flex flex-col gap-3">
              {(scan?.issues ?? []).map((issue) => {
                const selected = selectedIdSet.has(issue.id)
                return (
              <label
                key={issue.id}
                className={cn(
                  "flex items-start gap-2.5 rounded-md border border-border/80 bg-background/35 px-3 py-3",
                  (isScanning || isFixing || !issue.canClean) && "cursor-not-allowed opacity-60"
                )}
              >
                    <Checkbox
                      checked={selected}
                      disabled={!issue.canClean || isScanning || isFixing}
                      aria-label={`Select registry issue ${issue.displayName}`}
                      onCheckedChange={(checked) => {
                        if (isScanning || isFixing) {
                          return
                        }

                        setSelectedIds((current) => checked === true
                          ? Array.from(new Set([...current, issue.id]))
                          : current.filter((id) => id !== issue.id))
                      }}
                    />
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center justify-between gap-3">
                        <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{issue.displayName}</div>
                        <Badge variant="outline">{issue.kind}</Badge>
                      </div>
                      <p className="mt-1 text-sm leading-[1.6] text-secondary">{issue.reason}</p>
                      <div className="mt-2 truncate font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{issue.hive}\{issue.keyPath}</div>
                      <div className="mt-1 truncate text-sm text-secondary">{issue.targetPath}</div>
                    </div>
                  </label>
                )
              })}

              {scan && scan.issues.length === 0 ? (
                <div className="rounded-md border border-border bg-background/35 px-3 py-3 text-sm text-secondary">
                  {scan.status}
                </div>
              ) : null}
            </div>
          </MaintenanceSection>

          <MaintenanceSection title="Backup-First Fix" icon={ShieldAlert}>
            <div className="grid gap-4 md:grid-cols-2">
              <MetricTile label="Backup path" value={lastClean?.backupPath ? "Created" : "Not created"} good={Boolean(lastClean?.backupPath)} />
              <MetricTile label="Failures" value={lastClean ? String(lastClean.failedCount) : "0"} />
            </div>
            {lastClean?.backupPath ? (
              <div className="mt-4 truncate rounded-md border border-border/80 bg-background/35 px-4 py-3 font-mono text-xs text-secondary">
                {lastClean.backupPath}
              </div>
            ) : null}
            <div className="mt-5 flex flex-wrap gap-3">
              <Button variant="destructive" onClick={requestFixRegistry} disabled={!canFix}>
                <Wrench data-icon="inline-start" />
                {isFixing ? "Fixing" : "Fix Selected"}
              </Button>
              {registryNeedsAdministrator && !isAdministrator ? (
                <Button variant="secondary" onClick={onRestartAsAdministrator}>
                  <ShieldAlert data-icon="inline-start" />
                  Restart as Administrator
                </Button>
              ) : null}
            </div>
          </MaintenanceSection>
        </div>

        <SafetyPanel
          title="Registry Fixer Scope"
          points={[
            "Only stale startup and uninstall entries are scanned in this pass.",
            "A .reg backup is written before any selected cleanup runs.",
            "Broad registry repairs are avoided because they can break apps and Windows components.",
            "HKLM cleanup may fail without administrator permissions.",
          ]}
        />
      </div>
      <MaintenanceConfirmDialog
        open={showRegistryConfirmation}
        onOpenChange={setShowRegistryConfirmation}
        title="Fix selected registry items?"
        description={`FileLocker will create a .reg backup first, then remove ${selectedIds.length} selected stale startup or uninstall entr${selectedIds.length === 1 ? "y" : "ies"}.`}
        confirmLabel="Fix Selected"
        onConfirm={() => void fixRegistry()}
        onDontShowAgain={() => {
          setSkipRegistryConfirmation(true)
          void fixRegistry()
        }}
        isBusy={isFixing}
      />
    </MaintenanceFrame>
  )
}

export function StartupManagerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<StartupScanResult | null>(null)
  const [startupScanError, setStartupScanError] = useState("")
  const [startupActionError, setStartupActionError] = useState("")
  const [filter, setFilter] = useState<"all" | "enabled" | "disabled">("all")
  const [isScanning, setIsScanning] = useState(true)
  const [updatingId, setUpdatingId] = useState("")
  const [pendingToggle, setPendingToggle] = useState<StartupItem | null>(null)
  const items = scan?.items ?? []
  const visibleItems = useMemo(() => {
    if (filter === "enabled") {
      return items.filter((item) => item.isEnabled)
    }

    if (filter === "disabled") {
      return items.filter((item) => !item.isEnabled)
    }

    return items
  }, [filter, items])
  const selectedAdminItems = items.filter((item) => item.requiresAdministrator)
  const startupToggleBusy = Boolean(updatingId)
  const startupWarnings = useMemo(
    () => [
      ...(startupScanError ? [startupScanError] : []),
      ...(startupActionError ? [startupActionError] : []),
      ...(scan?.warnings ?? []),
    ],
    [scan, startupActionError, startupScanError]
  )

  useEffect(() => {
    void scanStartup()
  }, [])

  async function scanStartup() {
    if ((isScanning && scan !== null) || updatingId) {
      return
    }

    setIsScanning(true)
    setStartupScanError("")
    setStartupActionError("")
    setPendingToggle(null)
    try {
      const response = await invoke<StartupScanResult>("maintenance.scanStartup", {})
      setScan(response)
    } catch (error) {
      const message = error instanceof Error ? error.message : "Startup scan failed."
      setStartupScanError(message)
      toast.error(message)
    } finally {
      setIsScanning(false)
    }
  }

  async function toggleStartupItem(item: StartupItem) {
    if (isScanning || updatingId) {
      return
    }

    if (!item.canToggle) {
      toast.error("This startup item cannot be changed by FileLocker.")
      return
    }

    if (item.requiresAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before changing this startup item.")
      return
    }

    setPendingToggle(null)
    setStartupActionError("")
    setUpdatingId(item.id)
    try {
      const response = await invoke<StartupToggleResult>("maintenance.setStartupEnabled", {
        itemId: item.id,
        enabled: !item.isEnabled,
      })
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
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_370px]">
        <div className="flex min-w-0 flex-col gap-4">
          {selectedAdminItems.length > 0 && !isAdministrator ? (
            <AdminStatusBanner
              isAdministrator={isAdministrator}
              onRestartAsAdministrator={onRestartAsAdministrator}
              description="HKLM Run and common Startup folder entries need administrator mode before FileLocker can disable or restore them."
            />
          ) : null}

          <MaintenanceSection
            title="Startup Inventory"
            icon={Power}
            action={
              <Button variant="outline" onClick={() => void scanStartup()} disabled={isScanning || Boolean(updatingId)}>
                <RefreshCcw data-icon="inline-start" />
                Rescan
              </Button>
            }
          >
            <div className="grid gap-4 md:grid-cols-3">
              <MetricTile label="Enabled" value={scan ? String(scan.enabledCount) : "..."} />
              <MetricTile label="Disabled" value={scan ? String(scan.disabledCount) : "..."} />
              <MetricTile label="Admin scoped" value={String(selectedAdminItems.length)} />
            </div>
            <WarningList title={startupScanError ? "Startup scan issue" : startupActionError ? "Startup action issue" : "Startup scan warnings"} warnings={startupWarnings} />
            <div className="mt-4 flex flex-wrap gap-2">
              {(["all", "enabled", "disabled"] as const).map((mode) => (
                <Button
                  key={mode}
                  variant={filter === mode ? "secondary" : "outline"}
                  onClick={() => setFilter(mode)}
                  aria-pressed={filter === mode}
                  size="sm"
                >
                  {mode === "all" ? "All" : mode === "enabled" ? "Enabled" : "Disabled"}
                </Button>
              ))}
            </div>
          </MaintenanceSection>

          <MaintenanceSection
            title="Startup Entries"
            icon={PowerOff}
            action={<Badge variant="outline">{visibleItems.length} shown</Badge>}
          >
            <div className="overflow-hidden rounded-md border border-border/80">
              <div className="grid grid-cols-[minmax(0,1fr)_128px] border-b border-border/80 bg-background/45 px-4 py-3 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">
                <div>Entry</div>
                <div className="text-right">Action</div>
              </div>
              <div className="max-h-[620px] overflow-y-auto">
                {visibleItems.map((item) => {
                  const adminBlocked = item.requiresAdministrator && !isAdministrator
                  return (
                    <div key={item.id} className="grid grid-cols-[minmax(0,1fr)_128px] items-start gap-3 border-b border-border/70 px-4 py-3 last:border-b-0">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{item.name}</div>
                          <Badge variant={item.isEnabled ? "secondary" : "outline"}>{item.status}</Badge>
                          <Badge variant="outline">{item.source}</Badge>
                          {item.requiresAdministrator ? <Badge variant="destructive">Admin</Badge> : null}
                        </div>
                        <div className="mt-2 truncate font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{item.location}</div>
                        <div className="mt-1 truncate text-sm text-secondary">{item.command}</div>
                        {item.targetPath ? <div className="mt-1 truncate text-xs text-muted">{item.targetPath}</div> : null}
                        {item.warnings.length > 0 ? <div className="mt-2 text-xs text-amber-400">{item.warnings[0]}</div> : null}
                      </div>
                      <div className="flex justify-end">
                        <Button
                          variant={item.isEnabled ? "destructive" : "secondary"}
                          size="sm"
                          disabled={!item.canToggle || adminBlocked || isScanning || startupToggleBusy}
                          onClick={() => setPendingToggle(item)}
                        >
                          {item.isEnabled ? <PowerOff data-icon="inline-start" /> : <Power data-icon="inline-start" />}
                          {updatingId === item.id ? "Updating" : item.isEnabled ? "Disable" : "Enable"}
                        </Button>
                      </div>
                    </div>
                  )
                })}
                {visibleItems.length === 0 ? (
                  <div className="px-4 py-4 text-sm text-secondary">
                    {startupScanError ? "Startup scan failed. Review the warning and try again." : isScanning ? "Scanning startup entries..." : "No startup entries match this filter."}
                  </div>
                ) : null}
              </div>
            </div>
          </MaintenanceSection>
        </div>

        <SafetyPanel
          title="Startup Manager Guardrails"
          points={[
            "HKCU and HKLM Run values are backed up before FileLocker removes them.",
            "Startup folder shortcuts are moved into FileLocker-managed disabled storage.",
            "Disabled entries stay visible so they can be restored from this page.",
            "Common Startup and HKLM changes require administrator mode.",
          ]}
        />
      </div>
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
              {startupToggleBusy ? "Updating" : pendingToggle?.isEnabled ? "Disable" : "Restore"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </MaintenanceFrame>
  )
}

export function AppManagerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [apps, setApps] = useState<InstalledApp[]>([])
  const [appWarnings, setAppWarnings] = useState<string[]>([])
  const [appScanError, setAppScanError] = useState("")
  const [appActionError, setAppActionError] = useState("")
  const [query, setQuery] = useState("")
  const [sortKey, setSortKey] = useState<"name" | "publisher" | "size" | "date">("name")
  const [selectedAppId, setSelectedAppId] = useState("")
  const [isScanningApps, setIsScanningApps] = useState(true)
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
  const visibleApps = useMemo(() => sortInstalledApps(filterInstalledApps(apps, query), sortKey), [apps, query, sortKey])
  const selectedApp = visibleApps.find((app) => app.id === selectedAppId) ?? visibleApps[0] ?? null
  const leftovers = leftoverScan?.categories ?? []
  const selectedLeftoverIdSet = useMemo(() => new Set(selectedLeftoverIds), [selectedLeftoverIds])
  const selectedLeftovers = leftovers.filter((category) => selectedLeftoverIdSet.has(category.id))
  const leftoversNeedAdministrator = selectedLeftovers.some((category) => category.requiresAdministrator)
  const canCleanLeftovers = selectedLeftovers.length > 0 && !isScanningLeftovers && !isCleaningLeftovers && (!leftoversNeedAdministrator || isAdministrator)
  const uninstallerNotice = selectedApp ? getUninstallerNotice(selectedApp) : ""
  const leftoverWarnings = useMemo(
    () => [
      ...(leftoverScanError ? [leftoverScanError] : []),
      ...(leftoverCleanError ? [leftoverCleanError] : []),
      ...(leftoverScan?.warnings ?? []),
      ...(lastLeftoverClean?.failures.map((failure) => `${failure.appDisplayName}: ${failure.path} - ${failure.message}`) ?? []),
    ],
    [lastLeftoverClean, leftoverCleanError, leftoverScan, leftoverScanError]
  )

  useEffect(() => {
    void scanInstalledApps()
  }, [])

  useEffect(() => {
    setAppActionError("")
    resetLeftoverReview()
  }, [selectedApp?.id])

  async function scanInstalledApps() {
    if ((isScanningApps && apps.length > 0) || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) {
      return
    }

    resetLeftoverReview()
    setIsScanningApps(true)
    setAppScanError("")
    setAppActionError("")
    setPendingUninstall(null)
    try {
      const response = await invoke<InstalledAppsScanResult>("maintenance.scanInstalledApps", {})
      setApps(response.apps)
      setAppWarnings(response.warnings)
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
    if (isScanningApps || isCleaningLeftovers) {
      return
    }

    setSelectedAppId(app.id)
    setAppActionError("")
    resetLeftoverReview()
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
    if (isScanningApps || isCleaningLeftovers) {
      return
    }

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
    if (isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) {
      return
    }

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
      const response = await invoke<UninstallerLaunchResult>("maintenance.launchUninstaller", {
        appId: app.id,
        confirmation: "UNINSTALL",
      })
      setAppActionError("")
      toast.success(response.message)
    } catch (error) {
      setAppActionError(showMaintenanceError(error, "Unable to launch uninstaller."))
    } finally {
      setIsLaunchingUninstaller(false)
    }
  }

  async function scanLeftovers(app = selectedApp) {
    if (isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller) {
      return
    }

    if (!app) {
      toast.error("Select an app first.")
      return
    }

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
      if (leftoverScanRequestId.current !== requestId) {
        return
      }

      setLeftoverScanError("")
      setLeftoverScan(response)
      setSelectedLeftoverIds(response.categories.filter((category) => category.defaultSelected && category.isEnabled).map((category) => category.id))
    } catch (error) {
      if (leftoverScanRequestId.current !== requestId) {
        return
      }

      const message = error instanceof Error ? error.message : "App leftover scan failed."
      setLeftoverScanError(message)
      toast.error(message)
    } finally {
      if (leftoverScanRequestId.current === requestId) {
        setIsScanningLeftovers(false)
      }
    }
  }

  function setLeftoverSelected(categoryId: string, selected: boolean) {
    if (isScanningLeftovers || isCleaningLeftovers) {
      return
    }

    setSelectedLeftoverIds((current) => selected
      ? Array.from(new Set([...current, categoryId]))
      : current.filter((id) => id !== categoryId))
  }

  function requestCleanLeftovers() {
    if (isScanningLeftovers || isCleaningLeftovers) {
      return
    }

    if (selectedLeftovers.length === 0) {
      toast.error("Select at least one leftover category.")
      return
    }

    if (leftoversNeedAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before cleaning selected ProgramData leftovers.")
      return
    }

    if (skipLeftoverConfirmation) {
      void cleanLeftovers()
      return
    }

    setShowLeftoverConfirmation(true)
  }

  async function cleanLeftovers() {
    if (isScanningLeftovers || isCleaningLeftovers) {
      return
    }

    if (!selectedApp) {
      toast.error("Select an app first.")
      return
    }

    if (selectedLeftovers.length === 0) {
      toast.error("Select at least one leftover category.")
      return
    }

    if (leftoversNeedAdministrator && !isAdministrator) {
      toast.error("Restart FileLocker as administrator before cleaning selected ProgramData leftovers.")
      return
    }

    setShowLeftoverConfirmation(false)
    setIsCleaningLeftovers(true)
    setLeftoverCleanError("")
    try {
      const response = await invoke<AppLeftoverCleanResult>("maintenance.cleanAppLeftovers", {
        appIds: [selectedApp.id],
        categoryIds: selectedLeftovers.map((category) => category.id),
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
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.25fr)_390px]">
        <div className="flex min-w-0 flex-col gap-4">
          {leftoversNeedAdministrator && !isAdministrator ? (
            <AdminStatusBanner
              isAdministrator={isAdministrator}
              onRestartAsAdministrator={onRestartAsAdministrator}
              description="Selected ProgramData leftovers require administrator mode before cleanup can run."
            />
          ) : null}

          <MaintenanceSection
            title="Installed Apps"
            icon={Package}
            action={
              <Button variant="outline" onClick={() => void scanInstalledApps()} disabled={isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller}>
                <RefreshCcw data-icon="inline-start" />
                Rescan
              </Button>
            }
          >
            <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_180px]">
              <div className="relative">
                <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" aria-hidden />
                <Input
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder="Search installed apps"
                  aria-label="Search installed apps"
                  className="pl-9"
                  disabled={isScanningApps || isCleaningLeftovers}
                />
              </div>
              <Button
                variant="secondary"
                onClick={() => setSortKey((current) => current === "name" ? "publisher" : current === "publisher" ? "size" : current === "size" ? "date" : "name")}
                aria-label={`Sort installed apps by ${sortKey === "name" ? "name" : sortKey === "publisher" ? "publisher" : sortKey === "size" ? "size" : "install date"}`}
                disabled={isScanningApps || isCleaningLeftovers}
              >
                <ArrowUpDown data-icon="inline-start" />
                {sortKey === "name" ? "Name" : sortKey === "publisher" ? "Publisher" : sortKey === "size" ? "Size" : "Install Date"}
              </Button>
            </div>

            <div className="mt-4 grid gap-4 md:grid-cols-3">
              <MetricTile label="Apps found" value={String(apps.length)} />
              <MetricTile label="Shown" value={String(visibleApps.length)} />
              <MetricTile label="Selected size" value={selectedApp?.estimatedSizeDisplay ?? "Unknown"} />
            </div>
            <WarningList title={appScanError ? "Installed app scan issue" : "Installed app scan warnings"} warnings={appScanError ? [appScanError, ...appWarnings] : appWarnings} />

            <div className="mt-4 overflow-hidden rounded-md border border-border/80">
              <div className="grid grid-cols-[minmax(0,1fr)_120px_96px] border-b border-border/80 bg-background/45 px-4 py-3 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">
                <div>App</div>
                <div className="text-right">Version</div>
                <div className="text-right">Size</div>
              </div>
              <div className="max-h-[460px] overflow-y-auto">
                {visibleApps.map((app) => (
                  <button
                    key={app.id}
                    type="button"
                    onClick={() => selectApp(app)}
                    aria-pressed={selectedApp?.id === app.id}
                    disabled={isScanningApps || isCleaningLeftovers}
                    className={cn(
                      "grid w-full grid-cols-[minmax(0,1fr)_120px_96px] items-start gap-3 border-b border-border/70 px-4 py-3 text-left transition-colors last:border-b-0 hover:bg-bg-surface-hover/45 disabled:cursor-not-allowed disabled:opacity-60",
                      selectedApp?.id === app.id && "bg-accent/8"
                    )}
                  >
                    <div className="min-w-0">
                      <div className="truncate font-display text-[1rem] font-semibold tracking-tight text-primary">{app.displayName}</div>
                      <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-secondary">
                        <span className="truncate">{app.publisher || "Unknown publisher"}</span>
                        <Badge variant="outline">{app.sourceHive}</Badge>
                        <Badge variant="outline">{app.architecture}</Badge>
                      </div>
                    </div>
                    <div className="truncate text-right text-sm text-secondary">{app.version || "Unknown"}</div>
                    <div className="text-right font-display text-sm font-semibold text-primary">{app.estimatedSizeDisplay}</div>
                  </button>
                ))}
                {visibleApps.length === 0 ? (
                  <div className="px-4 py-4 text-sm text-secondary">
                    {appScanError ? "Installed app scan failed. Review the warning and try again." : isScanningApps ? "Scanning installed apps..." : "No installed apps match this search."}
                  </div>
                ) : null}
              </div>
            </div>
          </MaintenanceSection>

          {selectedApp ? (
            <MaintenanceSection title="Selected App Actions" icon={ExternalLink}>
              <div className="grid gap-4 md:grid-cols-2">
                <MetricTile label="Publisher" value={selectedApp.publisher || "Unknown"} />
                <MetricTile label="Install date" value={selectedApp.installDate || "Unknown"} />
              </div>
              <div className="mt-4 truncate rounded-md border border-border/80 bg-background/35 px-4 py-3 font-mono text-xs text-secondary">
                {selectedApp.installLocation || "Install location was not published by this app."}
              </div>
              {uninstallerNotice ? (
                <div className="mt-3 flex items-start gap-2.5 rounded-md border border-amber-500/30 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary">
                  <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
                  <span>{uninstallerNotice}</span>
                </div>
              ) : null}
              <WarningList title="Selected app action issue" warnings={appActionError ? [appActionError] : []} />
              <div className="mt-4 flex flex-wrap gap-2">
                <Button variant="secondary" onClick={() => void revealInstallLocation(selectedApp)} disabled={!selectedApp.installLocation || isScanningApps || isCleaningLeftovers}>
                  <FolderOpen data-icon="inline-start" />
                  Reveal Location
                </Button>
                <Button variant="outline" onClick={() => void scanLeftovers(selectedApp)} disabled={isScanningApps || isScanningLeftovers || isCleaningLeftovers || isLaunchingUninstaller}>
                  <ScanLine data-icon="inline-start" />
                  {isScanningLeftovers ? "Scanning" : "Scan Leftovers"}
                </Button>
                <Button variant="destructive" onClick={() => setPendingUninstall(selectedApp)} disabled={!selectedApp.canLaunchUninstaller || isScanningApps || isScanningLeftovers || isLaunchingUninstaller || isCleaningLeftovers}>
                  <ExternalLink data-icon="inline-start" />
                  {isLaunchingUninstaller ? "Opening" : "Launch Uninstaller"}
                </Button>
              </div>
            </MaintenanceSection>
          ) : null}

          <MaintenanceSection
            title="Leftover Cleanup"
            icon={Trash2}
            action={<Badge variant="outline">{selectedLeftovers.length} selected</Badge>}
          >
            <div className="grid gap-4 md:grid-cols-3">
              <MetricTile label="Recoverable" value={leftoverScanError ? "Scan failed" : leftoverScan?.totalDisplay ?? "Not scanned"} />
              <MetricTile label="Files" value={leftoverScan ? String(leftoverScan.totalFiles) : "0"} />
              <MetricTile label="Selected areas" value={String(selectedLeftovers.length)} />
            </div>
            <WarningList title={leftoverScanError ? "Leftover scan issue" : leftoverCleanError ? "Leftover cleanup issue" : "Leftover cleanup warnings"} warnings={leftoverWarnings} />

            <div className="mt-4 overflow-hidden rounded-md border border-border/80">
              <div className="grid grid-cols-[minmax(0,1fr)_96px_96px] border-b border-border/80 bg-background/45 px-4 py-3 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">
                <div>Area</div>
                <div className="text-right">Files</div>
                <div className="text-right">Size</div>
              </div>
              <div className="max-h-[360px] overflow-y-auto">
                {leftovers.map((category) => {
                  const selected = selectedLeftoverIdSet.has(category.id)
                  return (
                    <label
                      key={category.id}
                      className={cn(
                        "grid cursor-pointer grid-cols-[minmax(0,1fr)_96px_96px] items-start gap-3 border-b border-border/70 px-4 py-3 last:border-b-0",
                        (isScanningLeftovers || isCleaningLeftovers || !category.isEnabled) && "cursor-not-allowed opacity-60"
                      )}
                    >
                      <div className="flex min-w-0 items-start gap-3">
                        <Checkbox
                          checked={selected}
                          disabled={!category.isEnabled || isScanningLeftovers || isCleaningLeftovers}
                          aria-label={`Select ${category.appDisplayName} ${category.group} leftover cleanup area`}
                          onCheckedChange={(checked) => setLeftoverSelected(category.id, checked === true)}
                        />
                        <div className="min-w-0">
                          <div className="flex flex-wrap items-center gap-2">
                            <div className="font-display text-[1rem] font-semibold tracking-tight text-primary">{category.group}</div>
                            <Badge variant="outline">{category.defaultSelected ? "Default" : "Review"}</Badge>
                            {category.requiresAdministrator ? <Badge variant="destructive">Admin</Badge> : null}
                          </div>
                          <p className="mt-1 text-sm leading-[1.55] text-secondary">{category.description}</p>
                          <div className="mt-2 truncate font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{category.path}</div>
                          {category.warnings.length > 0 ? <div className="mt-2 text-xs text-amber-400">{category.warnings[0]}</div> : null}
                        </div>
                      </div>
                      <div className="text-right text-sm text-secondary">{category.fileCount}</div>
                      <div className="text-right font-display text-sm font-semibold text-primary">{category.sizeDisplay}</div>
                    </label>
                  )
                })}
                {leftovers.length === 0 ? (
                  <div className="px-4 py-4 text-sm text-secondary">
                    {leftoverScanError ? "Leftover scan failed. Review the warning and try again." : isScanningLeftovers ? "Scanning leftovers..." : "Select an app and scan leftovers to review cleanup candidates."}
                  </div>
                ) : null}
              </div>
            </div>

            <div className="mt-4 flex flex-wrap gap-2">
              <Button onClick={requestCleanLeftovers} disabled={!canCleanLeftovers}>
                <Trash2 data-icon="inline-start" />
                {isCleaningLeftovers ? "Cleaning" : "Clean Selected"}
              </Button>
              {leftoversNeedAdministrator && !isAdministrator ? (
                <Button variant="secondary" onClick={onRestartAsAdministrator}>
                  <ShieldAlert data-icon="inline-start" />
                  Restart as Administrator
                </Button>
              ) : null}
            </div>
          </MaintenanceSection>
        </div>

        <SafetyPanel
          title="App Manager Guardrails"
          points={[
            "Uninstallers are only launched after confirmation; FileLocker does not run silent uninstall commands.",
            "Install locations can be revealed through Windows Explorer when apps publish a path.",
            "Leftover cleanup is limited to AppData and ProgramData candidates.",
            "Program Files and Windows folders are excluded from recursive cleanup.",
          ]}
        />
      </div>
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
              {isLaunchingUninstaller ? "Opening" : "Launch Uninstaller"}
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
        onDontShowAgain={() => {
          setSkipLeftoverConfirmation(true)
          void cleanLeftovers()
        }}
        isBusy={isCleaningLeftovers}
      />
    </MaintenanceFrame>
  )
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
  if (isAdministrator) {
    return null
  }

  return (
    <div
      className="border-y border-amber-500/35 bg-amber-500/8 px-3 py-2 text-sm leading-snug text-secondary"
    >
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex min-w-0 flex-1 items-start gap-2.5">
          <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
          <div className="min-w-0 flex-1">
            <div className="font-display text-sm font-semibold tracking-tight text-primary">
              Administrator access required
            </div>
            <div className="mt-0.5">{description}</div>
          </div>
        </div>
        <Button variant="secondary" onClick={onRestartAsAdministrator} className="shrink-0 self-start sm:self-center">
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
          <AlertDialogDescription className="leading-[1.6]">
            {description}
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter className="sm:flex-wrap">
          <AlertDialogCancel disabled={isBusy}>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="secondary" disabled={isBusy} onClick={onDontShowAgain}>
            Don't show again
          </AlertDialogAction>
          <AlertDialogAction variant="destructive" disabled={isBusy} onClick={onConfirm}>
            {confirmLabel}
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

function CleanupGroupButton({
  label,
  selected,
  total,
  checked,
  onClick,
  onToggle,
  disabled = false,
}: {
  label: string
  selected: boolean
  total: number
  checked: number
  onClick: () => void
  onToggle: (checked: boolean) => void
  disabled?: boolean
}) {
  const allChecked = total > 0 && checked === total
  return (
    <div
      aria-disabled={disabled}
      className={cn(
        "grid grid-cols-[auto_minmax(0,1fr)] items-center gap-3 rounded-md border border-border/80 bg-background/30 px-3 py-3 transition-colors",
        selected && "border-accent/70 bg-accent/10",
        disabled && "opacity-60"
      )}
    >
      <Checkbox
        checked={allChecked}
        disabled={disabled}
        aria-label={`Select all ${label} cleanup areas`}
        onCheckedChange={(value) => onToggle(value === true)}
      />
      <button type="button" className="min-w-0 text-left disabled:cursor-not-allowed" onClick={onClick} disabled={disabled}>
        <div className="truncate font-display text-[0.98rem] font-semibold tracking-tight text-primary">{label}</div>
        <div className="mt-1 font-mono text-[11px] uppercase tracking-[0.18em] text-muted">{checked}/{total} selected</div>
      </button>
    </div>
  )
}

function MaintenanceFrame({ children }: { children: React.ReactNode }) {
  return <div className="security-page">{children}</div>
}

function MaintenanceSection({
  title,
  icon: Icon,
  action,
  children,
}: {
  title: string
  icon: typeof HardDrive
  action?: React.ReactNode
  children: React.ReactNode
}) {
  return (
    <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
      <SectionHeader className="border-b border-border/80 px-4 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex min-w-0 items-center gap-3">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent">
              <Icon className="size-4" aria-hidden />
            </div>
            <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">{title}</SectionTitle>
          </div>
          {action}
        </div>
      </SectionHeader>
      <SectionBody className="px-4 py-3">{children}</SectionBody>
    </Section>
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
    <MaintenanceSection
      title="Drive Selection"
      icon={HardDrive}
      action={
        <Button variant="outline" onClick={onRefresh} disabled={isLoading}>
          <RefreshCcw data-icon="inline-start" />
          Refresh
        </Button>
      }
    >
      <div className="grid gap-3 md:grid-cols-2">
        {drives.map((drive) => {
          const selected = drive.rootPath === selectedDriveRoot
          return (
            <button
              key={drive.id}
              type="button"
              className={cn(
                "min-h-[92px] rounded-md border border-border/80 bg-background/35 px-3 py-3 text-left transition-colors hover:border-accent/60 hover:bg-bg-surface-hover/70",
                selected && "border-accent bg-accent/8"
              )}
              onClick={() => onSelect(drive.rootPath)}
              aria-pressed={selected}
              disabled={!drive.isReady}
            >
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate font-display text-sm font-semibold tracking-tight text-primary">{drive.name}</div>
                  <div className="mt-0.5 font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{drive.rootPath} / {drive.driveFormat}</div>
                </div>
                <Badge variant={selected ? "secondary" : "outline"}>{drive.driveType}</Badge>
              </div>
              <div className="mt-3 h-1.5 overflow-hidden rounded-md bg-background/60">
                <div className="h-full bg-accent" style={{ width: `${getDriveUsedPercent(drive)}%` }} />
              </div>
              <div className="mt-2 flex items-center justify-between gap-3 text-xs text-secondary">
                <span>{drive.freeSpaceDisplay} free</span>
                <span>{drive.totalSizeDisplay}</span>
              </div>
            </button>
          )
        })}
      </div>
      {drives.length === 0 ? (
        <div className="rounded-md border border-border bg-background/35 px-3 py-3 text-sm text-secondary">
          {loadError ? "Drive list could not be loaded." : isLoading ? "Loading drives..." : "No fixed or removable drives are available."}
        </div>
      ) : null}
      {loadError ? (
        <div className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/8 px-3 py-3 text-sm text-secondary" role="status" aria-live="polite">
          <div className="flex items-start gap-2.5">
            <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
            <span>{loadError}</span>
          </div>
        </div>
      ) : null}
    </MaintenanceSection>
  )
}

function MetricTile({ label, value, good = false }: { label: string; value: string; good?: boolean }) {
  return (
    <div className="rounded-md border border-border/80 bg-background/35 px-3 py-2">
      <div className="font-mono text-[10px] uppercase tracking-[0.14em] text-muted">{label}</div>
      <div className={cn("mt-1 truncate font-display text-sm font-semibold tracking-tight", good ? "text-accent-green" : "text-primary")}>{value}</div>
    </div>
  )
}

function WarningList({ title, warnings }: { title: string; warnings: string[] }) {
  const uniqueWarnings = Array.from(new Set(warnings.map((warning) => warning.trim()).filter(Boolean)))
  const visibleWarnings = uniqueWarnings.slice(0, 4)
  const hiddenCount = uniqueWarnings.length - visibleWarnings.length

  if (visibleWarnings.length === 0) {
    return null
  }

  return (
    <div
      className="mt-4 rounded-md border border-amber-500/30 bg-amber-500/8 px-3 py-3 text-sm text-secondary"
      role="status"
      aria-live="polite"
      aria-label={title}
    >
      <div className="mb-2 flex items-center gap-2 font-display text-sm font-semibold tracking-tight text-primary">
        <AlertTriangle className="size-4 text-amber-400" aria-hidden />
        {title}
      </div>
      <div className="flex flex-col gap-1.5">
        {visibleWarnings.map((warning) => (
          <div key={warning} className="leading-snug">{warning}</div>
        ))}
        {hiddenCount > 0 ? (
          <div className="font-mono text-[11px] uppercase tracking-[0.18em] text-muted">+{hiddenCount} more</div>
        ) : null}
      </div>
    </div>
  )
}

function ToolOutput({ result, runningLabel, errorMessage }: { result: MaintenanceToolResult | null; runningLabel?: string; errorMessage?: string }) {
  if (!result && !runningLabel && !errorMessage) {
    return null
  }

  return (
    <MaintenanceSection title="Tool Output" icon={Download}>
      {runningLabel ? (
        <div className="mb-4 rounded-md border border-accent/25 bg-accent/8 px-4 py-3 text-sm text-secondary">{runningLabel}</div>
      ) : null}
      {errorMessage ? (
        <div className="mb-4 rounded-md border border-amber-500/30 bg-amber-500/8 px-4 py-3 text-sm text-secondary" role="status" aria-live="polite">
          <div className="flex items-start gap-2.5">
            <AlertTriangle className="mt-0.5 size-4 shrink-0 text-amber-400" aria-hidden />
            <span>{errorMessage}</span>
          </div>
        </div>
      ) : null}
      {result ? (
        <>
          <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
            <div className="text-sm text-secondary">{result.message}</div>
            <Badge variant={result.ok ? "secondary" : "destructive"}>{result.ok ? "Completed" : "Needs review"}</Badge>
          </div>
          <pre className="terminal-output max-h-[360px]">{result.output || "No command output was returned."}</pre>
        </>
      ) : null}
    </MaintenanceSection>
  )
}

function SafetyPanel({ title, points }: { title: string; points: string[] }) {
  return (
    <aside className="flex min-w-0 flex-col gap-4">
      <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
        <SectionHeader className="border-b border-border/80 px-4 py-3">
          <div className="flex items-center gap-3">
            <div className="flex size-8 items-center justify-center rounded-md border border-amber-500/30 bg-amber-500/10 text-amber-400">
              <AlertTriangle className="size-4" aria-hidden />
            </div>
            <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">{title}</SectionTitle>
          </div>
        </SectionHeader>
        <SectionBody className="flex flex-col gap-2 px-4 py-3">
          {points.map((point) => (
            <div key={point} className="rounded-md border border-border/80 bg-background/35 px-3 py-2 text-sm leading-snug text-secondary">
              {point}
            </div>
          ))}
        </SectionBody>
        <SectionFooter className="border-t border-border bg-transparent px-4 py-3">
          <div className="flex items-start gap-2.5 text-sm leading-snug text-secondary">
            <Info className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden />
            <span>All operations run locally through Windows maintenance tools.</span>
          </div>
        </SectionFooter>
      </Section>
    </aside>
  )
}

function useSelectedDrive(drives: MaintenanceDrive[], selectedDriveRoot: string) {
  return useMemo(
    () => drives.find((drive) => drive.rootPath.toLowerCase() === selectedDriveRoot.toLowerCase()) ?? drives.find((drive) => drive.isReady) ?? null,
    [drives, selectedDriveRoot]
  )
}

function buildCleanupGroupSummaries(categories: CleanupCategory[], selectedIdSet: Set<string>) {
  return Array.from(new Set(categories.map((category) => category.group)))
    .sort((a, b) => a.localeCompare(b))
    .map((group) => {
      const groupCategories = categories.filter((category) => category.group === group)
      return {
        group,
        total: groupCategories.length,
        checked: groupCategories.filter((category) => selectedIdSet.has(category.id)).length,
      }
    })
}

function filterInstalledApps(apps: InstalledApp[], query: string) {
  const normalized = query.trim().toLowerCase()
  if (!normalized) {
    return apps
  }

  return apps.filter((app) =>
    app.displayName.toLowerCase().includes(normalized) ||
    app.publisher.toLowerCase().includes(normalized) ||
    app.version.toLowerCase().includes(normalized)
  )
}

function sortInstalledApps(apps: InstalledApp[], sortKey: "name" | "publisher" | "size" | "date") {
  const sorted = [...apps]
  sorted.sort((a, b) => {
    if (sortKey === "size") {
      return b.estimatedSizeBytes - a.estimatedSizeBytes || a.displayName.localeCompare(b.displayName)
    }

    if (sortKey === "date") {
      return (b.installDate || "").localeCompare(a.installDate || "") || a.displayName.localeCompare(b.displayName)
    }

    if (sortKey === "publisher") {
      return (a.publisher || "").localeCompare(b.publisher || "") || a.displayName.localeCompare(b.displayName)
    }

    return a.displayName.localeCompare(b.displayName)
  })
  return sorted
}

function getUninstallerNotice(app: InstalledApp) {
  if (!app.uninstallCommand) {
    return "This app does not publish a vendor uninstall command."
  }

  if (!app.canLaunchUninstaller) {
    return "This uninstall command appears to include quiet or silent switches, so FileLocker will not launch it."
  }

  return ""
}

function useStoredBoolean(key: string) {
  const [value, setValue] = useState(() => {
    try {
      return window.localStorage.getItem(key) === "true"
    } catch {
      return false
    }
  })

  function updateValue(nextValue: boolean) {
    setValue(nextValue)
    try {
      window.localStorage.setItem(key, nextValue ? "true" : "false")
    } catch {
    }
  }

  return [value, updateValue] as const
}

function readStoredStringArray(key: string) {
  try {
    const raw = window.localStorage.getItem(key)
    const value = raw ? JSON.parse(raw) : []
    return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : []
  } catch {
    return []
  }
}

function writeStoredStringArray(key: string, value: string[]) {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
  } catch {
  }
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
      if (current && response.drives.some((drive) => drive.rootPath.toLowerCase() === current.toLowerCase())) {
        return current
      }

      return response.drives.find((drive) => drive.isReady)?.rootPath || response.drives[0]?.rootPath || ""
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
  if (drive.totalSizeBytes <= 0) {
    return 0
  }

  return Math.max(0, Math.min(100, ((drive.totalSizeBytes - drive.freeSpaceBytes) / drive.totalSizeBytes) * 100))
}

function showMaintenanceError(error: unknown, fallback: string) {
  const message = error instanceof Error ? error.message : fallback
  const permissionRelated = /administrator|elevat|access is denied|insufficient/i.test(message)
  const alreadyActionable = /restart .*administrator|use restart as administrator/i.test(message)
  const displayMessage = permissionRelated && !alreadyActionable ? `${message} Restart FileLocker as Administrator and try again.` : message
  toast.error(displayMessage)
  return displayMessage
}
