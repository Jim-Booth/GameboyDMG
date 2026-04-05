// ============================================================================
// Project:     GameboyEmu
// File:        Core/CPU.cs
// Description: LR35902 CPU — full instruction set, CB prefixed opcodes,
//              interrupt handling, and HALT emulation
//              Optimised: sealed class, AggressiveInlining on helpers,
//              separate carry/no-carry overloads, eliminated optional params,
//              reduced redundant memory reads, branchless SetBit
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
    public sealed class CPU(MMU _memory, GameBoy _gameboy)
    {
        private readonly MMU memory = _memory;

        private GameBoy gameboy = _gameboy;

        public Registers registers = new();

        public bool Running { get; set; } = false;

        private bool IME;
        private int _imeEnableDelay;

        private bool Halted;
        public bool IsHalted => Halted;
        private bool HaltBug;
        private bool _instructionHandledInternally;

        public void Reset()
        {
            registers = new Registers();
            Running = true;
            IME = false;
            _imeEnableDelay = 0;
            Halted = false;
            HaltBug = false;
        }

        public int Execute(int opcode)
        {
            _instructionHandledInternally = false;

            if (HaltBug)
            {
                registers.PC--;
                HaltBug = false;
            }

            registers.PC &= 0xFFFF;

            switch (opcode)
            {
                case 0x00:
                    // NOP
                    return 4;
                case 0x01:
                    registers.BC = memory.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x02:
                    memory.WriteByteToMemory(registers.BC, registers.A);
                    return 8;
                case 0x03:
                    if (IsInOamRange(registers.BC))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.BC++;
                    return 8;
                case 0x04:
                    registers.Flags.SetHalfCarryAdd(registers.B, 1);
                    registers.B++;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = false;
                    return 4;
                case 0x05:
                    registers.Flags.SetHalfCarrySub(registers.B, 1);
                    registers.B--;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = true;
                    return 4;
                case 0x06:
                    registers.B = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x07:
                    registers.A = RLC(registers.A);
                    registers.Flags.Z = false;
                    return 4;
                case 0x08:
                    memory.WriteWordToMemory(memory.ReadWordFromMemory(registers.PC), registers.SP);
                    registers.PC += 2;
                    return 20;
                case 0x09:
                    ADDHL(registers.BC);
                    return 8;
                case 0x0A:
                    registers.A = memory.ReadByteFromMemory(registers.BC);
                    return 8;
                case 0x0B:
                    if (IsInOamRange(registers.BC))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.BC--;
                    return 8;
                case 0x0C:
                    registers.Flags.SetHalfCarryAdd(registers.C, 1);
                    registers.C++;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.N = false;
                    return 4;
                case 0x0D:
                    registers.Flags.SetHalfCarrySub(registers.C, 1);
                    registers.C--;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.N = true;
                    return 4;
                case 0x0E:
                    registers.C = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x0F:
                    registers.A = RRC(registers.A);
                    registers.Flags.Z = false;
                    return 4;
                case 0x10:
                    Stop();
                    return 4;
                case 0x11:
                    registers.DE = memory.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x12:
                    memory.WriteByteToMemory(registers.DE, registers.A);
                    return 8;
                case 0x13:
                    if (IsInOamRange(registers.DE))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.DE++;
                    return 8;
                case 0x14:
                    registers.Flags.SetHalfCarryAdd(registers.D, 1);
                    registers.D++;
                    registers.Flags.UpdateZeroFlag(registers.D);
                    registers.Flags.N = false;
                    return 4;
                case 0x15:
                    registers.Flags.SetHalfCarrySub(registers.D, 1);
                    registers.D--;
                    registers.Flags.UpdateZeroFlag(registers.D);
                    registers.Flags.N = true;
                    return 4;
                case 0x16:
                    registers.D = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x17:
                    registers.A = RL(registers.A);
                    registers.Flags.Z = false;
                    return 4;
                case 0x18:
                    JRN((sbyte)(memory.ReadByteFromMemory(registers.PC)));
                    return 12;
                case 0x19:
                    ADDHL(registers.DE);
                    return 8;
                case 0x1A:
                    registers.A = memory.ReadByteFromMemory(registers.DE);
                    return 8;
                case 0x1B:
                    if (IsInOamRange(registers.DE))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.DE--;
                    return 8;
                case 0x1C:
                    registers.Flags.SetHalfCarryAdd(registers.E, 1);
                    registers.E++;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.N = false;
                    return 4;
                case 0x1D:
                    registers.Flags.SetHalfCarrySub(registers.E, 1);
                    registers.E--;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.N = true;
                    return 4;
                case 0x1E:
                    registers.E = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x1F:
                    registers.A = RR(registers.A);
                    registers.Flags.Z = false;
                    return 4;
                case 0x20:
                    return JRZ((sbyte)memory.ReadByteFromMemory(registers.PC), false);
                case 0x21:
                    registers.HL = memory.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x22:
                    memory.WriteByteToMemory(registers.HL, registers.A);
                    registers.HL++;
                    return 8;
                case 0x23:
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.HL++;
                    return 8;
                case 0x24:
                    registers.Flags.SetHalfCarryAdd(registers.H, 1);
                    registers.H++;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = false;
                    return 4;
                case 0x25:
                    registers.Flags.SetHalfCarrySub(registers.H, 1);
                    registers.H--;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = true;
                    return 4;
                case 0x26:
                    registers.H = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x27:
                    DAA();
                    return 4;
                case 0x28:
                    return JRZ((sbyte)memory.ReadByteFromMemory(registers.PC), true);
                case 0x29:
                    ADDHL(registers.HL);
                    return 8;
                case 0x2A:
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    registers.A = memory.ReadByteFromMemory(registers.HL);
                    registers.HL++;
                    return 8;
                case 0x2B:
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.HL--;
                    return 8;
                case 0x2C:
                    registers.Flags.SetHalfCarryAdd(registers.L, 1);
                    registers.L++;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.N = false;
                    return 4;
                case 0x2D:
                    registers.Flags.SetHalfCarrySub(registers.L, 1);
                    registers.L--;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.N = true;
                    return 4;
                case 0x2E:
                    registers.L = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x2F:
                    registers.A = (byte)~registers.A;
                    registers.Flags.N = true;
                    registers.Flags.H = true;
                    return 4;
                case 0x30:
                    return JRC((sbyte)memory.ReadByteFromMemory(registers.PC), false);
                case 0x31:
                    registers.SP = memory.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x32:
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    memory.WriteByteToMemory(registers.HL, registers.A);
                    registers.HL--;
                    return 8;
                case 0x33:
                    if (IsInOamRange(registers.SP))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.SP++;
                    return 8;
                case 0x34:
                    {
                        if (ExecuteIncDecHlTimed(increment: true)) return 0;
                        byte val = memory.ReadByteFromMemory(registers.HL);
                        registers.Flags.SetHalfCarryAdd(val, 1);
                        val++;
                        memory.WriteByteToMemory(registers.HL, val);
                        registers.Flags.UpdateZeroFlag(val);
                        registers.Flags.N = false;
                        return 12;
                    }
                case 0x35:
                    {
                        if (ExecuteIncDecHlTimed(increment: false)) return 0;
                        byte val = memory.ReadByteFromMemory(registers.HL);
                        registers.Flags.SetHalfCarrySub(val, 1);
                        val--;
                        memory.WriteByteToMemory(registers.HL, val);
                        registers.Flags.UpdateZeroFlag(val);
                        registers.Flags.N = true;
                        return 12;
                    }
                case 0x36:
                    if (ExecuteLdHlNTimed()) return 0;
                    memory.WriteByteToMemory(registers.HL, memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 12;
                case 0x37:
                    registers.Flags.C = true;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 4;
                case 0x38:
                    return JRC((sbyte)memory.ReadByteFromMemory(registers.PC), true);
                case 0x39:
                    ADDHL(registers.SP);
                    return 8;
                case 0x3A:
                    if (IsInOamRange(registers.HL))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    registers.A = memory.ReadByteFromMemory(registers.HL);
                    registers.HL--;
                    return 8;
                case 0x3B:
                    if (IsInOamRange(registers.SP))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                    registers.SP--;
                    return 8;
                case 0x3C:
                    registers.Flags.SetHalfCarryAdd(registers.A, 1);
                    registers.A++;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.N = false;
                    return 4;
                case 0x3D:
                    registers.Flags.SetHalfCarrySub(registers.A, 1);
                    registers.A--;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.N = true;
                    return 4;
                case 0x3E:
                    registers.A = memory.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x3F:
                    registers.Flags.C = !registers.Flags.C;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 4;
                case 0x40:
                    return 4;  // LD B,B — nop
                case 0x41:
                    registers.B = registers.C;
                    return 4;
                case 0x42:
                    registers.B = registers.D;
                    return 4;
                case 0x43:
                    registers.B = registers.E;
                    return 4;
                case 0x44:
                    registers.B = registers.H;
                    return 4;
                case 0x45:
                    registers.B = registers.L;
                    return 4;
                case 0x46:
                    registers.B = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x47:
                    registers.B = registers.A;
                    return 4;
                case 0x48:
                    registers.C = registers.B;
                    return 4;
                case 0x49:
                    return 4;  // LD C,C — nop
                case 0x4A:
                    registers.C = registers.D;
                    return 4;
                case 0x4B:
                    registers.C = registers.E;
                    return 4;
                case 0x4C:
                    registers.C = registers.H;
                    return 4;
                case 0x4D:
                    registers.C = registers.L;
                    return 4;
                case 0x4E:
                    registers.C = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x4F:
                    registers.C = registers.A;
                    return 4;
                case 0x50:
                    registers.D = registers.B;
                    return 4;
                case 0x51:
                    registers.D = registers.C;
                    return 4;
                case 0x52:
                    return 4;  // LD D,D — nop
                case 0x53:
                    registers.D = registers.E;
                    return 4;
                case 0x54:
                    registers.D = registers.H;
                    return 4;
                case 0x55:
                    registers.D = registers.L;
                    return 4;
                case 0x56:
                    registers.D = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x57:
                    registers.D = registers.A;
                    return 4;
                case 0x58:
                    registers.E = registers.B;
                    return 4;
                case 0x59:
                    registers.E = registers.C;
                    return 4;
                case 0x5A:
                    registers.E = registers.D;
                    return 4;
                case 0x5B:
                    return 4;  // LD E,E — nop
                case 0x5C:
                    registers.E = registers.H;
                    return 4;
                case 0x5D:
                    registers.E = registers.L;
                    return 4;
                case 0x5E:
                    registers.E = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x5F:
                    registers.E = registers.A;
                    return 4;
                case 0x60:
                    registers.H = registers.B;
                    return 4;
                case 0x61:
                    registers.H = registers.C;
                    return 4;
                case 0x62:
                    registers.H = registers.D;
                    return 4;
                case 0x63:
                    registers.H = registers.E;
                    return 4;
                case 0x64:
                    return 4;  // LD H,H — nop
                case 0x65:
                    registers.H = registers.L;
                    return 4;
                case 0x66:
                    registers.H = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x67:
                    registers.H = registers.A;
                    return 4;
                case 0x68:
                    registers.L = registers.B;
                    return 4;
                case 0x69:
                    registers.L = registers.C;
                    return 4;
                case 0x6A:
                    registers.L = registers.D;
                    return 4;
                case 0x6B:
                    registers.L = registers.E;
                    return 4;
                case 0x6C:
                    registers.L = registers.H;
                    return 4;
                case 0x6D:
                    return 4;  // LD L,L — nop
                case 0x6E:
                    registers.L = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x6F:
                    registers.L = registers.A;
                    return 4;
                case 0x70:
                    memory.WriteByteToMemory(registers.HL, registers.B);
                    return 8;
                case 0x71:
                    memory.WriteByteToMemory(registers.HL, registers.C);
                    return 8;
                case 0x72:
                    memory.WriteByteToMemory(registers.HL, registers.D);
                    return 8;
                case 0x73:
                    memory.WriteByteToMemory(registers.HL, registers.E);
                    return 8;
                case 0x74:
                    memory.WriteByteToMemory(registers.HL, registers.H);
                    return 8;
                case 0x75:
                    memory.WriteByteToMemory(registers.HL, registers.L);
                    return 8;
                case 0x76:
                    Halt(); return 4;
                case 0x77:
                    memory.WriteByteToMemory(registers.HL, registers.A);
                    return 8;
                case 0x78:
                    registers.A = registers.B;
                    return 4;
                case 0x79:
                    registers.A = registers.C;
                    return 4;
                case 0x7A:
                    registers.A = registers.D;
                    return 4;
                case 0x7B:
                    registers.A = registers.E;
                    return 4;
                case 0x7C:
                    registers.A = registers.H;
                    return 4;
                case 0x7D:
                    registers.A = registers.L;
                    return 4;
                case 0x7E:
                    registers.A = memory.ReadByteFromMemory(registers.HL);
                    return 8;
                case 0x7F:
                    return 4;  // LD A,A — nop
                case 0x80:
                    ADD(registers.B);
                    return 4;
                case 0x81:
                    ADD(registers.C);
                    return 4;
                case 0x82:
                    ADD(registers.D);
                    return 4;
                case 0x83:
                    ADD(registers.E);
                    return 4;
                case 0x84:
                    ADD(registers.H);
                    return 4;
                case 0x85:
                    ADD(registers.L);
                    return 4;
                case 0x86:
                    ADD(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x87:
                    ADD(registers.A);
                    return 4;
                case 0x88:
                    ADC(registers.B);
                    return 4;
                case 0x89:
                    ADC(registers.C);
                    return 4;
                case 0x8A:
                    ADC(registers.D);
                    return 4;
                case 0x8B:
                    ADC(registers.E);
                    return 4;
                case 0x8C:
                    ADC(registers.H);
                    return 4;
                case 0x8D:
                    ADC(registers.L);
                    return 4;
                case 0x8E:
                    ADC(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x8F:
                    ADC(registers.A);
                    return 4;
                case 0x90:
                    SUB(registers.B);
                    return 4;
                case 0x91:
                    SUB(registers.C);
                    return 4;
                case 0x92:
                    SUB(registers.D);
                    return 4;
                case 0x93:
                    SUB(registers.E);
                    return 4;
                case 0x94:
                    SUB(registers.H);
                    return 4;
                case 0x95:
                    SUB(registers.L);
                    return 4;
                case 0x96:
                    SUB(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x97:
                    SUB(registers.A);
                    return 4;
                case 0x98:
                    SBC(registers.B);
                    return 4;
                case 0x99:
                    SBC(registers.C);
                    return 4;
                case 0x9A:
                    SBC(registers.D);
                    return 4;
                case 0x9B:
                    SBC(registers.E);
                    return 4;
                case 0x9C:
                    SBC(registers.H);
                    return 4;
                case 0x9D:
                    SBC(registers.L);
                    return 4;
                case 0x9E:
                    SBC(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x9F:
                    SBC(registers.A);
                    return 4;
                case 0xA0:
                    AND(registers.B);
                    return 4;
                case 0xA1:
                    AND(registers.C);
                    return 4;
                case 0xA2:
                    AND(registers.D);
                    return 4;
                case 0xA3:
                    AND(registers.E);
                    return 4;
                case 0xA4:
                    AND(registers.H);
                    return 4;
                case 0xA5:
                    AND(registers.L);
                    return 4;
                case 0xA6:
                    AND(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0xA7:
                    AND(registers.A);
                    return 4;
                case 0xA8:
                    XOR(registers.B);
                    return 4;
                case 0xA9:
                    XOR(registers.C);
                    return 4;
                case 0xAA:
                    XOR(registers.D);
                    return 4;
                case 0xAB:
                    XOR(registers.E);
                    return 4;
                case 0xAC:
                    XOR(registers.H);
                    return 4;
                case 0xAD:
                    XOR(registers.L);
                    return 4;
                case 0xAE:
                    XOR(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0xAF:
                    XOR(registers.A);
                    return 4;
                case 0xB0:
                    OR(registers.B);
                    return 4;
                case 0xB1:
                    OR(registers.C);
                    return 4;
                case 0xB2:
                    OR(registers.D);
                    return 4;
                case 0xB3:
                    OR(registers.E);
                    return 4;
                case 0xB4:
                    OR(registers.H);
                    return 4;
                case 0xB5:
                    OR(registers.L);
                    return 4;
                case 0xB6:
                    OR(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0xB7:
                    OR(registers.A);
                    return 4;
                case 0xB8:
                    CP(registers.B);
                    return 4;
                case 0xB9:
                    CP(registers.C);
                    return 4;
                case 0xBA:
                    CP(registers.D);
                    return 4;
                case 0xBB:
                    CP(registers.E);
                    return 4;
                case 0xBC:
                    CP(registers.H);
                    return 4;
                case 0xBD:
                    CP(registers.L);
                    return 4;
                case 0xBE:
                    CP(memory.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0xBF:
                    CP(registers.A);
                    return 4;
                case 0xC0:
                    if (!registers.Flags.Z)
                    {
                        registers.PC = PopWordFromStack();
                        return 20;
                    }
                    return 8;
                case 0xC1:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.BC = PopWordFromStack();
                    return 12;
                case 0xC2:
                    if (!registers.Flags.Z)
                    {
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xC3:
                    registers.PC = memory.ReadWordFromMemory(registers.PC);
                    return 16;
                case 0xC4:
                    if (!registers.Flags.Z)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xC5:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    PushWordToStack(registers.BC);
                    return 16;
                case 0xC6:
                    ADD(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xC7:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x00;
                    return 16;
                case 0xC8:
                    if (registers.Flags.Z)
                    {
                        registers.PC = PopWordFromStack();
                        return 20;
                    }
                    return 8;
                case 0xC9:
                    registers.PC = PopWordFromStack();
                    return 16;
                case 0xCA:
                    if (registers.Flags.Z)
                    {
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xCB:
                    {
                        byte cbOpcode = memory.ReadByteFromMemory(registers.PC++);
                        if ((cbOpcode & 0x07) == 0x06)
                            return ExecuteCBTimedHL(cbOpcode);
                        return ExecuteCB(cbOpcode);
                    }
                case 0xCC:
                    if (registers.Flags.Z)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                    {
                        registers.PC += 2;
                        return 12;
                    }
                case 0xCD:
                    PushWordToStack(registers.PC + 2);
                    registers.PC = memory.ReadWordFromMemory(registers.PC);
                    return 24;
                case 0xCE:
                    ADC(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xCF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x08;
                    return 16;
                case 0xD0:
                    if (!registers.Flags.C)
                    {
                        registers.PC = PopWordFromStack();
                        return 20;
                    }
                    return 8;
                case 0xD1:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.DE = PopWordFromStack();
                    return 12;
                case 0xD2:
                    if (!registers.Flags.C)
                    {
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xD4:
                    if (!registers.Flags.C)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xD5:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    PushWordToStack(registers.DE);
                    return 16;
                case 0xD6:
                    SUB(memory.ReadByteFromMemory(registers.PC));
                    registers.PC++;
                    return 8;
                case 0xD7:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x10;
                    return 16;
                case 0xD8:
                    if (registers.Flags.C)
                    {
                        registers.PC = PopWordFromStack();
                        return 20;
                    }
                    return 8;
                case 0xD9:
                    registers.PC = PopWordFromStack();
                    IME = true;
                    return 16;
                case 0xDA:
                    if (registers.Flags.C)
                    {
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xDC:
                    if (registers.Flags.C)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xDE:
                    SBC(memory.ReadByteFromMemory(registers.PC));
                    registers.PC++;
                    return 8;
                case 0xDF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x18;
                    return 16;
                case 0xE0:
                    if (ExecuteLdhAToA8Timed()) return 0;
                    memory.WriteByteToMemory((uint)(memory.ReadByteFromMemory(registers.PC) + 0xFF00), registers.A);
                    registers.PC += 1;
                    return 12;
                case 0xE1:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.HL = PopWordFromStack();
                    return 12;
                case 0xE2:
                    if (ExecuteLdhAToCTimed()) return 0;
                    memory.WriteByteToMemory((uint)(registers.C + 0xFF00), registers.A);
                    return 8;
                case 0xE5:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    PushWordToStack(registers.HL);
                    return 16;
                case 0xE6:
                    AND(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xE7:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x20;
                    return 16;
                case 0xE8:
                    registers.SP = ADDR8(registers.SP);
                    return 16;
                case 0xE9:
                    registers.PC = registers.HL;
                    return 4;
                case 0xEA:
                    if (ExecuteLdAToA16Timed()) return 0;
                    memory.WriteByteToMemory(memory.ReadWordFromMemory(registers.PC), registers.A);
                    registers.PC += 2;
                    return 16;
                case 0xEE:
                    XOR(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xEF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x28;
                    return 16;
                case 0xF0:
                    if (ExecuteLdhAFromA8Timed()) return 0;
                    registers.A = memory.ReadByteFromMemory((uint)(0xFF00 + memory.ReadByteFromMemory(registers.PC)));
                    registers.PC += 1;
                    return 12;
                case 0xF1:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.ReadDuringIncDec, 1);
                    if (IsInOamRange(registers.SP + 1))
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Read, 2);
                    registers.AF = PopWordFromStack();
                    return 12;
                case 0xF2:
                    if (ExecuteLdhAFromCTimed()) return 0;
                    registers.A = memory.ReadByteFromMemory((uint)(0xFF00 + registers.C));
                    return 8;
                case 0xF3:
                    IME = false;
                    _imeEnableDelay = 0;
                    return 4;
                case 0xF5:
                    if (IsInOamRange(registers.SP) || IsInOamRange(registers.SP - 1) || IsInOamRange(registers.SP - 2))
                    {
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 1);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 2);
                        gameboy.TriggerOamBug(GameBoy.OamBugAccessType.Write, 3);
                    }
                    PushWordToStack(registers.AF);
                    return 16;
                case 0xF6:
                    OR(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xF7:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x30;
                    return 16;
                case 0xF8:
                    registers.HL = ADDR8(registers.SP);
                    return 12;
                case 0xF9:
                    registers.SP = registers.HL;
                    return 8;
                case 0xFA:
                    if (ExecuteLdAFromA16Timed()) return 0;
                    registers.A = memory.ReadByteFromMemory(memory.ReadWordFromMemory(registers.PC));
                    registers.PC += 2;
                    return 16;
                case 0xFB:
                    // EI enables IME after the following instruction completes.
                    _imeEnableDelay = 2;
                    return 4;
                case 0xFE:
                    CP(memory.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xFF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x38;
                    return 16;
            }
            return 0;
        }

        private int ExecuteCB(int opcodeHi)
        {
            switch (opcodeHi)
            {
                case 0x00: registers.B = RLC(registers.B); return 8;
                case 0x01: registers.C = RLC(registers.C); return 8;
                case 0x02: registers.D = RLC(registers.D); return 8;
                case 0x03: registers.E = RLC(registers.E); return 8;
                case 0x04: registers.H = RLC(registers.H); return 8;
                case 0x05: registers.L = RLC(registers.L); return 8;
                case 0x06: memory.WriteByteToMemory(registers.HL, RLC(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x07: registers.A = RLC(registers.A); return 8;
                case 0x08: registers.B = RRC(registers.B); return 8;
                case 0x09: registers.C = RRC(registers.C); return 8;
                case 0x0A: registers.D = RRC(registers.D); return 8;
                case 0x0B: registers.E = RRC(registers.E); return 8;
                case 0x0C: registers.H = RRC(registers.H); return 8;
                case 0x0D: registers.L = RRC(registers.L); return 8;
                case 0x0E: memory.WriteByteToMemory(registers.HL, RRC(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x0F: registers.A = RRC(registers.A); return 8;
                case 0x10: registers.B = RL(registers.B); return 8;
                case 0x11: registers.C = RL(registers.C); return 8;
                case 0x12: registers.D = RL(registers.D); return 8;
                case 0x13: registers.E = RL(registers.E); return 8;
                case 0x14: registers.H = RL(registers.H); return 8;
                case 0x15: registers.L = RL(registers.L); return 8;
                case 0x16: memory.WriteByteToMemory(registers.HL, RL(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x17: registers.A = RL(registers.A); return 8;
                case 0x18: registers.B = RR(registers.B); return 8;
                case 0x19: registers.C = RR(registers.C); return 8;
                case 0x1A: registers.D = RR(registers.D); return 8;
                case 0x1B: registers.E = RR(registers.E); return 8;
                case 0x1C: registers.H = RR(registers.H); return 8;
                case 0x1D: registers.L = RR(registers.L); return 8;
                case 0x1E: memory.WriteByteToMemory(registers.HL, RR(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x1F: registers.A = RR(registers.A); return 8;
                case 0x20: registers.B = SHL(registers.B); return 8;
                case 0x21: registers.C = SHL(registers.C); return 8;
                case 0x22: registers.D = SHL(registers.D); return 8;
                case 0x23: registers.E = SHL(registers.E); return 8;
                case 0x24: registers.H = SHL(registers.H); return 8;
                case 0x25: registers.L = SHL(registers.L); return 8;
                case 0x26: memory.WriteByteToMemory(registers.HL, SHL(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x27: registers.A = SHL(registers.A); return 8;
                case 0x28: registers.B = SHR(registers.B); return 8;
                case 0x29: registers.C = SHR(registers.C); return 8;
                case 0x2A: registers.D = SHR(registers.D); return 8;
                case 0x2B: registers.E = SHR(registers.E); return 8;
                case 0x2C: registers.H = SHR(registers.H); return 8;
                case 0x2D: registers.L = SHR(registers.L); return 8;
                case 0x2E: memory.WriteByteToMemory(registers.HL, SHR(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x2F: registers.A = SHR(registers.A); return 8;
                case 0x30: registers.B = SwapNibble(registers.B); return 8;
                case 0x31: registers.C = SwapNibble(registers.C); return 8;
                case 0x32: registers.D = SwapNibble(registers.D); return 8;
                case 0x33: registers.E = SwapNibble(registers.E); return 8;
                case 0x34: registers.H = SwapNibble(registers.H); return 8;
                case 0x35: registers.L = SwapNibble(registers.L); return 8;
                case 0x36: memory.WriteByteToMemory(registers.HL, SwapNibble(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x37: registers.A = SwapNibble(registers.A); return 8;
                case 0x38: registers.B = SRL(registers.B); return 8;
                case 0x39: registers.C = SRL(registers.C); return 8;
                case 0x3A: registers.D = SRL(registers.D); return 8;
                case 0x3B: registers.E = SRL(registers.E); return 8;
                case 0x3C: registers.H = SRL(registers.H); return 8;
                case 0x3D: registers.L = SRL(registers.L); return 8;
                case 0x3E: memory.WriteByteToMemory(registers.HL, SRL(memory.ReadByteFromMemory(registers.HL))); return 16;
                case 0x3F: registers.A = SRL(registers.A); return 8;
                case 0x40: CompBit(registers.B, 0); return 8;
                case 0x41: CompBit(registers.C, 0); return 8;
                case 0x42: CompBit(registers.D, 0); return 8;
                case 0x43: CompBit(registers.E, 0); return 8;
                case 0x44: CompBit(registers.H, 0); return 8;
                case 0x45: CompBit(registers.L, 0); return 8;
                case 0x46: CompBit(memory.ReadByteFromMemory(registers.HL), 0); return 12;
                case 0x47: CompBit(registers.A, 0); return 8;
                case 0x48: CompBit(registers.B, 1); return 8;
                case 0x49: CompBit(registers.C, 1); return 8;
                case 0x4A: CompBit(registers.D, 1); return 8;
                case 0x4B: CompBit(registers.E, 1); return 8;
                case 0x4C: CompBit(registers.H, 1); return 8;
                case 0x4D: CompBit(registers.L, 1); return 8;
                case 0x4E: CompBit(memory.ReadByteFromMemory(registers.HL), 1); return 12;
                case 0x4F: CompBit(registers.A, 1); return 8;
                case 0x50: CompBit(registers.B, 2); return 8;
                case 0x51: CompBit(registers.C, 2); return 8;
                case 0x52: CompBit(registers.D, 2); return 8;
                case 0x53: CompBit(registers.E, 2); return 8;
                case 0x54: CompBit(registers.H, 2); return 8;
                case 0x55: CompBit(registers.L, 2); return 8;
                case 0x56: CompBit(memory.ReadByteFromMemory(registers.HL), 2); return 12;
                case 0x57: CompBit(registers.A, 2); return 8;
                case 0x58: CompBit(registers.B, 3); return 8;
                case 0x59: CompBit(registers.C, 3); return 8;
                case 0x5A: CompBit(registers.D, 3); return 8;
                case 0x5B: CompBit(registers.E, 3); return 8;
                case 0x5C: CompBit(registers.H, 3); return 8;
                case 0x5D: CompBit(registers.L, 3); return 8;
                case 0x5E: CompBit(memory.ReadByteFromMemory(registers.HL), 3); return 12;
                case 0x5F: CompBit(registers.A, 3); return 8;
                case 0x60: CompBit(registers.B, 4); return 8;
                case 0x61: CompBit(registers.C, 4); return 8;
                case 0x62: CompBit(registers.D, 4); return 8;
                case 0x63: CompBit(registers.E, 4); return 8;
                case 0x64: CompBit(registers.H, 4); return 8;
                case 0x65: CompBit(registers.L, 4); return 8;
                case 0x66: CompBit(memory.ReadByteFromMemory(registers.HL), 4); return 12;
                case 0x67: CompBit(registers.A, 4); return 8;
                case 0x68: CompBit(registers.B, 5); return 8;
                case 0x69: CompBit(registers.C, 5); return 8;
                case 0x6A: CompBit(registers.D, 5); return 8;
                case 0x6B: CompBit(registers.E, 5); return 8;
                case 0x6C: CompBit(registers.H, 5); return 8;
                case 0x6D: CompBit(registers.L, 5); return 8;
                case 0x6E: CompBit(memory.ReadByteFromMemory(registers.HL), 5); return 12;
                case 0x6F: CompBit(registers.A, 5); return 8;
                case 0x70: CompBit(registers.B, 6); return 8;
                case 0x71: CompBit(registers.C, 6); return 8;
                case 0x72: CompBit(registers.D, 6); return 8;
                case 0x73: CompBit(registers.E, 6); return 8;
                case 0x74: CompBit(registers.H, 6); return 8;
                case 0x75: CompBit(registers.L, 6); return 8;
                case 0x76: CompBit(memory.ReadByteFromMemory(registers.HL), 6); return 12;
                case 0x77: CompBit(registers.A, 6); return 8;
                case 0x78: CompBit(registers.B, 7); return 8;
                case 0x79: CompBit(registers.C, 7); return 8;
                case 0x7A: CompBit(registers.D, 7); return 8;
                case 0x7B: CompBit(registers.E, 7); return 8;
                case 0x7C: CompBit(registers.H, 7); return 8;
                case 0x7D: CompBit(registers.L, 7); return 8;
                case 0x7E: CompBit(memory.ReadByteFromMemory(registers.HL), 7); return 12;
                case 0x7F: CompBit(registers.A, 7); return 8;
                case 0x80: registers.B = ResBit(registers.B, 0); return 8;
                case 0x81: registers.C = ResBit(registers.C, 0); return 8;
                case 0x82: registers.D = ResBit(registers.D, 0); return 8;
                case 0x83: registers.E = ResBit(registers.E, 0); return 8;
                case 0x84: registers.H = ResBit(registers.H, 0); return 8;
                case 0x85: registers.L = ResBit(registers.L, 0); return 8;
                case 0x86: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 0)); return 16;
                case 0x87: registers.A = ResBit(registers.A, 0); return 8;
                case 0x88: registers.B = ResBit(registers.B, 1); return 8;
                case 0x89: registers.C = ResBit(registers.C, 1); return 8;
                case 0x8A: registers.D = ResBit(registers.D, 1); return 8;
                case 0x8B: registers.E = ResBit(registers.E, 1); return 8;
                case 0x8C: registers.H = ResBit(registers.H, 1); return 8;
                case 0x8D: registers.L = ResBit(registers.L, 1); return 8;
                case 0x8E: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 1)); return 16;
                case 0x8F: registers.A = ResBit(registers.A, 1); return 8;
                case 0x90: registers.B = ResBit(registers.B, 2); return 8;
                case 0x91: registers.C = ResBit(registers.C, 2); return 8;
                case 0x92: registers.D = ResBit(registers.D, 2); return 8;
                case 0x93: registers.E = ResBit(registers.E, 2); return 8;
                case 0x94: registers.H = ResBit(registers.H, 2); return 8;
                case 0x95: registers.L = ResBit(registers.L, 2); return 8;
                case 0x96: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 2)); return 16;
                case 0x97: registers.A = ResBit(registers.A, 2); return 8;
                case 0x98: registers.B = ResBit(registers.B, 3); return 8;
                case 0x99: registers.C = ResBit(registers.C, 3); return 8;
                case 0x9A: registers.D = ResBit(registers.D, 3); return 8;
                case 0x9B: registers.E = ResBit(registers.E, 3); return 8;
                case 0x9C: registers.H = ResBit(registers.H, 3); return 8;
                case 0x9D: registers.L = ResBit(registers.L, 3); return 8;
                case 0x9E: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 3)); return 16;
                case 0x9F: registers.A = ResBit(registers.A, 3); return 8;
                case 0xA0: registers.B = ResBit(registers.B, 4); return 8;
                case 0xA1: registers.C = ResBit(registers.C, 4); return 8;
                case 0xA2: registers.D = ResBit(registers.D, 4); return 8;
                case 0xA3: registers.E = ResBit(registers.E, 4); return 8;
                case 0xA4: registers.H = ResBit(registers.H, 4); return 8;
                case 0xA5: registers.L = ResBit(registers.L, 4); return 8;
                case 0xA6: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 4)); return 16;
                case 0xA7: registers.A = ResBit(registers.A, 4); return 8;
                case 0xA8: registers.B = ResBit(registers.B, 5); return 8;
                case 0xA9: registers.C = ResBit(registers.C, 5); return 8;
                case 0xAA: registers.D = ResBit(registers.D, 5); return 8;
                case 0xAB: registers.E = ResBit(registers.E, 5); return 8;
                case 0xAC: registers.H = ResBit(registers.H, 5); return 8;
                case 0xAD: registers.L = ResBit(registers.L, 5); return 8;
                case 0xAE: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 5)); return 16;
                case 0xAF: registers.A = ResBit(registers.A, 5); return 8;
                case 0xB0: registers.B = ResBit(registers.B, 6); return 8;
                case 0xB1: registers.C = ResBit(registers.C, 6); return 8;
                case 0xB2: registers.D = ResBit(registers.D, 6); return 8;
                case 0xB3: registers.E = ResBit(registers.E, 6); return 8;
                case 0xB4: registers.H = ResBit(registers.H, 6); return 8;
                case 0xB5: registers.L = ResBit(registers.L, 6); return 8;
                case 0xB6: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 6)); return 16;
                case 0xB7: registers.A = ResBit(registers.A, 6); return 8;
                case 0xB8: registers.B = ResBit(registers.B, 7); return 8;
                case 0xB9: registers.C = ResBit(registers.C, 7); return 8;
                case 0xBA: registers.D = ResBit(registers.D, 7); return 8;
                case 0xBB: registers.E = ResBit(registers.E, 7); return 8;
                case 0xBC: registers.H = ResBit(registers.H, 7); return 8;
                case 0xBD: registers.L = ResBit(registers.L, 7); return 8;
                case 0xBE: memory.WriteByteToMemory(registers.HL, ResBit(memory.ReadByteFromMemory(registers.HL), 7)); return 16;
                case 0xBF: registers.A = ResBit(registers.A, 7); return 8;
                case 0xC0: registers.B = SetBitVal(registers.B, 0); return 8;
                case 0xC1: registers.C = SetBitVal(registers.C, 0); return 8;
                case 0xC2: registers.D = SetBitVal(registers.D, 0); return 8;
                case 0xC3: registers.E = SetBitVal(registers.E, 0); return 8;
                case 0xC4: registers.H = SetBitVal(registers.H, 0); return 8;
                case 0xC5: registers.L = SetBitVal(registers.L, 0); return 8;
                case 0xC6: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 0)); return 16;
                case 0xC7: registers.A = SetBitVal(registers.A, 0); return 8;
                case 0xC8: registers.B = SetBitVal(registers.B, 1); return 8;
                case 0xC9: registers.C = SetBitVal(registers.C, 1); return 8;
                case 0xCA: registers.D = SetBitVal(registers.D, 1); return 8;
                case 0xCB: registers.E = SetBitVal(registers.E, 1); return 8;
                case 0xCC: registers.H = SetBitVal(registers.H, 1); return 8;
                case 0xCD: registers.L = SetBitVal(registers.L, 1); return 8;
                case 0xCE: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 1)); return 16;
                case 0xCF: registers.A = SetBitVal(registers.A, 1); return 8;
                case 0xD0: registers.B = SetBitVal(registers.B, 2); return 8;
                case 0xD1: registers.C = SetBitVal(registers.C, 2); return 8;
                case 0xD2: registers.D = SetBitVal(registers.D, 2); return 8;
                case 0xD3: registers.E = SetBitVal(registers.E, 2); return 8;
                case 0xD4: registers.H = SetBitVal(registers.H, 2); return 8;
                case 0xD5: registers.L = SetBitVal(registers.L, 2); return 8;
                case 0xD6: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 2)); return 16;
                case 0xD7: registers.A = SetBitVal(registers.A, 2); return 8;
                case 0xD8: registers.B = SetBitVal(registers.B, 3); return 8;
                case 0xD9: registers.C = SetBitVal(registers.C, 3); return 8;
                case 0xDA: registers.D = SetBitVal(registers.D, 3); return 8;
                case 0xDB: registers.E = SetBitVal(registers.E, 3); return 8;
                case 0xDC: registers.H = SetBitVal(registers.H, 3); return 8;
                case 0xDD: registers.L = SetBitVal(registers.L, 3); return 8;
                case 0xDE: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 3)); return 16;
                case 0xDF: registers.A = SetBitVal(registers.A, 3); return 8;
                case 0xE0: registers.B = SetBitVal(registers.B, 4); return 8;
                case 0xE1: registers.C = SetBitVal(registers.C, 4); return 8;
                case 0xE2: registers.D = SetBitVal(registers.D, 4); return 8;
                case 0xE3: registers.E = SetBitVal(registers.E, 4); return 8;
                case 0xE4: registers.H = SetBitVal(registers.H, 4); return 8;
                case 0xE5: registers.L = SetBitVal(registers.L, 4); return 8;
                case 0xE6: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 4)); return 16;
                case 0xE7: registers.A = SetBitVal(registers.A, 4); return 8;
                case 0xE8: registers.B = SetBitVal(registers.B, 5); return 8;
                case 0xE9: registers.C = SetBitVal(registers.C, 5); return 8;
                case 0xEA: registers.D = SetBitVal(registers.D, 5); return 8;
                case 0xEB: registers.E = SetBitVal(registers.E, 5); return 8;
                case 0xEC: registers.H = SetBitVal(registers.H, 5); return 8;
                case 0xED: registers.L = SetBitVal(registers.L, 5); return 8;
                case 0xEE: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 5)); return 16;
                case 0xEF: registers.A = SetBitVal(registers.A, 5); return 8;
                case 0xF0: registers.B = SetBitVal(registers.B, 6); return 8;
                case 0xF1: registers.C = SetBitVal(registers.C, 6); return 8;
                case 0xF2: registers.D = SetBitVal(registers.D, 6); return 8;
                case 0xF3: registers.E = SetBitVal(registers.E, 6); return 8;
                case 0xF4: registers.H = SetBitVal(registers.H, 6); return 8;
                case 0xF5: registers.L = SetBitVal(registers.L, 6); return 8;
                case 0xF6: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 6)); return 16;
                case 0xF7: registers.A = SetBitVal(registers.A, 6); return 8;
                case 0xF8: registers.B = SetBitVal(registers.B, 7); return 8;
                case 0xF9: registers.C = SetBitVal(registers.C, 7); return 8;
                case 0xFA: registers.D = SetBitVal(registers.D, 7); return 8;
                case 0xFB: registers.E = SetBitVal(registers.E, 7); return 8;
                case 0xFC: registers.H = SetBitVal(registers.H, 7); return 8;
                case 0xFD: registers.L = SetBitVal(registers.L, 7); return 8;
                case 0xFE: memory.WriteByteToMemory(registers.HL, SetBitVal(memory.ReadByteFromMemory(registers.HL), 7)); return 16;
                case 0xFF: registers.A = SetBitVal(registers.A, 7); return 8;
            }
            return 0;
        }

        // ---- ALU: separate ADD/ADC and SUB/SBC to eliminate optional-param branch ----

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

        private void ADDHL(uint source16bitRegister)
        {
            uint value = registers.HL + source16bitRegister;
            registers.Flags.H = ((registers.HL & 0xFFF) + (source16bitRegister & 0xFFF)) > 0xFFF;
            registers.Flags.C = value > 0xFFFF;
            registers.Flags.N = false;
            registers.HL = value;
        }

        private uint ADDR8(uint value)
        {
            byte sb = memory.ReadByteFromMemory(registers.PC++);
            registers.Flags.SetHalfCarryAdd((byte)value, sb);
            registers.Flags.UpdateCarryFlag((byte)value + sb);
            registers.Flags.N = false;
            registers.Flags.Z = false;
            return (uint)((sbyte)sb + value);
        }

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

        // ---- Logic ops: direct field writes instead of FromByte ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AND(byte val)
        {
            registers.A &= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = true;
            registers.Flags.C = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void XOR(byte val)
        {
            registers.A ^= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = false;
            registers.Flags.C = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OR(byte val)
        {
            registers.A |= val;
            registers.Flags.Z = registers.A == 0;
            registers.Flags.N = false;
            registers.Flags.H = false;
            registers.Flags.C = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CP(byte val)
        {
            int result = registers.A - val;
            registers.Flags.Z = (byte)result == 0;
            registers.Flags.N = true;
            registers.Flags.H = (registers.A & 0xF) < (val & 0xF);
            registers.Flags.C = (result >> 8) != 0;
        }

        // ---- Rotate: separate RLC/RL/RRC/RR to eliminate optional-param branches ----

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void JRN(sbyte value)
        {
            registers.PC = (uint)(registers.PC + value + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int JRZ(sbyte value, bool state)
        {
            if (registers.Flags.Z == state)
            {
                registers.PC = (uint)(registers.PC + value + 1);
                return 12;
            }
            registers.PC += 1;
            return 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int JRC(sbyte value, bool state)
        {
            if (registers.Flags.C == state)
            {
                registers.PC = (uint)(registers.PC + value + 1);
                return 12;
            }
            registers.PC += 1;
            return 8;
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CompBit(int register, int bitIndex)
        {
            registers.Flags.Z = ((register >> bitIndex) & 1) == 0;
            registers.Flags.H = true;
            registers.Flags.N = false;
        }

        // ---- Branchless bit set/reset ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte SetBitVal(int register, int bitIndex)
            => (byte)(register | (1 << bitIndex));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ResBit(int register, int bitIndex)
            => (byte)(register & ~(1 << bitIndex));

        // Legacy SetBit for GameBoy.cs compatibility (interrupt flag clearing)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte SetBit(int register, int bitIndex, int newBitValue)
            => newBitValue != 0
                ? (byte)(register | (1 << bitIndex))
                : (byte)(register & ~(1 << bitIndex));

        public void PushWordToStack(uint value)
        {
            registers.SP -= 2;
            memory.WriteWordToMemory(registers.SP, value);
        }

        public uint PopWordFromStack()
        {
            uint value = memory.ReadWordFromMemory(registers.SP);
            registers.SP += 2;
            return value;
        }

        public void UpdateIME()
        {
            if (_imeEnableDelay > 0)
            {
                _imeEnableDelay--;
                if (_imeEnableDelay == 0)
                    IME = true;
            }
        }

        public bool ConsumeInstructionHandledInternally()
        {
            bool handled = _instructionHandledInternally;
            _instructionHandledInternally = false;
            return handled;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void StepInternal(int cycles)
        {
            _instructionHandledInternally = true;
            gameboy.AdvanceHardwareFromCpu(cycles);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsInOamRange(uint value)
            => value >= 0xFE00 && value <= 0xFEFF;

        private bool ExecuteLdhAFromA8Timed()
        {
            byte offset = memory.ReadByteFromMemory(registers.PC++);
            StepInternal(12); // complete instruction timing before access
            registers.A = memory.ReadByteFromMemory((uint)(0xFF00 + offset));
            return true;
        }

        private bool ExecuteLdAFromA16Timed()
        {
            byte lo = memory.ReadByteFromMemory(registers.PC++);
            byte hi = memory.ReadByteFromMemory(registers.PC++);
            StepInternal(8); // setup
            registers.A = memory.ReadByteFromMemory((uint)(lo | (hi << 8)));
            StepInternal(8); // remaining cycles
            return true;
        }

        private bool ExecuteLdhAToA8Timed()
        {
            byte offset = memory.ReadByteFromMemory(registers.PC++);
            StepInternal(12); // complete instruction timing before access
            memory.WriteByteToMemory((uint)(0xFF00 + offset), registers.A);
            return true;
        }

        private bool ExecuteLdhAToCTimed()
        {
            StepInternal(4); // setup
            memory.WriteByteToMemory((uint)(0xFF00 + registers.C), registers.A);
            StepInternal(4); // remaining cycles
            return true;
        }

        private bool ExecuteLdhAFromCTimed()
        {
            StepInternal(4); // setup
            registers.A = memory.ReadByteFromMemory((uint)(0xFF00 + registers.C));
            StepInternal(4); // remaining cycles
            return true;
        }

        private bool ExecuteLdAToA16Timed()
        {
            byte lo = memory.ReadByteFromMemory(registers.PC++);
            byte hi = memory.ReadByteFromMemory(registers.PC++);
            StepInternal(8); // setup
            memory.WriteByteToMemory((uint)(lo | (hi << 8)), registers.A);
            StepInternal(8); // remaining cycles
            return true;
        }

        private bool ExecuteLdHlNTimed()
        {
            byte value = memory.ReadByteFromMemory(registers.PC++);
            StepInternal(4); // setup
            memory.WriteByteToMemory(registers.HL, value);
            StepInternal(8); // remaining cycles
            return true;
        }

        private bool ExecuteIncDecHlTimed(bool increment)
        {
            byte val = memory.ReadByteFromMemory(registers.HL);
            StepInternal(4); // memory read cycle

            if (increment)
            {
                registers.Flags.SetHalfCarryAdd(val, 1);
                val++;
                registers.Flags.UpdateZeroFlag(val);
                registers.Flags.N = false;
            }
            else
            {
                registers.Flags.SetHalfCarrySub(val, 1);
                val--;
                registers.Flags.UpdateZeroFlag(val);
                registers.Flags.N = true;
            }

            memory.WriteByteToMemory(registers.HL, val);
            StepInternal(8); // remaining cycles
            return true;
        }

        private int ExecuteCBTimedHL(int cbOpcode)
        {
            bool isBitTest = (cbOpcode & 0xC0) == 0x40;
            StepInternal(4); // alignment cycle
            byte value = memory.ReadByteFromMemory(registers.HL);

            if (isBitTest)
            {
                CompBit(value, (cbOpcode >> 3) & 0x07);
                StepInternal(8); // remaining cycles
                return 0;
            }

            byte newValue = cbOpcode switch
            {
                0x06 => RLC(value),
                0x0E => RRC(value),
                0x16 => RL(value),
                0x1E => RR(value),
                0x26 => SHL(value),
                0x2E => SHR(value),
                0x36 => SwapNibble(value),
                0x3E => SRL(value),
                >= 0x80 and <= 0xBF => ResBit(value, (cbOpcode >> 3) & 0x07),
                _ => SetBitVal(value, (cbOpcode >> 3) & 0x07),
            };

            StepInternal(4); // between read/write
            memory.WriteByteToMemory(registers.HL, newValue);
            StepInternal(8); // remaining cycles
            return 0;
        }

        public int ExecuteInterrupt(int b)
        {
            if (Halted)
                Halted = false;

            if (IME)
            {
                PushWordToStack(registers.PC);
                registers.PC = (ushort)(0x40 + (8 * b));
                IME = false;
                memory.IF = SetBit(memory.IF, b, 0);
                return 20;
            }
            return 0;
        }

        private void Halt()
        {
            if (IME)
            {
                // Normal HALT: stop CPU until an interrupt fires
                Halted = true;
            }
            else
            {
                if ((memory.IE & memory.IF & 0x1F) == 0)
                {
                    // HALT with IME=false and no pending interrupt: halt and wait
                    Halted = true;
                }
                else
                {
                    // HALT bug: IE & IF != 0 but IME=false
                    HaltBug = true;
                }
            }
        }

        private void Stop()
        {
            // STOP is encoded as a two-byte instruction (0x10 0x00).
            registers.PC++;
        }
    }
}