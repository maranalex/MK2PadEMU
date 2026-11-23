# M&K2PadEMU

**Play PS5 games with a mouse and keyboard.**

> **PS5 only.** This is the first iteration of the project.

M&K2PadEMU turns your mouse and keyboard into a virtual DualShock 4 controller, so you can use M&K in PS5 games that don't support it. It also supports **hot-swappable profiles**, so you can change your whole control scheme in the middle of a game with one key press.

The game stays on the console's own HDMI output. Only your *inputs* travel to the PS5, over Remote Play, so you don't watch a compressed stream.

## How it works

The app creates a virtual controller on your PC (through the ViGEmBus driver). [chiaki-ng](https://github.com/streetpea/chiaki-ng), a Remote Play client, reads that controller and sends its input to the console.

> **Use chiaki-ng, not Sony's official PS Remote Play.** The official app stops working as soon as it detects ViGEmBus.

## Features

- **Hot-swap profiles.** Keep one profile per situation and switch between them without leaving the game.
- **Complex profiles (orchestrators).** A complex profile holds no bindings of its own. It only maps hotkeys to your regular profiles, for example `F1` to the on-foot profile and `F2` to the vehicle profile. Select one and the hotkeys work while capture is on.
- **Make your own.** Create new profiles and complex profiles, copy existing ones, and name them whatever you like.
- **Flexible mapping.** Bind a PS5 button to a key *or* a combination of keys, and add as many alternative keys as you want for the same button.
- **Macros.** Press several PS5 buttons at once with a single key or key combination.
- **Movement styles.** Full tilt, speed keys (normal and slow walk, plus a sprint modifier) or ramp-up, so you can walk, jog and run without an analog stick.
- **Mouse aim tuning.** Sensitivity per axis, deadzone offset, aim smoothing and invert Y. The aim reads the raw mouse input, so Windows pointer acceleration doesn't affect it.
- **Swappable sticks.** Aim on the left stick (left-handed layout) or on the right stick (standard layout).
- **Built-in profiles you can always restore.** The three bundled profiles can be reverted to their original settings at any time.

## Included profiles

| Profile | Type | Purpose |
|---|---|---|
| **GTA6-Foot** | Regular | Controls for playing on foot |
| **GTA6-Vehicle** | Regular | Controls for cars, planes and other vehicles |
| **GTA-switcher** | Complex | `F1` switches to GTA6-Foot, `F2` switches to GTA6-Vehicle |

- The included profiles use the **left-handed controller preset** (aim on the left stick, movement on the right). Visually it makes the quick actions you run into in the game easier to follow.
- The profiles are split between on foot and vehicle so that the layout feels like how a PC game is normally mapped.
- **The one inconvenience:** you have to switch profiles yourself. When you are on foot, make sure you pressed `F1`. When you get into a car or plane, press `F2`.

If you change a bundled profile and want the original back, select it and press **Revert to original**. If you deleted one, press **Restore built-ins**.

## Requirements

- Windows 10 or 11, 64-bit (the glass look needs Windows 11)
- A PS5 with **Remote Play enabled** (Settings, System, Remote Play)
- [ViGEmBus](https://github.com/nefarius/ViGEmBus) driver
- [chiaki-ng](https://github.com/streetpea/chiaki-ng), linked to your PS5

```
winget install --id ViGEm.ViGEmBus -e
winget install --id StreetPea.chiaki-ng -e
```

Reboot after installing ViGEmBus.

## Usage

1. Start **chiaki-ng** and connect to your PS5.
2. Start **M&K2PadEMU**.
3. Click the chiaki-ng window, then press **`F8`**. Your mouse and keyboard now drive the virtual controller.
4. Play on the console's HDMI output, not in the chiaki-ng window.

| Key | Action |
|---|---|
| `F8` | Turn capture on or off (gives you the mouse back) |
| `F9` | Quit |

Your profiles are saved automatically to `config.json`, next to the exe. Put the exe in a folder you can write to, not Program Files.

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

```
dotnet build -c Release
```

To produce a single self-contained exe:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

`dotnet run -c Release -- --test` runs the built-in self-checks.

## Notes

- The exe is unsigned, so Windows SmartScreen may warn about an unknown publisher (**More info, Run anyway**). Some antivirus tools may also flag it because it uses global mouse and keyboard hooks. That is expected.
- Aiming with a mouse through a virtual controller can count as cheating in online games. Use it in single-player modes.

## Support the project

If M&K2PadEMU is useful to you, a coffee is very appreciated. The download and updates are on Patreon:

**[patreon.com/SS3MRN: M&K2PadEMU](https://www.patreon.com/SS3MRN/posts/m-k2pademu-176146837)**
