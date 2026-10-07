using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;








namespace FractalKVS
{
    using System.ComponentModel;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.Intrinsics;
    using System.Runtime.Intrinsics.X86;

    internal static class Crc64
    {
        // ECMA polynomial
        private const ulong Poly = 0x42F0E1EBA9EA3693;

        private static readonly ulong[] Table = new ulong[256];
        private static bool _initialized;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureInit()
        {
            if (_initialized) return;
            InitTable();
            _initialized = true;
        }

        private static void InitTable()
        {
            for (ulong i = 0; i < 256; i++)
            {
                ulong crc = i << 56;

                for (int b = 0; b < 8; b++)
                {
                    if ((crc & 0x8000_0000_0000_0000) != 0)
                        crc = (crc << 1) ^ Poly;
                    else
                        crc <<= 1;
                }

                Table[i] = crc;
            }
        }

        /// <summary>
        /// Computes CRC64-ECMA of a 64-bit value.<br/>
        /// Input: ulong<br/>
        /// Output: ulong<br/>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Compute(ulong value)
        {
            EnsureInit();

            ulong crc = 0xFFFFFFFFFFFFFFFFUL;

            // Process 8 bytes of the ulong
            for (int i = 0; i < 8; i++)
            {
                byte b = (byte)(value >> ((7 - i) * 8));
                crc = Table[(byte)((crc >> 56) ^ b)] ^ (crc << 8);
            }

            return ~crc; // final XOR (standard ECMA finalization)
        }
    }

    //====== TYPES ======
    internal static partial class DiskGeometryInfo
    {
        //Shared/Static Members
        public static DiskGeometry Get(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return new DiskGeometry(
                    ClusterSize: GetClusterSizeWindows(path),
                    PhysicalSectorSize: GetPhysicalSectorSizeWindows(path));
            }
            else if (OperatingSystem.IsLinux())
            {
                return new DiskGeometry(
                    ClusterSize: GetClusterSizeLinux(path),
                    PhysicalSectorSize: GetPhysicalSectorSizeLinux(path));
            }
            else if (OperatingSystem.IsMacOS())
            {
                return new DiskGeometry(
                    ClusterSize: GetClusterSizeMac(path),
                    PhysicalSectorSize: 4096); // fallback—IOKit needed for true value
            }
            else
            {
                throw new PlatformNotSupportedException();
            }
        }








        public record DiskGeometry(uint ClusterSize, uint PhysicalSectorSize);








        #region Windows
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetDiskFreeSpaceW(string lpRootPathName,
            out uint lpSectorsPerCluster,
            out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters,
            out uint lpTotalNumberOfClusters);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            out STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        const uint FILE_SHARE_READ = 1;
        const uint FILE_SHARE_WRITE = 2;
        const uint OPEN_EXISTING = 3;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        [StructLayout(LayoutKind.Sequential)]
        struct STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public uint BytesPerCacheLine;
            public uint BytesOffsetForCacheAlignment;
            public uint BytesPerLogicalSector;
            public uint BytesPerPhysicalSector;
            public uint BytesOffsetForSectorAlignment;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId; // STORAGE_PROPERTY_ID (0 = StorageAccessAlignmentProperty)
            public int QueryType;  // STORAGE_QUERY_TYPE (0 = PropertyStandardQuery)
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        static uint GetClusterSizeWindows(string path)
        {
            string root = Path.GetPathRoot(path)!;
            uint spc = 0, bps = 0;
            if (!GetDiskFreeSpaceW(root, out spc, out bps, out _, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            return (uint)spc * bps;
        }

        static uint GetPhysicalSectorSizeWindows(string path)
        {
            string drive = Path.GetPathRoot(path)!.TrimEnd('\\');
            var handle = CreateFileW($"\\\\.\\{drive}",
                FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle.ToInt64() == -1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            // Prepare input buffer
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = 6, // StorageAccessAlignmentProperty
                QueryType = 0,  // PropertyStandardQuery
                AdditionalParameters = new byte[1] // Must be present, even if unused
            };
            int querySize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>();
            IntPtr queryPtr = Marshal.AllocHGlobal(querySize);
            try
            {
                Marshal.StructureToPtr(query, queryPtr, false);

                STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR desc;
                uint ret;
                bool ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, queryPtr, (uint)querySize,
                    out desc, (uint)Marshal.SizeOf<STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR>(), out ret, IntPtr.Zero);

                if (!ok)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                return desc.BytesPerPhysicalSector;
            }
            finally
            {
                Marshal.FreeHGlobal(queryPtr);
                CloseHandle(handle);
            }
        }
        #endregion

        #region Linux
        [StructLayout(LayoutKind.Sequential)]
        struct StatVFS
        {
            public ulong f_bsize, f_frsize, f_blocks, f_bfree, f_bavail;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
            public ulong[] f_unused;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int statvfs(string path, out StatVFS buf);

        static uint GetClusterSizeLinux(string path)
        {
            if (statvfs(path, out var info) != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return (uint)(info.f_frsize != 0 ? info.f_frsize : info.f_bsize);
        }

        static uint GetPhysicalSectorSizeLinux(string path)
        {
            var dev = "sda"; // Simplify; real code resolves from path
            var sysPath = $"/sys/block/{dev}/queue/physical_block_size";
            if (File.Exists(sysPath))
                return uint.Parse(File.ReadAllText(sysPath).Trim());
            return 4096;
        }
        #endregion

        #region macOS
        [StructLayout(LayoutKind.Sequential)]
        struct StatFS
        {
            public uint f_bsize;
            public int f_iosize;
            public ulong f_blocks, f_bfree, f_bavail, f_files, f_ffree;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
            public string f_fstypename;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
            public string f_mntonname;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int statfs(string path, out StatFS buf);

        static uint GetClusterSizeMac(string path)
        {
            if (statfs(path, out var info) != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return info.f_bsize;
        }
        #endregion
    }



    internal static class Utils
    {

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ZeroRange(Span<byte> span, int offset, int count)
        {
            if ((uint)offset > (uint)span.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            int len = (count < 0) ? span.Length - offset : count;
            if (len <= 0) return;
            if (offset + len > span.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(span), offset);

            // =====================
            // AVX2 — 32B per write
            // =====================
            if (Avx2.IsSupported)
            {
                var z = Vector256<byte>.Zero;

                while (len >= 32)
                {
                    Unsafe.WriteUnaligned(ref start, z);
                    start = ref Unsafe.Add(ref start, 32);
                    len -= 32;
                }
            }

            // =====================
            // SSE2 — 16B per write
            // =====================
            if (Sse2.IsSupported)
            {
                var z = Vector128<byte>.Zero;

                while (len >= 16)
                {
                    Unsafe.WriteUnaligned(ref start, z);
                    start = ref Unsafe.Add(ref start, 16);
                    len -= 16;
                }
            }

            // =====================
            // 8B remainder
            // =====================
            while (len >= 8)
            {
                Unsafe.WriteUnaligned(ref start, 0UL);
                start = ref Unsafe.Add(ref start, 8);
                len -= 8;
            }

            // =====================
            // final bytes
            // =====================
            while (len-- > 0)
            {
                start = 0;
                start = ref Unsafe.Add(ref start, 1);
            }
        }


        // Define the core constants derived from the optimal performance metrics
        private const double StableDensityRatio = 1.75; // D: Factor derived from 1.5M run (2.62 slots/item)
        private const int FixedRootTierBits = 8;             // B_R: Fixed for optimal cache partitioning (256 buckets)
        private const int MaxChain = 4;                 // T: Max items in a leaf chain
        private const int SubsequentTierBits = 4;       // B_n: Fixed for rapid deep resolution (16 buckets)
                                                        // Engineering threshold: If the required capacity is within 1% of the lower power of two,
                                                        // we round down to maximize memory density (as demonstrated by N=150K test).
        private const double DeficitThreshold = 0.01;


        /// <summary>
        /// Calculates the optimal parameters (levers) for the tiered hashing system
        /// based on the projected record count (N) and the performance-driven density model.
        /// </summary>
        /// <param name="recordCount">The projected total number of records (N).</param>
        /// <returns>A ValueTuple containing the calculated lever values as raw integers.</returns>
        public static (int rootCount, long tier1Count, int subsequentCount, int maxChain) CalculateOptimalLevers(long recordCount)
        {
            if (recordCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(recordCount), "Record count must be positive.");
            }

            // --- 1. Fixed Levers ---
            int maxChain = MaxChain;
            int subsequentCount = (int)Math.Pow(2, SubsequentTierBits); // 16
            int rootCount = (int)Math.Pow(2, FixedRootTierBits);       // 256

            // --- 2. Calculate Required Resolution Bits (B_total) ---

            double requiredTotalSlots = StableDensityRatio * recordCount;
            double rawB_total = Math.Log(requiredTotalSlots, 2);

            // --- 3. Threshold Rounding Logic (Ensures N=150K returns 1024, not 2048) ---

            int B_total;
            int B_total_low = (int)Math.Floor(rawB_total);

            // Calculate the capacity provided by the lower power of two
            double capacityLow = Math.Pow(2, B_total_low);

            // Check how much larger the requirement is than the lower capacity
            double deficit = requiredTotalSlots - capacityLow;

            // If the requirement is slightly over the lower capacity (and less than the 1% threshold),
            // we use the lower capacity to maximize density and cache efficiency.
            if (deficit > 0 && deficit <= capacityLow * DeficitThreshold)
            {
                B_total = B_total_low;
            }
            else
            {
                // Otherwise, we use the ceiling to ensure necessary structural stability.
                B_total = (int)Math.Ceiling(rawB_total);
            }

            // --- 4. Distribute Bits and Final Calculation ---

            int B_1 = B_total - FixedRootTierBits;
            if (B_1 < 0) B_1 = 0;

            // Use Math.Pow to generate the exact optimal power-of-two count
            long tier1Count = (long)Math.Pow(2, B_1);

            return (rootCount, tier1Count, subsequentCount, maxChain);
        }

        //Shared/Static Members
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<byte> AsByteSpan<T>(this T value) where T : unmanaged => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<byte> AsRefByteSpan<T>(ref T value) where T : unmanaged => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        /// <summary>
        /// Checks if an integral value is a power of two.
        /// Works for any integer type implementing IBinaryInteger.
        /// </summary>
        public static bool IsPowerOfTwo<T>(this T value) where T : IBinaryInteger<T>
            => value > T.Zero && (value & (value - T.One)) == T.Zero;
        public static ulong NearestPowerOfFour(this double value) => PowNearest(value, 4, Math.Round);
        public static ulong NearestPowerOfThree(this double value) => PowNearest(value, 3, Math.Round);
        public static ulong NearestPowerOfTwo(this double value) => PowNearest(value, 2, Math.Round);
        public static ulong NextPowerOfFour(this double value) => PowNearest(value, 4, Math.Ceiling);
        public static ulong NextPowerOfThree(this double value) => PowNearest(value, 3, Math.Ceiling);
        public static ulong NextPowerOfTwo(this double value) => PowNearest(value, 2, Math.Ceiling);
        private static ulong PowNearest(double value, double baseVal, Func<double, double> round)
        {
            if (value <= 0) return 0;
            double exp = round(Math.Log(value, baseVal));
            return (ulong)Math.Round(Math.Pow(baseVal, exp));
        }
        public static ulong PrevPowerOfFour(this double value) => PowNearest(value, 4, Math.Floor);
        public static ulong PrevPowerOfThree(this double value) => PowNearest(value, 3, Math.Floor);
        public static ulong PrevPowerOfTwo(this double value) => PowNearest(value, 2, Math.Floor);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(this ReadOnlySpan<byte> src) where T : unmanaged => MemoryMarshal.Read<T>(src);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(this Memory<byte> src) where T : unmanaged => MemoryMarshal.Read<T>(src.Span);
    }

    internal sealed class AbandonedSemutexException : SystemException
    {
        //======  CONSTRUCTORS  ======
        public AbandonedSemutexException(string name, bool alreadyAcquired)
                    : base($"Semutex '{name}' was abandoned; native ownership was acquired by this instance.")
        {
            Name = name;
            AlreadyAcquired = alreadyAcquired;
            HResult = unchecked((int)0x8013152D); // same code family as AbandonedMutexException
        }








        //======  PROPERTIES  ======
        public bool AlreadyAcquired { get; }
        public string Name { get; }
    }






    /// <summary>
    /// Cross-process atomic 64 and 32 bit counters backed by a tiny memory-mapped file.<br/>
    /// Provides atomic read/write/increment/decrement/add/compare-exchange operations.<br/>
    /// Mapped region is 4 KB (4096 bytes) in size, allowing up to 1024 32-bit slots or 512 64-bit slots.<br/>
    /// </summary>
    //====== TYPES ======
    internal sealed unsafe class AtomicIntegers : IDisposable
        {
            //Shared/Static Members
            private static string BuildMutexName(string path)
            {
                string full = System.IO.Path.GetFullPath(path);
                const ulong fnvOffset = 1469598103934665603UL;
                const ulong fnvPrime = 1099511628211UL;

                ulong hash = fnvOffset;
                foreach (char c in full)
                {
                    hash ^= c;
                    hash *= fnvPrime;
                }

                return "MmfAtomicSync_" + hash.ToString("X16");
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void CheckRange(int byteOffset, int size)
            {
                if ((uint)byteOffset > RegionSize - size)
                    throw new ArgumentOutOfRangeException(nameof(byteOffset));
            }
            // --------------------------------------------------------------
            // Index → aligned byte offset
            // --------------------------------------------------------------
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ValidateInt32Index(int index)
            {
                if ((uint)index >= MaxInt32Slots)
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void ValidateInt64Index(int index)
            {
                if ((uint)index >= MaxInt64Slots)
                    throw new ArgumentOutOfRangeException(nameof(index));
            }








            // Full region is user-usable
            //======  FIELDS  ======
            public const int RegionSize = 8192;
            public const int MaxInt32Slots = RegionSize / 4; // 2048
            public const int MaxInt64Slots = RegionSize / 8; // 1024








            private byte* _basePtr;
            private bool _disposed;
            private readonly FileStream _file;
            private readonly Mutex _initializationLifetimeMutex;
            private readonly MemoryMappedFile _mmf;
            private readonly int[]? _preserve32;
            private readonly int[]? _preserve64;
            private readonly MemoryMappedViewAccessor _view;








            //======  CONSTRUCTORS  ======
            public AtomicIntegers(
                            string path,
                            int[]? preserve32 = null,
                            int[]? preserve64 = null)
            {
                Path = path ?? throw new ArgumentNullException(nameof(path));

                // Copy preservation lists so caller can't mutate after construction
                if (preserve32 is { Length: > 0 })
                {
                    _preserve32 = new int[preserve32.Length];
                    Array.Copy(preserve32, _preserve32, preserve32.Length);
                }

                if (preserve64 is { Length: > 0 })
                {
                    _preserve64 = new int[preserve64.Length];
                    Array.Copy(preserve64, _preserve64, preserve64.Length);
                }

                _file = new FileStream(
                    Path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);

                // Ensure backing file is at least RegionSize
                if (_file.Length < RegionSize)
                {
                    _file.SetLength(RegionSize);
                    _file.Flush(true);
                }

                // Named mutex per file path
                string mutexName = BuildMutexName(Path);
                _initializationLifetimeMutex = new Mutex(false, mutexName, out bool createdNew);

                _initializationLifetimeMutex.WaitOne();
                try
                {
                    _mmf = MemoryMappedFile.CreateFromFile(
                        _file,
                        null,
                        RegionSize,
                        MemoryMappedFileAccess.ReadWrite,
                        HandleInheritability.None,
                        false);

                    _view = _mmf.CreateViewAccessor(0, RegionSize, MemoryMappedFileAccess.ReadWrite);

                    byte* ptr = null;
                    _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _basePtr = ptr;

                    // First creator of the mutex is responsible for initializing the region.
                    if (createdNew)
                    {
                        InitializeRegionPreservingConfiguredSlots();
                        _view.Flush(); // not strictly required, but reasonable for persisted slots
                    }
                }
                finally
                {
                    _initializationLifetimeMutex.ReleaseMutex();
                }
            }








            ~AtomicIntegers()
            {
                Dispose();
            }








            //======  PROPERTIES  ======
            public string Path { get; }








            // --------------------------------------------------------------
            // General helpers
            // --------------------------------------------------------------
            //======  METHODS  ======
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void EnsureNotDisposed()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(AtomicIntegers));
            }
            // --------------------------------------------------------------
            // Raw ref accessors (internal)
            // --------------------------------------------------------------

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref int GetInt32Ref(int byteOffset)
            {
                CheckRange(byteOffset, sizeof(int));
                return ref Unsafe.AsRef<int>(_basePtr + byteOffset);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref long GetInt64Ref(int byteOffset)
            {
                CheckRange(byteOffset, sizeof(long));
                return ref Unsafe.AsRef<long>(_basePtr + byteOffset);
            }
            // --------------------------------------------------------------
            // Initialization (first-opener only)
            // --------------------------------------------------------------

            private void InitializeRegionPreservingConfiguredSlots()
            {
                // Snapshot preserved values (if any) before clearing.
                int[]? preserved32Values = null;
                long[]? preserved64Values = null;

                if (_preserve32 is { Length: > 0 })
                {
                    preserved32Values = new int[_preserve32.Length];
                    for (int i = 0; i < _preserve32.Length; i++)
                    {
                        int index = _preserve32[i];
                        ValidateInt32Index(index);
                        preserved32Values[i] = UnsafeRead32(index);
                    }
                }

                if (_preserve64 is { Length: > 0 })
                {
                    preserved64Values = new long[_preserve64.Length];
                    for (int i = 0; i < _preserve64.Length; i++)
                    {
                        int index = _preserve64[i];
                        ValidateInt64Index(index);
                        preserved64Values[i] = UnsafeRead64(index);
                    }
                }

                // Clear the entire 4KB region (ephemeral values zeroed).
                AsSpan().Clear();

                // Restore preserved slots.
                if (preserved32Values is not null)
                {
                    for (int i = 0; i < _preserve32!.Length; i++)
                    {
                        UnsafeWrite32(_preserve32[i], preserved32Values[i]);
                    }
                }

                if (preserved64Values is not null)
                {
                    for (int i = 0; i < _preserve64!.Length; i++)
                    {
                        UnsafeWrite64(_preserve64[i], preserved64Values[i]);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref int Int32Slot(int index)
            {
                ValidateInt32Index(index);
                int offset = index << 2; // index * 4
                return ref GetInt32Ref(offset);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ref long Int64Slot(int index)
            {
                ValidateInt64Index(index);
                int offset = index << 3; // index * 8
                return ref GetInt64Ref(offset);
            }

            // Unsafe direct reads/writes (no disposed checks, used only during init)
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int UnsafeRead32(int index) => Int32Slot(index);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private long UnsafeRead64(int index) => Int64Slot(index);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void UnsafeWrite32(int index, int value) => Int32Slot(index) = value;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void UnsafeWrite64(int index, long value) => Int64Slot(index) = value;








            //------ Public Methods -----
            public int Add32(int index, int value)
            {
                EnsureNotDisposed();
                return Interlocked.Add(ref Int32Slot(index), value);
            }

            public long Add64(int index, long value)
            {
                EnsureNotDisposed();
                return Interlocked.Add(ref Int64Slot(index), value);
            }
            // --------------------------------------------------------------
            // Raw span access (full 4 KB region)
            // --------------------------------------------------------------

            public Span<byte> AsSpan()
            {
                EnsureNotDisposed();
                return new Span<byte>(_basePtr, RegionSize);
            }

            public int CompareExchange32(int index, int value, int comparand)
            {
                EnsureNotDisposed();
                return Interlocked.CompareExchange(ref Int32Slot(index), value, comparand);
            }

            public long CompareExchange64(int index, long value, long comparand)
            {
                EnsureNotDisposed();
                return Interlocked.CompareExchange(ref Int64Slot(index), value, comparand);
            }

            public int Decrement32(int index)
            {
                EnsureNotDisposed();
                return Interlocked.Decrement(ref Int32Slot(index));
            }

            public long Decrement64(int index)
            {
                EnsureNotDisposed();
                return Interlocked.Decrement(ref Int64Slot(index));
            }
            // --------------------------------------------------------------
            // Dispose
            // --------------------------------------------------------------

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_basePtr != null)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _basePtr = null;
                }

                _view.Dispose();
                _mmf.Dispose();
                _file.Dispose();
                _initializationLifetimeMutex.Dispose();
                GC.SuppressFinalize(this);
            }
        // --------------------------------------------------------------
        // Atomic fetch-and-add for 64-bit: GetAndIncrement
        // --------------------------------------------------------------

        public void Flush(bool flushToDisk = true)
        {
            if (_disposed) return;

            // Flush MMF view to file
            _view.Flush();

            if (flushToDisk)
            {
                // Ensure the file metadata + data are pushed to stable storage
                _file.Flush(flushToDisk: true);
            }
        }

        public long GetAndIncrement64(int index, uint dataLength)
            {
                EnsureNotDisposed();
                ref long slot = ref Int64Slot(index);

                long inc = dataLength;
                long after = Interlocked.Add(ref slot, inc);
                var ret = after - inc; // pre-increment value
                if (ret >= int.MaxValue)
                {
                    Debugger.Launch();
                    Debugger.Break();
                }
                return ret;
            }

            public int GetAndIncrement32(int index, uint dataLength)
            {
                EnsureNotDisposed();
                ref int slot = ref Int32Slot(index);
                int inc = (int)dataLength;
                int after = Interlocked.Add(ref slot, inc);
                return after - inc; // pre-increment value
            }

            public int Increment32(int index)
            {
                EnsureNotDisposed();
                return Interlocked.Increment(ref Int32Slot(index));
            }

            public long Increment64(int index)
            {
                EnsureNotDisposed();
                return Interlocked.Increment(ref Int64Slot(index));
            }
            // --------------------------------------------------------------
            // 32-bit operations
            // --------------------------------------------------------------

            public int Read32(int index)
            {
                EnsureNotDisposed();
                return Volatile.Read(ref Int32Slot(index));
            }
            // --------------------------------------------------------------
            // 64-bit operations
            // --------------------------------------------------------------

            public long Read64(int index)
            {
                EnsureNotDisposed();
                return Interlocked.Read(ref Int64Slot(index));
            }

            public void ReleaseSpinLock32(int index)
            {
                EnsureNotDisposed();
                Volatile.Write(ref Int32Slot(index), 0);
            }
            // --------------------------------------------------------------
            // Spinlock (32-bit)
            // --------------------------------------------------------------

            // 0 = free, 1 = held
            public bool TryAcquireSpinLock32(int index)
            {
                EnsureNotDisposed();
                return Interlocked.CompareExchange(ref Int32Slot(index), 1, 0) == 0;
            }

            public void Write32(int index, int value)
            {
                EnsureNotDisposed();
                Volatile.Write(ref Int32Slot(index), value);
            }

            public void Write64(int index, long value)
            {
                EnsureNotDisposed();
                Volatile.Write(ref Int64Slot(index), value);
            }
        }
}
