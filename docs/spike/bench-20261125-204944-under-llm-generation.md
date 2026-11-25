# Spoke bench 2026-11-25 20:49:44 under-llm-generation

- Runs: 100, completed: 100, misses: 0
- Renderer: GPU (D3D11 hardware) on Intel(R) UHD Graphics 630, DPI 96, surface 424px
- Display refresh period: 6.94 ms (the frame is on screen at the next vblank after DWM applies the commit)
- Foreground during test: "Spoke focus target"
- Focus changed while open/after close: **0** times; text typed after 100 opens landed in target: **yes**

| Stage (ms) | p50 | p95 | p99 | max |
|---|---|---|---|---|
| SendInput → WM_HOTKEY (OS input path) | 3.14 | 4.53 | 5.26 | 6.40 |
| WM_HOTKEY → shown (draw+present+commit+SetWindowPos) | 3.71 | 4.99 | 5.94 | 6.36 |
| shown → DWM applied commit | 2.55 | 5.95 | 6.92 | 9.97 |
| **SendInput → DWM applied (total)** | 9.71 | 13.50 | 15.80 | 16.18 |

- Our open path, mean per stage: monitor/DPI/hit-test 0.06 ms · draw+present 1.29 ms · DComp commit 0.02 ms · SetWindowPos show 2.45 ms
- Draw calls: 302, mean 1.310 ms, max 31.779 ms (CPU time to record + present)
- Arrow key → redraw presented: mean 1.225 ms, max 2.826 ms over 200 presses
- Memory at end of bench: working set 43.6 MB, private 17.8 MB
