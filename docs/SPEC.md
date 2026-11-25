# Spoke: product spec

**Spoke** is a radial command wheel for Windows. Press a shortcut and a wheel of actions opens around the mouse pointer. Point, click, done. It has to feel instant and stay instant while heavy apps (local AI models, IDEs, builds, games) are running.

Inspired by radial-menu tools on the Mac, built as its own product: own name, own look, own code. Nothing here copies another app's name, mascot, branding or exact visuals.

> Name check: "Spoke" was picked on 2026-11-25. Before launch, do a trademark and Store-name search (Microsoft Store, USPTO/EUIPO, winget IDs). Fallbacks: **Rondel**, **Pinwheel**.

---

## 1. Who it's for

People who live on Windows with a mouse or trackpad and want their ten most-used things one flick away: developers, designers, writers and power users. They hate launchers that steal focus, and they hate tray apps that eat RAM.

## 2. Product principles

1. **Instant.** The wheel is on screen within 30 ms of the shortcut, every time, under load.
2. **Never steals focus.** Whatever you were typing into is still active after the wheel closes, so paste and type actions land where you expect.
3. **Invisible when idle.** Under 40 MB RAM and about 0% CPU while waiting. No timers and no polling.
4. **Local only.** No account, no telemetry by default, nothing uploaded. Clipboard history is encrypted on disk.
5. **Mouse-first, keyboard-complete.** Everything works with the pointer alone, and everything also works from the keyboard.

## 3. The Spoke look (original visual language)

| Element | Spoke |
|---|---|
| Ring | 8 slices by default (4–12 allowed). Dark graphite slices with hairline gaps and a soft outer shadow. |
| **The spoke** | A thin needle from the hub to the slice you're pointing at. It's the signature element and gives the app its name. |
| Hover | The hovered slice **lifts outward** about 10 px, and its **inner edge** lights up in the wheel's accent colour. The outer rim stays neutral. |
| Hub | Shows the wheel's name when idle. On hover it shows the slice's label in monospace capitals and the action type underneath ("APP · VS CODE"). |
| Numbers | Small 1–9 tags at the inner edge of each slice for keyboard use. |
| Sub-actions | A slice with sub-actions **fans out** up to 4 small petals outside the ring. They don't replace the wheel. |
| Wheel row | A row of pills under the wheel showing your other wheels. Tab or click to switch. |
| Type | Monospace for labels (Cascadia Mono, which ships with Windows 11), Segoe UI Variable for settings. |
| Palette | Graphite #121418 / #1B1E24 / #2A2E36, text #E7E9EE, one accent per wheel (default Signal Amber #F5A524; presets: Teal, Coral, Violet, Lime, Ice). |

---

## 4. Features by phase

Each feature has **done criteria**. A feature is done only when every criterion passes on Windows 11 at 100% and 150% scaling, on one monitor and on two monitors with mixed DPI.

### Phase 0: Spike (proves the risky part)
| Feature | Done when |
|---|---|
| Tray daemon | `Spoke.exe` starts with a tray icon (menu: Open settings, Pause, Quit), runs a single instance, and exits cleanly. |
| Global hotkey | Ctrl+Alt+Space opens the wheel. If the hotkey is already taken, Spoke says so instead of failing silently. |
| Wheel overlay | A Direct2D/DirectComposition wheel with 8 slices appears centred on the pointer, on the monitor under the pointer, at that monitor's DPI. |
| No focus steal | With Notepad focused: open the wheel, close it, and type. The text goes into Notepad. The caret never blinks out. |
| Choose a slice | Point and click on the ring, keys 1–8, arrow keys plus Enter. Esc, right-click or a click outside the wheel closes it without running anything. |
| Launch | App and URL slices launch. |
| Measured | Hotkey-to-first-frame p50/p95, idle working set and private bytes, and idle CPU over 10 minutes are logged and reported. The same numbers are also measured with a local LLM loading the GPU. |

### Phase 1: Core wheel (≈2 weeks)
| Feature | Done when |
|---|---|
| Action: App | Win32 exe, Store/MSIX app (by AUMID) or shortcut. Optional arguments and working folder. Uses the app's own icon. |
| Action: Website | Opens the URL in the default browser or a chosen one. Shows the site's favicon if it's cached locally, otherwise a globe icon. Spoke itself makes no network calls. |
| Action: Folder | Opens the folder in Explorer. Variants: *open in Terminal here* (Windows Terminal, falling back to PowerShell) and *copy path*. |
| Action: Search | Prompts for a query in a small input under the wheel, which takes focus only for that moment, then opens Google, DuckDuckGo, Bing, YouTube, Wikipedia or a custom `{query}` template. |
| Action: Text | Inserts custom text into the focused app. Tokens: `{date}`, `{time}`, `{iso}`, `{unix}`, `{uuid}`, `{clipboard}`. Restores the clipboard afterwards. |
| Tray + autostart | Starts with Windows (toggle). Tray menu: Pause triggers, Settings, Quit. |
| Settings file | `%APPDATA%\Spoke\settings.json`, hand-editable, reloaded live when it changes. A bad file shows an error and keeps the last good config. |
| Basic settings window | Edit one wheel's 8 slices (type, target, label) and the hotkey. *(Replaced by the full editor in Phase 2.)* |
| Wheel row | Visible but showing just one wheel. |

### Phase 2: Many wheels and the editor (≈2.5 weeks)
| Feature | Done when |
|---|---|
| Multiple wheels | Unlimited wheels, each with its own name, accent, slice count (4–12), size, hotkey and trigger set. |
| Wheel row switching | Tab / Shift+Tab or clicking a pill switches wheels in place in under 16 ms, with no re-show flicker. |
| Sub-actions | A slice can hold up to 4 sub-actions that fan out on hover or a right arrow. Alternatively, **Run all** runs them in order with an optional delay. |
| Wheel editor | Live wheel preview. Drag to reorder slices on the wheel and in the list. Slice inspector. Undo/redo. Changes show in the running daemon within 200 ms. |
| Triggers: hotkeys | Record a hotkey per wheel and detect conflicts (taken by Windows or another app). Two modes: *tap* (opens, stays open) and *hold* (release picks the hovered slice). |
| Triggers: mouse buttons | Middle-click hold, or the X1/X2 side buttons, opens a chosen wheel. Off by default. |
| Triggers: hot corners | Per corner per monitor: dwell time (default 250 ms), which wheel, and ignore-while-fullscreen (games and video). |
| Triggers: shake | Shaking the pointer (≥4 direction reversals in ≤600 ms, adjustable) opens a chosen wheel. Off by default. |
| Export / import | A wheel saves as a `.spoke` file (JSON). App paths are made portable using env vars and AUMIDs. Import shows a preview and warns about missing apps. |
| Gallery | About 8 bundled starter wheels (Dev, Browser, Media, Writing, Clipboard, Windows, Design, Meetings), installable in one click. Offline. |

### Phase 3: Clipboard and system (≈2.5 weeks)
| Feature | Done when |
|---|---|
| Clipboard history | Text, images, files and links, stored in SQLite with each entry encrypted (AES-GCM, key protected by DPAPI for the current user). Retention: 1 day, 7 days, 30 days, 1 year or forever, plus a max item count. Pin items. |
| Privacy rules | Skips copies marked `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0` or `Clipboard Viewer Ignore` (what password managers set). Also skips any apps on a user list, and has a pause toggle. |
| Clipboard wheel | The last 8 clips as slices (text preview or thumbnail). The hub shows the full preview of the hovered clip. Picking one pastes it into the focused app. A row of tools sits underneath. |
| Clipboard tools | Plain text, UPPER/lower/Title/camel/snake, trim, word & character count, format/minify JSON, Base64/URL/HTML encode & decode, calculate (`2*(3+4)` → `14`), OCR an image clip, search it, translate (opens a translator page in the browser), open as a link, email it. Each tool can be a slice. |
| Screenshot | Region, window or full screen, saved to a chosen folder as PNG, with copy to clipboard optional. Filename pattern `Spoke {date} {time}.png`. |
| OCR | Region-to-text using Windows.Media.Ocr in the user's languages, then copies the result. Works offline. |
| System actions | Volume up/down/set, mute toggle, mic mute toggle, screen saver, lock, sleep, show desktop, media play/pause/next. |
| Generators | UUID v4/v7, timestamps (ISO, Unix, custom format), lorem ipsum, random password *generated locally and only pasted, never stored*. |
| Run script | Runs a `.ps1`, `.bat` or `.cmd` file or a shell command, hidden or visible, optionally with the selected text or clipboard. Also a "Send keys" action. |

### Phase 4: File-drop conversion (≈2 weeks)
| Feature | Done when |
|---|---|
| Drop mode | Dragging a file and pressing the wheel shortcut, or dragging it into a hot corner, opens the **format wheel** under the pointer. Explorer's right-click menu also has *Convert with Spoke…*. |
| Image formats | PNG, JPEG, WebP, TIFF, GIF, BMP and HEIC (offered only when the HEIF codec is installed). Each slice shows the **estimated output size** before you drop. Uses WIC, plus libwebp if WIC has no WebP encoder. |
| Video | MP4 (H.264), MOV, MKV, WebM, GIF; mute; replace or add audio (asks for an audio file); extract audio to MP3, AAC or WAV. Bundled LGPL ffmpeg. Shows progress in the hub and can be cancelled. |
| Output | Saved beside the original as `name (Spoke).ext`, never overwriting. A result toast shows the size before and after, with *Open* and *Show in folder*. Nothing is uploaded. |
| Batch | Dropping several files converts them all, in a queue. |

### Phase 5: Ship (≈2 weeks)
| Feature | Done when |
|---|---|
| Onboarding | First run: pick a hotkey (with a conflict check), try the wheel on a practice screen, choose a starter wheel, set autostart. Takes under 90 seconds. |
| Appearance | Size (S/M/L/custom), slice count, accent, opacity, blur behind (on/off), animation (none/subtle/springy), icon style (app icons or mono glyphs), hub style. |
| Sounds | Themes: Off (default), Soft Click, Mechanical, Glass. Volume control. Sounds on open, hover, select and error. Short WAVs held in memory. |
| Licensing | 7-day full trial, then a lifetime licence or a weekly plan. Store build uses Store add-ons; direct build uses signed offline licence keys. After the trial, triggers pause and the wheel shows *Buy* / *Enter key*. Settings stay. |
| Packaging | Signed MSIX for the Store and winget, plus a signed zip/MSI for direct download. Auto-update: the Store handles it for the Store build; the direct build checks once a day *only if the user allows it*. |
| Accessibility | High-contrast theme respected, Narrator announces the hovered slice, reduced-motion respected, everything reachable by keyboard. |
| Crash safety | Unhandled errors write a local log. A watchdog restarts the daemon once. Nothing is sent anywhere. |

---

## 5. Out of scope for v1
- Sync between machines (export/import covers it for now).
- Plugins and third-party actions.
- Pen/touch-first gestures (works with touch, but isn't tuned for it).
- Windows 10 support (decide after the spike. The code paths exist on 10 but aren't tested.)

## 6. Open decisions for later phases
1. **Payments for the direct build:** Paddle, Lemon Squeezy or Store-only. (Phase 5.)
2. **Weekly plan pricing** next to the lifetime price. (Phase 5.)
3. **Default hotkey:** Ctrl+Alt+Space for now. Onboarding asks anyway.
