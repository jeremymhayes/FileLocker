import type { StartupItem } from "@/types/bridge"

export type StartupBucketFilter = "all" | "Startup Apps" | "Broken Startup Items" | "Advanced Startup Hooks"
export type StartupEnabledFilter = "all" | "enabled" | "disabled"
export type StartupTrustFilter = "all" | "microsoft" | "non-microsoft" | "signed" | "unsigned"
export type StartupIgnoredFilter = "active" | "ignored" | "all"
export type StartupSortKey = "name" | "publisher" | "enabled" | "source" | "scope" | "risk" | "confidence" | "signature" | "impact"
export type StartupItemGroup = {
  category: Exclude<StartupBucketFilter, "all">
  items: StartupItem[]
}

const startupCategoryOrder: Exclude<StartupBucketFilter, "all">[] = ["Startup Apps", "Broken Startup Items", "Advanced Startup Hooks"]

export type StartupItemFilters = {
  query: string
  bucketFilter: StartupBucketFilter
  enabledFilter: StartupEnabledFilter
  trustFilter: StartupTrustFilter
  riskFilter: string
  sourceFilter: string
  ignoredFilter: StartupIgnoredFilter
}

export function filterStartupItems(items: StartupItem[], filters: StartupItemFilters) {
  const normalizedQuery = filters.query.trim().toLowerCase()
  return items.filter((item) => {
    if (filters.bucketFilter !== "all" && item.category !== filters.bucketFilter) {
      return false
    }

    if (filters.enabledFilter === "enabled" && !item.isEnabled) {
      return false
    }

    if (filters.enabledFilter === "disabled" && item.isEnabled) {
      return false
    }

    if (filters.ignoredFilter === "active" && item.isIgnored) {
      return false
    }

    if (filters.ignoredFilter === "ignored" && !item.isIgnored) {
      return false
    }

    if (filters.trustFilter === "microsoft" && !item.isMicrosoftSigned) {
      return false
    }

    if (filters.trustFilter === "non-microsoft" && item.isMicrosoftSigned) {
      return false
    }

    if (filters.trustFilter === "signed" && item.signatureStatus !== "Signed") {
      return false
    }

    if (filters.trustFilter === "unsigned" && item.signatureStatus === "Signed") {
      return false
    }

    if (filters.riskFilter !== "all" && item.riskLevel !== filters.riskFilter) {
      return false
    }

    if (filters.sourceFilter !== "all" && firstText(item.sourceType, item.source) !== filters.sourceFilter) {
      return false
    }

    if (!normalizedQuery) {
      return true
    }

    return [
      item.name,
      item.publisher,
      item.source,
      item.sourceType,
      item.scope,
      item.status,
      firstText(item.commandRaw, item.command),
      firstText(item.executableResolved, item.targetPath),
      firstText(item.sourceLocation, item.location),
    ].some((value) => safeText(value).toLowerCase().includes(normalizedQuery))
  })
}

export function sortStartupItems(items: StartupItem[], sortKey: StartupSortKey) {
  const sorted = [...items]
  const riskRank = new Map([
    ["High", 0],
    ["Medium", 1],
    ["Low", 2],
  ])
  const confidenceRank = new Map([
    ["High", 0],
    ["Medium", 1],
    ["Low", 2],
  ])
  const impactRank = new Map([
    ["High", 0],
    ["Medium", 1],
    ["Low", 2],
  ])
  sorted.sort((a, b) => {
    if (sortKey === "enabled") {
      return Number(b.isEnabled) - Number(a.isEnabled) || compareByName(a, b)
    }

    if (sortKey === "source") {
      return compareText(firstText(a.sourceType, a.source), firstText(b.sourceType, b.source)) || compareByName(a, b)
    }

    if (sortKey === "scope") {
      return compareText(a.scope, b.scope) || compareByName(a, b)
    }

    if (sortKey === "risk") {
      return (riskRank.get(a.riskLevel) ?? 9) - (riskRank.get(b.riskLevel) ?? 9) || compareByName(a, b)
    }

    if (sortKey === "confidence") {
      return (confidenceRank.get(a.confidence) ?? 9) - (confidenceRank.get(b.confidence) ?? 9) || compareByName(a, b)
    }

    if (sortKey === "signature") {
      return Number(b.isMicrosoftSigned) - Number(a.isMicrosoftSigned) || compareText(a.signatureStatus, b.signatureStatus) || compareByName(a, b)
    }

    if (sortKey === "impact") {
      return (impactRank.get(a.startupImpact) ?? 9) - (impactRank.get(b.startupImpact) ?? 9) || compareByName(a, b)
    }

    if (sortKey === "publisher") {
      return compareText(a.publisher, b.publisher) || compareByName(a, b)
    }

    return compareByName(a, b)
  })
  return sorted
}

export function groupStartupItems(items: StartupItem[]) {
  return startupCategoryOrder
    .map((category): StartupItemGroup => ({
      category,
      items: items.filter((item) => item.category === category),
    }))
    .filter((group) => group.items.length > 0)
}

function compareByName(a: StartupItem, b: StartupItem) {
  return compareText(a.name, b.name)
}

function safeText(value: unknown) {
  return typeof value === "string" ? value : ""
}

function firstText(...values: unknown[]) {
  for (const value of values) {
    const text = safeText(value)
    if (text) {
      return text
    }
  }

  return ""
}

function compareText(left: unknown, right: unknown) {
  return safeText(left).localeCompare(safeText(right))
}
