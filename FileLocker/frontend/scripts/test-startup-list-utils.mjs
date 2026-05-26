import assert from "node:assert/strict"
import fs from "node:fs/promises"
import os from "node:os"
import path from "node:path"
import ts from "typescript"

const sourcePath = path.resolve("src/lib/startupListUtils.ts")
const source = await fs.readFile(sourcePath, "utf8")
const transpiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022,
    verbatimModuleSyntax: true,
  },
  fileName: sourcePath,
}).outputText

const tempModule = path.join(os.tmpdir(), `filelocker-startup-utils-${process.pid}.mjs`)
await fs.writeFile(tempModule, transpiled, "utf8")
const { filterStartupItems, groupStartupItems, sortStartupItems } = await import(`file:///${tempModule.replaceAll("\\", "/")}`)
await fs.unlink(tempModule)

const baseItem = {
  id: "base",
  name: "Base",
  source: "HKCU Run",
  location: "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run",
  command: "",
  targetPath: "",
  isEnabled: true,
  requiresAdministrator: false,
  canToggle: true,
  status: "Enabled",
  warnings: [],
  sourceType: "Registry",
  category: "Startup Apps",
  scope: "Current user",
  publisher: "",
  signatureStatus: "Unknown",
  isMicrosoftSigned: false,
  commandRaw: "",
  executableResolved: "",
  arguments: "",
  workingDirectory: "",
  sourceLocation: "",
  lastModified: "",
  startupImpact: "Medium",
  confidence: "Medium",
  riskLevel: "Low",
  disableMethod: "RegistryValue",
  isReadOnlyManaged: false,
  backupPayload: "",
  notes: "",
  isIgnored: false,
}

const items = [
  {
    ...baseItem,
    id: "alpha",
    name: "Alpha App",
    publisher: "Microsoft Corporation",
    signatureStatus: "Signed",
    isMicrosoftSigned: true,
    riskLevel: "Low",
    confidence: "High",
    startupImpact: "Low",
    commandRaw: "\"C:\\Program Files\\Alpha\\alpha.exe\"",
    executableResolved: "C:\\Program Files\\Alpha\\alpha.exe",
  },
  {
    ...baseItem,
    id: "beta",
    name: "Beta Broken",
    category: "Broken Startup Items",
    publisher: "Unknown Vendor",
    isEnabled: true,
    riskLevel: "High",
    confidence: "High",
    startupImpact: "High",
    commandRaw: "C:\\Missing\\beta.exe",
    executableResolved: "C:\\Missing\\beta.exe",
  },
  {
    ...baseItem,
    id: "gamma",
    name: "Gamma Hook",
    source: "WMI permanent event consumer",
    sourceType: "WMI",
    category: "Advanced Startup Hooks",
    scope: "System",
    isEnabled: false,
    canToggle: false,
    status: "Read-only",
    riskLevel: "Medium",
    confidence: "Low",
    startupImpact: "High",
    isIgnored: true,
  },
]

const baseFilters = {
  query: "",
  bucketFilter: "all",
  enabledFilter: "all",
  trustFilter: "all",
  riskFilter: "all",
  sourceFilter: "all",
  ignoredFilter: "all",
}

assert.deepEqual(filterStartupItems(items, { ...baseFilters, bucketFilter: "Broken Startup Items" }).map((item) => item.id), ["beta"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, enabledFilter: "disabled" }).map((item) => item.id), ["gamma"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, trustFilter: "microsoft" }).map((item) => item.id), ["alpha"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, trustFilter: "unsigned" }).map((item) => item.id), ["beta", "gamma"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, riskFilter: "Medium" }).map((item) => item.id), ["gamma"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, sourceFilter: "WMI" }).map((item) => item.id), ["gamma"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, ignoredFilter: "active" }).map((item) => item.id), ["alpha", "beta"])
assert.deepEqual(filterStartupItems(items, { ...baseFilters, query: "missing" }).map((item) => item.id), ["beta"])

assert.deepEqual(sortStartupItems(items, "risk").map((item) => item.id), ["beta", "gamma", "alpha"])
assert.deepEqual(sortStartupItems(items, "confidence").map((item) => item.id), ["alpha", "beta", "gamma"])
assert.deepEqual(sortStartupItems(items, "enabled").map((item) => item.id), ["alpha", "beta", "gamma"])
assert.deepEqual(sortStartupItems(items, "signature").map((item) => item.id), ["alpha", "beta", "gamma"])
assert.deepEqual(sortStartupItems(items, "impact").map((item) => item.id), ["beta", "gamma", "alpha"])
assert.deepEqual(
  groupStartupItems(sortStartupItems(items, "risk")).map((group) => [group.category, group.items.map((item) => item.id)]),
  [
    ["Startup Apps", ["alpha"]],
    ["Broken Startup Items", ["beta"]],
    ["Advanced Startup Hooks", ["gamma"]],
  ]
)

console.log("Startup list utility tests passed.")
