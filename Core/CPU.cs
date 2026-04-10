// ============================================================================
// Project:     GameboyEmu
// File:        Core/CPU.cs
// Description: LR35902 CPU core - full opcode set, CB-prefixed operations,
//              IME/HALT behavior, interrupt servicing, and M-cycle-accurate
//              bus accesses for precise memory timing
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================
using System.Runtime.CompilerServices;
namespace GameboyEmu.Core
{
    // Executes cpu.
    public sealed class CPU(MMU _memory, GameBoy _gameboy)
    {
        private readonly MMU memory = _memory;
        private GameBoy gameboy = _gameboy;
        public Registers registers = new();
        // Gets or sets running.
        public bool Running { get; set; } = false;
        private bool IME;
        private int _imeEnableDelay;
        private bool Halted;
        public bool IsHalted => Halted;
        private bool HaltBug;
        // Executes reset.
        public void Reset()
        {
            registers = new Registers();
            Running = true;
            IME = false;
            _imeEnableDelay = 0;
            Halted = false;
            HaltBug = false;
        }
        // Executes tick4.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Tick4()
        {
            gameboy.AdvanceHardwareFromCpu(4);
        }
        // Executes read byte.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadByte(uint addr)
        {
            byte v = memory.ReadByteFromMemory(addr);
            Tick4();
            return v;
        }
        // Executes write byte.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteByte(uint addr, byte value)
        {
            memory.WriteByteToMemory(addr, value);
            Tick4();
        }
        // Executes write word.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteWord(uint addr, uint value)
        {
            WriteByte(addr + 1, (byte)(value >> 8));
            WriteByte(addr, (byte)value);
        }
        // Executes fetch byte.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte FetchByte()
        {
            return ReadByte(registers.PC++);
        }
        // Executes fetch word.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint FetchWord()
        {
            byte lo = FetchByte();
            byte hi = FetchByte();
            return (uint)(hi << 8 | lo);
        }
        // Executes execute.
        public int Execute(int opcode)
        {
            if (HaltBug)
            {
                registers.PC--;
                HaltBug = false;
            }
            registers.PC &= 0xFFFF;
            switch (opcode)
            {
                case 0x00: // NOP
                    return 0;
                case 0x01: // LD BC,d16
                    registers.BC = FetchWord();
                    return 0;
                case 0x02: // LD (BC),A
                    WriteByte(registers.BC, registers.A);
                    return 0;
                case 0x03: // INC BC
                    if (IsInOamRange(registers.BC))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.BC++;
                    Tick4(); // internal
                    return 0;
                case 0x04: // INC B
                    registers.Flags.SetHalfCarryAdd(registers.B, 1);
                    registers.B++;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = false;
                    return 0;
                case 0x05: // DEC B
                    registers.Flags.SetHalfCarrySub(registers.B, 1);
                    registers.B--;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = true;
                    return 0;
                case 0x06: // LD B,d8
                    registers.B = FetchByte();
                    return 0;
                case 0x07: // RLCA
                    registers.A = RLC(registers.A);
                    registers.Flags.Z = false;
                    return 0;
                case 0x08: // LD (a16),SP
                    {
                        uint addr = FetchWord();
                        WriteWord(addr, registers.SP);
                        return 0;
                    }
                case 0x09: // ADD HL,BC
                    ADDHL(registers.BC);
                    Tick4(); // internal
                    return 0;
                case 0x0A: // LD A,(BC)
                    registers.A = ReadByte(registers.BC);
                    return 0;
                case 0x0B: // DEC BC
                    if (IsInOamRange(registers.BC))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.BC--;
                    Tick4(); // internal
                    return 0;
                case 0x0C: // INC C
                    registers.Flags.SetHalfCarryAdd(registers.C, 1);
                    registers.C++;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.N = false;
                    return 0;
                case 0x0D: // DEC C
                    registers.Flags.SetHalfCarrySub(registers.C, 1);
                    registers.C--;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.N = true;
                    return 0;
                case 0x0E: // LD C,d8
                    registers.C = FetchByte();
                    return 0;
                case 0x0F: // RRCA
                    registers.A = RRC(registers.A);
                    registers.Flags.Z = false;
                    return 0;
                case 0x10: // STOP
                    Stop();
                    return 0;
                case 0x11: // LD DE,d16
                    registers.DE = FetchWord();
                    return 0;
                case 0x12: // LD (DE),A
                    WriteByte(registers.DE, registers.A);
                    return 0;
                case 0x13: // INC DE
                    if (IsInOamRange(registers.DE))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.DE++;
                    Tick4();
                    return 0;
                case 0x14: // INC D
                    registers.Flags.SetHalfCarryAdd(registers.D, 1);
                    registers.D++;
                    registers.Flags.UpdateZeroFlag(registers.D);
                    registers.Flags.N = false;
                    return 0;
                case 0x15: // DEC D
                    registers.Flags.SetHalfCarrySub(registers.D, 1);
                    registers.D--;
                    registers.Flags.UpdateZeroFlag(registers.D);
                    registers.Flags.N = true;
                    return 0;
                case 0x16: // LD D,d8
                    registers.D = FetchByte();
                    return 0;
                case 0x17: // RLA
                    registers.A = RL(registers.A);
                    registers.Flags.Z = false;
                    return 0;
                case 0x18: // JR r8
                    {
                        sbyte offset = (sbyte)FetchByte();
                        registers.PC = (uint)(registers.PC + offset);
                        Tick4(); // internal branch
                        return 0;
                    }
                case 0x19: // ADD HL,DE
                    ADDHL(registers.DE);
                    Tick4();
                    return 0;
                case 0x1A: // LD A,(DE)
                    registers.A = ReadByte(registers.DE);
                    return 0;
                case 0x1B: // DEC DE
                    if (IsInOamRange(registers.DE))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.DE--;
                    Tick4();
                    return 0;
                case 0x1C: // INC E
                    registers.Flags.SetHalfCarryAdd(registers.E, 1);
                    registers.E++;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.N = false;
                    return 0;
                case 0x1D: // DEC E
                    registers.Flags.SetHalfCarrySub(registers.E, 1);
                    registers.E--;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.N = true;
                    return 0;
                case 0x1E: // LD E,d8
                    registers.E = FetchByte();
                    return 0;
                case 0x1F: // RRA
                    registers.A = RR(registers.A);
                    registers.Flags.Z = false;
                    return 0;
                case 0x20: // JR NZ,r8
                    {
                        sbyte offset = (sbyte)FetchByte();
                        if (!registers.Flags.Z)
                        {
                            registers.PC = (uint)(registers.PC + offset);
                            Tick4();
                        }
                        return 0;
                    }
                case 0x21: // LD HL,d16
                    registers.HL = FetchWord();
                    return 0;
                case 0x22: // LD (HL+),A
                    WriteByte(registers.HL, registers.A);
                    registers.HL++;
                    return 0;
                case 0x23: // INC HL
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.HL++;
                    Tick4();
                    return 0;
                case 0x24: // INC H
                    registers.Flags.SetHalfCarryAdd(registers.H, 1);
                    registers.H++;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = false;
                    return 0;
                case 0x25: // DEC H
                    registers.Flags.SetHalfCarrySub(registers.H, 1);
                    registers.H--;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = true;
                    return 0;
                case 0x26: // LD H,d8
                    registers.H = FetchByte();
                    return 0;
                case 0x27: // DAA
                    DAA();
                    return 0;
                case 0x28: // JR Z,r8
                    {
                        sbyte offset = (sbyte)FetchByte();
                        if (registers.Flags.Z)
                        {
                            registers.PC = (uint)(registers.PC + offset);
                            Tick4();
                        }
                        return 0;
                    }
                case 0x29: // ADD HL,HL
                    ADDHL(registers.HL);
                    Tick4();
                    return 0;
                case 0x2A: // LD A,(HL+)
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    registers.A = ReadByte(registers.HL);
                    registers.HL++;
                    return 0;
                case 0x2B: // DEC HL
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.HL--;
                    Tick4();
                    return 0;
                case 0x2C: // INC L
                    registers.Flags.SetHalfCarryAdd(registers.L, 1);
                    registers.L++;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.N = false;
                    return 0;
                case 0x2D: // DEC L
                    registers.Flags.SetHalfCarrySub(registers.L, 1);
                    registers.L--;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.N = true;
                    return 0;
                case 0x2E: // LD L,d8
                    registers.L = FetchByte();
                    return 0;
                case 0x2F: // CPL
                    registers.A = (byte)~registers.A;
                    registers.Flags.N = true;
                    registers.Flags.H = true;
                    return 0;
                case 0x30: // JR NC,r8
                    {
                        sbyte offset = (sbyte)FetchByte();
                        if (!registers.Flags.C)
                        {
                            registers.PC = (uint)(registers.PC + offset);
                            Tick4();
                        }
                        return 0;
                    }
                case 0x31: // LD SP,d16
                    registers.SP = FetchWord();
                    return 0;
                case 0x32: // LD (HL-),A
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    WriteByte(registers.HL, registers.A);
                    registers.HL--;
                    return 0;
                case 0x33: // INC SP
                    if (IsInOamRange(registers.SP))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.SP++;
                    Tick4();
                    return 0;
                case 0x34: // INC (HL)
                    {
                        byte val = ReadByte(registers.HL);
                        registers.Flags.SetHalfCarryAdd(val, 1);
                        val++;
                        registers.Flags.UpdateZeroFlag(val);
                        registers.Flags.N = false;
                        WriteByte(registers.HL, val);
                        return 0;
                    }
                case 0x35: // DEC (HL)
                    {
                        byte val = ReadByte(registers.HL);
                        registers.Flags.SetHalfCarrySub(val, 1);
                        val--;
                        registers.Flags.UpdateZeroFlag(val);
                        registers.Flags.N = true;
                        WriteByte(registers.HL, val);
                        return 0;
                    }
                case 0x36: // LD (HL),d8
                    {
                        byte val = FetchByte();
                        WriteByte(registers.HL, val);
                        return 0;
                    }
                case 0x37: // SCF
                    registers.Flags.C = true;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 0;
                case 0x38: // JR C,r8
                    {
                        sbyte offset = (sbyte)FetchByte();
                        if (registers.Flags.C)
                        {
                            registers.PC = (uint)(registers.PC + offset);
                            Tick4();
                        }
                        return 0;
                    }
                case 0x39: // ADD HL,SP
                    ADDHL(registers.SP);
                    Tick4();
                    return 0;
                case 0x3A: // LD A,(HL-)
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    registers.A = ReadByte(registers.HL);
                    registers.HL--;
                    return 0;
                case 0x3B: // DEC SP
                    if (IsInOamRange(registers.SP))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.SP--;
                    Tick4();
                    return 0;
                case 0x3C: // INC A
                    registers.Flags.SetHalfCarryAdd(registers.A, 1);
                    registers.A++;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.N = false;
                    return 0;
                case 0x3D: // DEC A
                    registers.Flags.SetHalfCarrySub(registers.A, 1);
                    registers.A--;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.N = true;
                    return 0;
                case 0x3E: // LD A,d8
                    registers.A = FetchByte();
                    return 0;
                case 0x3F: // CCF
                    registers.Flags.C = !registers.Flags.C;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 0;
                case 0x40: return 0; // LD B,B
                case 0x41: registers.B = registers.C; return 0; // LD B,C
                case 0x42: registers.B = registers.D; return 0; // LD B,D
                case 0x43: registers.B = registers.E; return 0; // LD B,E
                case 0x44: registers.B = registers.H; return 0; // LD B,H
                case 0x45: registers.B = registers.L; return 0; // LD B,L
                case 0x46: registers.B = ReadByte(registers.HL); return 0; // LD B,(HL)
                case 0x47: registers.B = registers.A; return 0; // LD B,A
                case 0x48: registers.C = registers.B; return 0; // LD C,B
                case 0x49: return 0; // LD C,C
                case 0x4A: registers.C = registers.D; return 0; // LD C,D
                case 0x4B: registers.C = registers.E; return 0; // LD C,E
                case 0x4C: registers.C = registers.H; return 0; // LD C,H
                case 0x4D: registers.C = registers.L; return 0; // LD C,L
                case 0x4E: registers.C = ReadByte(registers.HL); return 0; // LD C,(HL)
                case 0x4F: registers.C = registers.A; return 0; // LD C,A
                case 0x50: registers.D = registers.B; return 0; // LD D,B
                case 0x51: registers.D = registers.C; return 0; // LD D,C
                case 0x52: return 0; // LD D,D
                case 0x53: registers.D = registers.E; return 0; // LD D,E
                case 0x54: registers.D = registers.H; return 0; // LD D,H
                case 0x55: registers.D = registers.L; return 0; // LD D,L
                case 0x56: registers.D = ReadByte(registers.HL); return 0; // LD D,(HL)
                case 0x57: registers.D = registers.A; return 0; // LD D,A
                case 0x58: registers.E = registers.B; return 0; // LD E,B
                case 0x59: registers.E = registers.C; return 0; // LD E,C
                case 0x5A: registers.E = registers.D; return 0; // LD E,D
                case 0x5B: return 0; // LD E,E
                case 0x5C: registers.E = registers.H; return 0; // LD E,H
                case 0x5D: registers.E = registers.L; return 0; // LD E,L
                case 0x5E: registers.E = ReadByte(registers.HL); return 0; // LD E,(HL)
                case 0x5F: registers.E = registers.A; return 0; // LD E,A
                case 0x60: registers.H = registers.B; return 0; // LD H,B
                case 0x61: registers.H = registers.C; return 0; // LD H,C
                case 0x62: registers.H = registers.D; return 0; // LD H,D
                case 0x63: registers.H = registers.E; return 0; // LD H,E
                case 0x64: return 0; // LD H,H
                case 0x65: registers.H = registers.L; return 0; // LD H,L
                case 0x66: registers.H = ReadByte(registers.HL); return 0; // LD H,(HL)
                case 0x67: registers.H = registers.A; return 0; // LD H,A
                case 0x68: registers.L = registers.B; return 0; // LD L,B
                case 0x69: registers.L = registers.C; return 0; // LD L,C
                case 0x6A: registers.L = registers.D; return 0; // LD L,D
                case 0x6B: registers.L = registers.E; return 0; // LD L,E
                case 0x6C: registers.L = registers.H; return 0; // LD L,H
                case 0x6D: return 0; // LD L,L
                case 0x6E: registers.L = ReadByte(registers.HL); return 0; // LD L,(HL)
                case 0x6F: registers.L = registers.A; return 0; // LD L,A
                case 0x70: WriteByte(registers.HL, registers.B); return 0; // LD (HL),B
                case 0x71: WriteByte(registers.HL, registers.C); return 0; // LD (HL),C
                case 0x72: WriteByte(registers.HL, registers.D); return 0; // LD (HL),D
                case 0x73: WriteByte(registers.HL, registers.E); return 0; // LD (HL),E
                case 0x74: WriteByte(registers.HL, registers.H); return 0; // LD (HL),H
                case 0x75: WriteByte(registers.HL, registers.L); return 0; // LD (HL),L
                case 0x76: Halt(); return 0; // HALT
                case 0x77: WriteByte(registers.HL, registers.A); return 0; // LD (HL),A
                case 0x78: registers.A = registers.B; return 0; // LD A,B
                case 0x79: registers.A = registers.C; return 0; // LD A,C
                case 0x7A: registers.A = registers.D; return 0; // LD A,D
                case 0x7B: registers.A = registers.E; return 0; // LD A,E
                case 0x7C: registers.A = registers.H; return 0; // LD A,H
                case 0x7D: registers.A = registers.L; return 0; // LD A,L
                case 0x7E: registers.A = ReadByte(registers.HL); return 0; // LD A,(HL)
                case 0x7F: return 0; // LD A,A
                case 0x80: ADD(registers.B); return 0; // ADD A,B
                case 0x81: ADD(registers.C); return 0; // ADD A,C
                case 0x82: ADD(registers.D); return 0; // ADD A,D
                case 0x83: ADD(registers.E); return 0; // ADD A,E
                case 0x84: ADD(registers.H); return 0; // ADD A,H
                case 0x85: ADD(registers.L); return 0; // ADD A,L
                case 0x86: ADD(ReadByte(registers.HL)); return 0; // ADD A,(HL)
                case 0x87: ADD(registers.A); return 0; // ADD A,A
                case 0x88: ADC(registers.B); return 0; // ADC A,B
                case 0x89: ADC(registers.C); return 0; // ADC A,C
                case 0x8A: ADC(registers.D); return 0; // ADC A,D
                case 0x8B: ADC(registers.E); return 0; // ADC A,E
                case 0x8C: ADC(registers.H); return 0; // ADC A,H
                case 0x8D: ADC(registers.L); return 0; // ADC A,L
                case 0x8E: ADC(ReadByte(registers.HL)); return 0; // ADC A,(HL)
                case 0x8F: ADC(registers.A); return 0; // ADC A,A
                case 0x90: SUB(registers.B); return 0; // SUB B
                case 0x91: SUB(registers.C); return 0; // SUB C
                case 0x92: SUB(registers.D); return 0; // SUB D
                case 0x93: SUB(registers.E); return 0; // SUB E
                case 0x94: SUB(registers.H); return 0; // SUB H
                case 0x95: SUB(registers.L); return 0; // SUB L
                case 0x96: SUB(ReadByte(registers.HL)); return 0; // SUB (HL)
                case 0x97: SUB(registers.A); return 0; // SUB A
                case 0x98: SBC(registers.B); return 0; // SBC A,B
                case 0x99: SBC(registers.C); return 0; // SBC A,C
                case 0x9A: SBC(registers.D); return 0; // SBC A,D
                case 0x9B: SBC(registers.E); return 0; // SBC A,E
                case 0x9C: SBC(registers.H); return 0; // SBC A,H
                case 0x9D: SBC(registers.L); return 0; // SBC A,L
                case 0x9E: SBC(ReadByte(registers.HL)); return 0; // SBC A,(HL)
                case 0x9F: SBC(registers.A); return 0; // SBC A,A
                case 0xA0: AND(registers.B); return 0; // AND B
                case 0xA1: AND(registers.C); return 0; // AND C
                case 0xA2: AND(registers.D); return 0; // AND D
                case 0xA3: AND(registers.E); return 0; // AND E
                case 0xA4: AND(registers.H); return 0; // AND H
                case 0xA5: AND(registers.L); return 0; // AND L
                case 0xA6: AND(ReadByte(registers.HL)); return 0; // AND (HL)
                case 0xA7: AND(registers.A); return 0; // AND A
                case 0xA8: XOR(registers.B); return 0; // XOR B
                case 0xA9: XOR(registers.C); return 0; // XOR C
                case 0xAA: XOR(registers.D); return 0; // XOR D
                case 0xAB: XOR(registers.E); return 0; // XOR E
                case 0xAC: XOR(registers.H); return 0; // XOR H
                case 0xAD: XOR(registers.L); return 0; // XOR L
                case 0xAE: XOR(ReadByte(registers.HL)); return 0; // XOR (HL)
                case 0xAF: XOR(registers.A); return 0; // XOR A
                case 0xB0: OR(registers.B); return 0; // OR B
                case 0xB1: OR(registers.C); return 0; // OR C
                case 0xB2: OR(registers.D); return 0; // OR D
                case 0xB3: OR(registers.E); return 0; // OR E
                case 0xB4: OR(registers.H); return 0; // OR H
                case 0xB5: OR(registers.L); return 0; // OR L
                case 0xB6: OR(ReadByte(registers.HL)); return 0; // OR (HL)
                case 0xB7: OR(registers.A); return 0; // OR A
                case 0xB8: CP(registers.B); return 0; // CP B
                case 0xB9: CP(registers.C); return 0; // CP C
                case 0xBA: CP(registers.D); return 0; // CP D
                case 0xBB: CP(registers.E); return 0; // CP E
                case 0xBC: CP(registers.H); return 0; // CP H
                case 0xBD: CP(registers.L); return 0; // CP L
                case 0xBE: CP(ReadByte(registers.HL)); return 0; // CP (HL)
                case 0xBF: CP(registers.A); return 0; // CP A
                case 0xC0: // RET NZ
                    Tick4(); // internal condition check
                    if (!registers.Flags.Z)
                    {
                        registers.PC = TimedPopWord();
                        Tick4(); // internal
                    }
                    return 0;
                case 0xC1: // POP BC
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.BC = TimedPopWord();
                    return 0;
                case 0xC2: // JP NZ,a16
                    {
                        uint addr = FetchWord();
                        if (!registers.Flags.Z)
                        {
                            registers.PC = addr;
                            Tick4();
                        }
                        return 0;
                    }
                case 0xC3: // JP a16
                    {
                        uint addr = FetchWord();
                        registers.PC = addr;
                        Tick4();
                        return 0;
                    }
                case 0xC4: // CALL NZ,a16
                    {
                        uint addr = FetchWord();
                        if (!registers.Flags.Z)
                        {
                            Tick4(); // internal
                            TimedPushWord(registers.PC);
                            registers.PC = addr;
                        }
                        return 0;
                    }
                case 0xC5: // PUSH BC
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    Tick4(); // internal
                    TimedPushWord(registers.BC);
                    return 0;
                case 0xC6: // ADD A,d8
                    ADD(FetchByte());
                    return 0;
                case 0xC7: // RST 00H
                    Tick4(); // internal
                    TimedPushWord(registers.PC);
                    registers.PC = 0x00;
                    return 0;
                case 0xC8: // RET Z
                    Tick4(); // internal condition check
                    if (registers.Flags.Z)
                    {
                        registers.PC = TimedPopWord();
                        Tick4(); // internal
                    }
                    return 0;
                case 0xC9: // RET
                    registers.PC = TimedPopWord();
                    Tick4(); // internal
                    return 0;
                case 0xCA: // JP Z,a16
                    {
                        uint addr = FetchWord();
                        if (registers.Flags.Z)
                        {
                            registers.PC = addr;
                            Tick4();
                        }
                        return 0;
                    }
                case 0xCB: // CB prefix
                    {
                        byte cbOpcode = FetchByte();
                        ExecuteCB(cbOpcode);
                        return 0;
                    }
                case 0xCC: // CALL Z,a16
                    {
                        uint addr = FetchWord();
                        if (registers.Flags.Z)
                        {
                            Tick4(); // internal
                            TimedPushWord(registers.PC);
                            registers.PC = addr;
                        }
                        return 0;
                    }
                case 0xCD: // CALL a16
                    {
                        uint addr = FetchWord();
                        Tick4(); // internal
                        TimedPushWord(registers.PC);
                        registers.PC = addr;
                        return 0;
                    }
                case 0xCE: // ADC A,d8
                    ADC(FetchByte());
                    return 0;
                case 0xCF: // RST 08H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x08;
                    return 0;
                case 0xD0: // RET NC
                    Tick4();
                    if (!registers.Flags.C)
                    {
                        registers.PC = TimedPopWord();
                        Tick4();
                    }
                    return 0;
                case 0xD1: // POP DE
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.DE = TimedPopWord();
                    return 0;
                case 0xD2: // JP NC,a16
                    {
                        uint addr = FetchWord();
                        if (!registers.Flags.C)
                        {
                            registers.PC = addr;
                            Tick4();
                        }
                        return 0;
                    }
                case 0xD4: // CALL NC,a16
                    {
                        uint addr = FetchWord();
                        if (!registers.Flags.C)
                        {
                            Tick4();
                            TimedPushWord(registers.PC);
                            registers.PC = addr;
                        }
                        return 0;
                    }
                case 0xD5: // PUSH DE
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    Tick4();
                    TimedPushWord(registers.DE);
                    return 0;
                case 0xD6: // SUB d8
                    SUB(FetchByte());
                    return 0;
                case 0xD7: // RST 10H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x10;
                    return 0;
                case 0xD8: // RET C
                    Tick4();
                    if (registers.Flags.C)
                    {
                        registers.PC = TimedPopWord();
                        Tick4();
                    }
                    return 0;
                case 0xD9: // RETI
                    registers.PC = TimedPopWord();
                    Tick4();
                    IME = true;
                    return 0;
                case 0xDA: // JP C,a16
                    {
                        uint addr = FetchWord();
                        if (registers.Flags.C)
                        {
                            registers.PC = addr;
                            Tick4();
                        }
                        return 0;
                    }
                case 0xDC: // CALL C,a16
                    {
                        uint addr = FetchWord();
                        if (registers.Flags.C)
                        {
                            Tick4();
                            TimedPushWord(registers.PC);
                            registers.PC = addr;
                        }
                        return 0;
                    }
                case 0xDE: // SBC A,d8
                    SBC(FetchByte());
                    return 0;
                case 0xDF: // RST 18H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x18;
                    return 0;
                case 0xE0: // LDH (a8),A
                    {
                        byte offset = FetchByte();
                        WriteByte((uint)(0xFF00 + offset), registers.A);
                        return 0;
                    }
                case 0xE1: // POP HL
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.HL = TimedPopWord();
                    return 0;
                case 0xE2: // LD (C),A
                    WriteByte((uint)(0xFF00 + registers.C), registers.A);
                    return 0;
                case 0xE5: // PUSH HL
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    Tick4();
                    TimedPushWord(registers.HL);
                    return 0;
                case 0xE6: // AND d8
                    AND(FetchByte());
                    return 0;
                case 0xE7: // RST 20H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x20;
                    return 0;
                case 0xE8: // ADD SP,r8
                    {
                        byte sb = FetchByte();
                        registers.Flags.SetHalfCarryAdd((byte)registers.SP, sb);
                        registers.Flags.UpdateCarryFlag((byte)registers.SP + sb);
                        registers.Flags.N = false;
                        registers.Flags.Z = false;
                        registers.SP = (uint)((sbyte)sb + registers.SP);
                        Tick4(); // internal
                        Tick4(); // internal
                        return 0;
                    }
                case 0xE9: // JP (HL)
                    registers.PC = registers.HL;
                    return 0;
                case 0xEA: // LD (a16),A
                    {
                        uint addr = FetchWord();
                        WriteByte(addr, registers.A);
                        return 0;
                    }
                case 0xEE: // XOR d8
                    XOR(FetchByte());
                    return 0;
                case 0xEF: // RST 28H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x28;
                    return 0;
                case 0xF0: // LDH A,(a8)
                    {
                        byte offset = FetchByte();
                        registers.A = ReadByte((uint)(0xFF00 + offset));
                        return 0;
                    }
                case 0xF1: // POP AF
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.AF = TimedPopWord();
                    return 0;
                case 0xF2: // LD A,(C)
                    registers.A = ReadByte((uint)(0xFF00 + registers.C));
                    return 0;
                case 0xF3: // DI
                    IME = false;
                    _imeEnableDelay = 0;
                    return 0;
                case 0xF5: // PUSH AF
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    Tick4();
                    TimedPushWord(registers.AF);
                    return 0;
                case 0xF6: // OR d8
                    OR(FetchByte());
                    return 0;
                case 0xF7: // RST 30H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x30;
                    return 0;
                case 0xF8: // LD HL,SP+r8
                    {
                        byte sb = FetchByte();
                        registers.Flags.SetHalfCarryAdd((byte)registers.SP, sb);
                        registers.Flags.UpdateCarryFlag((byte)registers.SP + sb);
                        registers.Flags.N = false;
                        registers.Flags.Z = false;
                        registers.HL = (uint)((sbyte)sb + registers.SP);
                        Tick4(); // internal
                        return 0;
                    }
                case 0xF9: // LD SP,HL
                    registers.SP = registers.HL;
                    Tick4(); // internal
                    return 0;
                case 0xFA: // LD A,(a16)
                    {
                        uint addr = FetchWord();
                        registers.A = ReadByte(addr);
                        return 0;
                    }
                case 0xFB: // EI
                    _imeEnableDelay = 1;
                    return 0;
                case 0xFE: // CP d8
                    CP(FetchByte());
                    return 0;
                case 0xFF: // RST 38H
                    Tick4();
                    TimedPushWord(registers.PC);
                    registers.PC = 0x38;
                    return 0;
            }
            return 0;
        }
        // Executes execute cb.
        private void ExecuteCB(int cbOpcode)
        {
            int reg = cbOpcode & 0x07;
            int op = cbOpcode >> 3;
            if (reg == 0x06)
            {
                byte value = ReadByte(registers.HL);
                if ((cbOpcode & 0xC0) == 0x40)
                {
                    CompBit(value, (cbOpcode >> 3) & 0x07);
                    return;
                }
                byte result;
                if ((cbOpcode & 0xC0) == 0x80)
                    result = ResBit(value, (cbOpcode >> 3) & 0x07);
                // Executes if.
                else if ((cbOpcode & 0xC0) == 0xC0)
                    result = SetBitVal(value, (cbOpcode >> 3) & 0x07);
                else
                {
                    result = op switch
                    {
                        0 => RLC(value),
                        1 => RRC(value),
                        2 => RL(value),
                        3 => RR(value),
                        4 => SHL(value),
                        5 => SHR(value),
                        6 => SwapNibble(value),
                        7 => SRL(value),
                        _ => value,
                    };
                }
                WriteByte(registers.HL, result);
                return;
            }
            byte rv = GetCBReg(reg);
            byte nv;
            switch (cbOpcode & 0xC0)
            {
                case 0x40: // BIT
                    CompBit(rv, (cbOpcode >> 3) & 0x07);
                    return;
                case 0x80: // RES
                    nv = ResBit(rv, (cbOpcode >> 3) & 0x07);
                    break;
                case 0xC0: // SET
                    nv = SetBitVal(rv, (cbOpcode >> 3) & 0x07);
                    break;
                default: // rotate/shift/swap
                    nv = op switch
                    {
                        0 => RLC(rv),
                        1 => RRC(rv),
                        2 => RL(rv),
                        3 => RR(rv),
                        4 => SHL(rv),
                        5 => SHR(rv),
                        6 => SwapNibble(rv),
                        7 => SRL(rv),
                        _ => rv,
                    };
                    break;
            }
            SetCBReg(reg, nv);
        }
        // Executes get cb reg.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetCBReg(int r) => r switch
        {
            0 => registers.B,
            1 => registers.C,
            2 => registers.D,
            3 => registers.E,
            4 => registers.H,
            5 => registers.L,
            7 => registers.A,
            _ => 0,
        };
        // Executes set cb reg.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetCBReg(int r, byte v)
        {
            switch (r)
            {
                case 0: registers.B = v; break;
                case 1: registers.C = v; break;
                case 2: registers.D = v; break;
                case 3: registers.E = v; break;
                case 4: registers.H = v; break;
                case 5: registers.L = v; break;
                case 7: registers.A = v; break;
            }
        }
        // Executes timed push word.
        private void TimedPushWord(uint value)
        {
            registers.SP--;
            WriteByte(registers.SP, (byte)(value >> 8));
            registers.SP--;
            WriteByte(registers.SP, (byte)value);
        }
        // Executes timed pop word.
        private uint TimedPopWord()
        {
            byte lo = ReadByte(registers.SP);
            registers.SP++;
            byte hi = ReadByte(registers.SP);
            registers.SP++;
            return (uint)(hi << 8 | lo);
        }
        // Executes push word to stack.
        public void PushWordToStack(uint value)
        {
            registers.SP -= 2;
            memory.WriteWordToMemory(registers.SP, value);
        }
        // Executes pop word from stack.
        public uint PopWordFromStack()
        {
            uint value = memory.ReadWordFromMemory(registers.SP);
            registers.SP += 2;
            return value;
        }
        // Executes add.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ADD(byte val)
        {
            int result = registers.A + val;
            registers.Flags.SetHalfCarryAdd(registers.A, val);
            registers.Flags.UpdateZeroFlag(result);
            registers.Flags.UpdateCarryFlag(result);
            registers.Flags.N = false;
            registers.A = (byte)result;
        }
        // Executes adc.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ADC(byte val)
        {
            int carry = registers.Flags.C ? 1 : 0;
            int result = registers.A + val + carry;
            registers.Flags.H = ((registers.A & 0xF) + (val & 0xF) + carry) > 0xF;
            registers.Flags.UpdateZeroFlag(result);
            registers.Flags.UpdateCarryFlag(result);
            registers.Flags.N = false;
            registers.A = (byte)result;
        }
        // Executes addhl.
        private void ADDHL(uint source16bitRegister)
        {
            uint value = registers.HL + source16bitRegister;
            registers.Flags.H = ((registers.HL & 0xFFF) + (source16bitRegister & 0xFFF)) > 0xFFF;
            registers.Flags.C = value > 0xFFFF;
            registers.Flags.N = false;
            registers.HL = value;
        }
        // Executes sub.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SUB(byte val)
        {
            int result = registers.A - val;
            registers.Flags.SetHalfCarrySub(registers.A, val);
            registers.Flags.UpdateZeroFlag(result);
            registers.Flags.UpdateCarryFlag(result);
            registers.Flags.N = true;
            registers.A = (byte)result;
        }
        // Executes sbc.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SBC(byte val)
        {
            int carry = registers.Flags.C ? 1 : 0;
            int result = registers.A - val - carry;
            registers.Flags.H = (registers.A & 0xF) < (val & 0xF) + carry;
            registers.Flags.UpdateZeroFlag(result);
            registers.Flags.UpdateCarryFlag(result);
            registers.Flags.N = true;
            registers.A = (byte)result;
        }
        // Executes and.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AND(byte val)
        {
            registers.A &= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = true;
            registers.Flags.C = false;
        }
        // Executes xor.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void XOR(byte val)
        {
            registers.A ^= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = false;
            registers.Flags.C = false;
        }
        // Executes or.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OR(byte val)
        {
            registers.A |= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = false;
            registers.Flags.C = false;
        }
        // Executes cp.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CP(byte val)
        {
            int result = registers.A - val;
            registers.Flags.Z = (byte)result == 0;
            registers.Flags.N = true;
            registers.Flags.H = (registers.A & 0xF) < (val & 0xF);
            registers.Flags.C = (result >> 8) != 0;
        }
        // Executes rlc.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte RLC(byte value)
        {
            int bit7 = value >> 7;
            byte result = (byte)((value << 1) | bit7);
            registers.Flags.C = bit7 != 0;
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes rl.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte RL(byte value)
        {
            int bit7 = value >> 7;
            byte result = (byte)((value << 1) | (registers.Flags.C ? 1 : 0));
            registers.Flags.C = bit7 != 0;
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes rrc.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte RRC(byte value)
        {
            int bit0 = value & 1;
            byte result = (byte)((value >> 1) | (bit0 << 7));
            registers.Flags.C = bit0 != 0;
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes rr.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte RR(byte value)
        {
            int bit0 = value & 1;
            byte result = (byte)((value >> 1) | ((registers.Flags.C ? 1 : 0) << 7));
            registers.Flags.C = bit0 != 0;
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes shl.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte SHL(byte value)
        {
            registers.Flags.C = (value & 0x80) != 0;
            byte result = (byte)(value << 1);
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes shr.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte SHR(byte value)
        {
            registers.Flags.C = (value & 0x01) != 0;
            byte result = (byte)((value >> 1) | (value & 0x80));
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes srl.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte SRL(byte value)
        {
            registers.Flags.C = (value & 0x01) != 0;
            byte result = (byte)(value >> 1);
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        // Executes swap nibble.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte SwapNibble(byte value)
        {
            byte result = (byte)((value >> 4) | (value << 4));
            registers.Flags.Z = result == 0;
            registers.Flags.H = false;
            registers.Flags.N = false;
            registers.Flags.C = false;
            return result;
        }
        // Executes daa.
        private void DAA()
        {
            if (registers.Flags.N)
            {
                if (registers.Flags.C)
                    registers.A -= 0x60;
                if (registers.Flags.H)
                    registers.A -= 0x6;
            }
            else
            {
                if (registers.Flags.C || (registers.A > 0x99))
                {
                    registers.A += 0x60;
                    registers.Flags.C = true;
                }
                if (registers.Flags.H || ((registers.A & 0xF) > 0x9))
                    registers.A += 0x6;
            }
            registers.Flags.Z = (registers.A == 0);
            registers.Flags.H = false;
        }
        // Executes comp bit.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CompBit(int register, int bitIndex)
        {
            registers.Flags.Z = ((register >> bitIndex) & 1) == 0;
            registers.Flags.H = true;
            registers.Flags.N = false;
        }
        // Executes set bit val.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte SetBitVal(int register, int bitIndex)
            => (byte)(register | (1 << bitIndex));
        // Executes res bit.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ResBit(int register, int bitIndex)
            => (byte)(register & ~(1 << bitIndex));
        // Executes set bit.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte SetBit(int register, int bitIndex, int newBitValue)
            => newBitValue != 0
                ? (byte)(register | (1 << bitIndex))
                : (byte)(register & ~(1 << bitIndex));
        // Executes update ime.
        public void UpdateIME()
        {
            if (_imeEnableDelay > 0)
            {
                _imeEnableDelay--;
                if (_imeEnableDelay == 0)
                    IME = true;
            }
        }
        // Executes is in oam range.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInOamRange(uint value)
            => value >= 0xFE00 && value <= 0xFEFF;
        // Executes execute interrupt.
        public int ExecuteInterrupt(int b)
        {
            if (Halted)
                Halted = false;
            if (IME)
            {
                Tick4();
                Tick4();
                TimedPushWord(registers.PC);
                registers.PC = (ushort)(0x40 + (8 * b));
                Tick4();
                IME = false;
                memory.IF = SetBit(memory.IF, b, 0);
                return 0;
            }
            return 0;
        }
        // Executes halt.
        private void Halt()
        {
            if (IME)
            {
                Halted = true;
            }
            else
            {
                if ((memory.IE & memory.IF & 0x1F) == 0)
                {
                    Halted = true;
                }
                else
                {
                    HaltBug = true;
                }
            }
        }
        // Executes stop.
        private void Stop()
        {
            registers.PC++;
        }
    }
}
