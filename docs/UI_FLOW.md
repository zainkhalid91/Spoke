# Spoke: UI flows

Rendered versions of these diagrams are in `docs/wireframes/png/flows-*.png`.

## 1. First run and onboarding

```mermaid
flowchart TD
  A[Install / first launch] --> B[Welcome<br/>'A wheel of shortcuts, one key away']
  B --> C[Pick your shortcut<br/>record keys, conflict check live]
  C -->|taken| C1[Shows who holds it<br/>suggests 3 free combos] --> C
  C -->|free| D[Try it<br/>practice screen: press shortcut,<br/>pick the glowing slice]
  D -->|picked| E[Choose a starter wheel<br/>Dev · Browser · Writing · Media · Blank]
  E --> F[Start with Windows? toggle<br/>Clipboard history? toggle, off by default]
  F --> G[Done<br/>tray tip: 'Spoke lives here']
  G --> H[(Daemon running, trial day 1 of 7)]
  D -->|skip| E
```

## 2. Open the wheel and choose a slice

```mermaid
stateDiagram-v2
  [*] --> Hidden
  Hidden --> Open: hotkey / hot corner / shake / mouse button
  note right of Open
    Wheel centred on pointer
    (clamped to stay on-screen)
    Hub shows wheel name
    Focus stays in user's app
  end note
  Open --> Hover: pointer enters a slice<br/>or arrow keys
  Hover --> Hover: move to another slice<br/>(needle follows, slice lifts)
  Hover --> Run: click slice
  Hover --> Hidden: click outside the wheel<br/>(nothing runs)
  Open --> Run: key 1–9
  Hover --> Run: Enter
  Hover --> Fan: slice has sub-actions<br/>(hover 150 ms or →)
  Open --> Hidden: Esc / click in hub or outside /<br/>hotkey again
  Hover --> Hidden: Esc
  Run --> Hidden: wheel fades (80 ms), action runs
  Run --> Toast: action reports result/error
  Toast --> [*]
```

**Hold mode:** keep the hotkey held, point, and release. Releasing over a slice runs it. Releasing in the hub cancels.

## 3. Sub-actions (fan-out)

```mermaid
flowchart LR
  A[Hover slice 'Search ▸'] -->|150 ms or →| B[Up to 4 petals fan out<br/>beyond the slice's rim]
  B --> C{Choose}
  C -->|click petal / 1–4| D[Run petal action]
  C -->|← or pointer back to ring| A
  C -->|Esc| E[Close wheel]
  A -->|slice set to 'Run all'| F[Runs all child actions in order<br/>hub shows 1/3, 2/3, 3/3]
```

## 4. Switching wheels with the wheel row

```mermaid
flowchart LR
  A[Wheel 'Dev' open<br/>row: ●Dev  Browser  Clips  Media] -->|Tab| B[Wheel 'Browser'<br/>same position, cross-fade 90 ms]
  B -->|Shift+Tab| A
  A -->|click pill 'Clips'| C[Clipboard wheel]
  A -->|hover a pill 300 ms| D[Preview ring of that wheel] -->|move away| A
```

## 5. File drop and conversion

```mermaid
sequenceDiagram
  actor U as User
  participant X as Explorer
  participant S as Spoke daemon
  participant W as SpokeWorker
  U->>X: drags photo.heic
  U->>S: presses wheel shortcut (or drags into hot corner)
  S->>S: open in DROP MODE under pointer
  S->>W: probe(photo.heic)
  W-->>S: type=image, 4032×3024, est sizes per format
  S-->>U: format wheel: PNG 18 MB · JPEG 2.1 MB · WebP 1.4 MB · …
  U->>S: drops on 'WebP'
  S->>W: convert(photo.heic → webp, q=82)
  W-->>S: progress 0..100 (shown in the hub)
  W-->>S: done: photo (Spoke).webp, 1.38 MB
  S-->>U: toast: 4.9 MB → 1.38 MB · Open · Show in folder
```

Video files get a different format wheel: MP4 · WebM · MOV · MKV · GIF · Mute · Audio ▸ (MP3/AAC/WAV/Replace audio).

## 6. Clipboard history wheel

```mermaid
flowchart TD
  A[Clipboard wheel hotkey<br/>or 'Clips' pill] --> B[Ring: last 8 clips<br/>text preview / thumbnail]
  B -->|hover| C[Hub shows full preview<br/>source app · age]
  C -->|click / 1–8| D[Paste into focused app<br/>clipboard restored afterwards]
  C -->|right-click or Shift+click| E[Tools row becomes active for that clip]
  E --> F[Plain · UPPER · lower · Trim · JSON · Count · Encode · Calc · OCR · Search · Translate]
  F --> G[Result pasted, or copied if target is elevated]
  B -->|PgDn / scroll| H[Next 8 clips]
  B -->|type letters| I[Filter clips by text] --> B
  B -->|P| J[Pin / unpin hovered clip]
```

## 7. Settings app

```mermaid
flowchart LR
  T[Tray → Settings<br/>or double-click tray icon] --> S[SpokeSettings.exe]
  S --> W[Wheels]
  S --> TR[Triggers]
  S --> AP[Appearance]
  S --> SO[Sounds]
  S --> CL[Clipboard]
  S --> GA[Gallery]
  S --> GE[General]
  S --> LI[License]
  W --> W1[Wheel list: add · duplicate · export · import · delete]
  W --> W2[Live preview: drag slices to reorder,<br/>drop apps/files/URLs onto it]
  W --> W3[Slice inspector: type · target · label · icon · colour · sub-actions · run-all]
  TR --> TR1[Per-wheel hotkey + tap/hold] & TR2[Hot corners per monitor] & TR3[Shake] & TR4[Mouse buttons]
  AP --> AP1[Size · slices · accent · opacity · blur · animation · icons · renderer]
  SO --> SO1[Theme · volume · per-event toggles · preview]
  CL --> CL1[On/off · retention · max items · excluded apps · clear all]
  GA --> GA1[Starter wheels → Add]
  GE --> GE1[Start with Windows · language · run elevated · logs · reset]
  W2 -. saves .-> CFG[(settings.json)] -. pipe 'reload' .-> D[Daemon applies < 200 ms]
```

## 8. Trial and purchase

```mermaid
stateDiagram-v2
  [*] --> Trial: first run (7 days, full features)
  Trial --> Trial: day 5 & 6: one gentle tray notice
  Trial --> Expired: day 8
  Trial --> Licensed: buy any time
  Expired --> Paywall: any trigger
  note right of Paywall
    Wheel opens, slices greyed
    Hub: "Trial ended"
    Two slices: Buy · Enter key
    Settings and wheels kept
  end note
  Paywall --> Store: Buy (Store build → Store add-on dialog)
  Paywall --> Checkout: Buy (direct build → browser checkout)
  Checkout --> KeyEntry: email delivers key
  Paywall --> KeyEntry: Enter key
  Store --> Licensed
  KeyEntry --> Licensed: offline signature check (Ed25519)
  Licensed --> [*]
```
