# SSH / TCP mock — install, load, and run

PC-side bench tool for [SSH Interface v2](../README.md). It speaks **SSH** or **raw TCP**, always bidirectional: lines you type are sent; incoming bytes print as `[RX]`.

New to this folder? Read `START-HERE.txt`, then follow the steps below.

---

## What you get

| File | Purpose |
|------|---------|
| `mock.py` | The program |
| `requirements.txt` | Python package: Paramiko 3.x |
| `install-dependencies.bat` | One-time `pip install` |
| `start-ssh-server.bat` | **Crestron Client test** — this PC is the SSH server |
| `start-tcp-client.bat` | Raw TCP client — use with SIMPL Windows **TCP/IP Server** |
| `rotate-host-key.bat` | New host fingerprint for Accept_New_Key tests |
| `START-HERE.txt` | One-page cheat sheet |

---

## Before you begin

1. Windows 10 or 11 PC on the **same LAN** as the Crestron processor
2. **Python 3.9 or newer** from [python.org](https://www.python.org/downloads/)
3. Check **Add python.exe to PATH** on the installer
4. Administrator access (once) for a Windows Firewall rule
5. SSH Interface v2 loaded on a **4-Series** processor (see the repo root `README.md`)

Typical bench:

```
┌──────────────────────┐                         ┌──────────────────────┐
│  Windows PC          │                         │  Crestron 4-Series   │
│  this mock           │                         │  RMC4 / CP4 / …      │
│                      │  SSH 2222  (Client test)│                      │
│  start-ssh-server ───┼◄────────────────────────┤  Client module       │
│                      │                         │                      │
│  start-tcp-client ───┼────────────────────────►┤  TCP/IP Server       │
│                      │  TCP (e.g. 5000)        │  (built-in symbol)   │
└──────────────────────┘                         └──────────────────────┘
```

Inbound TCP on the processor is the built-in **TCP/IP Server** symbol, not this SSH library.

---

## Step 1 — Install Python

1. Download Python from https://www.python.org/downloads/
2. Run the installer
3. Enable **Add python.exe to PATH**
4. Close and reopen PowerShell

```powershell
python --version
```

You should see `Python 3.9.x` or newer. If `python` is not recognized, reinstall with PATH enabled.

---

## Step 2 — Get this folder onto the PC

Clone the repo or copy `test-server\` to a local path, for example:

```
C:\CrestronTools\SSH-Interface-v2\test-server\
```

OneDrive paths work; a local folder avoids occasional file locks.

You need at least `mock.py`, `requirements.txt`, and the `.bat` files in that directory.

---

## Step 3 — Install Paramiko (once per PC)

Double-click **`install-dependencies.bat`**, or in PowerShell:

```powershell
cd C:\CrestronTools\SSH-Interface-v2\test-server
python -m pip install --user -r requirements.txt
```

Expected: `paramiko` 3.4 or 3.5 installs without errors.

---

## Step 4 — Windows Firewall (SSH server tests)

The processor must open **inbound TCP 2222** on this PC.

Admin PowerShell:

```powershell
New-NetFirewallRule -DisplayName "SSH Interface mock (2222)" `
  -Direction Inbound -Protocol TCP -LocalPort 2222 -Action Allow
```

---

## Step 5 — Find this PC’s LAN IP

The processor must use the PC’s **LAN address**, not `127.0.0.1`.

```powershell
Get-NetIPAddress -AddressFamily IPv4 | Format-Table IPAddress, InterfaceAlias
```

`start-ssh-server.bat` also prints **This PC LAN IP** on startup.

---

## Step 6 — Test the Crestron SSH **Client** module

The mock **listens**. The processor **connects out**.

1. Double-click **`start-ssh-server.bat`**
2. Confirm it shows `SSH server listening` and a host-key MD5 fingerprint
3. On the Client symbol in SIMPL:

   | Setting | Value |
   |---------|--------|
   | IP_Address$ | PC LAN IP from step 5 |
   | IP_Port | `2222` |
   | Username$ | `crestron` |
   | Password$ | `crestron` |
   | Accept_Any_Key | **Yes** on the first connect |
   | Connect | hold high |

4. Client `Connection_Status_Out` should go to **2** (Connected)
5. Type in the PC window and press Enter → processor `From_Server`
6. Send a string on processor `To_Server` → PC prints `[RX] …`

**Accept_New_Key:** run `rotate-host-key.bat`, start the SSH server again, hold Connect with Accept_Any_Key = No. You should get status **4** and `New_Key_Text$`. Pulse **Accept_New_Key** to store it.

Default user/password `crestron` / `crestron` are lab-only. Pass `--user` / `--password` if you start `mock.py` from a command line.

---

## Step 7 — Test inbound TCP (built-in SIMPL symbol)

The processor **listens** with SIMPL Windows **TCP/IP Server**. This PC **connects in** over raw TCP.

1. Add a TCP/IP Server symbol, pick a port (e.g. `5000`)
2. Double-click **`start-tcp-client.bat`**
3. Enter the processor IP and that port
4. Type here to send; incoming bytes print as `[RX]`

---

## Command line

```text
python mock.py ssh-server --port 2222 --user crestron --password crestron
python mock.py tcp-client --host 192.168.1.10 --port 5000
python mock.py ssh-server --rotate-key
```

`--eol cr|lf|crlf|none` controls what is appended when you press Enter (default **cr**).  
`--echo` loops received bytes back to the peer.

---

## Troubleshooting

| Symptom | Check |
|---------|--------|
| `python` not recognized | Reinstall Python; enable Add to PATH; new PowerShell window |
| Processor never connects to the mock | PC LAN IP (not 127.0.0.1); firewall TCP 2222; same subnet |
| Status 4 / fingerprint notice | Accept_Any_Key = Yes, or pulse Accept_New_Key |
| Auth failure | Username/password `crestron` / `crestron` unless you overrode them |
| TCP client cannot connect | Built-in TCP/IP Server enabled; port matches; processor IP |
| Error 1700 compiling `.usp` | File is LF-only; save as Windows CRLF |
| 3-Series compile of `.usp` | This `.clz` is 4-Series only |

Ctrl+C stops the mock. The SSH mock listens again after the peer drops; the Crestron Client module does **not** auto-reconnect yet (toggle Connect). See `_notes.txt`.

---

## Security

Lab use. The SSH server accepts a known password and has no account lockout. Do not expose port 2222 to an untrusted network. `host_key` is generated on first run and should stay on the bench PC (it is gitignored).
