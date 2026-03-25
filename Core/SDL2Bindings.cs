// ============================================================================
// Project:     GameboyEmu
// File:        Core/SDL2Bindings.cs
// Description: SDL2 P/Invoke declarations for video, audio, and input
// Author:      James Booth
// Created:     2024
// License:     MIT License - See LICENSE file in the project root
// Copyright:   (c) 2024-2026 James Booth
// Notice:      Game Boy is a registered trademark of Nintendo Co., Ltd.
//              This emulator is for educational purposes only.
// ============================================================================

using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GameboyEmu.Core
{
    /// <summary>
    /// Minimal SDL2 P/Invoke bindings for cross-platform window, rendering, and input.
    /// Requires SDL2 installed on the system:
    ///   macOS:  brew install sdl2
    ///   Linux:  sudo apt install libsdl2-dev
    ///   Windows: place SDL2.dll next to the executable
    /// </summary>
    internal static class SDL
    {
        private const string LibName = "SDL2";

        /// <summary>
        /// Register a custom native library resolver so the runtime can find
        /// SDL2 in Homebrew (/opt/homebrew/lib) or other non-default paths.
        /// Call once at startup before any SDL2 functions are used.
        /// </summary>
        public static void RegisterResolver()
        {
            NativeLibrary.SetDllImportResolver(typeof(SDL).Assembly, (name, assembly, path) =>
            {
                if (name != LibName)
                    return IntPtr.Zero;

                // Try the default search first
                if (NativeLibrary.TryLoad(name, assembly, path, out IntPtr handle))
                    return handle;

                // Platform-specific fallback paths
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    string[] macPaths =
                    [
                        "/opt/homebrew/lib/libSDL2.dylib",           // Apple Silicon Homebrew
                        "/usr/local/lib/libSDL2.dylib",              // Intel Homebrew
                        "/opt/homebrew/lib/libSDL2-2.0.0.dylib",
                        "/usr/local/lib/libSDL2-2.0.0.dylib",
                    ];
                    foreach (var p in macPaths)
                        if (NativeLibrary.TryLoad(p, out handle))
                            return handle;
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    string[] linuxPaths =
                    [
                        "libSDL2-2.0.so.0",
                        "libSDL2-2.0.so",
                        "/usr/lib/x86_64-linux-gnu/libSDL2-2.0.so.0",
                        "/usr/lib/aarch64-linux-gnu/libSDL2-2.0.so.0",
                    ];
                    foreach (var p in linuxPaths)
                        if (NativeLibrary.TryLoad(p, out handle))
                            return handle;
                }

                return IntPtr.Zero;
            });
        }

        // --- Init flags ---
        public const uint SDL_INIT_VIDEO = 0x00000020;
        public const uint SDL_INIT_AUDIO = 0x00000010;

        // --- Audio format ---
        public const ushort AUDIO_F32SYS = 0x8120; // 32-bit float, system byte order

        // --- Window flags ---
        public const uint SDL_WINDOW_SHOWN = 0x00000004;
        public const uint SDL_WINDOW_RESIZABLE = 0x00000020;

        // --- Window position ---
        public const int SDL_WINDOWPOS_CENTERED = 0x2FFF0000;

        // --- Renderer flags ---
        public const uint SDL_RENDERER_ACCELERATED = 0x00000002;
        public const uint SDL_RENDERER_PRESENTVSYNC = 0x00000004;

        // --- Pixel format ---
        public const uint SDL_PIXELFORMAT_ARGB8888 = 0x16362004;

        // --- Texture access ---
        public const int SDL_TEXTUREACCESS_STREAMING = 1;

        // --- Hint names ---
        public const string SDL_HINT_RENDER_SCALE_QUALITY = "SDL_RENDER_SCALE_QUALITY";

        // --- Event types ---
        public const uint SDL_QUIT = 0x100;
        public const uint SDL_KEYDOWN = 0x300;
        public const uint SDL_KEYUP = 0x301;

        // --- Scancode values for key mapping ---
        public const int SDL_SCANCODE_RETURN = 40;   // Start
        public const int SDL_SCANCODE_SPACE = 44;   // Select
        public const int SDL_SCANCODE_Z = 29;   // B
        public const int SDL_SCANCODE_X = 27;   // A
        public const int SDL_SCANCODE_UP = 82;
        public const int SDL_SCANCODE_DOWN = 81;
        public const int SDL_SCANCODE_LEFT = 80;
        public const int SDL_SCANCODE_RIGHT = 79;
        public const int SDL_SCANCODE_ESCAPE = 41;
        public const int SDL_SCANCODE_R = 21;
        public const int SDL_SCANCODE_W = 26;
        public const int SDL_SCANCODE_A = 4;
        public const int SDL_SCANCODE_S = 22;
        public const int SDL_SCANCODE_D = 7;
        public const int SDL_SCANCODE_M = 16;
        public const int SDL_SCANCODE_N = 17;

        // --- Structs ---

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_Keysym
        {
            public int scancode;
            public int sym;
            public ushort mod;
            public uint unused;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_KeyboardEvent
        {
            public uint type;
            public uint timestamp;
            public uint windowID;
            public byte state;
            public byte repeat;
            private byte padding2;
            private byte padding3;
            public SDL_Keysym keysym;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct SDL_Event
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(0)] public SDL_KeyboardEvent key;
        }

        // --- Functions ---

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_Init(uint flags);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_Quit();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_GetError();

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int SDL_SetHint(string name, string value);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyWindow(IntPtr window);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyRenderer(IntPtr renderer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_DestroyTexture(IntPtr texture);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderClear(IntPtr renderer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr srcrect, IntPtr dstrect);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_RenderPresent(IntPtr renderer);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_PollEvent(out SDL_Event e);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderFillRect(IntPtr renderer, ref SDL_Rect rect);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_RenderDrawRect(IntPtr renderer, ref SDL_Rect rect);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_Delay(uint ms);

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_Rect
        {
            public int x, y, w, h;
        }

        // --- Audio structs ---

        [StructLayout(LayoutKind.Sequential)]
        public struct SDL_AudioSpec
        {
            public int freq;
            public ushort format;
            public byte channels;
            public byte silence;
            public ushort samples;
            private ushort padding;
            public uint size;
            public IntPtr callback;
            public IntPtr userdata;
        }

        // --- Audio functions ---

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_InitSubSystem(uint flags);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_OpenAudioDevice(IntPtr device, int iscapture,
            ref SDL_AudioSpec desired, out SDL_AudioSpec obtained, int allowed_changes);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_CloseAudioDevice(uint dev);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_PauseAudioDevice(uint dev, int pause_on);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SDL_QueueAudio(uint dev, IntPtr data, uint len);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint SDL_GetQueuedAudioSize(uint dev);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SDL_ClearQueuedAudio(uint dev);
    }
}