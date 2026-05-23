export type PageKey =
  | "dashboard"
  | "encrypt"
  | "decrypt"
  | "hash"
  | "encode"
  | "metadata"
  | "secure-delete"
  | "custom-clean"
  | "partition-cleaner"
  | "drive-optimizer"
  | "registry-fixer"
  | "startup-manager"
  | "app-manager"
  | "settings"
  | "about"
  | "security-guide"

export type FileOperationResult = {
  sourcePath: string
  outputPath?: string
  backupPath?: string
  status: string
  message?: string
  originalRetained: boolean
  outputVerified: boolean
  originalSizeBytes?: number
  outputSizeBytes?: number
  compressionRequested: boolean
  compressionApplied: boolean
  compressionReason?: string
  elapsedMilliseconds?: number
  failureCategory?: string
  hashValue?: string
}

export type HistoryEntry = {
  id: string
  timestampUtc: string
  operation: string
  profileName: string
  successCount: number
  failureCount: number
  cancelled: boolean
  elapsedMilliseconds?: number
  results: FileOperationResult[]
}

export type StorageBreakdownItem = {
  label: string
  bytes: number
  display: string
  percent: number
  tone: "blue" | "teal" | "purple" | "orange" | "red" | "green"
}

export type WeeklyOperationBucket = {
  date: string
  label: string
  count: number
  failedCount: number
}

export type DashboardState = {
  incognitoMode: boolean
  protectedFilesCount: string
  protectedFilesDeltaText: string
  protectedFilesSubtitle: string
  storageSavedDisplay: string
  storageSavedDeltaText: string
  storageSavedSubtitle: string
  storageSavedBytes: number
  storageAddedBytes: number
  storageTrackedFiles: number
  compressionRequestedCount: number
  compressionAppliedCount: number
  storageBreakdown: StorageBreakdownItem[]
  operationsThisWeekCount: number
  successfulOperationsThisWeekCount: number
  failedOperationsThisWeekCount: number
  operationsThisWeek: WeeklyOperationBucket[]
  lastOperationName: string
  lastOperationFileName: string
  lastOperationTimeDisplay: string
  securityStatusTitle: string
  securityStatusSubtitle: string
  securityStatusDetail: string
  recentFiles: RecentFile[]
  history: HistoryEntry[]
}

export type RecentFile = {
  name: string
  fileIconText: string
  type: string
  status: string
  lastModified: string
}

export type Preferences = {
  incognitoMode: boolean
  includeFullPathsInExports: boolean
  outputTimestampPolicy: string
  useCustomEncryptOutputDirectory: boolean
  customEncryptOutputDirectory: string
  useCustomDecryptOutputDirectory: boolean
  customDecryptOutputDirectory: string
  themePreference: string
}

export type UpdateSettings = {
  autoCheckEnabled: boolean
  lastCheckedUtc?: string
  skippedVersion?: string
}

export type ExplorerIntegrationState = {
  isRegistered: boolean
  canManage: boolean
  statusMessage: string
}

export type UpdateRelease = {
  version: string
  displayVersion: string
  tagName: string
  htmlUrl: string
  notes: string
  installerFileName: string
  installerDownloadUrl: string
  sha256DigestHex?: string
  sha256DigestDownloadUrl?: string
}

export type UpdateCheckResult = {
  currentVersion: string
  isUpdateAvailable: boolean
  statusMessage: string
  release?: UpdateRelease
}

export type SettingsState = {
  preferences: Preferences
  updates: UpdateSettings
  explorerIntegration: ExplorerIntegrationState
}

export type InitialState = {
  app: {
    name: string
    version: string
    repositoryUrl: string
    launchPaths: string[]
    launchAction?: string
    isAdministrator: boolean
    canRestartAsAdministrator: boolean
    isDebug: boolean
  }
  dashboard: DashboardState
  settings: SettingsState
}

export type ProgressEvent = {
  type: "progress"
  operationId: string
  path: string
  fileName: string
  percent: number
  status: string
}

export type DroppedPathsEvent = {
  type: "droppedPaths"
  paths: string[]
}

export type DropErrorEvent = {
  type: "dropError"
  message: string
}

export type BridgeEvent = ProgressEvent | DroppedPathsEvent | DropErrorEvent

export type FileOperationRequest = {
  operationId: string
  paths: string[]
  password: string
  keyfilePath?: string
  recoveryKey?: string
  compressFiles: boolean
  scrambleNames: boolean
  useSteganography: boolean
  packageFolders: boolean
  removeOriginalsAfterSuccess: boolean
  secureDeleteOriginals: boolean
  verifyAfterWrite: boolean
  saveNextToSource: boolean
  encryptOutputDirectory?: string
  saveNextToEncrypted: boolean
  decryptOutputDirectory?: string
  restoreOriginalFilenames: boolean
  preserveFolderStructure: boolean
  outputTimestampPolicy: string
  backupFolderPath?: string
  profileName: string
  metadataLabel?: string
  metadataNotes?: string
  randomizeMetadata: boolean
  metadataCreatedText?: string
  metadataModifiedText?: string
  deleteConfirmation?: string
}

export type StartupItem = {
  id: string
  name: string
  source: string
  location: string
  command: string
  targetPath?: string
  isEnabled: boolean
  requiresAdministrator: boolean
  canToggle: boolean
  status: string
  warnings: string[]
}

export type StartupScanResult = {
  items: StartupItem[]
  enabledCount: number
  disabledCount: number
  warnings: string[]
}

export type StartupToggleResult = {
  item: StartupItem
  isEnabled: boolean
  backupPath: string
  message: string
}

export type InstalledApp = {
  id: string
  displayName: string
  publisher: string
  version: string
  installDate: string
  estimatedSizeBytes: number
  estimatedSizeDisplay: string
  installLocation: string
  uninstallCommand: string
  sourceHive: string
  architecture: string
  requiresAdministrator: boolean
  canLaunchUninstaller: boolean
  registryKeyPath: string
}

export type InstalledAppsScanResult = {
  apps: InstalledApp[]
  appCount: number
  warnings: string[]
}

export type UninstallerLaunchResult = {
  started: boolean
  appId: string
  displayName: string
  message: string
}

export type AppLeftoverCategory = {
  id: string
  appId: string
  appDisplayName: string
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

export type AppLeftoverScanResult = {
  categories: AppLeftoverCategory[]
  totalBytes: number
  totalDisplay: string
  totalFiles: number
  skippedItems: number
  warnings: string[]
}

export type AppLeftoverCleanResult = {
  cleanedCount: number
  failedCount: number
  freedBytes: number
  freedDisplay: string
  cleanedCategories: AppLeftoverCategory[]
  failures: Array<{ categoryId: string; appDisplayName: string; path: string; message: string }>
  scan: AppLeftoverScanResult
}
