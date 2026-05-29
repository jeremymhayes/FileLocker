export type HashAlgorithmDefinition = {
  id: string
  label: string
  digestBits: number
  expectedLength: number
  recommendation?: string
}

export const DEFAULT_HASH_ALGORITHM_ID = "SHA-256"
const MAX_HASH_ALGORITHM_ID_CHARS = 64
const UNSAFE_FORMATTING_PATTERN = /[\u0000-\u001f\u007f\p{Cf}]/u

export const HASH_ALGORITHMS: readonly HashAlgorithmDefinition[] = [
  {
    id: DEFAULT_HASH_ALGORITHM_ID,
    label: "SHA-256",
    digestBits: 256,
    expectedLength: 64,
    recommendation: "Recommended",
  },
  {
    id: "SHA-512",
    label: "SHA-512",
    digestBits: 512,
    expectedLength: 128,
  },
]

export function getHashAlgorithm(id?: string | null): HashAlgorithmDefinition | undefined {
  if (typeof id !== "string" || id.length > MAX_HASH_ALGORITHM_ID_CHARS || UNSAFE_FORMATTING_PATTERN.test(id)) {
    return undefined
  }

  const normalized = id.trim()
  if (!normalized) return undefined
  return HASH_ALGORITHMS.find((algorithm) => algorithm.id.toLowerCase() === normalized.toLowerCase())
}

export function isSupportedHashLength(length: number): boolean {
  return HASH_ALGORITHMS.some((algorithm) => algorithm.expectedLength === length)
}

export function describeSupportedHashAlgorithms(): string {
  return HASH_ALGORITHMS.map((algorithm) => algorithm.label).join(" or ")
}
