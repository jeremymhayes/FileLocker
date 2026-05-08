import type { BridgeEvent } from "@/types/bridge"

type PendingRequest = {
  resolve: (value: unknown) => void
  reject: (reason?: unknown) => void
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

let initialized = false

function ensureListener() {
  if (initialized || !window.chrome?.webview) {
    return
  }

  window.chrome.webview.addEventListener("message", (event) => {
    const message = event.data as BridgeResponse
    if (message.type === "progress" || message.type === "droppedPaths") {
      listeners.forEach((listener) => listener(message as BridgeEvent))
      return
    }

    if (!message.id) {
      return
    }

    const request = pending.get(message.id)
    if (!request) {
      return
    }

    pending.delete(message.id)
    if (message.ok) {
      request.resolve(message.result)
    } else {
      request.reject(new Error(message.error?.message ?? "FileLocker could not finish the request."))
    }
  })

  initialized = true
}

export function invokeBridge<T>(action: string, payload: unknown = {}): Promise<T> {
  ensureListener()

  if (!window.chrome?.webview) {
    return Promise.reject(new Error("FileLocker is not available in this window. Restart the app and try again."))
  }

  const id = crypto.randomUUID()
  const request = { id, action, payload }

  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject })
    window.chrome?.webview?.postMessage(request)
  })
}

export function subscribeToBridgeEvents(listener: (event: BridgeEvent) => void) {
  ensureListener()
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}
