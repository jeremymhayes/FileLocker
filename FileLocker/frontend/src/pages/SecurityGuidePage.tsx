const guideSections = [
  {
    title: "Encryption Basics",
    body:
      "FileLocker uses AES-256-GCM for file encryption. AES-256 is the encryption algorithm and key size; GCM is an authenticated mode, which means the encrypted file includes integrity data that lets FileLocker detect tampering before restoring output. A .locked file contains encrypted file data plus the information FileLocker needs to identify and validate the file format.",
  },
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

export function SecurityGuidePage() {
  return (
    <div className="security-page">
      <section className="surface-card">
        <div className="mb-6">
          <h2 className="font-display text-xl font-semibold text-primary">Security Guide</h2>
          <p className="mt-2 max-w-[680px] text-[15px] leading-[1.65] text-secondary">Practical guidance for using FileLocker safely on a local Windows desktop.</p>
        </div>
        <div className="space-y-6">
          {guideSections.map((section) => (
            <article key={section.title} className="border-b border-border pb-6 last:border-b-0 last:pb-0">
              <h3 className="font-display text-base font-semibold text-primary">{section.title}</h3>
              <p className="mt-2 max-w-[680px] text-[15px] leading-[1.65] text-secondary">{section.body}</p>
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}
