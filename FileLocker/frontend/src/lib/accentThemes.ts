/**
 * Accent theme presets.
 *
 * Each preset id maps to a `[data-accent-theme]` palette block in
 * `styles/globals.css`. The attribute is set on <html>, so changing it
 * re-themes the whole app instantly. The saved value travels through the
 * settings bridge (`preferences.accentTheme`); a localStorage cache lets the
 * app paint with the right palette before the bridge responds at startup.
 */

export const ACCENT_THEMES = [
  { id: "blue", label: "Blue", swatch: "#5d8dff" },
  { id: "orange", label: "Orange", swatch: "#e2a33c" },
  { id: "purple", label: "Purple", swatch: "#a974ff" },
  { id: "green", label: "Green", swatch: "#34c084" },
  { id: "red", label: "Red", swatch: "#ec6a78" },
  { id: "slate", label: "Slate", swatch: "#94a8c4" },
] as const

export type AccentThemeId = (typeof ACCENT_THEMES)[number]["id"]

export const DEFAULT_ACCENT_THEME: AccentThemeId = "blue"

const ACCENT_THEME_STORAGE_KEY = "filelocker.accentTheme"

export function normalizeAccentTheme(value: unknown): AccentThemeId {
  const candidate = typeof value === "string" ? value.trim().toLowerCase() : ""
  return ACCENT_THEMES.some((theme) => theme.id === candidate) ? (candidate as AccentThemeId) : DEFAULT_ACCENT_THEME
}

export function applyAccentTheme(value: unknown): AccentThemeId {
  const theme = normalizeAccentTheme(value)
  document.documentElement.dataset.accentTheme = theme
  return theme
}

export function readCachedAccentTheme(): AccentThemeId {
  try {
    return normalizeAccentTheme(window.localStorage.getItem(ACCENT_THEME_STORAGE_KEY))
  } catch {
    return DEFAULT_ACCENT_THEME
  }
}

export function cacheAccentTheme(theme: AccentThemeId) {
  try {
    window.localStorage.setItem(ACCENT_THEME_STORAGE_KEY, theme)
  } catch {
    // Cache is an optimization only; settings remain the source of truth.
  }
}
