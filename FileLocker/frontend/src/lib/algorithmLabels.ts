const MAX_ALGORITHM_LABEL_CHARS = 128
const UNSAFE_FORMATTING_PATTERN = /[\u0000-\u001f\u007f\p{Cf}]+/gu

export function formatAlgorithmLabel(algorithm?: string | null, keySizeBits?: number | null): string {
  const value = normalizeAlgorithmLabel(algorithm)
  if (!value) return ""
  if (value.toLowerCase() === "unknown") return value
  if (keySizeBits && keySizeBits > 0 && shouldDisplayAlgorithmKeySize(value) && !value.includes(String(keySizeBits))) {
    return `${value} (${keySizeBits}-bit)`
  }
  return value
}

function normalizeAlgorithmLabel(algorithm?: string | null): string {
  const value = algorithm
    ?.replace(UNSAFE_FORMATTING_PATTERN, " ")
    .replace(/\s+/g, " ")
    .trim()

  return value ? value.slice(0, MAX_ALGORITHM_LABEL_CHARS).trim() : ""
}

function shouldDisplayAlgorithmKeySize(algorithm: string): boolean {
  return /^(aes|chacha20|sha-?\d)/i.test(algorithm)
}
