# GameBoy Emulator

A Game Boy (DMG) emulator written in C# targeting .NET 9.0, using SDL2 for cross-platform graphics, audio, and input.

> **Note:** This is a DMG emulator. CGB-only ROMs are detected from the cartridge header and skipped.

## Features

### CPU
- Full LR35902 instruction set including all CB-prefixed opcodes
- Accurate interrupt handling (VBlank, LCD STAT, Timer, Serial, Joypad)
- HALT and HALT bug emulation

### PPU (Pixel Processing Unit)
- Scanline-based renderer at native 160×144 resolution, presented at 2× scale in the game viewport
- Background and Window layer rendering with per-pixel scrolling
- Sprite rendering with 8×8 and 8×16 modes
- LCDC bit 0 (BG enable) support
- Correct sprite priority (lower X coordinate wins; same X → lower OAM index wins)
- 8×16 sprite tile index bit 0 masking
- Window internal line counter for accurate mid-frame window toggling
- OAM DMA transfer
- Passes the **dmg-acid2** test ROM

### APU (Audio Processing Unit)
- All four sound channels:
  - Channel 1 — Square wave with frequency sweep
  - Channel 2 — Square wave
  - Channel 3 — Programmable wave
  - Channel 4 — Noise (LFSR)
- Frame sequencer clocked at 512 Hz (length, envelope, sweep)
- Stereo mixing with NR50 master volume and NR51 panning
- 44100 Hz stereo float32 output via SDL2 audio queue

### Memory (MMU)
- Full 64 KB address space
- Automatic mapper detection from ROM header (0x0147/0x0148/0x0149)
- MBC1 cartridge mapper with ROM/RAM banking and advanced mode
- MBC2 cartridge mapper with built-in 4-bit RAM
- MBC3 cartridge mapper with ROM/RAM banking and RTC register support
- MBC5 cartridge mapper with 9-bit ROM bank select (up to 512 banks)
- Battery-backed save support — automatically detects battery cartridges and persists game saves to `.sav` files alongside the ROM
- Boot ROM (`dmg_boot.bin`) execution with automatic hand-off to cartridge

### Input
- Keyboard-mapped joypad with proper hardware polling emulation

### Display & UI
- Cross-platform SDL2 window with nearest-neighbour scaling
- Native 160×144 framebuffer rendered at 2× scale in the game viewport
- Built-in ROM selection menu with a 5×7 pixel bitmap font
- Automatic ROM scanning from the `ROMs/` directory
- Menu scrolling (up to 10 visible entries)
- Hold Up/Down to repeat navigation while browsing ROMs
- Press A-Z or 0-9 to jump to the first ROM starting with that character
- Press Ctrl+Enter in the ROM menu to launch a game without the boot ROM
- ROM menu position is preserved when returning from a game
- Long highlighted ROM names (>21 chars) marquee after a short delay, then reset when no longer highlighted

### Compatibility Notes
- DMG-focused emulation: best compatibility is with original Game Boy (non-color) titles.
- CGB-only ROMs (header flag `0xC0`) are skipped at launch.
- CGB-compatible ROMs (header flag `0x80`) may boot but are not guaranteed to behave correctly in DMG mode.

## Known Limitations

- This project currently targets DMG hardware behavior, not full Game Boy Color (CGB) hardware features.
- Timing-sensitive edge cases may still exist in specific test ROMs and in a small number of game scenes.
- Audio emulation is functional for most games, but some hardware-quirk-level APU behavior is still being refined.
- SDL2 must be available on the host system at runtime; missing SDL2 libraries will prevent startup.

## Controls

| Key | Action |
|-----|--------|
| Arrow Keys / WASD | D-Pad |
| Z / M | A |
| X / N | B |
| Enter | Start |
| Space | Select |
| Escape | Reset (return to ROM menu) |

### ROM Menu Controls

| Key | Action |
|-----|--------|
| Up / Down | Move selection (hold to repeat) |
| A-Z / 0-9 | Jump to first ROM beginning with that character |
| Enter | Launch highlighted ROM |
| Ctrl+Enter | Launch highlighted ROM without boot ROM |
| Escape | Quit emulator |

> **Tip:** The WASD alternate keys are useful for games like Pinball Dreams where the arrow keys may feel less natural.

## Requirements

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [SDL2](https://www.libsdl.org/) runtime library installed on your system
- `dmg_boot.bin` — Game Boy boot ROM (optional). If present, the Nintendo logo scroll animation plays on startup. If absent, games start directly. **Not included** due to copyright — you must supply your own.

## Building & Running

```bash
dotnet build GameboyEmu.csproj
dotnet run --project GameboyEmu.csproj
```

Place `.gb` ROM files in a `ROMs/` folder in the project root. The emulator will display a selection menu on launch. Subfolders under `ROMs/` are supported and will appear as navigable directories in the menu.

To use the boot ROM, place `dmg_boot.bin` in the project root directory (the same folder as `GameboyEmu.csproj`).

### Command-Line Options

| Option | Description |
|--------|-------------|
| `path/to/game.gb` | Launch a specific ROM directly, bypassing the menu |
| `--rompath <path>` | Override the ROM source — accepts a `.gb` file or a directory of ROMs |
| `--nobootrom` | Skip the boot ROM animation even when `dmg_boot.bin` is present |

Options can be combined freely:

```bash
# Launch a specific ROM
dotnet run --project GameboyEmu.csproj path/to/game.gb

# Open the ROM menu pointing at a different folder (e.g. a test ROM suite)
dotnet run --project GameboyEmu.csproj --rompath path/to/tests

# Launch a specific ROM from a custom path, skipping the boot ROM
dotnet run --project GameboyEmu.csproj --rompath path/to/game.gb --nobootrom
```

## Project Structure

```
Core/
  CPU.cs          - LR35902 CPU with full instruction set
  MMU.cs          - Memory management unit with MBC1/MBC2/MBC3/MBC5 support
  PPU.cs          - Pixel Processing Unit (scanline renderer)
  APU.cs          - Audio Processing Unit (4 channels)
  GameBoy.cs      - Main emulator loop tying all components together
  SDLDisplay.cs   - SDL2 window, rendering, input, and ROM menu
  SDL2Bindings.cs - SDL2 P/Invoke declarations
  Registers.cs    - CPU register definitions
  Flags.cs        - CPU flag helpers
Program.cs        - Entry point and ROM loading flow
```

## Disclaimer

Game Boy is a registered trademark of Nintendo Co., Ltd. This project is not affiliated with or endorsed by Nintendo. All trademarks are property of their respective owners.

## License

MIT License. See [LICENSE](LICENSE).

## Screenshots

<table>
  <tr>
    <td><img src="ss1.png" alt="Screenshot 1" /></td>
    <td><img src="ss2.png" alt="Screenshot 2" /></td>
  </tr>
  <tr>
    <td><img src="ss3.png" alt="Screenshot 3" /></td>
    <td><img src="ss4.png" alt="Screenshot 4" /></td>
  </tr>
</table>