import { useState } from "react"
import { ArrowRight, Check, FileDigit, GraduationCap, KeyRound, ScanLine, ShieldCheck, Trash2, Wand2, X, type LucideIcon } from "lucide-react"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { getDefaultEncryptionAlgorithm, getEncryptionAlgorithmOptions } from "@/lib/encryptionAlgorithms"
import type { EncryptionAlgorithmOption, PageKey } from "@/types/bridge"

type SecurityGuidePageProps = {
  encryptionAlgorithms?: EncryptionAlgorithmOption[]
}

type GuideLevel = "beginner" | "advanced"

type ToolLink = {
  label: string
  page: Extract<PageKey, "encrypt" | "hash" | "metadata" | "secure-delete">
}

type GuideCard = {
  title: string
  icon: LucideIcon
  summary: string
  do?: string[]
  avoid?: string[]
  whenToUse?: string
  tool?: ToolLink
  advancedNotes?: string[]
}

function navigateToTool(page: ToolLink["page"]) {
  window.location.hash = page
}

function formatList(values: string[]) {
  if (values.length <= 1) {
    return values[0] ?? ""
  }

  if (values.length === 2) {
    return `${values[0]} and ${values[1]}`
  }

  return `${values.slice(0, -1).join(", ")}, and ${values[values.length - 1]}`
}

export function SecurityGuidePage({ encryptionAlgorithms }: SecurityGuidePageProps) {
  const [level, setLevel] = useState<GuideLevel>("beginner")
  const isAdvanced = level === "advanced"

  const supportedAlgorithms = getEncryptionAlgorithmOptions(encryptionAlgorithms)
  const defaultAlgorithm = getDefaultEncryptionAlgorithm(supportedAlgorithms)?.label
  const algorithmList = formatList(supportedAlgorithms.map((algorithm) => algorithm.label))
  const pngCarrierAlgorithms = formatList(
    supportedAlgorithms.filter((algorithm) => algorithm.canUsePngCarrier).map((algorithm) => algorithm.label),
  )
  const pngCarrierText = pngCarrierAlgorithms
    ? `PNG carrier output uses the older AES-GCM carrier path and is only available with ${pngCarrierAlgorithms}.`
    : "PNG carrier output is not available for the current encryption options."

  const encryptionAdvancedNotes = defaultAlgorithm
    ? [
        `FileLocker defaults to ${defaultAlgorithm}. New .locked payload options are ${algorithmList}.`,
        "These are authenticated modes, so the encrypted file carries integrity data that lets FileLocker detect tampering before restoring output. Decryption reads the saved algorithm from the payload header.",
        pngCarrierText,
      ]
    : [
        "No supported file-encryption algorithm is available on this runtime. FileLocker will not offer new encrypted payload creation until the platform crypto support check passes.",
      ]

  const cards: GuideCard[] = [
    {
      title: "Encrypt Files",
      icon: KeyRound,
      summary: "Scramble file contents with a password so only you can read them back.",
      do: [
        "Use a long passphrase of several unrelated words.",
        "Test decryption on a copy before deleting the original.",
        "Store your password in a password manager.",
      ],
      avoid: [
        "Saving the password in a text file beside the encrypted file.",
        "Assuming a forgotten password can be recovered — there is no backdoor.",
      ],
      whenToUse: "Whenever you need to protect the contents of a file at rest on this device.",
      tool: { label: "Open Encrypt", page: "encrypt" },
      advancedNotes: encryptionAdvancedNotes,
    },
    {
      title: "Hash & Verify",
      icon: FileDigit,
      summary: "Create a fingerprint of a file to confirm it has not changed.",
      do: [
        "Use SHA-256 as a strong default for verifying downloads.",
        "Compare the computed hash against a trusted published value.",
      ],
      avoid: ["Treating a hash as encryption — it verifies content but does not hide it."],
      whenToUse: "After downloading a file or before sharing one, to detect accidental or malicious changes.",
      tool: { label: "Open Hash", page: "hash" },
      advancedNotes: [
        "SHA-512 produces a larger digest and suits workflows that standardize on it.",
        "A hash is one-way: matching hashes mean matching content, but the hash reveals nothing about the data itself.",
      ],
    },
    {
      title: "Metadata Scrambler",
      icon: Wand2,
      summary: "Strip hidden details like timestamps, author fields, and GPS data before sharing.",
      do: [
        "Preview removal for supported formats before saving.",
        "Verify with a specialized tool such as ExifTool when removal is critical.",
      ],
      avoid: ["Assuming every metadata field is removed from every file type — no general tool can guarantee that."],
      whenToUse: "Before sending photos or documents that may carry location or identity details.",
      tool: { label: "Open Metadata", page: "metadata" },
      advancedNotes: [
        "Metadata can include timestamps, author fields, software names, camera details, and GPS coordinates in images.",
        "Coverage depends on the file format; container formats may retain fields FileLocker does not rewrite.",
      ],
    },
    {
      title: "Secure Delete",
      icon: Trash2,
      summary: "Overwrite file data before deletion so it is harder to recover.",
      do: [
        "Use it on traditional hard drives where overwrites are reliable.",
        "Pair it with full-disk encryption such as BitLocker for stronger protection.",
      ],
      avoid: ["Relying on it alone on SSDs, where wear leveling can leave old data in inaccessible blocks."],
      whenToUse: "When removing sensitive files you do not want recovered from this device.",
      tool: { label: "Open Secure Delete", page: "secure-delete" },
      advancedNotes: [
        "Secure delete overwrites file data where the filesystem and storage device allow it.",
        "SSD wear leveling means the OS no longer directly controls every block, so device-level encryption is the stronger complement.",
      ],
    },
    {
      title: "Choosing a Strong Password",
      icon: ShieldCheck,
      summary: "Length beats symbol tricks. A passphrase of several words is strong and memorable.",
      do: [
        "Prefer several unrelated words over a short complex string.",
        "Keep important passwords in a password manager.",
      ],
      avoid: [
        "Storing passwords in plain text on the same disk as your files.",
        "Expecting FileLocker to recover a forgotten password.",
      ],
      whenToUse: "Every time you encrypt a file or set up a new secret.",
      advancedNotes: [
        "FileLocker has no recovery material: if the wrong password is used and no backup exists, treat the file as inaccessible.",
        "Entropy from length and word count generally outperforms character substitution tricks.",
      ],
    },
    {
      title: "What FileLocker Is (and Is Not)",
      icon: ScanLine,
      summary: "FileLocker encrypts file contents locally. It is not a VPN, recovery, or full-disk tool.",
      do: [
        "Keep backups of important encrypted files.",
        "Remember that files never leave your machine — FileLocker is local-only.",
      ],
      avoid: [
        "Expecting it to hide file size, location, timestamps, or the presence of a .locked file.",
        "Treating it as a password manager, VPN, or full-disk encryption replacement.",
      ],
      whenToUse: "Read this once so you know what protection FileLocker does and does not provide.",
      advancedNotes: [
        "Encryption protects contents, not filenames, unless you rename files or choose output options that change names.",
        "Filesystem details such as size, folder, and timestamps remain visible to anyone with device access.",
      ],
    },
  ]

  // Beginner path leads with the actionable tool cards and hides algorithm details.
  // Advanced surfaces the concept cards first and expands the algorithm panels.
  const visibleCards = isAdvanced
    ? [...cards.filter((card) => !card.tool), ...cards.filter((card) => card.tool)]
    : cards

  return (
    <div className="security-page">
      <section className="section-surface">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="font-display text-lg font-semibold text-primary">Security Guide</h2>
            <p className="mt-1 max-w-[680px] text-sm leading-snug text-secondary">
              {isAdvanced
                ? "Algorithm details, integrity guarantees, and the edge cases behind each tool."
                : "Short, practical steps for using FileLocker safely on a local Windows desktop."}
            </p>
          </div>
          <div
            role="group"
            aria-label="Guide detail level"
            className="inline-flex shrink-0 items-center gap-1 rounded-md border border-border bg-transparent p-1"
          >
            <Button
              size="sm"
              variant={isAdvanced ? "ghost" : "secondary"}
              aria-pressed={!isAdvanced}
              onClick={() => setLevel("beginner")}
            >
              <GraduationCap data-icon="inline-start" />
              Beginner
            </Button>
            <Button
              size="sm"
              variant={isAdvanced ? "secondary" : "ghost"}
              aria-pressed={isAdvanced}
              onClick={() => setLevel("advanced")}
            >
              <ScanLine data-icon="inline-start" />
              Advanced
            </Button>
          </div>
        </div>

        <div className="mt-4 grid gap-3 lg:grid-cols-2">
          {visibleCards.map((card) => (
            <GuideCardView key={card.title} card={card} showAdvanced={isAdvanced} onOpenTool={navigateToTool} />
          ))}
        </div>
      </section>
    </div>
  )
}

function GuideCardView({
  card,
  showAdvanced,
  onOpenTool,
}: {
  card: GuideCard
  showAdvanced: boolean
  onOpenTool: (page: ToolLink["page"]) => void
}) {
  const Icon = card.icon
  return (
    <article className="flex min-w-0 flex-col rounded-md border border-border bg-bg-surface-raised/40 p-4">
      <div className="flex items-start gap-3">
        <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent-blue/12 text-accent-blue">
          <Icon className="size-4" aria-hidden />
        </div>
        <div className="min-w-0">
          <h3 className="font-display text-base font-semibold leading-tight text-primary">{card.title}</h3>
          <p className="mt-1 text-sm leading-snug text-secondary">{card.summary}</p>
        </div>
      </div>

      <div className="mt-3 flex flex-col gap-3">
        {card.do && card.do.length > 0 ? (
          <GuideList
            label="Do"
            badgeVariant="secondary"
            icon={Check}
            iconClassName="text-accent-green"
            items={card.do}
          />
        ) : null}
        {card.avoid && card.avoid.length > 0 ? (
          <GuideList
            label="Avoid"
            badgeVariant="destructive"
            icon={X}
            iconClassName="text-destructive"
            items={card.avoid}
          />
        ) : null}
        {card.whenToUse ? (
          <div>
            <Badge variant="outline">When to use</Badge>
            <p className="mt-1.5 text-sm leading-snug text-secondary">{card.whenToUse}</p>
          </div>
        ) : null}
        {showAdvanced && card.advancedNotes && card.advancedNotes.length > 0 ? (
          <div className="rounded-md border border-border bg-transparent p-3">
            <Badge variant="default">Algorithm details</Badge>
            <div className="mt-2 flex flex-col gap-1.5">
              {card.advancedNotes.map((note, index) => (
                <p key={index} className="text-sm leading-snug text-secondary">
                  {note}
                </p>
              ))}
            </div>
          </div>
        ) : null}
      </div>

      {card.tool ? (
        <div className="mt-4 flex">
          <Button size="sm" variant="outline" onClick={() => onOpenTool(card.tool!.page)}>
            {card.tool.label}
            <ArrowRight data-icon="inline-end" />
          </Button>
        </div>
      ) : null}
    </article>
  )
}

function GuideList({
  label,
  badgeVariant,
  icon: Icon,
  iconClassName,
  items,
}: {
  label: string
  badgeVariant: "secondary" | "destructive"
  icon: LucideIcon
  iconClassName: string
  items: string[]
}) {
  return (
    <div>
      <Badge variant={badgeVariant}>{label}</Badge>
      <ul className="mt-1.5 flex flex-col gap-1.5">
        {items.map((item, index) => (
          <li key={index} className="flex items-start gap-2 text-sm leading-snug text-secondary">
            <Icon className={`mt-0.5 size-3.5 shrink-0 ${iconClassName}`} aria-hidden />
            <span className="min-w-0">{item}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}
