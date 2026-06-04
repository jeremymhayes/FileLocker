# FileLocker UI/UX Improvement Audit

Date: 2026-06-04

Scope reviewed:
- `frontend/src/App.tsx` route map and shared shell.
- `frontend/src/components/layout/*` for sidebar, page header, workspace, and title-bar layout.
- Every routed page under `frontend/src/pages/*`.
- Browser-rendered desktop route checks for `#dashboard`, `#encrypt`, `#decrypt`, `#hash`, `#encode`, `#metadata`, `#secure-delete`, `#custom-clean`, `#partition-cleaner`, `#drive-optimizer`, `#registry-fixer`, `#startup-manager`, `#app-manager`, `#settings`, `#about`, and `#security-guide`.

## Product Direction

FileLocker is already powerful, but the UI should feel more like a major Windows utility: simple at first glance, clear about safety, and still deep for advanced users. CCleaner and Malwarebytes work because they give users one obvious next step, then reveal detail after scan, selection, or review. FileLocker should use the same pattern:

1. Start with a recommended action.
2. Show a plain-English safety summary.
3. Put advanced controls behind a named disclosure, drawer, or mode.
4. Keep dense tables for review states, not first-run empty states.
5. Make destructive actions visibly staged: scan, review, confirm, apply.

## Top Native Title Bar

Implemented change:
- The title-bar spacer in `frontend/src/styles/globals.css` no longer uses a fixed `38px` height.
- It now reads the WebView title-bar height when available and clamps the spacer to `28px` through `32px`.
- This keeps the drag/caption area but removes the oversized blank strip between the native Windows title bar area and page content.

Recommended follow-up:
- Verify the packaged WinUI/WebView2 app on Windows with the native minimize, maximize, and close buttons visible. Browser screenshots can confirm the web layout, but not the final OS caption-button overlap.
- Consider moving a compact app mark or page breadcrumb into the title-bar drag region so the top band feels intentional instead of empty.

## Highest Priority Changes

### 1. Add Simple and Advanced Layers

Current issue:
- Pages expose strong controls immediately: algorithms, output paths, timestamp policy, keyfile, recovery key, delete originals, secure delete, scan filters, table filters, and cleanup categories.
- This is good for power users but makes first-time use feel heavier than major utility apps.

Suggestion:
- Default every workflow to a simple mode with one recommended path.
- Move optional controls into an `Advanced` disclosure:
  - Encrypt: algorithm, keyfile, recovery key, timestamp policy, metadata note, PNG carrier, secure delete originals.
  - Decrypt: recovery key, keyfile, output naming, restore filename controls.
  - Hash: manifests and hash-length details.
  - Metadata: category-level selection and randomize mode.
  - System care pages: category filters, advanced hooks, registry key columns, details panels.

### 2. Standardize the Page Pattern

Current issue:
- Some pages use the shared `PageHeader`, while others render their own header inside the page.
- First-screen spacing and action placement varies by page.

Suggestion:
- Use one standard layout:
  - Page title and short description at top.
  - Primary action on the top right only when it is usable.
  - Main work area on the left.
  - Summary, safety, and advanced controls on the right.
  - A consistent footer action area for final destructive or apply actions.

### 3. Hide Disabled Action Buttons Until They Matter

Current issue:
- System-care first-run pages show disabled actions such as `Clean Selected`, `Uninstall Selected`, or `Select All` before a scan has run.
- This makes empty pages feel inactive and slightly broken.

Suggestion:
- On first-run empty states, show only the primary `Run Scan` action.
- After scan, reveal selection actions and advanced filters.
- For any disabled button that stays visible, add a tooltip or inline reason: `Run a scan first`, `Select an item`, or `Restart as Administrator`.

### 4. Make Safety Levels Consistent

Current issue:
- Different pages warn users in different ways: badges, inline notices, confirm dialogs, disabled buttons, and text blocks.

Suggestion:
- Create a shared `RiskBadge` or `SafetyBadge` scale:
  - Safe
  - Review
  - Admin
  - Destructive
  - Advanced
- Use the same colors and copy on Custom Clean, Registry Fixer, Secure Delete, Startup Manager, and App Manager.

### 5. Use Scan-Review-Apply for System Care

Current issue:
- System-care tools are close to CCleaner/Malwarebytes, but the flow is not always explicit.

Suggestion:
- Make all system-care pages follow:
  - Scan
  - Review findings
  - Select recommended items
  - Apply / clean / fix
  - Show results and restore path
- Use a small step indicator when a tool can modify the system.

## Shared Shell and Navigation

### Sidebar

Suggestions:
- Rename top-level groups to simpler product language:
  - `File Security` -> `Protect`
  - `System Care` -> `Clean & Optimize`
- Add a `Home` or `Overview` dashboard label instead of only `Dashboard`.
- Consider a compact sidebar mode for smaller windows, with icons plus tooltips.
- Add status markers next to system-care items after scans, such as `3.2 GB`, `4 issues`, or `High impact`.
- Keep `Settings` and `About` separated at the bottom, but remove the About tab inside Settings or make it clearly secondary.

### Page Header

Suggestions:
- Use shared `PageHeader` everywhere, including Encrypt, Decrypt, Hash, Encode, Metadata, Settings, About, and Security Guide.
- Keep page descriptions one sentence. Avoid repeating long explanatory copy in multiple cards.
- Pin notification/update controls to the shell or dashboard instead of repeating an icon in selected shared headers.

### Visual Density

Suggestions:
- Keep the app dense, but reduce the number of bordered boxes visible before the user has data.
- Use borders for tables, selected items, and modals. Use quieter unframed sections for guidance.
- Add a comfortable/compact density setting for tables in App Manager, Startup Manager, Registry Fixer, and Custom Clean.

## Page-by-Page Suggestions

### Dashboard

Current strengths:
- The first screen already has a strong drop zone and useful local activity.
- The right rail gives utility-style signals similar to cleanup products.

Suggestions:
- Make Dashboard the main command center:
  - `Protect files`
  - `Clean space`
  - `Review startup`
  - `Check registry`
- Add a health strip at the top: `Protected`, `Cleanup available`, `Startup review`, `Updates`.
- Keep quick encryption simple: drop files, password, start. Move algorithm and output detail into a small `Change settings` link.
- Let the Custom Clean card run a quick scan, then show one action: `Review cleanup`.
- Add one prominent `Recommended next step` based on the latest state.

### Encrypt Files

Current strengths:
- Good selected-file table, password strength, algorithm details, and destructive-option confirmation.

Suggestions:
- Show only four default sections:
  - Drop files
  - Password
  - Output location
  - Start encryption
- Move `Encryption Options` and `Advanced Controls` behind an `Advanced encryption settings` accordion.
- Add preset chips:
  - `Recommended`
  - `Private archive`
  - `Transfer copy`
  - `Advanced`
- Replace bare output path input with a segmented choice:
  - `Next to source`
  - `Choose folder`
- Keep destructive controls out of the default view until the user chooses a destructive preset or opens advanced settings.

### Decrypt Files

Suggestions:
- Make the page read as `Select locked files -> enter password -> restore`.
- Treat recovery key and keyfile as `Advanced unlock options`.
- Add a visible note that FileLocker auto-detects the algorithm from the payload.
- Put output location in a small restore card: `Restore next to encrypted files` or `Choose folder`.
- If selected files are not `.locked` or supported PNG carriers, show a direct warning in the selection table.

### Secure Delete

Current strengths:
- Good confirmation and method picker.

Suggestions:
- Rename the first decision to `Delete method` and make `Balanced` the default label for DoD.
- Show a device caveat near the method picker: `Best on HDDs. SSDs may retain blocks outside app control.`
- Add a clear `Review selected paths` step before the destructive button.
- Use a full-width danger footer only after the user confirms `I understand`.

### Hash Files

Current strengths:
- Hash output and verify flow are solid.

Suggestions:
- Reframe as a two-step integrity checker:
  - `Generate hash`
  - `Compare trusted hash`
- Put `Save Manifest` under advanced or a secondary menu. It is useful, but not the main path.
- Replace the algorithm dropdown with segmented buttons for `SHA-256` and `SHA-512`, with Base64 only if it is truly part of hash workflows.
- Make the expected-hash field more prominent after a hash is generated.

### Encode Text

Current strengths:
- Simple input and output panes, local conversion, copy buttons.

Suggestions:
- Use a segmented control for `Encode` / `Decode`.
- Use icon buttons for swap, copy, clear, and load example.
- Consider side-by-side input/output panes on wide screens to feel more like a utility tool.
- Add format-specific hints next to the format selector instead of the larger Quick Reference panel.
- Make it clearer that encoding is not encryption with one small inline note.

### Metadata Scrambler

Current strengths:
- The before/after preview model is strong.

Suggestions:
- Rename to `Metadata Privacy` or `Metadata Cleaner` if the feature is mostly inspection and cleanup.
- The disabled `Scramble Metadata` button should explain why it is disabled or be hidden until write support exists.
- Put category selection behind `Advanced categories`; default to recommended categories when preview data loads.
- Make `Preview only` the first-run default.
- Keep the before/after preview, but collapse the selected-file table for small selections.

### Custom Clean

Current strengths:
- This is the closest page to CCleaner-style utility design.

Suggestions:
- First-run page should show only `Run Scan`, not disabled selection/cleaning controls.
- After scan, split results into:
  - `Recommended cleanup`
  - `Review before cleaning`
  - `Advanced`
- Default-select safe categories and clearly state what is kept: cookies, passwords, active files, pending updates.
- Add a result screen after cleaning: `Freed`, `Files removed`, `Skipped`, `Needs review`.
- Use a persistent right details panel only after an item is selected.

### Partition Cleaner

Current issue:
- `Partition Cleaner` sounds like it may modify partitions, but the page wipes free space.

Suggestions:
- Rename to `Free Space Wipe`.
- Replace `Start Wipe` with `Wipe Free Space`.
- Add a plain summary before the button: `Existing files stay. Deleted-file traces are overwritten where Windows allows it.`
- Keep administrator requirement visible near the action button, not only at the top.
- Add estimated duration once a drive is selected, even if approximate.

### Drive Optimizer

Suggestions:
- Make the primary action `Analyze Drive`.
- Show `Optimize / Trim` only after analysis or as a secondary action.
- Label drives as SSD/HDD/removable where possible.
- Add a short explanation of what will happen:
  - SSD: `TRIM`
  - HDD: `Defrag/optimize`
- Add a last-run/result card after completion.

### Registry Fixer

Current strengths:
- Backup creation is called out, which is important.

Suggestions:
- Rename to `Registry Cleanup` for less scary consumer wording.
- Default-select only low-risk, clearly fixable issues.
- Make backup location and restore instructions visible before fixing.
- Group issues by risk and category, not just tabs.
- Add a `Create backup only` or `Open backup folder` action after a fix.
- Avoid showing raw registry keys as the main information for simple users; keep them in Details.

### Startup Manager

Current strengths:
- Startup impact summary is useful.

Suggestions:
- Make the default view `High impact startup apps`.
- Move advanced hooks and broken entries into secondary tabs.
- Add one-click actions:
  - `Disable high impact`
  - `Review unsigned`
  - `Restore disabled`
- Show whether an item is Microsoft-signed, user-installed, or unknown in plain text.
- Keep raw command and registry/source details in the details panel.

### App Manager

Current strengths:
- App icon handling and leftover cleanup are good utility features.

Suggestions:
- First-run page should only show `Scan installed apps`.
- After scan, default sort by size and show:
  - Large apps
  - Recently installed
  - Unknown publisher
  - Store apps
- Keep `Uninstall Selected` disabled until a selected app is visibly highlighted, with a disabled reason.
- Make leftover cleanup a post-selection task: `Scan leftovers for this app`.
- Add a safe explanation that FileLocker launches the vendor uninstaller and does not silently remove apps.

### Settings

Current strengths:
- Tabs and unsaved state are already present.

Suggestions:
- Add short tab descriptions, especially for Privacy and Integration.
- Make the save/reset bar sticky within Settings so users do not lose unsaved changes while scrolling.
- Rename `Full paths in exports` to `Include full file paths in exports`.
- Add a confirmation dialog before enabling Incognito Mode if it clears history.
- Move debug updater test actions behind a `Developer tools` disclosure.
- Remove or merge the Settings `About` tab with the standalone About page.

### About

Suggestions:
- Add update status and `Check for updates` if update checks are enabled.
- Add a `Diagnostics` section for app version, WebView2/runtime status, admin state, and data folder.
- Keep project link, privacy model, and version visible, but avoid repeating details already in Settings.

### Security Guide

Current issue:
- The guide is accurate, but long text blocks make it feel more like documentation than an in-app guide.

Suggestions:
- Convert long articles into short cards:
  - `Do`
  - `Avoid`
  - `When to use`
  - `Go to tool`
- Add direct links to Encrypt, Hash, Metadata, and Secure Delete pages.
- Add a `Beginner` path and an `Advanced` path.
- Keep algorithm details, but do not lead with them for new users.

## Reusable Components Worth Adding

These components would reduce repeated page-specific layout work:

- `ToolPageLayout`: shared title, actions, main column, side rail, footer.
- `SimpleAdvancedSection`: consistent advanced disclosure across security and system-care pages.
- `RiskBadge`: shared safety/risk language and colors.
- `ScanEmptyState`: first-run scan view without disabled action clutter.
- `ReviewTable`: dense table with consistent search, filters, selection, pagination, and details rail.
- `ActionFooter`: sticky or local action area for destructive and final-run actions.
- `DisabledReasonTooltip`: explains why a visible action is unavailable.
- `RecommendedPresetPicker`: simple preset chips for encryption and cleanup workflows.

## Style Adjustments

Suggestions:
- Keep the dark utility look, but add more green success and amber review signals so everything does not read as blue-on-blue.
- Use fewer card borders before a scan or selection exists.
- Use consistent 8px radius for tool panels and tables.
- Keep tables dense, but make row hover/selection stronger.
- Use icons for repeated utility actions: refresh, copy, clear, details, reveal, export.
- Keep button labels short and command-oriented.

## Validation Notes

Validation performed:
- `npm run build` passed after the title-bar CSS change.
- Vite dev server ran at `http://127.0.0.1:5173/`.
- Headless Chrome captured screenshots and DOM output for all 16 routes.
- Each route rendered the expected page heading with no Vite/framework overlay detected in the DOM.

Limitations:
- Playwright was not installed in this checkout, and no dependency install was performed.
- Browser plugin tools were not available in this session.
- The native WinUI title-bar spacing should still be checked in the packaged desktop app because browser screenshots do not include real Windows caption buttons.
