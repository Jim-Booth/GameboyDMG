// ============================================================================
// Project:     GameboyEmu
// File:        Core/MMU.cs
// Description: Memory Management Unit — 64 KB address space with MBC1/MBC2/
//              MBC3/MBC5 cartridge banking, ROM/RAM mapping, and I/O routing
//              Optimised: removed unused imports, timer speed lookup table
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.IO;

#nullable enable

namespace GameboyEmu.Core
{
    public enum MBCType { None, MBC1, MBC2, MBC3, MBC5 }

    public class MMU(GameBoy gb)
    {
        public byte[] Memory { get; set; } = new byte[0x10000];
        public byte[] Cartridge { get; set; } = new byte[0x200000];

        public MBCType MapperType = MBCType.None;
        public byte CurrentROMBank = 1;
        private byte _romBankHigh = 0;       // MBC1 upper 2 bits / MBC5 9th bit
        private bool _mbc1AdvancedMode = false; // MBC1 banking mode (0=16Mbit ROM/8KB RAM, 1=4Mbit ROM/32KB RAM)
        private byte CurrentRAMBank = 0;
        private readonly byte[] RAMBanks = new byte[0x20000]; // 128 KB max (MBC5 can have 16 banks)
        private bool EnableRAM = false;
        private int _romBankCount = 2;
        private int _ramSize = 0;

        // Battery-backed save support
        private bool _hasBattery = false;
        private string? _savePath = null;

        // MBC3 RTC registers
        private byte _rtcS, _rtcM, _rtcH, _rtcDL, _rtcDH;
        private byte _rtcLatchedS, _rtcLatchedM, _rtcLatchedH, _rtcLatchedDL, _rtcLatchedDH;
        private byte _rtcLatchPrev = 0xFF;
        private bool _rtcMapped = false;     // true when 0x08-0x0C is selected instead of RAM bank
        private byte _rtcSelectedReg = 0;

        private static readonly int[] TimerClockSpeeds = { 1024, 16, 64, 256 };

        private readonly GameBoy gameboy = gb;

        /// <summary>APU instance – set by GameBoy after construction.</summary>
        public APU Apu { get; set; } = null!;

        public byte IF { get { return Memory[0xFF0F]; } set { Memory[0xFF0F] = value; } }// 0xFF0F - Interrupt Flag (R/W)

        public byte IE { get { return Memory[0xFFFF]; } set { Memory[0xFFFF] = value; } } // 0xFFFF IE - Interrupt Enable (R/W)       

        public void WriteByteToMemory(uint addr, byte value)
        {
            //memory[addr] = value; return;  // Required for JSON Tests

            addr &= 0xFFFF;

            // ===== MBC register writes (ROM area 0x0000–0x7FFF) =====

            if (addr <= 0x1FFF)
            {
                // RAM enable (all MBCs: 0x0A in lower nibble = enable)
                switch (MapperType)
                {
                    case MBCType.MBC1:
                    case MBCType.MBC3:
                    case MBCType.MBC5:
                        EnableRAM = (value & 0x0F) == 0x0A;
                        break;
                    case MBCType.MBC2:
                        // MBC2: bit 0 of upper address byte must be 0
                        if ((addr & 0x0100) == 0)
                            EnableRAM = (value & 0x0F) == 0x0A;
                        break;
                }
            }

            else if (addr >= 0x2000 && addr <= 0x3FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                        value &= 0x1F;
                        if (value == 0) value = 1;  // MBC1: bank 0 → bank 1
                        CurrentROMBank = (byte)((CurrentROMBank & 0xE0) | value);
                        break;
                    case MBCType.MBC2:
                        // MBC2: bit 0 of upper address byte must be 1
                        if ((addr & 0x0100) != 0)
                            CurrentROMBank = (byte)(value & 0x0F);
                        if (CurrentROMBank == 0) CurrentROMBank = 1;
                        break;
                    case MBCType.MBC3:
                        value &= 0x7F;
                        if (value == 0) value = 1;  // MBC3: bank 0 → bank 1
                        CurrentROMBank = value;
                        break;
                    case MBCType.MBC5:
                        if (addr <= 0x2FFF)
                        {
                            // Low 8 bits of ROM bank
                            CurrentROMBank = value;
                        }
                        else
                        {
                            // Bit 0 = 9th bit of ROM bank number
                            _romBankHigh = (byte)(value & 0x01);
                        }
                        // MBC5: bank 0 is valid — no adjustment needed
                        break;
                }
            }

            else if (addr >= 0x4000 && addr <= 0x5FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                        value &= 0x03;
                        if (_mbc1AdvancedMode)
                        {
                            CurrentRAMBank = value;
                        }
                        else
                        {
                            _romBankHigh = value;
                            CurrentROMBank = (byte)(((value << 5) | (CurrentROMBank & 0x1F)));
                            if ((CurrentROMBank & 0x1F) == 0)
                                CurrentROMBank++;
                        }
                        break;
                    case MBCType.MBC3:
                        if (value <= 0x03)
                        {
                            CurrentRAMBank = value;
                            _rtcMapped = false;
                        }
                        else if (value >= 0x08 && value <= 0x0C)
                        {
                            _rtcMapped = true;
                            _rtcSelectedReg = value;
                        }
                        break;
                    case MBCType.MBC5:
                        CurrentRAMBank = (byte)(value & 0x0F);
                        break;
                }
            }

            else if (addr >= 0x6000 && addr <= 0x7FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                        _mbc1AdvancedMode = (value & 0x01) != 0;
                        if (_mbc1AdvancedMode)
                        {
                            // In advanced/RAM mode, upper ROM bits go to RAM bank
                            CurrentRAMBank = _romBankHigh;
                        }
                        else
                        {
                            CurrentRAMBank = 0;
                        }
                        break;
                    case MBCType.MBC3:
                        // RTC latch: writing 0x00 then 0x01 latches current time
                        if (_rtcLatchPrev == 0x00 && value == 0x01)
                        {
                            _rtcLatchedS = _rtcS;
                            _rtcLatchedM = _rtcM;
                            _rtcLatchedH = _rtcH;
                            _rtcLatchedDL = _rtcDL;
                            _rtcLatchedDH = _rtcDH;
                        }
                        _rtcLatchPrev = value;
                        break;
                }
            }

            else if (addr >= 0xA000 && addr < 0xC000)
            {
                if (EnableRAM)
                {
                    switch (MapperType)
                    {
                        case MBCType.MBC1:
                        case MBCType.MBC5:
                            {
                                uint ramAddr = (uint)((addr - 0xA000) + CurrentRAMBank * 0x2000);
                                if (ramAddr < RAMBanks.Length)
                                    RAMBanks[ramAddr] = value;
                                break;
                            }
                        case MBCType.MBC2:
                            {
                                if (addr < 0xA200)
                                {
                                    uint ramAddr = addr - 0xA000;
                                    RAMBanks[ramAddr] = (byte)(value & 0x0F); // MBC2: 4-bit RAM
                                }
                                break;
                            }
                        case MBCType.MBC3:
                            {
                                if (_rtcMapped)
                                {
                                    switch (_rtcSelectedReg)
                                    {
                                        case 0x08: _rtcS = value; break;
                                        case 0x09: _rtcM = value; break;
                                        case 0x0A: _rtcH = value; break;
                                        case 0x0B: _rtcDL = value; break;
                                        case 0x0C: _rtcDH = value; break;
                                    }
                                }
                                else
                                {
                                    uint ramAddr = (uint)((addr - 0xA000) + CurrentRAMBank * 0x2000);
                                    if (ramAddr < RAMBanks.Length)
                                        RAMBanks[ramAddr] = value;
                                }
                                break;
                            }
                        default:
                            {
                                uint ramAddr = addr - 0xA000;
                                if (ramAddr < RAMBanks.Length)
                                    RAMBanks[ramAddr] = value;
                                break;
                            }
                    }
                }
            }

            else if ((addr >= 0xC000) && (addr <= 0xDFFF))
            {
                Memory[addr] = value;
            }

            else if (addr >= 0xE000 && addr < 0xFE00)
            {
                Memory[addr] = value;
                Memory[addr - 0x2000] = value;
            }

            else if (addr >= 0xFEA0 && addr < 0xFEFF && value > 0)
            { }

            else if (0xFF04 == addr)
            {
                Memory[0xFF04] = 0;
                gameboy.DivCounter = 0;
            }

            else if (addr == 0xFF07)
            {
                Memory[addr] = value;

                int clockSpeed = TimerClockSpeeds[value & 0x03];

                if (clockSpeed != gameboy!.TimerCounter)
                {
                    gameboy!.TimerVariable = 0;
                    gameboy!.TimerCounter = clockSpeed;
                }
            }


            else if (addr == 0xFF0F)
            {
                IF = value;
            }

            else if (addr >= 0xFF10 && addr <= 0xFF3F) // Audio registers → APU
            {
                Apu.WriteRegister(addr, value);
            }

            else if (addr == 0xFF44)
            {
                Memory[addr] = 0;
            }

            else if (addr == 0xFF46)
                gameboy.DMATransfer(value);

            else if ((addr >= 0xFF4C) && (addr <= 0xFF7F))
            {
            }

            else if (addr == 0xFFFF)
            {
                IE = value;
            }
            else
                Memory[addr] = value;

        }

        public byte ReadByteFromMemory(uint addr)
        {

            //return memory[addr]; // Required for JSON Tests

            if (addr >= 0x4000 && addr <= 0x7FFF)
            {
                int effectiveBank;
                if (MapperType == MBCType.MBC5)
                    effectiveBank = CurrentROMBank | (_romBankHigh << 8);
                else
                    effectiveBank = CurrentROMBank;

                uint romAddr = (uint)((addr - 0x4000) + effectiveBank * 0x4000);
                if (romAddr < Cartridge.Length)
                    return Cartridge[romAddr];
                return 0xFF;
            }

            else if (addr >= 0xA000 && addr <= 0xBFFF)
            {
                if (!EnableRAM && MapperType != MBCType.None)
                    return 0xFF;

                if (MapperType == MBCType.MBC3 && _rtcMapped)
                {
                    return _rtcSelectedReg switch
                    {
                        0x08 => _rtcLatchedS,
                        0x09 => _rtcLatchedM,
                        0x0A => _rtcLatchedH,
                        0x0B => _rtcLatchedDL,
                        0x0C => _rtcLatchedDH,
                        _ => 0xFF,
                    };
                }

                if (MapperType == MBCType.MBC2)
                {
                    if (addr >= 0xA200) return 0xFF;
                    return (byte)(RAMBanks[addr - 0xA000] | 0xF0); // upper 4 bits read as 1
                }

                uint ramAddr = (uint)((addr - 0xA000) + CurrentRAMBank * 0x2000);
                if (ramAddr < RAMBanks.Length)
                    return RAMBanks[ramAddr];
                return 0xFF;
            }

            else if (addr == 0xFF00)
            {
                return gameboy!.GetKeypadState();
            }

            else if (addr == 0xFF04) return Memory[0xFF04];

            else if (addr >= 0xFF10 && addr <= 0xFF3F) // Audio registers → APU
            {
                return Apu.ReadRegister(addr);
            }

            else if (addr == 0xFF0F)
            {
                return IF;
            }

            else if (addr == 0xFFFF)
            {
                return IE;
            }

            return Memory[addr];
        }

        public uint ReadWordFromMemory(uint addr)
        {
            return (uint)ReadByteFromMemory(addr + 1) << 8 | ReadByteFromMemory(addr);
        }

        public void WriteWordToMemory(uint addr, uint value)
        {
            WriteByteToMemory(addr + 1, (byte)(value >> 8));
            WriteByteToMemory(addr, (byte)value);
        }

        public void InitROMBanks()
        {
            byte cartridgeType = Cartridge[0x0147];
            byte romSizeByte = Cartridge[0x0148];
            byte ramSizeByte = Cartridge[0x0149];

            // Determine mapper type from cartridge header
            MapperType = cartridgeType switch
            {
                0x00 => MBCType.None,         // ROM only
                0x01 or 0x02 or 0x03 => MBCType.MBC1,
                0x05 or 0x06 => MBCType.MBC2,
                0x0F or 0x10 or 0x11 or 0x12 or 0x13 => MBCType.MBC3,
                0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => MBCType.MBC5,
                _ => MBCType.None,            // fallback
            };

            // ROM bank count: 2 << romSizeByte (each bank is 16 KB)
            _romBankCount = romSizeByte <= 8 ? (2 << romSizeByte) : 2;

            // RAM size
            _ramSize = ramSizeByte switch
            {
                0x00 => 0,
                0x01 => 2048,       // 2 KB (unused in practice)
                0x02 => 8192,       // 8 KB  (1 bank)
                0x03 => 32768,      // 32 KB (4 banks)
                0x04 => 131072,     // 128 KB (16 banks)
                0x05 => 65536,      // 64 KB (8 banks)
                _ => 0,
            };

            // MBC2 has built-in 512×4-bit RAM regardless of header
            if (MapperType == MBCType.MBC2)
                _ramSize = 512;

            // Determine if cartridge has a battery
            _hasBattery = cartridgeType switch
            {
                0x03 => true,   // MBC1+RAM+BATTERY
                0x06 => true,   // MBC2+BATTERY
                0x09 => true,   // ROM+RAM+BATTERY
                0x0D => true,   // MMM01+RAM+BATTERY
                0x0F => true,   // MBC3+TIMER+BATTERY
                0x10 => true,   // MBC3+TIMER+RAM+BATTERY
                0x13 => true,   // MBC3+RAM+BATTERY
                0x1B => true,   // MBC5+RAM+BATTERY
                0x1E => true,   // MBC5+RUMBLE+RAM+BATTERY
                _ => false,
            };

            CurrentROMBank = 1;
            CurrentRAMBank = 0;
            EnableRAM = false;
            _romBankHigh = 0;
            _mbc1AdvancedMode = false;
            _rtcMapped = false;

            Console.WriteLine($"[MMU] Cartridge type: 0x{cartridgeType:X2} → {MapperType}, ROM banks: {_romBankCount}, RAM: {_ramSize} , Battery: {_hasBattery}");

            // Load battery-backed save if present
            if (_hasBattery && _savePath != null)
                LoadSave();
        }

        /// <summary>
        /// Sets the save file path based on the ROM file path.
        /// Called by GameBoy before InitROMBanks so the save can be loaded.
        /// </summary>
        public void SetSavePath(string romPath)
        {
            _savePath = Path.ChangeExtension(romPath, ".sav");
        }

        /// <summary>
        /// Loads battery-backed RAM from a .sav file if it exists.
        /// </summary>
        private void LoadSave()
        {
            if (_savePath == null || !File.Exists(_savePath)) return;

            try
            {
                byte[] data = File.ReadAllBytes(_savePath);
                int copyLen = Math.Min(data.Length, RAMBanks.Length);
                Array.Copy(data, 0, RAMBanks, 0, copyLen);
                Console.WriteLine($"[MMU] Loaded save: {_savePath} ({copyLen} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MMU] Failed to load save: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves battery-backed RAM to a .sav file.
        /// Called when the emulator exits or resets.
        /// </summary>
        public void SaveBattery()
        {
            if (!_hasBattery || _savePath == null || _ramSize == 0) return;

            try
            {
                byte[] data = new byte[_ramSize];
                Array.Copy(RAMBanks, 0, data, 0, _ramSize);
                File.WriteAllBytes(_savePath, data);
                Console.WriteLine($"[MMU] Saved battery RAM: {_savePath} ({_ramSize} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MMU] Failed to save: {ex.Message}");
            }
        }

    }

}