import type { FreeSpaceWipeStatus } from "@/types/bridge"

export function formatWipeElapsedDisplay(status: Pick<FreeSpaceWipeStatus, "startedAtUtc" | "completedAtUtc">, nowMs = Date.now()) {
  const startedAt = Date.parse(status.startedAtUtc)
  if (Number.isNaN(startedAt)) return "Unknown"

  const completedAt = status.completedAtUtc ? Date.parse(status.completedAtUtc) : nowMs
  const end = Number.isNaN(completedAt) ? nowMs : completedAt
  const seconds = Math.max(0, Math.round((end - startedAt) / 1000))
  if (seconds < 60) return `${seconds}s`

  const minutes = Math.floor(seconds / 60)
  const remainingSeconds = seconds % 60
  if (minutes < 60) return remainingSeconds > 0 ? `${minutes}m ${remainingSeconds}s` : `${minutes}m`

  const hours = Math.floor(minutes / 60)
  const remainingMinutes = minutes % 60
  return remainingMinutes > 0 ? `${hours}h ${remainingMinutes}m` : `${hours}h`
}
