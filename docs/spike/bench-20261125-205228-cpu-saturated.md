# Spoke bench 2026-11-25 20:52:28 cpu-saturated

- Runs: 100, completed: 100, misses: 0
- Renderer: GPU (D3D11 hardware) on Intel(R) UHD Graphics 630, DPI 96, surface 424px
- Display refresh period: 6.94 ms (the frame is on screen at the next vblank after DWM applies the commit)
- Foreground during test: "Spoke focus target"
- Focus changed while open/after close: **0** times; text typed after 100 opens landed in target: **yes**

| Stage (ms) | p50 | p95 | p99 | max |
|---|---|---|---|---|
| SendInput → WM_HOTKEY (OS input path) | 3.40 | 3.84 | 4.00 | 4.34 |
| WM_HOTKEY → shown (draw+present+commit+SetWindowPos) | 4.59 | 4.94 | 5.10 | 6.11 |
| shown → DWM applied commit | 2.94 | 5.89 | 6.33 | 6.71 |
| **SendInput → DWM applied (total)** | 11.12 | 14.02 | 14.68 | 14.73 |

- Our open path, mean per stage: monitor/DPI/hit-test 0.06 ms · draw+present 1.82 ms · DComp commit 0.03 ms · SetWindowPos show 2.71 ms
- Draw calls: 301, mean 1.958 ms, max 45.844 ms (CPU time to record + present)
- Arrow key → redraw presented: mean 1.871 ms, max 6.847 ms over 200 presses
- Memory at end of bench: working set 42.9 MB, private 17.0 MB
