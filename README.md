# D3D11 Thread Leak — GStreamer + NVIDIA + Intel

A minimal C# repro demonstrating a three-way interoperability bug between
GStreamer's `d3d11videosink`, NVIDIA's D3D11 user-mode driver (`nvwgf2um.dll`),
and Intel's D3D11 driver — discovered in a production Windows desktop application
deployed on tablets inside underground mining operations.

---

## The Bug

Our application crashed after 6 Enter/Exit playback cycles on every machine with
an NVIDIA GPU. The same build ran indefinitely on Intel iGPU without issue.

Thread count grew by ~230 per cycle and never recovered:

```
Baseline:  19 threads
Cycle 1:  ~414 threads  (+395)
Cycle 2:  ~870 threads  (+456)
Cycle 3:  ~870 threads  (plateau — no new cycles)
Cycle 4: ~1102 threads  (+232)
Cycle 5: ~1336 threads  (+234)
Cycle 6:  CRASH — Windows refuses to create new threads/handles
```

Intel iGPU on the same build, same session length: stable at ~100 threads, no crash.

---

## Investigation

The application uses GStreamer-sharp (`d3d11videosink`) to render video into WPF
tiles. Each playback session creates 4 GStreamer pipelines, one per video channel.
On exit, pipelines are disposed: `SetState(Null)` + `GetState` (waited to
confirm `cur=Null`) + `Dispose()`.

A thread snapshot revealed two populations of leaked threads:

| Thread start address | Count/cycle | Source |
|---|---|---|
| `nvwgf2um.dll+0x1FECB6E` | ~212 | NVIDIA D3D11 user-mode driver |
| `ucrtbase.dll+0x7E9B0` | ~18 | GLib bus poll thread (GStreamer) |

The GLib threads were fixed by adding `Bus.RemoveSignalWatch()` before pipeline
dispose (a straightforward GStreamer-sharp resource management oversight).

The NVIDIA threads were not fixed by any cleanup change. They persisted regardless
of how carefully the pipeline was shut down.

---

## Hypotheses

Three candidates:

- **H1 — NVIDIA driver bug:** `nvwgf2um.dll` spawns worker threads per
  `ID3D11Device` and never reclaims them, even after the device COM refcount
  reaches zero.

- **H2 — GStreamer device cache:** GStreamer's `gst_d3d11_device_new()` caches
  the device in a global registry. The pipeline's ref is released on dispose,
  but the cache retains one ref — device COM refcount never reaches zero,
  so NVIDIA correctly keeps its threads alive.

- **H3 — Application code:** Managed wrappers or additional COM objects hold
  dangling refs that prevent the device from being released.

---

## Experiment 1 — Raw D3D11, no GStreamer

To test H1, we wrote a standalone C# repro using only P/Invoke to `d3d11.dll`.
No GStreamer. No application framework.

### Test A — Proper release (refcount → 0 every cycle)

```
D3D11CreateDevice → Flush → Release context → Release device
```

| GPU | AfterCreate | AfterRelease | Delta |
|---|---|---|---|
| NVIDIA RTX 3070 / 5090 | +18 threads | -17 threads | **clean** |
| Intel iGPU | +1 thread | 0 threads | **clean** |

**H1 rejected.** NVIDIA releases threads correctly when the D3D11 contract is met.

### Test B — Dangling ref (GStreamer cache simulation)

One extra `AddRef()` is called before `Release()`, leaving device COM refcount at 1
instead of 0. This simulates GStreamer's `GstD3D11Device` global cache retaining
a reference after the pipeline is disposed.

```
D3D11CreateDevice → Flush → AddRef (cache) → Release context → Release device
                                              ↑ refcount = 1, device stays alive
```

| GPU | Threads per cycle | After 10 cycles | Recovers? |
|---|---|---|---|
| NVIDIA RTX 3050 Laptop / 3070 / 5090 | **+17** | **+170 threads** | Yes — after drain |
| AMD Radeon iGPU (Ryzen 5000) | **+2** | **+20 threads** | Yes — after drain |
| Intel iGPU | **+0** | **+0 threads** | N/A |

NVIDIA accumulates 17 threads per stranded device. AMD accumulates 2 — significantly
lower than NVIDIA but not zero. Intel accumulates zero.

AMD's result puts it in the middle: resilient enough to avoid rapid crashes in practice,
but not immune. With enough cycles or concurrent devices, AMD would eventually exhaust
thread limits as well — just far more slowly than NVIDIA.

### Test C — Cache drain

All held refs released at once (simulates GStreamer cache being flushed):

| GPU | Before drain | After drain (2s) | Recovered |
|---|---|---|---|
| NVIDIA RTX | 177 threads | 7 threads | **-170 (full)** |
| AMD Radeon iGPU | ~27 threads | ~7 threads | **-20 (full)** |
| Intel iGPU | 7 threads | 7 threads | 0 |

Threads are tied to device lifetime. Once the ref is released and refcount
reaches zero, both NVIDIA and AMD correctly reclaim all worker threads.

---

## Experiment 2 — Cross-vendor and cross-generation

Reproduced on multiple GPU generations and vendors with the production GStreamer application
and the raw D3D11 repro:

| GPU | Raw repro threads/cycle | Production threads/cycle | Crashes after |
|---|---|---|---|
| NVIDIA RTX 3050 Laptop | **+17** | ~230 | ~6 cycles |
| NVIDIA RTX 3070 | **+17** | ~230 | ~6 cycles |
| NVIDIA RTX 5090 | **+17** | ~230 | ~6 cycles |
| AMD Radeon iGPU (Ryzen 5000) | **+2** | not measured | >100 cycles (estimated) |
| Intel UHD iGPU | **+0** | ~5 | Never |

The +17 rate is consistent across all tested NVIDIA SKUs — not a single-generation regression.
AMD's +2/cycle means it would reach the same OS thread limit, but ~8.5× more slowly than NVIDIA.

---

## Conclusion

This crash sits at the intersection of three design decisions:

### GStreamer — the ref-count bug

`gst_d3d11_device_new()` caches `GstD3D11Device` in a global per-adapter
registry. When a pipeline is disposed, the pipeline's ref is released but
the cache entry retains one ref. The device COM refcount does not reach zero
until the cache is explicitly flushed — which does not happen between
`Enter/ExitPlaybackMode` cycles.

**GStreamer owns the primary bug.** The fix: release the cache entry when
no active pipelines reference the device.

### NVIDIA — the amplifier

NVIDIA's driver spawns approximately **17 worker threads per `ID3D11Device`
instance**. These threads are correctly released when refcount reaches zero,
but they accumulate rapidly when GStreamer's cache prevents that from happening.

AMD's driver spawns approximately **2 threads per device** — the same GStreamer
bug accumulates threads on AMD, but ~8.5× more slowly. AMD would eventually crash
given enough cycles, but survives far longer in practice.

Intel's driver spawns **0 threads per device** — the same GStreamer bug
produces zero observable accumulation on Intel hardware. The application runs
indefinitely.

The spectrum (NVIDIA: 17 → AMD: 2 → Intel: 0) suggests this is a design
choice, not a bug. NVIDIA's per-device thread model is technically correct but
fragile: a shared thread pool across device instances — as Intel appears to use
— would make the driver resilient to this class of application error.

**NVIDIA does not need to fix a bug, but a design change would prevent
this class of crash regardless of upstream resource management quality.**

### The interoperability gap

Neither party anticipated the other's behavior:

- GStreamer's device cache was designed for performance (avoid re-creating
  the device per pipeline). It did not account for drivers that maintain
  expensive per-device state.
- NVIDIA's driver spawns workers per device for performance (parallel
  command submission). It does not account for callers that create/destroy
  devices in rapid cycles.
- Intel's low per-device thread count accidentally makes it immune.

The crash only manifests in production with a specific usage pattern:
repeated Enter/Exit of a multi-pipeline playback mode. It was invisible
in development (single session) and in standard testing (single cycle).

---

## Reproducing

### Requirements

- Windows 10 / 11 x64
- .NET Framework 4.7.2 (pre-installed on Windows 10+)
- `d3d11.dll` (present on all Windows installations)
- NVIDIA dGPU (to observe the accumulation) or Intel iGPU (to observe stability)

### Build

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe D3D11ThreadTest.csproj /p:Platform=x64
```

### Run

```bat
bin\D3D11ThreadTest.exe                        # All three tests, 10 iterations
bin\D3D11ThreadTest.exe --iter 20 --sleep 0   # 20 cycles, no sleep (stress)
bin\D3D11ThreadTest.exe --test a               # Test A only (proper release)
bin\D3D11ThreadTest.exe --test b               # Test B only (dangling ref)
bin\D3D11ThreadTest.exe --test c               # Test B + C (accumulate then drain)
```

### Expected output — NVIDIA

```
TEST A: delta = -17 every cycle   (threads correctly released)
TEST B: +17 per cycle, linear     (threads accumulate, never released)
TEST C: -170 after drain          (full recovery once refcount reaches 0)
```

### Expected output — Intel

```
TEST A: delta = 0 every cycle     (no measurable thread overhead per device)
TEST B: +0 per cycle              (immune — driver does not spawn per-device threads)
TEST C: 0 recovered               (nothing to recover)
```

---

## Fix Paths

| Party | Fix | Impact |
|---|---|---|
| **GStreamer** | Release `GstD3D11Device` cache entry when last pipeline using it is disposed | Correct fix — addresses root cause on all GPU vendors |
| **NVIDIA** | Use a shared worker thread pool across `ID3D11Device` instances | Driver resilience — prevents this crash class regardless of upstream quality |
| **Application** | Reuse GStreamer pipelines between sessions instead of destroy/recreate | Workaround — avoids the device churn entirely |

---

## Environment

| | Detail |
|---|---|
| Application | .NET Framework 4.7.2 WPF, GStreamer-sharp 1.0 |
| GStreamer | 1.0 MinGW x86_64 (bundled) |
| NVIDIA drivers tested | Production drivers on RTX 3070 and RTX 5090 |
| Intel driver tested | Production driver on integrated UHD Graphics |
| OS | Windows 11 Pro |
| Discovery context | Tablet deployment, underground mining site |
