// ============================================================================
// Project:     GameboyEmu
// File:        Core/PPU.cs
// Description: Pixel Processing Unit — scanline renderer for background,
//              window, and sprite layers
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
#nullable enable

namespace GameboyEmu.Core
{
    public class PPU
    {
        private const int CyclesPerScanline = 456;
        private readonly MMU _mmu;

        public int ScanLineCounter = CyclesPerScanline;

        private enum COLOUR { White, LightGray, DarkGray, Black }

        private readonly int[,,] ScreenData = new int[160, 144, 3];

        // Stores the raw BG/Window colour number (0-3) per pixel, before palette mapping.
        // Used by sprite renderer to implement BG priority (attr bit 7).
        private readonly int[,] bgColorIndex = new int[160, 144];

        public int[,,] LCD { get { return ScreenData; } }

        // Current Mode 3 duration for this scanline — varies with sprite count,
        // SCX fine scroll, and window usage.  Computed when a scanline is rendered.
        private int currentMode3Duration = 172;
        private bool scanLineRendered = false;
        private bool lycWasMatching = false;

        // Window internal line counter – only increments on scanlines where
        // the window was actually rendered.  Reset at frame start (VBlank).
        private int windowLineCounter = 0;
        private bool windowWasRenderedThisLine = false;

        private bool _frameReady;

        public PPU(MMU mmu)
        {
            _mmu = mmu;
        }

        // ----- Bit helpers (local copies) -----

        private static bool TestBit(byte data, int bitPos)
        {
            return (data & (1 << bitPos)) != 0;
        }

        private static int GetBit(byte data, int bitPos)
        {
            return TestBit(data, bitPos) ? 1 : 0;
        }

        private static byte SetBit(int register, int bitIndex, int newBitValue)
        {
            if (newBitValue == 1)
                return (byte)(register |= (1 << bitIndex));
            else
                return (byte)(register &= ~(1 << bitIndex));
        }

        private void RequestInterrupt(int id)
        {
            _mmu.IF = SetBit(_mmu.IF, id, 1);
        }

        // ----- Public API -----

        public void Update(int cycles)
        {
            if (!IsLCDEnabled())
            {
                // LCD off: reset scanline state, stay in mode 1 (VBlank)
                ScanLineCounter = CyclesPerScanline;
                _mmu.Memory[0xFF44] = 0;
                scanLineRendered = false;
                windowLineCounter = 0;
                byte stat = _mmu.ReadByteFromMemory(0xFF41);
                stat = (byte)((stat & 0xFC) | 0x01);
                _mmu.WriteByteToMemory(0xFF41, stat);
                return;
            }

            ScanLineCounter -= cycles;

            // Scanline boundary crossed - advance to next line
            if (ScanLineCounter <= 0)
            {
                ScanLineCounter += CyclesPerScanline;
                scanLineRendered = false;

                // Advance LY
                _mmu.Memory[0xFF44]++;

                // VBlank: LY reaches 144
                if (_mmu.Memory[0xFF44] == 144)
                {
                    RequestInterrupt(0);
                    // All 144 visible lines are complete.
                    _frameReady = true;
                }

                // Wrap after line 153
                if (_mmu.Memory[0xFF44] > 153)
                    _mmu.Memory[0xFF44] = 0;

                // Reset window line counter at the start of a new frame
                if (_mmu.Memory[0xFF44] == 0)
                    windowLineCounter = 0;
            }

            // Draw scanline when entering Mode 3 (after 80 cycles of Mode 2)
            // This gives STAT LYC interrupt handlers time to change scroll
            // registers before the scanline is actually rendered.
            int mode2End = 456 - 80;
            if (!scanLineRendered && ScanLineCounter <= mode2End
                && _mmu.Memory[0xFF44] < 144)
            {
                windowWasRenderedThisLine = false;
                DrawScanLine();
                if (windowWasRenderedThisLine)
                    windowLineCounter++;
                scanLineRendered = true;

                // Compute variable Mode 3 duration for accurate STAT mode boundaries.
                // Base: 172 dots. +SCX%8 for fine scroll penalty. +~6 per sprite on line. +6 if window active.
                int spriteCount = CountSpritesOnLine(_mmu.Memory[0xFF44]);
                int scxPenalty = _mmu.ReadByteFromMemory(0xFF43) % 8;
                int windowPenalty = windowWasRenderedThisLine ? 6 : 0;
                currentMode3Duration = 172 + scxPenalty + (spriteCount * 6) + windowPenalty;
                // Clamp to hardware maximum (289 dots)
                if (currentMode3Duration > 289) currentMode3Duration = 289;
            }

            SetLCDStatus();
        }

        public void DMATransfer(byte value)
        {
            uint address = (uint)(value << 8);
            for (int i = 0; i < 0xA0; i++)
                _mmu.WriteByteToMemory((uint)(0xFE00 + i), _mmu.ReadByteFromMemory((uint)(address + i)));
        }

        public bool ConsumeFrameReady()
        {
            bool frameReady = _frameReady;
            _frameReady = false;
            return frameReady;
        }

        // ----- Internal methods -----

        private bool IsLCDEnabled()
        {
            return TestBit(_mmu.ReadByteFromMemory(0xFF40), 7);
        }

        private void SetLCDStatus()
        {
            byte status = _mmu.ReadByteFromMemory(0xFF41);

            byte currentline = _mmu.ReadByteFromMemory(0xFF44);
            byte currentmode = (byte)(status & 0x3);
            bool reqInt = false;
            int mode = 0;

            if (currentline >= 144)
            {
                mode = 1;
                status = SetBit(status, 0, 1);
                status = SetBit(status, 1, 0);
                reqInt = TestBit(status, 4);
            }
            else
            {
                int mode2bounds = 456 - 80;
                int mode3bounds = mode2bounds - currentMode3Duration;
                if (ScanLineCounter >= mode2bounds)
                {
                    mode = 2;
                    status = SetBit(status, 1, 1);
                    status = SetBit(status, 0, 0);
                    reqInt = TestBit(status, 5);
                }
                else if (ScanLineCounter >= mode3bounds)
                {
                    mode = 3;
                    status = SetBit(status, 1, 1);
                    status = SetBit(status, 0, 1);
                }
                else
                {
                    mode = 0;
                    status = SetBit(status, 1, 0);
                    status = SetBit(status, 0, 0);
                    reqInt = TestBit(status, 3);
                }
            }

            // Fire STAT interrupt only on mode TRANSITION
            if (reqInt && (mode != currentmode))
                RequestInterrupt(1);

            // LYC coincidence: fire only on RISING EDGE (transition to match)
            bool lycMatching = (currentline == _mmu.ReadByteFromMemory(0xFF45));
            if (lycMatching)
            {
                status = SetBit(status, 2, 1);
                if (!lycWasMatching && TestBit(status, 6))
                    RequestInterrupt(1);
            }
            else
            {
                status = SetBit(status, 2, 0);
            }
            lycWasMatching = lycMatching;

            _mmu.WriteByteToMemory(0xFF41, status);
        }

        /// <summary>
        /// Counts how many sprites are visible on the given scanline (max 10).
        /// Used to compute variable Mode 3 duration.
        /// </summary>
        private int CountSpritesOnLine(int scanline)
        {
            bool use8x16 = TestBit(_mmu.ReadByteFromMemory(0xFF40), 2);
            int ysize = use8x16 ? 16 : 8;
            int count = 0;
            for (int sprite = 0; sprite < 40 && count < 10; sprite++)
            {
                byte yPos = (byte)(_mmu.ReadByteFromMemory((uint)(0xFE00 + sprite * 4)) - 16);
                if (scanline >= yPos && scanline < yPos + ysize)
                    count++;
            }
            return count;
        }

        private void DrawScanLine()
        {
            byte lcdControl = _mmu.Memory[0xFF40];
            if (TestBit(lcdControl, 7))
            {
                if (TestBit(lcdControl, 0))
                {
                    RenderTiles(lcdControl);
                }
                else
                {
                    // LCDC bit 0 clear: BG and Window are blank (colour 0)
                    byte currentLine = _mmu.ReadByteFromMemory(0xFF44);
                    COLOUR col = GetColour(0, 0xFF47);
                    int red = 155, green = 188, blue = 15;
                    switch (col)
                    {
                        case COLOUR.White: red = 155; green = 188; blue = 15; break;
                        case COLOUR.LightGray: red = 139; green = 172; blue = 15; break;
                        case COLOUR.DarkGray: red = 48; green = 98; blue = 48; break;
                        case COLOUR.Black: red = 15; green = 56; blue = 15; break;
                    }
                    for (int pixel = 0; pixel < 160; pixel++)
                    {
                        bgColorIndex[pixel, currentLine] = 0;
                        ScreenData[pixel, currentLine, 0] = red;
                        ScreenData[pixel, currentLine, 1] = green;
                        ScreenData[pixel, currentLine, 2] = blue;
                    }
                }
                RenderSprites(lcdControl);
            }
        }

        private void RenderTiles(byte lcdControl)
        {
            byte scrollY = _mmu.ReadByteFromMemory(0xFF42);
            byte scrollX = _mmu.ReadByteFromMemory(0xFF43);
            byte windowY = _mmu.ReadByteFromMemory(0xFF4A);
            byte windowX = (byte)(_mmu.ReadByteFromMemory(0xFF4B) - 7);
            byte currentLine = _mmu.ReadByteFromMemory(0xFF44);

            bool windowEnabled = TestBit(lcdControl, 5) && (windowY <= currentLine);

            ushort tileData;
            bool unsig = true;
            if (TestBit(lcdControl, 4))
                tileData = 0x8000;
            else
            {
                tileData = 0x8800;
                unsig = false;
            }

            for (int pixel = 0; pixel < 160; pixel++)
            {
                // Decide per-pixel whether this pixel falls in the window or background
                bool useWindow = windowEnabled && (pixel >= windowX);

                byte xPos, yPos;
                uint tileMapBase;

                if (useWindow)
                {
                    xPos = (byte)(pixel - windowX);
                    yPos = (byte)windowLineCounter;
                    tileMapBase = TestBit(lcdControl, 6) ? (uint)0x9C00 : 0x9800;
                    windowWasRenderedThisLine = true;
                }
                else
                {
                    xPos = (byte)(pixel + scrollX);
                    yPos = (byte)(scrollY + currentLine);
                    tileMapBase = TestBit(lcdControl, 3) ? (uint)0x9C00 : 0x9800;
                }

                uint tileRow = (uint)(((byte)(yPos / 8)) * 32);
                uint tileCol = (uint)(xPos / 8);
                uint tileAddress = tileMapBase + tileRow + tileCol;

                short tileNum;
                if (unsig)
                    tileNum = _mmu.ReadByteFromMemory(tileAddress);
                else
                    tileNum = unchecked((sbyte)(_mmu.ReadByteFromMemory(tileAddress)));

                ushort tileLocation = tileData;
                if (unsig)
                    tileLocation += (ushort)(tileNum * 16);
                else
                    tileLocation += (ushort)((tileNum + 128) * 16);

                byte line = (byte)(yPos % 8);
                line *= 2;
                byte data1 = _mmu.ReadByteFromMemory((uint)(tileLocation + line));
                byte data2 = _mmu.ReadByteFromMemory((uint)(tileLocation + line + 1));

                int colourBit = xPos % 8;
                colourBit -= 7;
                colourBit *= -1;

                int colourNum = GetBit(data2, colourBit);
                colourNum <<= 1;
                colourNum |= GetBit(data1, colourBit);

                COLOUR col = GetColour(colourNum, 0xFF47);
                int red = 15, green = 56, blue = 15;
                switch (col)
                {
                    case COLOUR.White: red = 155; green = 188; blue = 15; break;
                    case COLOUR.LightGray: red = 139; green = 172; blue = 15; break;
                    case COLOUR.DarkGray: red = 48; green = 98; blue = 48; break;
                }

                if ((currentLine > 143) || (pixel > 159))
                    continue;

                bgColorIndex[pixel, currentLine] = colourNum;
                ScreenData[pixel, currentLine, 0] = red;
                ScreenData[pixel, currentLine, 1] = green;
                ScreenData[pixel, currentLine, 2] = blue;
            }
        }

        private COLOUR GetColour(int colourNum, uint address)
        {
            COLOUR res = COLOUR.White;
            byte palette = _mmu.ReadByteFromMemory(address);
            int hi = 0;
            int lo = 0;
            switch (colourNum)
            {
                case 0: hi = 1; lo = 0; break;
                case 1: hi = 3; lo = 2; break;
                case 2: hi = 5; lo = 4; break;
                case 3: hi = 7; lo = 6; break;
            }
            int colour = 0;
            colour = GetBit(palette, hi) << 1;
            colour |= GetBit(palette, lo);
            switch (colour)
            {
                case 0: res = COLOUR.White; break;
                case 1: res = COLOUR.LightGray; break;
                case 2: res = COLOUR.DarkGray; break;
                case 3: res = COLOUR.Black; break;
            }
            return res;
        }

        private void RenderSprites(byte lcdControl)
        {
            if (!TestBit(lcdControl, 1))
                return;

            bool use8x16 = TestBit(lcdControl, 2);
            int ysize = use8x16 ? 16 : 8;
            int scanline = _mmu.ReadByteFromMemory(0xFF44);

            // Collect the first 10 visible sprites in OAM order (hardware limit)
            var sprites = new System.Collections.Generic.List<(int oamIdx, byte xPos, byte yPos, byte tileNum, byte attr)>();

            for (int sprite = 0; sprite < 40 && sprites.Count < 10; sprite++)
            {
                int addr = 0xFE00 + sprite * 4;
                byte yPos = (byte)(_mmu.ReadByteFromMemory((uint)addr) - 16);
                byte xPos = (byte)(_mmu.ReadByteFromMemory((uint)(addr + 1)) - 8);

                if ((scanline >= yPos) && (scanline < (yPos + ysize)))
                {
                    byte tileNum = _mmu.ReadByteFromMemory((uint)(addr + 2));
                    byte attr = _mmu.ReadByteFromMemory((uint)(addr + 3));
                    sprites.Add((sprite, xPos, yPos, tileNum, attr));
                }
            }

            // DMG priority: lower X wins; same X → lower OAM index wins.
            // Sort in REVERSE priority order so the highest-priority sprite
            // is drawn last and its pixels end up on top.
            sprites.Sort((a, b) =>
            {
                if (a.xPos != b.xPos) return b.xPos.CompareTo(a.xPos);
                return b.oamIdx.CompareTo(a.oamIdx);
            });

            foreach (var (oamIdx, xPos, yPos, tileNum, attr) in sprites)
            {
                bool yFlip = TestBit(attr, 6);
                bool xFlip = TestBit(attr, 5);

                // In 8x16 mode, bit 0 of the tile index is ignored
                byte tile = tileNum;
                if (use8x16)
                    tile &= 0xFE;

                int line = scanline - yPos;
                if (yFlip)
                    line = (ysize - 1) - line;

                line *= 2;

                uint dataAddress = (uint)(0x8000 + tile * 16 + line);
                byte data1 = _mmu.ReadByteFromMemory(dataAddress);
                byte data2 = _mmu.ReadByteFromMemory(dataAddress + 1);

                for (int tilePixel = 7; tilePixel >= 0; tilePixel--)
                {
                    int colourbit = tilePixel;
                    if (xFlip)
                    {
                        colourbit -= 7;
                        colourbit *= -1;
                    }
                    int colourNum = GetBit(data2, colourbit);
                    colourNum <<= 1;
                    colourNum |= GetBit(data1, colourbit);

                    // Colour 0 is always transparent for sprites
                    if (colourNum == 0)
                        continue;

                    uint colourAddress = (uint)(TestBit(attr, 4) ? 0xFF49 : 0xFF48);
                    COLOUR col = GetColour(colourNum, colourAddress);
                    int red = 155, green = 188, blue = 15;
                    switch (col)
                    {
                        case COLOUR.LightGray: red = 139; green = 172; blue = 15; break;
                        case COLOUR.DarkGray: red = 48; green = 98; blue = 48; break;
                        case COLOUR.Black: red = 15; green = 56; blue = 15; break;
                    }

                    int xPix = 7 - tilePixel;
                    int pixel = xPos + xPix;
                    if ((scanline < 0) || (scanline > 143) || (pixel < 0) || (pixel > 159))
                        continue;

                    if (TestBit(attr, 7))
                    {
                        // BG priority: sprite only visible where BG colour is 0
                        if (bgColorIndex[pixel, scanline] != 0)
                            continue;
                    }

                    ScreenData[pixel, scanline, 0] = red;
                    ScreenData[pixel, scanline, 1] = green;
                    ScreenData[pixel, scanline, 2] = blue;
                }
            }
        }
    }
}