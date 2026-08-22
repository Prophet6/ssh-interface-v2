# SSH Interface v2

Crestron SIMPL+ / Simpl# **SSH client** for **4-Series** processors (RMC4, CP4, CP4N, VC-4, and similar).

| Module | File | Role |
|--------|------|------|
| **Client** | `SSH Interface Client v2.0.usp` | SSH client — processor connects out to a device |

Crestron Simpl# exposes `SshClient` only. For inbound TCP on the processor, use the built-in SIMPL Windows **TCP/IP Server** symbol (not this library).

A PC-side mock in [`test-server/`](test-server/) can stand in as an SSH server for bench tests.

This project is not affiliated with Crestron Electronics.

---

## Repository layout

```
SSH Interface Client v2.0.usp / .ush
SSH_Interface_v2.clz              compiled Simpl# library (required)
SSH Interface/                    Simpl# source (Visual Studio 2022)
test-server/                      Python mock (see that folder’s README)
SSH Testing.smw                   sample SIMPL Windows program (optional)
_archive/                         original v1.7 processor (reference only)
```

---

## Requirements

- 4-Series Crestron processor (or VC-4)
- SIMPL Windows + SIMPL+
- PC on the **same LAN** as the processor
- For the mock tester: Windows 10/11, **Python 3.9+** ([python.org](https://www.python.org/downloads/) — check **Add python.exe to PATH**)

Rebuilding the `.clz` also needs Visual Studio 2022 and NuGet package `Crestron.SimplSharp.SDK.Library` 2.21.274.

---

## Load the Crestron module

1. Clone or download this repository to a folder SIMPL Windows can see, for example:

   ```
   C:\Crestron\UserSPlus\SSH-Interface-v2\
   ```

2. Keep `SSH_Interface_v2.clz` **in the same folder** as the `.usp` file.

3. Compile the SIMPL+ module for **4-Series only**:

   ```powershell
   & "C:\Program Files (x86)\Crestron\Simpl\SPlusCC.exe" `
     \rebuild "C:\Crestron\UserSPlus\SSH-Interface-v2\SSH Interface Client v2.0.usp" `
     \target series4
   ```

   You can also open the `.usp` in the SIMPL+ editor and compile for 4-Series.

   A 3-Series target will fail (`archive does not contain a valid SIMPL# assembly`). That is expected for this NuGet SDK library.

4. In SIMPL Windows, add **SSH Interface Client v2.0**.

5. Compile the SIMPL program and load it to the processor (Toolbox / SIMPL Windows).

`.usp` files must be saved with **Windows (CRLF)** line endings or SIMPL+ reports Error 1700.

---

## Run the PC mock tester

Full walkthrough: [`test-server/README.md`](test-server/README.md).

1. Install Python 3.9+ with **Add to PATH**.
2. Double-click `test-server\install-dependencies.bat` (once).
3. Allow inbound **TCP 2222** on the PC firewall if the processor cannot connect.

### Test the Crestron **Client** (processor → this PC)

```
PC (start-ssh-server.bat, port 2222)  <--- SSH ---  Crestron Client module
```

1. Double-click `test-server\start-ssh-server.bat`.
2. Note the **LAN IP** it prints (not `127.0.0.1`).
3. On the Client symbol:

   | Parameter / join | Value |
   |------------------|--------|
   | IP_Address | PC LAN IP |
   | IP_Port | `2222` |
   | Username / Password | `crestron` / `crestron` |
   | Accept_Any_Key | Yes (first connect) |
   | Connect | hold high |

4. Type in the PC window to send to the processor (`From_Server`). Data you send on `To_Server` appears on the PC as `[RX]`.

If the mock is stopped, toggle **Connect** low then high to connect again (auto-retry after a killed server is not reliable yet).

### Inbound TCP on the processor

Use SIMPL Windows **TCP/IP Server**. Point `test-server\start-tcp-client.bat` at the processor IP and that symbol’s port (often `5000`).

---

## Client module notes

- Hold **Connect** to connect; release to disconnect.
- Runtime overrides (`IP_Address_Override`, `IP_Port_Override`, `Username_Override`, `Password_Override$`, `Accept_Any_Key_Override`) replace the parameter when they have a value.
- Host keys are stored as `\NVRAM\SSH_Interface_v2_<symbol instance name>.key`.
- `rotate-host-key.bat` then restart the mock SSH server to test **Accept_New_Key**.

Default mock credentials **`crestron` / `crestron`** are for a lab VLAN only.

---

## Rebuild the Simpl# library (optional)

```powershell
cd "SSH Interface"
nuget restore
msbuild "SSH Interface.sln" /p:Configuration=Debug
copy "SSH Interface\bin\Debug\SSH_Interface_v2.clz" ..
```

Then recompile the `.usp` file (step 3 above).

---

## License / disclaimer

Use at your own risk on a lab network. Not a production SSH gateway. Not affiliated with Crestron Electronics, Inc.
