import { Lock } from "lucide-react"
import { fileExtension, fileIconClass } from "@/lib/format"

type FileTypeIconProps = {
  filename: string
  size?: "sm" | "md"
}

export function FileTypeIcon({ filename, size = "sm" }: FileTypeIconProps) {
  if (fileExtension(filename) === "locked") {
    return <Lock size={size === "md" ? 24 : 18} color="var(--accent-blue)" aria-hidden />
  }

  return <span className={fileIconClass(filename)} style={{ fontSize: size === "md" ? "1.5rem" : "1.125rem" }} aria-hidden />
}
