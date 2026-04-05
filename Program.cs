// ============================================================================
// Project:     GameboyEmu
// File:        Program.cs
// Description: Application entry point - SDL startup, ROM menu/launch flow,
//              DMG-only ROM guard, and game session lifecycle loop
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.IO;
using System.Linq;
using GameboyEmu.Core;

#nullable enable

namespace GameboyEmu
{
    internal static class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("GameBoy Emulator starting...");

            // Parse --nobootrom switch
            bool noBoot = args.Contains("--nobootrom", StringComparer.OrdinalIgnoreCase);
            args = args.Where(a => !a.Equals("--nobootrom", StringComparison.OrdinalIgnoreCase)).ToArray();

            // Register native library resolver so SDL2 can be found on all platforms
            SDL.RegisterResolver();

            using var display = new SDLDisplay();

            // --- Game session loop: returns to menu on reset ---
            bool keepRunning = true;
            int lastMenuSelected = 0;
            int lastMenuScrollOffset = 0;
            while (keepRunning && display.IsOpen)
            {
                string? romPath = null;
                bool skipBootForThisLaunch = false;
                bool selectedFromCommandLine = false;
                bool gameRomAvailable = false;

                if (args.Length > 0 && keepRunning)
                {
                    // Command-line argument takes priority (first iteration only)
                    romPath = args[0];
                    selectedFromCommandLine = true;
                    gameRomAvailable = File.Exists(romPath);
                    args = Array.Empty<string>(); // clear so subsequent loops show menu
                }
                else
                {
                    // Scan the ROMs folder for .gb files
                    string romsDir = Path.Combine(AppContext.BaseDirectory, "ROMs");
                    if (!Directory.Exists(romsDir))
                        romsDir = "ROMs";

                    if (Directory.Exists(romsDir))
                    {
                        var romFiles = Directory.GetFiles(romsDir, "*.gb")
                            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (romFiles.Count > 0)
                        {
                            gameRomAvailable = true;
                            var romNames = romFiles
                                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                                .ToList();

                            Console.WriteLine($"Found {romFiles.Count} ROM(s)");
                            Console.WriteLine("Use Up/Down to select, Enter to launch, Ctrl+Enter to launch without boot ROM, Esc to quit.");

                            var menuSelection = display.ShowRomMenu(
                                romFiles,
                                romNames,
                                lastMenuSelected,
                                lastMenuScrollOffset);
                            romPath = menuSelection.RomPath;
                            skipBootForThisLaunch = menuSelection.SkipBootRom;
                            lastMenuSelected = menuSelection.SelectedIndex;
                            lastMenuScrollOffset = menuSelection.ScrollOffset;

                            if (romPath == null)
                            {
                                // User closed the window during menu
                                keepRunning = false;
                                break;
                            }
                        }
                    }
                }

                // --- Create GameBoy and load ROM ---
                if (romPath == null)
                {
                    if (gameRomAvailable)
                    {
                        // ROMs exist, but none selected/launched in this iteration.
                        keepRunning = false;
                        break;
                    }

                    const string title = "No game ROMs found.";
                    const string details = "Place at least one .gb file in the ROMs folder.";

                    Console.WriteLine(title);
                    Console.WriteLine(details);
                    display.ShowStartupError(title, details);
                    keepRunning = false;
                    break;
                }

                if (romPath != null && IsCgbOnlyRom(romPath, out byte cgbFlag))
                {
                    Console.WriteLine($"Skipping CGB-only ROM in DMG emulator: {romPath} (header CGB flag 0x{cgbFlag:X2}).");
                    if (selectedFromCommandLine)
                        keepRunning = false;
                    continue;
                }

                var gb = new GameBoy();
                gb.aPU.InitAudio();

                if (romPath != null)
                {
                    int romSize = (int)new FileInfo(romPath).Length;
                    gb.LoadCartridge(romPath, romSize, noBoot || skipBootForThisLaunch);
                    Console.WriteLine($"Loaded ROM: {romPath} ({romSize} bytes)");
                }
                else
                {
                    Console.WriteLine("No ROMs found — running boot ROM only.");
                }

                // Wire the SDL display into the emulation loop
                gb.OnFrameReady = () =>
                {
                    display.PollEvents(gb);
                    display.RenderFrame(gb.pPU.ScreenBuffer);
                };

                Console.WriteLine("Controls: Arrows/WASD=D-Pad, Z/M=A, X/N=B, Enter=Start, Space=Select, Esc=Reset");
                gb.Start();

                bool wasReset = gb.ResetRequested;
                gb.mMU.SaveBattery(); // Save battery-backed RAM before cleanup
                gb.aPU.Dispose();

                if (!wasReset)
                {
                    // User pressed Escape or closed the window — exit entirely
                    keepRunning = false;
                }
                else
                {
                    Console.WriteLine("Reset — returning to ROM menu...");
                }
            }

            Console.WriteLine("Emulator stopped.");
        }

        private static bool IsCgbOnlyRom(string romPath, out byte cgbFlag)
        {
            cgbFlag = 0x00;
            try
            {
                using var fs = File.OpenRead(romPath);
                if (fs.Length <= 0x143)
                    return false;

                fs.Position = 0x143;
                int flag = fs.ReadByte();
                if (flag < 0)
                    return false;

                cgbFlag = (byte)flag;
                return cgbFlag == 0xC0;
            }
            catch
            {
                return false;
            }
        }
    }
}