export const DEFAULT_OUTPUT_TIMESTAMP_POLICY = "Current time"

export const OUTPUT_TIMESTAMP_POLICIES = [
  { id: DEFAULT_OUTPUT_TIMESTAMP_POLICY, label: DEFAULT_OUTPUT_TIMESTAMP_POLICY },
  { id: "Preserve source timestamps", label: "Preserve source timestamps" },
  { id: "Randomize", label: "Randomize" },
] as const

export function normalizeOutputTimestampPolicy(policy?: string | null) {
  const normalized = policy?.trim() ?? ""
  return OUTPUT_TIMESTAMP_POLICIES.find((candidate) => candidate.id.toLowerCase() === normalized.toLowerCase())?.id ?? DEFAULT_OUTPUT_TIMESTAMP_POLICY
}
