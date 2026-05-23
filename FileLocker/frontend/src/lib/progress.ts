import type { ProgressEvent } from "@/types/bridge"

export function getLatestProgressForOperation(progressEvents: ProgressEvent[], operationId: string) {
  if (!operationId) {
    return null
  }

  for (let index = progressEvents.length - 1; index >= 0; index--) {
    const event = progressEvents[index]
    if (event.operationId === operationId) {
      return event
    }
  }

  return null
}
