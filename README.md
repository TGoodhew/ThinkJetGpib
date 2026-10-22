# ThinkJetGpib

**A Windows printer driver for the HP 2225 ThinkJet on GPIB.** The ThinkJet shows up as an
ordinary Windows printer, so any app can print to it: Notepad, Word, a browser, a screen capture.
Jobs go to the printer over the GPIB bus through NI-VISA.

It installs from a single exe, which also uninstalls cleanly from *Settings → Apps*.

```
App ──► Windows spooler ──► "HP 2225 ThinkJet (GPIB)" driver ──► RAW port 127.0.0.1:9100
                            (Unidrv + TJ2225.gpd, 192×96 dpi raster)      │
                                                                          ▼
                       HP 2225 ◄── GPIB0::5::INSTR ◄── NI-VISA ◄── ThinkJet GPIB Print Bridge (service)
```

## Requirements

- 64-bit (x64) Windows 10 or 11
- NI-VISA and NI-488.2, plus a GPIB interface NI-VISA can reach. Developed with an NI
  GPIB-ENET/1000.
- An HP 2225 ThinkJet with the HP-IB interface, at a known primary address (the default here is 5)

No .NET runtime is needed. The installer is self-contained.

## Install

1. Download `ThinkJetGpibSetup.exe` from [Releases](../../releases), or [build it](#build).
2. Run it and accept the UAC prompt.
3. Enter the printer's GPIB address. Choose whether to print a Windows test page at the end.

Setup then:

- **Builds and signs the driver catalog on your PC.** It uses the WDK's Inf2Cat if that's
  installed, otherwise `New-FileCatalog`. It signs with a certificate it creates on the spot.
  Only the public certificate is trusted (LocalMachine *Root* and *TrustedPublisher*). The private
  key is deleted right after signing, so the certificate can't sign anything else.
- **Installs the driver**, a Standard TCP/IP RAW port to `127.0.0.1:9100`, and a printer named
  **HP 2225 ThinkJet**.
- **Installs and starts the *ThinkJet GPIB Print Bridge* service.** It starts automatically, runs
  as LocalSystem, and restarts itself on failure.
- **Registers an entry in Settings → Apps → Installed apps.**

Running setup again upgrades in place and keeps your settings.

Unattended install:

```
ThinkJetGpibSetup.exe --address 5 --no-test-page --quiet
```

## Uninstall

Use **Settings → Apps → ThinkJet GPIB Printer (HP 2225) → Uninstall**, or run:

```
"C:\Program Files (x86)\ThinkJetGpib\ThinkJetGpib.exe" --uninstall
```

This removes the service, the printer, the port, the driver and every copy of its package in the
driver store, the signing certificate, the settings, and the install folder.

## How it works

### The driver (`driver/`)

`TJ2225.gpd` is a **Unidrv minidriver**, a text description of the printer for Windows' built-in
universal printer driver. It's raster-only. Unidrv renders every page, text included, to a 1-bit
bitmap the width of the ThinkJet's 6.67 in print line, then sends it with the ThinkJet's raster
commands. There are two resolutions (*Printing Preferences → Advanced → Resolution*):

| Resolution | Dots per line | Notes |
| --- | --- | --- |
| **192 × 96 dpi** (default) | 1280 | The ThinkJet's high-density mode. Its 0.007 in dots overlap across the line, so strokes print solid and small text keeps twice the detail. |
| 96 × 96 dpi | 640 | Low density. The dots are smaller than the 1/96 in pitch, so even solid black prints as separate dots. |

| Bytes | Meaning |
| --- | --- |
| `ESC * r 1280 S` / `ESC * r 640 S` | select high or low density (sent before every `ESC * r A`) |
| `ESC * r A` | start raster graphics |
| `ESC * b <n> W <n bytes>` | one dot row, MSB = leftmost dot; the paper advances one row |
| `ESC * r B` | end raster graphics |
| `FF` | eject the page |

Every row is sent, blank rows included. The driver never has to move the print head or the paper,
which the ThinkJet's raster mode has no command for. `TJ2225.inf` is a package-aware INF that
depends on the inbox Unidrv core driver.

### The bridge (`src/ThinkJetGpib/Bridge/`)

A small Windows service listens on **loopback only** (`127.0.0.1:9100`). It forwards each job to
the printer through the NI-VISA C API (`visa32.dll`):

- **Chunks.** It writes 1 KB at a time, with EOI asserted only on the job's last byte.
- **Printer not ready.** If the printer is out of paper or offline, or the GPIB interface is
  unreachable, the job is **held, not failed**. The bridge retries every 3 s from the exact byte
  it reached, so the page continues cleanly once the printer is ready.
- **Cancelled jobs.** Cancelling the job in Windows makes the bridge drop it and send the printer
  a Device Clear.

The process is 32-bit, to match the 32-bit NI-VISA .NET stack this was developed alongside.
Calling the C API directly means there are no VISA.NET assembly versions to track.

### The installer (`src/ThinkJetGpib/Setup/`)

The bridge and the installer are the same single-file .NET exe. The driver files and the
PowerShell scripts that do the Windows configuration (`Scripts/install.ps1`, `uninstall.ps1`) are
embedded in it as resources.

## Settings and logs

| File | Purpose |
| --- | --- |
| `%ProgramData%\ThinkJetGpib\bridge.ini` | VISA resource, TCP port, I/O timeout, chunk size. Restart the service after editing. |
| `%ProgramData%\ThinkJetGpib\bridge.log` | One line per job, plus waits and errors. Rolls over at 1 MB. |

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Job stays "Printing" and nothing comes out | `bridge.log`. The printer is probably off, offline or out of paper, or the GPIB interface is unreachable. The job resumes when the printer is ready. |
| Setup fails at `pnputil` or `Add-PrinterDriver` | Install the Windows Driver Kit so setup uses Inf2Cat, then run setup again. *Windows protected print mode* blocks all third-party drivers. Setup reports it if it's on. |
| A4 pages drift down the paper | The 2225's form feed uses its own form length (11 in by default). Set the printer's form length to A4, or print on Letter. |
| Compare the two densities on paper | Stop the service, then run `ThinkJetGpib.exe --density-test`. It prints solid, 1-on/1-off, 2-on/2-off and single-dot bars at 96 × 96 and at 192 × 96 dpi. If the 192 dpi bars come out wrong, choose 96 × 96 dpi under *Printing Preferences → Advanced*. |
| Wide documents are cut off at the sides | The print line is only 6.67 in. Print with *Fit to printable area* / *Shrink oversized pages*. |
| Test the bus without Windows printing | Stop the service, then run `ThinkJetGpib.exe --print-file job.prn` (raw bytes straight to the printer) or `ThinkJetGpib.exe --console` (the bridge in a window, logging to the console). |
| See exactly what Windows sends | Temporarily switch the printer to the `FILE:` port and inspect the bytes. |

## Build

You need the .NET 10 SDK. Building on Windows needs nothing else. On Linux or macOS the project
sets `EnableWindowsTargeting`.

```powershell
dotnet test    tests\ThinkJetGpib.Tests
dotnet publish src\ThinkJetGpib -c Release -o dist
```

The output is a single `dist\ThinkJetGpibSetup.exe` of about 10 MB: self-contained, trimmed and
compressed.

## Project layout

```
driver/               TJ2225.gpd (Unidrv minidriver), TJ2225.inf
src/ThinkJetGpib/
  Bridge/             VISA P/Invoke, job pump, TCP server, Windows service, config, log
  Setup/              installer / uninstaller
  Scripts/            install.ps1, uninstall.ps1 (embedded in the exe)
  Program.cs          command-line modes
tests/                unit tests for the job pump and settings (run anywhere, no hardware)
```

## Notes

- Microsoft's direction for new printers is the IPP class driver plus Print Support Apps. A
  GPD/Unidrv driver like this one still installs and works locally. It just isn't distributed
  through Windows Update.
- There are no device fonts. Everything prints as raster graphics, which is slower than the
  ThinkJet's native text mode but works with any app and any font.

## License

[MIT](LICENSE) © 2026 Tony Goodhew
