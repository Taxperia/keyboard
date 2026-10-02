# KeyBridge 0.2.5

KeyBridge is a Windows desktop companion for **two PCs on the same trusted local network**. It is not an AnyDesk or TeamViewer replacement: there is no internet relay, cross-network connection, or unattended access. The receiving PC must approve each session.

## Highlights

- Reconnect to a saved device from **Recent devices** without entering its code again, while still requiring approval on the receiving PC.
- View the other PC's primary display, use the full-screen viewer, and optionally forward keyboard and mouse input.
- Improved connection reliability after approval, including the code-based pairing response and incoming-session status.
- Smoother input forwarding through a non-blocking, ordered TCP control session and improved screen-frame decoding.
- Better mouse positioning across different display resolutions and DPI scales.
- Clearer distinction between **Disconnect** (keep the pairing) and **Forget device** (remove it).
- Refreshed dashboard and settings, scalable icons, and improved layouts for different window sizes.
- A Private-network firewall setup script for the portable build.

## Downloads

- **`KeyBridge-0.2.5-win-x64.exe`**: self-contained portable app; no separate .NET installation is required.
- **`KeyBridge-0.2.5-win-x64.zip`**: the same portable app plus the license, notices, README, and firewall setup script. Extract the ZIP before running the app.
- **`SHA256SUMS.txt`**: SHA-256 hashes for checking the downloaded files.

Run the **same version on both PCs**, on the same trusted LAN. Windows 10 version 1809 or later, or Windows 11, is required. Allow KeyBridge through Windows Firewall on the **Private** network profile. Do not expose its ports to the public internet.

The binaries are not code-signed; Windows may show a SmartScreen warning. Verify that the download came from this repository's release page and compare its SHA-256 hash before running it.

For setup, limitations, and the security model, see the [README](README.md) and [security policy](SECURITY.md). KeyBridge is distributed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
