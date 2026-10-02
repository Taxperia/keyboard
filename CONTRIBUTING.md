# Contributing to KeyBridge

Thank you for helping improve KeyBridge. Bug fixes, accessibility improvements, documentation, and LAN reliability work are especially welcome.

Before contributing, please read the [README](README.md), [security policy](SECURITY.md), [code of conduct](CODE_OF_CONDUCT.md), and [noncommercial license](LICENSE). KeyBridge is source-available, **not** OSI-licensed open source. If your contribution requires commercial-use rights, contact the copyright holder before contributing.

## Report an issue

- Search existing issues first.
- For a reproducible bug, include KeyBridge versions on **both** PCs, Windows versions, network type, display resolution/scaling, exact steps, expected result, and actual result.
- If the problem concerns pairing, say whether discovery, approval, screen viewing, keyboard, and mouse each worked. Include the precise status text from both PCs.
- Remove connection codes, pairing tokens, IP addresses you do not wish to publish, and other private data from screenshots or logs.
- **Never report a vulnerability in a public issue.** Use [SECURITY.md](SECURITY.md).

## Submit a change

1. Open an issue for a substantial behavior change so the approach can be discussed before implementation.
2. Fork the repository and create a focused branch.
3. Make the smallest change that solves the problem. Keep the LAN-only and explicit-consent model intact.
4. Run `dotnet build .\KeyBridge.sln -c Release`. If you change the connection or input path, test with two Windows PCs when possible.
5. Update the README or [CHANGELOG.md](CHANGELOG.md) when user-visible behavior changes.
6. Open a pull request describing the change, how it was tested, and any remaining limitations. Add before/after screenshots for UI work, after removing sensitive data.

## Engineering notes

- Preserve nullable reference type checking and avoid new compiler warnings.
- Validate device identity and pairing credentials for network messages. Do not weaken approval, permission, or encryption checks to make a connection succeed.
- Keep blocking network work off the WPF UI thread. Dispatch UI updates to the WPF dispatcher.
- Preserve compatibility with existing user settings where practical.
- Keep generated files and published binaries out of the source tree. Use UTF-8 for text files.

By submitting a contribution, you agree that your contribution may be distributed with KeyBridge under the repository's [PolyForm Noncommercial License 1.0.0](LICENSE). This does not grant commercial-use permission.
