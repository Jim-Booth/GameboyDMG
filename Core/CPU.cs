// ============================================================================
// Project:     GameboyEmu
// File:        Core/CPU.cs
// Description: LR35902 CPU — full instruction set, CB prefixed opcodes,
//              interrupt handling, and HALT emulation
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

namespace GameboyEmu.Core
{
    public class CPU(MMU _memory, GameBoy _gameboy)
    {
        private readonly MMU memory = _memory;

        private GameBoy gameboy = _gameboy;

        public Registers registers = new();

        public bool Running { get; set; } = false;

        private bool IME;
        public bool EnableIME;

        private bool Halted;
        public bool IsHalted => Halted;
        private bool HaltBug;

        public void Reset()
        {
            registers = new Registers();
            Running = true;
            IME = false;
            EnableIME = false;
            Halted = false;
            HaltBug = false;
        }

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
                case 0x00:
                    // NOP
                    return 4;
                case 0x01:
                    if (registers.B == 72 && registers.C == 217)
                    { }
                    registers.BC = memory!.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x02:
                    memory!.WriteByteToMemory(registers.BC, registers.A);
                    return 8;
                case 0x03:
                    registers.BC++;
                    return 8;
                case 0x04:
                    registers.Flags.SetHalfCarryAdd((byte)(registers.B), 1);
                    registers.B++;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = false;
                    return 4;
                case 0x05:
                    registers.Flags.SetHalfCarrySub((byte)(registers.B), 1);
                    registers.B--;
                    registers.Flags.UpdateZeroFlag(registers.B);
                    registers.Flags.N = true;
                    return 4;
                case 0x06:
                    registers.B = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x07:
                    registers.A = ROTL(registers.A, true, false);
                    return 4;
                case 0x08:
                    memory!.WriteWordToMemory(memory.ReadWordFromMemory(registers.PC), registers.SP);
                    registers.PC += 2;
                    return 20;
                case 0x09:
                    ADDHL(registers.BC);
                    return 8;
                case 0x0A:
                    LDA(memory!.ReadByteFromMemory(registers.BC));
                    return 8;
                case 0x0B:
                    registers.BC--;
                    return 8;
                case 0x0C:
                    registers.C++;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.SetHalfCarryAdd((byte)(registers.C - 1), 1);
                    registers.Flags.N = false;
                    return 4;
                case 0x0D:
                    registers.C--;
                    registers.Flags.UpdateZeroFlag(registers.C);
                    registers.Flags.SetHalfCarrySub((byte)(registers.C + 1), 1);
                    registers.Flags.N = true;
                    return 4;
                case 0x0E:
                    registers.C = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x0F:
                    registers.A = ROTR(registers.A, true, false);
                    return 4;
                case 0x10:
                    Stop();
                    return 4;
                case 0x11:
                    registers.DE = memory!.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x12:
                    memory!.WriteByteToMemory(registers.DE, registers.A);
                    return 8;
                case 0x13:
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
                    registers.D = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x17:
                    registers.A = ROTL(registers.A, false, false);
                    return 4;
                case 0x18:
                    JRN((sbyte)(memory!.ReadByteFromMemory(registers.PC)));
                    return 12;
                case 0x19:
                    ADDHL(registers.DE);
                    return 8;
                case 0x1A:
                    LDA(memory!.ReadByteFromMemory(registers.DE));
                    return 8;
                case 0x1B:
                    registers.DE--;
                    return 8;
                case 0x1C:
                    registers.E++;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.SetHalfCarryAdd((byte)(registers.E - 1), 1);
                    registers.Flags.N = false;
                    return 4;
                case 0x1D:
                    registers.E--;
                    registers.Flags.UpdateZeroFlag(registers.E);
                    registers.Flags.SetHalfCarrySub((byte)(registers.E + 1), 1);
                    registers.Flags.N = true;
                    return 4;
                case 0x1E:
                    registers.E = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x1F:
                    registers.A = ROTR(registers.A, false, false);
                    return 4;
                case 0x20:
                    return JRZ((sbyte)memory!.ReadByteFromMemory(registers.PC), false);
                case 0x21:
                    registers.HL = memory!.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x22:
                    memory!.WriteByteToMemory(registers.HL, registers.A);
                    registers.HL++;
                    return 8;
                case 0x23:
                    registers.HL++;
                    return 8;
                case 0x24:
                    registers.H++;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = false;
                    registers.Flags.SetHalfCarryAdd((byte)(registers.H - 1), 1);
                    return 4;
                case 0x25:
                    registers.H--;
                    registers.Flags.UpdateZeroFlag(registers.H);
                    registers.Flags.N = true;
                    registers.Flags.SetHalfCarrySub((byte)(registers.H + 1), 1);
                    return 4;
                case 0x26:
                    registers.H = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x27:
                    DAA();
                    return 4;
                case 0x28:
                    return JRZ((sbyte)memory!.ReadByteFromMemory(registers.PC), true);
                case 0x29:
                    ADDHL(registers.HL);
                    return 8;
                case 0x2A:
                    LDA(registers.HL, 1);
                    return 8;
                case 0x2B:
                    registers.HL--;
                    return 8;
                case 0x2C:
                    registers.L++;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.SetHalfCarryAdd((byte)(registers.L - 1), 1);
                    registers.Flags.N = false;
                    return 4;
                case 0x2D:
                    registers.L--;
                    registers.Flags.UpdateZeroFlag(registers.L);
                    registers.Flags.SetHalfCarrySub((byte)(registers.L + 1), 1);
                    registers.Flags.N = true;
                    return 4;
                case 0x2E:
                    registers.L = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x2F:
                    registers.A = (byte)~registers.A;
                    registers.Flags.N = true;
                    registers.Flags.H = true;
                    return 4;
                case 0x30:
                    return JRC((sbyte)memory!.ReadByteFromMemory(registers.PC), false);
                case 0x31:
                    registers.SP = memory!.ReadWordFromMemory(registers.PC);
                    registers.PC += 2;
                    return 12;
                case 0x32:
                    memory!.WriteByteToMemory(registers.HL, registers.A);
                    registers.HL--;
                    return 8;
                case 0x33:
                    registers!.SP++;
                    return 8;
                case 0x34:
                    registers.Flags.SetHalfCarryAdd(memory!.ReadByteFromMemory(registers.HL), 1);
                    memory!.WriteByteToMemory(registers.HL, (byte)(memory!.ReadByteFromMemory(registers.HL) + 1));
                    registers.Flags.UpdateZeroFlag(memory!.ReadByteFromMemory(registers.HL));
                    registers.Flags.N = false;
                    return 12;
                case 0x35:
                    registers.Flags.SetHalfCarrySub(memory!.ReadByteFromMemory(registers.HL), 1);
                    memory!.WriteByteToMemory(registers.HL, (byte)(memory!.ReadByteFromMemory(registers.HL) - 1));
                    registers.Flags.UpdateZeroFlag(memory!.ReadByteFromMemory(registers.HL));
                    registers.Flags.N = true;
                    return 12;
                case 0x36:
                    memory!.WriteByteToMemory(registers.HL, (byte)(memory!.ReadByteFromMemory(registers.PC)));
                    registers.PC += 1;
                    return 12;
                case 0x37:
                    registers.Flags.C = true;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 4;
                case 0x38:
                    return JRC((sbyte)memory!.ReadByteFromMemory(registers.PC), true);
                case 0x39:
                    ADDHL(registers.SP);
                    return 8;
                case 0x3A:
                    LDAD(registers.HL, 1);
                    return 8;
                case 0x3B:
                    registers.SP--;
                    return 8;
                case 0x3C:
                    registers.A++;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.SetHalfCarryAdd((byte)(registers.A - 1), 1);
                    registers.Flags.N = false;
                    return 4;
                case 0x3D:
                    registers.A--;
                    registers.Flags.UpdateZeroFlag(registers.A);
                    registers.Flags.SetHalfCarrySub((byte)(registers.A + 1), 1);
                    registers.Flags.N = true;
                    return 4;
                case 0x3E:
                    registers.A = memory!.ReadByteFromMemory(registers.PC);
                    registers.PC += 1;
                    return 8;
                case 0x3F:
                    registers.Flags.C = !registers.Flags.C;
                    registers.Flags.N = false;
                    registers.Flags.H = false;
                    return 4;
                case 0x40:
                    LDB(registers.B);
                    return 4;
                case 0x41:
                    LDB(registers.C);
                    return 4;
                case 0x42:
                    LDB(registers.D);
                    return 4;
                case 0x43:
                    LDB(registers.E);
                    return 4;
                case 0x44:
                    LDB(registers.H);
                    return 4;
                case 0x45:
                    LDB(registers.L);
                    return 4;
                case 0x46:
                    LDB(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x47:
                    LDB(registers.A);
                    return 4;
                case 0x48:
                    LDC(registers.B);
                    return 4;
                case 0x49:
                    LDC(registers.C);
                    return 4;
                case 0x4A:
                    LDC(registers.D);
                    return 4;
                case 0x4B:
                    LDC(registers.E);
                    return 4;
                case 0x4C:
                    LDC(registers.H);
                    return 4;
                case 0x4D:
                    LDC(registers.L);
                    return 4;
                case 0x4E:
                    LDC(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x4F:
                    LDC(registers.A);
                    return 4;
                case 0x50:
                    LDD(registers.B);
                    return 4;
                case 0x51:
                    LDD(registers.C);
                    return 4;
                case 0x52:
                    LDD(registers.D);
                    return 4;
                case 0x53:
                    LDD(registers.E);
                    return 4;
                case 0x54:
                    LDD(registers.H);
                    return 4;
                case 0x55:
                    LDD(registers.L);
                    return 4;
                case 0x56:
                    LDD(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x57:
                    LDD(registers.A);
                    return 4;
                case 0x58:
                    LDE(registers.B);
                    return 4;
                case 0x59:
                    LDE(registers.C);
                    return 4;
                case 0x5A:
                    LDE(registers.D);
                    return 4;
                case 0x5B:
                    LDE(registers.E);
                    return 4;
                case 0x5C:
                    LDE(registers.H);
                    return 4;
                case 0x5D:
                    LDE(registers.L);
                    return 4;
                case 0x5E:
                    LDE(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x5F:
                    LDE(registers.A);
                    return 4;
                case 0x60:
                    LDH(registers.B);
                    return 4;
                case 0x61:
                    LDH(registers.C);
                    return 4;
                case 0x62:
                    LDH(registers.D);
                    return 4;
                case 0x63:
                    LDH(registers.E);
                    return 4;
                case 0x64:
                    LDH(registers.H);
                    return 4;
                case 0x65:
                    LDH(registers.L);
                    return 4;
                case 0x66:
                    LDH(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x67:
                    LDH(registers.A);
                    return 4;
                case 0x68:
                    LDL(registers.B);
                    return 4;
                case 0x69:
                    LDL(registers.C);
                    return 4;
                case 0x6A:
                    LDL(registers.D);
                    return 4;
                case 0x6B:
                    LDL(registers.E);
                    return 4;
                case 0x6C:
                    LDL(registers.H);
                    return 4;
                case 0x6D:
                    LDL(registers.L);
                    return 4;
                case 0x6E:
                    LDL(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x6F:
                    LDL(registers.A);
                    return 4;
                case 0x70:
                    LDHL(registers.B);
                    return 8;
                case 0x71:
                    LDHL(registers.C);
                    return 8;
                case 0x72:
                    LDHL(registers.D);
                    return 8;
                case 0x73:
                    LDHL(registers.E);
                    return 8;
                case 0x74:
                    LDHL(registers.H);
                    return 8;
                case 0x75:
                    LDHL(registers.L);
                    return 8;
                case 0x76:
                    Halt(); return 4;
                case 0x77:
                    LDHL(registers.A);
                    return 8;
                case 0x78:
                    LDA(registers.B);
                    return 4;
                case 0x79:
                    LDA(registers.C);
                    return 4;
                case 0x7A:
                    LDA(registers.D);
                    return 4;
                case 0x7B:
                    LDA(registers.E);
                    return 4;
                case 0x7C:
                    LDA(registers.H);
                    return 4;
                case 0x7D:
                    LDA(registers.L);
                    return 4;
                case 0x7E:
                    LDA(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x7F:
                    LDA(registers.A);
                    return 4;
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
                    ADD(memory!.ReadByteFromMemory(registers.HL));
                    return 8;
                case 0x87:
                    ADD(registers.A);
                    return 4;
                case 0x88:
                    ADD(registers.B, true);
                    return 4;
                case 0x89:
                    ADD(registers.C, true);
                    return 4;
                case 0x8A:
                    ADD(registers.D, true);
                    return 4;
                case 0x8B:
                    ADD(registers.E, true);
                    return 4;
                case 0x8C:
                    ADD(registers.H, true);
                    return 4;
                case 0x8D:
                    ADD(registers.L, true);
                    return 4;
                case 0x8E:
                    ADD(memory!.ReadByteFromMemory(registers.HL), true);
                    return 8;
                case 0x8F:
                    ADD(registers.A, true);
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
                    SUB(memory!.ReadByteFromMemory(registers.HL), false);
                    return 8;
                case 0x97:
                    SUB(registers.A);
                    return 4;
                case 0x98:
                    SUB(registers.B, true);
                    return 4;
                case 0x99:
                    SUB(registers.C, true);
                    return 4;
                case 0x9A:
                    SUB(registers.D, true);
                    return 4;
                case 0x9B:
                    SUB(registers.E, true);
                    return 4;
                case 0x9C:
                    SUB(registers.H, true);
                    return 4;
                case 0x9D:
                    SUB(registers.L, true);
                    return 4;
                case 0x9E:
                    SUB(memory!.ReadByteFromMemory(registers.HL), true);
                    return 8;
                case 0x9F:
                    SUB(registers.A, true);
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
                    AND(memory!.ReadByteFromMemory(registers.HL));
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
                    XOR(memory!.ReadByteFromMemory(registers.HL));
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
                    OR(memory!.ReadByteFromMemory(registers.HL));
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
                    CP(memory!.ReadByteFromMemory(registers.HL));
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
                    registers.BC = PopWordFromStack();
                    return 12;
                case 0xC2:
                    if (!registers.Flags.Z)
                    {
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xC3:
                    registers.PC = memory!.ReadWordFromMemory(registers.PC);
                    return 16;
                case 0xC4:
                    if (!registers.Flags.Z)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xC5:
                    PushWordToStack(registers.BC);
                    return 16;
                case 0xC6:
                    ADD(memory!.ReadByteFromMemory(registers.PC), false);
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
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xCB:
                    byte opcodeHi = memory!.ReadByteFromMemory(registers.PC++);

                    switch (opcodeHi)
                    {
                        case 0x00:
                            registers.B = ROTL(registers.B);
                            return 8;
                        case 0x01:
                            registers.C = ROTL(registers.C);

                            return 8;
                        case 0x02:
                            registers.D = ROTL(registers.D);

                            return 8;
                        case 0x03:
                            registers.E = ROTL(registers.E);

                            return 8;
                        case 0x04:
                            registers.H = ROTL(registers.H);

                            return 8;
                        case 0x05:
                            registers.L = ROTL(registers.L);

                            return 8;
                        case 0x06:
                            memory.WriteByteToMemory(registers.HL, ROTL(memory.ReadByteFromMemory(registers.HL)));

                            return 16;
                        case 0x07:
                            registers.A = ROTL(registers.A);

                            return 8;
                        case 0x08:
                            registers.B = ROTR(registers.B);

                            return 8;
                        case 0x09:
                            registers.C = ROTR(registers.C);

                            return 8;
                        case 0x0A:
                            registers.D = ROTR(registers.D);

                            return 8;
                        case 0x0B:
                            registers.E = ROTR(registers.E);

                            return 8;
                        case 0x0C:
                            registers.H = ROTR(registers.H);

                            return 8;
                        case 0x0D:
                            registers.L = ROTR(registers.L);

                            return 8;
                        case 0x0E:
                            memory.WriteByteToMemory(registers.HL, ROTR(memory.ReadByteFromMemory(registers.HL)));

                            return 16;
                        case 0x0F:
                            registers.A = ROTR(registers.A);

                            return 8;
                        case 0x10:
                            registers.B = ROTL(registers.B, false);

                            return 8;
                        case 0x11:
                            registers.C = ROTL(registers.C, false);

                            return 8;
                        case 0x12:
                            registers.D = ROTL(registers.D, false);

                            return 8;
                        case 0x13:
                            registers.E = ROTL(registers.E, false);

                            return 8;
                        case 0x14:
                            registers.H = ROTL(registers.H, false);

                            return 8;
                        case 0x15:
                            registers.L = ROTL(registers.L, false);

                            return 8;
                        case 0x16:
                            memory.WriteByteToMemory(registers.HL, ROTL(memory.ReadByteFromMemory(registers.HL), false));

                            return 16;
                        case 0x17:
                            registers.A = ROTL(registers.A, false);

                            return 8;
                        case 0x18:
                            registers.B = ROTR(registers.B, false);

                            return 8;
                        case 0x19:
                            registers.C = ROTR(registers.C, false);

                            return 8;
                        case 0x1A:
                            registers.D = ROTR(registers.D, false);

                            return 8;
                        case 0x1B:
                            registers.E = ROTR(registers.E, false);

                            return 8;
                        case 0x1C:
                            registers.H = ROTR(registers.H, false);

                            return 8;
                        case 0x1D:
                            registers.L = ROTR(registers.L, false);

                            return 8;
                        case 0x1E:
                            memory.WriteByteToMemory(registers.HL, ROTR(memory.ReadByteFromMemory(registers.HL), false));

                            return 16;
                        case 0x1F:
                            registers.A = ROTR(registers.A, false);

                            return 8;
                        case 0x20:
                            registers.B = SHL(registers.B);
                            return 8;
                        case 0x21:
                            registers.C = SHL(registers.C);
                            return 8;
                        case 0x22:
                            registers.D = SHL(registers.D);
                            return 8;
                        case 0x23:
                            registers.E = SHL(registers.E);
                            return 8;
                        case 0x24:
                            registers.H = SHL(registers.H);
                            return 8;
                        case 0x25:
                            registers.L = SHL(registers.L);
                            return 8;
                        case 0x26:
                            memory.WriteByteToMemory(registers.HL, SHL(memory.ReadByteFromMemory(registers.HL)));
                            return 16;
                        case 0x27:
                            registers.A = SHL(registers.A);
                            return 8;
                        case 0x28:
                            registers.B = SHR(registers.B);
                            return 8;
                        case 0x29:
                            registers.C = SHR(registers.C);
                            return 8;
                        case 0x2A:
                            registers.D = SHR(registers.D);
                            return 8;
                        case 0x2B:
                            registers.E = SHR(registers.E);
                            return 8;
                        case 0x2C:
                            registers.H = SHR(registers.H);
                            return 8;
                        case 0x2D:
                            registers.L = SHR(registers.L);
                            return 8;
                        case 0x2E:
                            memory.WriteByteToMemory(registers.HL, SHR(memory.ReadByteFromMemory(registers.HL)));
                            return 16;
                        case 0x2F:
                            registers.A = SHR(registers.A);
                            return 8;
                        case 0x30:
                            registers.B = SwapNibble(registers.B);
                            return 8;
                        case 0x31:
                            registers.C = SwapNibble(registers.C);
                            return 8;
                        case 0x32:
                            registers.D = SwapNibble(registers.D);
                            return 8;
                        case 0x33:
                            registers.E = SwapNibble(registers.E);
                            return 8;
                        case 0x34:
                            registers.H = SwapNibble(registers.H);
                            return 8;
                        case 0x35:
                            registers.L = SwapNibble(registers.L);
                            return 8;
                        case 0x36:
                            memory.WriteByteToMemory(registers.HL, SwapNibble(memory.ReadByteFromMemory(registers.HL)));
                            return 16;
                        case 0x37:
                            registers.A = SwapNibble(registers.A);
                            return 8;
                        case 0x38:
                            registers.B = SRL(registers.B);
                            return 8;
                        case 0x39:
                            registers.C = SRL(registers.C);
                            return 8;
                        case 0x3A:
                            registers.D = SRL(registers.D);
                            return 8;
                        case 0x3B:
                            registers.E = SRL(registers.E);
                            return 8;
                        case 0x3C:
                            registers.H = SRL(registers.H);
                            return 8;
                        case 0x3D:
                            registers.L = SRL(registers.L);
                            return 8;
                        case 0x3E:
                            memory.WriteByteToMemory(registers.HL, SRL(memory.ReadByteFromMemory(registers.HL)));
                            return 16;
                        case 0x3F:
                            registers.A = SRL(registers.A);
                            return 8;
                        case 0x40:
                            CompBit(registers.B, 0);
                            return 8;
                        case 0x41:
                            CompBit(registers.C, 0);
                            return 8;
                        case 0x42:
                            CompBit(registers.D, 0);
                            return 8;
                        case 0x43:
                            CompBit(registers.E, 0);
                            return 8;
                        case 0x44:
                            CompBit(registers.H, 0);
                            return 8;
                        case 0x45:
                            CompBit(registers.L, 0);
                            return 8;
                        case 0x46:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 0);
                            return 12;
                        case 0x47:
                            CompBit(registers.A, 0);
                            return 8;
                        case 0x48:
                            CompBit(registers.B, 1);
                            return 8;
                        case 0x49:
                            CompBit(registers.C, 1);
                            return 8;
                        case 0x4A:
                            CompBit(registers.D, 1);
                            return 8;
                        case 0x4B:
                            CompBit(registers.E, 1);
                            return 8;
                        case 0x4C:
                            CompBit(registers.H, 1);
                            return 8;
                        case 0x4D:
                            CompBit(registers.L, 1);
                            return 8;
                        case 0x4E:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 1);
                            return 12;
                        case 0x4F:
                            CompBit(registers.A, 1);
                            return 8;
                        case 0x50:
                            CompBit(registers.B, 2);
                            return 8;
                        case 0x51:
                            CompBit(registers.C, 2);
                            return 8;
                        case 0x52:
                            CompBit(registers.D, 2);
                            return 8;
                        case 0x53:
                            CompBit(registers.E, 2);
                            return 8;
                        case 0x54:
                            CompBit(registers.H, 2);
                            return 8;
                        case 0x55:
                            CompBit(registers.L, 2);
                            return 8;
                        case 0x56:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 2);
                            return 12;
                        case 0x57:
                            CompBit(registers.A, 2);
                            return 8;
                        case 0x58:
                            CompBit(registers.B, 3);
                            return 8;
                        case 0x59:
                            CompBit(registers.C, 3);
                            return 8;
                        case 0x5A:
                            CompBit(registers.D, 3);
                            return 8;
                        case 0x5B:
                            CompBit(registers.E, 3);
                            return 8;
                        case 0x5C:
                            CompBit(registers.H, 3);
                            return 8;
                        case 0x5D:
                            CompBit(registers.L, 3);
                            return 8;
                        case 0x5E:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 3);
                            return 12;
                        case 0x5F:
                            CompBit(registers.A, 3);
                            return 8;
                        case 0x60:
                            CompBit(registers.B, 4);
                            return 8;
                        case 0x61:
                            CompBit(registers.C, 4);
                            return 8;
                        case 0x62:
                            CompBit(registers.D, 4);
                            return 8;
                        case 0x63:
                            CompBit(registers.E, 4);
                            return 8;
                        case 0x64:
                            CompBit(registers.H, 4);
                            return 8;
                        case 0x65:
                            CompBit(registers.L, 4);
                            return 8;
                        case 0x66:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 4);
                            return 12;
                        case 0x67:
                            CompBit(registers.A, 4);
                            return 8;
                        case 0x68:
                            CompBit(registers.B, 5);
                            return 8;
                        case 0x69:
                            CompBit(registers.C, 5);
                            return 8;
                        case 0x6A:
                            CompBit(registers.D, 5);
                            return 8;
                        case 0x6B:
                            CompBit(registers.E, 5);
                            return 8;
                        case 0x6C:
                            CompBit(registers.H, 5);
                            return 8;
                        case 0x6D:
                            CompBit(registers.L, 5);
                            return 8;
                        case 0x6E:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 5);
                            return 12;
                        case 0x6F:
                            CompBit(registers.A, 5);
                            return 8;
                        case 0x70:
                            CompBit(registers.B, 6);
                            return 8;
                        case 0x71:
                            CompBit(registers.C, 6);
                            return 8;
                        case 0x72:
                            CompBit(registers.D, 6);
                            return 8;
                        case 0x73:
                            CompBit(registers.E, 6);
                            return 8;
                        case 0x74:
                            CompBit(registers.H, 6);
                            return 8;
                        case 0x75:
                            CompBit(registers.L, 6);
                            return 8;
                        case 0x76:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 6);
                            return 12;
                        case 0x77:
                            CompBit(registers.A, 6);
                            return 8;
                        case 0x78:
                            CompBit(registers.B, 7);
                            return 8;
                        case 0x79:
                            CompBit(registers.C, 7);
                            return 8;
                        case 0x7A:
                            CompBit(registers.D, 7);
                            return 8;
                        case 0x7B:
                            CompBit(registers.E, 7);
                            return 8;
                        case 0x7C:
                            CompBit(registers.H, 7);
                            return 8;
                        case 0x7D:
                            CompBit(registers.L, 7);
                            return 8;
                        case 0x7E:
                            CompBit(memory.ReadByteFromMemory(registers.HL), 7);
                            return 12;
                        case 0x7F:
                            CompBit(registers.A, 7);
                            return 8;
                        case 0x80:
                            registers.B = SetBit(registers.B, 0, 0);
                            return 8;
                        case 0x81:
                            registers.C = SetBit(registers.C, 0, 0);
                            return 8;
                        case 0x82:
                            registers.D = SetBit(registers.D, 0, 0);
                            return 8;
                        case 0x83:
                            registers.E = SetBit(registers.E, 0, 0);
                            return 8;
                        case 0x84:
                            registers.H = SetBit(registers.H, 0, 0);
                            return 8;
                        case 0x85:
                            registers.L = SetBit(registers.L, 0, 0);
                            return 8;
                        case 0x86:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 0, 0));
                            return 16;
                        case 0x87:
                            registers.A = SetBit(registers.A, 0, 0);
                            return 8;
                        case 0x88:
                            registers.B = SetBit(registers.B, 1, 0);
                            return 8;
                        case 0x89:
                            registers.C = SetBit(registers.C, 1, 0);
                            return 8;
                        case 0x8A:
                            registers.D = SetBit(registers.D, 1, 0);
                            return 8;
                        case 0x8B:
                            registers.E = SetBit(registers.E, 1, 0);
                            return 8;
                        case 0x8C:
                            registers.H = SetBit(registers.H, 1, 0);
                            return 8;
                        case 0x8D:
                            registers.L = SetBit(registers.L, 1, 0);
                            return 8;
                        case 0x8E:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 1, 0));
                            return 16;
                        case 0x8F:
                            registers.A = SetBit(registers.A, 1, 0);
                            return 8;
                        case 0x90:
                            registers.B = SetBit(registers.B, 2, 0);
                            return 8;
                        case 0x91:
                            registers.C = SetBit(registers.C, 2, 0);
                            return 8;
                        case 0x92:
                            registers.D = SetBit(registers.D, 2, 0);
                            return 8;
                        case 0x93:
                            registers.E = SetBit(registers.E, 2, 0);
                            return 8;
                        case 0x94:
                            registers.H = SetBit(registers.H, 2, 0);
                            return 8;
                        case 0x95:
                            registers.L = SetBit(registers.L, 2, 0);
                            return 8;
                        case 0x96:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 2, 0));
                            return 16;
                        case 0x97:
                            registers.A = SetBit(registers.A, 2, 0);
                            return 8;
                        case 0x98:
                            registers.B = SetBit(registers.B, 3, 0);
                            return 8;
                        case 0x99:
                            registers.C = SetBit(registers.C, 3, 0);
                            return 8;
                        case 0x9A:
                            registers.D = SetBit(registers.D, 3, 0);
                            return 8;
                        case 0x9B:
                            registers.E = SetBit(registers.E, 3, 0);
                            return 8;
                        case 0x9C:
                            registers.H = SetBit(registers.H, 3, 0);
                            return 8;
                        case 0x9D:
                            registers.L = SetBit(registers.L, 3, 0);
                            return 8;
                        case 0x9E:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 3, 0));
                            return 16;
                        case 0x9F:
                            registers.A = SetBit(registers.A, 3, 0);
                            return 8;
                        case 0xA0:
                            registers.B = SetBit(registers.B, 4, 0);
                            return 8;
                        case 0xA1:
                            registers.C = SetBit(registers.C, 4, 0);
                            return 8;
                        case 0xA2:
                            registers.D = SetBit(registers.D, 4, 0);
                            return 8;
                        case 0xA3:
                            registers.E = SetBit(registers.E, 4, 0);
                            return 8;
                        case 0xA4:
                            registers.H = SetBit(registers.H, 4, 0);
                            return 8;
                        case 0xA5:
                            registers.L = SetBit(registers.L, 4, 0);
                            return 8;
                        case 0xA6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 4, 0));
                            return 16;
                        case 0xA7:
                            registers.A = SetBit(registers.A, 4, 0);
                            return 8;
                        case 0xA8:
                            registers.B = SetBit(registers.B, 5, 0);
                            return 8;
                        case 0xA9:
                            registers.C = SetBit(registers.C, 5, 0);
                            return 8;
                        case 0xAA:
                            registers.D = SetBit(registers.D, 5, 0);
                            return 8;
                        case 0xAB:
                            registers.E = SetBit(registers.E, 5, 0);
                            return 8;
                        case 0xAC:
                            registers.H = SetBit(registers.H, 5, 0);
                            return 8;
                        case 0xAD:
                            registers.L = SetBit(registers.L, 5, 0);
                            return 8;
                        case 0xAE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 5, 0));
                            return 16;
                        case 0xAF:
                            registers.A = SetBit(registers.A, 5, 0);
                            return 8;
                        case 0xB0:
                            registers.B = SetBit(registers.B, 6, 0);
                            return 8;
                        case 0xB1:
                            registers.C = SetBit(registers.C, 6, 0);
                            return 8;
                        case 0xB2:
                            registers.D = SetBit(registers.D, 6, 0);
                            return 8;
                        case 0xB3:
                            registers.E = SetBit(registers.E, 6, 0);
                            return 8;
                        case 0xB4:
                            registers.H = SetBit(registers.H, 6, 0);
                            return 8;
                        case 0xB5:
                            registers.L = SetBit(registers.L, 6, 0);
                            return 8;
                        case 0xB6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 6, 0));
                            return 16;
                        case 0xB7:
                            registers.A = SetBit(registers.A, 6, 0);
                            return 8;
                        case 0xB8:
                            registers.B = SetBit(registers.B, 7, 0);
                            return 8;
                        case 0xB9:
                            registers.C = SetBit(registers.C, 7, 0);
                            return 8;
                        case 0xBA:
                            registers.D = SetBit(registers.D, 7, 0);
                            return 8;
                        case 0xBB:
                            registers.E = SetBit(registers.E, 7, 0);
                            return 8;
                        case 0xBC:
                            registers.H = SetBit(registers.H, 7, 0);
                            return 8;
                        case 0xBD:
                            registers.L = SetBit(registers.L, 7, 0);
                            return 8;
                        case 0xBE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 7, 0));
                            return 16;
                        case 0xBF:
                            registers.A = SetBit(registers.A, 7, 0);
                            return 8;
                        case 0xC0:
                            registers.B = SetBit(registers.B, 0, 1);
                            return 8;
                        case 0xC1:
                            registers.C = SetBit(registers.C, 0, 1);
                            return 8;
                        case 0xC2:
                            registers.D = SetBit(registers.D, 0, 1);
                            return 8;
                        case 0xC3:
                            registers.E = SetBit(registers.E, 0, 1);
                            return 8;
                        case 0xC4:
                            registers.H = SetBit(registers.H, 0, 1);
                            return 8;
                        case 0xC5:
                            registers.L = SetBit(registers.L, 0, 1);
                            return 8;
                        case 0xC6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 0, 1));
                            return 16;
                        case 0xC7:
                            registers.A = SetBit(registers.A, 0, 1);
                            return 8;
                        case 0xC8:
                            registers.B = SetBit(registers.B, 1, 1);
                            return 8;
                        case 0xC9:
                            registers.C = SetBit(registers.C, 1, 1);
                            return 8;
                        case 0xCA:
                            registers.D = SetBit(registers.D, 1, 1);
                            return 8;
                        case 0xCB:
                            registers.E = SetBit(registers.E, 1, 1);
                            return 8;
                        case 0xCC:
                            registers.H = SetBit(registers.H, 1, 1);
                            return 8;
                        case 0xCD:
                            registers.L = SetBit(registers.L, 1, 1);
                            return 8;
                        case 0xCE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 1, 1));
                            return 16;
                        case 0xCF:
                            registers.A = SetBit(registers.A, 1, 1);
                            return 8;
                        case 0xD0:
                            registers.B = SetBit(registers.B, 2, 1);
                            return 8;
                        case 0xD1:
                            registers.C = SetBit(registers.C, 2, 1);
                            return 8;
                        case 0xD2:
                            registers.D = SetBit(registers.D, 2, 1);
                            return 8;
                        case 0xD3:
                            registers.E = SetBit(registers.E, 2, 1);
                            return 8;
                        case 0xD4:
                            registers.H = SetBit(registers.H, 2, 1);
                            return 8;
                        case 0xD5:
                            registers.L = SetBit(registers.L, 2, 1);
                            return 8;
                        case 0xD6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 2, 1));
                            return 16;
                        case 0xD7:
                            registers.A = SetBit(registers.A, 2, 1);
                            return 8;
                        case 0xD8:
                            registers.B = SetBit(registers.B, 3, 1);
                            return 8;
                        case 0xD9:
                            registers.C = SetBit(registers.C, 3, 1);
                            return 8;
                        case 0xDA:
                            registers.D = SetBit(registers.D, 3, 1);
                            return 8;
                        case 0xDB:
                            registers.E = SetBit(registers.E, 3, 1);
                            return 8;
                        case 0xDC:
                            registers.H = SetBit(registers.H, 3, 1);
                            return 8;
                        case 0xDD:
                            registers.L = SetBit(registers.L, 3, 1);
                            return 8;
                        case 0xDE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 3, 1));
                            return 16;
                        case 0xDF:
                            registers.A = SetBit(registers.A, 3, 1);
                            return 8;
                        case 0xE0:
                            registers.B = SetBit(registers.B, 4, 1);
                            return 8;
                        case 0xE1:
                            registers.C = SetBit(registers.C, 4, 1);
                            return 8;
                        case 0xE2:
                            registers.D = SetBit(registers.D, 4, 1);
                            return 8;
                        case 0xE3:
                            registers.E = SetBit(registers.E, 4, 1);
                            return 8;
                        case 0xE4:
                            registers.H = SetBit(registers.H, 4, 1);
                            return 8;
                        case 0xE5:
                            registers.L = SetBit(registers.L, 4, 1);
                            return 8;
                        case 0xE6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 4, 1));
                            return 16;
                        case 0xE7:
                            registers.A = SetBit(registers.A, 4, 1);
                            return 8;
                        case 0xE8:
                            registers.B = SetBit(registers.B, 5, 1);
                            return 8;
                        case 0xE9:
                            registers.C = SetBit(registers.C, 5, 1);
                            return 8;
                        case 0xEA:
                            registers.D = SetBit(registers.D, 5, 1);
                            return 8;
                        case 0xEB:
                            registers.E = SetBit(registers.E, 5, 1);
                            return 8;
                        case 0xEC:
                            registers.H = SetBit(registers.H, 5, 1);
                            return 8;
                        case 0xED:
                            registers.L = SetBit(registers.L, 5, 1);
                            return 8;
                        case 0xEE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 5, 1));
                            return 16;
                        case 0xEF:
                            registers.A = SetBit(registers.A, 5, 1);
                            return 8;
                        case 0xF0:
                            registers.B = SetBit(registers.B, 6, 1);
                            return 8;
                        case 0xF1:
                            registers.C = SetBit(registers.C, 6, 1);
                            return 8;
                        case 0xF2:
                            registers.D = SetBit(registers.D, 6, 1);
                            return 8;
                        case 0xF3:
                            registers.E = SetBit(registers.E, 6, 1);
                            return 8;
                        case 0xF4:
                            registers.H = SetBit(registers.H, 6, 1);
                            return 8;
                        case 0xF5:
                            registers.L = SetBit(registers.L, 6, 1);
                            return 8;
                        case 0xF6:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 6, 1));
                            return 16;
                        case 0xF7:
                            registers.A = SetBit(registers.A, 6, 1);
                            return 8;
                        case 0xF8:
                            registers.B = SetBit(registers.B, 7, 1);
                            return 8;
                        case 0xF9:
                            registers.C = SetBit(registers.C, 7, 1);
                            return 8;
                        case 0xFA:
                            registers.D = SetBit(registers.D, 7, 1);
                            return 8;
                        case 0xFB:
                            registers.E = SetBit(registers.E, 7, 1);
                            return 8;
                        case 0xFC:
                            registers.H = SetBit(registers.H, 7, 1);
                            return 8;
                        case 0xFD:
                            registers.L = SetBit(registers.L, 7, 1);
                            return 8;
                        case 0xFE:
                            memory.WriteByteToMemory(registers.HL, SetBit(memory.ReadByteFromMemory(registers.HL), 7, 1));
                            return 16;
                        case 0xFF:
                            registers.A = SetBit(registers.A, 7, 1);
                            return 8;
                    }
                case 0xCC:
                    if (registers.Flags.Z)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xCD:
                    PushWordToStack(registers.PC + 2);
                    registers.PC = memory!.ReadWordFromMemory(registers.PC);
                    return 24;
                case 0xCE:
                    ADD(memory!.ReadByteFromMemory(registers.PC), true);
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
                    registers.DE = PopWordFromStack();
                    return 12;
                case 0xD2:
                    if (!registers.Flags.C)
                    {
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                //case 0xD3:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xD4:
                    if (!registers.Flags.C)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                case 0xD5:
                    PushWordToStack(registers.DE);
                    return 16;
                case 0xD6:
                    SUB(memory!.ReadByteFromMemory(registers.PC), false);
                    registers.PC += 1;
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
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 16;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                //case 0xDB:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xDC:
                    if (registers.Flags.C)
                    {
                        PushWordToStack(registers.PC + 2);
                        registers.PC = memory!.ReadWordFromMemory(registers.PC);
                        return 24;
                    }
                    else
                        registers.PC += 2;
                    return 12;
                //case 0xDD:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xDE:
                    SUB(memory!.ReadByteFromMemory(registers.PC), true);
                    registers.PC++;
                    return 8;
                case 0xDF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x18;
                    return 16;
                case 0xE0:
                    memory!.WriteByteToMemory((uint)(memory!.ReadByteFromMemory(registers.PC) + 0xFF00), registers.A);
                    registers.PC += 1;
                    return 12;
                case 0xE1:
                    registers.HL = PopWordFromStack();
                    return 12;
                case 0xE2:
                    memory!.WriteByteToMemory((uint)(registers.C + 0xFF00), registers.A);
                    return 8;
                //case 0xE3:
                // NOT IMPLEMENTED
                //    return 0;
                //case 0xE4:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xE5:
                    PushWordToStack(registers.HL);
                    return 16;
                case 0xE6:
                    AND(memory!.ReadByteFromMemory(registers.PC));
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
                    memory!.WriteByteToMemory(memory!.ReadWordFromMemory(registers.PC), registers.A);
                    registers.PC += 2;
                    return 16;
                //case 0xEB:
                // NOT IMPLEMENTED
                //    return 0;
                //case 0xEC:
                // NOT IMPLEMENTED
                //    return 0;
                //case 0xED:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xEE:
                    XOR(memory!.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xEF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x28;
                    return 16;
                case 0xF0:
                    registers.A = memory!.ReadByteFromMemory((uint)(0xFF00 + memory!.ReadByteFromMemory(registers.PC)));
                    registers.PC += 1;
                    return 12;
                case 0xF1:
                    registers.AF = PopWordFromStack();
                    return 12;
                case 0xF2:
                    registers.A = memory!.ReadByteFromMemory((uint)(0xFF00 + registers.C));
                    return 8;
                case 0xF3:
                    IME = false;
                    return 4;
                //case 0xF4:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xF5:
                    PushWordToStack(registers.AF);
                    return 16;
                case 0xF6:
                    OR(memory!.ReadByteFromMemory(registers.PC));
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
                    registers.A = memory!.ReadByteFromMemory(memory!.ReadWordFromMemory(registers.PC));
                    registers.PC += 2;
                    return 16;
                case 0xFB:
                    EnableIME = true;
                    return 4;
                //case 0xFC:
                // NOT IMPLEMENTED
                //    return 0;
                //case 0xFD:
                // NOT IMPLEMENTED
                //    return 0;
                case 0xFE:
                    CP(memory!.ReadByteFromMemory(registers.PC));
                    registers.PC += 1;
                    return 8;
                case 0xFF:
                    PushWordToStack(registers.PC);
                    registers.PC = 0x38;
                    return 16;
            }
            return 0;
        }

        private void ADD(byte source8bitRegister, bool withCarry = false)
        {
            int value;
            if (withCarry)
            {
                value = registers.A + source8bitRegister + (registers.Flags.C ? 1 : 0);
                registers.Flags.SetHalfCarryAdd(registers.A, source8bitRegister, true);
            }
            else
            {
                value = registers.A + source8bitRegister;
                registers.Flags.SetHalfCarryAdd(registers.A, source8bitRegister);
            }
            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.UpdateCarryFlag(value);
            registers.Flags.N = false;
            registers.A = (byte)(value & 0xFF);
        }

        private void ADDHL(uint source16bitRegister, bool clearZero = false)
        {
            uint value = registers.HL + source16bitRegister;
            uint mask = 0b_0000_1111_1111_1111;
            registers.Flags.H = ((registers.HL & mask) + (source16bitRegister & mask)) > mask;
            registers.Flags.C = value >> 16 != 0;
            registers.Flags.N = false;
            registers.HL = value;
            if (clearZero)
                registers.Flags.Z = false;
        }

        private uint ADDR8(uint value)
        {
            byte sb = memory!.ReadByteFromMemory(registers.PC++);
            registers.Flags.SetHalfCarryAdd((byte)value, sb);
            registers.Flags.UpdateCarryFlag((byte)value + sb);
            registers.Flags.N = false;
            registers.Flags.Z = false;
            return (uint)((sbyte)sb + value);
        }

        private void SUB(byte source8bitRegister, bool withCarry = false)
        {
            int value;
            if (withCarry)
            {
                value = registers.A - source8bitRegister - (byte)(registers.Flags.C ? 1 : 0);
                registers.Flags.SetHalfCarrySub(registers.A, source8bitRegister, true);
            }
            else
            {
                value = registers.A - source8bitRegister;
                registers.Flags.SetHalfCarrySub(registers.A, source8bitRegister);
            }

            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.UpdateCarryFlag(value);
            registers.Flags.N = true;
            registers.A = (byte)(value & 0xFF);
        }

        private void AND(byte source8bitRegister)
        {
            byte value = (byte)(registers.A & source8bitRegister);
            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.FromByte(0x20, 0x70);
            registers.A = value;
        }


        private void XOR(byte source8bitRegister)
        {
            byte value = (byte)(registers.A ^ source8bitRegister);
            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.FromByte(0x00, 0x70);
            registers.A = value;
        }

        private void OR(byte source8bitRegister)
        {
            byte value = (byte)(registers.A | source8bitRegister);
            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.FromByte(0x00, 0x70);
            registers.A = value;
        }

        private void CP(byte source8bitRegister)
        {
            int value = registers.A - source8bitRegister;
            registers.Flags.UpdateZeroFlag(value);
            registers.Flags.N = true;
            registers.Flags.SetHalfCarrySub(registers.A, source8bitRegister);
            registers.Flags.UpdateCarryFlag(value);
        }

        private void LDB(byte register)
        {
            registers.B = register;
        }

        private void LDC(byte register)
        {
            registers.C = register;
        }

        private void LDD(byte register)
        {
            registers.D = register;
        }

        private void LDE(byte register)
        {
            registers.E = register;
        }

        private void LDH(byte register)
        {
            registers.H = register;
        }

        private void LDL(byte register)
        {
            registers.L = register;
        }

        private void LDHL(byte register, uint incHL = 0)
        {
            memory!.WriteByteToMemory(registers.HL, register);
            registers.HL += incHL;
        }

        private void LDBC(byte register)
        {
            memory!.WriteByteToMemory(registers.BC, register);
        }

        private void LDDE(byte register)
        {
            memory!.WriteByteToMemory(registers.DE, register);
        }

        private void LDA(byte register)
        {
            registers.A = register;
        }

        private void LDA(uint register, uint incHL = 0)
        {
            registers.A = memory!.ReadByteFromMemory(register);
            registers.HL += incHL;
        }

        private void LDAD(uint register, uint decHL = 0)
        {
            registers.A = memory!.ReadByteFromMemory(register);
            registers.HL = registers.HL - decHL;
        }

        private byte ROTL(byte value, bool rlc = true, bool checkZeroFlag = true)
        {
            int bit7 = (value & (1 << 7)) >> 7;
            value = (byte)(value << 1);

            if (rlc)
                value = SetBit(value, 0, bit7);
            else
                value = SetBit(value, 0, (int)(registers.Flags.C ? 1 : 0));

            registers.Flags.C = bit7 == 1;

            if (checkZeroFlag)
                registers.Flags.UpdateZeroFlag(value);
            else
                registers.Flags.Z = false;
            registers.Flags.H = false;
            registers.Flags.N = false;
            return value;
        }

        private byte ROTR(byte value, bool rrc = true, bool checkZeroFlag = true)
        {
            byte result;
            if (!rrc)
                result = (byte)((value >> 1) | ((registers.Flags.C ? 1 : 0) << 7));
            else
                result = (byte)((value >> 1) | (value << 7));
            if (checkZeroFlag)
                registers.Flags.UpdateZeroFlag(result);
            else
                registers.Flags.Z = false;
            registers.Flags.H = false;
            registers.Flags.N = false;
            registers.Flags.C = (value & 1) == 1;
            return result;
        }

        private byte SHL(byte value)
        {
            byte value2 = (byte)(value << 1);

            registers.Flags.C = (value & 0x80) != 0; ;
            registers.Flags.UpdateZeroFlag(value2);
            registers.Flags.H = false;
            registers.Flags.N = false;
            return value2;
        }

        private byte SHR(byte value)
        {
            byte value2 = (byte)((value >> 1) | (value & 0x80));
            registers.Flags.C = (value & 0x1) != 0;
            registers.Flags.UpdateZeroFlag(value2);
            registers.Flags.H = false;
            registers.Flags.N = false;
            return value2;
        }

        private byte SRL(byte value)
        {
            byte result = (byte)(value >> 1);
            registers.Flags.C = (value & 0x01) != 0;
            registers.Flags.Z = (result == 0);
            registers.Flags.H = false;
            registers.Flags.N = false;
            return result;
        }
        private void JRN(sbyte value)
        {
            registers.PC = (uint)(registers.PC + value);
            registers.PC += 1;
        }

        private int JRZ(sbyte value, bool state = true)
        {
            if (registers.Flags.Z == state)
            {
                registers.PC = (uint)((registers.PC) + value);
                registers.PC += 1;
                return 12;
            }
            registers.PC += 1;
            return 8;
        }

        private int JRC(sbyte value, bool state)
        {
            if (registers.Flags.C == state)
            {
                registers.PC = (ushort)((registers.PC) + value);
                registers.PC += 1;
                return 12;
            }
            registers.PC += 1;
            return 8;
        }



        private byte SwapNibble(byte register)
        {
            byte loNibble = (byte)(register & 0x0F);
            byte hiNibble = (byte)((register & 0xF0) >> 4);
            register = (byte)(loNibble << 4 | hiNibble);
            registers.Flags.UpdateZeroFlag(register);
            registers.Flags.H = false;
            registers.Flags.N = false;
            registers.Flags.C = false;
            return register;
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

        private void CompBit(int register, int bitIndex)
        {
            int bit = (register >> bitIndex) & 1;
            registers.Flags.Z = bit == 0;
            registers.Flags.H = true;
            registers.Flags.N = false;
        }

        private byte SetBit(int register, int bitIndex, int newBitValue)
        {
            if (newBitValue == 1)
                register |= (1 << bitIndex);
            else
                register &= ~(1 << bitIndex);
            return (byte)register;
        }

        public void PushWordToStack(uint value)
        {
            registers.SP -= 2;
            memory!.WriteWordToMemory(registers.SP, value);
        }

        public uint PopWordFromStack()
        {
            uint value = memory!.ReadWordFromMemory(registers.SP);
            registers.SP += 2;
            return value;
        }

        public void UpdateIME()
        {
            IME |= EnableIME;
            EnableIME = false;
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
                memory!.IF = SetBit(memory!.IF, b, 0);
                return 20; // Interrupt dispatch costs 5 M-cycles (20 T-cycles)
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
                if ((memory!.IE & memory!.IF & 0x1F) == 0)
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
            // throw new NotImplementedException();
        }
    }
}