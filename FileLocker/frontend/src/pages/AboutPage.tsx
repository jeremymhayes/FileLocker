import { ExternalLink, GitBranch, Info, LockKeyhole, Monitor, Shield, type LucideIcon } from "lucide-react"
import { Button } from "@/components/ui/button"
import type { InitialState } from "@/types/bridge"

type AboutPageProps = {
  app: InitialState["app"]
  onOpenRepository: () => void
}

const runtimeRows = [
  ["Protection", "Encrypt, decrypt, hash, inspect metadata, and securely remove local files"],
  ["Storage", "Activity history stays on this device and follows your privacy setting"],
  ["Updates", "FileLocker can check the project release page when update checks are enabled"],
  ["Privacy", "Files are handled locally and are not uploaded by FileLocker"],
]

export function AboutPage({ app, onOpenRepository }: AboutPageProps) {
  return (
    <div className="security-page">
      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <section className="surface-card">
          <div className="flex items-start gap-4">
            <div className="flex size-14 shrink-0 items-center justify-center rounded-2xl bg-accent-blue text-white">
              <Shield className="size-7" aria-hidden />
            </div>
            <div className="min-w-0">
              <h2 className="font-display text-2xl font-semibold leading-[1.3] text-primary">FileLocker</h2>
              <p className="mt-2 max-w-3xl text-sm leading-[1.65] text-secondary">
                A local-first desktop security utility for encryption, decryption, hashing, text encoding, metadata inspection, and secure delete workflows.
              </p>
              <div className="mt-5 flex flex-wrap gap-3">
                <Button onClick={onOpenRepository}>
                  <ExternalLink data-icon="inline-start" />
                  Open Project Page
                </Button>
                <span className="inline-flex h-10 items-center rounded-xl border border-border bg-bg-dropzone px-4 font-mono text-sm text-secondary">Version {app.version}</span>
              </div>
            </div>
          </div>
        </section>

        <aside className="space-y-4 xl:row-span-2">
          <SideFact icon={Monitor} label="App" value="Windows Desktop" detail="Runs locally on your machine." />
          <SideFact icon={LockKeyhole} label="Security" value="Local First" detail="Passwords and file contents stay on this device." />
          <SideFact icon={GitBranch} label="Updates" value="Project Releases" detail="Optional checks look for newer FileLocker versions." />
        </aside>

        <section className="surface-card">
          <div className="mb-5 flex items-start gap-3">
            <div className="flex size-11 shrink-0 items-center justify-center rounded-xl bg-accent-blue/10 text-accent-blue">
              <Info className="size-5" aria-hidden />
            </div>
            <div>
              <h2 className="font-display text-lg font-semibold leading-[1.3] text-primary">App Details</h2>
              <p className="mt-1 text-sm text-secondary">What FileLocker handles and where your files stay.</p>
            </div>
          </div>
          <div className="overflow-hidden rounded-2xl border border-border">
            {runtimeRows.map(([label, value]) => (
              <InfoRow key={label} label={label} value={value} />
            ))}
            <InfoRow label="Project Link" value={app.repositoryUrl} />
          </div>
        </section>
      </div>
    </div>
  )
}

function InfoRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid gap-3 border-b border-border bg-bg-dropzone px-4 py-4 last:border-b-0 md:grid-cols-[220px_1fr]">
      <div className="security-label">{label}</div>
      <div className="min-w-0 break-words font-mono text-sm text-secondary">{value}</div>
    </div>
  )
}

function SideFact({ icon: Icon, label, value, detail }: { icon: LucideIcon; label: string; value: string; detail: string }) {
  return (
    <section className="surface-card">
      <div className="flex items-start gap-3">
        <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-accent-blue/10 text-accent-blue">
          <Icon className="size-5" aria-hidden />
        </div>
        <div className="min-w-0">
          <div className="security-label">{label}</div>
          <div className="mt-2 font-display text-lg font-semibold text-primary">{value}</div>
          <p className="mt-1 text-sm leading-[1.55] text-secondary">{detail}</p>
        </div>
      </div>
    </section>
  )
}
