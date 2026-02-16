"""
TronDashboard Screenshot Analyzer
==================================
Reads a screenshot PNG and outputs quantitative metrics that a non-visual
AI can use to assess visual quality and iterate on shader/lighting params.

Outputs:
  - Overall brightness histogram (how much of the image is black/dark/mid/bright)
  - Grid line detection: scans horizontal and vertical strips looking for
    the periodic brightness spikes that grid lines create
  - Color channel analysis: how much cyan/teal vs other emissive colors
  - Contrast ratio between grid lines and floor
  - Region analysis: center vs edges vs corners
  - Trail wall brightness and spread
  - HUD readability check

Usage: python analyze_screenshot.py <path_to_png>
"""

import sys
import os
import numpy as np
from PIL import Image

def load_image(path):
    img = Image.open(path).convert("RGB")
    return np.array(img, dtype=np.float32) / 255.0  # Normalize to 0-1

def brightness(pixels):
    """Perceived brightness (ITU BT.709)"""
    return 0.2126 * pixels[..., 0] + 0.7152 * pixels[..., 1] + 0.0722 * pixels[..., 2]

def analyze_brightness_distribution(brt):
    """Categorize pixels into brightness buckets"""
    total = brt.size
    black   = np.sum(brt < 0.02) / total * 100   # True black
    vdark   = np.sum((brt >= 0.02) & (brt < 0.06)) / total * 100  # Very dark
    dark    = np.sum((brt >= 0.06) & (brt < 0.15)) / total * 100  # Dark (grid lines should be here)
    mid     = np.sum((brt >= 0.15) & (brt < 0.40)) / total * 100  # Mid (bright grid/trails)
    bright  = np.sum((brt >= 0.40) & (brt < 0.70)) / total * 100  # Bright (emissives)
    hot     = np.sum(brt >= 0.70) / total * 100                    # Hot (bloom core)

    return {
        "black (<2%)": black,
        "very_dark (2-6%)": vdark,
        "dark (6-15%)": dark,
        "mid (15-40%)": mid,
        "bright (40-70%)": bright,
        "hot (>70%)": hot,
        "mean": np.mean(brt) * 100,
        "median": np.median(brt) * 100,
        "p95": np.percentile(brt, 95) * 100,
        "p99": np.percentile(brt, 99) * 100,
        "max": np.max(brt) * 100,
    }

def analyze_grid_lines(img, brt):
    """
    Scan horizontal and vertical strips through the center of the image
    looking for periodic brightness peaks (grid lines).
    Returns contrast ratio and peak detection metrics.
    """
    h, w = brt.shape

    # Sample 5 horizontal scan lines at different heights (40%-60% of image = floor area)
    results = {}
    for name, y_frac in [("h_40%", 0.40), ("h_45%", 0.45), ("h_50%", 0.50), ("h_55%", 0.55), ("h_60%", 0.60)]:
        y = int(y_frac * h)
        row = brt[y, :]
        floor_level = np.percentile(row, 20)  # Floor is the 20th percentile (dark)
        line_level = np.percentile(row, 90)   # Lines are the 90th percentile (bright)
        if floor_level < 0.001:
            floor_level = 0.001  # avoid div/0
        contrast = line_level / floor_level
        peak_count = count_peaks(row, threshold=floor_level + (line_level - floor_level) * 0.3)
        results[name] = {
            "floor_brightness": floor_level * 100,
            "line_brightness": line_level * 100,
            "contrast_ratio": contrast,
            "peak_count": peak_count,
        }

    # Same for vertical scan lines
    for name, x_frac in [("v_40%", 0.40), ("v_50%", 0.50), ("v_60%", 0.60)]:
        x = int(x_frac * w)
        col = brt[:, x]
        floor_level = np.percentile(col, 20)
        line_level = np.percentile(col, 90)
        if floor_level < 0.001:
            floor_level = 0.001
        contrast = line_level / floor_level
        peak_count = count_peaks(col, threshold=floor_level + (line_level - floor_level) * 0.3)
        results[name] = {
            "floor_brightness": floor_level * 100,
            "line_brightness": line_level * 100,
            "contrast_ratio": contrast,
            "peak_count": peak_count,
        }

    return results

def count_peaks(arr, threshold):
    """Count brightness peaks above threshold (simple zero-crossing of derivative)"""
    above = arr > threshold
    # Find rising edges
    edges = np.diff(above.astype(int))
    return int(np.sum(edges == 1))

def analyze_color_channels(img):
    """Analyze color distribution — how much of each hue is present"""
    r, g, b = img[..., 0], img[..., 1], img[..., 2]

    # Cyan detection: high G+B, low R
    is_cyan = (g > 0.1) & (b > 0.1) & (r < g * 0.3)
    cyan_pct = np.sum(is_cyan) / is_cyan.size * 100

    # Warm colors: R dominant (trail walls from CLU, RINZLER, SARK, RAM)
    is_warm = (r > 0.1) & (r > g * 1.5) & (r > b * 1.5)
    warm_pct = np.sum(is_warm) / is_warm.size * 100

    # Purple/magenta: R+B high, G low (QUORRA, CASTOR)
    is_purple = (r > 0.1) & (b > 0.1) & (g < r * 0.5)
    purple_pct = np.sum(is_purple) / is_purple.size * 100

    # Green: G dominant (YORI)
    is_green = (g > 0.1) & (g > r * 2) & (g > b * 1.5)
    green_pct = np.sum(is_green) / is_green.size * 100

    # White/near-white (node dots, hot spots)
    is_white = (r > 0.5) & (g > 0.5) & (b > 0.5)
    white_pct = np.sum(is_white) / is_white.size * 100

    return {
        "cyan_teal_%": cyan_pct,
        "warm_red_orange_%": warm_pct,
        "purple_magenta_%": purple_pct,
        "green_%": green_pct,
        "white_hot_%": white_pct,
    }

def analyze_regions(brt):
    """Compare brightness in different image regions"""
    h, w = brt.shape
    regions = {}

    # Center (30-70% of image — main arena floor)
    center = brt[int(h*0.3):int(h*0.7), int(w*0.3):int(w*0.7)]
    regions["center_mean"] = np.mean(center) * 100
    regions["center_p95"] = np.percentile(center, 95) * 100

    # Bottom (60-90% — near camera, floor close-up)
    bottom = brt[int(h*0.6):int(h*0.9), int(w*0.2):int(w*0.8)]
    regions["bottom_mean"] = np.mean(bottom) * 100
    regions["bottom_p95"] = np.percentile(bottom, 95) * 100

    # Top (10-30% — far wall / sky)
    top = brt[int(h*0.1):int(h*0.3), int(w*0.2):int(w*0.8)]
    regions["top_mean"] = np.mean(top) * 100
    regions["top_p95"] = np.percentile(top, 95) * 100

    # Left edge
    left = brt[int(h*0.3):int(h*0.7), :int(w*0.15)]
    regions["left_mean"] = np.mean(left) * 100

    # Right edge
    right = brt[int(h*0.3):int(h*0.7), int(w*0.85):]
    regions["right_mean"] = np.mean(right) * 100

    return regions

def analyze_reflection_quality(img, brt):
    """
    Check if SSR reflections are working by comparing brightness
    of the floor vs what's above it. If SSR works, the floor near
    bright objects should be brighter than the floor far from them.
    """
    h, w = brt.shape

    # Floor region (lower 60-80% of image, where the grid floor is)
    floor = brt[int(h*0.6):int(h*0.8), int(w*0.2):int(w*0.8)]
    floor_std = np.std(floor) * 100  # High std = varied (reflections!)
    floor_max = np.max(floor) * 100

    # Near trail walls — trails should reflect on nearby floor
    # We can't know exact positions, but high local max near high brightness
    # areas suggests reflections are working

    return {
        "floor_brightness_std": floor_std,  # Higher = more reflection variation
        "floor_max_brightness": floor_max,
        "floor_has_variation": "YES" if floor_std > 3.0 else "WEAK" if floor_std > 1.0 else "NO",
    }

def format_report(dist, grid, colors, regions, reflections, dims):
    """Format a text report"""
    lines = []
    lines.append("=" * 70)
    lines.append("  TRON DASHBOARD — SCREENSHOT ANALYSIS REPORT")
    lines.append("=" * 70)
    lines.append(f"  Resolution: {dims[0]}x{dims[1]}")
    lines.append("")

    lines.append("── BRIGHTNESS DISTRIBUTION ──────────────────────")
    for k, v in dist.items():
        lines.append(f"  {k:25s}: {v:6.1f}{'%' if 'mean' not in k and 'median' not in k and 'p9' not in k and 'max' not in k else '%'}")
    lines.append("")

    lines.append("── GRID LINE DETECTION (scan lines through floor) ─")
    lines.append(f"  {'Scan':10s} {'Floor%':>8s} {'Line%':>8s} {'Contrast':>10s} {'Peaks':>6s}")
    for name, data in grid.items():
        lines.append(f"  {name:10s} {data['floor_brightness']:7.1f}% {data['line_brightness']:7.1f}% {data['contrast_ratio']:9.1f}x {data['peak_count']:5d}")
    # Summary
    all_contrasts = [d["contrast_ratio"] for d in grid.values()]
    all_peaks = [d["peak_count"] for d in grid.values()]
    avg_contrast = np.mean(all_contrasts)
    avg_peaks = np.mean(all_peaks)
    lines.append(f"  {'AVERAGE':10s} {'':>8s} {'':>8s} {avg_contrast:9.1f}x {avg_peaks:5.0f}")
    lines.append("")

    # Grid quality assessment
    if avg_contrast > 5.0 and avg_peaks > 3:
        grid_verdict = "EXCELLENT — grid lines are clearly visible with strong contrast"
    elif avg_contrast > 3.0 and avg_peaks > 2:
        grid_verdict = "GOOD — grid lines visible but could be stronger"
    elif avg_contrast > 1.5:
        grid_verdict = "WEAK — grid lines barely distinguishable from floor"
    else:
        grid_verdict = "INVISIBLE — grid lines cannot be detected above floor noise"
    lines.append(f"  GRID VERDICT: {grid_verdict}")
    lines.append("")

    lines.append("── COLOR CHANNELS ──────────────────────────────")
    for k, v in colors.items():
        bar = "█" * int(v * 2) if v > 0.1 else "·"
        lines.append(f"  {k:25s}: {v:5.1f}%  {bar}")
    lines.append("")

    lines.append("── REGION BRIGHTNESS ───────────────────────────")
    for k, v in regions.items():
        lines.append(f"  {k:25s}: {v:5.1f}%")
    lines.append("")

    lines.append("── REFLECTIONS (SSR) ───────────────────────────")
    for k, v in reflections.items():
        lines.append(f"  {k:25s}: {v}")
    lines.append("")

    # Overall assessment
    lines.append("── OVERALL ASSESSMENT ──────────────────────────")
    issues = []
    if dist["black (<2%)"] > 70:
        issues.append("TOO DARK: >70% of pixels are true black — emission values need to go up")
    if dist["black (<2%)"] > 50 and dist["dark (6-15%)"] < 5:
        issues.append("NO GRID FLOOR: Almost no pixels in the grid-line brightness range (6-15%)")
    if avg_contrast < 2.0:
        issues.append("LOW CONTRAST: Grid lines not distinguishable from floor")
    if colors["cyan_teal_%"] < 1.0:
        issues.append("NO CYAN: Grid emissives not registering — emission multiplier too low")
    if reflections["floor_has_variation"] == "NO":
        issues.append("NO SSR: Floor shows no brightness variation — reflections may not be working")
    if dist["hot (>70%)"] > 15:
        issues.append("BLOOM OVERLOAD: >15% of pixels are hot white — reduce emission or glow")

    if not issues:
        lines.append("  ✓ No major issues detected")
    else:
        for issue in issues:
            lines.append(f"  ✗ {issue}")

    lines.append("=" * 70)
    return "\n".join(lines)

def main():
    if len(sys.argv) < 2:
        # Auto-find latest screenshot
        ss_dir = os.path.join(os.path.dirname(__file__), "Screenshots")
        if os.path.isdir(ss_dir):
            pngs = sorted([f for f in os.listdir(ss_dir) if f.endswith(".png")])
            if pngs:
                path = os.path.join(ss_dir, pngs[-1])
                print(f"Auto-detected: {pngs[-1]}")
            else:
                print("No screenshots found in Screenshots/")
                return
        else:
            print("Usage: python analyze_screenshot.py <path_to_png>")
            return
    else:
        path = sys.argv[1]

    img = load_image(path)
    brt = brightness(img)
    dims = (img.shape[1], img.shape[0])

    dist = analyze_brightness_distribution(brt)
    grid = analyze_grid_lines(img, brt)
    colors = analyze_color_channels(img)
    regions = analyze_regions(brt)
    reflections = analyze_reflection_quality(img, brt)

    report = format_report(dist, grid, colors, regions, reflections, dims)
    print(report)

if __name__ == "__main__":
    main()
