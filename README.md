# dictation-shim

> Wispr-Flow-style **`Ctrl+Win` push-to-talk** for
> [Whispering](https://github.com/EpicenterHQ/epicenter) on Windows, plus
> full-format clipboard preserve and audio auto-mute.

This is a **companion app** for Whispering, not a standalone dictation
tool. It doesn't record, transcribe, or paste anything itself — Whispering
does all of that. The shim only catches a chord that Whispering can't
register on its own (`Ctrl+Win`, a bare modifier-only combo) and forwards
it as a normal hotkey, plus a few polish features around the edges.

```
keyboard ──Ctrl+Win held──▶ dictation-shim (LL hook + keybd_event)
                              │
                              └──Ctrl+Shift+Alt+Space held──▶ Whispering (Tauri, push-to-talk)
                                                                │
                                                                ▼
                                                          transcribed text at cursor
```

---

## Requirements

- **Windows 10 2004+ or Windows 11** (developed/tested on Win11 26200).
- **[Whispering](https://github.com/EpicenterHQ/epicenter/releases)
  installed and configured** with a working transcription backend
  ([Speaches](https://github.com/speaches-ai/speaches) for local, or
  Groq/OpenAI for cloud — Whispering's docs cover both).
- *(only when building from source)* .NET 9 SDK or newer.

## Features

- **Ctrl+Win push-to-talk** — hold the chord, dictate, release. Works in any
  press order, both left- and right-side modifiers.
- **Robust Win-key handling** — `Win+E`, `Win+R`, etc. still work normally.
  Start menu doesn't pop up by accident, even when the chord starts with
  `Win` first. ~30 ms resolve window keeps things deterministic.
- **Full-format clipboard preserve** — Whispering's built-in restore only
  saves plain text. The shim snapshots the **entire** clipboard
  (rich text, HTML, images, file lists, custom app formats) at chord-start
  and restores it after Whispering's paste-flow finishes. Dictate while
  copy-pasting and your clipboard stays intact.
- **Audio auto-mute** — mutes the default render endpoint while recording so
  background music/video doesn't bleed into the mic. 250 ms delay so
  Whispering's start-recording beep stays audible. Brief taps don't mute.
  If you had already muted manually, the shim leaves it alone.
- **Tray menu**: *Reset stuck keys* (defensive cleanup), *Restart*, *Quit*.
- **Single small `.exe`** (~48 MB self-contained, no .NET runtime install
  needed). No Windows service, no admin rights.

## Why a separate shim, not a Whispering fork?

The bare-modifier chord problem is fundamental: Win32's `RegisterHotKey`
(used by Tauri's global-shortcut plugin, which Whispering relies on) refuses
shortcuts without a non-modifier key. The only ways around it are a kernel
driver or a low-level keyboard hook in a separate process — which is
exactly what this shim is. Forking Whispering to add an LL hook would be
~10× the maintenance for the same result.

Two of the shim's other features (clipboard-all-formats, audio auto-mute)
could in principle land in Whispering proper as PRs — see
[Contributing](#contributing).

## Install

Pre-built binaries are not yet published. Build from source — see below.

## Build from source

Requires .NET 9 SDK (or newer) on Windows.

```powershell
git clone <this-repo-url>
cd dictation-shim
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Output: `bin\Release\net9.0-windows\win-x64\publish\dictation-shim.exe`.

## Run

Double-click the `.exe`, or:

```powershell
Start-Process .\bin\Release\net9.0-windows\win-x64\publish\dictation-shim.exe
```

A microphone tray icon appears (Segoe Fluent Icons U+E720). Right-click for
the menu. Single-instance via a named mutex; launching twice is harmless.

## Auto-start with Windows

```powershell
$startup = [Environment]::GetFolderPath("Startup")
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut("$startup\Dictation Shim.lnk")
$lnk.TargetPath = (Resolve-Path .\bin\Release\net9.0-windows\win-x64\publish\dictation-shim.exe).Path
$lnk.Save()
```

## Whispering setup

Inside Whispering, under Settings → Recording:
- **Hotkey**: `Ctrl+Shift+Alt+Space`
- **Mode**: Push-to-Talk
- *(optional)* turn off the post-transcription sound — it's redundant once
  you've trained your ear on the start-recording beep.

The shim catches `Ctrl+Win` and emits `Ctrl+Shift+Alt+Space` only as long
as the chord is held; Whispering does the rest. Whispering must be running
(window can be hidden via *tray → Hide Window*) — the shim only forwards
keystrokes, it doesn't launch or restart Whispering.

## Configuration

Source/target hotkeys and timing constants live as `const` values near the
top of `Program.cs`. To customize, edit and rebuild. A runtime config
file may land in a future release.

## Logs

`%LOCALAPPDATA%\DictationShim\debug.log` — minimal, only hook
install/uninstall and reset events. Verbose per-keypress logging is
intentionally disabled to keep the file tiny.

## Troubleshooting

- **Hotkey does nothing** — check `Get-Process dictation-shim`; check the
  log shows `hook installed`; verify Whispering is running with
  `Ctrl+Shift+Alt+Space` registered (test by pressing the four keys
  directly — Whispering should toggle).
- **Ctrl+Win opens Copilot or a download page** — shim isn't running.
  Restart it.
- **A modifier got stuck** (rare) — tray menu → *Reset stuck keys*.
- **Audio stays muted** — *Reset stuck keys* doesn't restore audio; use
  *Restart* (it disposes hooks cleanly) or unmute manually with the
  speaker icon on the taskbar.

## Background: why `keybd_event`, not `SendInput`?

On the developer's Win11 system, Whispering's RegisterHotKey-registered
global shortcut fires for `keybd_event`-injected synthetic events but
**not** for `SendInput` ones. Both APIs go through the same Win32 input
pipeline in theory. In practice — for whatever Win11-internal reason —
they behave differently here. PowerToys Keyboard Manager (which uses
`SendInput`) cannot trigger Whispering on this system; this shim (which
uses `keybd_event`) can. The asymmetry is documented in the source where
it matters. Reports of behavior on other Windows builds welcome.

## Origin

Built to replace [Wispr Flow](https://wisprflow.ai/) with a fully-local
stack (Whispering + Speaches + this shim) while keeping the exact
muscle-memory of `Ctrl+Win` push-to-talk. Distilled from a personal setup
into a reusable tool — your mileage and your Windows version may vary,
but the architecture should generalize.

## Contributing

Bug reports and PRs welcome. Two improvements that could land upstream
in Whispering rather than living here:

- **Full-format clipboard preserve** — Whispering's `write_text` in
  `apps/whispering/src-tauri/src/lib.rs` could be extended to read+restore
  the full OLE clipboard, not just `read_text()`. Would benefit every
  Whispering user.
- **Audio auto-mute as a settings toggle** — gated behind a setting so
  users who don't want it aren't surprised.

## License

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

## Acknowledgements

- [Whispering](https://github.com/EpicenterHQ/epicenter) by Braden Wong —
  the actual transcription pipeline this shim hangs off of. Without it
  there's nothing to forward to.
- [Speaches](https://github.com/speaches-ai/speaches) — the local Whisper
  inference server I run Whispering against. Not required by the shim —
  any Whispering-supported backend works.
