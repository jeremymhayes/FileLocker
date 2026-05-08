import fs from "node:fs"
import path from "node:path"

const sourceRoot = path.resolve("public")
const destinationRoot = path.resolve("dist")

if (!fs.existsSync(sourceRoot) || !fs.existsSync(destinationRoot)) {
  process.exit(0)
}

copyDirectory(sourceRoot, destinationRoot)

function copyDirectory(sourceDirectory, destinationDirectory) {
  fs.mkdirSync(destinationDirectory, { recursive: true })

  for (const entry of fs.readdirSync(sourceDirectory, { withFileTypes: true })) {
    const sourcePath = path.join(sourceDirectory, entry.name)
    const destinationPath = path.join(destinationDirectory, entry.name)

    if (entry.isDirectory()) {
      copyDirectory(sourcePath, destinationPath)
      continue
    }

    fs.mkdirSync(path.dirname(destinationPath), { recursive: true })
    fs.copyFileSync(sourcePath, destinationPath)
  }
}
