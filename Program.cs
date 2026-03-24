// ============================================================================
// Project:     GameboyEmu
// File:        Program.cs
// Description: Entry point — ROM scanning, menu display, and emulation loop
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

            // Parse --noboot switch
            bool noBoot = args.Contains("--noboot", StringComparer.OrdinalIgnoreCase);
            args = args.Where(a => !a.Equals("--noboot", StringComparison.OrdinalIgnoreCase)).ToArray();

            // Register native library resolver so SDL2 can be found on all platforms
            SDL.RegisterResolver();

            using var display = new SDLDisplay();

            // --- Game session loop: returns to menu on reset ---
            bool keepRunning = true;
            while (keepRunning && display.IsOpen)
            {
                string? romPath = null;

                if (args.Length > 0 && keepRunning)
                {
                    // Command-line argument takes priority (first iteration only)
                    romPath = args[0];
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
                            var romNames = romFiles
                                .Select(f => Path.GetFileNameWithoutExtension(f)!)
                                .ToList();

                            Console.WriteLine($"Found {romFiles.Count} ROM(s)");
                            Console.WriteLine("Use Up/Down to select, Enter to launch, Esc to quit.");

                            romPath = display.ShowRomMenu(romFiles, romNames);

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
                var gb = new GameBoy();
                gb.aPU.InitAudio();

                if (romPath != null)
                {
                    int romSize = (int)new FileInfo(romPath).Length;
                    gb.LoadCartridge(romPath, romSize, noBoot);
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
                    display.RenderFrame(gb.LCD);
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
    }
}