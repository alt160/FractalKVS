using Microsoft.Win32.SafeHandles;
using System.Runtime.CompilerServices;

namespace FractalKVS
{

    internal class FileController : IDisposable
    {
        private readonly ThreadLocal<SafeFileHandle?> _tls;
        private SafeFileHandle hFile;

        private readonly System.IO.FileInfo file;
        internal SafeFileHandle masterHandle;
        private bool isNew = false;
        private bool disposedValue;
        private DiskGeometryInfo.DiskGeometry diskGeometry;
        private FractalStoreWriteBehavior writeBehavior = FractalStoreWriteBehavior.Immediate;
        internal AtomicIntegers atomics;
        public FileSyscallCounters syscallPerfTracker { get; } = new FileSyscallCounters();
        // Index tiers share this controller; rebinding on Open updates existing and future tiers without a global target.
        internal BucketTierTelemetry? BucketTelemetry;

        /// <summary>
        /// Establishes a named-file identity and non-null containing directory before performing file I/O.<br/>
        /// Root-only and trailing-directory-separator paths are not controller file identities.<br/>
        /// Actual create/open operations retain responsibility for existence, access, and target usability.<br/>
        /// </summary>
        /// <param name="filePath">A file path, including a terminal filename; relative paths remain supported.<br/></param>
        /// <param name="createIfNotExists">Whether a missing file may be created.<br/></param>
        /// <param name="writeBehavior">Existing controller durability policy.<br/></param>
        /// <param name="preserveAtomic64">Existing persisted atomic slots to preserve when opening the sidecar.<br/></param>
        private FileController(
            string filePath,
            bool createIfNotExists,
            FractalStoreWriteBehavior writeBehavior = FractalStoreWriteBehavior.Deferred,
            int[]? preserveAtomic64 = null)
        {
            file = new System.IO.FileInfo(filePath);
            var directory = file.Directory;
            if (Path.EndsInDirectorySeparator(file.FullName) || string.IsNullOrEmpty(file.Name) || directory is null)
                throw new ArgumentException("A Fractal file path must name a file, not a root or a directory path ending in a separator.", nameof(filePath));
            DirectoryName = directory.FullName;
            if (!file.Exists)
            {
                if (createIfNotExists)
                {
                    isNew = true;
                    file.Create().Close();
                }
                else
                    throw new System.IO.FileNotFoundException($"File '{file.FullName}' not found and createIfNotExists=false.");
            }

            masterHandle = System.IO.File.OpenHandle(file.FullName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.RandomAccess | (writeBehavior == FractalStoreWriteBehavior.Immediate ? FileOptions.WriteThrough : FileOptions.None));
            hFile = masterHandle;
            _tls = new ThreadLocal<SafeFileHandle?>(() =>
            {
                // lazily create per-thread duplicate handle
                return System.IO.File.OpenHandle(file.FullName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.RandomAccess | (writeBehavior == FractalStoreWriteBehavior.Immediate ? FileOptions.WriteThrough : FileOptions.None));
            }, trackAllValues: true);
            diskGeometry = DiskGeometryInfo.Get(directory.Root.FullName);
            this.writeBehavior = writeBehavior;
            var eofHash = Standart.Hash.xxHash.xxHash128.ComputeHash(filePath.ToLower());
            long initialLength = TrackedRandomAccess.GetLength(masterHandle, syscallPerfTracker);

            if (initialLength < 4096)
            {
                TrackedRandomAccess.SetLength(masterHandle, 4096, syscallPerfTracker);
                initialLength = 4096;
            }
            atomics = new AtomicIntegers($"{filePath}.shm", null, preserveAtomic64 ?? [0]);
            atomics.Write64(0, initialLength);
        }


        /// <summary>
        /// Creates or opens a named file after validating its path and containing directory.<br/>
        /// Relative paths and files directly beneath a drive root are supported; root-only and trailing-separator paths are rejected.<br/>
        /// Filesystem operations determine actual target usability and access; validation does not guarantee future availability.<br/>
        /// </summary>
        /// <param name="filePath">A nonblank path including a terminal filename.<br/></param>
        /// <param name="writeBehavior">The durability behavior used by the file handles.<br/></param>
        /// <returns>An initialized controller with fixed file identity and a non-null containing directory.<br/></returns>
        /// <exception cref="ArgumentNullException">filePath is null.<br/></exception>
        /// <exception cref="ArgumentException">filePath is blank, root-only, or ends in a directory separator.<br/></exception>
        internal static FileController CreateOrOpen(
            string filePath,
            FractalStoreWriteBehavior writeBehavior = FractalStoreWriteBehavior.Immediate)
            => CreateOrOpen(filePath, writeBehavior, null);

        /// <summary>
        /// Creates or opens a controlled file while preserving the specified durable 64-bit
        /// atomic slots when the companion atomic map is initialized.<br/>
        /// This overload is internal because atomic-slot allocation is an engine storage
        /// concern and must not become part of the developer-facing file API.<br/>
        /// </summary>
        /// <param name="filePath">The primary file path to create or open.</param>
        /// <param name="writeBehavior">The durability behavior used by the file handles.</param>
        /// <param name="preserveAtomic64">
        /// The exact 64-bit atomic slots whose persisted values must survive initialization,
        /// or <see langword="null"/> to preserve only the logical end-of-file slot.
        /// </param>
        /// <returns>An initialized controller for the requested file.</returns>
        internal static FileController CreateOrOpen(
            string filePath,
            FractalStoreWriteBehavior writeBehavior,
            int[]? preserveAtomic64)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var filePathHash = Standart.Hash.xxHash.xxHash64.ComputeHash(filePath);
            using var mx = new Semutex($"FractalKVS-FileLock-{filePathHash:x16}");
            try
            {
                mx.WaitOne();
                return new FileController(filePath, true, writeBehavior, preserveAtomic64);
            }
            finally
            {
                mx.ReleaseMutex();
            }
        }

        /// <summary>
        /// Opens an existing named file after validating its path and containing directory.<br/>
        /// Relative paths and files directly beneath a drive root are supported; root-only and trailing-separator paths are rejected.<br/>
        /// Filesystem operations determine actual target usability and access; validation does not guarantee future availability.<br/>
        /// </summary>
        /// <param name="filePath">A nonblank path including a terminal filename.<br/></param>
        /// <param name="writeBehavior">The durability behavior used by the file handles.<br/></param>
        /// <returns>An initialized controller with fixed file identity and a non-null containing directory.<br/></returns>
        /// <exception cref="ArgumentNullException">filePath is null.<br/></exception>
        /// <exception cref="ArgumentException">filePath is blank, root-only, or ends in a directory separator.<br/></exception>
        /// <exception cref="FileNotFoundException">No existing file is found at filePath.<br/></exception>
        internal static FileController Open(
            string filePath,
            FractalStoreWriteBehavior writeBehavior = FractalStoreWriteBehavior.Immediate)
            => Open(filePath, writeBehavior, null);

        /// <summary>
        /// Opens an existing controlled file while preserving the specified durable 64-bit
        /// atomic slots when the companion atomic map is initialized.<br/>
        /// Keeping this overload internal prevents storage-layout coordinates from leaking
        /// into the public developer experience.<br/>
        /// </summary>
        /// <param name="filePath">The existing primary file path to open.</param>
        /// <param name="writeBehavior">The durability behavior used by the file handles.</param>
        /// <param name="preserveAtomic64">
        /// The exact 64-bit atomic slots whose persisted values must survive initialization,
        /// or <see langword="null"/> to preserve only the logical end-of-file slot.
        /// </param>
        /// <returns>An initialized controller for the existing file.</returns>
        internal static FileController Open(
            string filePath,
            FractalStoreWriteBehavior writeBehavior,
            int[]? preserveAtomic64)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var filePathHash = Standart.Hash.xxHash.xxHash64.ComputeHash(filePath);
            using var mx = new Semutex($"FractalKVS-FileLock-{filePathHash:x16}");
            try
            {
                mx.WaitOne();
                return new FileController(filePath, false, writeBehavior, preserveAtomic64);
            }
            finally
            {
                mx.ReleaseMutex();
            }
        }

        public bool IsNew => isNew;

        public long Length => atomics.Read64(0);
        public long FileLength => TrackedRandomAccess.GetLength(hFile, syscallPerfTracker);

        internal FractalStoreWriteBehavior WriteBehavior { get => writeBehavior; set => writeBehavior = value; }

        public DiskGeometryInfo.DiskGeometry DiskGeometry => diskGeometry;

        public void Close()
        {
            foreach (var h in _tls.Values) h?.Dispose();
            masterHandle.Dispose();
            _tls.Dispose();
        }
        public void FlushToDisk() => TrackedRandomAccess.FlushToDisk(hFile, syscallPerfTracker);

        public byte[] Read(ulong offset, uint length)
        {
            var data = new byte[length];
            TrackedRandomAccess.Read(hFile, data, (long)offset, syscallPerfTracker);
            return data;
        }
        public Span<byte> ReadSpan(ulong offset, uint length)
        {
            var data = new byte[length];
            TrackedRandomAccess.Read(hFile, data, (long)offset, syscallPerfTracker);
            return data;
        }
        public void Read(Span<byte> data, ulong offset) => TrackedRandomAccess.Read(hFile, data, (long)offset, syscallPerfTracker);
        public void Read(Span<byte> data, ulong offset, uint length) => TrackedRandomAccess.Read(hFile, data.Slice(0, (int)length), (long)offset, syscallPerfTracker);

        public ulong WriteAtEnd(ReadOnlySpan<byte> data)
        {
            var ret = atomics.GetAndIncrement64(0, (uint)data.Length);
            TrackedRandomAccess.Write(hFile, data, ret, syscallPerfTracker);
            return (ulong)ret;
        }
        public void Write(ReadOnlySpan<byte> data, ulong offset)
        {
            TrackedRandomAccess.Write(hFile, data, (long)offset, syscallPerfTracker);
        }

        public void SetLength(ulong length)
        {
            atomics.Write64(0, (long)length);
            TrackedRandomAccess.SetLength(hFile, (long)length, syscallPerfTracker);
        }

        public bool TryTruncateTail(long expectedOldLength, long newLength)
        {
            // expectedOldLength = logical EOF we validated
            // newLength         = new desired EOF (start of the old tail record)
            // If atomics[0] != expectedOldLength, someone appended; abort.
            long original = atomics.CompareExchange64(index: 0, value: newLength, comparand: expectedOldLength);

            if (original != expectedOldLength)
                return false;

            // Win: we own the tail. Now sync the underlying file length.
            TrackedRandomAccess.SetLength(hFile, newLength, syscallPerfTracker);
            return true;
        }

        public bool TryTruncateTail(ulong expectedOldLength, ulong newLength)
            => TryTruncateTail((long)expectedOldLength, (long)newLength);


        /// <summary>
        /// Zeros a range of the file.
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="length"></param>
        public void WriteZero(ulong offset, ulong length)
        {
            // For small writes, use a stack-allocated buffer
            if (length <= 1024)
            {
                Span<byte> zeros = stackalloc byte[(int)length];
                zeros.Clear(); // Fills with zeros
                TrackedRandomAccess.Write(hFile, zeros, (long)offset, syscallPerfTracker);
                return;
            }

            // For larger writes, use a reusable buffer and write in chunks
            const int bufferSize = 65536; // 64KB is a good balance
            var buffer = new byte[Math.Min(bufferSize, (int)length)];
            // buffer is zero-initialized by default

            long remaining = (long)length;
            long position = (long)offset;

            while (remaining > 0)
            {
                int toWrite = (int)Math.Min(remaining, buffer.Length);
                TrackedRandomAccess.Write(hFile, buffer.AsSpan(0, toWrite), position, syscallPerfTracker);
                position += toWrite;
                remaining -= toWrite;
            }
        }

        public bool IsDisposed => disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }
                TrackedRandomAccess.FlushToDisk(hFile, syscallPerfTracker);
                Close();
                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~FileController()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Gets the controller's normalized file path without exposing mutable FileInfo state.<br/></summary>
        internal string FullName => file.FullName;

        /// <summary>Gets the nonempty terminal filename established during controller construction.<br/></summary>
        internal string Name => file.Name;

        /// <summary>Gets the containing directory validated before file I/O; no fallback or repeated path parsing is performed.<br/></summary>
        internal string DirectoryName { get; }

        /// <summary>
        /// Refreshes file metadata and returns the current on-disk length, or zero if the file no longer exists.<br/>
        /// Preserves the store size-reporting behavior without exposing the mutable FileInfo instance.<br/>
        /// </summary>
        /// <returns>The refreshed physical byte count, or zero for a missing file.<br/></returns>
        internal ulong GetFileSize()
        {
            file.Refresh();
            return file.Exists ? (ulong)file.Length : 0;
        }
    }

    internal sealed class FileSyscallCounters
    {
        private long _readCalls;
        private long _writeCalls;
        private long _getLengthCalls;
        private long _setLengthCalls;
        private long _flushCalls;

        public long ReadCalls => Interlocked.Read(ref _readCalls);
        public long WriteCalls => Interlocked.Read(ref _writeCalls);
        public long GetLengthCalls => Interlocked.Read(ref _getLengthCalls);
        public long SetLengthCalls => Interlocked.Read(ref _setLengthCalls);
        public long FlushCalls => Interlocked.Read(ref _flushCalls);

        public long TotalSyscalls =>
            ReadCalls + WriteCalls + GetLengthCalls + SetLengthCalls + FlushCalls;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementRead() => Interlocked.Increment(ref _readCalls);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementWrite() => Interlocked.Increment(ref _writeCalls);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementGetLength() => Interlocked.Increment(ref _getLengthCalls);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementSetLength() => Interlocked.Increment(ref _setLengthCalls);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncrementFlush() => Interlocked.Increment(ref _flushCalls);

        internal void Reset()
        {
            Interlocked.Exchange(ref _readCalls, 0);
            Interlocked.Exchange(ref _writeCalls, 0);
            Interlocked.Exchange(ref _getLengthCalls, 0);
            Interlocked.Exchange(ref _setLengthCalls, 0);
            Interlocked.Exchange(ref _flushCalls, 0);
        }

        /// <summary>Creates engine-owned syscall counters; consumers obtain read-only reporting through their controller or store.<br/></summary>
        internal FileSyscallCounters() { }
    }
}
