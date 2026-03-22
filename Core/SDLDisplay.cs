// ============================================================================
// Project:     GameboyEmu
// File:        Core/SDLDisplay.cs
// Description: SDL2 display window, frame rendering, keyboard input,
//              ROM selection menu, and built-in 5×7 bitmap font
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

#nullable enable

namespace GameboyEmu.Core
{
    /// <summary>
    /// Cross-platform SDL2 display for the Game Boy emulator.
    /// Creates a window, streams the 160x144 framebuffer to a GPU texture,
    /// and handles keyboard input mapped to Game Boy controls.
    ///
    /// Key mapping:
    ///   WASD        -> D-Pad
    ///   M           -> A
    ///   N           -> B
    ///   Enter       -> Start
    ///   Space       -> Select
    ///   Escape      -> Reset (return to ROM menu)
    /// </summary>
    public class SDLDisplay : IDisposable
    {
        public const int ScreenWidth = 160;
        public const int ScreenHeight = 144;
        public const int Scale = 4;

        private IntPtr _window;
        private IntPtr _renderer;
        private IntPtr _texture;
        private readonly uint[] _pixelBuffer = new uint[ScreenWidth * ScreenHeight];
        private bool _disposed;

        public bool IsOpen { get; private set; }

        public SDLDisplay()
        {
            if (SDL2.SDL_Init(SDL2.SDL_INIT_VIDEO) < 0)
            {
                string error = Marshal.PtrToStringAnsi(SDL2.SDL_GetError()) ?? "Unknown error";
                throw new Exception($"SDL could not initialise: {error}");
            }

            // Nearest-neighbour scaling for crisp pixel art
            SDL2.SDL_SetHint(SDL2.SDL_HINT_RENDER_SCALE_QUALITY, "0");

            _window = SDL2.SDL_CreateWindow(
                "GameBoy Emulator",
                SDL2.SDL_WINDOWPOS_CENTERED,
                SDL2.SDL_WINDOWPOS_CENTERED,
                ScreenWidth * Scale,
                ScreenHeight * Scale,
                SDL2.SDL_WINDOW_SHOWN | SDL2.SDL_WINDOW_RESIZABLE);

            if (_window == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL2.SDL_GetError()) ?? "Unknown error";
                SDL2.SDL_Quit();
                throw new Exception($"SDL window creation failed: {error}");
            }

            _renderer = SDL2.SDL_CreateRenderer(_window, -1,
                SDL2.SDL_RENDERER_ACCELERATED | SDL2.SDL_RENDERER_PRESENTVSYNC);

            if (_renderer == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL2.SDL_GetError()) ?? "Unknown error";
                SDL2.SDL_DestroyWindow(_window);
                SDL2.SDL_Quit();
                throw new Exception($"SDL renderer creation failed: {error}");
            }

            _texture = SDL2.SDL_CreateTexture(
                _renderer,
                SDL2.SDL_PIXELFORMAT_ARGB8888,
                SDL2.SDL_TEXTUREACCESS_STREAMING,
                ScreenWidth,
                ScreenHeight);

            if (_texture == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL2.SDL_GetError()) ?? "Unknown error";
                SDL2.SDL_DestroyRenderer(_renderer);
                SDL2.SDL_DestroyWindow(_window);
                SDL2.SDL_Quit();
                throw new Exception($"SDL texture creation failed: {error}");
            }

            SDL2.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
            IsOpen = true;
        }

        /// <summary>
        /// Converts the emulator's ScreenData[x, y, rgb] array to a packed ARGB
        /// pixel buffer and presents it to the SDL window.
        /// </summary>
        public void RenderFrame(int[,,] screenData)
        {
            // Convert ScreenData[x, y, channel] to packed ARGB uint32 buffer
            for (int y = 0; y < ScreenHeight; y++)
            {
                for (int x = 0; x < ScreenWidth; x++)
                {
                    uint r = (uint)screenData[x, y, 0];
                    uint g = (uint)screenData[x, y, 1];
                    uint b = (uint)screenData[x, y, 2];
                    _pixelBuffer[y * ScreenWidth + x] = 0xFF000000 | (r << 16) | (g << 8) | b;
                }
            }

            // Pin the buffer and upload to GPU texture
            GCHandle handle = GCHandle.Alloc(_pixelBuffer, GCHandleType.Pinned);
            try
            {
                SDL2.SDL_UpdateTexture(_texture, IntPtr.Zero,
                    handle.AddrOfPinnedObject(), ScreenWidth * 4);
            }
            finally
            {
                handle.Free();
            }

            SDL2.SDL_RenderClear(_renderer);
            SDL2.SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);
            SDL2.SDL_RenderPresent(_renderer);
        }

        /// <summary>
        /// Polls all pending SDL events, handling quit and keyboard input.
        /// </summary>
        public void PollEvents(GameBoy gameBoy)
        {
            while (SDL2.SDL_PollEvent(out SDL2.SDL_Event e) != 0)
            {
                switch (e.type)
                {
                    case SDL2.SDL_QUIT:
                        IsOpen = false;
                        gameBoy.cPU.Running = false;
                        break;

                    case SDL2.SDL_KEYDOWN:
                        if (e.key.repeat == 0) // ignore key-repeat
                            HandleKeyDown(e.key.keysym.scancode, gameBoy);
                        break;

                    case SDL2.SDL_KEYUP:
                        HandleKeyUp(e.key.keysym.scancode, gameBoy);
                        break;
                }
            }
        }

        private void HandleKeyDown(int scancode, GameBoy gb)
        {
            if (scancode == SDL2.SDL_SCANCODE_ESCAPE)
            {
                gb.ResetRequested = true;
                return;
            }

            int key = MapKey(scancode);
            if (key >= 0)
                gb.KeypadKeyPressed(key);
        }

        private void HandleKeyUp(int scancode, GameBoy gb)
        {
            int key = MapKey(scancode);
            if (key >= 0)
                gb.KeypadKeyReleased(key);
        }

        /// <summary>
        /// Maps SDL scancodes to Game Boy keypad bit indices.
        /// Game Boy bits: 0=Right, 1=Left, 2=Up, 3=Down, 4=A, 5=B, 6=Select, 7=Start
        /// </summary>
        private static int MapKey(int scancode) => scancode switch
        {
            SDL2.SDL_SCANCODE_D      => 0, // Right
            SDL2.SDL_SCANCODE_A      => 1, // Left
            SDL2.SDL_SCANCODE_W      => 2, // Up
            SDL2.SDL_SCANCODE_S      => 3, // Down
            SDL2.SDL_SCANCODE_M      => 4, // A
            SDL2.SDL_SCANCODE_N      => 5, // B
            SDL2.SDL_SCANCODE_SPACE  => 6, // Select
            SDL2.SDL_SCANCODE_RETURN => 7, // Start
            _ => -1
        };

        // =============================================================
        //  ROM Selection Menu
        // =============================================================

        // Menu texture: same resolution as the Game Boy screen (160×144),
        // rendered with a simple built-in 5×7 pixel font.
        private IntPtr _menuTexture = IntPtr.Zero;
        private readonly uint[] _menuPixBuf = new uint[ScreenWidth * ScreenHeight];

        // Game-Boy-green palette
        private const uint ColBg     = 0xFF0F380F; // darkest green  (background)
        private const uint ColText   = 0xFF9BBC0F; // lightest green (normal text)
        private const uint ColSel    = 0xFF306230; // dark green     (selection bar)
        private const uint ColSelTxt = 0xFF8BAC0F; // bright green   (selected text)
        private const uint ColTitle  = 0xFF8BAC0F; // title colour

        /// <summary>
        /// Shows a ROM selection menu rendered inside the SDL window.
        /// Returns the selected ROM path, or null if the user closed the window.
        /// </summary>
        public string? ShowRomMenu(List<string> romPaths, List<string> romNames)
        {
            if (_menuTexture == IntPtr.Zero)
            {
                _menuTexture = SDL2.SDL_CreateTexture(
                    _renderer,
                    SDL2.SDL_PIXELFORMAT_ARGB8888,
                    SDL2.SDL_TEXTUREACCESS_STREAMING,
                    ScreenWidth, ScreenHeight);
            }

            int selected = 0;
            int scrollOffset = 0;
            const int maxVisible = 10; // max ROM entries visible at once
            const int itemHeight = 10; // pixels per item (font is 7px + 3px gap)
            const int titleY = 4;
            const int listY = 25;

            while (IsOpen)
            {
                // --- Event handling ---
                while (SDL2.SDL_PollEvent(out SDL2.SDL_Event e) != 0)
                {
                    switch (e.type)
                    {
                        case SDL2.SDL_QUIT:
                            IsOpen = false;
                            return null;

                        case SDL2.SDL_KEYDOWN:
                            if (e.key.repeat != 0) break;
                            int sc = e.key.keysym.scancode;
                            if (sc == SDL2.SDL_SCANCODE_ESCAPE)
                            {
                                IsOpen = false;
                                return null;
                            }
                            if (sc == SDL2.SDL_SCANCODE_UP)
                            {
                                selected--;
                                if (selected < 0) selected = romNames.Count - 1;
                            }
                            if (sc == SDL2.SDL_SCANCODE_DOWN)
                            {
                                selected++;
                                if (selected >= romNames.Count) selected = 0;
                            }
                            if (sc == SDL2.SDL_SCANCODE_RETURN)
                                return romPaths[selected];
                            break;
                    }
                }

                // Keep selected item in the visible window
                if (selected < scrollOffset) scrollOffset = selected;
                if (selected >= scrollOffset + maxVisible) scrollOffset = selected - maxVisible + 1;

                // --- Draw menu to pixel buffer ---
                Array.Fill(_menuPixBuf, ColBg);

                DrawString("SELECT ROM", 30, titleY, ColTitle, 1);

                // Thin separator line under the title
                for (int x = 8; x < ScreenWidth - 8; x++)
                    _menuPixBuf[(titleY + 10) * ScreenWidth + x] = ColTitle;
                for (int x = 8; x < ScreenWidth - 8; x++)
                    _menuPixBuf[(titleY + 128) * ScreenWidth + x] = ColTitle;

                for (int i = 0; i < maxVisible && (i + scrollOffset) < romNames.Count; i++)
                {
                    int idx = i + scrollOffset;
                    int y = listY + i * itemHeight;
                    bool isSel = (idx == selected);

                    if (isSel)
                    {
                        // Draw selection bar
                        for (int py = y - 1; py < y + 8; py++)
                            for (int px = 4; px < ScreenWidth - 4; px++)
                                if (py >= 0 && py < ScreenHeight)
                                    _menuPixBuf[py * ScreenWidth + px] = ColSel;
                    }

                    string name = romNames[idx];
                    if (name.Length > 20) name = name[..8] + "..";
                    uint col = isSel ? ColSelTxt : ColText;

                    // Arrow indicator for selected item
                    if (isSel) DrawString(">", 6, y, col, 1);
                    DrawString(name, 16, y, col, 1);
                }

                // Scroll indicators
                if (scrollOffset > 0)
                    DrawString("^", 76, listY - 9, ColText, 1);
                if (scrollOffset + maxVisible < romNames.Count)
                    DrawString("\x7F", 76, listY + maxVisible * itemHeight, ColText, 1);

                // --- Upload buffer to texture & present ---
                GCHandle pin = GCHandle.Alloc(_menuPixBuf, GCHandleType.Pinned);
                try
                {
                    SDL2.SDL_UpdateTexture(_menuTexture, IntPtr.Zero,
                        pin.AddrOfPinnedObject(), ScreenWidth * 4);
                }
                finally { pin.Free(); }

                SDL2.SDL_RenderClear(_renderer);
                SDL2.SDL_RenderCopy(_renderer, _menuTexture, IntPtr.Zero, IntPtr.Zero);
                SDL2.SDL_RenderPresent(_renderer);

                SDL2.SDL_Delay(16); // ~60 fps
            }

            return null;
        }

        // =============================================================
        //  Minimal 5×7 pixel font (ASCII 32–127)
        // =============================================================

        private void DrawString(string text, int x, int y, uint colour, int scale = 1)
        {
            int cx = x;
            foreach (char ch in text)
            {
                DrawChar(ch, cx, y, colour, scale);
                cx += (Font5x7.CharWidth + 1) * scale;
            }
        }

        private void DrawChar(char ch, int x, int y, uint colour, int scale)
        {
            int idx = ch - 32;
            if (idx < 0 || idx >= Font5x7.Data.Length) idx = 0;

            ulong glyph = Font5x7.Data[idx];
            for (int row = 0; row < Font5x7.CharHeight; row++)
            {
                for (int col = 0; col < Font5x7.CharWidth; col++)
                {
                    // Each glyph is packed: 7 rows of 5 bits, MSB-first in a ulong
                    int bit = (Font5x7.CharHeight - 1 - row) * Font5x7.CharWidth + (Font5x7.CharWidth - 1 - col);
                    if (((glyph >> bit) & 1) == 1)
                    {
                        for (int sy = 0; sy < scale; sy++)
                            for (int sx = 0; sx < scale; sx++)
                            {
                                int px = x + col * scale + sx;
                                int py = y + row * scale + sy;
                                if (px >= 0 && px < ScreenWidth && py >= 0 && py < ScreenHeight)
                                    _menuPixBuf[py * ScreenWidth + px] = colour;
                            }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_menuTexture != IntPtr.Zero) SDL2.SDL_DestroyTexture(_menuTexture);
                if (_texture != IntPtr.Zero)  SDL2.SDL_DestroyTexture(_texture);
                if (_renderer != IntPtr.Zero) SDL2.SDL_DestroyRenderer(_renderer);
                if (_window != IntPtr.Zero)   SDL2.SDL_DestroyWindow(_window);
                SDL2.SDL_Quit();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~SDLDisplay()
        {
            Dispose();
        }
    }

    /// <summary>
    /// Minimal 5×7 bitmap font covering ASCII 32–127.
    /// Each glyph is packed into a ulong: 7 rows × 5 columns = 35 bits.
    /// Bit layout: row 0 (top) occupies the highest bits.
    /// Row order top-to-bottom, column order left-to-right within each row,
    /// packed MSB-first so bit 34 = top-left, bit 0 = bottom-right.
    /// </summary>
    internal static class Font5x7
    {
        public const int CharWidth = 5;
        public const int CharHeight = 7;

        // 96 glyphs: space (32) through tilde (126) + one placeholder
        public static readonly ulong[] Data =
        {
            0b00000_00000_00000_00000_00000_00000_00000UL, // 32 ' '
            0b00100_00100_00100_00100_00100_00000_00100UL, // 33 '!'
            0b01010_01010_01010_00000_00000_00000_00000UL, // 34 '"'
            0b01010_01010_11111_01010_11111_01010_01010UL, // 35 '#'
            0b00100_01111_10100_01110_00101_11110_00100UL, // 36 '$'
            0b11000_11001_00010_00100_01000_10011_00011UL, // 37 '%'
            0b01100_10010_10100_01000_10101_10010_01101UL, // 38 '&'
            0b00100_00100_00100_00000_00000_00000_00000UL, // 39 '''
            0b00010_00100_01000_01000_01000_00100_00010UL, // 40 '('
            0b01000_00100_00010_00010_00010_00100_01000UL, // 41 ')'
            0b00000_00100_10101_01110_10101_00100_00000UL, // 42 '*'
            0b00000_00100_00100_11111_00100_00100_00000UL, // 43 '+'
            0b00000_00000_00000_00000_00100_00100_01000UL, // 44 ','
            0b00000_00000_00000_11111_00000_00000_00000UL, // 45 '-'
            0b00000_00000_00000_00000_00000_00000_00100UL, // 46 '.'
            0b00000_00001_00010_00100_01000_10000_00000UL, // 47 '/'
            0b01110_10001_10011_10101_11001_10001_01110UL, // 48 '0'
            0b00100_01100_00100_00100_00100_00100_01110UL, // 49 '1'
            0b01110_10001_00001_00010_00100_01000_11111UL, // 50 '2'
            0b11111_00010_00100_00010_00001_10001_01110UL, // 51 '3'
            0b00010_00110_01010_10010_11111_00010_00010UL, // 52 '4'
            0b11111_10000_11110_00001_00001_10001_01110UL, // 53 '5'
            0b00110_01000_10000_11110_10001_10001_01110UL, // 54 '6'
            0b11111_00001_00010_00100_01000_01000_01000UL, // 55 '7'
            0b01110_10001_10001_01110_10001_10001_01110UL, // 56 '8'
            0b01110_10001_10001_01111_00001_00010_01100UL, // 57 '9'
            0b00000_00000_00100_00000_00100_00000_00000UL, // 58 ':'
            0b00000_00000_00100_00000_00100_00100_01000UL, // 59 ';'
            0b00010_00100_01000_10000_01000_00100_00010UL, // 60 '<'
            0b00000_00000_11111_00000_11111_00000_00000UL, // 61 '='
            0b01000_00100_00010_00001_00010_00100_01000UL, // 62 '>'
            0b01110_10001_00001_00010_00100_00000_00100UL, // 63 '?'
            0b01110_10001_10111_10101_10110_10000_01110UL, // 64 '@'
            0b01110_10001_10001_11111_10001_10001_10001UL, // 65 'A'
            0b11110_10001_10001_11110_10001_10001_11110UL, // 66 'B'
            0b01110_10001_10000_10000_10000_10001_01110UL, // 67 'C'
            0b11100_10010_10001_10001_10001_10010_11100UL, // 68 'D'
            0b11111_10000_10000_11110_10000_10000_11111UL, // 69 'E'
            0b11111_10000_10000_11110_10000_10000_10000UL, // 70 'F'
            0b01110_10001_10000_10111_10001_10001_01111UL, // 71 'G'
            0b10001_10001_10001_11111_10001_10001_10001UL, // 72 'H'
            0b01110_00100_00100_00100_00100_00100_01110UL, // 73 'I'
            0b00111_00010_00010_00010_00010_10010_01100UL, // 74 'J'
            0b10001_10010_10100_11000_10100_10010_10001UL, // 75 'K'
            0b10000_10000_10000_10000_10000_10000_11111UL, // 76 'L'
            0b10001_11011_10101_10101_10001_10001_10001UL, // 77 'M'
            0b10001_10001_11001_10101_10011_10001_10001UL, // 78 'N'
            0b01110_10001_10001_10001_10001_10001_01110UL, // 79 'O'
            0b11110_10001_10001_11110_10000_10000_10000UL, // 80 'P'
            0b01110_10001_10001_10001_10101_10010_01101UL, // 81 'Q'
            0b11110_10001_10001_11110_10100_10010_10001UL, // 82 'R'
            0b01111_10000_10000_01110_00001_00001_11110UL, // 83 'S'
            0b11111_00100_00100_00100_00100_00100_00100UL, // 84 'T'
            0b10001_10001_10001_10001_10001_10001_01110UL, // 85 'U'
            0b10001_10001_10001_10001_10001_01010_00100UL, // 86 'V'
            0b10001_10001_10001_10101_10101_10101_01010UL, // 87 'W'
            0b10001_10001_01010_00100_01010_10001_10001UL, // 88 'X'
            0b10001_10001_01010_00100_00100_00100_00100UL, // 89 'Y'
            0b11111_00001_00010_00100_01000_10000_11111UL, // 90 'Z'
            0b01110_01000_01000_01000_01000_01000_01110UL, // 91 '['
            0b00000_10000_01000_00100_00010_00001_00000UL, // 92 '\'
            0b01110_00010_00010_00010_00010_00010_01110UL, // 93 ']'
            0b00100_01010_10001_00000_00000_00000_00000UL, // 94 '^'
            0b00000_00000_00000_00000_00000_00000_11111UL, // 95 '_'
            0b01000_00100_00010_00000_00000_00000_00000UL, // 96 '`'
            0b00000_00000_01110_00001_01111_10001_01111UL, // 97 'a'
            0b10000_10000_10110_11001_10001_10001_11110UL, // 98 'b'
            0b00000_00000_01110_10000_10000_10001_01110UL, // 99 'c'
            0b00001_00001_01101_10011_10001_10001_01111UL, // 100 'd'
            0b00000_00000_01110_10001_11111_10000_01110UL, // 101 'e'
            0b00110_01001_01000_11100_01000_01000_01000UL, // 102 'f'
            0b00000_01111_10001_10001_01111_00001_01110UL, // 103 'g'
            0b10000_10000_10110_11001_10001_10001_10001UL, // 104 'h'
            0b00100_00000_01100_00100_00100_00100_01110UL, // 105 'i'
            0b00010_00000_00110_00010_00010_10010_01100UL, // 106 'j'
            0b10000_10000_10010_10100_11000_10100_10010UL, // 107 'k'
            0b01100_00100_00100_00100_00100_00100_01110UL, // 108 'l'
            0b00000_00000_11010_10101_10101_10001_10001UL, // 109 'm'
            0b00000_00000_10110_11001_10001_10001_10001UL, // 110 'n'
            0b00000_00000_01110_10001_10001_10001_01110UL, // 111 'o'
            0b00000_00000_11110_10001_11110_10000_10000UL, // 112 'p'
            0b00000_00000_01101_10011_01111_00001_00001UL, // 113 'q'
            0b00000_00000_10110_11001_10000_10000_10000UL, // 114 'r'
            0b00000_00000_01110_10000_01110_00001_11110UL, // 115 's'
            0b01000_01000_11100_01000_01000_01001_00110UL, // 116 't'
            0b00000_00000_10001_10001_10001_10011_01101UL, // 117 'u'
            0b00000_00000_10001_10001_10001_01010_00100UL, // 118 'v'
            0b00000_00000_10001_10001_10101_10101_01010UL, // 119 'w'
            0b00000_00000_10001_01010_00100_01010_10001UL, // 120 'x'
            0b00000_00000_10001_10001_01111_00001_01110UL, // 121 'y'
            0b00000_00000_11111_00010_00100_01000_11111UL, // 122 'z'
            0b00010_00100_00100_01000_00100_00100_00010UL, // 123 '{'
            0b00100_00100_00100_00100_00100_00100_00100UL, // 124 '|'
            0b01000_00100_00100_00010_00100_00100_01000UL, // 125 '}'
            0b00000_00000_01000_10101_00010_00000_00000UL, // 126 '~'
            0b00000_00000_00000_10001_01010_00100_00000UL, // 127 down arrow (inverted ^)
        };
    }
}
