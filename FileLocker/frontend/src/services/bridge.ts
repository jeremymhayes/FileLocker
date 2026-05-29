import { isSafeLocalPathForReveal } from "@/lib/format"
import type { BridgeEvent } from "@/types/bridge"

type PendingRequest = {
  resolve: (value: unknown) => void
  reject: (reason?: unknown) => void
  timeoutId: ReturnType<typeof window.setTimeout>
}

type BridgeResponse = {
  id?: string
  ok?: boolean
  result?: unknown
  error?: {
    code: string
    message: string
  }
  type?: string
}

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void
        addEventListener: (event: "message", handler: (event: MessageEvent) => void) => void
      }
    }
  }
}

const pending = new Map<string, PendingRequest>()
const listeners = new Set<(event: BridgeEvent) => void>()
const BRIDGE_REQUEST_TIMEOUT_MS = 120_000

let initialized = false

function isBridgeResponse(message: unknown): message is BridgeResponse {
  return typeof message === "object" && message !== null
}

function notifyBridgeListeners(event: BridgeEvent) {
  listeners.forEach((listener) => {
    try {
      listener(event)
    } catch (error) {
      console.error("FileLocker bridge event listener failed.", error)
    }
  })
}

function ensureListener() {
  if (initialized || !window.chrome?.webview) {
    return
  }

  window.chrome.webview.addEventListener("message", (event) => {
    if (!isBridgeResponse(event.data)) {
      return
    }

    const message = event.data
    if (
      message.type === "progress" ||
      message.type === "droppedPaths" ||
      message.type === "dropError" ||
      message.type === "updateAvailable"
    ) {
      notifyBridgeListeners(message as BridgeEvent)
      return
    }

    if (typeof message.id !== "string" || message.id.length === 0) {
      return
    }

    const request = pending.get(message.id)
    if (!request) {
      return
    }

    pending.delete(message.id)
    window.clearTimeout(request.timeoutId)
    if (message.ok) {
      request.resolve(message.result)
    } else {
      request.reject(new Error(message.error?.message ?? "FileLocker could not finish the request."))
    }
  })

  initialized = true
}

function normalizeBridgePayload(action: string, payload: unknown) {
  if (action !== "files.revealPath") {
    return payload
  }

  const path = typeof payload === "object" && payload !== null && "path" in payload
    ? (payload as { path?: unknown }).path
    : undefined
  const targetPath = typeof path === "string" ? path.trim() : ""
  if (!isSafeLocalPathForReveal(targetPath)) {
    throw new Error("FileLocker can only reveal normal local file or folder paths.")
  }

  return { ...(payload as Record<string, unknown>), path: targetPath }
}

export function invokeBridge<T>(action: string, payload: unknown = {}): Promise<T> {
  ensureListener()

  const webview = window.chrome?.webview
  if (!webview) {
    return Promise.reject(new Error("FileLocker is not available in this window. Restart the app and try again."))
  }

  let normalizedPayload: unknown
  try {
    normalizedPayload = normalizeBridgePayload(action, payload)
  } catch (error) {
    return Promise.reject(error instanceof Error ? error : new Error("FileLocker could not validate the request."))
  }

  const id = crypto.randomUUID()
  const request = { id, action, payload: normalizedPayload }

  return new Promise<T>((resolve, reject) => {
    const timeoutId = window.setTimeout(() => {
      if (pending.delete(id)) {
        reject(new Error("FileLocker did not respond to the request. Try the action again."))
      }
    }, BRIDGE_REQUEST_TIMEOUT_MS)

    pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timeoutId })
    try {
      webview.postMessage(request)
    } catch (error) {
      window.clearTimeout(timeoutId)
      pending.delete(id)
      reject(error instanceof Error ? error : new Error("FileLocker could not send the request."))
    }
  })
}

export function subscribeToBridgeEvents(listener: (event: BridgeEvent) => void) {
  ensureListener()
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}
