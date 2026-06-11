/**
 * Dev-only bridge mock.
 *
 * FileLocker's real bridge talks to the WinUI 3 / WebView2 host through
 * `window.chrome.webview`. Outside that host (a plain browser, Playwright) the
 * bridge rejects every call, so nothing past the loading screen renders.
 *
 * This module fabricates a `window.chrome.webview` that answers bridge requests
 * with canned, plausible *local* data so the UI can be reviewed in a browser.
 *
 * It is imported only from `main.tsx` behind `import.meta.env.DEV` and is never
 * bundled into a production build. It must never mask a real bridge failure.
 */

import type {
  AppLeftoverScanResult,
  DashboardState,
  FreeSpaceWipeStatus,
  InitialState,
  InstalledAppsScanResult,
  SettingsState,
  StartupScanResult,
} from "@/types/bridge"
import { DEFAULT_ENCRYPTION_ALGORITHM_ID, FALLBACK_ENCRYPTION_ALGORITHMS } from "@/lib/encryptionAlgorithms"
import { DEFAULT_HASH_ALGORITHM_ID, getHashAlgorithm } from "@/lib/hashAlgorithms"
import { OUTPUT_TIMESTAMP_POLICIES } from "@/lib/outputTimestampPolicies"

type BridgeRequest = { id: string; action: string; payload: unknown }
type MessageListener = (event: MessageEvent) => void
const defaultHashAlgorithm = getHashAlgorithm(DEFAULT_HASH_ALGORITHM_ID)!
const mockHashValues: Record<string, string> = {
  "SHA-256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  "SHA-512": "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e",
}
const MAX_ENCODE_TEXT_INPUT_CHARS = 1024 * 1024
const ENCODE_TEXT_FORMATS = ["Base64", "URL", "Hex", "HTML Entities", "UTF-8"] as const
type EncodeTextFormat = (typeof ENCODE_TEXT_FORMATS)[number]
type EncodeTextModeId = "encode" | "decode"

const defaultSettings: SettingsState = {
  preferences: {
    incognitoMode: false,
    includeFullPathsInExports: false,
    outputTimestampPolicy: OUTPUT_TIMESTAMP_POLICIES[1].id,
    useCustomEncryptOutputDirectory: false,
    customEncryptOutputDirectory: "",
    useCustomDecryptOutputDirectory: true,
    customDecryptOutputDirectory: String.raw`C:\Users\demo\Documents\FileLocker\Decrypted`,
    themePreference: "Dark",
    accentTheme: "blue",
  },
  updates: {
    autoCheckEnabled: true,
    lastCheckedUtc: new Date().toISOString(),
    skippedVersion: undefined,
  },
  explorerIntegration: {
    isRegistered: true,
    canManage: true,
    statusMessage: "Right-click integration is registered for the current user.",
  },
}

let settings: SettingsState = cloneSettings(defaultSettings)
let mockWipeStatus: FreeSpaceWipeStatus | null = null

const dashboard: DashboardState = {
  incognitoMode: false,
  protectedFilesCount: "128",
  protectedFilesDeltaText: "+6 this week",
  protectedFilesSubtitle: "Files encrypted on this PC",
  storageSavedDisplay: "2.4 GB",
  storageSavedDeltaText: "+180 MB this week",
  storageSavedSubtitle: "Reclaimed by compression",
  storageSavedBytes: 2_576_980_377,
  storageAddedBytes: 412_316_860,
  storageTrackedFiles: 128,
  compressionRequestedCount: 96,
  compressionAppliedCount: 81,
  storageBreakdown: [
    { label: "Documents", bytes: 1_073_741_824, display: "1.0 GB", percent: 42, tone: "blue" },
    { label: "Images", bytes: 730_000_000, display: "696 MB", percent: 28, tone: "teal" },
    { label: "Archives", bytes: 520_000_000, display: "496 MB", percent: 20, tone: "purple" },
    { label: "Other", bytes: 253_238_553, display: "242 MB", percent: 10, tone: "orange" },
  ],
  operationsThisWeekCount: 23,
  successfulOperationsThisWeekCount: 22,
  failedOperationsThisWeekCount: 1,
  operationsThisWeek: [
    { date: "2026-05-22", label: "Fri", count: 2, failedCount: 0 },
    { date: "2026-05-23", label: "Sat", count: 0, failedCount: 0 },
    { date: "2026-05-24", label: "Sun", count: 4, failedCount: 0 },
    { date: "2026-05-25", label: "Mon", count: 6, failedCount: 1 },
    { date: "2026-05-26", label: "Tue", count: 3, failedCount: 0 },
    { date: "2026-05-27", label: "Wed", count: 5, failedCount: 0 },
    { date: "2026-05-28", label: "Thu", count: 3, failedCount: 0 },
  ],
  lastOperationName: "Encrypt Files",
  lastOperationFileName: "Q2-financials.xlsx",
  lastOperationTimeDisplay: "12 minutes ago",
  securityStatusTitle: "Protected",
  securityStatusSubtitle: "No action needed",
  securityStatusDetail: `All tracked files are encrypted with ${DEFAULT_ENCRYPTION_ALGORITHM_ID}. Originals were removed after verification.`,
  recentFiles: [
    { name: "Q2-financials.xlsx.locked", fileIconText: "xlsx", type: "Encrypted", status: "Encrypted", lastModified: "12 minutes ago" },
    { name: "passport-scan.pdf.locked", fileIconText: "pdf", type: "Encrypted", status: "Encrypted", lastModified: "1 hour ago" },
    { name: "tax-2025.zip.locked", fileIconText: "zip", type: "Encrypted", status: "Encrypted", lastModified: "Yesterday" },
    { name: "client-notes.docx", fileIconText: "docx", type: "Decrypted", status: "Decrypted", lastModified: "2 days ago" },
    { name: "backup-keys.txt.locked", fileIconText: "txt", type: "Encrypted", status: "Encrypted", lastModified: "3 days ago" },
  ],
  history: [
    {
      id: "h1",
      timestampUtc: new Date(Date.now() - 12 * 60 * 1000).toISOString(),
      operation: "Encrypt",
      profileName: "Default",
      algorithm: DEFAULT_ENCRYPTION_ALGORITHM_ID,
      keySizeBits: 256,
      successCount: 1,
      failureCount: 0,
      cancelled: false,
      elapsedMilliseconds: 842,
      results: [
        {
          sourcePath: "C:\\Users\\you\\Documents\\Q2-financials.xlsx",
          outputPath: "C:\\Users\\you\\Documents\\Q2-financials.xlsx.locked",
          status: "Completed",
          originalRetained: false,
          outputVerified: true,
          originalSizeBytes: 184_320,
          outputSizeBytes: 96_240,
          compressionRequested: true,
          compressionApplied: true,
          elapsedMilliseconds: 842,
          algorithm: DEFAULT_ENCRYPTION_ALGORITHM_ID,
          keySizeBits: 256,
        },
      ],
    },
    {
      id: "h2",
      timestampUtc: new Date(Date.now() - 60 * 60 * 1000).toISOString(),
      operation: "Hash",
      profileName: defaultHashAlgorithm.label,
      algorithm: defaultHashAlgorithm.id,
      keySizeBits: defaultHashAlgorithm.digestBits,
      successCount: 3,
      failureCount: 0,
      cancelled: false,
      elapsedMilliseconds: 410,
      results: [],
    },
  ],
}

const initialState: InitialState = {
  app: {
    name: "FileLocker",
    version: "1.3.1.0",
    repositoryUrl: "https://github.com/jeremymhayes/FileLocker",
    launchPaths: [],
    launchAction: undefined,
    isAdministrator: false,
    canRestartAsAdministrator: true,
    isDebug: true,
    encryptionAlgorithms: FALLBACK_ENCRYPTION_ALGORITHMS,
  },
  dashboard,
  settings,
}

let startupScan: StartupScanResult = {
  enabledCount: 4,
  disabledCount: 2,
  brokenCount: 1,
  advancedCount: 1,
  restoreRecordCount: 1,
  ignoredCount: 0,
  restoreRecords: [],
  warnings: [],
  items: [
    mockStartupItem({ name: "OneDrive", publisher: "Microsoft Corporation", isEnabled: true, isMicrosoftSigned: true, startupImpact: "Medium", category: "Startup Apps" }),
    mockStartupItem({ name: "Spotify", publisher: "Spotify AB", isEnabled: true, startupImpact: "High", category: "Startup Apps" }),
    mockStartupItem({ name: "Steam", publisher: "Valve Corp.", isEnabled: false, startupImpact: "High", category: "Startup Apps", status: "Disabled" }),
    mockStartupItem({ name: "Discord", publisher: "Discord Inc.", isEnabled: true, startupImpact: "Medium", category: "Startup Apps" }),
    mockStartupItem({ name: "NahimicSvc", publisher: "Unknown", isEnabled: true, startupImpact: "Low", category: "Startup Apps", signatureStatus: "Unsigned", riskLevel: "Review" }),
    mockStartupItem({ name: "LegacyUpdater", publisher: "", isEnabled: true, category: "Broken Startup Items", status: "Broken", warnings: ["Target file no longer exists."], riskLevel: "Broken" }),
    mockStartupItem({ name: "Run\\HKLM hook", publisher: "Unknown", isEnabled: true, category: "Advanced Startup Hooks", scope: "Machine" }),
  ],
}

// Tiny stand-in "logos" so the App Manager icon path can be reviewed in a
// browser. The real bridge returns PNG data URIs extracted from each app's
// registry DisplayIcon; apps without one fall back to the initials mark.
function mockLogo(background: string, glyph: string): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect width="32" height="32" rx="7" fill="${background}"/><text x="16" y="22" font-family="Segoe UI, sans-serif" font-size="16" font-weight="700" fill="#fff" text-anchor="middle">${glyph}</text></svg>`
  return "data:image/svg+xml;base64," + btoa(svg)
}

const installedAppsScan: InstalledAppsScanResult = {
  appCount: 5,
  warnings: [],
  apps: [
    mockApp({ displayName: "Visual Studio 2022", publisher: "Microsoft Corporation", version: "17.9.6", estimatedSizeBytes: 6_871_947_674, estimatedSizeDisplay: "6.4 GB", iconDataUri: mockLogo("#8b5cf6", "VS") }),
    mockApp({ displayName: "Google Chrome", publisher: "Google LLC", version: "124.0.6367.91", estimatedSizeBytes: 612_368_384, estimatedSizeDisplay: "584 MB", iconDataUri: mockLogo("#2563eb", "C") }),
    mockApp({ displayName: "Spotify", publisher: "Spotify AB", version: "1.2.37", estimatedSizeBytes: 304_087_040, estimatedSizeDisplay: "290 MB", iconDataUri: mockLogo("#1db954", "S") }),
    mockApp({ displayName: "7-Zip 23.01", publisher: "Igor Pavlov", version: "23.01", estimatedSizeBytes: 5_242_880, estimatedSizeDisplay: "5.0 MB" }),
    mockApp({ displayName: "Notepad++", publisher: "Notepad++ Team", version: "8.6.4", estimatedSizeBytes: 16_777_216, estimatedSizeDisplay: "16 MB" }),
  ],
}

let appLeftoverScan: AppLeftoverScanResult = {
  totalBytes: 268_435_456,
  totalDisplay: "256 MB",
  totalFiles: 1_204,
  skippedItems: 2,
  warnings: [],
  categories: [
    { id: "c1", appId: "a1", appDisplayName: "Spotify", group: "Cache", label: "App cache", description: "Temporary media and image cache.", path: "C:\\Users\\you\\AppData\\Local\\Spotify\\Storage", sizeBytes: 167_772_160, sizeDisplay: "160 MB", fileCount: 842, skippedCount: 0, isEnabled: true, requiresAdministrator: false, defaultSelected: true, status: "Ready", warnings: [] },
    { id: "c2", appId: "a1", appDisplayName: "Spotify", group: "Logs", label: "Log files", description: "Diagnostic logs safe to remove.", path: "C:\\Users\\you\\AppData\\Roaming\\Spotify\\Logs", sizeBytes: 25_165_824, sizeDisplay: "24 MB", fileCount: 120, skippedCount: 0, isEnabled: true, requiresAdministrator: false, defaultSelected: true, status: "Ready", warnings: [] },
    { id: "c3", appId: "a2", appDisplayName: "Chrome", group: "Cache", label: "GPU cache", description: "Rendering cache rebuilt on next launch.", path: "C:\\Users\\you\\AppData\\Local\\Google\\Chrome\\GPUCache", sizeBytes: 75_497_472, sizeDisplay: "72 MB", fileCount: 242, skippedCount: 2, isEnabled: false, requiresAdministrator: false, defaultSelected: false, status: "Ready", warnings: ["2 files are in use and will be skipped."] },
  ],
}

const cleanupScan = {
  totalBytes: 3_435_973_836,
  totalDisplay: "3.2 GB",
  totalFiles: 12_480,
  skippedItems: 3,
  categories: [
    { id: "k1", group: "Windows", label: "Temporary files", description: "Files in the Windows and user temp folders.", path: "C:\\Windows\\Temp", sizeBytes: 1_288_490_188, sizeDisplay: "1.2 GB", fileCount: 4_210, skippedCount: 1, isEnabled: true, requiresAdministrator: false, defaultSelected: true, status: "Ready", warnings: [], safetyLevel: "Safe", sizeKnown: true, locations: ["C:\\Windows\\Temp", "%TEMP%"], removes: ["Cached installers", "Temp files"], keeps: ["Open files"], recommendation: "Safe to remove", unavailableReason: "" },
    { id: "k2", group: "Windows", label: "Windows Update cache", description: "Downloaded update packages already installed.", path: "C:\\Windows\\SoftwareDistribution", sizeBytes: 858_993_459, sizeDisplay: "819 MB", fileCount: 312, skippedCount: 0, isEnabled: true, requiresAdministrator: true, defaultSelected: false, status: "Ready", warnings: [], safetyLevel: "Caution", sizeKnown: true, locations: ["C:\\Windows\\SoftwareDistribution\\Download"], removes: ["Installed update packages"], keeps: ["Pending updates"], recommendation: "Review before removing", unavailableReason: "" },
    { id: "k3", group: "Browsers", label: "Edge cache", description: "Cached web content for Microsoft Edge.", path: "%LOCALAPPDATA%\\Microsoft\\Edge", sizeBytes: 644_245_094, sizeDisplay: "614 MB", fileCount: 5_120, skippedCount: 2, isEnabled: true, requiresAdministrator: false, defaultSelected: true, status: "Ready", warnings: [], safetyLevel: "Safe", sizeKnown: true, locations: ["%LOCALAPPDATA%\\Microsoft\\Edge\\User Data\\Default\\Cache"], removes: ["Cached pages, images"], keeps: ["Cookies", "Passwords"], recommendation: "Safe to remove", unavailableReason: "" },
    { id: "k4", group: "Applications", label: "Thumbnail cache", description: "Explorer thumbnail database, rebuilt on demand.", path: "%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer", sizeBytes: 644_245_095, sizeDisplay: "614 MB", fileCount: 2_838, skippedCount: 0, isEnabled: true, requiresAdministrator: false, defaultSelected: true, status: "Ready", warnings: [], safetyLevel: "Safe", sizeKnown: true, locations: ["%LOCALAPPDATA%\\Microsoft\\Windows\\Explorer"], removes: ["Thumbnail database"], keeps: [], recommendation: "Safe to remove", unavailableReason: "" },
  ],
}

const registryScan = {
  issueCount: 4,
  status: "4 issues found",
  warnings: [],
  issues: [
    { id: "r1", hive: "HKCU", keyPath: "Software\\Microsoft\\Windows\\CurrentVersion\\Run", valueName: "OldApp", kind: "Invalid startup entry", displayName: "OldApp startup entry", targetPath: "C:\\Program Files\\OldApp\\old.exe", reason: "Target executable no longer exists.", severity: "Low", canClean: true, category: "Startup" },
    { id: "r2", hive: "HKLM", keyPath: "SOFTWARE\\Classes\\.xyz", kind: "Orphaned file association", displayName: ".xyz file association", targetPath: "xyzfile", reason: "Associated program is not installed.", severity: "Low", canClean: true, category: "File associations" },
    { id: "r3", hive: "HKCU", keyPath: "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Ghost", subKeyName: "Ghost", kind: "Leftover uninstall entry", displayName: "Ghost (uninstall)", targetPath: "", reason: "Application files were removed but the entry remains.", severity: "Medium", canClean: true, category: "Uninstall" },
    { id: "r4", hive: "HKCR", keyPath: "CLSID\\{00000000-0000-0000-0000-000000000000}", kind: "Missing COM server", displayName: "Orphaned COM reference", targetPath: "C:\\Windows\\System32\\missing.dll", reason: "Referenced DLL is missing.", severity: "Low", canClean: true, category: "COM/ActiveX" },
  ],
}

function handle(action: string, payload: unknown): unknown {
  switch (action) {
    case "app.getInitialState":
      return { ...initialState, settings: cloneSettings(settings) }
    case "settings.get":
      return cloneSettings(settings)
    case "settings.save":
      settings = payload ? cloneSettings(payload as SettingsState) : settings
      return cloneSettings(settings)
    case "settings.reset":
      settings = cloneSettings(defaultSettings)
      return cloneSettings(settings)
    case "history.clear":
      return { dashboard }
    case "history.export":
      return { exportPath: "C:\\Users\\you\\Documents\\filelocker-history.csv", fileName: "filelocker-history.csv", recordCount: dashboard.history.length }
    case "updates.check":
    case "updates.testStartupCheck":
      return { currentVersion: "1.3.1.0", isUpdateAvailable: false, statusMessage: "FileLocker is up to date." }
    case "updates.skip":
      settings = { ...settings, updates: { ...settings.updates, skippedVersion: (payload as { version?: string } | null)?.version } }
      return cloneSettings(settings)
    case "updates.clearSkip":
      settings = { ...settings, updates: { ...settings.updates, skippedVersion: undefined } }
      return cloneSettings(settings)
    case "updates.testDialog":
      return { tested: true }
    case "updates.download":
      return { installerPath: "C:\\Users\\you\\AppData\\Local\\FileLocker\\Updater\\Downloads\\FileLocker-Setup-1.3.1.0.exe", fileName: "FileLocker-Setup-1.3.1.0.exe" }
    case "updates.install":
      return { installerPath: "C:\\Users\\you\\AppData\\Local\\FileLocker\\Updater\\Downloads\\FileLocker-Setup-1.3.1.0.exe", fileName: "FileLocker-Setup-1.3.1.0.exe" }
    case "shell.setExplorerIntegration": {
      const enabled = (payload as { enabled?: boolean } | null)?.enabled === true
      settings = {
        ...settings,
        explorerIntegration: {
          ...settings.explorerIntegration,
          isRegistered: enabled,
          statusMessage: enabled
            ? "Right-click integration is registered for the current user."
            : "Right-click integration is not registered for the current user.",
        },
      }
      return cloneSettings(settings)
    }
    case "files.pickFiles":
      return { paths: ["C:\\Users\\you\\Documents\\sample-document.pdf", "C:\\Users\\you\\Pictures\\photo.png"] }
    case "files.pickFolder":
      return { path: "C:\\Users\\you\\Documents" }
    case "files.suggestEncryptOutput":
      return { suggestedPath: "C:\\Users\\you\\Documents\\FileLocker Locked", hasFolderSelection: true, folderCount: 1 }
    case "files.describePaths":
      return {
        items: [
          { fullPath: "C:\\Users\\you\\Documents\\sample-document.pdf", displayName: "sample-document.pdf", itemType: "PDF Document", sizeBytes: 184_320, sizeDisplay: "180 KB", isDirectory: false, details: "Modified 2 days ago" },
          { fullPath: "C:\\Users\\you\\Pictures\\photo.png", displayName: "photo.png", itemType: "PNG Image", sizeBytes: 1_048_576, sizeDisplay: "1.0 MB", isDirectory: false, details: "Modified yesterday" },
        ],
        totalSizeBytes: 1_232_896,
        totalSizeDisplay: "1.2 MB",
        warnings: [],
      }
    case "text.convert": {
      const { input, mode, format, preserveLineBreaks } = normalizeTextConversionPayload(payload)
      const output = convertTextForMock(input, mode, format, preserveLineBreaks)
      return { output, inputLength: input.length, outputLength: output.length }
    }
    case "hash.compute": {
      const requestedAlgorithm = getRequestedHashAlgorithm(payload)
      return {
        operationId: "mock-hash",
        path: "C:\\Users\\you\\Documents\\sample-document.pdf",
        fileName: "sample-document.pdf",
        algorithm: requestedAlgorithm.id,
        hash: mockHashValues[requestedAlgorithm.id] ?? mockHashValues[defaultHashAlgorithm.id],
        digestBits: requestedAlgorithm.digestBits,
        expectedLength: requestedAlgorithm.expectedLength,
      }
    }
    case "hash.verify":
      return getMockHashVerification(payload)
    case "hash.manifestCreate": {
      const requestedAlgorithm = getRequestedHashAlgorithm(payload)
      const extension = requestedAlgorithm.id === "SHA-512" ? "sha512" : "sha256"
      return { manifestPath: `C:\\Users\\you\\Documents\\hashes.${extension}`, fileName: `hashes.${extension}`, algorithm: requestedAlgorithm.id, fileCount: 2 }
    }
    case "hash.manifestVerify":
      return {
        manifestPath: (payload as { manifestPath?: string } | null)?.manifestPath ?? "C:\\Users\\you\\Documents\\hashes.sha256",
        entryCount: 2,
        matchedCount: 2,
        mismatchedCount: 0,
        missingCount: 0,
        status: "Verified",
      }
    case "crypto.encryptFiles":
      return getMockFileOperation(payload, "Encrypted")
    case "crypto.decryptFiles":
      return getMockFileOperation(payload, "Decrypted")
    case "crypto.verifyPayload":
      return getMockFileOperation(payload, "Verified")
    case "secureDelete.delete":
      return getMockFileOperation(payload, "Deleted")
    case "metadata.inspect":
      return {
        activeFilePath: "C:\\Users\\you\\Pictures\\photo.png",
        mode: "Review",
        writeSupportEnabled: true,
        files: [
          { displayName: "photo.png", fullPath: "C:\\Users\\you\\Pictures\\photo.png", fileType: "PNG Image", sizeDisplay: "1.0 MB", metadataTagCount: 12, statusDisplay: "12 tags", isSupported: true },
        ],
        file: { displayName: "photo.png", fullPath: "C:\\Users\\you\\Pictures\\photo.png", fileType: "PNG Image", sizeDisplay: "1.0 MB", metadataTagCount: 12, statusDisplay: "12 tags", isSupported: true },
        categories: [
          { label: "Camera", tags: [ { name: "Make", value: "Canon" }, { name: "Model", value: "EOS R6" } ] },
          { label: "Location", tags: [ { name: "GPS Latitude", value: "47.6062" }, { name: "GPS Longitude", value: "-122.3321" } ] },
        ],
      }
    case "maintenance.scanStartup":
      return startupScan
    case "maintenance.setStartupIgnored":
      return setMockStartupIgnored(payload)
    case "maintenance.exportStartupItem": {
      const item = getMockStartupItem(payload)
      return {
        exportPath: `C:\\Users\\you\\Documents\\FileLocker\\${safeFileToken(item.name)}-startup.json`,
        fileName: `${safeFileToken(item.name)}-startup.json`,
        itemId: item.id,
        fullPathsIncluded: !settings.preferences.incognitoMode,
      }
    }
    case "maintenance.openStartupSource": {
      const item = getMockStartupItem(payload)
      return { opened: true, itemId: item.id, targetKind: item.sourceType || "Startup source", target: item.sourceLocation || item.location }
    }
    case "maintenance.removeBrokenStartupItem":
      return removeMockStartupItem(payload)
    case "maintenance.setStartupEnabled":
      return setMockStartupEnabled(payload)
    case "maintenance.scanInstalledApps":
      return installedAppsScan
    case "maintenance.launchUninstaller": {
      const app = getMockInstalledApp(payload)
      return { started: true, appId: app.id, displayName: app.displayName, message: `Launched ${app.displayName} uninstaller.` }
    }
    case "maintenance.scanAppLeftovers":
      return appLeftoverScan
    case "maintenance.cleanAppLeftovers":
      return cleanMockAppLeftovers(payload)
    case "maintenance.scanCleanup":
      return cleanupScan
    case "maintenance.runCleanup":
      return { ...cleanupScan, freedBytes: 1_073_741_824, freedDisplay: "1.0 GB", deletedFiles: 1840, warnings: [] }
    case "maintenance.scanRegistry":
      return registryScan
    case "maintenance.cleanRegistry":
      return { cleanedCount: registryScan.issues.length, failedCount: 0, backupPath: "C:\\Users\\you\\Documents\\FileLocker\\registry-backup.reg", cleanedIssues: registryScan.issues, failures: [], scan: { ...registryScan, issues: [], issueCount: 0, status: "No issues found" } }
    case "maintenance.startWipeFreeSpace": {
      const driveRoot = typeof payload === "object" && payload && "driveRoot" in payload ? String((payload as { driveRoot?: unknown }).driveRoot) : "D:\\"
      mockWipeStatus = {
        operationId: crypto.randomUUID(),
        driveRoot,
        state: "Running",
        pass: "Zeros",
        percent: 20,
        status: "Pass 1 of 3: writing zeros",
        output: `cipher /w:${driveRoot}
Writing 0x00 ...`,
        startedAtUtc: new Date().toISOString(),
        completedAtUtc: null,
        cleanupStatus: "notNeeded",
        message: "Free-space wipe started.",
      }
      return mockWipeStatus
    }
    case "maintenance.getWipeFreeSpaceStatus":
      return mockWipeStatus
    case "maintenance.cancelWipeFreeSpace":
      mockWipeStatus = mockWipeStatus ? { ...mockWipeStatus, state: "Cancelled", status: "Wipe incomplete", completedAtUtc: new Date().toISOString(), cleanupStatus: "unknown", message: "Wipe incomplete. FileLocker attempted to locate residual cipher temporary files." } : null
      return mockWipeStatus
    case "maintenance.wipeFreeSpace":
      return { ok: true, title: "Free space wiped", message: "Free space on C: was overwritten once.", driveRoot: "C:\\", output: "Wiped 132 GB of free space.\nCompleted in 4m 12s.", startedAtUtc: new Date(Date.now() - 252_000).toISOString(), completedAtUtc: new Date().toISOString() }
    case "maintenance.optimizeDrive":
      return { ok: true, title: "Optimization complete", message: "Drive C: was optimized (TRIM sent to SSD).", driveRoot: "C:\\", output: "Retrim:  Completed.\nDefragmentation skipped (solid-state drive).", startedAtUtc: new Date(Date.now() - 38_000).toISOString(), completedAtUtc: new Date().toISOString() }
    case "maintenance.getDrives":
      return {
        drives: [
          { id: "C", name: "Windows", rootPath: "C:\\", driveType: "Fixed", driveFormat: "NTFS", totalSizeBytes: 511_000_000_000, totalSizeDisplay: "476 GB", freeSpaceBytes: 142_000_000_000, freeSpaceDisplay: "132 GB", isReady: true, mediaType: "SSD", mediaDetectionStatus: "Detected", mediaDescription: "TRIM and wear-leveling limit free-space overwrite benefit." },
          { id: "D", name: "Data", rootPath: "D:\\", driveType: "Fixed", driveFormat: "NTFS", totalSizeBytes: 2_000_000_000_000, totalSizeDisplay: "1.8 TB", freeSpaceBytes: 442_381_631_488, freeSpaceDisplay: "412 GB", isReady: true, mediaType: "HDD", mediaDetectionStatus: "Detected", mediaDescription: "Good fit for traditional hard drives." },
          { id: "E", name: "USB Backup", rootPath: "E:\\", driveType: "Removable", driveFormat: "exFAT", totalSizeBytes: 64_000_000_000, totalSizeDisplay: "59.6 GB", freeSpaceBytes: 30_064_771_072, freeSpaceDisplay: "28 GB", isReady: true, mediaType: "Removable", mediaDetectionStatus: "Detected", mediaDescription: "Limited benefit on wear-leveling flash media." },
          { id: "R", name: "Recovery", rootPath: "R:\\", driveType: "Fixed", driveFormat: "RAW", totalSizeBytes: 0, totalSizeDisplay: "0 B", freeSpaceBytes: 0, freeSpaceDisplay: "0 B", isReady: false, mediaType: "Unknown", mediaDetectionStatus: "Unsupported", mediaDescription: "Drive is not ready or uses an unsupported RAW format." },
        ],
      }
    case "files.revealPath":
    case "links.openExternal":
    case "app.setTitlePage":
    case "app.restartAsAdministrator":
      return {}
    default:
      // Unmocked interaction returns an empty object rather than throwing so
      // idle surfaces still render. Surfaces that need richer data log a hint.
      console.info(`[devBridgeMock] unmocked action "${action}" -> {}`, payload)
      return {}
  }
}

function cloneSettings(value: SettingsState): SettingsState {
  return JSON.parse(JSON.stringify(value)) as SettingsState
}

function getRequestedHashAlgorithm(payload: unknown) {
  return getHashAlgorithm((payload as { algorithm?: string } | null)?.algorithm) ?? defaultHashAlgorithm
}

function getMockHashVerification(payload: unknown) {
  const request = payload as { generatedHash?: string; expectedHash?: string } | null
  const generatedHash = request?.generatedHash?.trim().toLowerCase() ?? ""
  const expectedHash = request?.expectedHash?.trim().toLowerCase() ?? ""
  if (generatedHash.length > 0 && expectedHash.length > 0 && generatedHash.length !== expectedHash.length) {
    throw new Error("The expected hash length does not match the generated hash.")
  }

  const match = generatedHash.length > 0 && generatedHash === expectedHash
  return { match, status: match ? "Match" : "Mismatch" }
}

function getMockFileOperation(payload: unknown, action: "Encrypted" | "Decrypted" | "Verified" | "Deleted") {
  const request = payload as { operationId?: string; paths?: string[]; algorithm?: string; removeOriginalsAfterSuccess?: boolean; secureDeleteOriginals?: boolean } | null
  const paths = request?.paths?.filter((path) => path.trim().length > 0) ?? ["C:\\Users\\you\\Documents\\sample-document.pdf"]
  const removesSource = action === "Deleted" || request?.removeOriginalsAfterSuccess === true || request?.secureDeleteOriginals === true
  const results = paths.map((path) => {
    const outputPath = action === "Encrypted"
      ? `${path}.locked`
      : action === "Decrypted" && path.toLowerCase().endsWith(".locked")
        ? path.slice(0, -".locked".length)
        : action === "Decrypted"
          ? `${path}.restored`
          : undefined

    return {
      sourcePath: path,
      outputPath,
      status: "Completed",
      message: action === "Deleted" ? "Source securely deleted." : `${action} by the dev bridge mock.`,
      originalRetained: !removesSource,
      outputVerified: action !== "Deleted",
      originalSizeBytes: 184_320,
      outputSizeBytes: action === "Deleted" ? undefined : 188_416,
      compressionRequested: action === "Encrypted",
      compressionApplied: false,
      compressionReason: action === "Encrypted" ? "Mock payload was not compressed." : undefined,
      elapsedMilliseconds: 320,
      failureCategory: undefined,
      algorithm: request?.algorithm ?? DEFAULT_ENCRYPTION_ALGORITHM_ID,
      keySizeBits: 256,
    }
  })

  return {
    operationId: request?.operationId ?? "mock-operation",
    completed: results.length,
    failed: 0,
    results,
    dashboard,
  }
}

function getMockStartupItem(payload: unknown) {
  const itemId = (payload as { itemId?: string } | null)?.itemId
  return startupScan.items.find((item) => item.id === itemId) ?? startupScan.items[0] ?? mockStartupItem({ name: "Startup Item" })
}

function setMockStartupIgnored(payload: unknown) {
  const request = payload as { itemId?: string; ignored?: boolean } | null
  const item = getMockStartupItem(payload)
  const isIgnored = request?.ignored === true
  refreshMockStartupScan(startupScan.items.map((current) => current.id === item.id ? { ...current, isIgnored } : current))
  return {
    itemId: item.id,
    isIgnored,
    message: isIgnored ? `${item.name} hidden from startup review.` : `${item.name} restored to startup review.`,
  }
}

function setMockStartupEnabled(payload: unknown) {
  const request = payload as { itemId?: string; enabled?: boolean } | null
  const item = getMockStartupItem(payload)
  const isEnabled = request?.enabled === true
  const updatedItem = { ...item, isEnabled, status: isEnabled ? "Enabled" : "Disabled" }
  refreshMockStartupScan(startupScan.items.map((current) => current.id === item.id ? updatedItem : current))
  return {
    item: updatedItem,
    isEnabled,
    backupPath: `C:\\Users\\you\\Documents\\FileLocker\\${safeFileToken(item.name)}-startup-backup.json`,
    message: `${item.name} ${isEnabled ? "enabled" : "disabled"} for startup.`,
  }
}

function removeMockStartupItem(payload: unknown) {
  const item = getMockStartupItem(payload)
  refreshMockStartupScan(startupScan.items.filter((current) => current.id !== item.id))
  return {
    item: { ...item, isEnabled: false, status: "Removed" },
    isEnabled: false,
    backupPath: `C:\\Users\\you\\Documents\\FileLocker\\${safeFileToken(item.name)}-removed-startup-backup.json`,
    message: `${item.name} removed from startup.`,
  }
}

function refreshMockStartupScan(items: StartupScanResult["items"]) {
  startupScan = {
    ...startupScan,
    items,
    enabledCount: items.filter((item) => item.isEnabled && !item.isIgnored).length,
    disabledCount: items.filter((item) => !item.isEnabled && !item.isIgnored).length,
    brokenCount: items.filter((item) => item.category === "Broken Startup Items" || item.status === "Broken").length,
    advancedCount: items.filter((item) => item.category === "Advanced Startup Hooks").length,
    ignoredCount: items.filter((item) => item.isIgnored).length,
  }
}

function getMockInstalledApp(payload: unknown) {
  const appId = (payload as { appId?: string } | null)?.appId
  return installedAppsScan.apps.find((app) => app.id === appId) ?? installedAppsScan.apps[0] ?? mockApp({ displayName: "Application" })
}

function cleanMockAppLeftovers(payload: unknown) {
  const request = payload as { categoryIds?: string[] } | null
  const selectedIds = new Set(request?.categoryIds?.filter(Boolean) ?? appLeftoverScan.categories.map((category) => category.id))
  const cleanedCategories = appLeftoverScan.categories.filter((category) => selectedIds.has(category.id))
  const remainingCategories = appLeftoverScan.categories.filter((category) => !selectedIds.has(category.id))
  const freedBytes = cleanedCategories.reduce((sum, category) => sum + Math.max(0, category.sizeBytes), 0)
  appLeftoverScan = {
    ...appLeftoverScan,
    categories: remainingCategories,
    totalBytes: remainingCategories.reduce((sum, category) => sum + Math.max(0, category.sizeBytes), 0),
    totalFiles: remainingCategories.reduce((sum, category) => sum + Math.max(0, category.fileCount), 0),
  }
  appLeftoverScan = { ...appLeftoverScan, totalDisplay: formatMockBytes(appLeftoverScan.totalBytes) }

  return {
    cleanedCount: cleanedCategories.length,
    failedCount: 0,
    freedBytes,
    freedDisplay: formatMockBytes(freedBytes),
    cleanedCategories,
    failures: [],
    scan: appLeftoverScan,
  }
}

function safeFileToken(value: string) {
  return value.replace(/[^A-Za-z0-9._-]+/g, "-").replace(/^-+|-+$/g, "") || "filelocker"
}

function formatMockBytes(bytes: number) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B"
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(0)} MB`
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${bytes} B`
}

function normalizeTextConversionPayload(payload: unknown) {
  const request = payload as { input?: unknown; mode?: unknown; format?: unknown; preserveLineBreaks?: unknown } | null
  const input = typeof request?.input === "string" ? request.input : ""
  if (input.length > MAX_ENCODE_TEXT_INPUT_CHARS) {
    throw new Error("Text conversion input is too large.")
  }

  const mode = normalizeEncodeTextMode(typeof request?.mode === "string" ? request.mode : "encode")
  const format = normalizeEncodeTextFormat(typeof request?.format === "string" ? request.format : "Base64")
  return { input, mode, format, preserveLineBreaks: request?.preserveLineBreaks !== false }
}

function normalizeEncodeTextMode(mode: string): EncodeTextModeId {
  const normalized = mode.trim().toLowerCase()
  if (normalized === "encode") {
    return "encode"
  }

  if (normalized === "decode") {
    return "decode"
  }

  throw new Error("Choose encode or decode for text conversion.")
}

function normalizeEncodeTextFormat(format: string): EncodeTextFormat {
  const normalized = format.trim()
  const supportedFormat = ENCODE_TEXT_FORMATS.find((candidate) => candidate.toLowerCase() === normalized.toLowerCase())
  if (!supportedFormat) {
    throw new Error("Choose a supported text conversion format.")
  }

  return supportedFormat
}

function convertTextForMock(input: string, mode: EncodeTextModeId, format: EncodeTextFormat, preserveLineBreaks: boolean) {
  const preparedInput = preserveLineBreaks ? input : input.replace(/\r\n|\r|\n/g, " ")
  switch (format) {
    case "URL":
      return mode === "decode" ? decodeMockUrl(preparedInput) : encodeMockUrl(preparedInput)
    case "Hex":
    case "UTF-8":
      return mode === "decode" ? decodeMockHexToUtf8(preparedInput) : encodeMockHexUtf8(preparedInput, format === "UTF-8")
    case "HTML Entities":
      return mode === "decode" ? decodeMockHtml(preparedInput) : encodeMockHtml(preparedInput)
    case "Base64":
      return mode === "decode" ? decodeMockBase64(preparedInput) : encodeMockBase64(preparedInput)
  }
}

function encodeMockBase64(input: string) {
  return btoa(bytesToBinary(new TextEncoder().encode(input)))
}

function decodeMockBase64(input: string) {
  const decoded = atob(input.replace(/\s/g, ""))
  const bytes = Uint8Array.from(decoded, (character) => character.charCodeAt(0))
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes)
}

function encodeMockUrl(input: string) {
  return encodeURIComponent(input).replace(/%20/g, "+")
}

function decodeMockUrl(input: string) {
  return decodeURIComponent(input.replace(/\+/g, "%20"))
}

function encodeMockHexUtf8(input: string, spaced: boolean) {
  const hex = Array.from(new TextEncoder().encode(input), (value) => value.toString(16).padStart(2, "0").toUpperCase())
  return spaced ? hex.join(" ") : hex.join("")
}

function decodeMockHexToUtf8(input: string) {
  const hex = input.replace(/0x/gi, "").replace(/[\s\-:,]/g, "")
  if (hex.length % 2 !== 0 || /[^0-9a-f]/i.test(hex)) {
    throw new Error("Hex decode failed. Use an even number of valid hex characters.")
  }

  const bytes = new Uint8Array(hex.length / 2)
  for (let index = 0; index < hex.length; index += 2) {
    bytes[index / 2] = Number.parseInt(hex.slice(index, index + 2), 16)
  }

  return new TextDecoder("utf-8", { fatal: true }).decode(bytes)
}

function encodeMockHtml(input: string) {
  return input.replace(/[&<>"']/g, (character) => {
    switch (character) {
      case "&":
        return "&amp;"
      case "<":
        return "&lt;"
      case ">":
        return "&gt;"
      case "\"":
        return "&quot;"
      case "'":
        return "&#39;"
      default:
        return character
    }
  })
}

function decodeMockHtml(input: string) {
  const textarea = document.createElement("textarea")
  textarea.innerHTML = input
  return textarea.value
}

function bytesToBinary(bytes: Uint8Array) {
  let binary = ""
  const chunkSize = 0x8000
  for (let index = 0; index < bytes.length; index += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(index, index + chunkSize))
  }

  return binary
}

export function installDevBridgeMock() {
  if (window.chrome?.webview) {
    return
  }

  const listeners = new Set<MessageListener>()

  window.chrome = {
    webview: {
      postMessage: (message: unknown) => {
        const { id, action, payload } = message as BridgeRequest
        window.setTimeout(() => {
          let response: Record<string, unknown>
          try {
            response = { id, ok: true, result: handle(action, payload) }
          } catch (error) {
            response = { id, ok: false, error: { code: "mock", message: error instanceof Error ? error.message : String(error) } }
          }
          listeners.forEach((listener) => listener({ data: response } as MessageEvent))
        }, 80)
      },
      addEventListener: (_event: "message", handler: MessageListener) => {
        listeners.add(handler)
      },
    },
  }

  // Dev convenience: lets a test (or the console) push host->WebView events such
  // as `updateAvailable`, mirroring what the real bridge posts via PostBridgeEvent.
  ;(window as unknown as { __fileLockerDevEmit?: (event: unknown) => void }).__fileLockerDevEmit = (event: unknown) => {
    listeners.forEach((listener) => listener({ data: event } as MessageEvent))
  }

  console.info("[devBridgeMock] installed — UI is running on canned local data, not a real bridge.")
}

function mockStartupItem(overrides: Partial<StartupScanResult["items"][number]>): StartupScanResult["items"][number] {
  return {
    id: overrides.name ?? Math.random().toString(36).slice(2),
    name: "Item",
    source: "Registry",
    location: "HKCU\\...\\Run",
    command: "\"C:\\Program Files\\App\\app.exe\" /background",
    targetPath: "C:\\Program Files\\App\\app.exe",
    isEnabled: true,
    requiresAdministrator: false,
    canToggle: true,
    status: "Enabled",
    warnings: [],
    sourceType: "Registry",
    category: "Startup Apps",
    scope: "User",
    publisher: "Unknown",
    signatureStatus: "Signed",
    isMicrosoftSigned: false,
    commandRaw: "\"C:\\Program Files\\App\\app.exe\" /background",
    executableResolved: "C:\\Program Files\\App\\app.exe",
    arguments: "/background",
    workingDirectory: "C:\\Program Files\\App",
    sourceLocation: "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
    lastModified: "2026-05-20",
    startupImpact: "Low",
    confidence: "High",
    riskLevel: "Normal",
    disableMethod: "Registry",
    isReadOnlyManaged: false,
    backupPayload: "",
    notes: "",
    isIgnored: false,
    ...overrides,
  }
}

function mockApp(overrides: Partial<InstalledAppsScanResult["apps"][number]>): InstalledAppsScanResult["apps"][number] {
  return {
    id: overrides.displayName ?? Math.random().toString(36).slice(2),
    displayName: "App",
    publisher: "Unknown",
    version: "1.0.0",
    installDate: "2026-01-15",
    estimatedSizeBytes: 0,
    estimatedSizeDisplay: "—",
    installLocation: "C:\\Program Files\\App",
    uninstallCommand: "\"C:\\Program Files\\App\\uninstall.exe\"",
    sourceHive: "HKLM",
    architecture: "x64",
    requiresAdministrator: false,
    canLaunchUninstaller: true,
    registryKeyPath: "HKLM\\...\\Uninstall\\App",
    ...overrides,
  }
}
