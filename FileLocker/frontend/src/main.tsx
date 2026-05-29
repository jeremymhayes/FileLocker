import React from "react"
import ReactDOM from "react-dom/client"
import "file-icon-vectors/dist/file-icon-classic.min.css"
import { App } from "./App"
import "./styles/globals.css"

async function bootstrap() {
  // Dev-only: stand up a fake WebView2 bridge so the UI renders (and can be
  // reviewed in a browser) without the WinUI host. Never runs in production.
  if (import.meta.env.DEV) {
    const { installDevBridgeMock } = await import("./services/devBridgeMock")
    installDevBridgeMock()
  }

  ReactDOM.createRoot(document.getElementById("root")!).render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  )
}

void bootstrap()
