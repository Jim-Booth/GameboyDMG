// ============================================================================
// Project:     GameboyEmu
// File:        Core/MMU.cs
// Description: Memory bus and cartridge controller - 64 KB address space,
//              MBC1/MBC2/MBC3/MBC5 banking, RTC, I/O routing, and save RAM
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
    // Defines the mbc type enum.
    public enum MBCType { None, MBC1, MBC2, MBC3, MBC5 }

    // Executes mmu.
    public class MMU(GameBoy gb)
    {
        // Gets or sets memory.
        public byte[] Memory { get; set; } = new byte[0x10000];
        // Gets or sets cartridge.
        public byte[] Cartridge { get; set; } = new byte[0x200000];

        public MBCType MapperType = MBCType.None;
        public byte CurrentROMBank = 1;
        private byte _romBankHigh = 0;
        private bool _mbc1AdvancedMode = false;
        private byte CurrentRAMBank = 0;
        private readonly byte[] RAMBanks = new byte[0x20000];
        private bool EnableRAM = false;
        private int _romBankCount = 2;
        private int _ramSize = 0;

        private bool _hasBattery = false;
        private string? _savePath = null;

        private byte _rtcS, _rtcM, _rtcH, _rtcDL, _rtcDH;
        private byte _rtcLatchedS, _rtcLatchedM, _rtcLatchedH, _rtcLatchedDL, _rtcLatchedDH;
        private byte _rtcLatchPrev = 0xFF;
        private bool _rtcMapped = false;
        private byte _rtcSelectedReg = 0;

        private static readonly int[] TimerClockSpeeds = { 1024, 16, 64, 256 };

        private readonly GameBoy gameboy = gb;

        // Gets or sets apu.
        public APU Apu { get; set; } = null!;

        public byte IF
        {
            // Gets the value.
            get { return (byte)(Memory[0xFF0F] | 0xE0); }
            // Sets the value.
            set { Memory[0xFF0F] = (byte)(value | 0xE0); }
        }

        // Gets or sets ie.
        public byte IE { get { return Memory[0xFFFF]; } set { Memory[0xFFFF] = value; } }

        // Executes write byte to memory.
        public void WriteByteToMemory(uint addr, byte value)
        {
            addr &= 0xFFFF;

            if (addr <= 0x1FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                    case MBCType.MBC3:
                    case MBCType.MBC5:
                        EnableRAM = (value & 0x0F) == 0x0A;
                        break;
                    case MBCType.MBC2:

                        if ((addr & 0x0100) == 0)
                            EnableRAM = (value & 0x0F) == 0x0A;
                        break;
                }
            }

            // Executes if.
            else if (addr >= 0x2000 && addr <= 0x3FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                        value &= 0x1F;
                        if (value == 0) value = 1;
                        CurrentROMBank = (byte)((CurrentROMBank & 0xE0) | value);
                        break;
                    case MBCType.MBC2:

                        if ((addr & 0x0100) != 0)
                            CurrentROMBank = (byte)(value & 0x0F);
                        if (CurrentROMBank == 0) CurrentROMBank = 1;
                        break;
                    case MBCType.MBC3:
                        value &= 0x7F;
                        if (value == 0) value = 1;
                        CurrentROMBank = value;
                        break;
                    case MBCType.MBC5:
                        if (addr <= 0x2FFF)
                        {
                            CurrentROMBank = value;
                        }
                        else
                        {
                            _romBankHigh = (byte)(value & 0x01);
                        }

                        break;
                }
            }

            // Executes if.
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
                        // Executes if.
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

            // Executes if.
            else if (addr >= 0x6000 && addr <= 0x7FFF)
            {
                switch (MapperType)
                {
                    case MBCType.MBC1:
                        _mbc1AdvancedMode = (value & 0x01) != 0;
                        if (_mbc1AdvancedMode)
                        {
                            CurrentRAMBank = _romBankHigh;
                        }
                        else
                        {
                            CurrentRAMBank = 0;
                        }
                        break;
                    case MBCType.MBC3:

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

            // Executes if.
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
                                    RAMBanks[ramAddr] = (byte)(value & 0x0F);
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

            // Executes if.
            else if ((addr >= 0xC000) && (addr <= 0xDFFF))
            {
                Memory[addr] = value;
            }

            // Executes if.
            else if (addr >= 0xE000 && addr < 0xFE00)
            {
                Memory[addr] = value;
                Memory[addr - 0x2000] = value;
            }

            // Executes if.
            else if (addr >= 0xFEA0 && addr < 0xFEFF && value > 0)
            { }

            // Executes if.
            else if (0xFF04 == addr)
            {
                gameboy.WriteDiv();
            }

            // Executes if.
            else if (addr == 0xFF05)
            {
                gameboy.WriteTima(value);
            }

            // Executes if.
            else if (addr == 0xFF06)
            {
                gameboy.WriteTma(value);
            }

            // Executes if.
            else if (addr == 0xFF07)
            {
                gameboy.WriteTac(value);
            }

            // Executes if.
            else if (addr == 0xFF0F)
            {
                IF = value;
            }

            // Executes if.
            else if (addr == 0xFF01)
            {
                Memory[addr] = value;
            }

            // Executes if.
            else if (addr == 0xFF02)
            {
                Memory[addr] = value;
            }

            // Executes if.
            else if (addr >= 0xFF10 && addr <= 0xFF3F)
            {
                Apu.WriteRegister(addr, value);
            }

            // Executes if.
            else if (addr == 0xFF40)
            {
                byte oldValue = Memory[0xFF40];
                Memory[0xFF40] = value;
                gameboy.OnLcdcWrite(oldValue, value);
            }

            // Executes if.
            else if (addr == 0xFF44)
            {
                Memory[addr] = 0;
            }

            // Executes if.
            else if (addr == 0xFF46)
                gameboy.StartDmaTransfer(value);

            // Executes if.
            else if ((addr >= 0xFF4C) && (addr <= 0xFF7F))
            {
            }

            // Executes if.
            else if (addr == 0xFFFF)
            {
                IE = value;
            }
            else
                Memory[addr] = value;
        }

        // Executes read byte from memory.
        public byte ReadByteFromMemory(uint addr)
        {
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

            // Executes if.
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
                    return (byte)(RAMBanks[addr - 0xA000] | 0xF0);
                }

                uint ramAddr = (uint)((addr - 0xA000) + CurrentRAMBank * 0x2000);
                if (ramAddr < RAMBanks.Length)
                    return RAMBanks[ramAddr];
                return 0xFF;
            }

            // Executes if.
            else if (addr == 0xFF00)
            {
                return gameboy!.GetKeypadState();
            }

            else if (addr == 0xFF04) return Memory[0xFF04];

            // Executes if.
            else if (addr == 0xFF44)
            {
                return gameboy.ReadLyForCpu();
            }

            // Executes if.
            else if (addr >= 0xFF10 && addr <= 0xFF3F)
            {
                return Apu.ReadRegister(addr);
            }

            // Executes if.
            else if (addr == 0xFF0F)
            {
                return IF;
            }

            // Executes if.
            else if (addr == 0xFFFF)
            {
                return IE;
            }

            return Memory[addr];
        }

        // Executes read word from memory.
        public uint ReadWordFromMemory(uint addr)
        {
            return (uint)ReadByteFromMemory(addr + 1) << 8 | ReadByteFromMemory(addr);
        }

        // Executes write word to memory.
        public void WriteWordToMemory(uint addr, uint value)
        {
            WriteByteToMemory(addr + 1, (byte)(value >> 8));
            WriteByteToMemory(addr, (byte)value);
        }

        // Executes init rom banks.
        public void InitROMBanks()
        {
            byte cartridgeType = Cartridge[0x0147];
            byte romSizeByte = Cartridge[0x0148];
            byte ramSizeByte = Cartridge[0x0149];

            MapperType = cartridgeType switch
            {
                0x00 => MBCType.None,
                0x01 or 0x02 or 0x03 => MBCType.MBC1,
                0x05 or 0x06 => MBCType.MBC2,
                0x0F or 0x10 or 0x11 or 0x12 or 0x13 => MBCType.MBC3,
                0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => MBCType.MBC5,
                _ => MBCType.None,
            };

            _romBankCount = romSizeByte <= 8 ? (2 << romSizeByte) : 2;

            _ramSize = ramSizeByte switch
            {
                0x00 => 0,
                0x01 => 2048,
                0x02 => 8192,
                0x03 => 32768,
                0x04 => 131072,
                0x05 => 65536,
                _ => 0,
            };

            if (MapperType == MBCType.MBC2)
                _ramSize = 512;

            _hasBattery = cartridgeType switch
            {
                0x03 => true,
                0x06 => true,
                0x09 => true,
                0x0D => true,
                0x0F => true,
                0x10 => true,
                0x13 => true,
                0x1B => true,
                0x1E => true,
                _ => false,
            };

            CurrentROMBank = 1;
            CurrentRAMBank = 0;
            EnableRAM = false;
            _romBankHigh = 0;
            _mbc1AdvancedMode = false;
            _rtcMapped = false;

            Console.WriteLine($"[MMU] Cartridge type: 0x{cartridgeType:X2} → {MapperType}, ROM banks: {_romBankCount}, RAM: {_ramSize} , Battery: {_hasBattery}");

            if (_hasBattery && _savePath != null)
                LoadSave();
        }

        // Executes set save path.
        public void SetSavePath(string romPath)
        {
            _savePath = Path.ChangeExtension(romPath, ".sav");
        }

        // Executes load save.
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

        // Executes save battery.
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
