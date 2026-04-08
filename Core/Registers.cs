// ============================================================================
// Project:     GameboyEmu
// File:        Core/Registers.cs
// Description: CPU register file definitions and 8-bit/16-bit pair accessors
//              for AF, BC, DE, HL, SP, and PC
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
    public sealed class Registers
    {
        public byte A;
        public byte B;
        public byte C;
        public byte D;
        public byte E;
        public byte H;
        public byte L;
        public uint PC;
        public uint SP;
        public readonly Flags Flags = new();

        public byte F
        {
            // Gets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Flags.ToByte();
            // Sets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Flags.FromByte(value, 0xF0);
        }

        public uint AF
        {
            // Gets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (uint)A << 8 | (uint)F;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                A = (byte)(value >> 8);
                F = (byte)(value & 0xFF);
            }
        }

        public uint BC
        {
            // Gets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (uint)B << 8 | C;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                B = (byte)(value >> 8);
                C = (byte)value;
            }
        }

        public uint DE
        {
            // Gets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (uint)D << 8 | E;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                D = (byte)(value >> 8);
                E = (byte)value;
            }
        }

        public uint HL
        {
            // Gets the value.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (uint)H << 8 | L;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                H = (byte)(value >> 8);
                L = (byte)value;
            }
        }
    }
}
