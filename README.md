# Spoke

A radial command wheel for Windows. Press a shortcut, and a wheel of your apps, sites, folders, snippets and tools opens around the pointer. Point, click, done.

Native and tiny: C# / .NET 10 Native AOT, Direct2D + DirectComposition overlay, under 30 ms to open, under 40 MB idle.

![Spoke in its four themes: Aurora, Ember, Paper and Graphite](docs/themes.png)

**Status:** M0 spike done (the wheel opens in 9–14 ms, focus never stolen; see [docs/SPIKE_REPORT.md](docs/SPIKE_REPORT.md)). M1a adds built-in tools and a new look:
- **Screenshot** (slice 1): drag a region, click a window, or Space for the whole screen. Copied, saved to Pictures\Screenshots, and added to Clips.
- **Color picker** (slice 2): magnifier loupe, arrow keys to nudge, keys 1–n copy a format. Uses your PowerToys Color Picker formats and colour history when present (format code from PowerToys, MIT; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
- **Clips** (slice 3): a wheel of your last 40 copied images, stored encrypted (DPAPI) in `%LOCALAPPDATA%\Spoke\clips`. Click to paste, Shift+click to copy, Del to remove, mouse wheel or Tab for more pages. Respects apps that mark clipboard data private.
- **Themes:** Aurora, Ember, Paper (light) and Graphite, from the tray menu.

Open the wheel with **Ctrl+Alt+Z**. Choose a slice on the ring or press 1–8; a click outside the wheel closes it.

Plan:
- [docs/SPEC.md](docs/SPEC.md): product spec, features by phase, done criteria
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): stack, processes, performance budgets
- [docs/UI_FLOW.md](docs/UI_FLOW.md): flows (Mermaid); rendered in `docs/wireframes/png/flows-*.png`
- [docs/wireframes/](docs/wireframes/): low-fidelity wireframes (HTML/SVG), rendered PNGs in `png/`
- [docs/BUILD_PLAN.md](docs/BUILD_PLAN.md): milestones, estimates, risks

Re-render wireframes: `node docs/wireframes/make-flows.js && bash docs/wireframes/render.sh`

## Build

Needs the .NET 10 SDK and the MSVC C++ build tools (VS 2019 or 2022 Build Tools) for Native AOT. If `vswhere.exe` isn't found, add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH`.

```powershell
dotnet test tests/Spoke.Core.Tests
dotnet publish src/Spoke.Daemon -c Release -r win-x64 -o publish/m1a
publish/m1a/Spoke.exe --bench 100 --label my-run   # latency/focus report in %LOCALAPPDATA%\Spoke
```
