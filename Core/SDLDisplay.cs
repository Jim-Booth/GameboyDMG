// ============================================================================
// Project:     GameboyEmu
// File:        Core/SDLDisplay.cs
// Description: SDL2 display frontend - frame presentation, keyboard input,
//              ROM selection menu, and built-in bitmap menu font rendering
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
    ///   Arrow keys / WASD -> D-Pad
    ///   Z / M             -> A
    ///   X / N             -> B
    ///   Enter             -> Start
    ///   Space             -> Select
    ///   Escape            -> Reset (return to ROM menu)
    /// </summary>
    public sealed class SDLDisplay : IDisposable
    {
        public const int ScreenWidth = 160;
        public const int ScreenHeight = 144;
        public const int Scale = 2;

        // Position of the game viewport within the larger window
        private const int GameX = 154;
        private const int GameY = 145;

        private IntPtr _window;
        private IntPtr _renderer;
        private IntPtr _texture;
        private IntPtr _bgTexture = IntPtr.Zero;
        private IntPtr _ledTexture = IntPtr.Zero;
        private bool _disposed;

        // LED indicator: centre position in window coords, radius, glow margin
        private const int LedCx = 101;
        private const int LedCy = 244;
        private const int LedRadius = 10;
        private const int LedGlow = 4;                          // extra pixels for soft halo
        private const int LedTexSize = (LedRadius + LedGlow) * 2; // 28

        public bool IsOpen { get; private set; }

        public SDLDisplay()
        {
            if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO) < 0)
            {
                string error = Marshal.PtrToStringAnsi(SDL.SDL_GetError()) ?? "Unknown error";
                throw new Exception($"SDL could not initialise: {error}");
            }

            // Nearest-neighbour scaling for crisp pixel art
            SDL.SDL_SetHint(SDL.SDL_HINT_RENDER_SCALE_QUALITY, "0");

            _window = SDL.SDL_CreateWindow(
                "GameBoy DMG Emulator",
                SDL.SDL_WINDOWPOS_CENTERED,
                SDL.SDL_WINDOWPOS_CENTERED,
                630,
                1015,
                SDL.SDL_WINDOW_SHOWN);

            if (_window == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL.SDL_GetError()) ?? "Unknown error";
                SDL.SDL_Quit();
                throw new Exception($"SDL window creation failed: {error}");
            }

            // The emulator owns frame pacing; presenting with vsync would add a
            // second wall-clock gate that varies with the desktop refresh rate.
            _renderer = SDL.SDL_CreateRenderer(_window, -1, SDL.SDL_RENDERER_ACCELERATED);

            if (_renderer == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL.SDL_GetError()) ?? "Unknown error";
                SDL.SDL_DestroyWindow(_window);
                SDL.SDL_Quit();
                throw new Exception($"SDL renderer creation failed: {error}");
            }

            _texture = SDL.SDL_CreateTexture(
                _renderer,
                SDL.SDL_PIXELFORMAT_ARGB8888,
                SDL.SDL_TEXTUREACCESS_STREAMING,
                ScreenWidth,
                ScreenHeight);

            if (_texture == IntPtr.Zero)
            {
                string error = Marshal.PtrToStringAnsi(SDL.SDL_GetError()) ?? "Unknown error";
                SDL.SDL_DestroyRenderer(_renderer);
                SDL.SDL_DestroyWindow(_window);
                SDL.SDL_Quit();
                throw new Exception($"SDL texture creation failed: {error}");
            }

            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
            LoadBackground();
            CreateLedTexture();
            IsOpen = true;
        }

        private void LoadBackground()
        {
            // Search next to the executable first, then the working directory
            string bgPath = Path.Combine(AppContext.BaseDirectory, "background.bmp");
            if (!File.Exists(bgPath))
                bgPath = "background.bmp";
            if (!File.Exists(bgPath))
            {
                Console.WriteLine("[Display] background.bmp not found — window background will be black.");
                return;
            }

            IntPtr rw = SDL.SDL_RWFromFile(bgPath, "rb");
            if (rw == IntPtr.Zero) return;

            IntPtr surface = SDL.SDL_LoadBMP_RW(rw, 1); // 1 = free RWops after load
            if (surface == IntPtr.Zero)
            {
                Console.WriteLine($"[Display] Failed to load background.bmp: {Marshal.PtrToStringAnsi(SDL.SDL_GetError())}");
                return;
            }

            _bgTexture = SDL.SDL_CreateTextureFromSurface(_renderer, surface);
            SDL.SDL_FreeSurface(surface);

            if (_bgTexture == IntPtr.Zero)
                Console.WriteLine($"[Display] Failed to create background texture: {Marshal.PtrToStringAnsi(SDL.SDL_GetError())}");
            else
                Console.WriteLine($"[Display] Background loaded: {bgPath}");
        }

        private void CreateLedTexture()
        {
            // Build a 28×28 ARGB pixel map: bright red disc with soft glow halo.
            // The disc spans radius 10 px; pixels outside that up to +4 px form a
            // semi-transparent red glow blended at render time.
            uint[] pixels = new uint[LedTexSize * LedTexSize];
            int cx = LedTexSize / 2; // 14
            int cy = LedTexSize / 2; // 14

            for (int py = 0; py < LedTexSize; py++)
            {
                for (int px = 0; px < LedTexSize; px++)
                {
                    double dx = px - cx;
                    double dy = py - cy;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist <= LedRadius)
                    {
                        // Interpolate center (near-white red) → edge (saturated red)
                        double t = dist / LedRadius;
                        byte g = (byte)(200 * (1.0 - t)); // 200 at centre → 0 at edge
                        byte b = (byte)(180 * (1.0 - t)); // slight blue tint for warmth
                        pixels[py * LedTexSize + px] = (uint)(0xFF000000 | (0xFF << 16) | (g << 8) | b);
                    }
                    else if (dist <= LedRadius + LedGlow)
                    {
                        // Soft outer glow: pure red, alpha fades to zero
                        double t = (dist - LedRadius) / LedGlow;
                        byte alpha = (byte)(180 * (1.0 - t * t)); // quadratic fall-off
                        pixels[py * LedTexSize + px] = (uint)((alpha << 24) | 0x00DD0000);
                    }
                    // else transparent (pixel stays 0)
                }
            }

            _ledTexture = SDL.SDL_CreateTexture(
                _renderer,
                SDL.SDL_PIXELFORMAT_ARGB8888,
                SDL.SDL_TEXTUREACCESS_STREAMING,
                LedTexSize, LedTexSize);

            if (_ledTexture == IntPtr.Zero) return;

            // Enable per-pixel alpha blending so the glow composites correctly
            SDL.SDL_SetTextureBlendMode(_ledTexture, 1); // SDL_BLENDMODE_BLEND

            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { SDL.SDL_UpdateTexture(_ledTexture, IntPtr.Zero, pin.AddrOfPinnedObject(), LedTexSize * 4); }
            finally { pin.Free(); }
        }

        /// <summary>
        /// Uploads the PPU's packed ARGB pixel buffer directly to the GPU texture.
        /// Zero conversion overhead — the PPU writes packed ARGB uint32 values.
        /// </summary>
        public void RenderFrame(uint[] pixelBuffer)
        {
            GCHandle handle = GCHandle.Alloc(pixelBuffer, GCHandleType.Pinned);
            try
            {
                SDL.SDL_UpdateTexture(_texture, IntPtr.Zero,
                    handle.AddrOfPinnedObject(), ScreenWidth * 4);
            }
            finally
            {
                handle.Free();
            }

            var dst = new SDL.SDL_Rect { x = GameX, y = GameY, w = ScreenWidth * Scale, h = ScreenHeight * Scale };
            SDL.SDL_RenderClear(_renderer);
            if (_bgTexture != IntPtr.Zero)
                SDL.SDL_RenderCopy(_renderer, _bgTexture, IntPtr.Zero, IntPtr.Zero); // stretch to full window
            SDL.SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, ref dst);
            DrawViewportBevel();
            // LED illuminated while a game is running
            if (_ledTexture != IntPtr.Zero)
            {
                var ledDst = new SDL.SDL_Rect
                {
                    x = LedCx - LedTexSize / 2,
                    y = LedCy - LedTexSize / 2,
                    w = LedTexSize,
                    h = LedTexSize
                };
                SDL.SDL_RenderCopy(_renderer, _ledTexture, IntPtr.Zero, ref ledDst);
            }
            SDL.SDL_RenderPresent(_renderer);
        }

        /// <summary>
        /// Polls all pending SDL events, handling quit and keyboard input.
        /// </summary>
        public void PollEvents(GameBoy gameBoy)
        {
            while (SDL.SDL_PollEvent(out SDL.SDL_Event e) != 0)
            {
                switch (e.type)
                {
                    case SDL.SDL_QUIT:
                        IsOpen = false;
                        gameBoy.cPU.Running = false;
                        break;

                    case SDL.SDL_KEYDOWN:
                        if (e.key.repeat == 0) // ignore key-repeat
                            HandleKeyDown(e.key.keysym.scancode, gameBoy);
                        break;

                    case SDL.SDL_KEYUP:
                        HandleKeyUp(e.key.keysym.scancode, gameBoy);
                        break;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void HandleKeyDown(int scancode, GameBoy gb)
        {
            if (scancode == SDL.SDL_SCANCODE_ESCAPE)
            {
                gb.ResetRequested = true;
                return;
            }

            int key = MapKey(scancode);
            if (key >= 0)
                gb.KeypadKeyPressed(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MapKey(int scancode) => scancode switch
        {
            SDL.SDL_SCANCODE_RIGHT or SDL.SDL_SCANCODE_D => 0,
            SDL.SDL_SCANCODE_LEFT or SDL.SDL_SCANCODE_A => 1,
            SDL.SDL_SCANCODE_UP or SDL.SDL_SCANCODE_W => 2,
            SDL.SDL_SCANCODE_DOWN or SDL.SDL_SCANCODE_S => 3,
            SDL.SDL_SCANCODE_Z or SDL.SDL_SCANCODE_M => 4, // A
            SDL.SDL_SCANCODE_X or SDL.SDL_SCANCODE_N => 5, // B
            SDL.SDL_SCANCODE_SPACE => 6, // Select
            SDL.SDL_SCANCODE_RETURN => 7, // Start
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
        private const uint ColBg = 0xFF0F380F; // darkest green  (background)
        private const uint ColText = 0xFF9BBC0F; // lightest green (normal text)
        private const uint ColSel = 0xFF306230; // dark green     (selection bar)
        private const uint ColSelTxt = 0xFF8BAC0F; // bright green   (selected text)
        private const uint ColTitle = 0xFF8BAC0F; // title colour

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryGetMenuJumpChar(int keySym, out char jumpChar)
        {
            if (keySym >= 'a' && keySym <= 'z')
            {
                jumpChar = (char)(keySym - 32); // to uppercase ASCII
                return true;
            }

            if (keySym >= 'A' && keySym <= 'Z')
            {
                jumpChar = (char)keySym;
                return true;
            }

            if (keySym >= '0' && keySym <= '9')
            {
                jumpChar = (char)keySym;
                return true;
            }

            jumpChar = '\0';
            return false;
        }

        private static int FindFirstRomStartingWith(List<string> romNames, char jumpChar)
        {
            for (int i = 0; i < romNames.Count; i++)
            {
                string name = romNames[i];
                if (string.IsNullOrWhiteSpace(name)) continue;

                string trimmed = name.TrimStart();
                if (trimmed.Length == 0) continue;

                if (char.ToUpperInvariant(trimmed[0]) == jumpChar)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Shows a ROM selection menu rendered inside the SDL window.
        /// Returns the selected ROM path, whether to skip the boot ROM, and
        /// current menu position. If the user closed the window: (null, false, selected, scroll).
        /// </summary>
        public (string? RomPath, bool SkipBootRom, int SelectedIndex, int ScrollOffset) ShowRomMenu(
            List<string> romPaths,
            List<string> romNames,
            int initialSelected = 0,
            int initialScrollOffset = 0)
        {
            if (_menuTexture == IntPtr.Zero)
            {
                _menuTexture = SDL.SDL_CreateTexture(
                    _renderer,
                    SDL.SDL_PIXELFORMAT_ARGB8888,
                    SDL.SDL_TEXTUREACCESS_STREAMING,
                    ScreenWidth, ScreenHeight);
            }

            const int maxVisible = 10; // max ROM entries visible at once
            const int itemHeight = 10; // pixels per item (font is 7px + 3px gap)
            const int titleY = 4;
            const int listY = 25;
            const int maxNameChars = 21;
            const int marqueeStartDelayFrames = 60; // ~1 second at ~60 fps
            const int marqueeFramesPerStep = 5;
            int selected = Math.Clamp(initialSelected, 0, Math.Max(romNames.Count - 1, 0));
            int scrollOffset = Math.Clamp(initialScrollOffset, 0, Math.Max(romNames.Count - maxVisible, 0));
            int marqueeSelected = selected;
            int marqueeOffset = 0;
            int marqueeDelayCounter = 0;
            int marqueeFrameCounter = 0;

            while (IsOpen)
            {
                // --- Event handling ---
                while (SDL.SDL_PollEvent(out SDL.SDL_Event e) != 0)
                {
                    switch (e.type)
                    {
                        case SDL.SDL_QUIT:
                            IsOpen = false;
                            return (null, false, selected, scrollOffset);

                        case SDL.SDL_KEYDOWN:
                            int sc = e.key.keysym.scancode;
                            if (sc == SDL.SDL_SCANCODE_ESCAPE)
                            {
                                if (e.key.repeat != 0) break;
                                IsOpen = false;
                                return (null, false, selected, scrollOffset);
                            }
                            if (sc == SDL.SDL_SCANCODE_UP)
                            {
                                selected--;
                                if (selected < 0) selected = romNames.Count - 1;
                            }
                            if (sc == SDL.SDL_SCANCODE_DOWN)
                            {
                                selected++;
                                if (selected >= romNames.Count) selected = 0;
                            }
                            if (sc == SDL.SDL_SCANCODE_RETURN)
                            {
                                if (e.key.repeat != 0) break;
                                bool ctrlHeld = (e.key.keysym.mod & SDL.KMOD_CTRL) != 0;
                                return (romPaths[selected], ctrlHeld, selected, scrollOffset);
                            }

                            if (e.key.repeat == 0 && TryGetMenuJumpChar(e.key.keysym.sym, out char jumpChar))
                            {
                                int idx = FindFirstRomStartingWith(romNames, jumpChar);
                                if (idx >= 0)
                                    selected = idx;
                            }
                            break;
                    }
                }

                // Keep selected item in the visible window
                if (selected < scrollOffset) scrollOffset = selected;
                if (selected >= scrollOffset + maxVisible) scrollOffset = selected - maxVisible + 1;

                // Reset marquee when selection changes so each row starts from the first 21 chars
                if (selected != marqueeSelected)
                {
                    marqueeSelected = selected;
                    marqueeOffset = 0;
                    marqueeDelayCounter = 0;
                    marqueeFrameCounter = 0;
                }

                // --- Draw menu to pixel buffer ---
                Array.Fill(_menuPixBuf, ColBg);

                string title = $"SELECT ROM {selected + 1}/{romNames.Count}";
                DrawString(title, 12, titleY, ColTitle, 1);

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

                    string fullName = romNames[idx];
                    string name;

                    if (fullName.Length > maxNameChars)
                    {
                        if (isSel)
                        {
                            int maxOffset = fullName.Length - maxNameChars;
                            if (marqueeOffset < maxOffset)
                            {
                                if (marqueeDelayCounter < marqueeStartDelayFrames)
                                {
                                    marqueeDelayCounter++;
                                }
                                else
                                {
                                    marqueeFrameCounter++;
                                    if (marqueeFrameCounter >= marqueeFramesPerStep)
                                    {
                                        marqueeFrameCounter = 0;
                                        marqueeOffset++;
                                    }
                                }
                            }

                            name = fullName.Substring(marqueeOffset, maxNameChars);
                        }
                        else
                        {
                            name = fullName[..maxNameChars];
                        }
                    }
                    else
                    {
                        name = fullName;
                    }
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
                    SDL.SDL_UpdateTexture(_menuTexture, IntPtr.Zero,
                        pin.AddrOfPinnedObject(), ScreenWidth * 4);
                }
                finally { pin.Free(); }

                var dst = new SDL.SDL_Rect { x = GameX, y = GameY, w = ScreenWidth * Scale, h = ScreenHeight * Scale };
                SDL.SDL_RenderClear(_renderer);
                if (_bgTexture != IntPtr.Zero)
                    SDL.SDL_RenderCopy(_renderer, _bgTexture, IntPtr.Zero, IntPtr.Zero); // stretch to full window
                SDL.SDL_RenderCopy(_renderer, _menuTexture, IntPtr.Zero, ref dst);
                DrawViewportBevel();
                SDL.SDL_RenderPresent(_renderer);

                SDL.SDL_Delay(16); // ~60 fps
            }

            return (null, false, selected, scrollOffset);
        }

        /// <summary>
        /// Draws a 3-D bevel effect around the game viewport.
        /// Top and left edges get a translucent dark shadow (inset); bottom and
        /// right edges get a translucent light highlight (raised).
        /// </summary>
        private void DrawViewportBevel()
        {
            const int BevelWidth = 4;
            int vw = ScreenWidth  * Scale;   // viewport pixel width
            int vh = ScreenHeight * Scale;   // viewport pixel height
            int x  = GameX;
            int y  = GameY;

            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_BLEND);

            // --- dark shadow: top and left edges ---
            SDL.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 120);
            for (int i = 0; i < BevelWidth; i++)
            {
                // top
                SDL.SDL_RenderDrawLine(_renderer, x + i, y + i, x + vw - 1 - i, y + i);
                // left
                SDL.SDL_RenderDrawLine(_renderer, x + i, y + i, x + i, y + vh - 1 - i);
            }

            // --- light highlight: bottom and right edges ---
            SDL.SDL_SetRenderDrawColor(_renderer, 255, 255, 255, 80);
            for (int i = 0; i < BevelWidth; i++)
            {
                // bottom
                SDL.SDL_RenderDrawLine(_renderer, x + i, y + vh - 1 - i, x + vw - 1 - i, y + vh - 1 - i);
                // right
                SDL.SDL_RenderDrawLine(_renderer, x + vw - 1 - i, y + i, x + vw - 1 - i, y + vh - 1 - i);
            }

            SDL.SDL_SetRenderDrawBlendMode(_renderer, SDL.SDL_BlendMode.SDL_BLENDMODE_NONE);
        }

        /// <summary>
        /// Displays a startup error inside the SDL window and keeps the
        /// window responsive until the user closes the emulator.
        /// </summary>
        public void ShowStartupError(string title, string details)
        {
            if (!IsOpen)
                return;

            if (_menuTexture == IntPtr.Zero)
            {
                _menuTexture = SDL.SDL_CreateTexture(
                    _renderer,
                    SDL.SDL_PIXELFORMAT_ARGB8888,
                    SDL.SDL_TEXTUREACCESS_STREAMING,
                    ScreenWidth, ScreenHeight);
            }

            Array.Fill(_menuPixBuf, ColBg);
            DrawString("STARTUP ERROR", 28, 10, ColTitle, 1);

            for (int x = 8; x < ScreenWidth - 8; x++)
                _menuPixBuf[22 * ScreenWidth + x] = ColTitle;

            int textY = 32;
            textY = DrawWrappedText(title, 8, textY, ColText, 25, 10);
            textY = DrawWrappedText(details, 8, textY + 4, ColText, 25, 10);

            // Keep guidance text below wrapped error lines to avoid overlap.
            int helpY = textY + 6;
            DrawString("Place ROM .GB files", 8, helpY, ColSelTxt, 1);
            DrawString("in ROMS/ folder.", 8, helpY + 10, ColSelTxt, 1);

            int footerY = helpY + 24;
            DrawString("Press ESC to quit.", 8, footerY, ColText, 1);

            if (_menuTexture != IntPtr.Zero)
            {
                GCHandle pin = GCHandle.Alloc(_menuPixBuf, GCHandleType.Pinned);
                try
                {
                    SDL.SDL_UpdateTexture(_menuTexture, IntPtr.Zero,
                        pin.AddrOfPinnedObject(), ScreenWidth * 4);
                }
                finally { pin.Free(); }

                var dst = new SDL.SDL_Rect { x = GameX, y = GameY, w = ScreenWidth * Scale, h = ScreenHeight * Scale };
                SDL.SDL_RenderClear(_renderer);
                if (_bgTexture != IntPtr.Zero)
                    SDL.SDL_RenderCopy(_renderer, _bgTexture, IntPtr.Zero, IntPtr.Zero);
                SDL.SDL_RenderCopy(_renderer, _menuTexture, IntPtr.Zero, ref dst);
                SDL.SDL_RenderPresent(_renderer);
            }

            while (IsOpen)
            {
                while (SDL.SDL_PollEvent(out SDL.SDL_Event e) != 0)
                {
                    if (e.type == SDL.SDL_QUIT)
                    {
                        IsOpen = false;
                        break;
                    }

                    if (e.type == SDL.SDL_KEYDOWN && e.key.repeat == 0 && e.key.keysym.scancode == SDL.SDL_SCANCODE_ESCAPE)
                    {
                        IsOpen = false;
                        break;
                    }
                }

                SDL.SDL_Delay(16);
            }
        }

        private int DrawWrappedText(string text, int x, int y, uint colour, int maxChars, int lineHeight)
        {
            foreach (string line in WrapWords(text, maxChars))
            {
                DrawString(line, x, y, colour, 1);
                y += lineHeight;
            }

            return y;
        }

        private static List<string> WrapWords(string text, int maxChars)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                lines.Add(string.Empty);
                return lines;
            }

            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;

            foreach (string word in words)
            {
                if (current.Length == 0)
                {
                    current = word;
                    continue;
                }

                string candidate = current + " " + word;
                if (candidate.Length <= maxChars)
                {
                    current = candidate;
                }
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }

            if (current.Length > 0)
                lines.Add(current);

            return lines;
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
                if (_ledTexture != IntPtr.Zero) SDL.SDL_DestroyTexture(_ledTexture);
                if (_bgTexture != IntPtr.Zero) SDL.SDL_DestroyTexture(_bgTexture);
                if (_menuTexture != IntPtr.Zero) SDL.SDL_DestroyTexture(_menuTexture);
                if (_texture != IntPtr.Zero) SDL.SDL_DestroyTexture(_texture);
                if (_renderer != IntPtr.Zero) SDL.SDL_DestroyRenderer(_renderer);
                if (_window != IntPtr.Zero) SDL.SDL_DestroyWindow(_window);
                SDL.SDL_Quit();
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