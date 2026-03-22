# GameBoy Emulator

A Game Boy (DMG) emulator written in C# targeting .NET 9.0, using SDL2 for cross-platform graphics, audio, and input.

## Features

### CPU
- Full LR35902 instruction set including all CB-prefixed opcodes
- Accurate interrupt handling (VBlank, LCD STAT, Timer, Serial, Joypad)
- HALT and HALT bug emulation

### PPU (Pixel Processing Unit)
- Scanline-based renderer at native 160×144 resolution, scaled 4× for display
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
- Boot ROM (`dmg_boot.bin`) execution with automatic hand-off to cartridge

### Input
- Keyboard-mapped joypad with proper hardware polling emulation

### Display & UI
- Cross-platform SDL2 window with nearest-neighbour scaling
- Built-in ROM selection menu with a 5×7 pixel bitmap font
- Automatic ROM scanning from the `ROMs/` directory
- Menu scrolling (up to 10 visible entries)

## Controls

| Key | Action |
|-----|--------|
| W/A/S/D | D-Pad (Up/Left/Down/Right) |
| M | A |
| N | B |
| Enter | Start |
| Space | Select |
| Escape | Reset (return to ROM menu) |

## Requirements

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [SDL2](https://www.libsdl.org/) runtime library installed on your system
- `dmg_boot.bin` — Game Boy boot ROM (optional). If present, the Nintendo logo scroll animation plays on startup. If absent, games start directly. **Not included** due to copyright — you must supply your own.

## Building & Running

```bash
dotnet build GameboyEmu.csproj
dotnet run --project GameboyEmu.csproj
```

Place `.gb` ROM files in a `ROMs/` folder in the project root. The emulator will display a selection menu on launch.

To use the boot ROM, place `dmg_boot.bin` in the project root directory (the same folder as `GameboyEmu.csproj`).

You can also pass a ROM path directly:

```bash
dotnet run --project GameboyEmu.csproj -- path/to/game.gb
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

This project is for educational purposes.
