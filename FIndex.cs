using Standart.Hash.xxHash;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;








namespace FractalKVS
{
    //====== TYPES ======
    /// <summary>Thrown when a store file is opened from a location other than its persisted ownership location.<br/></summary>
    public class FileCopiedOrMovedException : Exception
    {
        //======  PROPERTIES  ======
        /// <summary>Gets the directory path recorded in the file header.<br/></summary>
        public string? StoredPath { get; }
        /// <summary>Gets the filename recorded in the file header.<br/></summary>
        public string? StoredName { get; }
        /// <summary>Gets the directory path used for the attempted open.<br/></summary>
        public string? CurrentPath { get; }
        /// <summary>Gets the filename used for the attempted open.<br/></summary>
        public string? CurrentName { get; }
        /// <summary>Gets the affected file's persisted identity when available.<br/></summary>
        public Guid? FileUuid { get; }
        /// <summary>Gets whether the affected file is the Fractal index file.<br/></summary>
        public bool IsIndexFile { get; }
        /// <summary>Gets whether the affected file is the Fractal data file.<br/></summary>
        public bool IsDataFile { get; }

        //======  CONSTRUCTORS  ======
        /// <summary>Initializes the exception with a caller-supplied diagnostic message.<br/></summary>
        /// <param name="message">Explanation of the ownership-location failure.<br/></param>
        public FileCopiedOrMovedException(string message) : base(message)
        {
        }

        /// <summary>Initializes the exception with a diagnostic message and its originating exception.<br/></summary>
        /// <param name="message">Explanation of the ownership-location failure.<br/></param>
        /// <param name="innerException">Underlying failure that caused this exception to be raised.<br/></param>
        public FileCopiedOrMovedException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>Initializes the exception from the stored and attempted file names when directory metadata is unavailable.<br/></summary>
        /// <param name="fileNameInFile">File name recorded in the persisted header.<br/></param>
        /// <param name="fileName">File name used for the attempted open.<br/></param>
        public FileCopiedOrMovedException(string? fileNameInFile, string? fileName)
            : this(null, fileNameInFile, null, fileName, null, false)
        {
        }

        /// <summary>Initializes the exception from persisted and attempted ownership metadata.<br/></summary>
        /// <param name="storedPath">Directory path recorded in the persisted header.<br/></param>
        /// <param name="storedName">File name recorded in the persisted header.<br/></param>
        /// <param name="currentPath">Directory path used for the attempted open.<br/></param>
        /// <param name="currentName">File name used for the attempted open.<br/></param>
        /// <param name="fileUuid">Persisted file identity when available.<br/></param>
        /// <param name="isIndexFile">Whether the affected file is the index sidecar.<br/></param>
        public FileCopiedOrMovedException(string? storedPath, string? storedName, string? currentPath, string? currentName, Guid? fileUuid, bool isIndexFile)
            : base(BuildMessage(storedPath, storedName, currentPath, currentName, fileUuid, isIndexFile))
        {
            StoredPath = storedPath;
            StoredName = storedName;
            CurrentPath = currentPath;
            CurrentName = currentName;
            FileUuid = fileUuid;
            IsIndexFile = isIndexFile;
            IsDataFile = !isIndexFile;
        }

        private static string BuildMessage(string? storedPath, string? storedName, string? currentPath, string? currentName, Guid? fileUuid, bool isIndexFile)
        {
            var kind = isIndexFile ? "index" : "data";
            var storedPathDisplay = storedPath ?? "?";
            var storedNameDisplay = storedName ?? "?";
            var currentPathDisplay = currentPath ?? "?";
            var currentNameDisplay = currentName ?? "?";
            var uuidPart = fileUuid.HasValue ? $" FileUuid={fileUuid}" : string.Empty;
            return $"Fractal {kind} file appears to have been copied or moved. Stored header path='{storedPathDisplay}', name='{storedNameDisplay}'; current path='{currentPathDisplay}', name='{currentNameDisplay}'. Call TakeOwnership(...) to rehome before opening.{uuidPart}";
        }
    }

    internal class FIndex
    {
        //Shared/Static Members
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int FastLog2(uint v) => BitOperations.TrailingZeroCount(v);

        internal static ulong Mix13(ulong x)
        {
            x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            x = x ^ (x >> 31);
            return x;

        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong ReverseBits(ulong x)
        {
            x = ((x & 0x5555555555555555UL) << 1) | ((x >> 1) & 0x5555555555555555UL);
            x = ((x & 0x3333333333333333UL) << 2) | ((x >> 2) & 0x3333333333333333UL);
            x = ((x & 0x0F0F0F0F0F0F0F0FUL) << 4) | ((x >> 4) & 0x0F0F0F0F0F0F0F0FUL);
            x = ((x & 0x00FF00FF00FF00FFUL) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFUL);
            x = ((x & 0x0000FFFF0000FFFFUL) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFUL);
            x = (x << 32) | (x >> 32);
            return x;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong HashRecordID(ulong x)
        {
            return Mix13(x);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong MurmurID(ulong x)
        {
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return x;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong WyHash64(ulong x)
        {
            x ^= x >> 32;
            x *= 0xdaba0b6eb09322e3UL;
            x ^= x >> 32;
            x *= 0xc3a5c85c97cb3127UL;
            x ^= x >> 32;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReMix13(ulong x)
        {
            // First mix.
            ulong h0 = Mix13(x);

            // Extract the upper part after removing the strongest 12 bits.
            ulong upper = h0 >> 12;

            // Re-mix the upper portion.
            ulong h1 = Mix13(upper);

            // Stitch:
            //   lower 12 bits from h0  (already strong)
            //   upper bits from h1 << 12 (freshly avalanche-whitened)
            return (h0 & 0xFFFUL) | (h1 << 12);
        }



        // inside FIndex class
        internal BucketExpansionTelemetry BucketExpansions { get; } = new BucketExpansionTelemetry();

        internal long BucketExpansionCount => BucketExpansions.TotalExpansions;



        //======  FIELDS  ======
        internal const ulong FLAGS_MASK = 0b11111111_00000000_00000000_00000000_00000000_00000000_00000000_0000000ul;
        /// <summary>
        /// Offsets in hash buckets only use 56 bits.<br/>
        /// Bits indexes 0-55 are used for the actual offset value.<br/>
        /// </summary>
        //======  FIELDS  ======
        internal const ulong OFFSET_MASK = 0b00000000_11111111_11111111_11111111_11111111_11111111_11111111_1111111ul;
        private const uint DefaultRootTierOffset = 4096;








        // Deliberately deferred: construction also supports offline ownership adoption; successful Init establishes both fields.
        private ConcurrentDictionary<ulong, (BucketTier tier, DateTime insertTime)> bucketTiers = null!;
        readonly FractalStoreConfiguration config;
        private FIndexHeader header;
        private BucketTier rootBuckets = null!;


        public FIndexHeader Header { get => header; }





        internal FileController fc;






        //======  CONSTRUCTORS  ======
        public FIndex(string folderPath, string name, FractalStoreConfiguration config)
        {
            this.config = config;
            var fifx = new System.IO.FileInfo($"{folderPath}\\{name}.fractX");
            if (!fifx.Exists)
                if (config.CreateIfNotExists)
                    fc = FileController.CreateOrOpen(
                        $"{folderPath}\\{name}.fractX",
                        config.WriteBehavior,
                        FractalStore.CreatePersistedIndexAtomicSlotList());
                else
                    throw new System.IO.FileNotFoundException($"File '{fifx.FullName}' not found and config.CreateIfNotExists=false.");
            else
                fc = FileController.Open(
                    $"{folderPath}\\{name}.fractX",
                    config.WriteBehavior,
                    FractalStore.CreatePersistedIndexAtomicSlotList());
        }






        /// <summary>
        /// Initializes a new <see cref="FIndex"/> from an already opened <see cref="FileController"/> instance.<br/>
        /// This constructor does not call <see cref="Init"/>; callers must invoke it explicitly.<br/>
        /// </summary>
        /// <param name="controller">The file controller bound to the index file.<br/></param>
        /// <param name="config">The active Fractal store configuration.<br/></param>
        private FIndex(FileController controller, FractalStoreConfiguration config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            fc = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <summary>
        /// Creates a new <see cref="FIndex"/> bound to a specific index file path (including extension).<br/>
        /// This is intended for maintenance workflows like rebuild-and-swap.<br/>
        /// </summary>
        /// <param name="filePath">Full path to the index file to create or open.<br/></param>
        /// <param name="config">The active Fractal store configuration.<br/></param>
        /// <returns>A new <see cref="FIndex"/> instance bound to the specified file path.<br/></returns>
        internal static FIndex CreateForPath(string filePath, FractalStoreConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));
            if (config is null)
                throw new ArgumentNullException(nameof(config));

            var controller = FileController.CreateOrOpen(
                filePath,
                config.WriteBehavior,
                FractalStore.CreatePersistedIndexAtomicSlotList());
            return new FIndex(controller, config);
        }

        public void ClearTierCache()
        {
            bucketTiers.Clear();
        }


        //======  METHODS  ======
        internal (BucketTier tier, DateTime insertTime) LoadTier(ulong offset)
        {
            //while (bucketTiers.Values.Sum(x => x.tier.Length) > config.MaxCachedIndexTierMB * 1024 * 1024)
            //{
            //    // evict oldest tier
            //    var oldest = bucketTiers.OrderBy(kv => kv.Value.insertTime).First();
            //    bucketTiers.TryRemove(oldest.Key, out _);
            //}

            return bucketTiers.GetOrAdd(offset, k =>
            {
                // load Tier Header
                Span<byte> headerBytes = stackalloc byte[(int)BucketTierHeader.SizeOf];
                fc.Read(headerBytes, offset);
                var header = MemoryMarshal.Read<BucketTierHeader>(headerBytes);

                int initialCapacity = (int)(header.BucketCount * BucketEntry.SizeOf + BucketTierHeader.SizeOf);
                var ret = new BufferStream(initialCapacity);
                ret.SetLength(initialCapacity);
                fc.Read(ret.AsWritableSpan, offset, header.BucketCount * BucketEntry.SizeOf + BucketTierHeader.SizeOf);
                return (new BucketTier(ret, offset, fc), DateTime.UtcNow);
            });

        }








        internal int CalcShiftForHash(int tierLevel)
        {
            int shift = 0;
            uint bucketCount = 0;
            for (short i = 0; i <= tierLevel; i++)
            {
                bucketCount = CalcBucketCountForTier(i);
                shift += FastLog2(bucketCount);
            }
            return shift;
        }
        internal uint CalcBucketCountForTier(int tierLevel)
        {
            uint bucketCount = header.RootBucketCount;
            for (int i = 0; i <= tierLevel; i++)
            {
                switch (config.IndexGrowthStrategy)
                {
                    case FanOutAlgorithms.Pow2xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 2; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow4xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 4; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow8xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 8; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow16xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 16; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow32xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 32; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow64xChain4x:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 64; break;
                            case 2: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                            default: bucketCount = (uint)config.MaxChainedRecords * 4; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow8x256:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 8; break;
                            case 2: bucketCount = 256; break;
                            default: bucketCount = 256; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow8422:
                        switch (i)
                        {
                            case 0: break;
                            case 1: bucketCount = bucketCount * 8; break;
                            case 2: bucketCount = bucketCount * 4; break;
                            default: bucketCount = bucketCount * 2; break;
                        }
                        break;
                    case FanOutAlgorithms.Pow15:
                        // ensure nextTier is change to the closest next power of 2 value.
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = (uint)(1.5 * bucketCount).NearestPowerOfTwo();
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Pow2:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = bucketCount * 2;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Pow3:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = (uint)((double)(bucketCount * 3)).NearestPowerOfTwo();
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Pow4:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = bucketCount * 4;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Pow8:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = bucketCount * 8;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed64:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 64;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed128:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 128;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed256:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 256;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed1k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 1024;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed2k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 4096;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed4k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 4096;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed8k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 8192;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed16k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 1 << 14;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed32k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 1 << 15;
                                break;
                        }
                        break;
                    case FanOutAlgorithms.Fixed64k:
                        switch (i)
                        {
                            case 0:
                                break;
                            default:
                                bucketCount = 1 << 16;
                                break;
                        }
                        break;
                    default:
                        throw new NotImplementedException($"FanOutAlgorithms '{config.IndexGrowthStrategy}' not implemented.");
                }
            }
            return bucketCount;
        }

        //======  METHODS  ======

        private (BucketTier tier, ulong bucketIndex, BucketEntry bucket) LocateBucket(ulong hash)
        {
            uint count = header.RootBucketCount;
            var tier = rootBuckets;
            int shift = 0;
            var tierLevel = 0;

            while (true)
            {
                ulong idx = (hash >> shift) & (count - 1);

                var bucketEntry = tier.GetEntryRef(idx);

                // Case 1: normal expansion (this bucket points to a child tier)
                if (bucketEntry.IsExpanded)
                {
                    var child = LoadTier(bucketEntry.RecordOffset);
                    tier = child.tier;

                    shift += FastLog2(count);
                    count = tier.Header.BucketCount;
                    tierLevel++;
                    continue;
                }

                // Case 2: chained backref
                if (bucketEntry.IsChainedBackref)
                {
                    ulong anchorBucketIndex = bucketEntry.OffsetA;
                    var anchorEntry = tier.GetEntryRef(anchorBucketIndex);

                    // We still don't allow backref -> backref cycles.
                    if (anchorEntry.IsChainedBackref)
                        throw new InvalidOperationException("Invalid backref chain: backref pointing to another backref.");

                    if (anchorEntry.IsExpanded)
                    {
                        // The anchor bucket has itself been expanded since this backref was created.
                        // Follow that expansion to the child tier and continue the hash walk there.
                        var child = LoadTier(anchorEntry.RecordOffset);
                        tier = child.tier;

                        shift += FastLog2(count);
                        count = tier.Header.BucketCount;
                        tierLevel++;
                        continue;
                    }

                    // Anchor is a normal direct data entry; treat it as the canonical bucket.
                    return (tier, anchorBucketIndex, anchorEntry);
                }

                // Case 3: normal direct or unset bucket
                return (tier, idx, bucketEntry);
            }
        }




        //------ Public Methods -----
        /// <summary>
        /// Attempts to set a root-tier bucket entry only when the slot is currently unset.<br/>
        /// Returns <c>true</c> when the entry was applied; otherwise <c>false</c>.<br/>
        /// </summary>
        /// <param name="bucketIndex">The root-tier bucket index.<br/></param>
        /// <param name="entry">The bucket entry to set.<br/></param>
        internal bool TrySetRootEntryIfUnset(ulong bucketIndex, in BucketEntry entry)
        {
            var existing = rootBuckets.GetEntry(bucketIndex);
            if (!existing.IsUnset)
                return false;

            rootBuckets.SetEntry(bucketIndex, entry);
            return true;
        }


        public void DeleteAllKeys()
        {
            var spin = new SpinWait();
            while (!fc.atomics.TryAcquireSpinLock32(600))
                spin.SpinOnce();

            try
            {
                rootBuckets.Erase();
                fc.Write(rootBuckets.AsReadOnlySpan, header.RootBucketOffset);
                fc.SetLength(header.RootBucketOffset + (ulong)rootBuckets.Length);
                bucketTiers.Clear();
            }
            finally
            {
                fc.atomics.ReleaseSpinLock32(600);
            }
            // simply zero out root tier

        }

        /// <summary>
        /// Resolve a chained-backref bucket entry (IsChainedBackref == true) to its
        /// anchor bucket in the same tier. If the entry is not a backref, the original
        /// bucketIndex and entry are returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal (ulong anchorBucketIndex, BucketEntry anchorEntry) ResolveChainedBackref(
            BucketTier tier,
            ulong bucketIndex,
            BucketEntry entry)
        {
            if (!entry.IsChainedBackref)
                return (bucketIndex, entry);

            ulong anchorBucketIndex = entry.OffsetA;
            var anchorEntry = tier.GetEntryRef(anchorBucketIndex);

            if (anchorEntry.IsChainedBackref)
                throw new InvalidOperationException("Invalid backref chain: backref pointing to another backref.");

            if (anchorEntry.IsExpanded)
                throw new InvalidOperationException("Invalid backref: backref pointing to an expanded (child-tier) entry.");

            return (anchorBucketIndex, anchorEntry);
        }

        /// <summary>
        /// Write a BucketEntry into the in-memory tier buffer without issuing any file IO.
        /// Used during expansion seeding; the caller is responsible for calling SaveTier().
        /// </summary>
        private static void WriteBucketEntryInMemory(BucketTier tier, ulong bucketIndex, BucketEntry entry)
        {
            int offset = (int)(BucketTierHeader.SizeOf + bucketIndex * BucketEntry.SizeOf);
            var span = tier.Tier.AsWritableSpan.Slice(offset, (int)BucketEntry.SizeOf);
            System.Runtime.InteropServices.MemoryMarshal.Write(span, in entry);
        }



        private uint bucketExpansionCount = 0;
        // FIndex.cs
        public ulong ExpandBucket(BucketTier parentTier, ulong parentBucketIndex, BucketEntry parentEntry, ulong hash)
        {
            // Allocate a new tier at EOF using the atomic EOF counter.
            // This method:
            //  - decides the new bucket count for the child tier,
            //  - reserves file space via fc.atomics.GetAndIncrement64(0, newTierSize),
            //  - allocates and zeroes an in-memory BucketTier,
            //  - initializes the in-memory BucketTierHeader (Tier, BucketCount).
            //
            // It does NOT:
            //  - write anything to the index file
            //  - redistribute any records
            //  - flip the parent entry to IsExpanded
            //
            // Caller (FractalStore.ExpandAndReindex) is responsible for:
            //  - seeding bucket entries (anchor + backrefs) into the new tier,
            //  - SaveTier() once, as a single write syscall,
            //  - updating the parent bucket entry.

            // 1. Decide the new tier bucket count.
            uint newTierCount = CalcBucketCountForTier((ushort)(parentTier.Header.Tier + 1));

            uint newTierSize = (uint)(newTierCount * BucketEntry.SizeOf + BucketTierHeader.SizeOf);

            // 2. Reserve space in the index file using the atomic EOF counter.
            //    Slot 0 is the shared "EOF" counter for this file.
            long reservedStart = fc.atomics.GetAndIncrement64(0, newTierSize);
            ulong newTierOffset = (ulong)reservedStart;

            // 3. Allocate in memory and initialize header (in-memory only).
            var tierData = new BufferStream(newTierSize);
            tierData.SetLength(newTierSize);

            var newTier = new BucketTier(tierData, newTierOffset, fc);
            newTier.Erase(); // zero entire tier buffer

            // Initialize header struct (Tier, BucketCount, Checksum) in memory.
            newTier.Header.BucketCount = newTierCount;
            newTier.Header.Tier = (ushort)(parentTier.Header.Tier + 1);
            newTier.Header.Flags = 0;
            newTier.Header.Checksum = 0; // not currently used

            // Write the header into the in-memory buffer only (no syscalls here).
            var headerSpan = newTier.Tier.AsWritableSpan.Slice(0, (int)BucketTierHeader.SizeOf);
            System.Runtime.InteropServices.MemoryMarshal.Write(headerSpan, in newTier.Header);

            // 4. Cache the in-memory tier so LoadTier(newTierOffset) will hit memory
            //    and not try to read a not-yet-flushed tier from disk.
            bucketTiers.TryAdd(newTierOffset, (newTier, DateTime.UtcNow));

            bucketExpansionCount++;

            // 5. Telemetry: record this expansion.
            BucketExpansions.Record(parentTier, parentBucketIndex);

            return newTierOffset;
        }


        public BucketEntry GetBucketEntry(ulong recordID)
        {
            ulong hash = HashRecordID(recordID);
            var (_, _, bucket) = LocateBucket(hash);
            return bucket;
        }

        public (BucketTier tier, ulong bucketIndex, BucketEntry bucket) GetBucketEntryInfo(ulong recordID)
        {
            ulong hash = HashRecordID(recordID);
            return LocateBucket(hash);
        }

        // in FIndex
        internal uint GetRootBucketIndex(ulong recordID)
        {
            ulong hash = HashRecordID(recordID);
            return (uint)(hash & (rootBuckets.Header.BucketCount - 1));
        }

        // Backwards-compatible wrapper: preserves original API but delegates to the new method.
        public ulong GetRecordOffset(ulong recordID)
        {
            return GetRecordOffsetInfo(recordID).recordOffset;
        }

        // New: returns both the tier base offset and the record offset within that tier.
        public (ulong tierOffset, ulong recordOffset, ulong bucketIndex, ulong hash) GetRecordOffsetInfo(ulong recordID)
        {
            ulong idHash = HashRecordID(recordID);

            uint bucketCount = header.RootBucketCount;
            var buckets = rootBuckets;
            int shift = 0;
            // start with root tier base offset
            ulong tierBase = header.RootBucketOffset;

            while (true)
            {
                var bucketIndex = (idHash >> shift) & (bucketCount - 1);
                var entry = buckets.GetEntry(bucketIndex);

                // determine active slot by checking bit 62 (same logic as before)
                var bucketVal = entry.RecordOffset;

                // if MSB not set -> this is a data pointer in current tier
                if (!entry.IsExpanded)
                    return (tierBase, bucketVal & OFFSET_MASK, bucketIndex, idHash);

                // MSB set -> descend to referenced tier
                tierBase = bucketVal;
                shift += FastLog2(bucketCount);
                buckets = LoadTier(tierBase).tier;

                bucketCount = buckets.Header.BucketCount;
            }
        }

        /// <summary>
        /// Initializes the index cache and root tier after controller construction or offline ownership adoption.<br/>
        /// Every normal return establishes both fields for index operations; a failed initialization does not establish a usable index.<br/>
        /// Call before operations requiring tiers or the cache; constructors intentionally do not invoke this method.<br/>
        /// </summary>
        public void Init()
        {
            bucketTiers = new ConcurrentDictionary<ulong, (BucketTier tier, DateTime insertTime)>();
            if (fc.IsNew || fc.Length < FIndexHeader.SizeOf)
            {
                // build and write file header
                header = new FIndexHeader();
                header.CreatedTicks = DateTime.UtcNow.Ticks;
                header.Uuid = Guid.NewGuid();
                header.FanOutAlgorithm = config.IndexGrowthStrategy;
                header.RootBucketCount = config.RootTierBucketCount;
                header.Magic = (ulong)Consts.FINDEX_MAGIC;
                header.PageSizeLogical = fc.DiskGeometry.ClusterSize;
                header.PageSizePhysical = fc.DiskGeometry.PhysicalSectorSize;
                header.FileNameOffset = (uint)FIndexHeader.SizeOf;
                Span<byte> fName = stackalloc byte[1024];
                var fNameWritten = System.Text.Encoding.UTF8.GetBytes(fc.Name, fName);
                header.FileNameLength = (ushort)fNameWritten;
                Span<byte> fPath = stackalloc byte[1024];
                var fPathWritten = System.Text.Encoding.UTF8.GetBytes(fc.DirectoryName, fPath);
                header.FilePathLength = (ushort)fPathWritten;
                header.FilePathOffset = (uint)(header.FileNameOffset + fNameWritten);
                header.RootBucketOffset = Math.Max(FIndexHeader.SizeOf + header.FilePathLength + header.FileNameLength, Math.Max(DefaultRootTierOffset, fc.DiskGeometry.ClusterSize));
                header.Version = 0x1_0000_0000_0000;

                header.HeaderChecksum = header.CalculateChecksum();

                var headerBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1));

                fc.Write(headerBytes, 0);
                fc.Write(fName, header.FileNameOffset);
                fc.Write(fPath, header.FilePathOffset);

                var rootTierSize = (uint)(config.RootTierBucketCount * BucketEntry.SizeOf + BucketTierHeader.SizeOf);
                BufferStream tierData = new(rootTierSize);
                tierData.Zero(0, rootTierSize);
                rootBuckets = new BucketTier(tierData, header.RootBucketOffset, fc);
                rootBuckets.Erase();
                rootBuckets.Header.BucketCount = config.RootTierBucketCount;
                rootBuckets.Header.Tier = 0;
                rootBuckets.UpdateHeader();
                rootBuckets.SaveTier();

                // Update shared EOF atomics to the actual file length (header + root tier)
                var eof = (long)(header.RootBucketOffset + (ulong)rootTierSize);
                fc.atomics.Write64(0, eof);
            }
            else
            {

                Span<byte> headerBytes = fc.ReadSpan(0, (uint)FIndexHeader.SizeOf);
                header = MemoryMarshal.Read<FIndexHeader>(headerBytes);

                if (header.Magic != (ulong)Consts.FINDEX_MAGIC)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is not a valid fractX file.  Incorrect magic number.");

                var checksum = header.CalculateChecksum();
                if (checksum != header.HeaderChecksum)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");

                var curPath = fc.DirectoryName;
                var curName = fc.Name;

                if (header.FileNameOffset < FIndexHeader.SizeOf || header.FileNameLength == 0)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");
                if (header.FilePathOffset < header.FileNameOffset + header.FileNameLength || header.FilePathLength == 0)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");

                if (header.RootBucketOffset <= header.FilePathOffset + header.FilePathLength || header.RootBucketCount == 0)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");


                var fPath = fc.ReadSpan(header.FilePathOffset, header.FilePathLength);
                var fName = fc.ReadSpan(header.FileNameOffset, header.FileNameLength);

                var fPathStr = System.Text.Encoding.UTF8.GetString(fPath);
                var fNameStr = System.Text.Encoding.UTF8.GetString(fName);

                if (!FractalStore.StoredLocationMatches(fPathStr, fNameStr, curPath, curName))
                    throw new FileCopiedOrMovedException(fPathStr, fNameStr, curPath, curName, header.Uuid, isIndexFile: true);

                var rootTierSize = (uint)(header.RootBucketCount * BucketEntry.SizeOf + BucketTierHeader.SizeOf);
                BufferStream tierData = new(rootTierSize);
                tierData.Zero(0, rootTierSize);
                rootBuckets = new BucketTier(tierData, header.RootBucketOffset, fc);
                rootBuckets.SetLength(rootTierSize);
                rootBuckets.LoadTier();

                // Refresh EOF atomics to the actual current file length
                fc.atomics.Write64(0, fc.FileLength);

            }
        }

        /// <summary>
        /// Rewrites the header's stored file name and directory path to the current file location without moving any buckets.<br/>
        /// Use after intentionally moving or copying the index file while the store is offline.<br/>
        /// Throws if the new UTF-8 path or name cannot fit inside the reserved header space.<br/>
        /// </summary>
        public void TakeOwnership()
        {
            if (fc.Length < FIndexHeader.SizeOf)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete. Cannot take ownership.");

            Span<byte> headerBytes = fc.ReadSpan(0, FIndexHeader.SizeOf);
            var newHeader = MemoryMarshal.Read<FIndexHeader>(headerBytes);

            if (newHeader.Magic != (ulong)Consts.FINDEX_MAGIC)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is not a valid fractX file.  Incorrect magic number.");

            var checksum = newHeader.CalculateChecksum();
            if (checksum != newHeader.HeaderChecksum)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");

            if (newHeader.FileNameOffset < FIndexHeader.SizeOf || newHeader.FilePathOffset <= newHeader.FileNameOffset)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' has invalid header offsets.  Cannot take ownership.");
            if (newHeader.RootBucketOffset <= newHeader.FilePathOffset || newHeader.RootBucketCount == 0)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");
            if (newHeader.FileNameLength == 0)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");
            if (newHeader.FilePathOffset < newHeader.FileNameOffset + newHeader.FileNameLength || newHeader.FilePathLength == 0)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");

            string curPath = fc.DirectoryName;
            string curName = fc.Name;

            int newNameBytes = Encoding.UTF8.GetByteCount(curName);
            int newPathBytes = Encoding.UTF8.GetByteCount(curPath);

            if (newNameBytes == 0 || newPathBytes == 0)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' cannot record an empty path or file name.");
            if (newNameBytes > ushort.MaxValue || newPathBytes > ushort.MaxValue)
                throw new InvalidOperationException($"File '{fc.FullName}' path or file name is too long to encode in the header.");

            int availableNameBytes = checked((int)(newHeader.FilePathOffset - newHeader.FileNameOffset));
            int availablePathBytes = checked((int)(newHeader.RootBucketOffset - newHeader.FilePathOffset));

            if (availableNameBytes <= 0 || availablePathBytes <= 0)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' has invalid header spacing.  Cannot take ownership.");
            if (newHeader.FileNameLength > availableNameBytes || newHeader.FilePathLength > availablePathBytes)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");
            if (newNameBytes > availableNameBytes)
                throw new InvalidOperationException($"File '{fc.FullName}' file name length {newNameBytes} exceeds reserved header space of {availableNameBytes} bytes.");
            if (newPathBytes > availablePathBytes)
                throw new InvalidOperationException($"File '{fc.FullName}' path length {newPathBytes} exceeds reserved header space of {availablePathBytes} bytes.");

            var storedName = Encoding.UTF8.GetString(fc.ReadSpan(newHeader.FileNameOffset, newHeader.FileNameLength));
            var storedPath = Encoding.UTF8.GetString(fc.ReadSpan(newHeader.FilePathOffset, newHeader.FilePathLength));
            if (FractalStore.StoredLocationMatches(storedPath, storedName, curPath, curName))
            {
                header = newHeader;
                return;
            }

            newHeader.FileNameLength = (ushort)newNameBytes;
            newHeader.FilePathLength = (ushort)newPathBytes;
            newHeader.HeaderChecksum = 0;
            newHeader.HeaderChecksum = newHeader.CalculateChecksum();

            var headerOut = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref newHeader, 1));
            fc.Write(headerOut, 0);

            var pool = ArrayPool<byte>.Shared;

            byte[]? nameBuffer = null;
            byte[]? pathBuffer = null;
            try
            {
                nameBuffer = pool.Rent(availableNameBytes);
                var nameSpan = nameBuffer.AsSpan(0, availableNameBytes);
                nameSpan.Clear();
                Encoding.UTF8.GetBytes(curName, nameSpan);
                fc.Write(nameSpan, newHeader.FileNameOffset);

                pathBuffer = pool.Rent(availablePathBytes);
                var pathSpan = pathBuffer.AsSpan(0, availablePathBytes);
                pathSpan.Clear();
                Encoding.UTF8.GetBytes(curPath, pathSpan);
                fc.Write(pathSpan, newHeader.FilePathOffset);
            }
            finally
            {
                if (nameBuffer is not null)
                    pool.Return(nameBuffer);
                if (pathBuffer is not null)
                    pool.Return(pathBuffer);
            }

            fc.FlushToDisk();
            header = newHeader;
        }

        // e.g., in FIndex:
        internal static int GetChainLockIndex(ulong tierOffset, ulong bucketIndex)
        {
            ulong mixed = Mix13((tierOffset << 32) | bucketIndex);
            // Choose a range that doesn't conflict with per-entry or stripe locks.
            // Example: 1024..2047
            return (int)(mixed & 0x3FF) + 1024;
        }

        /// <summary>
        /// Publishes one data-record location into an unset canonical index bucket.<br/>
        /// Collision handling and tier expansion belong to the caller; an occupied bucket is never overwritten here.<br/>
        /// </summary>
        /// <remarks>The caller must keep the bucket stable from lookup through publication, as PutOneCore does under its root-bucket stripe lock.<br/></remarks>
        /// <param name="recordID">Identity used to locate the destination bucket.<br/></param>
        /// <param name="dataOffset">Physical offset of the already-written data record.<br/></param>
        /// <param name="dataSize">Payload size stored with the index location.<br/></param>
        /// <exception cref="InvalidOperationException">The canonical bucket is already occupied and requires caller-side collision handling.<br/></exception>
        public void InsertRecordOffset(ulong recordID, ulong dataOffset, uint dataSize)
        {
            ulong hash = HashRecordID(recordID);

            // Walk tiers until we find a non-tier bucket
            var (tier, bucketIndex, entry) = LocateBucket(hash);

            // Collision handling belongs to the caller; only an unset bucket may be published.
            if (!entry.IsUnset)
            {
                throw new InvalidOperationException("InsertRecordOffset called on non-empty bucket. Caller must handle collisions.");
            }

            entry.OffsetA = dataOffset;
            entry.ABReadSelector = BucketEntrySelector.A;
            entry.SizeA = dataSize;
            entry.HasSingleRecord = true;
            entry.IsChainedBackref = false;
            tier.SetEntry(bucketIndex, entry);

        }

        public IEnumerable<BucketEntry> IterateKeys()
        {
            if (rootBuckets == null) yield break;

            var visited = new HashSet<ulong>();

            foreach (var rec in IterateKeys(rootBuckets, visited))
                yield return rec;
        }

        public IEnumerable<BucketEntry> IterateKeys(BucketTier tier)
        {
            var visited = new HashSet<ulong>();

            foreach (var rec in IterateKeys(tier, visited))
                yield return rec;
        }

        private IEnumerable<BucketEntry> IterateKeys(BucketTier tier, HashSet<ulong> visited)
        {
            if (!visited.Add(tier.TierOffset))
                throw new System.IO.InvalidDataException($"Detected cyclic tier reference at offset 0x{tier.TierOffset:X}.");

            var th = tier.Header;
            for (ulong i = 0; i < th.BucketCount; i++)
            {
                var bucketEntry = tier.GetEntry(i);
                if (bucketEntry.IsUnset) continue;
                if (bucketEntry.IsExpanded)
                {
                    // load next tier for bucket
                    var nextTier = LoadTier(bucketEntry.RecordOffset);
                    foreach (var rec in IterateKeys(nextTier.tier, visited))
                        yield return rec;
                    continue;
                }

                if (bucketEntry.IsChainedBackref)
                    continue;

                yield return bucketEntry;
            }
        }

        /// <summary>
        /// Opens a forward-only reader over the live leaf entries in the Fractal index.<br/>
        /// The reader avoids compiler-generated iterator state and exposes one entry at a time without per-entry allocation.<br/>
        /// Expanded tiers are traversed depth-first and cyclic tier references are rejected with <see cref="System.IO.InvalidDataException"/>.<br/>
        /// </summary>
        /// <returns>A reader positioned before the first live leaf entry.<br/></returns>
        internal BucketReader OpenBucketReader()
            => new BucketReader(this, rootBuckets);

        /// <summary>
        /// Provides allocation-stable, forward-only traversal of Fractal index leaf buckets.<br/>
        /// One stack and one visited-tier set are allocated per reader; advancing does not allocate per bucket.<br/>
        /// </summary>
        internal sealed class BucketReader : IDisposable
        {
            private readonly FIndex owner;
            private readonly Stack<Frame> frames = new Stack<Frame>();
            private readonly HashSet<ulong> visited = new HashSet<ulong>();
            private BucketEntry current;
            private BucketTier? currentTier;
            private ulong currentIndex;
            private bool disposed;

            /// <summary>
            /// Initializes traversal at the supplied root tier.<br/>
            /// </summary>
            /// <param name="owner">Index that resolves expanded child tiers.<br/></param>
            /// <param name="root">Root tier to traverse, or <see langword="null"/> for an empty index.<br/></param>
            internal BucketReader(FIndex owner, BucketTier? root)
            {
                this.owner = owner;
                if (root is not null)
                    Push(root);
            }

            /// <summary>
            /// Gets the leaf entry at the current reader position.<br/>
            /// The value is valid until the next call to <see cref="Read"/> or disposal.<br/>
            /// </summary>
            internal BucketEntry Current
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return current;
                }
            }

            /// <summary>
            /// Gets the tier that owns <see cref="Current"/> so an exclusive maintenance operation can update the current leaf without repeating hash resolution.<br/>
            /// The value is valid only after a successful <see cref="Read"/> and until the next read or disposal.<br/>
            /// </summary>
            internal BucketTier CurrentTier
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return currentTier ?? throw new InvalidOperationException("The bucket reader is not positioned on a leaf entry.");
                }
            }

            /// <summary>
            /// Gets the owning tier's ordinal for <see cref="Current"/> so exclusive maintenance can mutate the exact leaf in constant time.<br/>
            /// The value is valid only after a successful <see cref="Read"/> and until the next read or disposal.<br/>
            /// </summary>
            internal ulong CurrentIndex
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (currentTier is null)
                        throw new InvalidOperationException("The bucket reader is not positioned on a leaf entry.");
                    return currentIndex;
                }
            }

            /// <summary>
            /// Advances to the next live leaf bucket.<br/>
            /// Unset entries and chained back-references are skipped; expanded entries push their referenced tier.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when <see cref="Current"/> was populated; otherwise <see langword="false"/> at end of input.<br/></returns>
            internal bool Read()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                while (frames.Count != 0)
                {
                    Frame frame = frames.Pop();
                    if (frame.Index >= frame.Tier.Header.BucketCount)
                        continue;

                    ulong entryIndex = frame.Index;
                    BucketEntry entry = frame.Tier.GetEntry(entryIndex);
                    frame.Index++;
                    frames.Push(frame);

                    if (entry.IsUnset || entry.IsChainedBackref)
                        continue;
                    if (entry.IsExpanded)
                    {
                        Push(owner.LoadTier(entry.RecordOffset).tier);
                        continue;
                    }

                    current = entry;
                    currentTier = frame.Tier;
                    currentIndex = entryIndex;
                    return true;
                }

                current = default;
                currentTier = null;
                currentIndex = 0;
                return false;
            }

            /// <summary>
            /// Releases traversal state.<br/>
            /// Cached index tiers remain owned by the parent <see cref="FIndex"/>.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                frames.Clear();
                visited.Clear();
                current = default;
                currentTier = null;
                currentIndex = 0;
            }

            /// <summary>
            /// Pushes one previously unseen tier onto the depth-first traversal stack.<br/>
            /// </summary>
            /// <param name="tier">Tier whose buckets should be visited.<br/></param>
            private void Push(BucketTier tier)
            {
                if (!visited.Add(tier.TierOffset))
                    throw new System.IO.InvalidDataException($"Detected cyclic tier reference at offset 0x{tier.TierOffset:X}.");
                frames.Push(new Frame(tier));
            }

            /// <summary>
            /// Tracks the next bucket ordinal within one active tier.<br/>
            /// </summary>
            private struct Frame
            {
                internal BucketTier Tier;
                internal ulong Index;

                /// <summary>
                /// Starts traversal of one tier at bucket zero.<br/>
                /// </summary>
                /// <param name="tier">Tier to traverse.<br/></param>
                internal Frame(BucketTier tier)
                {
                    Tier = tier;
                    Index = 0;
                }
            }
        }

        public void UnsetRecord(ulong recordID)
        {
            ulong hash = HashRecordID(recordID);
            var (tier, bucketIndex, entry) = LocateBucket(hash);

            entry = new BucketEntry();
            tier.SetEntry(bucketIndex, entry);
        }

        public void UpdateRecordOffset(ulong recordID, ulong dataOffset, uint dataSize)
        {
            ulong hash = HashRecordID(recordID);

            var (tier, bucketIndex, entry) = LocateBucket(hash);

            try
            {
                if (entry.ABReadSelector == BucketEntrySelector.A)
                {
                    entry.OffsetB = dataOffset;
                    entry.SizeB = dataSize;
                }
                else
                {
                    entry.OffsetA = dataOffset;
                    entry.SizeA = dataSize;
                }

                tier.SetEntry(bucketIndex, entry);

                entry.ABReadSelector = entry.ABReadSelector == BucketEntrySelector.A ? BucketEntrySelector.B : BucketEntrySelector.A;

                tier.SetEntry(bucketIndex, entry);
            }
            finally
            {
            }
        }


        public BucketStructureSnapshot GetBucketStructureStats(int topBucketCount = 16)
        {
            if (topBucketCount <= 0)
                topBucketCount = 16;

            var perTier = new List<BucketTierOccupancy>();
            var byFill = new List<BucketFillStat>();
            var visitedOffsets = new HashSet<ulong>();

            void WalkTier(BucketTier tier)
            {
                if (!visitedOffsets.Add(tier.TierOffset))
                    return;

                var hdr = tier.Header;
                uint bucketCount = hdr.BucketCount;
                uint unused = 0;
                uint expanded = 0;
                uint leaf = 0;

                for (ulong i = 0; i < bucketCount; i++)
                {
                    var entry = tier.GetEntry(i);

                    if (entry.IsUnset)
                    {
                        unused++;
                        continue;
                    }

                    if (entry.IsExpanded)
                    {
                        expanded++;

                        // Child tier for this expanded bucket
                        var childInfo = LoadTier(entry.RecordOffset);
                        var child = childInfo.tier;
                        var childHdr = child.Header;
                        uint childCount = childHdr.BucketCount;
                        uint childUsed = 0;

                        for (ulong j = 0; j < childCount; j++)
                        {
                            var childEntry = child.GetEntry(j);
                            if (!childEntry.IsUnset)
                                childUsed++;
                        }

                        byFill.Add(new BucketFillStat(
                            parentTier: hdr.Tier,
                            tierOffset: tier.TierOffset,
                            bucketIndex: i,
                            childBucketCount: childCount,
                            childUsedBuckets: childUsed));

                        // Recurse down
                        WalkTier(child);
                    }
                    else
                    {
                        // Non-expanded, non-unset (leaf or backref)
                        leaf++;
                    }
                }

                perTier.Add(new BucketTierOccupancy(
                    tier: hdr.Tier,
                    tierOffset: tier.TierOffset,
                    bucketCount: bucketCount,
                    unused: unused,
                    expanded: expanded,
                    leaf: leaf));
            }

            // Start from root tier
            WalkTier(rootBuckets);

            var hottest = byFill
                .OrderByDescending(b => b.FillRatio)
                .ThenByDescending(b => b.ChildUsedBuckets)
                .Take(topBucketCount)
                .ToArray();

            var coolest = byFill
                .Where(b => b.ChildUsedBuckets > 0)
                .OrderBy(b => b.FillRatio)
                .ThenBy(b => b.ChildUsedBuckets)
                .Take(topBucketCount)
                .ToArray();

            var perTierOrdered = perTier
                .OrderBy(t => t.Tier)
                .ThenBy(t => t.TierOffset)
                .ToArray();

            return new BucketStructureSnapshot(perTierOrdered, hottest, coolest);
        }
    }
}
