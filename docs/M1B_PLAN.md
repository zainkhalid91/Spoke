# M1b: customization + file and everyday utilities

Requested 2026-11-27. It pulls work forward from M7 (clipboard tools and system), M9 (image conversions) and M11 (appearance), and adds document conversion. All of it stays inside the wheel; no settings window is needed yet (that is still M2).

## 1. Customization (settings.json + tray menu)

| Key | Values | Effect |
|---|---|---|
| `theme` | `Auto`, `Aurora`, `Ember`, `Paper`, `Graphite` | `Auto` follows Windows light/dark mode live (`lightTheme`, default Paper; `darkTheme`, default Aurora). |
| `accent` | `""` (theme colours), `windows`, `#RRGGBB` | `windows` paints every slice in the Windows accent colour and follows it live when you change it in Settings → Personalization. |
| `size` | `S`, `M`, `L`, `XL` or a number 0.7–1.6 | Scales the whole wheel (ring, text, icons). It is applied as a DPI factor, so drawing stays sharp and hit-testing stays exact. |

The tray menu gains Theme → *Match Windows*, a *Colours* submenu (theme colours / Windows accent) and a *Size* submenu. Each choice is saved to settings.json with the same one-key write the theme already uses.

## 2. Sub-wheels

One mechanism: a slice can open a generated wheel in place (the same idea as Clips). Backspace or Esc goes back, and the hub shows the sub-wheel's name. New tool slices:

| Tool (`"tool"`) | Opens |
|---|---|
| `files` | Actions for the **files selected in File Explorer** (or on the desktop, or files copied with Ctrl+C), filtered by file type: **Convert ▸**, **Resize ▸**, **Compress**, **Remove metadata**, **Combine into PDF**, **Copy paths**. |
| `text` | Text tools on the **selected text** (Spoke copies it, changes it and pastes it back), or on the clipboard when nothing is selected: UPPER, lower, Title Case, Clean spaces, Pretty JSON, Base64 encode and decode, URL encode and decode, Word count. |
| `generate` | Types a fresh UUID, strong password, timestamp, ISO date or lorem paragraph. |
| `system` | Mute, Volume ±, Play/Pause, Lock, Screen off, Dark mode toggle, Keep awake (toggle), Empty Recycle Bin (Windows asks to confirm). |

## 3. File conversions (out of process)

The heavy work runs in `Spoke.exe --worker job.json`. It is the same exe in a different mode, started on demand, and it exits when finished, so the daemon's memory stays flat. It reports through a result file, and a toast shows the outcome. Output goes beside the original as `name (Spoke).ext` or `name (1280w).ext` and never overwrites. Batches are supported.

| From | To | How |
|---|---|---|
| Images (PNG, JPEG, BMP, GIF, TIFF, WebP, HEIC, AVIF, JXL, RAW where Windows has a decoder) | PNG, JPEG, BMP, TIFF, GIF, ICO, HEIC (only if the encoder exists), WebP/AVIF (only when ffmpeg is on PATH) | WIC. EXIF orientation is applied. ICO is written by hand (PNG entries, 16–256). |
| Images | Resize: 25/50/75 %, width 1920/1280/800, 512 thumbnail | WIC high-quality cubic scaler. |
| Images | Compress | JPEG quality 75; alpha is flattened onto white. |
| JPEG/PNG | Remove metadata | Lossless segment/chunk removal (EXIF, GPS, XMP, IPTC, text); the colour profile is kept. If a JPEG relies on an EXIF rotation, it is re-encoded upright at quality 95. |
| Images | Combine into one PDF | A hand-written PDF with one page per image (JPEG streams). |
| DOC, DOCX, RTF, ODT, TXT | PDF | Microsoft Word through COM automation (hand-rolled IDispatch, AOT-safe). |
| PDF | DOCX | Word's PDF reflow. |
| XLS, XLSX, CSV | PDF | Excel. |
| PPT, PPTX | PDF | PowerPoint. |
| Office formats | DOCX/XLSX/PPTX | Office (for old .doc/.xls/.ppt). |

Without Office installed, Spoke falls back to LibreOffice (`soffice --headless --convert-to`) if it is there; otherwise the slice says what is needed. Office runs invisibly with alerts off, and a job that hangs is killed after 3 minutes.

## 4. Build order

1. Size + Auto theme + Windows accent (renderer + tray + config). ✅ target: today
2. Sub-wheel mechanism + `system`, `generate`, `text` tools.
3. Explorer selection reader + worker process + image convert/resize/compress/strip/PDF.
4. Office conversions.
5. Tests (Core: options parsing, file naming, PDF writer, ICO writer, metadata stripping, text transforms), live checks, docs.

## Deferred
- Drag a file onto the wheel (OLE drop target, M9), Explorer *Convert with Spoke…* right-click verb, video (M10), PDF → images (needs WinRT `Windows.Data.Pdf`), OCR (M8).
