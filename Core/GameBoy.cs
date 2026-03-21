// ============================================================================
// Project:     GameboyEmu
// File:        Core/GameBoy.cs
// Description: Main emulator orchestrator — ties CPU, MMU, PPU, and APU
//              together and runs the emulation loop
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
    public class GameBoy
    {
        public CPU cPU;
        public MMU mMU;
        public APU aPU;
        public PPU pPU;

        public int[,,] LCD { get { return pPU.LCD; } }

        private readonly byte[] tempROM = new byte[0xFF];

        public int DivCounter;
        public int TimerCounter = 1024;
        public int TimerVariable = 0;

        byte keypadState = 0xFF;

        private bool _useBootROM;
        public bool ResetRequested { get; set; }

        public bool IsRunning { get; set; }

        /// <summary>
        /// Called at the end of each frame (~59.7 Hz). Hook this up to your
        /// display to render the screen and poll input.
        /// </summary>
        public Action? OnFrameReady
        {
            get => pPU.OnFrameReady;
            set => pPU.OnFrameReady = value;
        }

        public GameBoy()
        {
            mMU = new(this);
            cPU = new CPU(mMU!, this);
            aPU = new APU();
            pPU = new PPU(mMU!);
            mMU.Apu = aPU;
        }

        public void LoadCartridge(string path, int size)
        {
            Array.Copy(File.ReadAllBytes(path), 0, mMU!.Cartridge, 0, size);
            Array.Copy(mMU!.Cartridge, 0, mMU!.Memory, 0, 0x8000);
            mMU!.CurrentROMBank = 1;

            if (File.Exists("dmg_boot.bin"))
            {
                // Run the boot ROM: back up the first 0xFF bytes of the
                // cartridge so they can be restored after the boot sequence.
                Array.Copy(mMU!.Cartridge, 0, tempROM, 0, 0xFF);
                Array.Copy(File.ReadAllBytes("dmg_boot.bin"), 0, mMU!.Memory, 0x00, 0xFF);
                cPU!.registers.PC = 0x00;
                _useBootROM = true;
            }
            else
            {
                // No boot ROM — jump straight to post-boot state.
                InitialiseGameboyForCartridge(0x100);
                mMU!.InitROMBanks();
                _useBootROM = false;
            }
        }

        public void PostBootROMCopy()
        {
            Array.Copy(tempROM, 0, mMU!.Memory, 0, tempROM.Length); // replace kernal at 0x00 with temp memory
            InitialiseGameboyForCartridge(0x100);
            mMU!.InitROMBanks();
        }

        private void InitialiseGameboyForCartridge(uint startPC)
        {
            cPU!.registers.PC = startPC;
            cPU!.registers.SP = 0xFFFE;
            cPU!.registers.AF = 0x01B0;
            cPU!.registers.BC = 0x0013;
            cPU!.registers.DE = 0x00D8;
            cPU!.registers.HL = 0x014D;
            mMU!.Memory[0xFF00] = 0xFF;
            mMU!.Memory[0xFF05] = 0x00;
            mMU!.Memory[0xFF06] = 0x00;
            mMU!.Memory[0xFF07] = 0x00;
            mMU!.Memory[0xFF10] = 0x80;
            mMU!.Memory[0xFF11] = 0xBF;
            mMU!.Memory[0xFF12] = 0xF3;
            mMU!.Memory[0xFF14] = 0xBF;
            mMU!.Memory[0xFF16] = 0x3F;
            mMU!.Memory[0xFF17] = 0x00;
            mMU!.Memory[0xFF19] = 0xBF;
            mMU!.Memory[0xFF1A] = 0x7F;
            mMU!.Memory[0xFF1B] = 0xFF;
            mMU!.Memory[0xFF1C] = 0x9F;
            mMU!.Memory[0xFF1E] = 0xBF;
            mMU!.Memory[0xFF20] = 0xFF;
            mMU!.Memory[0xFF21] = 0x00;
            mMU!.Memory[0xFF22] = 0x00;
            mMU!.Memory[0xFF23] = 0xBF;
            mMU!.Memory[0xFF24] = 0x77;
            mMU!.Memory[0xFF25] = 0xF3;
            mMU!.Memory[0xFF26] = 0xF1;
            mMU!.Memory[0xFF40] = 0x91;
            mMU!.Memory[0xFF42] = 0x00;
            mMU!.Memory[0xFF43] = 0x00;
            mMU!.Memory[0xFF45] = 0x00;
            mMU!.Memory[0xFF47] = 0xFC;
            mMU!.Memory[0xFF48] = 0xFF;
            mMU!.Memory[0xFF49] = 0xFF;
            mMU!.Memory[0xFF4A] = 0x00;
            mMU!.Memory[0xFF4B] = 0x00;
            mMU!.Memory[0xFFFF] = 0x00;
            TimerCounter = 1024;
            keypadState = 255;
            pPU.ScanLineCounter = 456;
        }


        public void Start()
        {
            IsRunning = true;
            cPU!.Running = true;
            int cycles = 0;
            pPU.StartFrameTimer();
            while (cPU.Running)
            {
                if (ResetRequested)
                {
                    cPU.Running = false;
                    break;
                }

                if (cPU.IsHalted)
                    cycles = 4;
                else
                    cycles = cPU.Execute(mMU!.ReadByteFromMemory(cPU!.registers.PC++));
                UpdateTimers(cycles);
                pPU.Update(cycles);
                aPU.Tick(cycles);
                HandleInterupts();
                if (_useBootROM && cPU.registers.PC == 0x100)
                    PostBootROMCopy();
            }
            IsRunning = false;
        }

        private void UpdateTimers(int cycles)
        {
            byte timerAtts = mMU!.Memory[0xFF07];
            DivCounter += cycles;
            if (TestBit(timerAtts, 2))
            {
                TimerVariable += cycles;
                if (TimerVariable >= TimerCounter)
                {
                    TimerVariable = 0;
                    if (mMU!.Memory[0xFF05] == 0xFF)
                    {
                        mMU!.Memory[0xFF05] = mMU!.Memory[0xFF06];
                        RequestInterrupt(2);
                    }
                    else mMU!.Memory[0xFF05]++;
                }
            }
            if (DivCounter >= 256)
            {
                DivCounter = 0;
                mMU!.Memory[0xFF04]++;
            }
        }

        private void RequestInterrupt(int id)
        {
            mMU!.IF = SetBit(mMU!.IF, id, 1);
        }

        private void HandleInterupts()
        {
            for (int i = 0; i < 5; i++)
                if ((((mMU!.IF & mMU!.IE) >> i) & 0x1) == 1)
                    cPU!.ExecuteInterrupt(i);
            cPU!.UpdateIME();
        }

        public bool TestBit(byte data, int bitPos)
        {
            return (data & (1 << bitPos)) != 0;
        }

        private static byte SetBit(int register, int bitIndex, int newBitValue)
        {
            if (newBitValue == 1)
                return (byte)(register |= (1 << bitIndex));
            else
                return (byte)(register &= ~(1 << bitIndex));
        }

        public void DMATransfer(byte value)
        {
            pPU.DMATransfer(value);
        }

        public void KeypadKeyPressed(int key)
        {
            bool previouslySet = !TestBit(keypadState, key);

            keypadState = SetBit(keypadState, key, 0);
            bool button;

            if (key > 3)
                button = true;
            else 
                button = false;

            byte keyReq = mMU!.Memory[0xFF00];
            bool requestInterupt = false;

            if (button && !TestBit(keyReq, 5))
                requestInterupt = true;

            else if (!button && !TestBit(keyReq, 4))
                requestInterupt = true;

            if (requestInterupt && !previouslySet)
                RequestInterrupt(4);
        }

        public void KeypadKeyReleased(int key)
        {
            keypadState = SetBit(keypadState, key, 1);
        }

        public byte GetKeypadState()
        {
            byte reg = mMU!.Memory[0xFF00];
            // Bits 6-7 always read as 1; start bits 0-3 high (unpressed)
            reg |= 0xCF;

            // Bit 4 low = direction keys selected
            if ((reg & 0x10) == 0)
            {
                // Mix in direction state (lower nibble of keypadState: Right,Left,Up,Down)
                reg &= (byte)(0xF0 | (keypadState & 0x0F));
            }

            // Bit 5 low = action buttons selected
            if ((reg & 0x20) == 0)
            {
                // Mix in button state (upper nibble of keypadState shifted to bits 0-3: A,B,Sel,Start)
                reg &= (byte)(0xF0 | ((keypadState >> 4) & 0x0F));
            }

            return reg;
        }
    }
}