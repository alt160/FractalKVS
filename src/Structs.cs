using Standart.Hash.xxHash;
using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;








namespace FractalKVS
{
    //====== TYPES ======
    internal enum BucketEntrySelector : byte
    {
        A = 0,
        B = 1
    }

    //====== TYPES ======
    internal enum Consts : ulong
    {
        FINDEX_MAGIC = 0x465241434B565378, // FRACKVSx
        FDATA_MAGIC = 0x465241434B565364, // FRACKVSd

    }

    /// <summary>Defines how a Fractal index allocates bucket counts for expanded tiers.<br/></summary>
    public enum FanOutAlgorithms : uint
    {
        /// <summary>
        /// Tier-1 at 4x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow2xChain4x,
        /// <summary>
        /// Tier-1 at 8x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow4xChain4x,
        /// <summary>
        /// Tier-1 at 8x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow8xChain4x,
        /// <summary>
        /// Tier-1 at 16x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow16xChain4x,
        /// <summary>
        /// Tier-1 at 32x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow32xChain4x,
        /// <summary>
        /// Tier-1 at 64x of base, then 4x of MaxChainCount thereafter
        /// </summary>
        Pow64xChain4x,
        /// <summary>
        /// Tier-1 at 8x of base, then 256 buckets per tier thereafter
        /// </summary>
        Pow8x256,
        /// <summary>
        /// Next tier count is x8 at tier 1, x4 at tier 2, x2 at tier 3, then power-of-2 thereafter
        /// </summary>
        Pow8422,
        /// <summary>
        /// Next tier count is nearest power-of-2 after multiplying previous tier by 1.5
        /// </summary>
        Pow15,
        /// <summary>
        /// Next tier count is result of multiplying previous tier by 2
        /// </summary>
        Pow2,
        /// <summary>
        /// Next tier count is result of multiplying previous tier by 3
        /// </summary>
        Pow3,
        /// <summary>
        /// Next tier count is result of multiplying previous tier by 4
        /// </summary>
        Pow4,
        /// <summary>
        /// Next tier count is result of multiplying previous tier by 8
        /// </summary>
        Pow8,
        /// <summary>
        /// Next tier count is fixed at 64 buckets
        /// </summary>
        Fixed64,
        /// <summary>
        /// Next tier count is fixed at 128 buckets
        /// </summary>
        Fixed128,
        /// <summary>
        /// Next tier count is fixed at 256 buckets
        /// </summary>
        Fixed256,
        /// <summary>
        /// Next tier count is fixed at 1024 buckets
        /// </summary>
        Fixed1k,
        /// <summary>
        /// Next tier count is fixed at 4096 buckets
        /// </summary>
        Fixed2k,
        /// <summary>
        /// Next tier count is fixed at 8192 buckets
        /// </summary>
        Fixed4k,
        /// <summary>
        /// Next tier count is fixed at 8192 buckets
        /// </summary>
        Fixed8k,
        /// <summary>
        /// Next tier count is fixed at 16384 buckets
        /// </summary>
        Fixed16k,
        /// <summary>
        /// Next tier count is fixed at 32768 buckets
        /// </summary>
        Fixed32k,
        /// <summary>
        /// Next tier count is fixed at 65536 buckets
        /// </summary>
        Fixed64k,
    }

    internal enum KeyTypes : uint
    {
        SByte,
        Byte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Int128,
        UInt128
    }

    internal enum RecordInfo : byte
    {
        OneRecord = 0,
        TwoRecords = 1,
        Unused = 2,
        Reserved = 3
    }








    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BucketEntry
    {
        private ulong _OffsetA;
        private ulong _OffsetB;
        public uint SizeA;
        public uint SizeB;
        public static uint SizeOf = 8 + 8 + 4 + 4;








        //======  PROPERTIES  ======
        public BucketEntrySelector ABReadSelector
        {
            get => ((_OffsetA >> 62) & 1ul) == 0 ? BucketEntrySelector.A : BucketEntrySelector.B;
            set => _OffsetA = (_OffsetA & ~(1ul << 62)) | ((value == BucketEntrySelector.A ? 0ul : 1ul) << 62);
        }
        public bool IsChainedBackref
        {
            get => ((_OffsetA >> 61) & 0x1ul) != 0;
            set => _OffsetA = (_OffsetA & ~(1ul << 61)) | ((value ? 1ul : 0ul) << 61);
        }
        public bool HasSingleRecord
        {
            get => ((_OffsetA >> 60) & 0x1ul) != 0;
            set => _OffsetA = (_OffsetA & ~(1ul << 60)) | ((value ? 1ul : 0ul) << 60);
        }
        public bool IsExpanded
        {
            get => (_OffsetA & (1ul << 63)) != 0;
            set
            {
                if (value)
                    _OffsetA |= (1ul << 63);
                else
                    _OffsetA &= ~(1ul << 63);
            }
        }
        public bool IsUnset
        {
            get => (_OffsetA == 0 && _OffsetB == 0 && SizeA == 0 && SizeB == 0);
        }
        public ulong OffsetA { get => _OffsetA & FIndex.OFFSET_MASK; set => _OffsetA = _OffsetA & FIndex.FLAGS_MASK | value & FIndex.OFFSET_MASK; }
        public ulong OffsetB { get => _OffsetB & FIndex.OFFSET_MASK; set => _OffsetB = _OffsetB & FIndex.FLAGS_MASK | value & FIndex.OFFSET_MASK; }
        public ulong RecordOffset
        {
            get => ABReadSelector == BucketEntrySelector.A ? OffsetA : OffsetB;
        }
        public uint RecordSize
        {
            get => ABReadSelector == BucketEntrySelector.A ? SizeA : SizeB;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BucketTierHeader
    {
        public ushort Tier;
        public ushort Flags;
        public uint BucketCount;
        public ulong Checksum;
        public static uint SizeOf = 16;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct FDataHeader
    {
        public ulong Magic;
        public ulong Version;
        public uint PageSizeLogical;
        public uint PageSizePhysical;
        public ulong Reserved1;
        public ulong Reserved2;
        public Guid Uuid;
        public long CreatedTicks;
        public ulong UserData1;
        public ulong UserData2;
        public ulong UserData3;
        public uint FileNameOffset;
        public ushort FileNameLength;
        public uint FilePathOffset;
        public ushort FilePathLength;
        public uint FirstRecordOffset;
        public ulong HeaderChecksum;








        //======  PROPERTIES  ======
        public static uint SizeOf => 8 + 8 + 4 + 4 + 8 + 8 + 16 + 8 + 8 + 8 + 8 + 4 + 2 + 4 + 2 + 4 + 8;








        //------ Public Methods -----
        //======  METHODS  ======
        public ulong CalculateChecksum()
        {
            // convert this to span<byte>
            var origChecksum = HeaderChecksum;
            try
            {
                HeaderChecksum = 0;
                var data = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
                return xxHash64.ComputeHash(data, data.Length - 8); // minus last 8 bytes so that checksum is not included
            }
            finally
            {
                HeaderChecksum = origChecksum;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct FDataVacancy
    {
        public ulong Offset; // absolute file offset of first free byte
        public uint Reserved;  // reserved to align to 64-bit
        public uint Size;    // size in bytes of free region








        //======  FIELDS  ======
        public const int SizeOf = 16; // 8 + 4 + 4
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct FIndexHeader
    {
        public ulong Magic;
        public ulong Version;
        public uint PageSizeLogical;
        public uint PageSizePhysical;
        public uint RootBucketCount;
        public Guid Uuid;
        public KeyTypes KeyType;
        public long CreatedTicks;
        public FanOutAlgorithms FanOutAlgorithm;
        public ulong Reserved1;
        public ulong UserData1;
        public ulong UserData2;
        public uint FileNameOffset;
        public ushort FileNameLength;
        public uint FilePathOffset;
        public ushort FilePathLength;
        public uint RootBucketOffset;
        public ulong HeaderChecksum;
        public static uint SizeOf = 8 + 8 + 4 + 4 + 4 + 16 + 4 + 8 + 4 + 8 + 8 + 8 + 4 + 2 + 4 + 2 + 4 + 8;








        //------ Public Methods -----
        //======  METHODS  ======
        public ulong CalculateChecksum()
        {
            // convert this to span<byte>
            var origChecksum = HeaderChecksum;
            try
            {
                HeaderChecksum = 0;
                var data = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
                return xxHash64.ComputeHash(data, data.Length - 8); // minus last 8 bytes so that checksum is not included
            }
            finally
            {
                HeaderChecksum = origChecksum;
            }
        }
    }

    /// <summary>Represents one Fractal record whose payload may be backed by a rented buffer.<br/></summary>
    public struct Record : IDisposable
    {
        //======  FIELDS  ======
        private Memory<byte> _data;
        private uint _dataLength;
        private bool _disposed = false;








        //======  CONSTRUCTORS  ======
        /// <summary>Initializes a record over engine-owned bytes containing a header and payload.<br/></summary>
        public Record(Memory<byte> data, uint dataLength)
        {
            _data = data;
            _dataLength = dataLength;
        }








        //======  PROPERTIES  ======
        /// <summary>Gets the payload memory excluding Fractal's record header.<br/></summary>
        public Memory<byte> Data
        {
            get => _data.Slice(RecordHeader.SizeOf, (int)_dataLength);
        }
        /// <summary>Gets a writable span over the payload while this record remains undisposed.<br/></summary>
        public Span<byte> DataSpan
        {
            get => _data.Span.Slice(RecordHeader.SizeOf, (int)_dataLength);
        }
        /// <summary>Gets the persisted header associated with this record.<br/></summary>
        public ref RecordHeader Header
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException("Record");
                return ref Unsafe.As<byte, RecordHeader>(ref _data.Span[0]);
            }
        }
        /// <summary>Gets whether the rented payload buffer has been returned.<br/></summary>
        public bool IsDisposed
        {
            get => _disposed;
        }

        /// <summary>Gets whether this value represents no record bytes.<br/></summary>
        public bool IsEmpty => Header.ID == 0 && _data.IsEmpty;







        // standard IDisposable pattern and IsDisposed property
        //------ Public Methods -----
        //======  METHODS  ======
        /// <summary>Returns any rented backing buffer and invalidates borrowed payload access.<br/></summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_data.Length == 0) return;
            _dataLength = 0;
            MemoryMarshal.TryGetArray<byte>(_data, out var dataBytes);
            ArrayPool<byte>.Shared.Return(dataBytes.Array!);
        }
    }

    /// <summary>Describes the persisted identity, payload size, checksums, and mutation links for a record.<br/></summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RecordHeader
    {
        /// <summary>
        /// If this value is zero when other fields are non-zero, it represents a deleted/dead record.<br/>
        /// </summary>
        public ulong ID;
        /// <summary>Gets or sets the next collision-chain record offset, or zero when none exists.<br/></summary>
        public ulong NextRecordOffset;
        /// <summary>
        /// The offset of the prior mutation record for this key, or zero if this record instance has had no mutations yet.<br/>
        /// </summary>
        public ulong PriorMutationOffset;
        /// <summary>Gets or sets the checksum of the current payload bytes.<br/></summary>
        public ulong DataChecksum;
        /// <summary>
        /// Size of the next record in the chain, or zero if this is the last record.<br/>
        /// This value is used to avoid 2 syscalls (header then data) when reading chained records.
        /// </summary>
        public uint NextRecordSize;
        /// <summary>
        /// Size of the prior mutation record for this key, or zero if this record instance has had no mutations yet.<br/>
        /// </summary>
        public uint PriorMutationSize;

        /// <summary>
        /// Size of the entire record including header, data, and trailing size (ulong) value, in bytes.<br/>
        /// </summary>
        public uint RecordSize;
        /// <summary>
        /// Size of the data payload in bytes.<br/>
        /// </summary>
        public uint DataLength;
        // data bytes immediately follow
        /// <summary>Gets the serialized header size in bytes.<br/></summary>
        public static int SizeOf = 8 + 8 + 8 + 8 + 4 + 4 + 4 + 4;
    }








    internal class BucketTier
    {
        //Shared/Static Members
        internal static bool CycleDebugEnabled;
        internal static Action<string>? CycleDebugLogger;

        public static void ConfigureCycleDebug(bool enabled, Action<string>? logger = null)
        {
            CycleDebugEnabled = enabled;
            CycleDebugLogger = logger;
        }

        //======  FIELDS  ======
        private FileController fc;
        private ulong fileOffest;








        public BucketTierHeader Header;
        public BufferStream Tier;








        //======  CONSTRUCTORS  ======
        public BucketTier(BufferStream tierData, ulong fileOffset, FileController fc)
        {
            Tier = tierData;
            Header = MemoryMarshal.Read<BucketTierHeader>(tierData.AsReadOnlySpan.Slice(0, (int)BucketTierHeader.SizeOf));
            fileOffest = fileOffset;
            this.fc = fc;
        }








        // operator to return as readonlyspan
        //======  PROPERTIES  ======
        public ReadOnlySpan<byte> AsReadOnlySpan => Tier.AsReadOnlySpan;
        public long Length => Tier.Length;
        public ulong TierOffset { get => fileOffest; }








        //------ Public Methods -----
        //======  METHODS  ======
        public void Erase()
        {
            Tier.Zero(BucketTierHeader.SizeOf, (uint)Tier.Length - BucketTierHeader.SizeOf);
        }

        public BucketEntry GetEntry(ulong index)
        {
            int entryOffset = (int)(BucketTierHeader.SizeOf + (index * BucketEntry.SizeOf));
            return MemoryMarshal.Read<BucketEntry>(Tier.ReadOnlySpan(entryOffset, (int)BucketEntry.SizeOf));
        }

        public ref readonly BucketEntry GetEntryRef(ulong index)
        {
            int entryOffset = (int)(BucketTierHeader.SizeOf + (index * BucketEntry.SizeOf));
            return ref MemoryMarshal.AsRef<BucketEntry>(Tier.AsWritableSpan.Slice(entryOffset, (int)BucketEntry.SizeOf));
        }

        public void LoadTier()
        {
            fc.Read(data: Tier.AsWritableSpan, offset: fileOffest);
            Header = MemoryMarshal.Read<BucketTierHeader>(Tier.AsReadOnlySpan.Slice(0, (int)BucketTierHeader.SizeOf));
        }

        public void SaveTier()
        {
            fc.Write(data: Tier.AsReadOnlySpan, offset: fileOffest);
        }

        public void SetEntries(Span<(ulong index, BucketEntry entry)> entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var (index, entry) = entries[i];
                SetEntry(index, entry);
            }
        }

        public void UnsetEntry(ulong index)
        {

            uint entryOffset = (uint)(BucketTierHeader.SizeOf + (index * BucketEntry.SizeOf));

            Span<byte> zeros = stackalloc byte[(int)BucketEntry.SizeOf];

            Tier.Zero(entryOffset, BucketEntry.SizeOf);

            fc.Write(zeros, fileOffest + entryOffset);
            fc.BucketTelemetry?.RecordClear(this.TierOffset, index);
        }

        private void ValidateCycle(ulong bucketIndex, in BucketEntry entry)
        {
            if (!CycleDebugEnabled)
                return;

            if (entry.IsExpanded && entry.RecordOffset == TierOffset)
            {
                var message = $"Tier cycle detected: tier 0x{TierOffset:X} bucket {bucketIndex} points to its own tier.";
                CycleDebugLogger?.Invoke(message);
                throw new InvalidDataException(message);
            }
        }

        public void SetEntry(ulong index, BucketEntry entry)
        {
            int entryOffset = (int)(BucketTierHeader.SizeOf + (index * BucketEntry.SizeOf));
            // determine write lock bucket (2048) from index.  use pow2 modulo of lowest 9 bits.
            ulong mixed = FIndex.HashRecordID((this.TierOffset << 32) | index);
            int lockBucket = (int)(mixed & 0x1FF) + 1;

            ValidateCycle(index, entry);

            MemoryMarshal.Write(Tier.AsWritableSpan.Slice(entryOffset, (int)BucketEntry.SizeOf), in entry);
            fc.Write(data: Tier.AsReadOnlySpan.Slice(entryOffset, (int)BucketEntry.SizeOf), offset: fileOffest + (ulong)entryOffset);

            fc.BucketTelemetry?.RecordWrite(this.TierOffset, index);

        }

        internal void SetEntryUnlocked(ulong index, in BucketEntry entry)
        {
            int entryOffset = (int)(BucketTierHeader.SizeOf + (index * BucketEntry.SizeOf));

            ValidateCycle(index, entry);

            // No per-bucket spinlock. Caller must guarantee exclusive access
            // (e.g., root-bucket stripe lock already held for this chain).
            MemoryMarshal.Write(
                Tier.AsWritableSpan.Slice(entryOffset, (int)BucketEntry.SizeOf),
                in entry
            );

            fc.Write(
                data: Tier.AsReadOnlySpan.Slice(entryOffset, (int)BucketEntry.SizeOf),
                offset: fileOffest + (ulong)entryOffset
            );
        }

        public void SetLength(long length)
        {
            Tier.SetLength(length);
        }

        public void UpdateHeader()
        {
            MemoryMarshal.Write(Tier.AsWritableSpan.Slice(0, (int)BucketTierHeader.SizeOf), in Header);
            fc.Write(data: Tier.AsReadOnlySpan.Slice(0, (int)BucketTierHeader.SizeOf), offset: fileOffest);
        }

        public void UpdateHeader(BucketTierHeader newHeader)
        {
            Header = newHeader;
            MemoryMarshal.Write(Tier.AsWritableSpan.Slice(0, (int)BucketTierHeader.SizeOf), in Header);
            fc.Write(data: Tier.AsReadOnlySpan.Slice(0, (int)BucketTierHeader.SizeOf), offset: fileOffest);
        }
    }
}
