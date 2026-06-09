# Partition Cleaner UI Revamp Design

Date: 2026-06-09
Project: FileLocker
Surface: `frontend/src/pages/SystemMaintenancePages.tsx` / `PartitionCleanerPage`

## Summary

Revamp Partition Cleaner as a focused free-space wipe tool. The page remains backed by the existing `maintenance.getDrives` and `maintenance.wipeFreeSpace` actions, but the user-facing flow becomes clearer, denser, and more professional.

The approved direction is a guided flow plus drive inspector:

1. Select drive.
2. Review impact.
3. Wipe free space.
4. Report result.

The visible tool framing should emphasize the actual operation: overwriting unused space so already-deleted files are harder to recover. The page remains under the existing Partition Cleaner route/sidebar entry, while the in-page tool title is `Free-Space Sanitizer`.

## Goals

- Make the page feel like a serious Windows utility: compact, restrained, clear, and fast to scan.
- Preserve the existing free-space wipe behavior. Do not turn Partition Cleaner into Custom Clean.
- Replace the misleading destructive framing with a truthful risk model:
  - Existing files: untouched.
  - Cost: long-running, heavy writes.
- Keep the workflow simple: select drive, review impact, confirm, run, report.
- Make admin state, disabled drives, running state, errors, and completed output clear.
- Avoid fake functionality. Any media-type, progress, or cancel UI must be backed by real bridge data or implemented as a directly related bridge enhancement.

## Non-Goals

- No full app redesign.
- No changes to Custom Clean, Drive Optimizer, Registry Fixer, Startup Manager, or App Manager except shared helper reuse if already local to `SystemMaintenancePages.tsx`.
- No fake scan results, fake cleanup targets, or placeholder actions.
- No route or major system rename.
- No broad visual theme changes.

## Current Context

`PartitionCleanerPage` currently loads drives, lets the user choose a ready drive, requires administrator mode, confirms the operation, calls `maintenance.wipeFreeSpace`, and displays command output through `ToolOutput`.

The current bridge model for a drive includes:

- `id`
- `name`
- `rootPath`
- `driveType`
- `driveFormat`
- `totalSizeBytes`
- `totalSizeDisplay`
- `freeSpaceBytes`
- `freeSpaceDisplay`
- `isReady`

It does not currently expose SSD/HDD media type, per-pass progress, or a user-triggered cancel action. `SystemMaintenanceService.WipeFreeSpaceAsync` runs `cipher.exe /w:<root>` as one awaited process with a timeout.

## Proposed UI

### Header

Use a compact page-local header inside the existing app shell:

- Title: `Free-Space Sanitizer`.
- Subtitle: `Overwrite unused space so already-deleted files on the selected drive are harder to recover.`
- Admin state area:
  - If elevated: `Running as administrator`.
  - If not elevated: `Restart as administrator` action.
- Secondary action: refresh drives.

The header should not look like a marketing hero. It is a utility command area.

### Guided Status

Show the four-step flow as a compact status indicator, but keep it honest:

- Initial loaded state with no selected drive: step 1, `Select drive`.
- Initial loaded state with a selected drive: step 2, `Review impact`.
- Ready-to-run state: step 2, `Review impact`.
- Running state: step 3, `Wipe free space`.
- Completed state: step 4, `Report`.

The stepper must track real page state. Do not leave it decorative or contradictory.

### Drive List

Replace the current card-like drive picker with a compact drive table/list:

- Drive name/root.
- Free space.
- Format.
- Drive type.
- Media type, using `Unknown` when the host cannot detect it.
- Status.

Rows should be compact with thin borders and a restrained selected-row state. Disabled rows should remain readable and include a reason. Example: `RAW or unformatted volumes are not supported by Windows cipher.`

Do not display guessed SSD/HDD labels. Use `Unknown` when the host cannot detect media type.

### Review Panel

The right-side review panel should be the core inspector. It should add value beyond the row:

- Selected drive root/name.
- Existing files: `Untouched`.
- Cost: `Long-running, heavy writes`.
- Free space.
- Estimated write volume: `freeSpaceBytes * 3`, because cipher performs three overwrite passes.
- Estimated time range from selected drive free-space bytes and media type.
- Operation: Windows cipher free-space wipe.
- Media-specific recommendation based on the resolved `mediaType`:
  - HDD: good fit; free-space overwrite works as intended on traditional hard drives.
  - SSD: warn or discourage; TRIM and wear leveling limit the usefulness of free-space overwrite and add write wear.
  - Removable: limited-benefit guidance because USB flash media can be wear-leveled like SSD storage.
  - Mixed: neutral caution that the volume maps to multiple physical media types.
  - Unknown: neutral copy explaining that FileLocker could not confidently identify the underlying media.

The warning copy should be direct:

`This does not erase current files. Windows cipher writes temporary data into free space, then removes it. Avoid running it while the drive is busy or nearly full.`

### Actions

Before running:

- Primary: `Wipe Free Space`.
- Secondary: `Refresh Drives`.
- The wipe button is disabled unless the app is elevated and the selected drive is ready.
- Confirmation is required before starting because the operation is long-running and fills free space with temporary data.

While running:

- Drive selection and refresh are disabled.
- `Wipe Free Space` must be disabled.
- `Cancel Wipe` becomes the primary running action.
- Cancelling requires confirmation that the wipe will be incomplete.
- The cancel result must report that the operation was interrupted and whether cleanup of temporary cipher files succeeded, failed, or was not needed.

After running:

- Show result status, elapsed time, drive root, and command output.
- Allow refresh and another run after completion.

### Output and Report

Avoid a redundant `View Command Output` button next to an always-visible output panel.

The command output panel is visible during and after a run. It is hidden before the first run. Use `Hide` only once output exists.

The completion report should be concrete:

- Completed: `Deleted-file traces in free space on D:\ were overwritten.`
- Failed: show the service message and command output.
- Timed out: show timeout status and note that the operation did not complete.
- Cancelled: show `Wipe incomplete`, interrupted pass when known, elapsed time, and cleanup attempt result.

## Backend-Adjacent Requirements

These are directly related to the page and are in scope for the revamp because the approved UI depends on them. They must be implemented honestly.

### Media Type

Add media information to `MaintenanceDrive`:

- `mediaType`: `SSD`, `HDD`, `Removable`, `Mixed`, or `Unknown`.
- `mediaDetectionStatus`: `Detected`, `Mixed`, `Unsupported`, `TimedOut`, or `Unknown`.
- `mediaDescription` for the user-facing explanation when the type is not a straightforward HDD.

Media type is a property of the physical disk, while the UI selects a volume such as `D:\`. The host must resolve volume to partition to physical disk before labeling a volume as HDD or SSD. Use the Storage WMI namespace, with `MSFT_PhysicalDisk.MediaType` as the preferred source (`3` = HDD, `4` = SSD). Do not use `Win32_DiskDrive.MediaType` as the deciding source because it commonly reports generic text such as `Fixed hard disk` for both HDDs and SSDs.

Return `Unknown` when the mapping cannot be resolved, when the WMI query fails, when the query times out, or when the volume maps to zero physical disks. Return `Mixed` when the selected volume maps to multiple physical disks with conflicting media types, such as Storage Spaces, striped/spanned volumes, virtual disks, or other composite storage. The frontend must never guess SSD/HDD labels from `DriveType`, `driveFormat`, or volume name.

Drive enumeration must not hang on media detection. Run media lookup with a short timeout and return drives with `Unknown` media if detection is slow.

`Status` in the drive table is a frontend composite, not a raw backend field. It is derived from readiness, format support, admin state, running state, and media guidance. Examples: `Ready`, `Running`, `Unsupported`, `Limited`, `Unknown media`.

### Estimate

The frontend can compute a rough estimate from existing free-space data:

- write volume: `freeSpaceBytes * 3`.
- time range: conservative assumptions based on media type when known.

For HDDs, a broad estimate such as `~2-5+ hours` for 412 GB free is acceptable. Keep ranges intentionally wide because `cipher.exe` can be much slower than raw sequential throughput due to temporary file creation and disk-full behavior, especially on old, busy, or nearly full drives. If media type is unknown, use less precise copy and label the estimate as rough.

### Progress and Cancel

Current `maintenance.wipeFreeSpace` returns after the process exits, so the revamp needs a directly related operation enhancement for `cipher.exe`. This is more than a cosmetic UI change: the main process must own the running operation, stream status to the renderer, and allow the renderer to request cancellation.

The implementation should support:

- one active free-space wipe at a time,
- an operation id that the page can reattach to after navigation or remount,
- running-state updates while the process is active,
- coarse pass status derived from real command output where possible,
- a fallback indeterminate running state if pass parsing is unavailable,
- user-triggered cancellation by stopping the running process tree,
- a cancelled report that clearly says the wipe is incomplete.

Running state cannot live only in React component state. It must be held by the host or a bridge-level store so users can leave Partition Cleaner and return while a multi-hour wipe is still running.

Progress events should use the existing bridge event mechanism or a similarly explicit event channel. The design expects invoke-for-start plus event-for-status, not a single invoke response that blocks all UI updates until the process exits.

Pass parsing is best effort. `cipher.exe` output strings such as the 0x00, 0xFF, and random-number passes can be localized, so the parser must fall back to an indeterminate running state when output does not match tested patterns. The progress UI must not claim byte-level precision unless the backend can support it. A three-pass indicator is acceptable only when it reflects actual parsed cipher output.

Cancellation uses process termination of the running `cipher.exe` process tree. After cancellation, FileLocker attempts best-effort cleanup of cipher-created temporary fill files and reports `cleanupSucceeded`, `cleanupFailed`, `notNeeded`, or `unknown`. The cleanup locator must not assume a single hardcoded folder name; it should identify likely cipher temporary artifacts conservatively and report residual files honestly when they remain.

## Components and Code Shape

Keep changes focused in `frontend/src/pages/SystemMaintenancePages.tsx`.

Recommended local helpers:

- `PartitionCleanerHeader`
- `PartitionCleanerSteps`
- `PartitionDriveTable`
- `PartitionReviewPanel`
- `PartitionRunStatus`
- small helpers for risk copy, estimate text, and drive status

Extract helpers only if they reduce the size and complexity of `PartitionCleanerPage`. Do not create a broad design system abstraction for this page.

Reuse existing local utilities where useful:

- `MaintenanceFrame`
- `AdminStatusBanner` or equivalent admin action behavior
- `MaintenanceConfirmDialog`
- `MetricTile` patterns if the final UI stays compact
- `ActionWithReason`
- `ToolOutput` only if it fits the new report/output structure

## Error and Empty States

- Loading drives: compact loading row or inline status.
- No drives: clear empty state with refresh action.
- Drive load failed: inline error with retry.
- Not administrator: action area explains that elevation is required and offers restart. Copy must state that restarting as administrator relaunches FileLocker and any in-progress non-elevated UI state is not preserved.
- Selected drive unavailable: keep row visible, disabled, and explain why.
- Wipe failed: report area shows service message and output.
- Wipe timed out: report area says the operation timed out and did not complete.

## Visual Direction

- Flat surfaces, thin borders, compact spacing.
- Restrained accent colors:
  - green for existing files untouched / ready,
  - amber for long-running heavy writes,
  - teal/blue for informational media guidance,
  - red only for actual errors or unavailable destructive consequences.
- No giant cards, heavy gradients, glow, cyberpunk, glass, or generic dashboard styling.
- Text must remain readable at smaller window sizes.
- Use icons sparingly and only where they improve scanning speed.

## Verification

After implementation:

1. Run `npm run build` in `frontend`.
2. Run a .NET build from the app project. If the normal output path is blocked by the dirty worktree or existing build artifacts, use a separate output path such as `dotnet build .\FileLocker.csproj -p:BaseOutputPath=bin\codex-verify\`.
3. Open the app or Vite surface and verify Partition Cleaner:
   - initial/drive loading state,
   - administrator state,
   - drive selection,
   - unavailable drive state if present in mock/dev data,
   - confirmation dialog,
   - running state,
   - completed output,
   - error state where practical.
4. Add or update test fixtures/mocks for:
   - HDD media guidance,
   - SSD media guidance,
   - Removable media guidance,
   - Mixed and Unknown media fallback,
   - unsupported RAW/unformatted drive,
   - running state,
   - cancel confirmation,
   - cancelled report with cleanup status.
5. Confirm no unrelated pages were intentionally changed.

## Implementation Decisions

- Add media type to the drive payload through conservative volume-to-physical-disk detection, with `Mixed` and `Unknown` as safe fallbacks.
- Implement host-owned running/cancel/report behavior for the free-space wipe rather than rendering fake progress controls.
- Keep the route/sidebar label as `Partition Cleaner`; use `Free-Space Sanitizer` as the page-local tool title.
