# Security policy

## Supported versions

Security fixes are intended for the most recent published KeyBridge version. Older versions are not guaranteed to receive fixes. Run the same current version on both Windows PCs.

## Report a vulnerability privately

Do **not** open a public issue, discussion, or pull request containing exploit details, pairing tokens, connection codes, or private network information.

Use the repository's **Security → Advisories → Report a vulnerability** feature if private vulnerability reporting is enabled. If it is unavailable, contact the repository owner privately using the contact information on their GitHub profile before disclosing details publicly. Include the affected version, impact, reproduction steps, and a minimal proof of concept if available. Please allow time for investigation and a coordinated fix before public disclosure.

Ordinary bugs that do not expose sensitive information belong in [GitHub Issues](https://github.com/Taxperia/keyboard/issues).

## Security boundaries and limitations

- KeyBridge is intended for **trusted local networks only**. Do not expose its ports to the internet or use router port forwarding.
- Each new pairing and saved-device reconnect requires an approval on the receiving PC. Receiving-side input and clipboard permissions are configurable.
- Paired screen, input, file, and clipboard payloads use AES-GCM authenticated encryption. However, discovery and initial pairing traffic—including the six-digit code and the token used for later encryption—is **not protected from observers on the local network**. Someone who can monitor that exchange may compromise the session. Do not use KeyBridge on public Wi-Fi or an untrusted LAN.
- A pairing token is stored in the local KeyBridge settings. Treat that settings file as a secret; do not share or commit it.
- KeyBridge has no hosted relay, NAT traversal, account-based identity system, unattended access, or formal independent security audit. Do not rely on it as a substitute for a professionally audited remote-access product.
- Windows elevation and secure-desktop restrictions still apply. Do not disable operating-system protections to work around them.

The [README](README.md#network-and-security-model) describes the network ports and user-facing security model.
