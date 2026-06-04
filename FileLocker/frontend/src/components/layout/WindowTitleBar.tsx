export function WindowTitleBar() {
  return (
    <div className="app-window-titlebar" aria-hidden="true">
      <div className="app-window-titlebar-sidebar" />
      <div className="app-window-titlebar-main">
        <div className="app-window-titlebar-caption-space" />
        <div className="app-window-titlebar-drag" />
        <div className="app-window-titlebar-caption-space" />
      </div>
    </div>
  )
}
