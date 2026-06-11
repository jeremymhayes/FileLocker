import { useEffect, useMemo, useRef, useState } from "react"
import { CheckCircle2, Clipboard, Copy, Home, RotateCcw, Scissors, Settings, ShieldCheck, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { cn } from "@/lib/utils"
import type { PageKey } from "@/types/bridge"

type TextControl = HTMLInputElement | HTMLTextAreaElement

type ContextMenuState = {
  x: number
  y: number
  target: EventTarget | null
  textControl: TextControl | null
  editable: HTMLElement | null
  selectionStart: number | null
  selectionEnd: number | null
  selectedText: string
  buttonLabel: string
}

type ContextMenuAction = {
  id: string
  label: string
  detail?: string
  icon: React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>
  disabled?: boolean
  danger?: boolean
  onRun: () => void | Promise<void>
}

const CONTEXT_MENU_WIDTH = 248
const CONTEXT_MENU_EDGE_GAP = 10

export function AppContextMenu({
  activePage,
  onNavigate,
}: {
  activePage: PageKey
  onNavigate: (page: PageKey) => void
}) {
  const [menu, setMenu] = useState<ContextMenuState | null>(null)
  const menuRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    function openFromPointer(event: PointerEvent | MouseEvent) {
      if (event.target instanceof Element && event.target.closest("[data-app-context-menu]")) {
        return
      }

      event.preventDefault()
      event.stopPropagation()
      setMenu(createContextMenuState(event.clientX, event.clientY, event.target))
    }

    function onPointerDown(event: PointerEvent) {
      if (event.button === 2) {
        openFromPointer(event)
      } else if (menuRef.current && event.target instanceof Node && !menuRef.current.contains(event.target)) {
        setMenu(null)
      }
    }

    function onContextMenu(event: MouseEvent) {
      openFromPointer(event)
    }

    function onKeyDown(event: KeyboardEvent) {
      if (event.key === "Escape") {
        setMenu(null)
      }
    }

    window.addEventListener("pointerdown", onPointerDown, true)
    window.addEventListener("contextmenu", onContextMenu, true)
    window.addEventListener("keydown", onKeyDown, true)
    window.addEventListener("resize", closeMenu)
    window.addEventListener("scroll", closeMenu, true)

    return () => {
      window.removeEventListener("pointerdown", onPointerDown, true)
      window.removeEventListener("contextmenu", onContextMenu, true)
      window.removeEventListener("keydown", onKeyDown, true)
      window.removeEventListener("resize", closeMenu)
      window.removeEventListener("scroll", closeMenu, true)
    }

    function closeMenu() {
      setMenu(null)
    }
  }, [])

  const actions = useMemo(() => {
    if (!menu) return []

    const copyText = menu.selectedText || menu.buttonLabel
    const canCopySelection = menu.selectedText.length > 0
    const canCopyButton = !canCopySelection && menu.buttonLabel.length > 0
    const hasEditableValue = Boolean(menu.textControl && menu.textControl.value.length > 0)

    const next: Array<ContextMenuAction | "separator"> = []

    if (menu.editable && menu.selectedText) {
      next.push({
          id: "cut",
          label: "Cut",
          detail: "Move selected text to clipboard",
          icon: Scissors,
          onRun: async () => {
            await copyToClipboard(menu.selectedText)
            removeSelectedText(menu)
          },
        })
    }

    if (copyText) {
      next.push({
          id: "copy",
          label: canCopyButton ? "Copy Button Label" : "Copy",
          detail: canCopyButton ? menu.buttonLabel : "Copy selected text",
          icon: Copy,
          onRun: async () => {
            await copyToClipboard(copyText)
            toast.success(canCopyButton ? "Button label copied." : "Selection copied.")
          },
        })
    }

    if (menu.editable) {
      next.push(
        {
          id: "paste",
          label: "Paste",
          detail: "Insert clipboard text",
          icon: Clipboard,
          onRun: async () => {
            const text = await readClipboardText()
            insertText(menu, text)
          },
        },
        {
          id: "select-field",
          label: "Select Field",
          detail: "Highlight current input text",
          icon: CheckCircle2,
          onRun: () => selectEditableText(menu),
        })
    }

    if (hasEditableValue) {
      next.push({
          id: "clear-field",
          label: "Clear Field",
          detail: "Remove current input value",
          icon: Trash2,
          danger: true,
          onRun: () => clearTextControl(menu),
        })
    }

    if (next.length > 0) {
      next.push("separator")
    }

    if (activePage !== "dashboard") {
      next.push({
        id: "home",
        label: "Go Home",
        detail: "Open dashboard",
        icon: Home,
        onRun: () => onNavigate("dashboard"),
      })
    }

    if (activePage !== "settings") {
      next.push({
        id: "settings",
        label: "Settings",
        detail: "Open app preferences",
        icon: Settings,
        onRun: () => onNavigate("settings"),
      })
    }

    if (activePage !== "security-guide") {
      next.push({
        id: "security-guide",
        label: "Security Guide",
        detail: "Open crypto guidance",
        icon: ShieldCheck,
        onRun: () => onNavigate("security-guide"),
      })
    }

    next.push({
      id: "reload",
      label: "Reload Interface",
      detail: "Refresh WebView UI",
      icon: RotateCcw,
      onRun: () => window.location.reload(),
    })

    return next
  }, [activePage, menu, onNavigate])

  if (!menu) return null

  return (
    <div
      ref={menuRef}
      data-app-context-menu
      className="app-context-menu"
      style={{ left: menu.x, top: menu.y, width: CONTEXT_MENU_WIDTH }}
      role="menu"
      aria-label="FileLocker context menu"
    >
      {actions.map((action, index) => {
        if (action === "separator") {
          return <div key={`separator-${index}`} className="app-context-menu-separator" role="separator" />
        }

        const Icon = action.icon
        return (
          <button
            key={action.id}
            type="button"
            className={cn("app-context-menu-item", action.danger && "app-context-menu-item-danger")}
            disabled={action.disabled}
            role="menuitem"
            onClick={() => {
              setMenu(null)
              void Promise.resolve(action.onRun()).catch((error) => {
                toast.error(error instanceof Error ? error.message : "Context action failed.")
              })
            }}
          >
            <Icon className="size-4" aria-hidden />
            <span className="min-w-0 flex-1">
              <span className="block truncate text-sm font-medium">{action.label}</span>
              {action.detail ? <span className="block truncate text-[11px] text-muted">{action.detail}</span> : null}
            </span>
          </button>
        )
      })}
    </div>
  )
}

function createContextMenuState(x: number, y: number, target: EventTarget | null): ContextMenuState {
  const element = target instanceof Element ? target : null
  const textControl = findTextControl(element)
  const editable = textControl ?? findEditableElement(element)
  const selection = getSelectionSnapshot(textControl)
  const selectedText = selection.selectedText || window.getSelection()?.toString().trim() || ""
  const button = element?.closest("button")
  const buttonLabel = button?.textContent?.replace(/\s+/g, " ").trim() ?? ""
  const nextX = Math.min(Math.max(CONTEXT_MENU_EDGE_GAP, x), Math.max(CONTEXT_MENU_EDGE_GAP, window.innerWidth - CONTEXT_MENU_WIDTH - CONTEXT_MENU_EDGE_GAP))
  const nextY = Math.min(Math.max(CONTEXT_MENU_EDGE_GAP, y), Math.max(CONTEXT_MENU_EDGE_GAP, window.innerHeight - 350))

  return {
    x: nextX,
    y: nextY,
    target,
    textControl,
    editable,
    selectionStart: selection.start,
    selectionEnd: selection.end,
    selectedText,
    buttonLabel,
  }
}

function findTextControl(element: Element | null): TextControl | null {
  const target = element?.closest("input, textarea")
  if (target instanceof HTMLTextAreaElement) return target
  if (target instanceof HTMLInputElement && isTextInputType(target)) return target
  return null
}

function findEditableElement(element: Element | null): HTMLElement | null {
  const editable = element?.closest("[contenteditable=''], [contenteditable='true']")
  return editable instanceof HTMLElement ? editable : null
}

function isTextInputType(input: HTMLInputElement) {
  return ["", "email", "number", "password", "search", "tel", "text", "url"].includes(input.type)
}

function getSelectionSnapshot(textControl: TextControl | null) {
  if (!textControl) {
    return { start: null, end: null, selectedText: "" }
  }

  const start = textControl.selectionStart ?? 0
  const end = textControl.selectionEnd ?? start
  return {
    start,
    end,
    selectedText: textControl.value.slice(Math.min(start, end), Math.max(start, end)),
  }
}

async function copyToClipboard(text: string) {
  if (!text) return
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text)
    return
  }

  const textarea = document.createElement("textarea")
  textarea.value = text
  textarea.style.position = "fixed"
  textarea.style.opacity = "0"
  document.body.append(textarea)
  textarea.select()
  document.execCommand("copy")
  textarea.remove()
}

async function readClipboardText() {
  if (!navigator.clipboard?.readText) {
    throw new Error("Clipboard paste is unavailable in this window.")
  }

  return navigator.clipboard.readText()
}

function restoreTextSelection(menu: ContextMenuState) {
  if (!menu.textControl) return
  menu.textControl.focus()
  if (menu.selectionStart !== null && menu.selectionEnd !== null) {
    menu.textControl.setSelectionRange(menu.selectionStart, menu.selectionEnd)
  }
}

function insertText(menu: ContextMenuState, text: string) {
  if (!text) return
  if (menu.textControl) {
    restoreTextSelection(menu)
    const start = menu.textControl.selectionStart ?? menu.textControl.value.length
    const end = menu.textControl.selectionEnd ?? start
    const nextValue = `${menu.textControl.value.slice(0, start)}${text}${menu.textControl.value.slice(end)}`
    setTextControlValue(menu.textControl, nextValue)
    const caret = start + text.length
    menu.textControl.setSelectionRange(caret, caret)
    return
  }

  if (menu.editable) {
    menu.editable.focus()
    document.execCommand("insertText", false, text)
  }
}

function removeSelectedText(menu: ContextMenuState) {
  if (menu.textControl) {
    restoreTextSelection(menu)
    const start = menu.textControl.selectionStart ?? 0
    const end = menu.textControl.selectionEnd ?? start
    const nextValue = `${menu.textControl.value.slice(0, start)}${menu.textControl.value.slice(end)}`
    setTextControlValue(menu.textControl, nextValue)
    menu.textControl.setSelectionRange(start, start)
    return
  }

  if (menu.editable) {
    menu.editable.focus()
    document.execCommand("delete")
  }
}

function selectEditableText(menu: ContextMenuState) {
  if (menu.textControl) {
    menu.textControl.focus()
    menu.textControl.setSelectionRange(0, menu.textControl.value.length)
    return
  }

  if (menu.editable) {
    const range = document.createRange()
    range.selectNodeContents(menu.editable)
    const selection = window.getSelection()
    selection?.removeAllRanges()
    selection?.addRange(range)
  }
}

function clearTextControl(menu: ContextMenuState) {
  if (!menu.textControl) return
  menu.textControl.focus()
  setTextControlValue(menu.textControl, "")
}

function setTextControlValue(control: TextControl, value: string) {
  const prototype = control instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype
  const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set
  setter?.call(control, value)
  control.dispatchEvent(new Event("input", { bubbles: true }))
}
