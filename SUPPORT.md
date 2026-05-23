# Support

## Getting Help

Use the GitHub issue tracker for reproducible bugs, release problems, installer problems, and feature requests.

Before opening an issue:

- Check that you are using the latest FileLocker release.
- Search existing issues.
- Try to reproduce the problem with a non-sensitive test file.
- Keep passwords, keyfiles, recovery material, personal paths, and private file contents out of screenshots and logs.

## What To Include

For bug reports, include:

- FileLocker version.
- Windows version and architecture.
- Installer filename or whether you built from source.
- The workflow affected, such as Encrypt Files, Decrypt Files, Hash Files, Secure Delete, Startup Manager, or App Manager.
- Expected behavior.
- Actual behavior.
- Reproduction steps using safe sample data.
- Relevant error text.

For release or installer issues, include:

- Installer filename.
- SHA-256 value if you checked it.
- Whether Windows SmartScreen, antivirus, or administrator elevation blocked the installer.
- Whether FileLocker opens after install.

## Security Issues

Do not report vulnerabilities in public issues. Follow `SECURITY.md`.

## Project Boundaries

FileLocker cannot recover data if passwords, keyfiles, or recovery material are lost. It also cannot guarantee complete secure deletion on every storage device, especially SSDs with wear leveling.
