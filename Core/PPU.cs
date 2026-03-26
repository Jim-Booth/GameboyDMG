// ============================================================================
// Project:     GameboyEmu
// File:        Core/PPU.cs
// Description: Pixel Processing Unit — scanline renderer for background,
//              window, and sprite layers (optimised: flat buffers, palette
//              LUT, zero per-scanline allocations)
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Runtime.CompilerServices;
#nullable enable

namespace GameboyEmu.Core
{
    public class PPU
    {
        private const int CyclesPerScanline = 456;
        private const int Width = 160;
        private const int Height = 144;

        private readonly MMU _mmu;

        public int ScanLineCounter = CyclesPerScanline;

        // ---- Flat packed-ARGB pixel buffer: row-major [y * 160 + x] ----
        private readonly uint[] _screenBuffer = new uint[Width * Height];

        // Raw BG/Window colour index (0-3) per pixel — used for sprite BG priority.
        private readonly byte[] _bgColorIndex = new byte[Width * Height];

        /// <summary>Packed ARGB pixel buffer — zero-copy access for the display.</summary>
        public uint[] ScreenBuffer => _screenBuffer;

        // ---- Pre-computed palette LUT ----
        // For every possible palette register value (0-255) × colour index (0-3)
        // we store the final packed ARGB colour.
        private static readonly uint[] PaletteLUT = new uint[256 * 4];

        // ---- Sprite sorting — pre-allocated arrays (no List<>/closure allocs) ----
        private readonly int[] _spriteOam = new int[10];
        private readonly byte[] _spriteX = new byte[10];
        private readonly byte[] _spriteY = new byte[10];
        private readonly byte[] _spriteTile = new byte[10];
        private readonly byte[] _spriteAttr = new byte[10];
        private int _spriteCount;

        // Current Mode 3 duration for this scanline.
        private int currentMode3Duration = 172;
        private bool scanLineRendered = false;
        private bool lycWasMatching = false;

        // Window internal line counter.
        private int windowLineCounter = 0;
        private bool windowWasRenderedThisLine = false;

        private bool _frameReady;

        // ---- Static constructor: build palette LUT once ----
        static PPU()
        {
            // Game Boy green palette: mapped colour 0-3 → packed ARGB
            uint[] colours =
            [
                0xFF9BBC0F, // White      (155, 188, 15)
                0xFF8BAC0F, // LightGray  (139, 172, 15)
                0xFF306230, // DarkGray   (48,  98,  48)
                0xFF0F380F  // Black      (15,  56,  15)
            ];

            for (int pal = 0; pal < 256; pal++)
            {
                for (int colIdx = 0; colIdx < 4; colIdx++)
                {
                    int mapped = (pal >> (colIdx * 2)) & 0x03;
                    PaletteLUT[pal * 4 + colIdx] = colours[mapped];
                }
            }
        }

        public PPU(MMU mmu)
        {
            _mmu = mmu;
        }

        // ----- Bit helpers (inlined for hot paths) -----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TestBit(byte data, int bitPos)
            => (data & (1 << bitPos)) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte SetBit(int reg, int bit, int val)
            => val == 1 ? (byte)(reg | (1 << bit)) : (byte)(reg & ~(1 << bit));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RequestInterrupt(int id)
            => _mmu.IF = (byte)(_mmu.IF | (1 << id));

        // ----- Public API -----

        public void Update(int cycles)
        {
            byte[] mem = _mmu.Memory;

            if ((mem[0xFF40] & 0x80) == 0)
            {
                // LCD off: reset scanline state, stay in mode 1 (VBlank)
                ScanLineCounter = CyclesPerScanline;
                mem[0xFF44] = 0;
                scanLineRendered = false;
                windowLineCounter = 0;
                mem[0xFF41] = (byte)((mem[0xFF41] & 0xFC) | 0x01);
                return;
            }

            ScanLineCounter -= cycles;

            // Scanline boundary crossed - advance to next line
            if (ScanLineCounter <= 0)
            {
                ScanLineCounter += CyclesPerScanline;
                scanLineRendered = false;

                // Advance LY
                mem[0xFF44]++;

                // VBlank: LY reaches 144
                if (mem[0xFF44] == 144)
                {
                    RequestInterrupt(0);
                    _frameReady = true;
                }

                // Wrap after line 153
                if (mem[0xFF44] > 153)
                    mem[0xFF44] = 0;

                // Reset window line counter at the start of a new frame
                if (mem[0xFF44] == 0)
                    windowLineCounter = 0;
            }

            // Draw scanline when entering Mode 3 (after 80 cycles of Mode 2)
            if (!scanLineRendered && ScanLineCounter <= 376 // 456 - 80
                && mem[0xFF44] < 144)
            {
                windowWasRenderedThisLine = false;
                DrawScanLine();
                if (windowWasRenderedThisLine)
                    windowLineCounter++;
                scanLineRendered = true;

                // Compute variable Mode 3 duration for accurate STAT mode boundaries.
                int spriteCount = CountSpritesOnLine(mem[0xFF44]);
                int scxPenalty = mem[0xFF43] & 7;
                int windowPenalty = windowWasRenderedThisLine ? 6 : 0;
                currentMode3Duration = 172 + scxPenalty + (spriteCount * 6) + windowPenalty;
                if (currentMode3Duration > 289) currentMode3Duration = 289;
            }

            SetLCDStatus();
        }

        public void DMATransfer(byte value)
        {
            uint address = (uint)(value << 8);
            for (int i = 0; i < 0xA0; i++)
                _mmu.WriteByteToMemory((uint)(0xFE00 + i), _mmu.ReadByteFromMemory(address + (uint)i));
        }

        public bool ConsumeFrameReady()
        {
            bool ready = _frameReady;
            _frameReady = false;
            return ready;
        }

        // ----- Internal methods -----

        private void SetLCDStatus()
        {
            byte[] mem = _mmu.Memory;
            byte status = mem[0xFF41];
            byte currentline = mem[0xFF44];
            byte currentmode = (byte)(status & 0x03);
            bool reqInt = false;
            int mode;

            if (currentline >= 144)
            {
                mode = 1;
                status = (byte)((status & 0xFC) | 0x01);
                reqInt = (status & 0x10) != 0;
            }
            else
            {
                int mode2bounds = 456 - 80;
                int mode3bounds = mode2bounds - currentMode3Duration;
                if (ScanLineCounter >= mode2bounds)
                {
                    mode = 2;
                    status = (byte)((status & 0xFC) | 0x02);
                    reqInt = (status & 0x20) != 0;
                }
                else if (ScanLineCounter >= mode3bounds)
                {
                    mode = 3;
                    status = (byte)((status & 0xFC) | 0x03);
                }
                else
                {
                    mode = 0;
                    status = (byte)(status & 0xFC);
                    reqInt = (status & 0x08) != 0;
                }
            }

            // Fire STAT interrupt only on mode TRANSITION
            if (reqInt && (mode != currentmode))
                RequestInterrupt(1);

            // LYC coincidence: fire only on RISING EDGE (transition to match)
            bool lycMatching = (currentline == mem[0xFF45]);
            if (lycMatching)
            {
                status |= 0x04;
                if (!lycWasMatching && (status & 0x40) != 0)
                    RequestInterrupt(1);
            }
            else
            {
                status &= unchecked((byte)~0x04);
            }
            lycWasMatching = lycMatching;

            mem[0xFF41] = status;
        }

        /// <summary>
        /// Counts how many sprites are visible on the given scanline (max 10).
        /// Used to compute variable Mode 3 duration.
        /// </summary>
        private int CountSpritesOnLine(int scanline)
        {
            byte[] mem = _mmu.Memory;
            int ysize = (mem[0xFF40] & 0x04) != 0 ? 16 : 8;
            int count = 0;
            for (int sprite = 0; sprite < 40 && count < 10; sprite++)
            {
                byte yPos = (byte)(mem[0xFE00 + sprite * 4] - 16);
                if (scanline >= yPos && scanline < yPos + ysize)
                    count++;
            }
            return count;
        }

        private void DrawScanLine()
        {
            byte[] mem = _mmu.Memory;
            byte lcdControl = mem[0xFF40];
            if ((lcdControl & 0x80) == 0) return;

            byte currentLine = mem[0xFF44];

            if ((lcdControl & 0x01) != 0)
            {
                RenderTiles(lcdControl, currentLine);
            }
            else
            {
                // BG disabled: fill with colour 0 from BG palette
                byte palette = mem[0xFF47];
                uint col = PaletteLUT[palette * 4]; // colour index 0
                int rowBase = currentLine * Width;
                for (int pixel = 0; pixel < Width; pixel++)
                {
                    _bgColorIndex[rowBase + pixel] = 0;
                    _screenBuffer[rowBase + pixel] = col;
                }
            }
            RenderSprites(lcdControl, currentLine);
        }

        private void RenderTiles(byte lcdControl, byte currentLine)
        {
            byte[] mem = _mmu.Memory;
            byte scrollY = mem[0xFF42];
            byte scrollX = mem[0xFF43];
            byte windowY = mem[0xFF4A];
            byte windowX = (byte)(mem[0xFF4B] - 7);
            byte bgPalette = mem[0xFF47];

            bool windowEnabled = (lcdControl & 0x20) != 0 && (windowY <= currentLine);

            ushort tileData;
            bool unsig;
            if ((lcdControl & 0x10) != 0)
            {
                tileData = 0x8000;
                unsig = true;
            }
            else
            {
                tileData = 0x8800;
                unsig = false;
            }

            int rowBase = currentLine * Width;
            int palBase = bgPalette * 4;

            // Cache tile row data to avoid re-reading for every pixel in the same tile
            int cachedTileX = -1;
            bool cachedIsWindow = false;
            byte data1 = 0, data2 = 0;

            for (int pixel = 0; pixel < Width; pixel++)
            {
                bool useWindow = windowEnabled && (pixel >= windowX);

                byte xPos, yPos;
                uint tileMapBase;

                if (useWindow)
                {
                    xPos = (byte)(pixel - windowX);
                    yPos = (byte)windowLineCounter;
                    tileMapBase = (lcdControl & 0x40) != 0 ? 0x9C00u : 0x9800u;
                    windowWasRenderedThisLine = true;
                }
                else
                {
                    xPos = (byte)(pixel + scrollX);
                    yPos = (byte)(scrollY + currentLine);
                    tileMapBase = (lcdControl & 0x08) != 0 ? 0x9C00u : 0x9800u;
                }

                int tileX = xPos >> 3;

                // Only re-fetch tile data when crossing a tile boundary or BG↔Window switch
                if (tileX != cachedTileX || useWindow != cachedIsWindow)
                {
                    cachedTileX = tileX;
                    cachedIsWindow = useWindow;

                    uint tileAddress = tileMapBase + (uint)((yPos >> 3) * 32) + (uint)tileX;

                    int tileLocation;
                    if (unsig)
                        tileLocation = tileData + mem[tileAddress] * 16;
                    else
                        tileLocation = tileData + (unchecked((sbyte)mem[tileAddress]) + 128) * 16;

                    int line = (yPos & 7) * 2;
                    data1 = mem[tileLocation + line];
                    data2 = mem[tileLocation + line + 1];
                }

                int colourBit = 7 - (xPos & 7);
                int colourNum = ((data2 >> colourBit) & 1) << 1 | ((data1 >> colourBit) & 1);

                int bufIdx = rowBase + pixel;
                _bgColorIndex[bufIdx] = (byte)colourNum;
                _screenBuffer[bufIdx] = PaletteLUT[palBase + colourNum];
            }
        }

        private void RenderSprites(byte lcdControl, byte scanline)
        {
            if ((lcdControl & 0x02) == 0) return;

            byte[] mem = _mmu.Memory;
            bool use8x16 = (lcdControl & 0x04) != 0;
            int ysize = use8x16 ? 16 : 8;

            // Collect visible sprites into pre-allocated arrays (zero allocation)
            _spriteCount = 0;
            for (int sprite = 0; sprite < 40 && _spriteCount < 10; sprite++)
            {
                int addr = 0xFE00 + sprite * 4;
                byte yPos = (byte)(mem[addr] - 16);
                if (scanline >= yPos && scanline < yPos + ysize)
                {
                    int i = _spriteCount++;
                    _spriteOam[i] = sprite;
                    _spriteX[i] = (byte)(mem[addr + 1] - 8);
                    _spriteY[i] = yPos;
                    _spriteTile[i] = mem[addr + 2];
                    _spriteAttr[i] = mem[addr + 3];
                }
            }

            // Insertion sort in reverse priority order (≤10 items, no allocation)
            for (int i = 1; i < _spriteCount; i++)
            {
                int j = i;
                while (j > 0)
                {
                    bool swap = _spriteX[j] != _spriteX[j - 1]
                        ? _spriteX[j] > _spriteX[j - 1]
                        : _spriteOam[j] > _spriteOam[j - 1];
                    if (!swap) break;

                    (_spriteOam[j], _spriteOam[j - 1]) = (_spriteOam[j - 1], _spriteOam[j]);
                    (_spriteX[j], _spriteX[j - 1]) = (_spriteX[j - 1], _spriteX[j]);
                    (_spriteY[j], _spriteY[j - 1]) = (_spriteY[j - 1], _spriteY[j]);
                    (_spriteTile[j], _spriteTile[j - 1]) = (_spriteTile[j - 1], _spriteTile[j]);
                    (_spriteAttr[j], _spriteAttr[j - 1]) = (_spriteAttr[j - 1], _spriteAttr[j]);
                    j--;
                }
            }

            int rowBase = scanline * Width;

            for (int s = 0; s < _spriteCount; s++)
            {
                byte xPos = _spriteX[s];
                byte yPos = _spriteY[s];
                byte attr = _spriteAttr[s];
                bool yFlip = (attr & 0x40) != 0;
                bool xFlip = (attr & 0x20) != 0;

                byte tile = _spriteTile[s];
                if (use8x16) tile &= 0xFE;

                int line = scanline - yPos;
                if (yFlip) line = (ysize - 1) - line;
                line *= 2;

                int dataAddress = 0x8000 + tile * 16 + line;
                byte data1 = mem[dataAddress];
                byte data2 = mem[dataAddress + 1];

                byte palette = (attr & 0x10) != 0 ? mem[0xFF49] : mem[0xFF48];
                int palBase = palette * 4;

                for (int tilePixel = 7; tilePixel >= 0; tilePixel--)
                {
                    int colourbit = xFlip ? 7 - tilePixel : tilePixel;
                    int colourNum = ((data2 >> colourbit) & 1) << 1 | ((data1 >> colourbit) & 1);

                    if (colourNum == 0) continue;

                    int pixel = xPos + (7 - tilePixel);
                    if ((uint)pixel >= Width) continue;

                    int bufIdx = rowBase + pixel;
                    if ((attr & 0x80) != 0 && _bgColorIndex[bufIdx] != 0)
                        continue;

                    _screenBuffer[bufIdx] = PaletteLUT[palBase + colourNum];
                }
            }
        }
    }
}