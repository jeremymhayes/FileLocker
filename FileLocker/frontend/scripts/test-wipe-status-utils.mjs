import assert from "node:assert/strict"
import fs from "node:fs/promises"
import os from "node:os"
import path from "node:path"
import ts from "typescript"

const sourcePath = path.resolve("src/lib/wipeStatusUtils.ts")
const source = await fs.readFile(sourcePath, "utf8")
const transpiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022,
    verbatimModuleSyntax: true,
  },
  fileName: sourcePath,
}).outputText

const tempModule = path.join(os.tmpdir(), `filelocker-wipe-status-utils-${process.pid}.mjs`)
await fs.writeFile(tempModule, transpiled, "utf8")
const { formatWipeElapsedDisplay } = await import(`file:///${tempModule.replaceAll("\\", "/")}`)
await fs.unlink(tempModule)

const runningStatus = {
  operationId: "operation-1",
  driveRoot: "D:\\",
  state: "Running",
  pass: "Zeros",
  percent: 20,
  status: "Pass 1 of 3: writing zeros",
  output: "Writing 0x00",
  startedAtUtc: "2026-06-10T12:00:00.000Z",
  completedAtUtc: null,
  cleanupStatus: "notNeeded",
  message: "Running Windows cipher",
}

assert.equal(formatWipeElapsedDisplay(runningStatus, Date.parse("2026-06-10T12:01:15.000Z")), "1m 15s")
assert.equal(formatWipeElapsedDisplay({ ...runningStatus, startedAtUtc: "not-a-date" }, Date.parse("2026-06-10T12:01:15.000Z")), "Unknown")
assert.equal(formatWipeElapsedDisplay({
  ...runningStatus,
  state: "Completed",
  completedAtUtc: "2026-06-10T12:02:00.000Z",
}, Date.parse("2026-06-10T12:30:00.000Z")), "2m")

console.log("Wipe status utility tests passed.")
