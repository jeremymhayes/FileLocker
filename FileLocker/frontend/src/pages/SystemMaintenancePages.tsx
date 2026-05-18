import { useEffect, useMemo, useState } from "react"
import {
  AlertTriangle,
  CheckCircle2,
  Database,
  Download,
  HardDrive,
  Info,
  Play,
  RefreshCcw,
  ScanLine,
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
import { cn } from "@/lib/utils"

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
  const [isRunning, setIsRunning] = useState(false)
  const [result, setResult] = useState<MaintenanceToolResult | null>(null)
  const selectedDrive = useSelectedDrive(drives, selectedDriveRoot)
  const canStart = isAdministrator && Boolean(selectedDrive?.isReady) && !isRunning

  useEffect(() => {
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading)
  }, [invoke])

  function requestWipe() {
    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    if (skipWipeConfirmation) {
      void runWipe()
      return
    }

    setShowWipeConfirmation(true)
  }

  async function runWipe() {
    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    setIsRunning(true)
    setResult(null)
    try {
      const response = await invoke<MaintenanceToolResult>("maintenance.wipeFreeSpace", {
        driveRoot: selectedDrive.rootPath,
        confirmation: "WIPE FREE SPACE",
      })
      setResult(response)
      toast[response.ok ? "success" : "error"](response.message)
    } catch (error) {
      showMaintenanceError(error, "Free-space wipe failed.")
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
            onSelect={setSelectedDriveRoot}
            isLoading={isLoading}
            onRefresh={() => void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading)}
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

          <ToolOutput result={result} runningLabel={isRunning ? "Windows cipher is running. This can take a long time on large drives." : undefined} />
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
      />
    </MaintenanceFrame>
  )
}

export function DriveOptimizerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [drives, setDrives] = useState<MaintenanceDrive[]>([])
  const [selectedDriveRoot, setSelectedDriveRoot] = useState("")
  const [isLoading, setIsLoading] = useState(true)
  const [isRunning, setIsRunning] = useState(false)
  const [result, setResult] = useState<MaintenanceToolResult | null>(null)
  const selectedDrive = useSelectedDrive(drives, selectedDriveRoot)

  useEffect(() => {
    void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading)
  }, [invoke])

  async function runOptimization(mode: "analyze" | "optimize") {
    if (!selectedDrive) {
      toast.error("Select a drive first.")
      return
    }

    setIsRunning(true)
    setResult(null)
    try {
      const response = await invoke<MaintenanceToolResult>("maintenance.optimizeDrive", {
        driveRoot: selectedDrive.rootPath,
        mode,
      })
      setResult(response)
      toast[response.ok ? "success" : "error"](response.message)
    } catch (error) {
      showMaintenanceError(error, "Drive optimization failed.")
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
            onSelect={setSelectedDriveRoot}
            isLoading={isLoading}
            onRefresh={() => void loadDrives(invoke, setDrives, setSelectedDriveRoot, setIsLoading)}
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

          <ToolOutput result={result} runningLabel={isRunning ? "Windows defrag is running with the selected mode." : undefined} />
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
  const [isScanning, setIsScanning] = useState(true)
  const [isCleaning, setIsCleaning] = useState(false)
  const [lastRun, setLastRun] = useState<CleanupRunResult | null>(null)
  const [savedSelectionLoaded, setSavedSelectionLoaded] = useState(false)
  const categories = scan?.categories ?? []
  const selectedCategories = categories.filter((category) => selectedIds.includes(category.id))
  const cleanupNeedsAdministrator = selectedCategories.some((category) => category.requiresAdministrator)
  const canRunCleanup = selectedIds.length > 0 && !isScanning && !isCleaning && (!cleanupNeedsAdministrator || isAdministrator)
  const groupSummaries = useMemo(() => buildCleanupGroupSummaries(categories, selectedIds), [categories, selectedIds])
  const visibleCategories = activeGroup === "All"
    ? categories
    : categories.filter((category) => category.group === activeGroup)

  useEffect(() => {
    void scanCleanup()
  }, [])

  async function scanCleanup() {
    setIsScanning(true)
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
      toast.error(error instanceof Error ? error.message : "Cleanup scan failed.")
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
    updateSelectedIds((current) => selected
      ? Array.from(new Set([...current, categoryId]))
      : current.filter((id) => id !== categoryId))
  }

  function selectGroup(group: string, selected: boolean) {
    const groupIds = categories
      .filter((category) => group === "All" || category.group === group)
      .filter((category) => category.isEnabled)
      .map((category) => category.id)

    updateSelectedIds((current) => selected
      ? Array.from(new Set([...current, ...groupIds]))
      : current.filter((id) => !groupIds.includes(id)))
  }

  async function runCleanup() {
    if (selectedIds.length === 0) {
      toast.error("Select at least one cleanup category.")
      return
    }

    setIsCleaning(true)
    try {
      const response = await invoke<CleanupRunResult>("maintenance.runCleanup", { categoryIds: selectedIds })
      setLastRun(response)
      await scanCleanup()
      toast.success(`Cleaned ${response.freedDisplay}.`)
    } catch (error) {
      showMaintenanceError(error, "Cleanup failed.")
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
              <MetricTile label="Recoverable" value={scan?.totalDisplay ?? "Scanning"} />
              <MetricTile label="Selected areas" value={String(selectedIds.length)} />
              <MetricTile label="Last cleaned" value={lastRun?.freedDisplay ?? "Not run"} good={Boolean(lastRun?.freedBytes)} />
            </div>
            <div className="mt-4 flex flex-wrap gap-2">
              <Button variant="secondary" onClick={() => void scanCleanup()} disabled={isScanning || isCleaning}>
                <ScanLine data-icon="inline-start" />
                {isScanning ? "Scanning" : "Scan"}
              </Button>
              <Button onClick={() => void runCleanup()} disabled={!canRunCleanup}>
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
                  const selected = selectedIds.includes(category.id)
                  return (
                    <label
                      key={category.id}
                      className={cn(
                        "grid cursor-pointer grid-cols-[minmax(0,1fr)_120px_120px] items-start gap-3 border-b border-border/70 px-3 py-3 transition-colors last:border-b-0 hover:bg-bg-surface-hover/45",
                        selected && "bg-background/35"
                      )}
                    >
                      <div className="flex min-w-0 items-start gap-3">
                        <Checkbox
                          checked={selected}
                          disabled={!category.isEnabled || isCleaning}
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
                    {isScanning ? "Scanning cleanup areas..." : "No cleanup areas are available for this group."}
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
    </MaintenanceFrame>
  )
}

export function RegistryFixerPage({ invoke, isAdministrator, onRestartAsAdministrator }: MaintenancePageProps) {
  const [scan, setScan] = useState<RegistryScanResult | null>(null)
  const [selectedIds, setSelectedIds] = useState<string[]>([])
  const [showRegistryConfirmation, setShowRegistryConfirmation] = useState(false)
  const [skipRegistryConfirmation, setSkipRegistryConfirmation] = useStoredBoolean("filelocker.skipRegistryFixConfirmation")
  const [isScanning, setIsScanning] = useState(true)
  const [isFixing, setIsFixing] = useState(false)
  const [lastClean, setLastClean] = useState<RegistryCleanResult | null>(null)
  const registryNeedsAdministrator = scan?.issues.some((issue) => selectedIds.includes(issue.id) && issue.hive === "HKLM") ?? false
  const canFix = selectedIds.length > 0 && !isFixing && (!registryNeedsAdministrator || isAdministrator)

  useEffect(() => {
    void scanRegistry()
  }, [])

  async function scanRegistry() {
    setIsScanning(true)
    try {
      const response = await invoke<RegistryScanResult>("maintenance.scanRegistry", {})
      setScan(response)
      setSelectedIds((current) => current.length > 0 ? current.filter((id) => response.issues.some((issue) => issue.id === id)) : response.issues.filter((issue) => issue.canClean).map((issue) => issue.id))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Registry scan failed.")
    } finally {
      setIsScanning(false)
    }
  }

  function requestFixRegistry() {
    if (selectedIds.length === 0) {
      toast.error("Select at least one registry item.")
      return
    }

    if (skipRegistryConfirmation) {
      void fixRegistry()
      return
    }

    setShowRegistryConfirmation(true)
  }

  async function fixRegistry() {
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
              <MetricTile label="Issues found" value={scan ? String(scan.issueCount) : "..."} />
              <MetricTile label="Selected fixes" value={String(selectedIds.length)} />
              <MetricTile label="Last fixed" value={lastClean ? String(lastClean.cleanedCount) : "0"} good={Boolean(lastClean?.cleanedCount)} />
            </div>

            <div className="mt-5 flex flex-col gap-3">
              {(scan?.issues ?? []).map((issue) => {
                const selected = selectedIds.includes(issue.id)
                return (
              <label key={issue.id} className="flex items-start gap-2.5 rounded-md border border-border/80 bg-background/35 px-3 py-3">
                    <Checkbox
                      checked={selected}
                      disabled={!issue.canClean || isFixing}
                      onCheckedChange={(checked) => {
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
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: string
  description: string
  confirmLabel: string
  onConfirm: () => void
  onDontShowAgain: () => void
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
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="secondary" onClick={onDontShowAgain}>
            Don't show again
          </AlertDialogAction>
          <AlertDialogAction variant="destructive" onClick={onConfirm}>
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
}: {
  label: string
  selected: boolean
  total: number
  checked: number
  onClick: () => void
  onToggle: (checked: boolean) => void
}) {
  const allChecked = total > 0 && checked === total
  return (
    <div
      className={cn(
        "grid grid-cols-[auto_minmax(0,1fr)] items-center gap-3 rounded-md border border-border/80 bg-background/30 px-3 py-3 transition-colors",
        selected && "border-accent/70 bg-accent/10"
      )}
    >
      <Checkbox checked={allChecked} onCheckedChange={(value) => onToggle(value === true)} />
      <button type="button" className="min-w-0 text-left" onClick={onClick}>
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
  onRefresh,
}: {
  drives: MaintenanceDrive[]
  selectedDriveRoot: string
  onSelect: (root: string) => void
  isLoading: boolean
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
          {isLoading ? "Loading drives..." : "No fixed or removable drives are available."}
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

function ToolOutput({ result, runningLabel }: { result: MaintenanceToolResult | null; runningLabel?: string }) {
  if (!result && !runningLabel) {
    return null
  }

  return (
    <MaintenanceSection title="Tool Output" icon={Download}>
      {runningLabel ? (
        <div className="mb-4 rounded-md border border-accent/25 bg-accent/8 px-4 py-3 text-sm text-secondary">{runningLabel}</div>
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
    () => drives.find((drive) => drive.rootPath === selectedDriveRoot) ?? drives.find((drive) => drive.isReady) ?? null,
    [drives, selectedDriveRoot]
  )
}

function buildCleanupGroupSummaries(categories: CleanupCategory[], selectedIds: string[]) {
  return Array.from(new Set(categories.map((category) => category.group)))
    .sort((a, b) => a.localeCompare(b))
    .map((group) => {
      const groupCategories = categories.filter((category) => category.group === group)
      return {
        group,
        total: groupCategories.length,
        checked: groupCategories.filter((category) => selectedIds.includes(category.id)).length,
      }
    })
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
  setIsLoading: (value: boolean) => void
) {
  setIsLoading(true)
  try {
    const response = await invoke<MaintenanceDriveList>("maintenance.getDrives", {})
    setDrives(response.drives)
    setSelectedDriveRoot((current) => current || response.drives.find((drive) => drive.isReady)?.rootPath || "")
  } catch (error) {
    toast.error(error instanceof Error ? error.message : "Unable to load drives.")
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
  toast.error(permissionRelated && !alreadyActionable ? `${message} Restart FileLocker as Administrator and try again.` : message)
}
