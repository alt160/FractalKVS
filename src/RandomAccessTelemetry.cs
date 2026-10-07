using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FractalKVS
{
    internal static class TrackedRandomAccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetLength(SafeFileHandle handle, FileSyscallCounters counters)
        {
            counters.IncrementGetLength();
            return RandomAccess.GetLength(handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetLength(SafeFileHandle handle, long length, FileSyscallCounters counters)
        {
            if (length > int.MaxValue)
            {
                Debugger.Launch();
                Debugger.Break();
            }
            counters.IncrementSetLength();
            RandomAccess.SetLength(handle, length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(SafeFileHandle handle, Span<byte> buffer, long offset, FileSyscallCounters counters)
        {
            counters.IncrementRead();
            RandomAccess.Read(handle, buffer, offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long offset, FileSyscallCounters counters)
        {
            if (offset > int.MaxValue)
            {
                Debugger.Launch();
                Debugger.Break();
            }
            counters.IncrementWrite();
            RandomAccess.Write(handle, buffer, offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FlushToDisk(SafeFileHandle handle, FileSyscallCounters counters)
        {
            counters.IncrementFlush();
            RandomAccess.FlushToDisk(handle);
        }
    }
}
