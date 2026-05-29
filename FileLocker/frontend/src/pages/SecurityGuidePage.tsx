import { getDefaultEncryptionAlgorithm, getEncryptionAlgorithmOptions } from "@/lib/encryptionAlgorithms"
import type { EncryptionAlgorithmOption } from "@/types/bridge"

type SecurityGuidePageProps = {
  encryptionAlgorithms?: EncryptionAlgorithmOption[]
}

const fixedGuideSections = [
  {
    title: "Choosing a Strong Password",
    body:
      "Use a passphrase when possible: several unrelated words are usually easier to remember and stronger than a short complex password. Length matters more than symbol tricks. FileLocker cannot recover forgotten passwords and does not include a backdoor. Store important passwords in a password manager rather than in a plain text file on the same disk.",
  },
  {
    title: "What FileLocker Encrypts (and What It Doesn't)",
    body:
      "FileLocker encrypts file contents. It does not automatically hide every filename unless you choose an option that changes output names or rename files before encryption. Filesystem-level details such as file size, folder location, timestamps, and the presence of a .locked file may still be visible to someone with access to the device.",
  },
  {
    title: "File Integrity and Hashing",
    body:
      "A hash is a fingerprint of data. SHA-256 is a strong default for verifying downloads and detecting accidental or malicious changes. SHA-512 produces a larger digest and can be useful for workflows that standardize on it. Hashing is not encryption: a hash helps verify that content did not change, but it does not hide the content.",
  },
  {
    title: "Secure Delete",
    body:
      "Secure delete overwrites file data before deletion where the filesystem and storage device allow it. This is more reliable on traditional hard drives than on SSDs, where wear leveling can leave old data in blocks the operating system no longer controls directly. Use BitLocker or another full-disk encryption layer as a complement for stronger device-level protection.",
  },
  {
    title: "Metadata Scrambler",
    body:
      "Metadata can include timestamps, author fields, software names, camera details, and GPS data in images. FileLocker can preview removal or scrambling for supported formats, but no general-purpose tool can guarantee that every possible metadata field is removed from every file type. When metadata removal is critical, verify the final file with a specialized tool such as ExifTool.",
  },
  {
    title: "Best Practices",
    body:
      "Test decryption on a copy before deleting originals. Keep backups of important encrypted files. Store passwords in a password manager, not beside the encrypted files. FileLocker is local-only: files never leave your machine.",
  },
  {
    title: "What FileLocker Is Not",
    body:
      "FileLocker is not a VPN, network security tool, password manager, full-disk encryption replacement, or recovery tool. If the wrong password is used and no recovery material exists, the encrypted file should be treated as inaccessible.",
  },
]

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
  const supportedAlgorithms = getEncryptionAlgorithmOptions(encryptionAlgorithms)
  const defaultAlgorithm = getDefaultEncryptionAlgorithm(supportedAlgorithms)?.label
  const algorithmList = formatList(supportedAlgorithms.map((algorithm) => algorithm.label))
  const pngCarrierAlgorithms = formatList(supportedAlgorithms.filter((algorithm) => algorithm.canUsePngCarrier).map((algorithm) => algorithm.label))
  const pngCarrierText = pngCarrierAlgorithms
    ? `PNG carrier output uses the older AES-GCM carrier path and is only available with ${pngCarrierAlgorithms}.`
    : "PNG carrier output is not available for the current encryption options."
  const guideSections = [
    {
      title: "Encryption Basics",
      body: defaultAlgorithm
        ? `FileLocker defaults to ${defaultAlgorithm}. New .locked payload options are ${algorithmList}. These are authenticated modes, so the encrypted file carries integrity data that lets FileLocker detect tampering before restoring output. Decryption reads the saved algorithm from the payload header. ${pngCarrierText}`
        : "No supported file-encryption algorithm is available on this runtime. FileLocker will not offer new encrypted payload creation until the platform crypto support check passes.",
    },
    ...fixedGuideSections,
  ]

  return (
    <div className="security-page">
      <section className="section-surface">
        <div className="mb-3">
          <h2 className="font-display text-lg font-semibold text-primary">Security Guide</h2>
          <p className="mt-1 max-w-[680px] text-sm leading-snug text-secondary">Practical guidance for using FileLocker safely on a local Windows desktop.</p>
        </div>
        <div className="flex flex-col gap-0">
          {guideSections.map((section) => (
            <article key={section.title} className="border-b border-border py-3 first:pt-0 last:border-b-0 last:pb-0">
              <h3 className="font-display text-base font-semibold text-primary">{section.title}</h3>
              <p className="mt-1 max-w-[680px] text-sm leading-snug text-secondary">{section.body}</p>
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
