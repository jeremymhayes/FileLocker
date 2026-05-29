import type { EncryptionAlgorithmOption } from "@/types/bridge"

export const DEFAULT_ENCRYPTION_ALGORITHM_ID = "AES-256-GCM"
export const PNG_CARRIER_MAX_SOURCE_BYTES = 64 * 1024 * 1024
const MAX_ALGORITHM_ID_CHARS = 128
const MAX_ALGORITHM_TEXT_CHARS = 512
const UNSAFE_FORMATTING_PATTERN = /[\u0000-\u001f\u007f\p{Cf}]/u

export const FALLBACK_ENCRYPTION_ALGORITHMS: EncryptionAlgorithmOption[] = [
  {
    id: DEFAULT_ENCRYPTION_ALGORITHM_ID,
    label: DEFAULT_ENCRYPTION_ALGORITHM_ID,
    fileFormatName: DEFAULT_ENCRYPTION_ALGORITHM_ID,
    keySizeBits: 256,
    status: "Default",
    detail: "Hardware-accelerated on most Windows machines. This is the safest default for normal file locking.",
    bestFor: "Everyday files, archives, and folder packages.",
    supportNote: "Default authenticated file encryption.",
    canUsePngCarrier: true,
    pngCarrierMaxSourceBytes: PNG_CARRIER_MAX_SOURCE_BYTES,
  },
  {
    id: "ChaCha20-Poly1305",
    label: "ChaCha20-Poly1305",
    fileFormatName: "ChaCha20-Poly1305",
    keySizeBits: 256,
    status: "Fast software",
    detail: "Modern authenticated encryption that stays quick when AES hardware acceleration is not the bottleneck.",
    bestFor: "Large jobs on mixed hardware or lower-power devices.",
    supportNote: "Authenticated stream cipher using the platform implementation when available.",
    canUsePngCarrier: false,
  },
  {
    id: "AES-256-GCM-SIV",
    label: "AES-256-GCM-SIV",
    fileFormatName: "AES-256-GCM-SIV",
    keySizeBits: 256,
    status: "Misuse resistant",
    detail: "AES with better protection if nonce handling ever goes wrong. It trades a little familiarity for extra margin.",
    bestFor: "High-value local files and cautious cleanup workflows.",
    supportNote: "Misuse-resistant authenticated encryption via Bouncy Castle.",
    canUsePngCarrier: false,
  },
]

function isEncryptionAlgorithmOption(value: unknown): value is EncryptionAlgorithmOption {
  if (typeof value !== "object" || value === null) {
    return false
  }

  const candidate = value as Partial<EncryptionAlgorithmOption>
  return (
    isBoundedAlgorithmText(candidate.id, MAX_ALGORITHM_ID_CHARS, true) &&
    isBoundedAlgorithmText(candidate.label, MAX_ALGORITHM_ID_CHARS, true) &&
    isBoundedAlgorithmText(candidate.fileFormatName, MAX_ALGORITHM_ID_CHARS, true) &&
    typeof candidate.keySizeBits === "number" &&
    Number.isFinite(candidate.keySizeBits) &&
    Number.isInteger(candidate.keySizeBits) &&
    candidate.keySizeBits > 0 &&
    candidate.keySizeBits <= 4096 &&
    isBoundedAlgorithmText(candidate.status, MAX_ALGORITHM_ID_CHARS, true) &&
    isBoundedAlgorithmText(candidate.detail, MAX_ALGORITHM_TEXT_CHARS, false) &&
    isBoundedAlgorithmText(candidate.bestFor, MAX_ALGORITHM_TEXT_CHARS, false) &&
    (candidate.supportNote === undefined || isBoundedAlgorithmText(candidate.supportNote, MAX_ALGORITHM_TEXT_CHARS, false)) &&
    typeof candidate.canUsePngCarrier === "boolean" &&
    (
      candidate.pngCarrierMaxSourceBytes === undefined ||
      candidate.pngCarrierMaxSourceBytes === null ||
      (typeof candidate.pngCarrierMaxSourceBytes === "number" && Number.isFinite(candidate.pngCarrierMaxSourceBytes) && candidate.pngCarrierMaxSourceBytes > 0)
    )
  )
}

function isBoundedAlgorithmText(value: unknown, maxLength: number, required: boolean): value is string {
  if (typeof value !== "string") {
    return false
  }

  const trimmed = value.trim()
  return (
    (!required || trimmed.length > 0) &&
    value.length <= maxLength &&
    !UNSAFE_FORMATTING_PATTERN.test(value)
  )
}

export function getEncryptionAlgorithmOptions(encryptionAlgorithms?: EncryptionAlgorithmOption[]) {
  if (encryptionAlgorithms == null) {
    return FALLBACK_ENCRYPTION_ALGORITHMS
  }

  const options = encryptionAlgorithms.filter(isEncryptionAlgorithmOption)
  return options.length > 0 ? options : FALLBACK_ENCRYPTION_ALGORITHMS
}

export function getDefaultEncryptionAlgorithm(encryptionAlgorithms?: EncryptionAlgorithmOption[]) {
  const options = getEncryptionAlgorithmOptions(encryptionAlgorithms)
  return (
    options.find((algorithm) => algorithm.id === DEFAULT_ENCRYPTION_ALGORITHM_ID) ??
    options.find((algorithm) => algorithm.status.toLowerCase() === "default") ??
    options[0]
  )
}
