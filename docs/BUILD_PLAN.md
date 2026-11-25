# Spoke: build plan

Estimates assume one developer working with Claude, full days. Each milestone ends with a short demo and the performance numbers from `tools/bench`.

## Milestones

| # | Milestone | Contents | Estimate | Exit check |
|---|---|---|---|---|
| **M0** ✅ | **Spike** (riskiest part first). Done 2026-11-25, see [SPIKE_REPORT.md](SPIKE_REPORT.md) | .NET 10 + AOT toolchain. Tray daemon, single instance. `RegisterHotKey`. Overlay window (NOACTIVATE/TOOLWINDOW/TOPMOST/NOREDIRECTIONBITMAP) + D3D11/D2D/DComp, created at start-up. 8 slices at the cursor, per-monitor DPI. Hover + needle. Click / click-past-ring (later limited to the ring in M1a) / 1–8 / arrows / Esc. Launch app + URL. Bench harness. | **3–4 days** | Focus test passes in Notepad, VS Code, Chrome and Word. Measured: p50/p95 latency, idle RAM, idle CPU, both normally and with a GPU-heavy local LLM running. Go/no-go on CsWin32 vs Vortice. |
| **M1a** ✅ | **Built-in tools + new look** (added on request). Done 2026-11-25 | Screenshot (region / window / screen → clipboard, Pictures\Screenshots, Clips). Color picker (loupe, PowerToys formats + history, MIT code credited). Clips: encrypted image history wheel (40), paste/copy/delete/pages. Four themes from the tray. Resting wheel cached as a bitmap; text layouts cached. Selection only on the ring (outside click closes). | 1 day | Open latency at or below the M0 baseline on the same machine (median 10–19 ms vs 15–18 ms; draw 0.9 ms vs 1.5 ms). 59 unit tests pass. Screenshot, picker and paste verified live. |
| M1 | Core actions + config | `Spoke.Core` config model (JSON source-gen) and live reload. Actions: app (Win32 + AUMID), URL, folder (+ terminal, copy path), search (+ query box), text with tokens (SendInput Unicode / clipboard paste). Icon extraction + cache. Tray menu, autostart. Error toasts. | 5 days | Every Phase 1 done criterion in SPEC. Unit tests for layout math, tokens and config. |
| M2 | Minimal settings + IPC | Named-pipe server/client. `SpokeSettings.exe` (WinUI 3, AOT) with one page that edits one wheel and its hotkey, including conflict detection. | 4 days | Edit in the UI and the daemon updates within 200 ms. Daemon RAM unchanged while settings is open. |
| **— Phase 1 done (≈ 2.5 weeks incl. spike) —** ||||
| M3 | Many wheels + sub-actions | Multiple wheels, wheel row (Tab/click), petals fan-out, run-all, 4–12 slices, sizes. | 4 days | Switching wheels < 16 ms, no flicker. |
| M4 | Wheel editor | Full Wheels page: live preview, drag to reorder (list + wheel), drop apps/files/URLs onto slices, inspector, undo/redo. | 5 days | All Wheels-page flows from UI_FLOW §7. |
| M5 | Triggers + share | Hold mode, mouse buttons, hot corners (per monitor), shake, Triggers page. `.spoke` export/import, Gallery with 8 starter wheels. | 4 days | Idle CPU with the mouse hook on stays below 0.1% during constant mouse movement. |
| **— Phase 2 done (≈ 2.5 weeks) —** ||||
| M6 | Clipboard history | Listener, skip rules, SQLite + AES-GCM/DPAPI, retention, pins, clipboard wheel + preview, filter, Clipboard page. | 5 days | Password-manager copies are never stored (tested with KeePassXC + Bitwarden). DB unreadable from another Windows user. |
| M7 | Clipboard tools + system | All tools in SPEC, generators, volume/mute/mic (Core Audio), media keys, lock/sleep/screen saver, run script, send keys. | 4 days | Each tool has a unit test. Elevated-target fallback works. |
| M8 | Worker: screenshot + OCR | `SpokeWorker.exe` on demand. Region/window/screen capture (frozen-frame overlay), Windows.Media.Ocr. | 3 days | Worker starts in < 100 ms and exits after 30 s idle. Daemon RAM unaffected. |
| **— Phase 3 done (≈ 2.5 weeks) —** ||||
| M9 | Drop mode + images | OLE drop target on the overlay, drag + hotkey / hot corner, format wheel, WIC conversions, size estimates, HEIC detection, result toast, batch. | 4 days | Estimates within ±25% of the actual size. |
| M10 | Video | ffmpeg LGPL bundle, probe, MP4/WebM/MOV/MKV/GIF, mute, replace/add audio, extract MP3/AAC/WAV, progress + cancel. Explorer *Convert with Spoke…* | 5 days | Cancel leaves no partial files. The LGPL notice and an ffmpeg build-config check are in CI. |
| **— Phase 4 done (≈ 2 weeks) —** ||||
| M11 | Polish | Onboarding (4 steps), Appearance, Sounds, accessibility (Narrator, high contrast, reduced motion), crash log + watchdog. | 5 days | First-run test with someone new to the app, finished in < 90 s. |
| M12 | Ship | Licensing (Store add-ons + offline Ed25519 keys), MSIX + signing, winget manifest, Store listing assets, portable zip. | 5 days | Clean install/uninstall on a fresh Windows 11 VM. Trial expiry tested by changing the clock. |
| **— Phase 5 done (≈ 2 weeks) —** ||||

**Total ≈ 11–12 weeks** to a store-ready v1. Phase 1 (usable daily driver) lands in about 2.5 weeks.

## Risks

| # | Risk | Likelihood / impact | Mitigation | Proven in |
|---|---|---|---|---|
| 1 | **Focus leaks to the overlay**, so paste/type lands in the wrong place | Med / **High** | Never activate: `WS_EX_NOACTIVATE`, `MA_NOACTIVATE`, `SWP_NOACTIVATE`, keyboard via a temporary LL hook. Automated focus test in 4 apps on every build. The only focus-taking UI (query box) saves and restores the foreground window. | M0 |
| 2 | **Hotkey conflicts** with Windows, IDEs or other tools | High / Med | `RegisterHotKey` fails if the hotkey is taken, and we show that at record time with 3 suggested free combos. Mouse triggers as an alternative. A warning list of known Windows-reserved combos. | M2 |
| 3 | **Elevated (admin) target windows**: UIPI blocks SendInput and hooks | High / Med | Detect target elevation and fall back to "Copied, press Ctrl+V". An opt-in *Run elevated at logon* via Task Scheduler. Documented clearly. | M1 |
| 4 | **Mixed-DPI monitors**: blurry or wrongly sized wheel, wrong position | Med / Med | Per-monitor-v2 awareness. The wheel is positioned in physical pixels from `GetCursorPos` + `MonitorFromPoint`, with D2D DPI set per open. Cache invalidated on `WM_DPICHANGED`/`WM_DISPLAYCHANGE`. Tested at 100% + 150% + 175%. | M0 |
| 5 | **GPU saturated by local AI**, so the first frame is late | Med / High | Pre-rendered cached frame, DComp animations run in DWM, `Auto` renderer falls back to WARP. Benchmarked with a GPU-heavy LLM. | M0 |
| 6 | **Native AOT gaps** (WinUI 3, CsWinRT, SQLite, COM) | Med / Med | Each process is AOT-tested at its milestone. The daemon has no WinRT at all. If WinUI 3 AOT misbehaves, Settings ships as framework-dependent (it's not on the hot path). | M0/M2 |
| 7 | **LL hooks dropped by Windows** under heavy load (`LowLevelHooksTimeout`) | Low / Med | Callbacks do arithmetic only, the thread runs at high priority, EcoQoS is off, and the hook is re-installed if a watchdog sees it silently removed. | M5 |
| 8 | **Clipboard privacy**: storing a password by accident | Low / **High** | Honour all 3 exclusion formats, an app exclude list, pause, and encryption at rest. Tested against real password managers. | M6 |
| 9 | **ffmpeg licensing** | Low / High | LGPL shared build, no GPL encoders (no x264/x265). H.264 via Media Foundation. Licence notices in About. Build flags checked in CI. | M10 |
| 10 | **Drag-and-drop + hotkey interplay** (OLE modal drag loop) | Med / Med | Spike it early in M9. Fallbacks: hot-corner drop and the Explorer *Convert with Spoke…* menu. | M9 |
| 11 | **Full-monitor overlay triggering fullscreen heuristics** (taskbar, Focus Assist) | Low / Low | Size to the work area, no fullscreen flags. Verify in M0. | M0 |
| 12 | **Name/trademark clash** for "Spoke" | Med / Med | Search before the Store listing (M12). Fallbacks: Rondel, Pinwheel. Keep the name in one constants file. | M12 |
| 13 | **Toolchain**: this PC has VS 2019 Build Tools; AOT may need the 2022 linker | Med / Low | Try VS 2019 first. If ILCompiler refuses, install VS 2022 Build Tools (C++ workload) with the user's OK. | M0 |

## Spike plan (M0) in detail

1. Install the .NET 10 SDK (approved). Verify `dotnet publish -r win-x64 -p:PublishAot=true` on a hello-world with the local C++ tools.
2. `Spoke.Daemon`: message loop, single-instance mutex, tray icon (`Shell_NotifyIcon`), `RegisterHotKey(Ctrl+Alt+Space)`.
3. Overlay: create the window and D3D/D2D/DComp at start-up. Build the wheel geometry (8 path geometries) and cache it into a bitmap.
4. On hotkey: position on the cursor's monitor at that monitor's DPI, present, commit. Hover via `WM_MOUSEMOVE` angle maths. Needle + hub text (DirectWrite, Cascadia Mono).
5. Keyboard while open: install the LL keyboard hook on show, remove it on hide.
6. Actions: `ShellExecuteEx` for app/URL. Hard-coded 8 slices (Notepad, Terminal, Explorer, Calculator, 4 URLs).
7. `tools/bench`: 200× SendInput hotkey → QPC to first present (and DWM frame timing). Memory/CPU sampler. Run idle, then with a local LLM (Ollama) generating.
8. Report: numbers table + a short screen recording + go/no-go notes → `docs/SPIKE_REPORT.md`.
