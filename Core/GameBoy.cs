// ============================================================================
// Project:     GameboyEmu
// File:        Core/GameBoy.cs
// Description: System orchestrator - coordinates CPU, MMU, PPU, and APU,
//              and drives timers, DMA, interrupts, and frame pacing
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

#nullable enable

namespace GameboyEmu.Core
{
    public sealed class GameBoy
    {
        internal enum OamBugAccessType
        {
            Read,
            Write,
            ReadDuringIncDec,
        }

        private const int CpuClockRate = 4194304;
        private const int CyclesPerFrame = 456 * 154;
        private static readonly double StopwatchTicksPerCycle = (double)Stopwatch.Frequency / CpuClockRate;

        public CPU cPU;
        public MMU mMU;
        public APU aPU;
        public PPU pPU;

        private readonly byte[] tempROM = new byte[0xFF];

        public int DivCounter;
        public int TimerCounter = 1024;
        public int TimerVariable = 0;
        private bool _timaOverflowPending = false;
        private int _timaOverflowDelay = 0;
        private int _timaReloadBlockTicks = 0;
        private ushort _systemCounter;

        private bool _dmaActive;
        private ushort _dmaSourceBase;
        private int _dmaBytesCopied;
        private int _dmaTicksToNextByte;

        byte keypadState = 0xFF;

        private bool _useBootROM;
        public bool ResetRequested { get; set; }

        public bool IsRunning { get; set; }
        private readonly Stopwatch _runTimer = new();
        private long _emulatedCycles;
        private int _cyclesUntilPace = CyclesPerFrame;
        private Action? _onFrameReady;

        /// <summary>
        /// Called when a completed frame reaches the paced presentation boundary.
        /// Hook this up to your display to render the screen and poll input.
        /// </summary>
        public Action? OnFrameReady
        {
            get => _onFrameReady;
            set => _onFrameReady = value;
        }

        public GameBoy()
        {
            mMU = new(this);
            cPU = new CPU(mMU!, this);
            aPU = new APU();
            pPU = new PPU(mMU!);
            mMU.Apu = aPU;
        }

        public void LoadCartridge(string path, int size, bool skipBootROM = false)
        {
            // Bulk cartridge staging is done via direct arrays; MMU methods are for bus-visible accesses.
            Array.Copy(File.ReadAllBytes(path), 0, mMU!.Cartridge, 0, size);
            Array.Copy(mMU!.Cartridge, 0, mMU!.Memory, 0, 0x8000);
            mMU!.CurrentROMBank = 1;
            mMU!.SetSavePath(path);

            if (!skipBootROM && File.Exists("dmg_boot.bin"))
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
                if (skipBootROM)
                    Console.WriteLine("Boot ROM bypassed (--nobootrom).");
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
            mMU!.WriteByteToMemory(0xFF10, 0x80);
            mMU!.WriteByteToMemory(0xFF11, 0xBF);
            mMU!.WriteByteToMemory(0xFF12, 0xF3);
            mMU!.WriteByteToMemory(0xFF14, 0xBF);
            mMU!.WriteByteToMemory(0xFF16, 0x3F);
            mMU!.WriteByteToMemory(0xFF17, 0x00);
            mMU!.WriteByteToMemory(0xFF19, 0xBF);
            mMU!.WriteByteToMemory(0xFF1A, 0x7F);
            mMU!.WriteByteToMemory(0xFF1B, 0xFF);
            mMU!.WriteByteToMemory(0xFF1C, 0x9F);
            mMU!.WriteByteToMemory(0xFF1E, 0xBF);
            mMU!.WriteByteToMemory(0xFF20, 0xFF);
            mMU!.WriteByteToMemory(0xFF21, 0x00);
            mMU!.WriteByteToMemory(0xFF22, 0x00);
            mMU!.WriteByteToMemory(0xFF23, 0xBF);
            mMU!.WriteByteToMemory(0xFF24, 0x77);
            mMU!.WriteByteToMemory(0xFF25, 0xF3);
            mMU!.WriteByteToMemory(0xFF26, 0xF1);
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
            _systemCounter = 0;
            _timaOverflowPending = false;
            _timaOverflowDelay = 0;
            _timaReloadBlockTicks = 0;
            _dmaActive = false;
            _dmaBytesCopied = 0;
            _dmaTicksToNextByte = 0;
        }


        public void Start()
        {
            IsRunning = true;
            cPU!.Running = true;
            _runTimer.Restart();
            _emulatedCycles = 0;
            _cyclesUntilPace = CyclesPerFrame;
            pPU.ConsumeFrameReady();
            int cycles = 0;
            while (cPU.Running)
            {
                if (ResetRequested)
                {
                    cPU.Running = false;
                    break;
                }

                if (cPU.IsHalted)
                {
                    // HALT runs in 4T idle steps, but if an interrupt is already pending,
                    // wake immediately and let interrupt handling proceed this boundary.
                    cycles = (mMU!.IF & mMU.IE & 0x1F) != 0 ? 0 : 4;
                }
                else
                {
                    byte opcode = mMU!.ReadByteFromMemory(cPU!.registers.PC++);
                    cycles = cPU.Execute(opcode);
                }

                if (cPU.ConsumeInstructionHandledInternally())
                    cycles = 0;

                AdvanceHardware(cycles);
                HandleInterupts();
                if (_useBootROM && cPU.registers.PC == 0x100)
                    PostBootROMCopy();
            }
            IsRunning = false;
        }

        private void AdvanceHardware(int cycles)
        {
            if (cycles <= 0)
                return;

            UpdateTimers(cycles);
            UpdateDma(cycles);
            pPU.Update(cycles);
            aPU.Tick(cycles);

            if (PaceToRealTime(cycles))
                PresentCompletedFrame();
        }

        public void AdvanceHardwareFromCpu(int cycles)
        {
            AdvanceHardware(cycles);
        }

        public void OnLcdcWrite(byte oldValue, byte newValue)
        {
            pPU.OnLcdcWrite(oldValue, newValue);
        }

        public byte ReadLyForCpu()
        {
            return pPU.ReadLyForCpu();
        }

        private bool PaceToRealTime(int cycles)
        {
            _emulatedCycles += cycles;
            _cyclesUntilPace -= cycles;
            if (_cyclesUntilPace > 0)
                return false;

            do
            {
                _cyclesUntilPace += CyclesPerFrame;
            }
            while (_cyclesUntilPace <= 0);

            long targetElapsedTicks = (long)Math.Round(_emulatedCycles * StopwatchTicksPerCycle);
            while (true)
            {
                long remainingTicks = targetElapsedTicks - _runTimer.ElapsedTicks;
                if (remainingTicks <= 0)
                    break;

                if (remainingTicks > Stopwatch.Frequency / 500)
                {
                    Thread.Sleep(1);
                    continue;
                }

                Thread.SpinWait(64);
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PresentCompletedFrame()
        {
            // Always invoke the presentation callback at paced boundaries so
            // frontend event processing continues even when LCD output is off.
            pPU.ConsumeFrameReady();
            _onFrameReady?.Invoke();
        }

        private void UpdateTimers(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                if (_timaReloadBlockTicks > 0)
                    _timaReloadBlockTicks--;

                bool oldTimerSignal = GetTimerSignal();

                _systemCounter++;
                mMU!.Memory[0xFF04] = (byte)(_systemCounter >> 8);
                DivCounter = _systemCounter;

                bool newTimerSignal = GetTimerSignal();
                if (oldTimerSignal && !newTimerSignal)
                    IncrementTimaOnTimerEdge();

                // TIMA overflow reload and IF request occur 1 M-cycle (4 T-cycles) after overflow.
                if (_timaOverflowPending)
                {
                    _timaOverflowDelay--;
                    if (_timaOverflowDelay <= 0)
                    {
                        mMU.Memory[0xFF05] = mMU.Memory[0xFF06];
                        RequestInterrupt(2);
                        _timaOverflowPending = false;
                        _timaReloadBlockTicks = 4;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool GetTimerSignal()
        {
            byte tac = mMU!.Memory[0xFF07];
            if ((tac & 0x04) == 0)
                return false;

            int bit = (tac & 0x03) switch
            {
                0x00 => 9, // 4096 Hz
                0x01 => 3, // 262144 Hz
                0x02 => 5, // 65536 Hz
                _ => 7,    // 16384 Hz
            };

            return ((_systemCounter >> bit) & 1) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void IncrementTimaOnTimerEdge()
        {
            byte tima = mMU!.Memory[0xFF05];
            if (tima == 0xFF)
            {
                // On overflow TIMA is 0x00 for one M-cycle, then reloads from TMA and requests IF.
                mMU.Memory[0xFF05] = 0x00;
                _timaOverflowPending = true;
                _timaOverflowDelay = 4;
            }
            else
            {
                mMU!.Memory[0xFF05] = (byte)(tima + 1);
            }
        }

        private void UpdateDma(int cycles)
        {
            if (!_dmaActive)
                return;

            for (int i = 0; i < cycles && _dmaActive; i++)
            {
                _dmaTicksToNextByte--;
                if (_dmaTicksToNextByte > 0)
                    continue;

                byte value = mMU!.ReadByteFromMemory((uint)(_dmaSourceBase + _dmaBytesCopied));
                mMU.Memory[0xFE00 + _dmaBytesCopied] = value;

                _dmaBytesCopied++;
                _dmaTicksToNextByte = 4;

                if (_dmaBytesCopied >= 0xA0)
                    _dmaActive = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RequestInterrupt(int id)
        {
            mMU!.IF = SetBit(mMU!.IF, id, 1);
        }

        private void HandleInterupts()
        {
            cPU!.UpdateIME();

            for (int i = 0; i < 5; i++)
            {
                if ((((mMU!.IF & mMU!.IE) >> i) & 0x1) == 1)
                {
                    int intCycles = cPU!.ExecuteInterrupt(i);
                    if (intCycles > 0)
                    {
                        AdvanceHardware(intCycles);
                    }
                }
            }
        }

        internal void TriggerOamBug(OamBugAccessType accessType, int mCycleOffset = 0)
        {
            if (!IsOamScanAtOffset(mCycleOffset, out int row))
                return;

            switch (accessType)
            {
                case OamBugAccessType.Read:
                    ApplyReadCorruption(row);
                    break;
                case OamBugAccessType.Write:
                    ApplyWriteCorruption(row);
                    break;
                case OamBugAccessType.ReadDuringIncDec:
                    ApplyReadDuringIncDecCorruption(row);
                    break;
            }
        }

        private bool IsOamScanAtOffset(int mCycleOffset, out int row)
        {
            row = 0;

            byte lcdc = mMU!.Memory[0xFF40];
            if ((lcdc & 0x80) == 0)
                return false;

            int ly = mMU.Memory[0xFF44];
            int scanCounter = pPU.ScanLineCounter - (mCycleOffset * 4);

            while (scanCounter <= 0)
            {
                scanCounter += 456;
                ly++;
            }

            if (ly >= 144)
                return false;

            int mode2ProgressT = 456 - scanCounter;
            if (mode2ProgressT < 0 || mode2ProgressT >= 80)
                return false;

            row = mode2ProgressT >> 2;
            if (row < 0 || row > 19)
                return false;

            return true;
        }

        private ushort ReadOamWord(int row, int word)
        {
            int baseAddr = 0xFE00 + row * 8 + word * 2;
            return (ushort)(mMU!.Memory[baseAddr] | (mMU.Memory[baseAddr + 1] << 8));
        }

        private void WriteOamWord(int row, int word, ushort value)
        {
            int baseAddr = 0xFE00 + row * 8 + word * 2;
            mMU!.Memory[baseAddr] = (byte)(value & 0xFF);
            mMU.Memory[baseAddr + 1] = (byte)(value >> 8);
        }

        private void CopyOamRow(int srcRow, int dstRow)
        {
            for (int w = 0; w < 4; w++)
                WriteOamWord(dstRow, w, ReadOamWord(srcRow, w));
        }

        private void ApplyWriteCorruption(int row)
        {
            if (row <= 0)
                return;

            ushort a = ReadOamWord(row, 0);
            ushort b = ReadOamWord(row - 1, 0);
            ushort c = ReadOamWord(row - 1, 2);
            ushort first = (ushort)(((a ^ c) & (b ^ c)) ^ c);
            WriteOamWord(row, 0, first);

            for (int w = 1; w < 4; w++)
                WriteOamWord(row, w, ReadOamWord(row - 1, w));
        }

        private void ApplyReadCorruption(int row)
        {
            if (row <= 0)
                return;

            ushort a = ReadOamWord(row, 0);
            ushort b = ReadOamWord(row - 1, 0);
            ushort c = ReadOamWord(row - 1, 2);
            ushort first = (ushort)(b | (a & c));
            WriteOamWord(row, 0, first);

            for (int w = 1; w < 4; w++)
                WriteOamWord(row, w, ReadOamWord(row - 1, w));
        }

        private void ApplyReadDuringIncDecCorruption(int row)
        {
            if (row > 3 && row < 19)
            {
                ushort a = ReadOamWord(row - 2, 0);
                ushort b = ReadOamWord(row - 1, 0);
                ushort c = ReadOamWord(row, 0);
                ushort d = ReadOamWord(row - 1, 2);

                ushort prevFirst = (ushort)((b & (a | c | d)) | (a & c & d));
                WriteOamWord(row - 1, 0, prevFirst);

                CopyOamRow(row - 1, row);
                CopyOamRow(row - 1, row - 2);
            }

            ApplyReadCorruption(row);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TestBit(byte data, int bitPos)
        {
            return (data & (1 << bitPos)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte SetBit(int register, int bitIndex, int newBitValue)
        {
            if (newBitValue == 1)
                return (byte)(register | (1 << bitIndex));
            else
                return (byte)(register & ~(1 << bitIndex));
        }

        public void DMATransfer(byte value)
        {
            StartDmaTransfer(value);
        }

        public void StartDmaTransfer(byte value)
        {
            _dmaSourceBase = (ushort)(value << 8);
            _dmaBytesCopied = 0;
            _dmaTicksToNextByte = 4;
            _dmaActive = true;
        }

        public void WriteDiv()
        {
            bool oldTimerSignal = GetTimerSignal();
            _systemCounter = 0;
            DivCounter = 0;
            mMU!.Memory[0xFF04] = 0;

            if (oldTimerSignal && !GetTimerSignal())
                IncrementTimaOnTimerEdge();
        }

        public void WriteTac(byte value)
        {
            bool oldTimerSignal = GetTimerSignal();
            mMU!.Memory[0xFF07] = value;

            // DMG obscure timing: if timer input transitions 1->0 due to TAC write, TIMA ticks once.
            if (oldTimerSignal && !GetTimerSignal())
                IncrementTimaOnTimerEdge();
        }

        public void WriteTima(byte value)
        {
            // Writes during reload cycle are ignored.
            if (_timaReloadBlockTicks > 0)
                return;

            // Writing TIMA during the pending reload window cancels the pending reload/interrupt.
            if (_timaOverflowPending)
            {
                _timaOverflowPending = false;
                _timaOverflowDelay = 0;
            }

            mMU!.Memory[0xFF05] = value;
        }

        public void WriteTma(byte value)
        {
            mMU!.Memory[0xFF06] = value;

            // During reload cycle, TMA writes are reflected into TIMA.
            if (_timaReloadBlockTicks > 0)
                mMU.Memory[0xFF05] = value;
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

            // Read raw FF00 select bits (not MMU read path) to decide whether to request joypad interrupt.
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
            // Start from raw JOYP register and compose bits 0-3 based on current key matrix state.
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