import { useState, type ReactNode } from "react"
import { Binary, Copy } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Switch } from "@/components/ui/switch"
import { Textarea } from "@/components/ui/textarea"

type EncodeTextPageProps = {
  invoke: <T>(action: string, payload?: unknown) => Promise<T>
}

export function EncodeTextPage({ invoke }: EncodeTextPageProps) {
  const [mode, setMode] = useState("encode")
  const [format, setFormat] = useState("Base64")
  const [input, setInput] = useState("")
  const [output, setOutput] = useState("")
  const [preserveLineBreaks, setPreserveLineBreaks] = useState(true)
  const [isRunning, setIsRunning] = useState(false)

  async function run() {
    setIsRunning(true)
    try {
      const response = await invoke<{ output: string; inputLength: number; outputLength: number }>("text.convert", { mode, format, input, preserveLineBreaks })
      setOutput(response.output)
      toast.success(`${format} ${mode === "decode" ? "decoded" : "encoded"}.`)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Conversion failed.")
    } finally {
      setIsRunning(false)
    }
  }

  async function copyOutput() {
    await navigator.clipboard.writeText(output)
    toast.success("Output copied.")
  }

  return (
    <div className="security-page">
      <section className="security-section">
        <div className="mb-4 flex items-start gap-3">
          <Binary className="mt-1 size-4 text-accent" aria-hidden />
          <div>
            <div className="security-section-title">Encode Text</div>
            <p className="security-description">Convert text between common formats.</p>
          </div>
        </div>
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Mode">
            <Select value={mode} onValueChange={setMode}>
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
            <Select value={format} onValueChange={setFormat}>
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
        </div>
        <Toggle label="Preserve line breaks" checked={preserveLineBreaks} onChange={setPreserveLineBreaks} />
      </section>

      <section className="security-section">
        <label className="flex flex-col gap-2">
          <span className="security-label">Input</span>
          <Textarea value={input} onChange={(event) => setInput(event.target.value)} placeholder="Input text" className="min-h-64" />
        </label>
        <Button className="mt-4 w-full" onClick={run} disabled={!input || isRunning}>
          {isRunning ? "Converting" : "Run Conversion"}
        </Button>
      </section>

      <section className="security-section">
        <div className="mb-4 flex items-end justify-between gap-3">
          <div>
            <div className="security-section-title">Output</div>
            <p className="security-description">{output.length} characters</p>
          </div>
          <Button variant="secondary" onClick={copyOutput} disabled={!output}>
            <Copy data-icon="inline-start" />
            Copy Output
          </Button>
        </div>
        <pre className={`terminal-output min-h-64 ${output ? "" : "text-secondary"}`}>{output || "Converted output will appear here."}</pre>
      </section>
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-2">
      <span className="security-label">{label}</span>
      {children}
    </label>
  )
}

function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="mt-4 flex min-h-11 items-center justify-between gap-3 border-y border-border px-3 py-3 text-sm text-secondary">
      <span>{label}</span>
      <Switch checked={checked} onCheckedChange={onChange} />
    </label>
  )
}
