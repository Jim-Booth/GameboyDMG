// ============================================================================
// Project:     GameboyEmu
// File:        Core/Flags.cs
// Description: CPU flag register helpers (Z, N, H, C) for pack/unpack and
//              arithmetic flag update semantics
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
    public sealed class Flags
    {
        public bool Z;
        public bool N;
        public bool H;
        public bool C;

        // Executes to byte.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ToByte()
        {
            return (byte)(
                (Z ? 0x80 : 0) |
                (N ? 0x40 : 0) |
                (H ? 0x20 : 0) |
                (C ? 0x10 : 0));
        }

        // Executes from byte.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FromByte(byte bits, byte mask)
        {
            if ((mask & 0x80) != 0) Z = (bits & 0x80) != 0;
            if ((mask & 0x40) != 0) N = (bits & 0x40) != 0;
            if ((mask & 0x20) != 0) H = (bits & 0x20) != 0;
            if ((mask & 0x10) != 0) C = (bits & 0x10) != 0;
        }

        // Executes update carry flag.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateCarryFlag(int value)
        {
            C = (value >> 8) != 0;
        }

        // Executes update zero flag.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UpdateZeroFlag(int value)
        {
            Z = (byte)value == 0;
        }

        // Executes set half carry add.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetHalfCarryAdd(byte a, byte b)
        {
            H = ((a & 0xF) + (b & 0xF)) > 0xF;
        }

        // Executes set half carry sub.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetHalfCarrySub(byte a, byte b)
        {
            H = (a & 0xF) < (b & 0xF);
        }
    }
}
