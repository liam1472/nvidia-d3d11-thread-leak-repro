"""
Generate chart for the GStreamer/NVIDIA/AMD/Intel D3D11 thread leak post.
Run: python chart.py
Output: thread_leak_chart.png
"""

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

# ── Colors ──────────────────────────────────────────────────────────────────
NVIDIA_GREEN = "#76b900"
AMD_RED      = "#ed1c24"
INTEL_BLUE   = "#0071c5"
CRASH_RED    = "#e03030"
BG           = "#0f0f0f"
GRID         = "#2a2a2a"
TEXT         = "#e8e8e8"
SUBTEXT      = "#888888"

# ── Data ────────────────────────────────────────────────────────────────────

# Panel 1 — Raw D3D11 repro (Test B: dangling ref, 10 cycles)
# Measured on HP Pavilion (Ryzen 5000): NVIDIA RTX 3050 Laptop + AMD Radeon iGPU
# Intel data from separate tablet run. Baseline normalized to 0.
repro_cycles = list(range(11))                  # 0 = baseline
nvidia_repro = [17 * i for i in range(11)]      # +17/cycle (measured)
amd_repro    = [2  * i for i in range(11)]      # +2/cycle  (measured)
intel_repro  = [0  * i for i in range(11)]      # +0/cycle  (measured)

# Panel 2 — Production app (GStreamer + WPF, 6 cycles before crash on NVIDIA)
prod_cycles  = [0, 1, 2, 3, 4, 5]
nvidia_prod  = [19, 414, 870, 870, 1102, 1336]
intel_prod   = [71, 78, 82, 85, 90, 95]         # from tablet logs

# ── Figure ──────────────────────────────────────────────────────────────────
fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(15, 6))
fig.patch.set_facecolor(BG)

for ax in (ax1, ax2):
    ax.set_facecolor(BG)
    ax.tick_params(colors=TEXT, labelsize=10)
    ax.spines["bottom"].set_color(GRID)
    ax.spines["left"].set_color(GRID)
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)
    ax.grid(axis="y", color=GRID, linewidth=0.8, linestyle="--")
    ax.grid(axis="x", color=GRID, linewidth=0.5, linestyle=":")
    ax.set_xlabel("Playback cycle", color=TEXT, fontsize=11, labelpad=8)
    ax.set_ylabel("Thread count accumulated", color=TEXT, fontsize=11, labelpad=8)
    ax.title.set_color(TEXT)

# ── Panel 1: Raw D3D11 repro ─────────────────────────────────────────────────
ax1.set_title("Experiment — Raw D3D11 repro (no GStreamer)\nDangling ref: one extra AddRef() per cycle", fontsize=12, pad=12)

ax1.plot(repro_cycles, nvidia_repro, color=NVIDIA_GREEN, linewidth=2.5,
         marker="o", markersize=6, label="NVIDIA RTX 3050  (+17/cycle)")
ax1.plot(repro_cycles, amd_repro,   color=AMD_RED,      linewidth=2.5,
         marker="^", markersize=6, label="AMD Radeon iGPU  (+2/cycle)")
ax1.plot(repro_cycles, intel_repro, color=INTEL_BLUE,   linewidth=2.5,
         marker="s", markersize=6, label="Intel iGPU  (+0/cycle)")

ax1.annotate("+17 threads\nper stranded device",
             xy=(5, nvidia_repro[5]), xytext=(5.8, nvidia_repro[5] - 30),
             color=NVIDIA_GREEN, fontsize=9,
             arrowprops=dict(arrowstyle="->", color=NVIDIA_GREEN, lw=1.2))

ax1.annotate("+2/cycle",
             xy=(8, amd_repro[8]), xytext=(6.2, amd_repro[8] + 10),
             color=AMD_RED, fontsize=9,
             arrowprops=dict(arrowstyle="->", color=AMD_RED, lw=1.0))

ax1.set_xticks(repro_cycles)
ax1.set_ylim(-5, max(nvidia_repro) * 1.2)
ax1.legend(facecolor="#1a1a1a", edgecolor=GRID, labelcolor=TEXT, fontsize=10)

# ── Panel 2: Production app ──────────────────────────────────────────────────
ax2.set_title("Production app — GStreamer + WPF\nNVIDIA RTX vs Intel iGPU (same binary)", fontsize=12, pad=12)

ax2.plot(prod_cycles, nvidia_prod, color=NVIDIA_GREEN, linewidth=2.5,
         marker="o", markersize=6, label="NVIDIA RTX  (~230/cycle)")
ax2.plot(prod_cycles, intel_prod,  color=INTEL_BLUE,   linewidth=2.5,
         marker="s", markersize=6, label="Intel iGPU  (~5/cycle)")

# Crash annotation
crash_y = 1336
ax2.annotate("CRASH\n(cycle 6)",
             xy=(5.85, crash_y), xytext=(4.2, crash_y + 40),
             color=CRASH_RED, fontsize=10, fontweight="bold",
             arrowprops=dict(arrowstyle="->", color=CRASH_RED, lw=1.5))
ax2.scatter([5], [crash_y], color=CRASH_RED, s=120, zorder=5, marker="X")

# Thread limit line
limit = 1500
ax2.axhline(limit, color=CRASH_RED, linewidth=1, linestyle="--", alpha=0.5)
ax2.text(0.1, limit + 20, "OS thread limit", color=CRASH_RED, fontsize=8, alpha=0.8)

ax2.set_xticks(list(range(7)))
ax2.set_xticklabels([str(i) if i < 6 else "6\n(crash)" for i in range(7)])
ax2.get_xticklabels()[-1].set_color(CRASH_RED)
ax2.set_ylim(0, 1650)
ax2.legend(facecolor="#1a1a1a", edgecolor=GRID, labelcolor=TEXT, fontsize=10)

# ── Footer ───────────────────────────────────────────────────────────────────
fig.text(0.5, 0.01,
         "GStreamer cache retains D3D11 device ref  →  NVIDIA: +17 threads/device  |  AMD: +2/device  |  Intel: +0/device",
         ha="center", color=SUBTEXT, fontsize=9)

plt.tight_layout(rect=[0, 0.04, 1, 1])
plt.savefig("thread_leak_chart.png", dpi=180, bbox_inches="tight",
            facecolor=BG, edgecolor="none")
print("Saved: thread_leak_chart.png")
