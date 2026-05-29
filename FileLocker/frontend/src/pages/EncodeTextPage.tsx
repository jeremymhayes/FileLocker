import { useMemo, useState } from "react"
import {
  Binary,
  BookOpen,
  CheckCircle2,
  Copy,
  Eraser,
  FileText,
  Info,
  Languages,
  RefreshCw,
} from "lucide-react"
import { toast } from "sonner"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Section, SectionBody, SectionFooter, SectionHeader, SectionTitle } from "@/components/layout/Workspace"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { Textarea } from "@/components/ui/textarea"
import { Field } from "@/components/common/Field"
import { cn } from "@/lib/utils"

type EncodeTextPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
}

const encodeGuidePoints = [
  "Encoding changes how text is represented. It does not secure the contents like encryption does.",
  "Use Base64 when you need a portable text-safe representation of binary or structured text.",
  "Use URL encoding when text needs to be safely embedded in a web address or query string.",
  "Use Decode when you already have encoded text and want to restore its readable form locally.",
]

const exampleInputs: Record<string, string> = {
  Base64: "Quarterly report ready for secure delivery.",
  URL: "customer=jeremy hayes&status=ready to send",
  Hex: "FileLocker 1.1.1.0",
  "HTML Entities": "<strong>Encode Text</strong> keeps everything local.",
  "UTF-8": "Privacy-first text workflows for Windows.",
}

const MAX_ENCODE_TEXT_INPUT_CHARS = 1024 * 1024

export function EncodeTextPage({ invoke }: EncodeTextPageProps) {
  const [mode, setMode] = useState("encode")
  const [format, setFormat] = useState("Base64")
  const [input, setInput] = useState("")
  const [output, setOutput] = useState("")
  const [conversionError, setConversionError] = useState("")
  const [preserveLineBreaks, setPreserveLineBreaks] = useState(true)
  const [isRunning, setIsRunning] = useState(false)

  const inputLength = input.length
  const outputLength = output.length
  const modeLabel = mode === "decode" ? "Decode" : "Encode"
  const statusText = !input
    ? "Waiting for input"
    : isRunning
      ? "Converting"
      : conversionError
        ? "Failed"
      : output
        ? "Ready"
        : "Input ready"
  const outputStatusText = isRunning ? "Converting" : conversionError ? "Conversion failed" : output ? "Generated successfully" : "Run a conversion to populate output"
  const outputBadgeText = isRunning ? "Converting" : conversionError ? "Failed" : output ? "Ready" : "Waiting"

  const quickExample = useMemo(() => exampleInputs[format] ?? exampleInputs.Base64, [format])

  async function run() {
    if (isRunning) {
      return
    }

    if (!input) {
      toast.error("Enter text before running a conversion.")
      return
    }

    setOutput("")
    setConversionError("")
    setIsRunning(true)
    try {
      const response = await invoke<{ output: string; inputLength: number; outputLength: number }>("text.convert", { mode, format, input, preserveLineBreaks })
      setOutput(response.output)
      toast.success(`${format} ${mode === "decode" ? "decoded" : "encoded"}.`)
    } catch (error) {
      const message = error instanceof Error && error.message ? error.message : "Conversion failed."
      setConversionError(message)
      toast.error(message)
    } finally {
      setIsRunning(false)
    }
  }

  function updateInput(value: string) {
    if (isRunning) {
      return
    }

    setInput(value)
    setOutput("")
    setConversionError("")
  }

  function updateMode(value: string) {
    if (isRunning) {
      return
    }

    if (value !== mode) {
      setOutput("")
      setConversionError("")
    }

    setMode(value)
  }

  function updateFormat(value: string) {
    if (isRunning) {
      return
    }

    if (value !== format) {
      setOutput("")
      setConversionError("")
    }

    setFormat(value)
  }

  function updatePreserveLineBreaks(value: boolean) {
    if (isRunning) {
      return
    }

    if (value !== preserveLineBreaks) {
      setOutput("")
      setConversionError("")
    }

    setPreserveLineBreaks(value)
  }

  async function copyOutput() {
    if (!output) {
      return
    }

    try {
      await navigator.clipboard.writeText(output)
      toast.success("Output copied.")
    } catch {
      toast.error("Output could not be copied.")
    }
  }

  function loadExample() {
    if (isRunning) {
      return
    }

    setInput(quickExample)
    setOutput("")
    setConversionError("")
  }

  function clearAll() {
    if (isRunning) {
      return
    }

    setInput("")
    setOutput("")
    setConversionError("")
  }

  return (
    <div className="security-page">
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-3 xl:flex-row xl:items-start xl:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-3">
              <div className="flex size-8 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent ">
                <Binary className="size-4" aria-hidden />
              </div>
              <div>
                <h2 className="font-display text-lg font-semibold tracking-tight text-primary">Encode Text</h2>
                <p className="mt-1 max-w-3xl text-sm leading-snug text-secondary">
                  Convert text between common transport-safe formats locally, then copy the result wherever you need it.
                </p>
              </div>
            </div>
          </div>

          <div className="flex shrink-0 flex-wrap gap-2">
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline" size="lg">
                  <BookOpen data-icon="inline-start" />
                  Encoding Guide
                </Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-2xl">
                <DialogHeader>
                  <DialogTitle>Encoding Guide</DialogTitle>
                  <DialogDescription>What to keep in mind when converting text locally.</DialogDescription>
                </DialogHeader>
                <div className="flex flex-col gap-3">
                  {encodeGuidePoints.map((point) => (
                    <div key={point} className="rounded-md border border-border bg-background/40 px-3 py-2 text-sm leading-snug text-secondary">
                      {point}
                    </div>
                  ))}
                </div>
              </DialogContent>
            </Dialog>

            <Button variant="outline" size="lg" onClick={() => void copyOutput()} disabled={!output}>
              <Copy data-icon="inline-start" />
              Copy Results
            </Button>
          </div>
        </div>

        <div className="grid gap-4 2xl:grid-cols-[minmax(0,1.32fr)_410px]">
          <div className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Input Text</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">
                      Write or paste text, then run a local conversion using the mode and format you choose.
                    </p>
                  </div>
                  <Badge variant="outline">{inputLength} characters</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="rounded-md border border-border/60 bg-bg-subtle/35 p-3">
                  <Textarea
                    value={input}
                    onChange={(event) => updateInput(event.target.value)}
                    placeholder="Enter text to encode or decode"
                    maxLength={MAX_ENCODE_TEXT_INPUT_CHARS}
                    className="min-h-[180px] border-none bg-transparent px-0 py-0 font-mono text-sm leading-snug shadow-none focus-visible:ring-0"
                    disabled={isRunning}
                  />
                </div>

                <div className="flex flex-wrap gap-2">
                  <Button onClick={loadExample} variant="secondary" disabled={isRunning}>
                    <FileText data-icon="inline-start" />
                    Load Example
                  </Button>
                  <Button onClick={run} disabled={!input || isRunning}>
                    <RefreshCw data-icon="inline-start" className={cn(isRunning && "animate-spin")} />
                    {isRunning ? "Converting" : "Run Conversion"}
                  </Button>
                  <Button onClick={clearAll} variant="outline" disabled={(!input && !output) || isRunning}>
                    <Eraser data-icon="inline-start" />
                    Clear
                  </Button>
                </div>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div>
                    <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Conversion Output</SectionTitle>
                    <p className="mt-1 text-sm leading-snug text-secondary">{outputLength} characters</p>
                  </div>
                  <Badge variant={conversionError ? "destructive" : output ? "secondary" : "outline"}>{outputBadgeText}</Badge>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <div className="text-sm text-secondary">{modeLabel} using {format}</div>
                <div className={cn("rounded-md border bg-background/35 px-3 py-3", conversionError ? "border-destructive/55" : "border-sky-400/55")}>
                  <pre className={cn("min-h-[150px] whitespace-pre-wrap break-all font-mono text-sm leading-snug", conversionError ? "text-red-100" : output ? "text-primary" : "text-secondary")}>
                    {conversionError || output || "No output. Run a conversion to generate text."}
                  </pre>
                </div>

                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div className={cn("rounded-md border px-3 py-2 text-sm leading-snug", conversionError ? "border-destructive/25 bg-destructive/10 text-red-100" : output ? "border-accent-green/25 bg-accent-green/8 text-accent-green" : "border-border/80 bg-background/35 text-secondary")}>
                    {outputStatusText}
                  </div>
                  <Button onClick={() => void copyOutput()} disabled={!output}>
                    <Copy data-icon="inline-start" />
                    Copy Output
                  </Button>
                </div>
              </SectionBody>
            </Section>
          </div>

          <aside className="flex min-w-0 flex-col gap-4">
            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-accent/25 bg-accent/10 text-accent">
                    <Languages className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Conversion Options</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-3 px-4 py-3">
                <Field label="Mode">
                  <Select value={mode} onValueChange={updateMode} disabled={isRunning}>
                    <SelectTrigger>
                      <SelectValue placeholder="Mode" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        <SelectItem value="encode">Encode</SelectItem>
                        <SelectItem value="decode">Decode</SelectItem>
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                </Field>

                <Field label="Format">
                  <Select value={format} onValueChange={updateFormat} disabled={isRunning}>
                    <SelectTrigger>
                      <SelectValue placeholder="Format" />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectGroup>
                        <SelectItem value="Base64">Base64</SelectItem>
                        <SelectItem value="URL">URL</SelectItem>
                        <SelectItem value="Hex">Hex</SelectItem>
                        <SelectItem value="HTML Entities">HTML Entities</SelectItem>
                        <SelectItem value="UTF-8">UTF-8</SelectItem>
                      </SelectGroup>
                    </SelectContent>
                  </Select>
                </Field>

                <label className="flex min-h-10 items-center justify-between gap-3 rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2 transition-colors hover:border-accent/30 hover:bg-bg-surface-hover/70">
                  <span className="min-w-0 flex-1">
                    <span className="block font-display text-sm font-semibold tracking-tight text-primary">Preserve line breaks</span>
                    <span className="mt-0.5 block text-xs leading-snug text-secondary">Keep line breaks when safe for the chosen format.</span>
                  </span>
                  <Switch checked={preserveLineBreaks} onCheckedChange={updatePreserveLineBreaks} disabled={isRunning} />
                </label>
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-border/70 bg-background/35 text-secondary">
                    <Info className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Operation Summary</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-2 px-4 py-3">
                <SummaryRow icon={Binary} label="Mode" value={modeLabel} />
                <SummaryRow icon={Languages} label="Format" value={format} />
                <SummaryRow icon={FileText} label="Input length" value={`${inputLength} characters`} />
                <SummaryRow icon={FileText} label="Output length" value={`${outputLength} characters`} />
                <SummaryRow icon={conversionError ? Info : output ? CheckCircle2 : Info} label="Status" value={statusText} good={Boolean(output)} />
              </SectionBody>
            </Section>

            <Section className="overflow-hidden rounded-md border border-border bg-transparent py-0 shadow-none ring-0">
              <SectionHeader className="border-b border-border/80 px-4 py-3">
                <div className="flex items-center gap-3">
                  <div className="flex size-8 items-center justify-center rounded-md border border-accent-green/25 bg-accent-green/10 text-accent-green">
                    <BookOpen className="size-4" aria-hidden />
                  </div>
                  <SectionTitle className="font-display text-base font-semibold tracking-tight text-primary">Quick Reference</SectionTitle>
                </div>
              </SectionHeader>
              <SectionBody className="flex flex-col gap-2 px-4 py-3">
                {quickReferenceRows.map((row) => (
                  <div key={row.label} className="rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2">
                    <div className="font-display text-sm font-semibold tracking-tight text-primary">{row.label}</div>
                    <p className="mt-0.5 text-xs leading-snug text-secondary">{row.description}</p>
                  </div>
                ))}
              </SectionBody>
              <SectionFooter className="flex gap-2 border-t border-border bg-transparent px-4 py-3">
                <Button className="flex-1" onClick={run} disabled={!input || isRunning}>
                  <Binary data-icon="inline-start" />
                  {isRunning ? "Converting" : "Run Conversion"}
                </Button>
                <Button className="flex-1" variant="outline" onClick={clearAll} disabled={(!input && !output) || isRunning}>
                  <Eraser data-icon="inline-start" />
                  Clear
                </Button>
              </SectionFooter>
            </Section>
          </aside>
        </div>
      </div>
    </div>
  )
}

type SummaryRowProps = {
  icon: typeof FileText
  label: string
  value: string
  good?: boolean
}

function SummaryRow({ icon: Icon, label, value, good = false }: SummaryRowProps) {
  return (
    <div className="flex min-h-9 items-center justify-between gap-2 rounded-md border border-border/60 bg-bg-subtle/35 px-3 py-2">
      <div className="flex min-w-0 items-center gap-2.5">
        <div className={cn("flex size-7 shrink-0 items-center justify-center rounded-md border", good ? "border-accent-green/30 bg-accent-green/10 text-accent-green" : "border-border/70 bg-background/35 text-secondary")}>
          <Icon className="size-4" aria-hidden />
        </div>
        <span className="text-sm text-secondary">{label}</span>
      </div>
      <span className={cn("max-w-[13rem] truncate text-right font-display text-sm font-semibold tracking-tight", good ? "text-accent-green" : "text-primary")}>{value}</span>
    </div>
  )
}

const quickReferenceRows = [
  {
    label: "Base64",
    description: "Good for moving text safely through systems that expect plain ASCII characters.",
  },
  {
    label: "URL",
    description: "Useful when you need to safely place text inside a query string or URL path.",
  },
  {
    label: "Hex",
    description: "Helpful for byte-oriented or low-level text representations.",
  },
]
