# Spoke bench 2026-11-25 20:46:29 under-llm-load

- Runs: 100, completed: 100, misses: 0
- Renderer: GPU (D3D11 hardware) on Intel(R) UHD Graphics 630, DPI 96, surface 424px
- Display refresh period: 6.94 ms (the frame is on screen at the next vblank after DWM applies the commit)
- Foreground during test: "Spoke focus target"
- Focus changed while open/after close: **0** times; text typed after 100 opens landed in target: **yes**

| Stage (ms) | p50 | p95 | p99 | max |
|---|---|---|---|---|
| SendInput → WM_HOTKEY (OS input path) | 4.37 | 10.25 | 10.79 | 25.53 |
| WM_HOTKEY → shown (draw+present+commit+SetWindowPos) | 3.67 | 8.98 | 9.68 | 9.92 |
| shown → DWM applied commit | 2.83 | 6.31 | 8.32 | 8.48 |
| **SendInput → DWM applied (total)** | 12.09 | 21.53 | 22.84 | 35.10 |

- Our open path, mean per stage: monitor/DPI/hit-test 0.11 ms · draw+present 1.55 ms · DComp commit 0.02 ms · SetWindowPos show 2.82 ms
- Draw calls: 302, mean 1.324 ms, max 15.580 ms (CPU time to record + present)
- Arrow key → redraw presented: mean 1.198 ms, max 3.843 ms over 200 presses
- Memory at end of bench: working set 42.8 MB, private 17.1 MB
