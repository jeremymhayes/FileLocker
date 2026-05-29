import assert from "node:assert/strict"
import fs from "node:fs/promises"
import os from "node:os"
import path from "node:path"
import ts from "typescript"

const sourcePath = path.resolve("src/lib/encryptionAlgorithms.ts")
const source = await fs.readFile(sourcePath, "utf8")
const transpiled = ts.transpileModule(source, {
  compilerOptions: {
    module: ts.ModuleKind.ES2022,
    target: ts.ScriptTarget.ES2022,
    verbatimModuleSyntax: true,
  },
  fileName: sourcePath,
}).outputText

const tempModule = path.join(os.tmpdir(), `filelocker-encryption-algorithms-${process.pid}.mjs`)
await fs.writeFile(tempModule, transpiled, "utf8")
const {
  DEFAULT_ENCRYPTION_ALGORITHM_ID,
  FALLBACK_ENCRYPTION_ALGORITHMS,
  getDefaultEncryptionAlgorithm,
  getEncryptionAlgorithmOptions,
} = await import(`file:///${tempModule.replaceAll("\\", "/")}`)
await fs.unlink(tempModule)

const fallbackIds = FALLBACK_ENCRYPTION_ALGORITHMS.map((algorithm) => algorithm.id)

assert.deepEqual(getEncryptionAlgorithmOptions(undefined).map((algorithm) => algorithm.id), fallbackIds)
assert.deepEqual(getEncryptionAlgorithmOptions(null).map((algorithm) => algorithm.id), fallbackIds)
assert.deepEqual(getEncryptionAlgorithmOptions([]), [])
assert.equal(getDefaultEncryptionAlgorithm([]), undefined)

const aesGcm = FALLBACK_ENCRYPTION_ALGORITHMS.find((algorithm) => algorithm.id === DEFAULT_ENCRYPTION_ALGORITHM_ID)
const chacha = FALLBACK_ENCRYPTION_ALGORITHMS.find((algorithm) => algorithm.id === "ChaCha20-Poly1305")
assert.ok(aesGcm)
assert.ok(chacha)

assert.deepEqual(
  getEncryptionAlgorithmOptions([
    aesGcm,
    { ...chacha, id: "" },
    { ...chacha, keySizeBits: "256" },
    { ...chacha, label: "Bad\u0000Label" },
    chacha,
  ]).map((algorithm) => algorithm.id),
  [DEFAULT_ENCRYPTION_ALGORITHM_ID, "ChaCha20-Poly1305"]
)

assert.deepEqual(
  getEncryptionAlgorithmOptions([
    { ...aesGcm, id: "" },
    { ...chacha, keySizeBits: 0 },
  ]),
  []
)
assert.equal(getDefaultEncryptionAlgorithm([{ ...chacha, status: "Default" }])?.id, "ChaCha20-Poly1305")
assert.equal(getDefaultEncryptionAlgorithm([{ ...chacha, status: "Fast software" }])?.id, "ChaCha20-Poly1305")

console.log("Encryption algorithm utility tests passed.")
