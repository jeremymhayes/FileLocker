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

export type DashboardState = {
  incognitoMode: boolean
  protectedFilesCount: string
  protectedFilesDeltaText: string
  protectedFilesSubtitle: string
  storageSavedDisplay: string
  storageSavedDeltaText: string
  storageSavedSubtitle: string
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

export type BridgeEvent = ProgressEvent | DroppedPathsEvent

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
