import { useEffect, useState } from "react"
import { CheckCircle2, ExternalLink, GitBranch, Info, LockKeyhole, Monitor, RefreshCw, Shield, Stethoscope, XCircle, type LucideIcon } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { invokeBridge } from "@/services/bridge"
import type { InitialState, SettingsState, UpdateCheckResult } from "@/types/bridge"

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

function detectWebViewStatus(): { available: boolean; label: string } {
  const available = typeof window !== "undefined" && Boolean(window.chrome?.webview)
  return { available, label: available ? "Connected" : "Unavailable" }
}

export function AboutPage({ app, onOpenRepository }: AboutPageProps) {
  const [updatesEnabled, setUpdatesEnabled] = useState(false)
  const [updateStatus, setUpdateStatus] = useState("Not checked yet")
  const [checking, setChecking] = useState(false)

  const webView = detectWebViewStatus()

  useEffect(() => {
    let active = true
    invokeBridge<SettingsState>("settings.get")
      .then((settings) => {
        if (active) {
          setUpdatesEnabled(settings.updates.autoCheckEnabled)
        }
      })
      .catch(() => {
        if (active) {
          setUpdatesEnabled(false)
        }
      })
    return () => {
      active = false
    }
  }, [])

  async function checkForUpdates() {
    setChecking(true)
    try {
      const response = await invokeBridge<UpdateCheckResult>("updates.check")
      setUpdateStatus(response.statusMessage)
      toast[response.isUpdateAvailable ? "success" : "message"](response.statusMessage)
    } catch (error) {
      const message = error instanceof Error ? error.message : "Update check failed."
      setUpdateStatus(message)
      toast.error(message)
    } finally {
      setChecking(false)
    }
  }

  return (
    <div className="security-page">
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_300px]">
        <section className="section-surface">
          <div className="flex items-start gap-3">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent-blue/12 text-accent-blue">
              <Shield className="size-4" aria-hidden />
            </div>
            <div className="min-w-0">
              <h2 className="font-display text-lg font-semibold leading-tight text-primary">FileLocker</h2>
              <p className="mt-1 max-w-3xl text-sm leading-snug text-secondary">
                A local-first desktop security utility for encryption, decryption, hashing, text encoding, metadata inspection, and secure delete workflows.
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <Button onClick={onOpenRepository}>
                  <ExternalLink data-icon="inline-start" />
                  Open Project Page
                </Button>
                <span className="inline-flex h-9 items-center rounded-md border border-border bg-transparent px-3 font-mono text-sm text-secondary">Version {app.version}</span>
              </div>
            </div>
          </div>
        </section>

        <aside className="border-l border-border pl-4 xl:row-span-4">
          <SideFact icon={Monitor} label="App" value="Windows Desktop" detail="Runs locally on your machine." />
          <SideFact icon={LockKeyhole} label="Security" value="Local First" detail="Passwords and file contents stay on this device." />
          <SideFact icon={GitBranch} label="Updates" value="Project Releases" detail="Optional checks look for newer FileLocker versions." />
        </aside>

        <section className="section-surface">
          <div className="mb-3 flex items-start gap-3">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent-blue/10 text-accent-blue">
              <RefreshCw className="size-4" aria-hidden />
            </div>
            <div className="min-w-0">
              <h2 className="font-display text-base font-semibold leading-tight text-primary">Updates</h2>
              <p className="mt-1 text-sm leading-snug text-secondary">
                {updatesEnabled ? "Check the project release page for a newer FileLocker version." : "Update checks are turned off in Settings."}
              </p>
            </div>
          </div>
          <div className="flex flex-col gap-3 border-y border-border py-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="min-w-0">
              <div className="security-label">Status</div>
              <div className="mt-1 break-words font-mono text-sm text-secondary">{updateStatus}</div>
            </div>
            <Button variant="secondary" onClick={checkForUpdates} disabled={!updatesEnabled || checking} className="shrink-0">
              <RefreshCw data-icon="inline-start" className={checking ? "animate-spin" : undefined} />
              {checking ? "Checking..." : "Check Updates"}
            </Button>
          </div>
        </section>

        <section className="section-surface">
          <div className="mb-3 flex items-start gap-3">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent-blue/10 text-accent-blue">
              <Stethoscope className="size-4" aria-hidden />
            </div>
            <div className="min-w-0">
              <h2 className="font-display text-base font-semibold leading-tight text-primary">Diagnostics</h2>
              <p className="mt-1 text-sm leading-snug text-secondary">Environment details for troubleshooting.</p>
            </div>
          </div>
          <div className="overflow-hidden border-y border-border">
            <InfoRow label="App Version" value={app.version} />
            <DiagnosticRow label="WebView2 Runtime" value={webView.label} ok={webView.available} />
            <InfoRow label="Project Link" value={app.repositoryUrl} />
          </div>
        </section>

        <section className="section-surface">
          <div className="mb-3 flex items-start gap-3">
            <div className="flex size-8 shrink-0 items-center justify-center rounded-md bg-accent-blue/10 text-accent-blue">
              <Info className="size-4" aria-hidden />
            </div>
            <div>
              <h2 className="font-display text-base font-semibold leading-tight text-primary">App Details</h2>
              <p className="mt-1 text-sm leading-snug text-secondary">What FileLocker handles and where your files stay.</p>
            </div>
          </div>
          <div className="overflow-hidden border-y border-border">
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
    <div className="grid gap-3 border-b border-border py-2.5 last:border-b-0 md:grid-cols-[160px_1fr]">
      <div className="security-label">{label}</div>
      <div className="min-w-0 break-words font-mono text-sm text-secondary">{value}</div>
    </div>
  )
}

function DiagnosticRow({ label, value, ok }: { label: string; value: string; ok: boolean }) {
  const Icon = ok ? CheckCircle2 : XCircle
  return (
    <div className="grid gap-3 border-b border-border py-2.5 last:border-b-0 md:grid-cols-[160px_1fr]">
      <div className="security-label">{label}</div>
      <div className={`flex min-w-0 items-center gap-2 break-words font-mono text-sm ${ok ? "text-accent-green" : "text-accent-red"}`}>
        <Icon className="size-4 shrink-0" aria-hidden />
        {value}
      </div>
    </div>
  )
}

function SideFact({ icon: Icon, label, value, detail }: { icon: LucideIcon; label: string; value: string; detail: string }) {
  return (
    <section className="section-surface">
      <div className="flex items-start gap-3">
        <div className="flex size-7 shrink-0 items-center justify-center rounded-md bg-accent-blue/10 text-accent-blue">
          <Icon className="size-4" aria-hidden />
        </div>
        <div className="min-w-0">
          <div className="security-label">{label}</div>
          <div className="mt-1 font-display text-base font-semibold text-primary">{value}</div>
          <p className="mt-1 text-sm leading-snug text-secondary">{detail}</p>
        </div>
      </div>
    </section>
  )
}
