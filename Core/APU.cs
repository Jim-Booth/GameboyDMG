// ============================================================================
// Project:     GameboyEmu
// File:        Core/APU.cs
// Description: Audio Processing Unit — 4 sound channels, frame sequencer,
//              stereo mixing, and SDL audio output
//              Optimised: sealed class, AggressiveInlining on channel hot paths,
//              flat duty cycle table replacing jagged array
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#nullable enable

namespace GameboyEmu.Core
{
    /// <summary>
    /// Game Boy Audio Processing Unit.
    /// Emulates all four sound channels, the frame sequencer, and mixing/panning.
    /// Outputs stereo PCM via SDL audio queue.
    ///
    /// Channel 1 – Square wave with frequency sweep  (NR10–NR14, 0xFF10–0xFF14)
    /// Channel 2 – Square wave                       (NR21–NR24, 0xFF16–0xFF19)
    /// Channel 3 – Programmable wave                  (NR30–NR34, 0xFF1A–0xFF1E + wave RAM 0xFF30–0xFF3F)
    /// Channel 4 – Noise (LFSR)                       (NR41–NR44, 0xFF20–0xFF23)
    /// Master     – NR50 (0xFF24), NR51 (0xFF25), NR52 (0xFF26)
    /// </summary>
    public sealed class APU : IDisposable
    {
        // ----- Constants -----
        private const int SampleRate = 44100;
        private const int CPUClockRate = 4194304;
        private const int FrameSequencerPeriod = 8192; // CPU clocks between frame sequencer steps (512 Hz)
        private const int AudioBufferSamples = 1024;

        // ----- Frame sequencer -----
        private int _fsTimer;
        private int _fsStep;

        // ----- Down-sampler -----
        private double _sampleTimer;
        private readonly double _samplePeriod;
        private readonly float[] _sampleBuf = new float[AudioBufferSamples * 2]; // interleaved L R
        private int _sampleIdx;

        // ----- Master registers -----
        private bool _powered;     // NR52 bit 7
        private byte _nr50;        // 0xFF24 – master volume / VIN routing
        private byte _nr51;        // 0xFF25 – channel panning

        // ----- Channels -----
        private readonly SquareSweepChannel _ch1;
        private readonly SquareChannel _ch2;
        private readonly WaveChannel _ch3;
        private readonly NoiseChannel _ch4;

        // ----- SDL audio device -----
        private uint _audioDevice;
        private bool _audioReady;

        // =====================================================================
        //  Construction / Init
        // =====================================================================

        public APU()
        {
            _ch1 = new SquareSweepChannel(() => _fsStep);
            _ch2 = new SquareChannel(() => _fsStep);
            _ch3 = new WaveChannel(() => _fsStep);
            _ch4 = new NoiseChannel(() => _fsStep);

            _samplePeriod = (double)CPUClockRate / SampleRate;
            _fsTimer = FrameSequencerPeriod;
            _powered = true;
            _nr50 = 0x77;
            _nr51 = 0xF3;
        }

        /// <summary>
        /// Opens the SDL audio device. Call once after SDL_Init(SDL_INIT_AUDIO).
        /// </summary>
        public void InitAudio()
        {
            if (SDL.SDL_InitSubSystem(SDL.SDL_INIT_AUDIO) < 0)
            {
                Console.WriteLine($"[APU] SDL audio init failed: {Marshal.PtrToStringAnsi(SDL.SDL_GetError())}");
                return;
            }

            var desired = new SDL.SDL_AudioSpec
            {
                freq = SampleRate,
                format = SDL.AUDIO_F32SYS,
                channels = 2,
                samples = AudioBufferSamples,
                callback = IntPtr.Zero,
                userdata = IntPtr.Zero,
            };

            _audioDevice = SDL.SDL_OpenAudioDevice(IntPtr.Zero, 0, ref desired, out _, 0);
            if (_audioDevice == 0)
            {
                Console.WriteLine($"[APU] Failed to open audio device: {Marshal.PtrToStringAnsi(SDL.SDL_GetError())}");
                return;
            }

            SDL.SDL_PauseAudioDevice(_audioDevice, 0); // unpause
            _audioReady = true;
            Console.WriteLine("[APU] Audio initialised – 44100 Hz stereo float32");
        }

        // =====================================================================
        //  Reset – restore APU to power-on state (keeps audio device open)
        // =====================================================================

        public void Reset()
        {
            _fsTimer = FrameSequencerPeriod;
            _fsStep = 0;
            _sampleTimer = 0;
            _sampleIdx = 0;
            Array.Clear(_sampleBuf, 0, _sampleBuf.Length);
            _powered = true;
            _nr50 = 0x77;
            _nr51 = 0xF3;
            _ch1.Reset();
            _ch2.Reset();
            _ch3.Reset();
            _ch4.Reset();

            // Clear any queued audio samples
            if (_audioReady)
                SDL.SDL_ClearQueuedAudio(_audioDevice);
        }

        // =====================================================================
        //  Tick – call once per CPU instruction with the cycle count
        // =====================================================================

        public void Tick(int cpuCycles)
        {
            if (!_powered) return;

            for (int i = 0; i < cpuCycles; i++)
            {
                // --- Frame sequencer ---
                _fsTimer--;
                if (_fsTimer <= 0)
                {
                    _fsTimer = FrameSequencerPeriod;
                    StepFrameSequencer();
                }

                // --- Channel timers ---
                _ch1.TickTimer();
                _ch2.TickTimer();
                _ch3.TickTimer();
                _ch4.TickTimer();

                // --- Down-sample to 44100 Hz ---
                _sampleTimer++;
                if (_sampleTimer >= _samplePeriod)
                {
                    _sampleTimer -= _samplePeriod;
                    MixSample();
                }
            }
        }

        // =====================================================================
        //  Frame sequencer (512 Hz, 8 steps)
        // =====================================================================

        private void StepFrameSequencer()
        {
            switch (_fsStep)
            {
                case 0: ClockLengthCounters(); break;
                case 2: ClockLengthCounters(); _ch1.ClockSweep(); break;
                case 4: ClockLengthCounters(); break;
                case 6: ClockLengthCounters(); _ch1.ClockSweep(); break;
                case 7: _ch1.ClockEnvelope(); _ch2.ClockEnvelope(); _ch4.ClockEnvelope(); break;
            }
            _fsStep = (_fsStep + 1) & 7;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClockLengthCounters()
        {
            _ch1.ClockLength();
            _ch2.ClockLength();
            _ch3.ClockLength();
            _ch4.ClockLength();
        }

        // =====================================================================
        //  Mixing & audio output
        // =====================================================================

        private void MixSample()
        {
            float s1 = _ch1.GetOutput();
            float s2 = _ch2.GetOutput();
            float s3 = _ch3.GetOutput();
            float s4 = _ch4.GetOutput();

            // Panning (NR51)
            float left = 0, right = 0;
            if ((_nr51 & 0x10) != 0) left += s1;
            if ((_nr51 & 0x20) != 0) left += s2;
            if ((_nr51 & 0x40) != 0) left += s3;
            if ((_nr51 & 0x80) != 0) left += s4;

            if ((_nr51 & 0x01) != 0) right += s1;
            if ((_nr51 & 0x02) != 0) right += s2;
            if ((_nr51 & 0x04) != 0) right += s3;
            if ((_nr51 & 0x08) != 0) right += s4;

            // Master volume (NR50)  L = bits 4-6, R = bits 0-2, each 0-7 → multiply by vol+1
            int volL = ((_nr50 >> 4) & 7) + 1;
            int volR = (_nr50 & 7) + 1;
            left *= volL;
            right *= volR;

            // Normalise to –1..1.  Max raw value = 15 (per channel) × 4 channels × 8 volume = 480
            const float norm = 1.0f / 480.0f;
            left *= norm * 0.5f;   // 0.5 comfort scaling
            right *= norm * 0.5f;

            _sampleBuf[_sampleIdx++] = left;
            _sampleBuf[_sampleIdx++] = right;

            if (_sampleIdx >= _sampleBuf.Length)
            {
                FlushAudioBuffer();
                _sampleIdx = 0;
            }
        }

        private void FlushAudioBuffer()
        {
            if (!_audioReady) return;

            // Back-pressure: wait if too much audio is queued (max ~⅛ second)
            uint maxQueued = (uint)(SampleRate * 2 * sizeof(float) / 8);
            while (SDL.SDL_GetQueuedAudioSize(_audioDevice) > maxQueued)
                System.Threading.Thread.Sleep(1);

            GCHandle pin = GCHandle.Alloc(_sampleBuf, GCHandleType.Pinned);
            try
            {
                SDL.SDL_QueueAudio(_audioDevice, pin.AddrOfPinnedObject(),
                    (uint)(_sampleIdx * sizeof(float)));
            }
            finally { pin.Free(); }
        }

        // =====================================================================
        //  Register I/O  (called from MMU)
        // =====================================================================

        public byte ReadRegister(uint addr)
        {
            return addr switch
            {
                // Channel 1
                0xFF10 => (byte)(_ch1.NR10 | 0x80),
                0xFF11 => (byte)(_ch1.NR11 | 0x3F),
                0xFF12 => _ch1.NR12,
                0xFF13 => 0xFF,                              // write-only
                0xFF14 => (byte)(_ch1.NR14 | 0xBF),

                // Channel 2
                0xFF16 => (byte)(_ch2.NR21 | 0x3F),
                0xFF17 => _ch2.NR22,
                0xFF18 => 0xFF,
                0xFF19 => (byte)(_ch2.NR24 | 0xBF),

                // Channel 3
                0xFF1A => (byte)(_ch3.NR30 | 0x7F),
                0xFF1B => 0xFF,
                0xFF1C => (byte)(_ch3.NR32 | 0x9F),
                0xFF1D => 0xFF,
                0xFF1E => (byte)(_ch3.NR34 | 0xBF),

                // Channel 4
                0xFF20 => 0xFF,
                0xFF21 => _ch4.NR42,
                0xFF22 => _ch4.NR43,
                0xFF23 => (byte)(_ch4.NR44 | 0xBF),

                // Master
                0xFF24 => _nr50,
                0xFF25 => _nr51,
                0xFF26 => BuildNR52(),

                // Wave RAM
                >= 0xFF30 and <= 0xFF3F => _ch3.ReadWaveRAM(addr),

                _ => 0xFF,
            };
        }

        public void WriteRegister(uint addr, byte value)
        {
            // NR52 power control is always writable
            if (addr == 0xFF26)
            {
                bool next = (value & 0x80) != 0;
                if (!next && _powered) PowerOff();
                else if (next && !_powered)
                {
                    // DMG behavior: powering on resets frame sequencer timing state.
                    _fsStep = 0;
                    _fsTimer = FrameSequencerPeriod;
                }
                _powered = next;
                return;
            }

            // Wave RAM is always writable
            if (addr is >= 0xFF30 and <= 0xFF3F) { _ch3.WriteWaveRAM(addr, value); return; }

            // While powered off, DMG still accepts writes to length-load registers.
            if (!_powered)
            {
                switch (addr)
                {
                    case 0xFF11: _ch1.LoadLength(value); return;
                    case 0xFF16: _ch2.LoadLength(value); return;
                    case 0xFF1B: _ch3.LoadLength(value); return;
                    case 0xFF20: _ch4.LoadLength(value); return;
                    default: return;
                }
            }

            switch (addr)
            {
                // Channel 1
                case 0xFF10: _ch1.WriteNR10(value); break;
                case 0xFF11: _ch1.WriteNR11(value); break;
                case 0xFF12: _ch1.WriteNR12(value); break;
                case 0xFF13: _ch1.WriteNR13(value); break;
                case 0xFF14: _ch1.WriteNR14(value); break;

                // Channel 2
                case 0xFF16: _ch2.WriteNR21(value); break;
                case 0xFF17: _ch2.WriteNR22(value); break;
                case 0xFF18: _ch2.WriteNR23(value); break;
                case 0xFF19: _ch2.WriteNR24(value); break;

                // Channel 3
                case 0xFF1A: _ch3.WriteNR30(value); break;
                case 0xFF1B: _ch3.WriteNR31(value); break;
                case 0xFF1C: _ch3.WriteNR32(value); break;
                case 0xFF1D: _ch3.WriteNR33(value); break;
                case 0xFF1E: _ch3.WriteNR34(value); break;

                // Channel 4
                case 0xFF20: _ch4.WriteNR41(value); break;
                case 0xFF21: _ch4.WriteNR42(value); break;
                case 0xFF22: _ch4.WriteNR43(value); break;
                case 0xFF23: _ch4.WriteNR44(value); break;

                // Master
                case 0xFF24: _nr50 = value; break;
                case 0xFF25: _nr51 = value; break;
            }
        }

        private byte BuildNR52()
        {
            byte v = (byte)(_powered ? 0x80 : 0x00);
            if (_ch1.Enabled) v |= 0x01;
            if (_ch2.Enabled) v |= 0x02;
            if (_ch3.Enabled) v |= 0x04;
            if (_ch4.Enabled) v |= 0x08;
            return (byte)(v | 0x70);  // bits 4-6 always read 1
        }

        private void PowerOff()
        {
            // DMG power-off behavior differs from a full reset.
            _ch1.PowerOffDmg();
            _ch2.PowerOffDmg();
            _ch3.PowerOffDmg();
            _ch4.PowerOffDmg();
            _nr50 = 0; _nr51 = 0;
            _powered = false;
        }

        // =====================================================================
        //  Dispose
        // =====================================================================

        public void Dispose()
        {
            if (_audioReady)
            {
                SDL.SDL_CloseAudioDevice(_audioDevice);
                _audioReady = false;
            }
            GC.SuppressFinalize(this);
        }
    }

    // =========================================================================
    //  Channel 1 – Square wave with frequency sweep
    // =========================================================================

    internal sealed class SquareSweepChannel
    {
        public bool Enabled;

        // Registers (raw written values)
        public byte NR10, NR11, NR12, NR13, NR14;

        // Frequency timer & duty
        private int _freqTimer;
        private int _dutyPos;

        // Length counter
        private int _lengthCounter;
        private bool _lengthEnabled;

        // Volume envelope
        private int _volume;
        private int _envTimer;
        private int _envPeriod;
        private bool _envUp;

        // Sweep
        private int _sweepTimer;
        private int _sweepPeriod;
        private bool _sweepNegate;
        private int _sweepShift;
        private int _shadowFreq;
        private bool _sweepEnabled;
        private bool _sweepNegUsed;

        private int _frequency;

        // Flat duty cycle table: 4 waveforms × 8 steps — avoids double-dereference of jagged array
        internal static readonly byte[] Duty =
        {
            0, 0, 0, 0, 0, 0, 0, 1,  // 12.5 %
            1, 0, 0, 0, 0, 0, 0, 1,  // 25 %
            1, 0, 0, 0, 0, 1, 1, 1,  // 50 %
            0, 1, 1, 1, 1, 1, 1, 0,  // 75 %
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 4;
                _dutyPos = (_dutyPos + 1) & 7;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return Duty[((NR11 >> 6) & 3) * 8 + _dutyPos] * _volume;
        }

        // --- Clocks ---

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockEnvelope()
        {
            if (_envPeriod == 0) return;
            if (--_envTimer <= 0)
            {
                _envTimer = _envPeriod;
                if (_envUp && _volume < 15) _volume++;
                else if (!_envUp && _volume > 0) _volume--;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockSweep()
        {
            if (--_sweepTimer <= 0)
            {
                _sweepTimer = _sweepPeriod > 0 ? _sweepPeriod : 8;
                if (_sweepEnabled && _sweepPeriod > 0)
                {
                    int nf = CalcSweepFreq();
                    if (nf <= 2047 && _sweepShift > 0)
                    {
                        _shadowFreq = nf;
                        _frequency = nf;
                        NR13 = (byte)(_frequency & 0xFF);
                        NR14 = (byte)((NR14 & 0xF8) | ((_frequency >> 8) & 7));
                        CalcSweepFreq(); // overflow check again
                    }
                }
            }
        }

        private int CalcSweepFreq()
        {
            int delta = _shadowFreq >> _sweepShift;
            int nf = _sweepNegate ? _shadowFreq - delta : _shadowFreq + delta;
            if (_sweepNegate) _sweepNegUsed = true;
            if (nf > 2047) Enabled = false;
            return nf;
        }

        // --- Register writes ---

        public void WriteNR10(byte v)
        {
            NR10 = v;
            _sweepPeriod = (v >> 4) & 7;
            bool wasNeg = _sweepNegate;
            _sweepNegate = (v & 0x08) != 0;
            _sweepShift = v & 7;
            if (wasNeg && !_sweepNegate && _sweepNegUsed) Enabled = false;
        }

        public void WriteNR11(byte v) { NR11 = v; _lengthCounter = 64 - (v & 0x3F); }

        public void LoadLength(byte v)
        {
            _lengthCounter = 64 - (v & 0x3F);
        }

        public void WriteNR12(byte v) { NR12 = v; if ((v & 0xF8) == 0) Enabled = false; }

        public void WriteNR13(byte v) { NR13 = v; _frequency = (_frequency & 0x700) | v; }

        public void WriteNR14(byte v)
        {
            bool oldLengthEnabled = _lengthEnabled;
            bool trigger = (v & 0x80) != 0;
            bool oddPhase = ((_apuStepProvider?.Invoke() ?? 0) & 1) == 1;
            NR14 = v;
            _frequency = (_frequency & 0xFF) | ((v & 7) << 8);
            _lengthEnabled = (v & 0x40) != 0;

            // Obscure length behavior: enabling length can immediately clock once
            // when done during a frame-sequencer half where length won't be clocked next.
            if (!oldLengthEnabled && _lengthEnabled && oddPhase && !(trigger && _lengthCounter == 0))
                ClockLength();

            bool lengthWillReloadOnTrigger = _lengthCounter == 0;

            if (trigger)
            {
                Trigger();
                // DMG quirk: triggering with zero length can immediately clock once
                // if the next frame-sequencer step won't clock length.
                if (_lengthEnabled && oddPhase && lengthWillReloadOnTrigger)
                    ClockLength();
            }
        }

        private void Trigger()
        {
            Enabled = true;
            if (_lengthCounter == 0) _lengthCounter = 64;
            _freqTimer = (2048 - _frequency) * 4;

            // Envelope
            _volume = (NR12 >> 4) & 0xF;
            _envUp = (NR12 & 0x08) != 0;
            _envPeriod = NR12 & 7;
            _envTimer = _envPeriod;

            // Sweep
            _shadowFreq = _frequency;
            _sweepPeriod = (NR10 >> 4) & 7;
            _sweepShift = NR10 & 7;
            _sweepNegate = (NR10 & 0x08) != 0;
            _sweepNegUsed = false;
            _sweepTimer = _sweepPeriod > 0 ? _sweepPeriod : 8;
            _sweepEnabled = _sweepPeriod > 0 || _sweepShift > 0;
            if (_sweepShift > 0) CalcSweepFreq();

            if ((NR12 & 0xF8) == 0) Enabled = false; // DAC off
        }

        private readonly Func<int>? _apuStepProvider;

        public SquareSweepChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        public void PowerOffDmg()
        {
            // Clear registers but keep length counter state.
            Enabled = false;
            NR10 = NR11 = NR12 = NR13 = NR14 = 0;
            _volume = 0;
            _freqTimer = 0;
            _dutyPos = 0;
            _envTimer = 0;
            _sweepTimer = 0;
            _sweepEnabled = false;
            _shadowFreq = 0;
            _frequency = 0;
            _lengthEnabled = false;
        }

        public void Reset()
        {
            Enabled = false;
            NR10 = NR11 = NR12 = NR13 = NR14 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _dutyPos = 0;
            _envTimer = 0; _sweepTimer = 0; _sweepEnabled = false; _shadowFreq = 0;
        }
    }

    // =========================================================================
    //  Channel 2 – Square wave (no sweep)
    // =========================================================================

    internal sealed class SquareChannel
    {
        public bool Enabled;
        public byte NR21, NR22, NR23, NR24;

        private int _freqTimer, _dutyPos;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _volume, _envTimer, _envPeriod; private bool _envUp;
        private int _frequency;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 4;
                _dutyPos = (_dutyPos + 1) & 7;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return SquareSweepChannel.Duty[((NR21 >> 6) & 3) * 8 + _dutyPos] * _volume;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockEnvelope()
        {
            if (_envPeriod == 0) return;
            if (--_envTimer <= 0)
            {
                _envTimer = _envPeriod;
                if (_envUp && _volume < 15) _volume++;
                else if (!_envUp && _volume > 0) _volume--;
            }
        }

        public void WriteNR21(byte v) { NR21 = v; _lengthCounter = 64 - (v & 0x3F); }
        public void LoadLength(byte v) { _lengthCounter = 64 - (v & 0x3F); }
        public void WriteNR22(byte v) { NR22 = v; if ((v & 0xF8) == 0) Enabled = false; }
        public void WriteNR23(byte v) { NR23 = v; _frequency = (_frequency & 0x700) | v; }

        public void WriteNR24(byte v)
        {
            bool oldLengthEnabled = _lengthEnabled;
            bool trigger = (v & 0x80) != 0;
            bool oddPhase = ((_apuStepProvider?.Invoke() ?? 0) & 1) == 1;
            NR24 = v;
            _frequency = (_frequency & 0xFF) | ((v & 7) << 8);
            _lengthEnabled = (v & 0x40) != 0;

            if (!oldLengthEnabled && _lengthEnabled && oddPhase && !(trigger && _lengthCounter == 0))
                ClockLength();

            bool lengthWillReloadOnTrigger = _lengthCounter == 0;

            if (trigger)
            {
                Trigger();
                if (_lengthEnabled && oddPhase && lengthWillReloadOnTrigger)
                    ClockLength();
            }
        }

        private void Trigger()
        {
            Enabled = true;
            if (_lengthCounter == 0) _lengthCounter = 64;
            _freqTimer = (2048 - _frequency) * 4;
            _volume = (NR22 >> 4) & 0xF;
            _envUp = (NR22 & 0x08) != 0;
            _envPeriod = NR22 & 7;
            _envTimer = _envPeriod;
            if ((NR22 & 0xF8) == 0) Enabled = false;
        }

        private readonly Func<int>? _apuStepProvider;

        public SquareChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        public void PowerOffDmg()
        {
            Enabled = false;
            NR21 = NR22 = NR23 = NR24 = 0;
            _volume = 0;
            _freqTimer = 0;
            _dutyPos = 0;
            _envTimer = 0;
            _frequency = 0;
            _lengthEnabled = false;
        }

        public void Reset()
        {
            Enabled = false;
            NR21 = NR22 = NR23 = NR24 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _dutyPos = 0; _envTimer = 0;
        }
    }

    // =========================================================================
    //  Channel 3 – Programmable wave
    // =========================================================================

    internal sealed class WaveChannel
    {
        public bool Enabled;
        public byte NR30, NR31, NR32, NR33, NR34;

        private readonly byte[] _waveRAM = new byte[16]; // 32 × 4-bit samples
        private int _freqTimer;
        private int _wavePos;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _frequency;
        private int _waveAccessWindow;
        private int _waveAccessIndex;
        private int _waveCorruptIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (_waveAccessWindow > 0)
                _waveAccessWindow--;

            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 2;
                // Byte being fetched at retrigger-sensitive instant.
                _waveCorruptIndex = (_wavePos >> 1) & 0x0F;
                _wavePos = (_wavePos + 1) & 31;
                // Byte visible to CPU wave RAM accesses during the DMG access window.
                _waveAccessIndex = (_wavePos >> 1) & 0x0F;

                // DMG allows wave RAM access only briefly after the active byte fetch.
                _waveAccessWindow = 1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled || (NR30 & 0x80) == 0) return 0;

            byte raw = _waveRAM[_wavePos / 2];
            int sample = (_wavePos & 1) == 0 ? (raw >> 4) & 0xF : raw & 0xF;

            int shift = (NR32 >> 5) & 3;
            sample = shift switch
            {
                0 => sample >> 4,  // mute
                1 => sample,       // 100 %
                2 => sample >> 1,  // 50 %
                3 => sample >> 2,  // 25 %
                _ => 0,
            };
            return sample;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        public void WriteNR30(byte v) { NR30 = v; if ((v & 0x80) == 0) Enabled = false; }
        public void WriteNR31(byte v) { NR31 = v; _lengthCounter = 256 - v; }
        public void LoadLength(byte v) { _lengthCounter = 256 - v; }
        public void WriteNR32(byte v) { NR32 = v; }
        public void WriteNR33(byte v) { NR33 = v; _frequency = (_frequency & 0x700) | v; }

        public void WriteNR34(byte v)
        {
            bool oldLengthEnabled = _lengthEnabled;
            bool trigger = (v & 0x80) != 0;
            bool oddPhase = ((_apuStepProvider?.Invoke() ?? 0) & 1) == 1;
            NR34 = v;
            _frequency = (_frequency & 0xFF) | ((v & 7) << 8);
            _lengthEnabled = (v & 0x40) != 0;

            if (!oldLengthEnabled && _lengthEnabled && oddPhase && !(trigger && _lengthCounter == 0))
                ClockLength();

            bool lengthWillReloadOnTrigger = _lengthCounter == 0;

            if (trigger)
            {
                Trigger();
                if (_lengthEnabled && oddPhase && lengthWillReloadOnTrigger)
                    ClockLength();
            }
        }

        private void Trigger()
        {
            // DMG wave retrigger corruption quirk while already running.
            if (Enabled && (NR30 & 0x80) != 0 && _waveAccessWindow > 0)
                CorruptWaveRamOnRetrigger();

            Enabled = true;
            if (_lengthCounter == 0) _lengthCounter = 256;
            _freqTimer = (2048 - _frequency) * 2;
            _wavePos = 0;
            _waveAccessWindow = 0;
            _waveAccessIndex = 0;
            _waveCorruptIndex = 0;
            if ((NR30 & 0x80) == 0) Enabled = false;
        }

        public byte ReadWaveRAM(uint addr)
        {
            if (Enabled && (NR30 & 0x80) != 0)
            {
                if (_waveAccessWindow == 0)
                    return 0xFF;

                // During the access window, all addresses mirror the currently fetched byte.
                return _waveRAM[_waveAccessIndex];
            }

            return _waveRAM[addr - 0xFF30];
        }

        public void WriteWaveRAM(uint addr, byte value)
        {
            if (Enabled && (NR30 & 0x80) != 0)
            {
                if (_waveAccessWindow == 0)
                    return;

                _waveRAM[_waveAccessIndex] = value;
                return;
            }

            _waveRAM[addr - 0xFF30] = value;
        }

        private void CorruptWaveRamOnRetrigger()
        {
            int i = _waveCorruptIndex;

            if (i < 4)
            {
                _waveRAM[0] = _waveRAM[i];
                return;
            }

            int baseIndex = i & ~0x03;
            _waveRAM[0] = _waveRAM[baseIndex];
            _waveRAM[1] = _waveRAM[baseIndex + 1];
            _waveRAM[2] = _waveRAM[baseIndex + 2];
            _waveRAM[3] = _waveRAM[baseIndex + 3];
        }

        private readonly Func<int>? _apuStepProvider;

        public WaveChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        public void PowerOffDmg()
        {
            // Wave RAM and length are preserved on DMG power off, registers are cleared.
            Enabled = false;
            NR30 = NR31 = NR32 = NR33 = NR34 = 0;
            _freqTimer = 0;
            _wavePos = 0;
            _waveAccessWindow = 0;
            _waveAccessIndex = 0;
            _waveCorruptIndex = 0;
            _frequency = 0;
            _lengthEnabled = false;
        }

        public void Reset()
        {
            Enabled = false;
            NR30 = NR31 = NR32 = NR33 = NR34 = 0;
            _lengthCounter = 0; _freqTimer = 0; _wavePos = 0;
            // Wave RAM intentionally preserved across power-off
        }
    }

    // =========================================================================
    //  Channel 4 – Noise (linear-feedback shift register)
    // =========================================================================

    internal sealed class NoiseChannel
    {
        public bool Enabled;
        public byte NR41, NR42, NR43, NR44;

        private int _freqTimer;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _volume, _envTimer, _envPeriod; private bool _envUp;
        private ushort _lfsr = 0x7FFF;

        private static readonly int[] Divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = Divisors[NR43 & 7] << ((NR43 >> 4) & 0xF);

                int xor = (_lfsr & 1) ^ ((_lfsr >> 1) & 1);
                _lfsr = (ushort)((_lfsr >> 1) | (xor << 14));

                if ((NR43 & 0x08) != 0) // 7-bit width
                {
                    _lfsr &= unchecked((ushort)~(1 << 6));
                    _lfsr |= (ushort)(xor << 6);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return (~_lfsr & 1) * _volume;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockEnvelope()
        {
            if (_envPeriod == 0) return;
            if (--_envTimer <= 0)
            {
                _envTimer = _envPeriod;
                if (_envUp && _volume < 15) _volume++;
                else if (!_envUp && _volume > 0) _volume--;
            }
        }

        public void WriteNR41(byte v) { NR41 = v; _lengthCounter = 64 - (v & 0x3F); }
        public void LoadLength(byte v) { _lengthCounter = 64 - (v & 0x3F); }
        public void WriteNR42(byte v) { NR42 = v; if ((v & 0xF8) == 0) Enabled = false; }
        public void WriteNR43(byte v) { NR43 = v; }

        public void WriteNR44(byte v)
        {
            bool oldLengthEnabled = _lengthEnabled;
            bool trigger = (v & 0x80) != 0;
            bool oddPhase = ((_apuStepProvider?.Invoke() ?? 0) & 1) == 1;
            NR44 = v;
            _lengthEnabled = (v & 0x40) != 0;

            if (!oldLengthEnabled && _lengthEnabled && oddPhase && !(trigger && _lengthCounter == 0))
                ClockLength();

            bool lengthWillReloadOnTrigger = _lengthCounter == 0;

            if (trigger)
            {
                Trigger();
                if (_lengthEnabled && oddPhase && lengthWillReloadOnTrigger)
                    ClockLength();
            }
        }

        private void Trigger()
        {
            Enabled = true;
            if (_lengthCounter == 0) _lengthCounter = 64;
            _freqTimer = Divisors[NR43 & 7] << ((NR43 >> 4) & 0xF);
            _volume = (NR42 >> 4) & 0xF;
            _envUp = (NR42 & 0x08) != 0;
            _envPeriod = NR42 & 7;
            _envTimer = _envPeriod;
            _lfsr = 0x7FFF;
            if ((NR42 & 0xF8) == 0) Enabled = false;
        }

        private readonly Func<int>? _apuStepProvider;

        public NoiseChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        public void PowerOffDmg()
        {
            // Clear most registers but preserve length counter state.
            Enabled = false;
            NR41 = 0;
            NR44 = 0;
            NR42 = NR43 = 0;
            _volume = 0;
            _freqTimer = 0;
            _envTimer = 0;
            _lfsr = 0x7FFF;
            _lengthEnabled = false;
        }

        public void Reset()
        {
            Enabled = false;
            NR41 = NR42 = NR43 = NR44 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _envTimer = 0;
            _lfsr = 0x7FFF;
        }
    }
}