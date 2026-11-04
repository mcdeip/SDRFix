# SDRFix

**Correct gamma for SDR content in Windows HDR mode, at every SDR brightness level.**

When HDR is on, Windows makes SDR apps, the desktop, videos and games look washed out: blacks
turn gray and shadows lose depth. SDRFix is a small tray app that fixes this. It gives Windows
a matching gamma-correction profile for every position of the *SDR content brightness* slider
and switches between them for you.

<p align="center"><img src="docs/images/slider.png" width="360" alt="The SDRFix slider: SDR content brightness at 30 (200 nits), gamma 2.2 selected, HDR content profile off"></p>

**[Download SDRFix.exe](https://github.com/mcdeip/SDRFix/releases/latest/download/SDRFix.exe)**.
It's a single portable file with no installer and needs no admin rights.

---

## Why SDR looks wrong in HDR mode

In HDR mode Windows has to translate every SDR pixel into an absolute brightness. It does that
with the **piecewise sRGB curve**: a short straight line near black, then a 2.4 power curve,
scaled so that white lands on your *SDR content brightness* setting.

Almost all SDR content is made and checked on displays that follow a **pure power curve**:
about gamma 2.2 for PC, web and game content, and gamma 2.4 (BT.1886) for films and TV graded
in a dark room. The two curves agree in the bright half of the range, but they split apart in
the shadows. The straight segment of the sRGB curve makes near-black much brighter than the
content was graded for:

<p align="center"><img src="docs/images/gamma-chart.png" width="820" alt="Line chart of luminance in nits against 8-bit SDR code value 0 to 48 at 200-nit SDR white. Windows' sRGB decode sits well above gamma 2.2 and 2.4 in the shadows: at code 13 it is 0.80 nits versus 0.29 for gamma 2.2."></p>

| SDR code value | Windows (sRGB) | Gamma 2.2 | Gamma 2.4 | Windows vs 2.2 |
|---:|---:|---:|---:|---:|
| 5 (2%) | 0.30 nits | 0.035 nits | 0.016 nits | 8.7× brighter |
| 13 (5%) | 0.80 nits | 0.29 nits | 0.16 nits | 2.8× brighter |
| 26 (10%) | 2.07 nits | 1.32 nits | 0.83 nits | 1.6× brighter |
| 51 (20%) | 6.6 nits | 5.8 nits | 4.2 nits | 1.1× brighter |
| 128 (50%) | 43 nits | 44 nits | 38 nits | about the same |
| 255 (100%) | 200 nits | 200 nits | 200 nits | the same |

*Values at 200-nit SDR white (slider at 30). The ratios are the same at every brightness level.*

On an OLED or a mini-LED display with deep blacks, this raised floor is easy to see. Dark
scenes look foggy, letterboxing looks gray, and dark UI themes lose contrast.

## How SDRFix fixes it

Windows supports a special kind of color profile for HDR mode: an ICC profile with a Microsoft
**MHC2** tag. This tag holds a lookup table that Windows' display pipeline applies to the whole
screen. SDRFix uses it to turn Windows' sRGB decode back into a pure power curve:

- **One profile per slider step.** The right correction depends on where SDR white sits. A
  curve built for 200 nits is the wrong shape at 300 nits. Setup creates a profile for every
  step from 0 to 100 (80 to 480 nits), for both gamma 2.2 and gamma 2.4.
- **Switched automatically.** Whenever the brightness changes, from the slider, a shortcut or
  the tray menu, SDRFix sets Windows' SDR brightness *and* makes the matching profile the
  display's default. They never get out of step.
- **Only SDR shadows change.** The correction is strongest near black. It fades out smoothly
  towards SDR white, and everything brighter than SDR white passes through unchanged, so HDR
  highlights stay as they are.
- **The proven curve.** The lookup tables match the "Pure Power" profiles from
  [ColorControl](https://github.com/Maassoft/ColorControl) value for value. All 1024 entries
  were checked against ColorControl's profiles for the same display. The profiles are also
  tagged with your display's own color primaries, read from its EDID.
- **Nothing runs per frame.** Windows applies the profile in its display pipeline, so it works
  in every app, browser, video player and game (windowed or full-screen). Nothing is injected,
  and there's no performance cost. SDRFix itself only wakes up when you change something.

## Compared with other fixes

| Approach | Works everywhere | Follows the brightness slider | Leaves HDR content alone | Catch |
|---|:---:|:---:|:---:|---|
| **SDRFix** | ✅ | ✅ | ✅ | Needs HDR on (that's the point) |
| A single hand-made MHC2 profile (ColorControl, dantmnf's tools) | ✅ | ❌ | ✅ | Correct at one SDR brightness only. Change the slider and you have to make and apply a new profile. |
| Do nothing | ✅ | n/a | ✅ | Raised blacks, washed-out shadows |
| Per-game shaders and injectors (ReShade, Special K) | ❌ | Manual | Depends | Set up per game, the injection can trip anti-cheat, and it doesn't help the desktop or video |
| SDR-to-HDR conversion (Windows Auto HDR, NVIDIA RTX HDR) | ❌ | n/a | n/a | Games (and some video) only. It *expands* SDR into HDR, which changes the look instead of showing it as made, and RTX HDR costs GPU time. |
| Turn HDR off for SDR content | ✅ | n/a | n/a | No HDR at all while it's off, and switching blanks the screen for a moment |

SDRFix takes the most accurate approach, a display-pipeline MHC2 correction, and fixes its one
weakness: a single profile is only right at one brightness level.

---

## Features

### Brightness slider

Left-click the tray icon to open the slider. It works like Windows' own *SDR content brightness*
slider, but it shows the resulting SDR white level in nits and snaps to steps of 5, one per
profile. Brightness follows the slider live while you drag, and the matching profile is applied
as soon as you let go.

- **Mouse wheel** or **arrow keys**: one step. **Page Up / Page Down**: five steps. **Home / End**:
  0 or 100.
- **Enter** applies and closes. **Esc** closes.
- The status line confirms which profile is active. If something else changed the brightness, for
  example Windows Settings set an in-between value like 33, the status line says so. Click it to
  snap back to a matching step.

### Gamma 2.2 or 2.4

Choose the curve in the slider, press **G** while the slider is open, or use **Gamma** in the
tray menu. Use **2.2** for most PC content and a lit room, and **2.4** for films in a dark room.
Both sets are created by setup, so switching is instant.

If you never switch, hide the toggle with *Gamma → Show toggle in slider* in the tray menu:

<p align="center"><img src="docs/images/slider-no-gamma.png" width="360" alt="The slider without the gamma row"></p>

### HDR content profile

For HDR games and HDR video you don't want an SDR gamma correction. Turn on **HDR content** in
the slider (or press **Space** in the slider, or use the shortcut) to switch to a plain HDR
profile with no gamma curve. It only declares your display's brightness range, from 0 to the
peak you entered in setup. The brightness slider still works while it's on. Turn it off again
to go back to the gamma profile for the current step.

<p align="center"><img src="docs/images/slider-hdr.png" width="360" alt="The slider with the HDR content profile on"></p>

Windows reports this peak to HDR games and apps as the display's maximum brightness, just like a
profile from the Windows HDR Calibration app.

### Setup

Setup runs on first launch, and you can run it again any time from **Set up gamma profiles…**
in the tray menu. Pick the gamma to start with and enter your display's peak HDR brightness.
Setup then creates all 43 profiles in a second or two: 21 steps × 2 gammas, plus the HDR
profile. Running it again (say, with a different peak) overwrites them in place, and SDRFix
re-applies the active one.

<p align="center"><img src="docs/images/setup.png" width="462" alt="SDRFix setup: gamma 2.2 or 2.4, peak brightness 1300 nits, display AW3926QW with color primaries from EDID"></p>

For the peak, use your display's real HDR peak, for example from a review, your display's
specifications, or the Windows HDR Calibration app.

### Keyboard shortcuts

Change SDR brightness from inside a full-screen game without alt-tabbing. These shortcuts work
in any app:

| Action | Default |
|---|---|
| Brightness down | `Alt` + `Shift` + `F1` |
| Brightness up | `Alt` + `Shift` + `F2` |
| HDR content profile on/off | `Alt` + `Shift` + `F3` |

To change them, open **Keyboard shortcuts…** in the tray menu, click a shortcut and press the new
keys. **Backspace** clears a shortcut. Keys that type something (letters, digits, arrows) need at
least one modifier, but function keys and media keys can be used alone. **Step per press** sets
how far each press moves the slider: 5 (one profile step, the default), 10, 15, 20 or 25.

<p align="center"><img src="docs/images/shortcuts.png" width="482" alt="Keyboard shortcuts window with the three shortcuts and the step-per-press picker"></p>

If another app already owns a shortcut, Windows won't give it to SDRFix. The tray menu then shows
*Keyboard shortcuts… (1 unavailable)* and the window tells you which one, so you can pick
another. SDRFix tries again each time you open the tray menu.

### On-screen indicator

Shortcut presses show a small indicator at the bottom of the screen, like Windows' volume
flyout. It never takes focus and lets clicks pass through, so your game keeps running
undisturbed. Quick repeated presses are combined, so the profile is applied once when you stop.

<p align="center"><img src="docs/images/indicator.png" width="272" alt="Indicator showing SDR brightness 55, 300 nits"></p>

### Tray menu

Right-click the tray icon:

| Item | What it does |
|---|---|
| **Open slider** | Same as left-clicking the icon |
| **SDR brightness** | Every step with its nits value, for example *30 · 200 nits*. Steps without a profile are marked. |
| **Gamma** | *Show toggle in slider*, plus 2.2 and 2.4 |
| **HDR content profile** | On/off, with its shortcut |
| **Display** | Shown only with more than one display. Picks which one SDRFix controls, and marks displays with HDR off or without HDR. |
| **Set up gamma profiles…** | Runs setup again |
| **Keyboard shortcuts…** | Changes the shortcuts and the step per press |
| **Display settings…** | Opens Windows' display settings (turn HDR on there) |
| **Start with Windows** | Starts SDRFix when you sign in |
| **Exit** | Quits. The current brightness and profile stay as they are. |

The tray tooltip shows the current state, for example *SDR brightness 30 (200 nits) · gamma 2.2*.
SDRFix follows Windows' light or dark theme.

### Works with your own profiles too

If you'd rather make profiles yourself (with ColorControl, for example), SDRFix will use those
for displays where setup hasn't been run. Name each profile after its slider step, like `40.icm`,
or give it a description that starts with the number, like *40 (Pure Power 2.2)*. A profile named
`HDR.icc`, or described as *HDR*, becomes the HDR content profile.

---

## SDR brightness and nits

Windows' *SDR content brightness* slider maps to SDR white as **nits = 80 + 4 × slider**:

| Slider | SDR white | Slider | SDR white | Slider | SDR white |
|---:|---:|---:|---:|---:|---:|
| 0 | 80 nits | 35 | 220 nits | 70 | 360 nits |
| 5 | 100 nits | 40 | 240 nits | 75 | 380 nits |
| 10 | 120 nits | 45 | 260 nits | 80 | 400 nits |
| 15 | 140 nits | 50 | 280 nits | 85 | 420 nits |
| 20 | 160 nits | 55 | 300 nits | 90 | 440 nits |
| 25 | 180 nits | 60 | 320 nits | 95 | 460 nits |
| 30 | 200 nits | 65 | 340 nits | 100 | 480 nits |

## Requirements

- Windows 11 with an HDR display and HDR turned on (*Settings → System → Display → Use HDR*).
  It's developed and tested on Windows 11. Windows 10 is untested.
- .NET Framework 4.x, which is already part of Windows 10 and 11.

The exe isn't code-signed, so SmartScreen may warn the first time you run it. Choose
*More info → Run anyway*, or build it yourself from source (see below).

## Files and settings

- **Profiles:** `C:\Windows\System32\spool\drivers\color\SdrBrightness-<display model>-<gamma>-<step>.icm`,
  plus `SdrBrightness-<display model>-HDR.icm`. Profiles are named per display model, so each
  display gets its own set.
- **Settings:** `HKEY_CURRENT_USER\Software\SdrBrightness`
- **Start with Windows:** the `SdrBrightness` value under
  `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`

### Uninstalling

1. Untick **Start with Windows** and choose **Exit**.
2. Delete `SDRFix.exe`.
3. Optional: to remove the profiles, open *Color Management* (`colorcpl`), and on the *Devices*
   tab select your display and remove the `SdrBrightness-…` profiles. Then delete the files from
   the folder above and the settings key.

## Building from source

No SDK needed. Windows already ships the C# compiler that SDRFix is built with.

```bat
build.cmd
```

This produces `SDRFix.exe` next to the script. `tools\GammaChart.cs` draws the chart in this
README.

## Troubleshooting

- **A shortcut doesn't work.** Another app may own it. The NVIDIA App overlay, for example, uses
  `Alt` + `F1`, `Alt` + `F2` and others. Choose a different shortcut in **Keyboard shortcuts…**.
- **"HDR is off".** SDRFix only works in HDR mode. Turn HDR on in **Display settings…**.
- **"Windows is set to 33".** Windows Settings (or another app) set a value between steps. Click
  the status line to snap to the nearest step.
- **"No profile for 35".** There are no profiles for this display and gamma yet. Run
  **Set up gamma profiles…**.
- **Colors look off after changing the peak in setup.** Toggle the **HDR content profile** on and
  off once, so Windows reloads the profile.

## Credits

- The correction curve follows [ColorControl](https://github.com/Maassoft/ColorControl) by
  Maassoft and its "Pure Power" SDR transfer function.
- MHC2 research by [dantmnf](https://github.com/dantmnf/MHC2), which made HDR-mode calibration
  profiles on Windows practical.
