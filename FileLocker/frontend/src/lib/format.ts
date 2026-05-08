export function fileName(path?: string) {
  if (!path) {
    return "No file"
  }
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path
}

export function mergeUniquePaths(current: string[], incoming: string[]) {
  const seen = new Set(current)
  const next = [...current]
  for (const path of incoming) {
    if (!seen.has(path)) {
      seen.add(path)
      next.push(path)
    }
  }
  return next
}

export function fileExtension(path: string) {
  return fileName(path).split(".").pop()?.toLowerCase() ?? ""
}

export function fileIconClass(path: string) {
  return `fiv-cla fiv-icon-${fileExtension(path)}`
}

export function formatBytes(bytes?: number) {
  if (!bytes || bytes <= 0) {
    return "0 B"
  }

  const units = ["B", "KB", "MB", "GB", "TB"]
  let size = bytes
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit += 1
  }

  return `${size.toFixed(size >= 10 ? 1 : 2).replace(/\.0$/, "")} ${units[unit]}`
}

export function formatDate(value?: string) {
  if (!value) {
    return "Not recorded"
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value))
}
