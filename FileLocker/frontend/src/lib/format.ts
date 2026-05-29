export const MAX_SELECTED_PATHS = 5000
export const MAX_SELECTED_PATH_CHARS = 32767

export function fileName(path?: string) {
  if (!path) {
    return "No file"
  }
  return path.split(/[\\/]/).filter(Boolean).at(-1) ?? path
}

export function mergeUniquePaths(current: string[], incoming: string[]) {
  const seen = new Set<string>()
  const next: string[] = []
  for (const path of [...current, ...incoming]) {
    const trimmedPath = path.trim()
    if (!isSafeLocalPath(trimmedPath)) {
      continue
    }

    const identityKey = getComparableLocalPath(trimmedPath)
    if (!seen.has(identityKey)) {
      seen.add(identityKey)
      next.push(trimmedPath)
      if (next.length >= MAX_SELECTED_PATHS) {
        break
      }
    }
  }
  return next
}

export function isSafeLocalPath(path?: string | null) {
  const trimmedPath = path?.trim() ?? ""
  if (trimmedPath.length === 0 || trimmedPath.length > MAX_SELECTED_PATH_CHARS || hasUnsafeFormatting(trimmedPath)) {
    return false
  }

  const normalizedPath = trimmedPath.replaceAll("/", "\\")
  if (/^[a-zA-Z]:\\/.test(normalizedPath)) {
    return !normalizedPath.slice(2).includes(":")
  }

  return /^\\\\[^\\]+\\[^\\]+/.test(normalizedPath) && !normalizedPath.includes(":")
}

export const isSafeLocalPathForReveal = isSafeLocalPath

function hasUnsafeFormatting(value: string) {
  return /[\u0000-\u001f\u007f\p{Cf}]/u.test(value)
}

export function getComparableLocalPath(path: string) {
  return path
    .replaceAll("/", "\\")
    .replace(/[\\]+$/, "")
    .toLowerCase()
}

export function fileExtension(path: string) {
  return fileName(path).split(".").pop()?.toLowerCase() ?? ""
}

export function fileIconClass(path: string) {
  return `fiv-cla fiv-icon-${fileExtension(path)}`
}

export function formatBytes(bytes?: number) {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes <= 0) {
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

  const date = new Date(value)
  if (!Number.isFinite(date.getTime())) {
    return "Not recorded"
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date)
}
