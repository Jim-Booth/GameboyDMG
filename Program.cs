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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameboyEmu.Core;

#nullable enable

namespace GameboyEmu
{
    internal static class Program
    {
        // Executes main.
        static void Main(string[] args)
        {
            Console.WriteLine("GameBoy Emulator starting...");

            bool noBoot = false;
            string? romPathOverride = null;
            var positionalArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--nobootrom", StringComparison.OrdinalIgnoreCase))
                {
                    noBoot = true;
                    continue;
                }

                if (arg.StartsWith("--rompath=", StringComparison.OrdinalIgnoreCase))
                {
                    romPathOverride = arg.Substring("--rompath=".Length);
                    continue;
                }

                if (arg.Equals("--rompath", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("Missing value for --rompath.");
                        return;
                    }

                    romPathOverride = args[++i];
                    continue;
                }

                positionalArgs.Add(arg);
            }

            if (string.IsNullOrWhiteSpace(romPathOverride))
                romPathOverride = null;

            string[] launchArgs = positionalArgs.ToArray();
            string? startupRomPath = launchArgs.Length > 0 ? launchArgs[0] : null;
            string? romDirectoryOverride = null;

            if (romPathOverride != null)
            {
                if (Directory.Exists(romPathOverride))
                    romDirectoryOverride = romPathOverride;
                else
                    startupRomPath = romPathOverride;
            }

            SDL.RegisterResolver();

            using var display = new SDLDisplay();

            bool keepRunning = true;
            int lastMenuSelected = 0;
            int lastMenuScrollOffset = 0;
            while (keepRunning && display.IsOpen)
            {
                string? romPath = null;
                bool skipBootForThisLaunch = false;
                bool selectedFromCommandLine = false;
                bool gameRomAvailable = false;

                if (startupRomPath != null && keepRunning)
                {
                    romPath = startupRomPath;
                    selectedFromCommandLine = true;
                    gameRomAvailable = File.Exists(romPath);
                    startupRomPath = null;
                }
                else
                {
                    string romsDir;
                    if (romDirectoryOverride != null)
                    {
                        romsDir = romDirectoryOverride;
                        if (!Directory.Exists(romsDir))
                        {
                            const string title = "ROM path not found.";
                            string details = $"The --rompath directory does not exist: {romsDir}";
                            Console.WriteLine(title);
                            Console.WriteLine(details);
                            display.ShowStartupError(title, details);
                            keepRunning = false;
                            break;
                        }
                    }
                    else
                    {
                        romsDir = Path.Combine(AppContext.BaseDirectory, "ROMs");
                        if (!Directory.Exists(romsDir))
                            romsDir = "ROMs";
                    }

                    if (Directory.Exists(romsDir))
                    {
                        var romFiles = Directory.GetFiles(romsDir, "*.gb")
                            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (romFiles.Count == 0)
                        {
                            romFiles = Directory.GetFiles(romsDir, "*.gb", SearchOption.AllDirectories)
                                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        }

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
                                keepRunning = false;
                                break;
                            }
                        }
                    }
                }

                if (romPath == null)
                {
                    if (gameRomAvailable)
                    {
                        keepRunning = false;
                        break;
                    }

                    const string title = "No game ROMs found.";
                    string details = romDirectoryOverride != null
                        ? $"Place at least one .gb file in: {romDirectoryOverride}"
                        : "Place at least one .gb file in the ROMs folder.";

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

                if (romPath != null && !File.Exists(romPath))
                {
                    const string title = "ROM file not found.";
                    string details = $"Could not find ROM at path: {romPath}";
                    Console.WriteLine(title);
                    Console.WriteLine(details);
                    display.ShowStartupError(title, details);
                    keepRunning = false;
                    break;
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

                gb.OnFrameReady = () =>
                {
                    display.PollEvents(gb);
                    display.RenderFrame(gb.pPU.ScreenBuffer);
                };

                Console.WriteLine("Controls: Arrows/WASD=D-Pad, Z/M=A, X/N=B, Enter=Start, Space=Select, Esc=Reset");
                gb.Start();

                bool wasReset = gb.ResetRequested;
                gb.mMU.SaveBattery();
                gb.aPU.Dispose();

                if (!wasReset)
                {
                    keepRunning = false;
                }
                else
                {
                    Console.WriteLine("Reset — returning to ROM menu...");
                }
            }

            Console.WriteLine("Emulator stopped.");
        }

        // Executes is cgb only rom.
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
