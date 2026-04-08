// ============================================================================
// Project:     GameboyEmu
// File:        Core/APU.cs
// Description: DMG Audio Processing Unit - 4 channels, frame sequencer,
//              register-accurate mixing/panning, and SDL audio output
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
    public sealed class APU : IDisposable
    {
        private const int SampleRate = 44100;
        private const int CPUClockRate = 4194304;
        private const int FrameSequencerPeriod = 8192;
        private const int AudioBufferSamples = 1024;

        private int _fsTimer;
        private int _fsStep;

        private double _sampleTimer;
        private readonly double _samplePeriod;
        private readonly float[] _sampleBuf = new float[AudioBufferSamples * 2];
        private int _sampleIdx;

        private bool _powered;
        private byte _nr50;
        private byte _nr51;

        private readonly SquareSweepChannel _ch1;
        private readonly SquareChannel _ch2;
        private readonly WaveChannel _ch3;
        private readonly NoiseChannel _ch4;

        private uint _audioDevice;
        private bool _audioReady;

        // Initializes apu.
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

        // Executes init audio.
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

            SDL.SDL_PauseAudioDevice(_audioDevice, 0);
            _audioReady = true;
            Console.WriteLine("[APU] Audio initialised – 44100 Hz stereo float32");
        }

        // Executes reset.
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

            if (_audioReady)
                SDL.SDL_ClearQueuedAudio(_audioDevice);
        }

        // Executes tick.
        public void Tick(int cpuCycles)
        {
            if (!_powered) return;

            for (int i = 0; i < cpuCycles; i++)
            {
                _fsTimer--;
                if (_fsTimer <= 0)
                {
                    _fsTimer = FrameSequencerPeriod;
                    StepFrameSequencer();
                }

                _ch1.TickTimer();
                _ch2.TickTimer();
                _ch3.TickTimer();
                _ch4.TickTimer();

                _sampleTimer++;
                if (_sampleTimer >= _samplePeriod)
                {
                    _sampleTimer -= _samplePeriod;
                    MixSample();
                }
            }
        }

        // Executes step frame sequencer.
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

        // Executes clock length counters.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClockLengthCounters()
        {
            _ch1.ClockLength();
            _ch2.ClockLength();
            _ch3.ClockLength();
            _ch4.ClockLength();
        }

        // Executes mix sample.
        private void MixSample()
        {
            float s1 = _ch1.GetOutput();
            float s2 = _ch2.GetOutput();
            float s3 = _ch3.GetOutput();
            float s4 = _ch4.GetOutput();

            float left = 0, right = 0;
            if ((_nr51 & 0x10) != 0) left += s1;
            if ((_nr51 & 0x20) != 0) left += s2;
            if ((_nr51 & 0x40) != 0) left += s3;
            if ((_nr51 & 0x80) != 0) left += s4;

            if ((_nr51 & 0x01) != 0) right += s1;
            if ((_nr51 & 0x02) != 0) right += s2;
            if ((_nr51 & 0x04) != 0) right += s3;
            if ((_nr51 & 0x08) != 0) right += s4;

            int volL = ((_nr50 >> 4) & 7) + 1;
            int volR = (_nr50 & 7) + 1;
            left *= volL;
            right *= volR;

            const float norm = 1.0f / 480.0f;
            left *= norm * 0.5f;
            right *= norm * 0.5f;

            _sampleBuf[_sampleIdx++] = left;
            _sampleBuf[_sampleIdx++] = right;

            if (_sampleIdx >= _sampleBuf.Length)
            {
                FlushAudioBuffer();
                _sampleIdx = 0;
            }
        }

        // Executes flush audio buffer.
        private void FlushAudioBuffer()
        {
            if (!_audioReady) return;

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

        // Executes read register.
        public byte ReadRegister(uint addr)
        {
            return addr switch
            {
                0xFF10 => (byte)(_ch1.NR10 | 0x80),
                0xFF11 => (byte)(_ch1.NR11 | 0x3F),
                0xFF12 => _ch1.NR12,
                0xFF13 => 0xFF,
                0xFF14 => (byte)(_ch1.NR14 | 0xBF),

                0xFF16 => (byte)(_ch2.NR21 | 0x3F),
                0xFF17 => _ch2.NR22,
                0xFF18 => 0xFF,
                0xFF19 => (byte)(_ch2.NR24 | 0xBF),

                0xFF1A => (byte)(_ch3.NR30 | 0x7F),
                0xFF1B => 0xFF,
                0xFF1C => (byte)(_ch3.NR32 | 0x9F),
                0xFF1D => 0xFF,
                0xFF1E => (byte)(_ch3.NR34 | 0xBF),

                0xFF20 => 0xFF,
                0xFF21 => _ch4.NR42,
                0xFF22 => _ch4.NR43,
                0xFF23 => (byte)(_ch4.NR44 | 0xBF),

                0xFF24 => _nr50,
                0xFF25 => _nr51,
                0xFF26 => BuildNR52(),

                >= 0xFF30 and <= 0xFF3F => _ch3.ReadWaveRAM(addr),

                _ => 0xFF,
            };
        }

        // Executes write register.
        public void WriteRegister(uint addr, byte value)
        {
            if (addr == 0xFF26)
            {
                bool next = (value & 0x80) != 0;
                if (!next && _powered) PowerOff();
                // Executes if.
                else if (next && !_powered)
                {
                    _fsStep = 0;
                    _fsTimer = FrameSequencerPeriod;
                }
                _powered = next;
                return;
            }

            if (addr is >= 0xFF30 and <= 0xFF3F) { _ch3.WriteWaveRAM(addr, value); return; }

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
                case 0xFF10: _ch1.WriteNR10(value); break;
                case 0xFF11: _ch1.WriteNR11(value); break;
                case 0xFF12: _ch1.WriteNR12(value); break;
                case 0xFF13: _ch1.WriteNR13(value); break;
                case 0xFF14: _ch1.WriteNR14(value); break;

                case 0xFF16: _ch2.WriteNR21(value); break;
                case 0xFF17: _ch2.WriteNR22(value); break;
                case 0xFF18: _ch2.WriteNR23(value); break;
                case 0xFF19: _ch2.WriteNR24(value); break;

                case 0xFF1A: _ch3.WriteNR30(value); break;
                case 0xFF1B: _ch3.WriteNR31(value); break;
                case 0xFF1C: _ch3.WriteNR32(value); break;
                case 0xFF1D: _ch3.WriteNR33(value); break;
                case 0xFF1E: _ch3.WriteNR34(value); break;

                case 0xFF20: _ch4.WriteNR41(value); break;
                case 0xFF21: _ch4.WriteNR42(value); break;
                case 0xFF22: _ch4.WriteNR43(value); break;
                case 0xFF23: _ch4.WriteNR44(value); break;

                case 0xFF24: _nr50 = value; break;
                case 0xFF25: _nr51 = value; break;
            }
        }

        // Executes build nr52.
        private byte BuildNR52()
        {
            byte v = (byte)(_powered ? 0x80 : 0x00);
            if (_ch1.Enabled) v |= 0x01;
            if (_ch2.Enabled) v |= 0x02;
            if (_ch3.Enabled) v |= 0x04;
            if (_ch4.Enabled) v |= 0x08;
            return (byte)(v | 0x70);
        }

        // Executes power off.
        private void PowerOff()
        {
            _ch1.PowerOffDmg();
            _ch2.PowerOffDmg();
            _ch3.PowerOffDmg();
            _ch4.PowerOffDmg();
            _nr50 = 0; _nr51 = 0;
            _powered = false;
        }

        // Executes dispose.
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

    internal sealed class SquareSweepChannel
    {
        public bool Enabled;

        public byte NR10, NR11, NR12, NR13, NR14;

        private int _freqTimer;
        private int _dutyPos;

        private int _lengthCounter;
        private bool _lengthEnabled;

        private int _volume;
        private int _envTimer;
        private int _envPeriod;
        private bool _envUp;

        private int _sweepTimer;
        private int _sweepPeriod;
        private bool _sweepNegate;
        private int _sweepShift;
        private int _shadowFreq;
        private bool _sweepEnabled;
        private bool _sweepNegUsed;

        private int _frequency;

        internal static readonly byte[] Duty =
        {
            0, 0, 0, 0, 0, 0, 0, 1,
            1, 0, 0, 0, 0, 0, 0, 1,
            1, 0, 0, 0, 0, 1, 1, 1,
            0, 1, 1, 1, 1, 1, 1, 0,
        };

        // Executes tick timer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 4;
                _dutyPos = (_dutyPos + 1) & 7;
            }
        }

        // Executes get output.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return Duty[((NR11 >> 6) & 3) * 8 + _dutyPos] * _volume;
        }

        // Executes clock length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        // Executes clock envelope.
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

        // Executes clock sweep.
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
                        CalcSweepFreq();
                    }
                }
            }
        }

        // Executes calc sweep freq.
        private int CalcSweepFreq()
        {
            int delta = _shadowFreq >> _sweepShift;
            int nf = _sweepNegate ? _shadowFreq - delta : _shadowFreq + delta;
            if (_sweepNegate) _sweepNegUsed = true;
            if (nf > 2047) Enabled = false;
            return nf;
        }

        // Executes write nr10.
        public void WriteNR10(byte v)
        {
            NR10 = v;
            _sweepPeriod = (v >> 4) & 7;
            bool wasNeg = _sweepNegate;
            _sweepNegate = (v & 0x08) != 0;
            _sweepShift = v & 7;
            if (wasNeg && !_sweepNegate && _sweepNegUsed) Enabled = false;
        }

        // Executes write nr11.
        public void WriteNR11(byte v) { NR11 = v; _lengthCounter = 64 - (v & 0x3F); }

        // Executes load length.
        public void LoadLength(byte v)
        {
            _lengthCounter = 64 - (v & 0x3F);
        }

        // Executes write nr12.
        public void WriteNR12(byte v) { NR12 = v; if ((v & 0xF8) == 0) Enabled = false; }

        // Executes write nr13.
        public void WriteNR13(byte v) { NR13 = v; _frequency = (_frequency & 0x700) | v; }

        // Executes write nr14.
        public void WriteNR14(byte v)
        {
            bool oldLengthEnabled = _lengthEnabled;
            bool trigger = (v & 0x80) != 0;
            bool oddPhase = ((_apuStepProvider?.Invoke() ?? 0) & 1) == 1;
            NR14 = v;
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

        // Executes trigger.
        private void Trigger()
        {
            Enabled = true;
            if (_lengthCounter == 0) _lengthCounter = 64;
            _freqTimer = (2048 - _frequency) * 4;

            _volume = (NR12 >> 4) & 0xF;
            _envUp = (NR12 & 0x08) != 0;
            _envPeriod = NR12 & 7;
            _envTimer = _envPeriod;

            _shadowFreq = _frequency;
            _sweepPeriod = (NR10 >> 4) & 7;
            _sweepShift = NR10 & 7;
            _sweepNegate = (NR10 & 0x08) != 0;
            _sweepNegUsed = false;
            _sweepTimer = _sweepPeriod > 0 ? _sweepPeriod : 8;
            _sweepEnabled = _sweepPeriod > 0 || _sweepShift > 0;
            if (_sweepShift > 0) CalcSweepFreq();

            if ((NR12 & 0xF8) == 0) Enabled = false;
        }

        private readonly Func<int>? _apuStepProvider;

        // Initializes square sweep channel.
        public SquareSweepChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        // Executes power off dmg.
        public void PowerOffDmg()
        {
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

        // Executes reset.
        public void Reset()
        {
            Enabled = false;
            NR10 = NR11 = NR12 = NR13 = NR14 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _dutyPos = 0;
            _envTimer = 0; _sweepTimer = 0; _sweepEnabled = false; _shadowFreq = 0;
        }
    }

    internal sealed class SquareChannel
    {
        public bool Enabled;
        public byte NR21, NR22, NR23, NR24;

        private int _freqTimer, _dutyPos;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _volume, _envTimer, _envPeriod; private bool _envUp;
        private int _frequency;

        // Executes tick timer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 4;
                _dutyPos = (_dutyPos + 1) & 7;
            }
        }

        // Executes get output.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return SquareSweepChannel.Duty[((NR21 >> 6) & 3) * 8 + _dutyPos] * _volume;
        }

        // Executes clock length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        // Executes clock envelope.
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

        // Executes write nr21.
        public void WriteNR21(byte v) { NR21 = v; _lengthCounter = 64 - (v & 0x3F); }
        // Executes load length.
        public void LoadLength(byte v) { _lengthCounter = 64 - (v & 0x3F); }
        // Executes write nr22.
        public void WriteNR22(byte v) { NR22 = v; if ((v & 0xF8) == 0) Enabled = false; }
        // Executes write nr23.
        public void WriteNR23(byte v) { NR23 = v; _frequency = (_frequency & 0x700) | v; }

        // Executes write nr24.
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

        // Executes trigger.
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

        // Initializes square channel.
        public SquareChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        // Executes power off dmg.
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

        // Executes reset.
        public void Reset()
        {
            Enabled = false;
            NR21 = NR22 = NR23 = NR24 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _dutyPos = 0; _envTimer = 0;
        }
    }

    internal sealed class WaveChannel
    {
        public bool Enabled;
        public byte NR30, NR31, NR32, NR33, NR34;

        private readonly byte[] _waveRAM = new byte[16];
        private int _freqTimer;
        private int _wavePos;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _frequency;
        private int _waveAccessWindow;
        private int _waveAccessIndex;
        private int _waveCorruptIndex;

        // Executes tick timer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (_waveAccessWindow > 0)
                _waveAccessWindow--;

            if (--_freqTimer <= 0)
            {
                _freqTimer = (2048 - _frequency) * 2;

                _waveCorruptIndex = (_wavePos >> 1) & 0x0F;
                _wavePos = (_wavePos + 1) & 31;

                _waveAccessIndex = (_wavePos >> 1) & 0x0F;

                _waveAccessWindow = 1;
            }
        }

        // Executes get output.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled || (NR30 & 0x80) == 0) return 0;

            byte raw = _waveRAM[_wavePos / 2];
            int sample = (_wavePos & 1) == 0 ? (raw >> 4) & 0xF : raw & 0xF;

            int shift = (NR32 >> 5) & 3;
            sample = shift switch
            {
                0 => sample >> 4,
                1 => sample,
                2 => sample >> 1,
                3 => sample >> 2,
                _ => 0,
            };
            return sample;
        }

        // Executes clock length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        // Executes write nr30.
        public void WriteNR30(byte v) { NR30 = v; if ((v & 0x80) == 0) Enabled = false; }
        // Executes write nr31.
        public void WriteNR31(byte v) { NR31 = v; _lengthCounter = 256 - v; }
        // Executes load length.
        public void LoadLength(byte v) { _lengthCounter = 256 - v; }
        // Executes write nr32.
        public void WriteNR32(byte v) { NR32 = v; }
        // Executes write nr33.
        public void WriteNR33(byte v) { NR33 = v; _frequency = (_frequency & 0x700) | v; }

        // Executes write nr34.
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

        // Executes trigger.
        private void Trigger()
        {
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

        // Executes read wave ram.
        public byte ReadWaveRAM(uint addr)
        {
            if (Enabled && (NR30 & 0x80) != 0)
            {
                if (_waveAccessWindow == 0)
                    return 0xFF;

                return _waveRAM[_waveAccessIndex];
            }

            return _waveRAM[addr - 0xFF30];
        }

        // Executes write wave ram.
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

        // Executes corrupt wave ram on retrigger.
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

        // Initializes wave channel.
        public WaveChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        // Executes power off dmg.
        public void PowerOffDmg()
        {
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

        // Executes reset.
        public void Reset()
        {
            Enabled = false;
            NR30 = NR31 = NR32 = NR33 = NR34 = 0;
            _lengthCounter = 0; _freqTimer = 0; _wavePos = 0;
        }
    }

    internal sealed class NoiseChannel
    {
        public bool Enabled;
        public byte NR41, NR42, NR43, NR44;

        private int _freqTimer;
        private int _lengthCounter; private bool _lengthEnabled;
        private int _volume, _envTimer, _envPeriod; private bool _envUp;
        private ushort _lfsr = 0x7FFF;

        private static readonly int[] Divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

        // Executes tick timer.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TickTimer()
        {
            if (--_freqTimer <= 0)
            {
                _freqTimer = Divisors[NR43 & 7] << ((NR43 >> 4) & 0xF);

                int xor = (_lfsr & 1) ^ ((_lfsr >> 1) & 1);
                _lfsr = (ushort)((_lfsr >> 1) | (xor << 14));

                if ((NR43 & 0x08) != 0)
                {
                    _lfsr &= unchecked((ushort)~(1 << 6));
                    _lfsr |= (ushort)(xor << 6);
                }
            }
        }

        // Executes get output.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetOutput()
        {
            if (!Enabled) return 0;
            return (~_lfsr & 1) * _volume;
        }

        // Executes clock length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ClockLength()
        {
            if (_lengthEnabled && _lengthCounter > 0)
                if (--_lengthCounter == 0) Enabled = false;
        }

        // Executes clock envelope.
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

        // Executes write nr41.
        public void WriteNR41(byte v) { NR41 = v; _lengthCounter = 64 - (v & 0x3F); }
        // Executes load length.
        public void LoadLength(byte v) { _lengthCounter = 64 - (v & 0x3F); }
        // Executes write nr42.
        public void WriteNR42(byte v) { NR42 = v; if ((v & 0xF8) == 0) Enabled = false; }
        // Executes write nr43.
        public void WriteNR43(byte v) { NR43 = v; }

        // Executes write nr44.
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

        // Executes trigger.
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

        // Initializes noise channel.
        public NoiseChannel(Func<int>? apuStepProvider = null)
        {
            _apuStepProvider = apuStepProvider;
        }

        // Executes power off dmg.
        public void PowerOffDmg()
        {
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

        // Executes reset.
        public void Reset()
        {
            Enabled = false;
            NR41 = NR42 = NR43 = NR44 = 0;
            _volume = 0; _lengthCounter = 0; _freqTimer = 0; _envTimer = 0;
            _lfsr = 0x7FFF;
        }
    }
}
