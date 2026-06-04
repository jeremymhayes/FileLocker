# Security Policy

## Reporting a Vulnerability

Please do not open a public issue for a suspected security vulnerability.

Use GitHub's private vulnerability reporting for this repository when available. If private reporting is not available, open a minimal public issue that says you need a private security contact, but do not include exploit details, sensitive file paths, secrets, payloads, or proof-of-concept material in the public issue.

Useful details to include privately:

- FileLocker version and setup installer filename.
- Windows version and architecture.
- Whether the app was installed normally or run from a local build.
- A concise description of the affected workflow.
- Impact assessment, such as disclosure, data loss, privilege boundary concerns, or denial of service.
- Reproduction steps that avoid real personal data.
- Logs, screenshots, or sample files only if they do not contain sensitive data.

## Scope

In scope:

- Encryption, decryption, keyfile, recovery-key, and payload handling issues.
- Secure delete or cleanup behavior that can delete outside approved targets.
- Update, setup installer, checksum, or release-validation weaknesses.
- Sensitive data exposure in logs, history, bridge payloads, exports, errors, or UI state.
- Startup, app-management, registry, and system-maintenance actions that bypass confirmation or administrator checks.

Out of scope:

- Social engineering.
- Vulnerabilities requiring malware or administrator-level compromise before FileLocker runs.
- Reports based only on obsolete releases.
- Generic dependency reports without a FileLocker impact path.
- Rate-limit or denial-of-service findings against GitHub-hosted project pages.

## Handling Expectations

- Acknowledgement target: within 7 days.
- Initial triage target: within 14 days.
- Fix target: based on severity and release risk.

Coordinated disclosure is expected. Please give maintainers reasonable time to validate, patch, and publish an updated release before public disclosure.

## Security Model Notes

FileLocker is local-first. It does not provide cloud recovery, remote key escrow, or a password reset path. If a password, keyfile, or recovery material is lost, protected data should be treated as inaccessible.

FileLocker performs destructive operations such as secure delete and cleanup only after user action. Some System Care operations require administrator mode because Windows protects the target locations.
If you discover a security vulnerability in FileLocker, please report it responsibly.

Contact:
jeremy@jeremymhayes.com
