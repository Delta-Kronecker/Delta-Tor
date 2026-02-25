# Brief

A Windows GUI tool that downloads Tor Expert Bundle, bridge lists, and optionally routes system traffic through Tor via a SOCKS5 proxy.



https://github.com/user-attachments/assets/9189c81a-f4cd-462a-9475-138e1034f9b6


---

# Usage

## Option 1 — Run via EXE (Recommended)
- Download the latest release `.exe`.
- Run the executable directly.
- **No installation or prerequisites required.**

## Option 2 — Run from Source
- Install Python 3.
- Run:
  python main.py

---

# Quick Start

1. Launch the application.
2. Wait for the automatic Tor download and extraction.
3. Click **Start Tor**.
4. Wait until connection reaches **100%**.
5. Enable **Set System Proxy** to route all traffic through Tor.

---

# How It Works

## 1. Tor Bundle Management

* Automatically downloads the Tor Expert Bundle.
* Extracts it into a local working directory.
  
## 2. Bridge Handling

* Downloads and stores bridge lists locally.
* Supports multiple categories and transport types (e.g. `obfs4`, `webtunnel`).
* Injects selected bridges into `torrc`.
*  Use "Update Bridges" at any time to refresh them.


## 3. Configuration (torrc)

* Dynamically generates a `torrc` file with:

  * SOCKS5 proxy (`127.0.0.1:19050`)
  * Control port
  * Optional bridges
  * Pluggable transports

## 4. Tor Process Control

* Runs `tor.exe` as a subprocess.
* Monitors bootstrap progress from logs.
* Updates UI in real time.

## 5. System Proxy Integration

* Optionally sets Windows system proxy:

  ```
  socks=127.0.0.1:19050
  ```
* Automatically resets proxy when Tor stops.

## 6. Connection Testing

* Performs a SOCKS5 handshake through Tor.
* Queries Tor check API to verify:

  * Exit IP
  * Tor status
* Retrieves basic geo information.


# Notes

* Designed for Windows.
* EXE version runs fully standalone (no Python required).
* System proxy changes affect all applications using Windows proxy settings.
* Bridge usage is optional but recommended in restricted networks.


# 🔥 Keep This Project Going!

If you're finding this useful, please show your support:

⭐ **Star the repository on GitHub**

⭐ **Star our [Telegram posts](https://t.me/DeltaKroneckerGithub)** 

Your stars fuel our motivation to keep improving!

