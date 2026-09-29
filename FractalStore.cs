using Standart.Hash.xxHash;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using static FractalKVS.FractalStore;








namespace FractalKVS
{
    //====== TYPES ======
    /// <summary>
    /// Provides a file-backed durable dictionary for <see cref="ulong"/> record identities and binary payloads.<br/>
    /// A store uses separate data and index files, grows its hash-directed index incrementally, and supports concurrent ordinary reads and writes.<br/>
    /// </summary>
    public class FractalStore
    {
        /// <summary>
        /// Controls the verbosity of online defrag logging.
        /// </summary>
        internal enum OnlineDefragLogLevel
        {
            Off = 0,
            Error = 1,
            Info = 2,
            Debug = 3,
            Trace = 4
        }

        /// <summary>
        /// Gets or sets the current online defrag logging level.
        /// </summary>
        internal static OnlineDefragLogLevel DefragLogLevel { get; set; } = OnlineDefragLogLevel.Off;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDefragLogEnabled(OnlineDefragLogLevel level)
            => level != OnlineDefragLogLevel.Off && DefragLogLevel >= level;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DefragLog(OnlineDefragLogLevel level, string message)
        {
            if (!IsDefragLogEnabled(level))
                return;
            Debug.WriteLine($"[FractalKVS.Defrag/{level}] {message}");
        }

        //Shared/Static Members
        internal static ulong KeyToID(string key) => Standart.Hash.xxHash.xxHash64.ComputeHash(key);
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








        // Bucket-level striped locks used to serialize PutOne per bucket chain.
        // Assumes AtomicIntegers.RegionSize == 8192 => MaxInt32Slots == 2048.
        // We reserve 1024..1535 for bucket stripes (512 stripes).
        //======  FIELDS  ======
        private const int BucketStripeBase = 1024;
        private const int BucketStripeCount = 512;
        private const int BucketStripeMask = BucketStripeCount - 1;
        private const int PutOneStripeBase = 1024;      // uses atomicintegers slots 1024..2047
        private const int PutOneStripeCount = 1024;
        private const int PutOneStripeMask = PutOneStripeCount - 1;
        // Reserve a dedicated block of 64-bit atomic slots for per-store USNs (storeID 1..255).
        // Placing these below the 32-bit stripe ranges avoids overlap.
        internal const int StoreUsnBaseIndex64 = 32;
        // ------- ID Generator fields -------
        // 64-bit slot index in AtomicIntegers for the RecordID allocator.
        // Pick something not used by bucket stripes / EOF. 12 is just an example.
        internal const int RecordIdAtomicSlot = 12;
        // How many IDs to reserve in one atomic op + flush.
        // 128 is a nice compromise: small enough to keep “wasted” range tiny,
        // big enough to amortize fsync cost.
        private const int RecordIdBlockSize = 256;

        /// <summary>
        /// Creates the exact 64-bit atomic-slot preservation list for an index-sidecar reopen.<br/>
        /// Logical EOF, the global record-ID allocator, and each store USN are durable; transient 32-bit stripe locks deliberately remain outside this list and are cleared by first-opener initialization.<br/>
        /// </summary>
        /// <returns>A fresh slot array safe for ownership by one <see cref="AtomicIntegers"/> instance.<br/></returns>
        internal static int[] CreatePersistedIndexAtomicSlotList()
        {
            var slots = new int[byte.MaxValue + 2];
            slots[0] = 0;
            slots[1] = RecordIdAtomicSlot;
            for (var storeId = 1; storeId <= byte.MaxValue; storeId++)
                slots[storeId + 1] = StoreUsnBaseIndex64 + storeId;
            return slots;
        }








        private long _idBlockEnd = 0;
        private long _idBlockNext = 0;
        private readonly object _idBlockSync = new();
        // Online defrag state
        private readonly OnlineDefragmenter _onlineDefrag;
        private FractalStoreConfiguration config;
        private FData fData;
        private FIndex fIndex;
        private readonly string folderPath;
        private readonly string name;
        private readonly object snapshotGate = new();
        private int activeMutations;
        private bool snapshotExclusive;
        private bool snapshotExclusivePending;
        // Established only after successful component initialization in Open; normal operation paths require that lifecycle boundary.
        private FractalStoreTelemetry telemetry = null!;








        //======  CONSTRUCTORS  ======
        /// <summary>
        /// Defines one Fractal store family without opening its files.<br/>
        /// Call <see cref="Open"/> before reading or writing records.<br/>
        /// </summary>
        /// <param name="folderPath">Directory that contains the Fractal data and index files.<br/></param>
        /// <param name="name">Base name shared by the store's files.<br/></param>
        /// <param name="config">Creation, index-growth, concurrency, and durability configuration.<br/></param>
        public FractalStore(string folderPath, string name, FractalStoreConfiguration config)
        {
            if (config.RootTierBucketCount.IsPowerOfTwo() == false)
                throw new ArgumentException($"The {nameof(config.RootTierBucketCount)} value must be a power-of-2 value.");

            var di = new System.IO.DirectoryInfo(folderPath);
            if (!di.Exists)
                if (config.CreateIfNotExists)
                    di.Create();
                else
                    throw new System.IO.DirectoryNotFoundException($"Directory '{di.FullName}' not found and config.CreateIfNotExists=false.");

            fIndex = new FIndex(folderPath, name, config);

            fData = new FData(folderPath, name, config);

            this.folderPath = folderPath;
            this.name = name;
            this.config = config;
            _onlineDefrag = new OnlineDefragmenter(this);
        }








        //====== EVENTS & DELEGATES ======
        /// <summary>Occurs when online defragmentation reports bounded progress.<br/></summary>
        public event EventHandler<OnlineDefragEventArgs>? OnlineDefragProgress;
        /// <summary>Occurs when online defragmentation starts.<br/></summary>
        public event EventHandler<OnlineDefragEventArgs>? OnlineDefragStarted;
        /// <summary>Occurs when online defragmentation stops.<br/></summary>
        public event EventHandler<OnlineDefragEventArgs>? OnlineDefragStopped;








        //======  PROPERTIES  ======
        /// <summary>Gets whether the store's online defragmentation worker is active.<br/></summary>
        public bool IsOnlineDefragRunning => _onlineDefrag.IsRunning;
        //======  PROPERTIES  ======
        /// <summary>
        /// Gets telemetry established by a successful Open; retained counters remain available after Close.<br/>
        /// A subsequent successful Open replaces this object, preserving the existing counter lifetime.<br/>
        /// </summary>
        /// <exception cref="InvalidOperationException">No successful Open has established telemetry for this store.<br/></exception>
        internal FractalStoreTelemetry Telemetry => telemetry
            ?? throw new InvalidOperationException("Store telemetry is unavailable. Call Open() successfully before accessing Telemetry.");
        // properties hoisted up from FData and FIndex for convenience
        /// <summary>Gets whether this store is configured for a single hosting process.<br/></summary>
        public bool AssumeSingleProcess => config.AssumeSingleProcess;
        /// <summary>Gets the persisted identity of this Fractal store's index file.<br/></summary>
        public Guid UUID => fIndex.Header.Uuid;








        //======  METHODS  ======
        /// <summary>
        /// Enters one mutation that participates in the live-snapshot publication boundary.<br/>
        /// The returned lease is not thread-affine, allowing parallel and asynchronous mutation implementations to release it from any completion thread.<br/>
        /// Reads do not enter this boundary because a snapshot requires publication quiescence, not reader exclusion.<br/>
        /// </summary>
        /// <returns>A lease that releases the active-mutation count exactly once.<br/></returns>
        private IDisposable EnterMutationScope()
        {
            lock (snapshotGate)
            {
                while (snapshotExclusive)
                    Monitor.Wait(snapshotGate);

                checked { activeMutations++; }
                return new MutationScope(this);
            }
        }

        /// <summary>
        /// Waits for every admitted mutation to complete, then atomically blocks new mutation admission for a coherent snapshot window.<br/>
        /// Admission remains open while draining so an already-admitted composite mutation may safely enter nested mutation helpers without deadlocking itself.<br/>
        /// Continuous new mutation traffic can delay acquisition; callers can bound that wait with <paramref name="cancellationToken"/>.<br/>
        /// </summary>
        /// <param name="cancellationToken">Token observed while waiting for admitted mutations to drain.<br/></param>
        /// <returns>A lease that reopens mutation admission exactly once.<br/></returns>
        private IDisposable EnterSnapshotExclusiveScope(CancellationToken cancellationToken)
        {
            lock (snapshotGate)
            {
                if (snapshotExclusive || snapshotExclusivePending)
                    throw new InvalidOperationException("A Fractal live snapshot is already active.");

                snapshotExclusivePending = true;
                try
                {
                    while (activeMutations != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Monitor.Wait(snapshotGate, TimeSpan.FromMilliseconds(100));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    snapshotExclusivePending = false;
                    snapshotExclusive = true;
                    return new SnapshotExclusiveScope(this);
                }
                catch
                {
                    snapshotExclusivePending = false;
                    snapshotExclusive = false;
                    Monitor.PulseAll(snapshotGate);
                    throw;
                }
            }
        }

        /// <summary>
        /// Resolves a record identity through its index bucket and collision chain without loading its payload.<br/>
        /// A successful lookup returns the containing non-null tier and its canonical bucket index.<br/>
        /// </summary>
        /// <param name="recordID">Logical record identity to locate in the initialized store.<br/></param>
        /// <returns>When found is true, the matching header, physical offset, chain index, non-null tier, and bucket index.<br/>
        /// When found is false, tier is null and the other lookup values are default or sentinel values.<br/></returns>
        internal (bool found, ulong recordOffset, int chainIndex, RecordHeader header, BucketTier? tier, ulong bucketIndex) GetRecordInfo(ulong recordID)
        {
            var (tier, bucketIndex, bucket) = fIndex.GetBucketEntryInfo(recordID);

            if (bucket.IsUnset)
                return (false, 0ul, -1, default, default, 0);

            ulong currentOffset = bucket.RecordOffset;
            uint currentSize = bucket.RecordSize;

            var record = fData.ReadRecordHeader(currentOffset);

            if (record.ID == recordID)
                return (true, currentOffset, 0, record, tier, bucketIndex);

            // Walk the collision chain starting at this bucket's head.
            var chainIndex = 1;
            while (true)
            {
                if (record.NextRecordOffset == 0)
                    break;

                currentOffset = record.NextRecordOffset;
                currentSize = record.NextRecordSize;

                record = fData.ReadRecordHeader(currentOffset);

                if (record.ID == recordID)
                    return (true, currentOffset, chainIndex, record, tier, bucketIndex);

                chainIndex++;
            }

            return (false, 0ul, -1, default, default, 0);

        }








        private ulong CountChain(ulong recordOffset) => CountChain(recordOffset, useMask: false, 0, 0);

        private ulong CountChain(ulong recordOffset, bool useMask, ulong mask, ulong expectedValue)
        {
            ulong count = 0;
            ulong slow = recordOffset;
            ulong fast = recordOffset;

            while (slow != 0)
            {
                var header = fData.ReadRecordHeader(slow);
                if (header.ID != 0 && (!useMask || (header.ID & mask) == expectedValue))
                    count++;

                slow = header.NextRecordOffset;

                var f1 = Advance(fast);
                var f2 = f1 == 0 ? 0 : Advance(f1);
                fast = f2; // keep hare two steps ahead when possible

                if (fast != 0 && slow != 0 && slow == fast)
                    throw new InvalidDataException($"Detected cyclic record chain starting at offset {recordOffset}.");
            }
            return count;

            ulong Advance(ulong offset)
            {
                if (offset == 0)
                    return 0;

                var header = fData.ReadRecordHeader(offset);
                return header.NextRecordOffset;
            }
        }

        private ulong CountDeletedChain(ulong recordOffset)
        {
            ulong count = 0;
            ulong current = recordOffset;

            while (current != 0)
            {
                var header = fData.ReadRecordHeader(current);
                if (header.ID == 0)
                    count++;

                if (header.NextRecordOffset == 0)
                    break;

                current = header.NextRecordOffset;
            }

            return count;
        }

        /// <summary>
        /// Expand the given parent bucket into a new tier and seed bucket entries
        /// using an anchor + backref model:
        ///  - allocate a new tier via FIndex.ExpandBucket (in-memory only),
        ///  - choose the first record in the chain as the "anchor" chain head,
        ///  - for the anchor's child bucket: direct entry -> anchor's RecordOffset/Size,
        ///  - for other child buckets that need to reference records in the same chain:
        ///        IsChainedBackref = true, OffsetA = anchorBucketIndex,
        ///  - flush the entire tier once via SaveTier(),
        ///  - flip the parent entry to IsExpanded pointing at the new tier.
        /// 
        /// No RecordHeader.NextRecordOffset/NextRecordSize rewrites happen here; the
        /// original collision chain in the data file is left intact. Lazy per-bucket
        /// surgery (normalization) can be done later when inserting into backref buckets.
        /// 
        /// Must be called under the root-bucket stripe lock.
        /// </summary>
        private void ExpandAndReindex(
            BucketTier parentTier,
            ulong parentBucketIndex,
            BucketEntry parentEntry,
            ReadOnlySpan<(ulong hash, ulong recordOffset, uint size)> chain)
        {
            if (chain.Length == 0)
                throw new ArgumentException("Chain must contain at least one entry.", nameof(chain));

            // 1) Allocate new tier (no parent update, no record writes).
            ulong newTierOffset = fIndex.ExpandBucket(parentTier, parentBucketIndex, parentEntry, chain[0].hash);

            var newTierInfo = fIndex.LoadTier(newTierOffset);
            var newTier = newTierInfo.tier;

            int shift = fIndex.CalcShiftForHash(newTier.Header.Tier - 1);
            uint bucketCount = newTier.Header.BucketCount;
            ulong indexMask = bucketCount - 1;

            // 2) Choose the chain head as the global anchor for this expanded tier.
            //    All logical child buckets that contain elements from this chain will
            //    either:
            //      - point directly at this anchor (one bucket), or
            //      - be backrefs to the anchor bucket.
            {
                var (anchorHash, anchorOffset, anchorSize) = chain[0];
                ulong anchorBucketIndex = (anchorHash >> shift) & indexMask;

                // Track which child bucket indices already have entries.
                var seenBuckets = new HashSet<ulong>();

                // 2a) Write the anchor bucket entry (direct data reference).
                {
                    var anchorEntry = new BucketEntry
                    {
                        OffsetA = anchorOffset,
                        SizeA = anchorSize,
                        OffsetB = 0,
                        SizeB = 0
                    };

                    anchorEntry.ABReadSelector = BucketEntrySelector.A;
                    anchorEntry.IsExpanded = false;
                    anchorEntry.IsChainedBackref = false;
                    anchorEntry.HasSingleRecord = false;

                    WriteBucketEntryInMemory(newTier, anchorBucketIndex, anchorEntry);
                    seenBuckets.Add(anchorBucketIndex);
                }

                // 2b) For each remaining record in the chain, compute its child bucket
                //     index and seed a backref entry (if we haven't already).
                for (int i = 1; i < chain.Length; i++)
                {
                    var (hash, _, _) = chain[i];
                    ulong bucketIndex = (hash >> shift) & indexMask;

                    // Same logical bucket as anchor -> no separate entry; they all share
                    // the anchor chain head.
                    if (bucketIndex == anchorBucketIndex)
                        continue;

                    // Already have an entry for this child bucket -> nothing more to do.
                    if (!seenBuckets.Add(bucketIndex))
                        continue;

                    var backrefEntry = new BucketEntry
                    {
                        // OffsetA stores the anchor bucket index (not a record offset)
                        // when IsChainedBackref is true.
                        OffsetA = anchorBucketIndex,
                        OffsetB = 0,
                        SizeA = 0,
                        SizeB = 0
                    };

                    backrefEntry.ABReadSelector = BucketEntrySelector.A;
                    backrefEntry.IsExpanded = false;
                    backrefEntry.IsChainedBackref = true;
                    backrefEntry.HasSingleRecord = false;

                    WriteBucketEntryInMemory(newTier, bucketIndex, backrefEntry);
                }
            }

            // 3) Flush the entire new tier as a single write syscall.
            //Console.WriteLine($"[FractalStore] Expanded bucket at parent tier offset 0x{parentTier.TierOffset:X16} index {parentBucketIndex:X8} and size {newTier.Tier.Length:00000000#} into new tier at offset 0x{newTierOffset:X16} with {chain.Length} records reindexed.");
            newTier.SaveTier();

            // 4) Flip the parent entry to point at the new tier and mark it expanded.
            var expandedEntry = new BucketEntry
            {
                OffsetA = newTierOffset, // child tier base
                OffsetB = 0,
                SizeA = 0,
                SizeB = 0
            };

            expandedEntry.ABReadSelector = BucketEntrySelector.A;
            expandedEntry.IsExpanded = true;
            expandedEntry.IsChainedBackref = false;

            parentTier.SetEntryUnlocked(parentBucketIndex, expandedEntry);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetStoreIdFromRecordId(ulong recordID)
            => (byte)(recordID >> 56);

        private void IncrementStoreUsn(byte storeId, bool deferFlush)
        {
            if (storeId == 0)
                return;

            var atomics = fIndex.fc.atomics;
            atomics.Add64(StoreUsnBaseIndex64 + storeId, 1);

            if (!deferFlush && config.WriteBehavior == FractalStoreWriteBehavior.Immediate)
                atomics.Flush(flushToDisk: true);
        }

        private void FlushStoreUsnIfImmediate()
        {
            if (config.WriteBehavior == FractalStoreWriteBehavior.Immediate)
                fIndex.fc.atomics.Flush(flushToDisk: true);
        }

        /// <summary>
        /// Returns the current update sequence number (USN) for the specified store identifier.<br/>
        /// The USN increments on logical mutations (put/update/delete) for that store and can be used to detect changes since a prior snapshot.<br/>
        /// </summary>
        /// <param name="storeId">Store identifier (1..255) carried in the high byte of record IDs.<br/></param>
        /// <returns>The current 64-bit USN for the store.<br/></returns>
        public ulong GetUSN(byte storeId)
        {
            if (storeId == 0)
                throw new ArgumentOutOfRangeException(nameof(storeId), "Store IDs must be in the range 1..255.");

            return unchecked((ulong)fIndex.fc.atomics.Read64(StoreUsnBaseIndex64 + storeId));
        }

        /// <summary>
        /// Advances the durable logical USN for one store when coordinated metadata outside the Fractal payload changes query-visible record semantics.<br/>
        /// Ordinary Fractal put, update, and delete operations already advance this value automatically; callers must not advance it a second time for those mutations.<br/>
        /// </summary>
        /// <param name="storeId">Store identifier in the range 1 through 255.<br/></param>
        /// <returns>The durable USN after the increment.<br/></returns>
        public ulong AdvanceUSN(byte storeId)
        {
            if (storeId == 0)
                throw new ArgumentOutOfRangeException(nameof(storeId), "Store IDs must be in the range 1..255.");

            IncrementStoreUsn(storeId, deferFlush: false);
            return GetUSN(storeId);
        }

        private bool PutOneInternal(ulong recordID, ReadOnlySpan<byte> data, bool deferUsnFlush = false)
        {
            // Stable root bucket index for this ID.
            ulong hash = FIndex.HashRecordID(recordID);
            uint rootCount = fIndex.Header.RootBucketCount;
            ulong rootIndex = hash & (rootCount - 1);

            // Telemetry baselines (index + data syscalls + expansion count)
            long idxBefore = telemetry.IndexFileSyscalls.TotalSyscalls;
            long datBefore = telemetry.DataFileSyscalls.TotalSyscalls;
            long expBefore = fIndex.BucketExpansionCount;

            // Use rootIndex as the key for lock striping.
            ulong bucketKey = rootIndex;
            ulong bucketHash = FIndex.HashRecordID(bucketKey);

            int stripe = (int)(bucketHash & BucketStripeMask);
            int lockIndex = BucketStripeBase + stripe;

            var atomics = fIndex.fc.atomics;
            var spin = new SpinWait();
            bool changed = false;

            while (!atomics.TryAcquireSpinLock32(lockIndex))
                spin.SpinOnce();

            try
            {
                changed = PutOneCore(recordID, data);
            }
            finally
            {
                atomics.ReleaseSpinLock32(lockIndex);
            }

            long idxAfter = telemetry.IndexFileSyscalls.TotalSyscalls;
            long datAfter = telemetry.DataFileSyscalls.TotalSyscalls;
            long expAfter = fIndex.BucketExpansionCount;

            long deltaSyscalls = (idxAfter - idxBefore) + (datAfter - datBefore);
            bool hadExpansion = expAfter > expBefore;

            telemetry.RecordPut(deltaSyscalls, hadExpansion);

            if (changed)
                IncrementStoreUsn(GetStoreIdFromRecordId(recordID), deferUsnFlush);

            return changed;
        }

        private bool PutOneCore(ulong recordID, ReadOnlySpan<byte> data)
        {
            // Root-bucket stripe lock MUST already be held by caller (PutOne).

            var info = fIndex.GetBucketEntryInfo(recordID);
            var tier = info.tier;
            ulong bucketIndex = info.bucketIndex;
            var bucket = info.bucket;

            if (!bucket.IsUnset)
            {
                // Walk the full collision chain for this bucket.
                var chain = new List<(ulong hash, ulong recordOffset, uint size)>(8);

                ulong currentOffset = bucket.RecordOffset;
                var rh = fData.ReadRecordHeader(currentOffset);

                bool found = false;
                int foundIndex = -1;

                while (true)
                {
                    ulong h = FIndex.HashRecordID(rh.ID);
                    chain.Add((h, currentOffset, rh.DataLength));

                    if (rh.ID == recordID)
                    {
                        found = true;
                        foundIndex = chain.Count - 1;
                    }

                    if (rh.NextRecordOffset == 0)
                        break;

                    currentOffset = rh.NextRecordOffset;
                    rh = fData.ReadRecordHeader(currentOffset);
                }

                // 1) Found this ID in the existing chain → mutate/update.
                if (found)
                {
                    ulong targetOffset = chain[foundIndex].recordOffset;

                    // Next element in the chain (if any)
                    ulong nextOffset = foundIndex + 1 < chain.Count
                        ? chain[foundIndex + 1].recordOffset
                        : 0UL;

                    uint nextSize = foundIndex + 1 < chain.Count
                        ? chain[foundIndex + 1].size
                        : 0U;

                    // Perform the mutation (may append or overwrite in place).
                    var (newOffset, record) = fData.MutateRecord(recordID, targetOffset, data);

                    // *** NEW: if nothing structurally changed, stop here. ***
                    if (newOffset == targetOffset)
                    {
                        // Data file, chain, and index remain valid as-is.
                        return false;
                    }

                    if (foundIndex == 0)
                    {
                        // *** Case 1: mutated the chain head ***

                        // New head must inherit the "next" pointer so the chain remains intact.
                        var newHead = fData.ReadRecordHeader(newOffset);
                        newHead.NextRecordOffset = nextOffset;
                        newHead.NextRecordSize = nextSize;
                        fData.UpdateRecordHeader(newOffset, newHead);

                        // Keep the A/B index durability for the head of this bucket.
                        // This will repoint the bucket entry to newOffset (or keep it if in-place).
                        fIndex.UpdateRecordOffset(recordID, newOffset, (uint)data.Length);
                    }
                    else
                    {
                        // *** Case 2: mutated a non-head element ***

                        // Previous element in the chain MUST exist because foundIndex > 0.
                        ulong prevOffset = chain[foundIndex - 1].recordOffset;

                        // Rewire prev → newOffset
                        var prevHeader = fData.ReadRecordHeader(prevOffset);
                        prevHeader.NextRecordOffset = newOffset;
                        prevHeader.NextRecordSize = (uint)data.Length; // size of the mutated record
                        fData.UpdateRecordHeader(prevOffset, prevHeader);

                        // And newOffset → next (or null if tail)
                        var newHeader = fData.ReadRecordHeader(newOffset);
                        newHeader.NextRecordOffset = nextOffset;
                        newHeader.NextRecordSize = nextSize;
                        fData.UpdateRecordHeader(newOffset, newHeader);

                        // IMPORTANT: Do *not* call UpdateRecordOffset here.
                        // The bucket entry must stay pointed at chain[0].recordOffset.
                    }

                    return true;
                }

                // 2) Not found in chain. Decide if we need to expand or just append to tail.
                bool needExpand = chain.Count >= config.MaxChainedRecords;

                if (needExpand)
                {
                    // Expand bucket + seed anchor + backref entries into the new tier.
                    try
                    {
                        ExpandAndReindex(tier, bucketIndex, bucket, CollectionsMarshal.AsSpan(chain));
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(ex.ToString());
                        Console.ResetColor();
                    }

                    // Re-run logical PutOne under same root-bucket lock against expanded view.
                    return PutOneCore(recordID, data);
                }

                // 3) Chain shorter than threshold, append new colliding record at tail.
                //    currentOffset / rh correspond to the last element (NextRecordOffset == 0).
                var (appendedOffset, appendedRecord) = fData.AppendRecord(recordID, 0ul, default, data);

                rh.NextRecordOffset = appendedOffset;
                rh.NextRecordSize = (uint)data.Length;
                fData.UpdateRecordHeader(currentOffset, rh);

                if (bucket.HasSingleRecord)
                {
                    bucket.HasSingleRecord = false;
                    tier!.SetEntryUnlocked(bucketIndex, bucket);
                }

                return true;
            }
            else
            {
                // Bucket is unset → first record for this bucket group.
                var (newOffset, record) = fData.AppendRecord(recordID, 0ul, default, data);
                fIndex.InsertRecordOffset(recordID, newOffset, (uint)data.Length);
            }

            return true;
        }

        private void RaiseOnlineDefragEvent(EventHandler<OnlineDefragEventArgs>? handler, OnlineDefragStats stats)
        {
            if (handler is null || stats is null)
                return;

            var args = new OnlineDefragEventArgs(stats.State, stats);

            try
            {
                handler(this, args);
            }
            catch
            {
                // Observers should not be able to fault the defrag loop.
            }
        }

        private void RaiseOnlineDefragProgress(OnlineDefragStats stats) => RaiseOnlineDefragEvent(OnlineDefragProgress, stats);

        private void RaiseOnlineDefragStarted(OnlineDefragStats stats) => RaiseOnlineDefragEvent(OnlineDefragStarted, stats);

        private void RaiseOnlineDefragStopped(OnlineDefragStats stats) => RaiseOnlineDefragEvent(OnlineDefragStopped, stats);

        private ulong RecordCountInternal(bool useMask, ulong mask, ulong expectedValue)
        {
            ulong total = 0;

            foreach (var entry in fIndex.IterateKeys())
            {
                if (entry.IsUnset || entry.IsExpanded || entry.IsChainedBackref)
                    continue;

                if (!useMask && entry.HasSingleRecord)
                {
                    total += 1UL;
                    continue;
                }
                if (entry.HasSingleRecord)
                {
                    var header = fData.ReadRecordHeader(entry.RecordOffset);
                    if (header.ID != 0 && (header.ID & mask) == expectedValue)
                        total += 1UL;
                    continue;
                }

                total += CountChain(entry.RecordOffset, useMask, mask, expectedValue);
            }

            telemetry.RecordLiveRecordCount(total);
            return total;
        }








        //------ Public Methods -----
        internal void ClearIndexCache()
        {
            fIndex.ClearTierCache();
        }

        /// <summary>Flushes and closes the store's data, index, and shared atomic resources.<br/></summary>
        public void Close()
        {
            StopOnlineDefrag();
            try { fData?.fc?.FlushToDisk(); } catch { }
            try { fIndex?.fc?.FlushToDisk(); } catch { }
            try { fData?.fc?.Close(); } catch { }
            try { fIndex?.fc?.Close(); } catch { }
            try { fData?.fc?.atomics?.Dispose(); } catch { }
            try { fIndex?.fc?.atomics?.Dispose(); } catch { }
        }

        /// <summary>Deletes every logical record and advances the affected store update sequences.<br/></summary>
        public void DeleteAll()

        {
            using var mutation = EnterMutationScope();
            fIndex.DeleteAllKeys();
            fData.DeleteAllRecords();
            bool bumped = false;
            for (int storeId = 1; storeId <= byte.MaxValue; storeId++)
            {
                IncrementStoreUsn((byte)storeId, deferFlush: true);
                bumped = true;
            }
            if (bumped)
                FlushStoreUsnIfImmediate();
        }

        /// <summary>
        /// Rebuilds the Fractal index file from the current data file and swaps it in place.<br/>
        /// This is an offline operation and requires exclusive access; callers must gate concurrent writers.<br/>
        /// The rebuild creates a minimal root-tier-only index (no expansions) and preserves the existing atomic SHM state.<br/>
        /// </summary>
        public void RebuildIndex()
        {
            using var mutation = EnterMutationScope();
            StopOnlineDefrag();

            var indexPath = Path.Combine(folderPath, $"{name}.fractX");
            var tempPath = indexPath + ".rebuild";
            var tempShmPath = tempPath + ".shm";
            var backupPath = indexPath + ".bak";

            if (File.Exists(tempPath))
                File.Delete(tempPath);
            if (File.Exists(tempShmPath))
                File.Delete(tempShmPath);

            var newIndex = FIndex.CreateForPath(tempPath, config);
            newIndex.Init();
            newIndex.fc.BucketTelemetry = telemetry?.BucketTelemetry;
            BuildRootIndexFromData(newIndex);

            newIndex.fc.FlushToDisk();
            newIndex.fc.Close();
            try { newIndex.fc.atomics.Dispose(); } catch { }

            // Close current index handles so we can swap files.
            fIndex.fc.Close();

            var swapped = false;
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                if (File.Exists(indexPath))
                    File.Move(indexPath, backupPath, overwrite: true);

                File.Move(tempPath, indexPath, overwrite: true);
                swapped = true;
            }
            finally
            {
                if (!swapped)
                {
                    if (File.Exists(backupPath) && !File.Exists(indexPath))
                        File.Move(backupPath, indexPath, overwrite: true);
                }
                else
                {
                    if (File.Exists(backupPath))
                        File.Delete(backupPath);
                }

                if (File.Exists(tempPath))
                    File.Delete(tempPath);
                if (File.Exists(tempShmPath))
                    File.Delete(tempShmPath);
            }

            fIndex = new FIndex(folderPath, name, config);
            fIndex.Init();

            if (telemetry is not null)
            {
                telemetry.IndexFileSyscallsInternal = fIndex.fc.syscallPerfTracker;
                fIndex.fc.BucketTelemetry = telemetry.BucketTelemetry;
            }
        }

        /// <summary>
        /// Builds a minimal root-tier-only index using the current data file's collision chains.<br/>
        /// Only bucket heads are indexed; no tier expansions are created during rebuild.<br/>
        /// </summary>
        /// <param name="targetIndex">The target index instance to populate.<br/></param>
        private void BuildRootIndexFromData(FIndex targetIndex)
        {
            ArgumentNullException.ThrowIfNull(targetIndex);

            var fileEnd = (ulong)fData.fc.Length;
            var start = fData.header.FirstRecordOffset;
            if (start == 0 || start >= fileEnd)
                return;

            var headers = new Dictionary<ulong, RecordHeader>();
            var referenced = new HashSet<ulong>();

            ulong offset = start;
            while (offset + (uint)RecordHeader.SizeOf <= fileEnd)
            {
                var header = fData.ReadRecordHeader(offset);
                if (header.RecordSize == 0)
                    break;

                headers[offset] = header;

                if (header.NextRecordOffset != 0)
                    referenced.Add(header.NextRecordOffset);

                offset += header.RecordSize;
            }




            uint rootCount = targetIndex.Header.RootBucketCount;
            ulong mask = rootCount - 1;

            foreach (var kvp in headers)
            {
                var headOffset = kvp.Key;
                var headHeader = kvp.Value;

                if (referenced.Contains(headOffset))
                    continue;

                // Find any non-zero record ID in the chain so we can compute the bucket index.
                ulong id = 0;
                ulong current = headOffset;
                while (current != 0 && headers.TryGetValue(current, out var h))
                {
                    if (h.ID != 0)
                    {
                        id = h.ID;
                        break;
                    }
                    current = h.NextRecordOffset;
                }

                if (id == 0)
                    continue;

                ulong bucketIndex = FIndex.HashRecordID(id) & mask;

                var entry = new BucketEntry
                {
                    OffsetA = headOffset,
                    SizeA = headHeader.DataLength,
                    OffsetB = 0,
                    SizeB = 0
                };
                entry.ABReadSelector = BucketEntrySelector.A;
                entry.IsExpanded = false;
                entry.IsChainedBackref = false;
                entry.HasSingleRecord = headHeader.NextRecordOffset == 0;

                targetIndex.TrySetRootEntryIfUnset(bucketIndex, entry);
            }
        }
        /// <summary>
        /// Counts all deleted (tombstoned) records that still occupy space within live bucket chains.
        /// </summary>
        public ulong DeletedRecordCount()
        {
            ulong total = 0;

            foreach (var entry in fIndex.IterateKeys())
            {
                if (entry.IsUnset || entry.IsExpanded || entry.IsChainedBackref)
                    continue;

                total += CountDeletedChain(entry.RecordOffset);
            }

            telemetry.RecordDeletedRecordCount(total);
            return total;
        }

        /// <summary>
        /// Deletes each requested live record while preserving the ordinary tombstone fast path by default.<br/>
        /// When <paramref name="zeroWrite"/> is enabled, every physical mutation payload for each resolved identity is overwritten with zeros and durably flushed before that identity's deletion returns.<br/>
        /// </summary>
        /// <param name="recordIDs">Logical record identities to delete; missing identities are ignored.<br/></param>
        /// <param name="zeroWrite">Whether to overwrite all discoverable physical mutation payload copies before completing each deletion.<br/></param>
        public void DeleteMany(IEnumerable<ulong> recordIDs, bool zeroWrite = false)
        {
            if (recordIDs is null)
                throw new ArgumentNullException(nameof(recordIDs));

            var ids = recordIDs as ICollection<ulong> ?? recordIDs.ToArray();
            if (!ids.Any())
                return;

            if (ids.Count < config.PutManyParallelMinRecordCount)
            {
                bool anyChanged = false;
                foreach (var id in ids)
                {
                    if (DeleteOne(id, zeroWrite))
                        anyChanged = true;
                }

                if (anyChanged)
                    FlushStoreUsnIfImmediate();
                return;
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = config.PutManyParallelThreadCount
            };

            int anyChangedParallel = 0;
            Parallel.ForEach(ids, options, id =>
            {
                if (DeleteOne(id, zeroWrite))
                    Interlocked.Exchange(ref anyChangedParallel, 1);
            });

            if (anyChangedParallel == 1)
                FlushStoreUsnIfImmediate();
        }

        /// <summary>
        /// Deletes one logical record through either the ordinary tombstone path or an explicit residual-data overwrite path.<br/>
        /// Zero-write deletion validates the complete physical mutation chain before changing bytes, overwrites each payload through bounded file-controller chunks, tombstones every physical copy, and durably flushes the data file before returning.<br/>
        /// This is a file-content hygiene contract and does not claim physical-media sanitization below the filesystem.<br/>
        /// </summary>
        /// <param name="recordID">The logical record identity to delete.<br/></param>
        /// <param name="zeroWrite">Whether every discoverable physical mutation payload should be overwritten before deletion completes.<br/></param>
        /// <returns><c>true</c> when the record existed and was deleted; otherwise <c>false</c>.<br/></returns>
        public bool DeleteOne(ulong recordID, bool zeroWrite = false)
        {
            using var mutation = EnterMutationScope();
            var (found, recordOffset, chainIndex, header, tier, bucketIndex) = GetRecordInfo(recordID);
            if (!found)
                return false;

            // if chainIndex == 0 and record header's NextRecordOffset == 0, we can simply unset the bucket entry.
            if (chainIndex == 0 && header.NextRecordOffset == 0)
            {
                // GetRecordInfo guarantees a non-null tier when found is true; misses returned above.
                tier!.UnsetEntry(bucketIndex);
                if (zeroWrite)
                    _ = fData.ZeroMutationChain(recordOffset, recordID);
                IncrementStoreUsn(GetStoreIdFromRecordId(recordID), deferFlush: false);
                return true;
            }

            // update recordID in chain to zero (tombstone).
            if (zeroWrite)
                _ = fData.ZeroMutationChain(recordOffset, recordID);
            else
                fData.ZeroRecordID(recordOffset);

            IncrementStoreUsn(GetStoreIdFromRecordId(recordID), deferFlush: false);

            return true;


        }

        internal BucketExpansionSnapshot GetBucketExpansionStats(int topBuckets = 16) => fIndex.BucketExpansions.GetSnapshot(topBuckets);

        internal BucketStructureSnapshot GetBucketStructureStats(int topBuckets = 16) => fIndex.GetBucketStructureStats(topBuckets);

        /// <summary>Reads multiple records, using the configured bounded read parallelism.<br/></summary>
        /// <param name="recordIDs">Identities of the records to load.<br/></param>
        /// <returns>Loaded records paired with their requested identities; callers must dispose each returned record.<br/></returns>
        public (ulong recordID, Record record)[] GetMany(IEnumerable<ulong> recordIDs)
        {

            var results = new ConcurrentBag<(ulong, Record)>();
            var counter = 0;
            Parallel.ForEach(recordIDs, new ParallelOptions { MaxDegreeOfParallelism = config.GetManyParallelThreadCount }, recordID =>
            {
                results.Add((recordID, GetOne(recordID)));
                counter++;
            });
            return results.ToArray();
        }

        /// <summary>
        /// Returns a newly allocated, strictly monotonic <see cref="ulong"/> record ID.<br/>
        /// Allocation is backed by the store's shared <c>AtomicIntegers</c> region so IDs remain<br/>
        /// globally unique across all threads and all processes using this store.<br/>
        /// <br/>
        /// IDs are allocated in fixed-size blocks (e.g., 256 IDs) to reduce contention<br/>
        /// and to minimize disk flush operations. Only the first allocation of each block<br/>
        /// performs an atomic Add64 to reserve the range and then issues a durability flush<br/>
        /// to commit the allocator state to disk.<br/>
        /// <br/>
        /// Crash-safety model:<br/>
        /// • The block reservation is written to the atomic MMF region before any IDs<br/>
        ///   from the block are returned to callers.<br/>
        /// • If a crash occurs before the flush is completed, no IDs from that block are<br/>
        ///   returned; the previous allocator value remains authoritative on restart.<br/>
        /// • If a crash occurs after the flush, some or all IDs in the committed block<br/>
        ///   may be unused, but no ID will ever be reused or returned twice.<br/>
        /// <br/>
        /// This guarantees strictly increasing, never-repeating IDs even in the presence of:<br/>
        /// • cross-process concurrency<br/>
        /// • partial writes or torn MMF pages<br/>
        /// • abnormal termination or power loss<br/>
        /// <br/>
        /// Performance model:<br/>
        /// • Most calls are served from the in-memory portion of the current block.<br/>
        /// • Flush-to-disk only occurs once per block, not per ID.<br/>
        /// • This makes GetNewRecordID() effectively O(1) for high-throughput write workloads.<br/>
        /// <br/>
        /// The returned ID is suitable for use as a FractalStore record key or as an<br/>
        /// application-level monotonic sequence number.<br/>
        /// </summary>
        /// <returns>A unique, strictly increasing <see cref="ulong"/> identifier.</returns>
        public ulong GetNewRecordID()
        {
            using var mutation = EnterMutationScope();
            // Fast path: use the in-memory block if any IDs remain.
            long next = Interlocked.Read(ref _idBlockNext);
            if (next < _idBlockEnd)
            {
                long ret = Interlocked.Increment(ref _idBlockNext);
                if (ret <= _idBlockEnd)
                    return (ulong)ret;
            }

            // Slow path: refill block with cross-process atomic + fsync.
            lock (_idBlockSync)
            {
                // Re-check under lock; someone else may have refilled.
                if (_idBlockNext < _idBlockEnd)
                {
                    long ret = ++_idBlockNext;
                    return (ulong)ret;
                }

                var atomics = fIndex.fc.atomics;

                // 1) Reserve a new block at the global allocator:
                //    Add64 returns the POST-add value.
                long newEnd = atomics.Add64(RecordIdAtomicSlot, RecordIdBlockSize);

                // 2) Force allocator state durably to disk BEFORE handing out any ID
                //    from this block.
                atomics.Flush(flushToDisk: true);

                // 3) Compute the block range [start, end]
                long newStart = newEnd - RecordIdBlockSize + 1;

                _idBlockNext = newStart;
                _idBlockEnd = newEnd;

                return (ulong)newStart;
            }
        }


        /// <summary>Loads one record and rents its payload storage from the underlying pool.<br/></summary>
        /// <param name="recordID">Identity of the record to load.<br/></param>
        /// <returns>A disposable record containing the loaded header and payload.<br/></returns>
        /// <exception cref="KeyNotFoundException">No live record has the requested identity.<br/></exception>
        public Record GetOne(ulong recordID)
        {
            long idxBefore = telemetry.IndexFileSyscalls.TotalSyscalls;
            long datBefore = telemetry.DataFileSyscalls.TotalSyscalls;

            try
            {
                var (tier, bucketIndex, bucket) = fIndex.GetBucketEntryInfo(recordID);

                if (bucket.IsUnset)
                    throw new KeyNotFoundException($"Record with ID {recordID} not found.");

                ulong currentOffset = bucket.RecordOffset;
                uint currentSize = bucket.RecordSize;

                var record = fData.ReadRecord(currentOffset, currentSize);

                if (record.Header.ID == recordID)
                    return record;

                // Walk the collision chain starting at this bucket's head.
                while (true)
                {
                    if (record.Header.NextRecordOffset == 0)
                        break;

                    currentOffset = record.Header.NextRecordOffset;
                    currentSize = record.Header.NextRecordSize;

                    record.Dispose();
                    record = fData.ReadRecord(currentOffset, currentSize);

                    if (record.Header.ID == recordID)
                        return record;
                }

                record.Dispose();
                throw new KeyNotFoundException($"Record with ID {recordID} not found in chain.");

            }
            finally
            {
                long idxAfter = telemetry.IndexFileSyscalls.TotalSyscalls;
                long datAfter = telemetry.DataFileSyscalls.TotalSyscalls;

                long deltaSyscalls = (idxAfter - idxBefore) + (datAfter - datBefore);
                telemetry.RecordGet(deltaSyscalls);

            }
        }

        /// <summary>Returns a point-in-time snapshot of online-defragmentation activity.<br/></summary>
        /// <returns>The latest online-defragmentation statistics.<br/></returns>
        public OnlineDefragStats GetOnlineDefragStats() => _onlineDefrag.GetStats();

        /// <summary>
        /// Attempts to read only the record header for <paramref name="recordID"/> without loading the payload.<br/>
        /// Returns <c>true</c> when the record exists and <paramref name="header"/> is populated; otherwise <c>false</c> with an unspecified header.<br/>
        /// </summary>
        /// <param name="recordID">Identifier of the record whose header should be read.<br/></param>
        /// <param name="header">When successful, receives the record header containing checksum, lengths, and chain metadata.<br/></param>
        /// <returns><c>true</c> if the record exists; otherwise <c>false</c>.<br/></returns>
        internal bool TryReadRecordHeader(ulong recordID, out RecordHeader header)
        {
            var (found, recordOffset, _, _, _, _) = GetRecordInfo(recordID);
            if (!found)
            {
                header = default;
                return false;
            }

            header = fData.ReadRecordHeader(recordOffset);
            return true;
        }

        /// <summary>
        /// Enumerates record IDs whose upper 8 bits match the provided group key.<br/>
        /// This is a general-purpose logical grouping mechanism; the group key can represent any developer-defined partition (e.g., store, tenant, type, etc).<br/>
        /// Uses the top-byte mask to filter IDs without loading full record bodies.<br/>
        /// Skips unset, expanded, and chained-backref buckets; follows chains otherwise.<br/>
        /// </summary>
        /// <param name="groupKey">The value to match against the upper 8 bits of each record ID.<br/>This is a developer-defined logical grouping key.</param>
        public IEnumerable<ulong> IterateRecordIDsByGroupKey(byte groupKey)
        {
            using GroupIdentityReader reader = OpenGroupIdentityReader(groupKey);
            while (reader.Read())
                yield return reader.ID;
        }

        /// <summary>
        /// Materializes record identities for one upper-byte group while preserving the exact native bucket/chain order.<br/>
        /// Leaf offsets are temporarily sorted only for bounded physical header reads; parsed headers are restored to their original bucket ordinals before identity filtering and collision-chain expansion.<br/>
        /// This path is intended for callers that already require a complete list and therefore can trade streaming first-row latency for substantially fewer synchronous file calls.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <returns>A complete identity list in the same order produced by <see cref="IterateRecordIDsByGroupKey(byte)"/>.<br/></returns>
        public List<ulong> ListRecordIDsByGroupKey(byte groupKey)
        {
            const ulong groupMask = 0xFF00_0000_0000_0000UL;
            ulong expected = (ulong)groupKey << 56;
            GroupHeaderGraph graph = BuildGroupHeaderGraph(includePhysicalOffsets: false);
            var identities = new List<ulong>(graph.Nodes.Count);
            for (int rootOrdinal = 0; rootOrdinal < graph.RootNodeIndexes.Length; rootOrdinal++)
            {
                int nodeIndex = graph.RootNodeIndexes[rootOrdinal];
                while (nodeIndex >= 0)
                {
                    GroupIdentityHeaderNode node = graph.Nodes[nodeIndex];
                    ulong id = node.Header.ID;
                    if (id != 0 && (id & groupMask) == expected)
                        identities.Add(id);
                    nodeIndex = node.NextNodeIndex;
                }
            }
            return identities;
        }

        /// <summary>
        /// Materializes authoritative record-header topology once for complete-group operations.<br/>
        /// Bucket heads and every collision-chain level are physically sorted only while their headers are read; flat successor links restore exact native bucket/chain order without allocating one collection per chain.<br/>
        /// </summary>
        /// <returns>Flat header nodes plus one root-node index per native bucket entry.<br/></returns>
        private GroupHeaderGraph BuildGroupHeaderGraph(bool includePhysicalOffsets)
        {
            var naturalOffsets = new List<ulong>();
            using (FIndex.BucketReader buckets = fIndex.OpenBucketReader())
            {
                while (buckets.Read())
                    naturalOffsets.Add(buckets.Current.RecordOffset);
            }

            if (naturalOffsets.Count == 0)
            {
                return new GroupHeaderGraph(
                    Array.Empty<int>(),
                    new List<GroupIdentityHeaderNode>(),
                    includePhysicalOffsets ? new List<ulong>() : null);
            }

            var rootNodeIndexes = new int[naturalOffsets.Count];
            Array.Fill(rootNodeIndexes, -1);
            var nodes = new List<GroupIdentityHeaderNode>(naturalOffsets.Count);
            List<ulong>? nodeOffsets = includePhysicalOffsets
                ? new List<ulong>(naturalOffsets.Count)
                : null;
            var pending = new List<GroupIdentityPendingHeader>(naturalOffsets.Count);
            var nextPending = new List<GroupIdentityPendingHeader>(naturalOffsets.Count);
            for (int rootOrdinal = 0; rootOrdinal < naturalOffsets.Count; rootOrdinal++)
            {
                pending.Add(new GroupIdentityPendingHeader(
                    naturalOffsets[rootOrdinal],
                    rootOrdinal,
                    PriorNodeIndex: -1));
            }

            while (pending.Count > 0)
            {
                int pendingCount = pending.Count;
                ulong[] sortedOffsets = ArrayPool<ulong>.Shared.Rent(pendingCount);
                int[] pendingOrdinals = ArrayPool<int>.Shared.Rent(pendingCount);
                RecordHeader[] sortedHeaders = ArrayPool<RecordHeader>.Shared.Rent(pendingCount);
                RecordHeader[] pendingHeaders = ArrayPool<RecordHeader>.Shared.Rent(pendingCount);
                try
                {
                    for (int pendingOrdinal = 0; pendingOrdinal < pendingCount; pendingOrdinal++)
                    {
                        sortedOffsets[pendingOrdinal] = pending[pendingOrdinal].Offset;
                        pendingOrdinals[pendingOrdinal] = pendingOrdinal;
                    }

                    Array.Sort(sortedOffsets, pendingOrdinals, index: 0, length: pendingCount);
                    fData.ReadRecordHeadersCoalesced(
                        sortedOffsets.AsSpan(0, pendingCount),
                        sortedHeaders.AsSpan(0, pendingCount));
                    for (int sortedOrdinal = 0; sortedOrdinal < pendingCount; sortedOrdinal++)
                        pendingHeaders[pendingOrdinals[sortedOrdinal]] = sortedHeaders[sortedOrdinal];

                    nextPending.Clear();
                    for (int pendingOrdinal = 0; pendingOrdinal < pendingCount; pendingOrdinal++)
                    {
                        GroupIdentityPendingHeader request = pending[pendingOrdinal];
                        RecordHeader header = pendingHeaders[pendingOrdinal];
                        int nodeIndex = nodes.Count;
                        nodes.Add(new GroupIdentityHeaderNode(header, NextNodeIndex: -1));
                        nodeOffsets?.Add(request.Offset);
                        if (request.PriorNodeIndex < 0)
                        {
                            rootNodeIndexes[request.RootOrdinal] = nodeIndex;
                        }
                        else
                        {
                            GroupIdentityHeaderNode prior = nodes[request.PriorNodeIndex];
                            nodes[request.PriorNodeIndex] = prior.WithNextNodeIndex(nodeIndex);
                        }

                        if (header.NextRecordOffset != 0)
                        {
                            nextPending.Add(new GroupIdentityPendingHeader(
                                header.NextRecordOffset,
                                request.RootOrdinal,
                                nodeIndex));
                        }
                    }
                }
                finally
                {
                    ArrayPool<ulong>.Shared.Return(sortedOffsets);
                    ArrayPool<int>.Shared.Return(pendingOrdinals);
                    ArrayPool<RecordHeader>.Shared.Return(sortedHeaders);
                    ArrayPool<RecordHeader>.Shared.Return(pendingHeaders);
                }

                (pending, nextPending) = (nextPending, pending);
            }
            return new GroupHeaderGraph(rootNodeIndexes, nodes, nodeOffsets);
        }

        /// <summary>
        /// Creates the authoritative live location set for one identity group and orders it by physical data-file offset.<br/>
        /// Physical order is intentionally separate from Fractal natural order and is used only by complete, order-insensitive bulk consumers.<br/>
        /// </summary>
        /// <param name="groupKey">Upper identity byte selecting the logical group.<br/></param>
        /// <returns>Compact live record locations sorted by strictly ascending physical offset.<br/></returns>
        private GroupRecordLocationBuffer RentGroupRecordLocationsByPhysicalOrder(byte groupKey)
        {
            const ulong groupMask = 0xFF00_0000_0000_0000UL;
            ulong expected = (ulong)groupKey << 56;
            GroupPhysicalPendingHeader[] pending = ArrayPool<GroupPhysicalPendingHeader>.Shared.Rent(256);
            GroupPhysicalPendingHeader[] nextPending = ArrayPool<GroupPhysicalPendingHeader>.Shared.Rent(256);
            GroupPhysicalRecordCandidate[] matches = ArrayPool<GroupPhysicalRecordCandidate>.Shared.Rent(256);
            int[]? matchingCountsByRoot = null;
            try
            {
                int rootCount = 0;
                using (FIndex.BucketReader buckets = fIndex.OpenBucketReader())
                {
                    while (buckets.Read())
                    {
                        if (rootCount == pending.Length)
                        {
                            GroupPhysicalPendingHeader[] grown =
                                ArrayPool<GroupPhysicalPendingHeader>.Shared.Rent(checked(pending.Length * 2));
                            pending.AsSpan(0, rootCount).CopyTo(grown);
                            ArrayPool<GroupPhysicalPendingHeader>.Shared.Return(pending);
                            pending = grown;
                        }

                        pending[rootCount] = new GroupPhysicalPendingHeader(
                            buckets.Current.RecordOffset,
                            rootCount);
                        rootCount++;
                    }
                }

                if (rootCount == 0)
                    return new GroupRecordLocationBuffer(Array.Empty<GroupRecordLocation>(), 0);

                matchingCountsByRoot = ArrayPool<int>.Shared.Rent(rootCount);
                Array.Clear(matchingCountsByRoot, 0, rootCount);
                int pendingCount = rootCount;
                int matchCount = 0;
                while (pendingCount > 0)
                {
                    ulong[] sortedOffsets = ArrayPool<ulong>.Shared.Rent(pendingCount);
                    int[] pendingOrdinals = ArrayPool<int>.Shared.Rent(pendingCount);
                    RecordHeader[] sortedHeaders = ArrayPool<RecordHeader>.Shared.Rent(pendingCount);
                    RecordHeader[] pendingHeaders = ArrayPool<RecordHeader>.Shared.Rent(pendingCount);
                    int nextPendingCount = 0;
                    try
                    {
                        for (int pendingOrdinal = 0; pendingOrdinal < pendingCount; pendingOrdinal++)
                        {
                            sortedOffsets[pendingOrdinal] = pending[pendingOrdinal].Offset;
                            pendingOrdinals[pendingOrdinal] = pendingOrdinal;
                        }

                        Array.Sort(sortedOffsets, pendingOrdinals, index: 0, length: pendingCount);
                        fData.ReadRecordHeadersCoalesced(
                            sortedOffsets.AsSpan(0, pendingCount),
                            sortedHeaders.AsSpan(0, pendingCount));
                        for (int sortedOrdinal = 0; sortedOrdinal < pendingCount; sortedOrdinal++)
                            pendingHeaders[pendingOrdinals[sortedOrdinal]] = sortedHeaders[sortedOrdinal];

                        for (int pendingOrdinal = 0; pendingOrdinal < pendingCount; pendingOrdinal++)
                        {
                            GroupPhysicalPendingHeader request = pending[pendingOrdinal];
                            RecordHeader header = pendingHeaders[pendingOrdinal];
                            ulong id = header.ID;
                            if (id != 0 && (id & groupMask) == expected)
                            {
                                if (matchCount == matches.Length)
                                {
                                    GroupPhysicalRecordCandidate[] grown =
                                        ArrayPool<GroupPhysicalRecordCandidate>.Shared.Rent(checked(matches.Length * 2));
                                    matches.AsSpan(0, matchCount).CopyTo(grown);
                                    ArrayPool<GroupPhysicalRecordCandidate>.Shared.Return(matches);
                                    matches = grown;
                                }

                                int ordinalWithinRoot = matchingCountsByRoot[request.RootOrdinal]++;
                                matches[matchCount] = new GroupPhysicalRecordCandidate(
                                    request.Offset,
                                    header,
                                    request.RootOrdinal,
                                    ordinalWithinRoot);
                                matchCount++;
                            }

                            if (header.NextRecordOffset != 0)
                            {
                                if (nextPendingCount == nextPending.Length)
                                {
                                    GroupPhysicalPendingHeader[] grown =
                                        ArrayPool<GroupPhysicalPendingHeader>.Shared.Rent(checked(nextPending.Length * 2));
                                    nextPending.AsSpan(0, nextPendingCount).CopyTo(grown);
                                    ArrayPool<GroupPhysicalPendingHeader>.Shared.Return(nextPending);
                                    nextPending = grown;
                                }

                                nextPending[nextPendingCount] = new GroupPhysicalPendingHeader(
                                    header.NextRecordOffset,
                                    request.RootOrdinal);
                                nextPendingCount++;
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<ulong>.Shared.Return(sortedOffsets);
                        ArrayPool<int>.Shared.Return(pendingOrdinals);
                        ArrayPool<RecordHeader>.Shared.Return(sortedHeaders);
                        ArrayPool<RecordHeader>.Shared.Return(pendingHeaders);
                    }

                    (pending, nextPending) = (nextPending, pending);
                    pendingCount = nextPendingCount;
                }

                if (matchCount == 0)
                    return new GroupRecordLocationBuffer(Array.Empty<GroupRecordLocation>(), 0);

                int runningNaturalOrdinal = 0;
                for (int rootOrdinal = 0; rootOrdinal < rootCount; rootOrdinal++)
                {
                    int rootMatchCount = matchingCountsByRoot[rootOrdinal];
                    matchingCountsByRoot[rootOrdinal] = runningNaturalOrdinal;
                    runningNaturalOrdinal = checked(runningNaturalOrdinal + rootMatchCount);
                }
                if (runningNaturalOrdinal != matchCount)
                    throw new InvalidDataException("Physical group materialization produced inconsistent natural-order counts.");

                GroupRecordLocation[] result = ArrayPool<GroupRecordLocation>.Shared.Rent(matchCount);
                try
                {
                    for (int matchIndex = 0; matchIndex < matchCount; matchIndex++)
                    {
                        GroupPhysicalRecordCandidate match = matches[matchIndex];
                        result[matchIndex] = new GroupRecordLocation(
                            match.Offset,
                            match.Header,
                            checked(matchingCountsByRoot[match.RootOrdinal] + match.OrdinalWithinRoot));
                    }

                    Array.Sort(result, index: 0, length: matchCount);
                    return new GroupRecordLocationBuffer(result, matchCount);
                }
                catch
                {
                    ArrayPool<GroupRecordLocation>.Shared.Return(result);
                    throw;
                }
            }
            finally
            {
                ArrayPool<GroupPhysicalPendingHeader>.Shared.Return(pending);
                ArrayPool<GroupPhysicalPendingHeader>.Shared.Return(nextPending);
                ArrayPool<GroupPhysicalRecordCandidate>.Shared.Return(matches);
                if (matchingCountsByRoot is not null)
                    ArrayPool<int>.Shared.Return(matchingCountsByRoot, clearArray: true);
            }
        }

        /// <summary>
        /// Holds the compact output of one authoritative header-topology materialization.<br/>
        /// </summary>
        /// <param name="RootNodeIndexes">Flat-node index for every native bucket root.<br/></param>
        /// <param name="Nodes">Every parsed bucket/collision header with its in-memory successor link.<br/></param>
        /// <param name="NodeOffsets">Physical offsets corresponding to materialized graph nodes when retained.<br/></param>
        private readonly record struct GroupHeaderGraph(
            int[] RootNodeIndexes,
            List<GroupIdentityHeaderNode> Nodes,
            List<ulong>? NodeOffsets);

        /// <summary>
        /// Owns one pooled physical-location array and its populated prefix length.<br/>
        /// </summary>
        /// <param name="Buffer">Pooled location storage, or an empty array when the group has no records.<br/></param>
        /// <param name="Count">Number of populated sorted locations in <paramref name="Buffer"/>.<br/></param>
        private readonly record struct GroupRecordLocationBuffer(
            GroupRecordLocation[] Buffer,
            int Count);

        /// <summary>
        /// Identifies one authoritative live record for physical-order bulk payload access.<br/>
        /// </summary>
        /// <param name="Offset">Physical offset of the persisted record header.<br/></param>
        /// <param name="Header">Header observed while the authoritative bucket/collision topology was materialized.<br/></param>
        /// <param name="NaturalOrdinal">Zero-based position in Fractal's authoritative bucket/collision order before physical sorting.<br/></param>
        internal readonly record struct GroupRecordLocation(
            ulong Offset,
            RecordHeader Header,
            int NaturalOrdinal) : IComparable<GroupRecordLocation>
        {
            /// <summary>
            /// Orders compact record metadata by ascending physical data-file offset.<br/>
            /// </summary>
            /// <param name="other">Location to compare with this value.<br/></param>
            /// <returns>A signed comparison result suitable for in-place array sorting.<br/></returns>
            public int CompareTo(GroupRecordLocation other)
                => Offset.CompareTo(other.Offset);
        }

        /// <summary>
        /// Identifies one header awaiting a coalesced physical read while retaining only the native bucket root required to reconstruct group-local natural order.<br/>
        /// Instances live in pooled arrays owned by one bulk snapshot and never become part of the persisted Fractal format.<br/>
        /// </summary>
        /// <param name="Offset">Physical Fractal data-file offset of the header to read.<br/></param>
        /// <param name="RootOrdinal">Zero-based native bucket root owning this collision-chain member.<br/></param>
        private readonly record struct GroupPhysicalPendingHeader(
            ulong Offset,
            int RootOrdinal);

        /// <summary>
        /// Retains one matching physical record until pooled per-root counts can be converted into exact group-local natural ordinals.<br/>
        /// The candidate is runtime-only and avoids the general graph's successor links and duplicate offset list for consumers that need only one physical-order snapshot.<br/>
        /// </summary>
        /// <param name="Offset">Physical Fractal data-file offset of the authoritative record header.<br/></param>
        /// <param name="Header">Header observed during the coalesced topology read.<br/></param>
        /// <param name="RootOrdinal">Zero-based native bucket root owning this collision-chain member.<br/></param>
        /// <param name="OrdinalWithinRoot">Zero-based position among matching group records in this root's collision chain.<br/></param>
        private readonly record struct GroupPhysicalRecordCandidate(
            ulong Offset,
            RecordHeader Header,
            int RootOrdinal,
            int OrdinalWithinRoot);

        /// <summary>
        /// Identifies one physical header still required by the breadth-first complete-group materializer.<br/>
        /// The root ordinal and prior-node link allow physical reads to be reordered without changing the public natural identity order.<br/>
        /// </summary>
        /// <param name="Offset">Physical Fractal data-file offset of the header to read.<br/></param>
        /// <param name="RootOrdinal">Natural bucket ordinal that owns the collision-chain member.<br/></param>
        /// <param name="PriorNodeIndex">Previously materialized chain-node index, or negative one for a root header.<br/></param>
        private readonly record struct GroupIdentityPendingHeader(
            ulong Offset,
            int RootOrdinal,
            int PriorNodeIndex);

        /// <summary>
        /// Stores one parsed header and its in-memory successor link while a complete group identity list is assembled.<br/>
        /// The structure deliberately avoids retaining physical offsets or allocating one collection per collision chain.<br/>
        /// </summary>
        /// <param name="Header">Parsed persisted record header.<br/></param>
        /// <param name="NextNodeIndex">Next in-memory collision-chain node, or negative one when no successor has been linked.<br/></param>
        private readonly record struct GroupIdentityHeaderNode(
            RecordHeader Header,
            int NextNodeIndex)
        {
            /// <summary>
            /// Returns this immutable node with its materialized successor attached.<br/>
            /// </summary>
            /// <param name="nextNodeIndex">Successor node index in the owning flat node list.<br/></param>
            /// <returns>A node preserving the parsed header and carrying the supplied successor index.<br/></returns>
            public GroupIdentityHeaderNode WithNextNodeIndex(int nextNodeIndex)
                => new(Header, nextNodeIndex);
        }

        /// <summary>
        /// Enumerates record IDs whose upper 8 bits match the provided group key.<br/>
        /// This is a general-purpose logical grouping mechanism; the group key can represent any developer-defined partition (e.g., store, tenant, type, etc).<br/>
        /// Uses the top-byte mask to filter IDs without loading full record bodies.<br/>
        /// Skips unset, expanded, and chained-backref buckets; follows chains otherwise.<br/>
        /// </summary>
        /// <param name="groupKey">The value to match against the upper 8 bits of each record ID.<br/>This is a developer-defined logical grouping key.</param>
        public IEnumerable<Record> IterateRecordsByGroupKey(byte groupKey)
        {
            const ulong mask = 0xFF00_0000_0000_0000UL;
            ulong expected = (ulong)groupKey << 56;

            foreach (var entry in fIndex.IterateKeys())
            {
                if (entry.IsUnset || entry.IsExpanded || entry.IsChainedBackref)
                    continue;

                if (entry.HasSingleRecord)
                {
                    var record = fData.ReadRecord(entry.RecordOffset, entry.RecordSize);
                    if (record.Header.ID != 0 && (record.Header.ID & mask) == expected)
                        yield return record;
                    else
                        record.Dispose();
                    continue;
                }

                var current = (offset: entry.RecordOffset, size: entry.RecordSize);
                while (current.offset != 0)
                {
                    var record = fData.ReadRecord(current.offset, current.size);
                    ulong id = record.Header.ID;
                    ulong nextOffset = record.Header.NextRecordOffset;
                    uint nextSize = record.Header.NextRecordSize;
                    bool matches = id != 0 && (id & mask) == expected;
                    if (matches)
                        yield return record;
                    else
                        record.Dispose();

                    current = (nextOffset, nextSize);
                }
            }
        }

        /// <summary>
        /// Opens a forward-only, borrowed-payload reader for records whose upper identity byte equals <paramref name="groupKey"/>.<br/>
        /// The reader owns each pooled Fractal record and returns it automatically when advancing or disposing, removing consumer-side pooled-buffer ownership from iterative reads.<br/>
        /// <see cref="GroupRecordReader.Data"/> and <see cref="GroupRecordReader.DataSpan"/> remain valid only until the next read, skip, or disposal.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <returns>A reader positioned before the first matching record.<br/></returns>
        public GroupRecordReader OpenGroupRecordReader(byte groupKey)
            => new GroupRecordReader(this, groupKey);

        /// <summary>
        /// Opens a complete, physical-order group reader for order-insensitive bulk extraction.<br/>
        /// The reader first materializes compact authoritative record locations, then serves borrowed payload slices from pooled contiguous data-file windows so nearby records share one physical read.<br/>
        /// Unlike <see cref="OpenGroupRecordReader(byte)"/>, result order is physical and must not be interpreted as Fractal natural order.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <param name="readWindowBytes">Preferred contiguous read-ahead window; oversized individual records expand the pooled buffer only as required.<br/></param>
        /// <returns>A reader positioned before the first physical-order matching record.<br/></returns>
        public GroupBulkRecordReader OpenGroupBulkRecordReader(
            byte groupKey,
            int readWindowBytes = 256 * 1024)
            => new GroupBulkRecordReader(this, groupKey, readWindowBytes);

        /// <summary>
        /// Opens one shared authoritative physical-location snapshot divided into disjoint contiguous readers for locality-preserving concurrent work.<br/>
        /// Snapshot topology is materialized and physically sorted exactly once; each partition then owns only its pooled read-ahead window while borrowing a non-overlapping location range from the returned set.<br/>
        /// Partition order is physical data-file order rather than Fractal natural order, so consumers that expose an order-sensitive result must project or merge explicitly after evaluation.<br/>
        /// The returned set must remain alive until every partition reader has been disposed, and callers must serialize mutations against the represented group for the lifetime of the set.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <param name="partitionCount">Requested maximum number of non-empty physical partitions.<br/></param>
        /// <param name="readWindowBytes">Preferred contiguous read-ahead window owned independently by each partition reader.<br/></param>
        /// <returns>A disposable snapshot that opens byte-balanced, non-overlapping physical readers.<br/></returns>
        public GroupBulkRecordPartitionSet OpenGroupBulkRecordPartitions(
            byte groupKey,
            int partitionCount,
            int readWindowBytes = 256 * 1024)
            => new(this, groupKey, partitionCount, readWindowBytes);

        /// <summary>
        /// Opens one sequential producer of leased pooled physical-record blocks for a complete identity group.<br/>
        /// The producer materializes authoritative compact locations once, then coalesces physically adjacent headers and payloads into bounded windows consumed without per-record payload copies.<br/>
        /// Produced blocks are unordered with respect to Fractal natural order and must be disposed before the producer.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <param name="blockBytes">Preferred maximum physical span of one leased block; an oversized individual record expands only its own block.<br/></param>
        /// <returns>A sequential block producer positioned before its first physical record.<br/></returns>
        public GroupBulkRecordBlockProducer OpenGroupBulkRecordBlockProducer(
            byte groupKey,
            int blockBytes = 256 * 1024)
            => new(this, groupKey, blockBytes);

        /// <summary>
        /// Opens a forward-only, header-only reader for identities whose upper byte equals <paramref name="groupKey"/>.<br/>
        /// The reader shares Fractal's allocation-stable bucket traversal and never reads record payload bodies.<br/>
        /// </summary>
        /// <param name="groupKey">Value to match against the upper eight bits of each live record identity.<br/></param>
        /// <returns>A reader positioned before the first matching identity.<br/></returns>
        public GroupIdentityReader OpenGroupIdentityReader(byte groupKey)
            => new GroupIdentityReader(this, groupKey);

        /// <summary>
        /// Reads one complete logical group in physical data-file order through a reusable pooled window.<br/>
        /// The reader retains compact offset/header metadata for the selected group, but never retains record payloads or consumer materializations beyond the current window.<br/>
        /// </summary>
        public sealed class GroupBulkRecordReader : IDisposable
        {
            private readonly FractalStore owner;
            private readonly GroupRecordLocation[] locations;
            private readonly int locationCount;
            private readonly int readWindowBytes;
            private readonly bool ownsLocations;
            private readonly GroupBulkRecordPartitionSet? partitionOwner;
            private byte[] window = Array.Empty<byte>();
            private ulong windowOffset;
            private int windowLength;
            private int nextLocationIndex;
            private int currentPayloadOffset;
            private int currentPayloadLength;
            private ulong currentIdentity;
            private int currentNaturalOrdinal = -1;
            private long ordinal = -1;
            private bool hasCurrent;
            private bool disposed;

            /// <summary>
            /// Creates one physical-order reader from an authoritative compact group-location snapshot.<br/>
            /// </summary>
            /// <param name="owner">Open Fractal store owning the data and index files.<br/></param>
            /// <param name="groupKey">Upper-byte identity group to expose.<br/></param>
            /// <param name="readWindowBytes">Preferred pooled data-file read-ahead size.<br/></param>
            internal GroupBulkRecordReader(FractalStore owner, byte groupKey, int readWindowBytes)
            {
                if (readWindowBytes < RecordHeader.SizeOf)
                    throw new ArgumentOutOfRangeException(nameof(readWindowBytes));
                this.owner = owner;
                this.readWindowBytes = readWindowBytes;
                GroupRecordLocationBuffer locationBuffer = owner.RentGroupRecordLocationsByPhysicalOrder(groupKey);
                locations = locationBuffer.Buffer;
                locationCount = locationBuffer.Count;
                ownsLocations = true;
            }

            /// <summary>
            /// Creates one reader over a disjoint borrowed range in an already materialized physical-location snapshot.<br/>
            /// The owning partition set retains the shared location array; this reader owns only its pooled data window.<br/>
            /// </summary>
            /// <param name="partitionOwner">Snapshot owner that must outlive this reader.<br/></param>
            /// <param name="startIndex">Inclusive first physical-location ordinal.<br/></param>
            /// <param name="endIndex">Exclusive final physical-location ordinal.<br/></param>
            /// <param name="readWindowBytes">Preferred pooled data-file read-ahead size.<br/></param>
            internal GroupBulkRecordReader(
                GroupBulkRecordPartitionSet partitionOwner,
                int startIndex,
                int endIndex,
                int readWindowBytes)
            {
                ArgumentNullException.ThrowIfNull(partitionOwner);
                if (readWindowBytes < RecordHeader.SizeOf)
                    throw new ArgumentOutOfRangeException(nameof(readWindowBytes));
                if (startIndex < 0 || endIndex < startIndex || endIndex > partitionOwner.LocationCount)
                    throw new ArgumentOutOfRangeException(nameof(startIndex));

                this.partitionOwner = partitionOwner;
                owner = partitionOwner.Owner;
                locations = partitionOwner.Locations;
                nextLocationIndex = startIndex;
                locationCount = endIndex;
                this.readWindowBytes = readWindowBytes;
                ownsLocations = false;
            }

            /// <summary>
            /// Gets the zero-based physical-order ordinal of the current record, or -1 before the first successful read.<br/>
            /// </summary>
            public long Ordinal => ordinal;

            /// <summary>
            /// Gets the identity of the current authoritative record.<br/>
            /// </summary>
            public ulong ID
            {
                get
                {
                    EnsureCurrent();
                    return currentIdentity;
                }
            }

            /// <summary>
            /// Gets the current record's zero-based ordinal in Fractal's authoritative natural bucket/collision order.<br/>
            /// Physical bulk traversal may emit a different order; this retained ordinal lets complete consumers restore natural order without rebuilding the header graph.<br/>
            /// </summary>
            public int NaturalOrdinal
            {
                get
                {
                    EnsureCurrent();
                    return currentNaturalOrdinal;
                }
            }

            /// <summary>
            /// Gets borrowed payload memory valid only until the next read or disposal.<br/>
            /// </summary>
            public ReadOnlyMemory<byte> Data
            {
                get
                {
                    EnsureCurrent();
                    return window.AsMemory(currentPayloadOffset, currentPayloadLength);
                }
            }

            /// <summary>
            /// Gets a borrowed payload span valid only until the next read or disposal.<br/>
            /// </summary>
            public ReadOnlySpan<byte> DataSpan
            {
                get
                {
                    EnsureCurrent();
                    return window.AsSpan(currentPayloadOffset, currentPayloadLength);
                }
            }

            /// <summary>
            /// Advances to the next authoritative record in physical data-file order.<br/>
            /// A new contiguous window is read only when the requested header and payload are not already contained in the current pooled window.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when a borrowed current payload is available; otherwise <see langword="false"/> at end of input.<br/></returns>
            public bool Read()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                partitionOwner?.EnsureUsable();
                hasCurrent = false;
                currentPayloadOffset = 0;
                currentPayloadLength = 0;
                currentIdentity = 0;
                currentNaturalOrdinal = -1;
                if (nextLocationIndex >= locationCount)
                    return false;

                GroupRecordLocation location = locations[nextLocationIndex++];
                int requiredLength;
                try
                {
                    requiredLength = checked(RecordHeader.SizeOf + (int)location.Header.DataLength);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        $"Record '{location.Header.ID}' payload length exceeds the supported in-memory bulk-reader range.",
                        exception);
                }

                ulong requiredEnd = checked(location.Offset + (ulong)requiredLength);
                ulong windowEnd = checked(windowOffset + (ulong)windowLength);
                if (windowLength == 0 || location.Offset < windowOffset || requiredEnd > windowEnd)
                    FillWindow(location.Offset, requiredLength);

                int relativeOffset = checked((int)(location.Offset - windowOffset));
                RecordHeader observed = MemoryMarshal.Read<RecordHeader>(
                    window.AsSpan(relativeOffset, RecordHeader.SizeOf));
                if (!HeaderEquals(observed, location.Header))
                {
                    throw new InvalidDataException(
                        $"Record '{location.Header.ID}' changed while its Fractal bulk-read snapshot was active.");
                }

                currentIdentity = observed.ID;
                currentNaturalOrdinal = location.NaturalOrdinal;
                currentPayloadOffset = checked(relativeOffset + RecordHeader.SizeOf);
                currentPayloadLength = checked((int)observed.DataLength);
                hasCurrent = true;
                ordinal++;
                return true;
            }

            /// <summary>
            /// Returns the pooled read-ahead buffer and invalidates the current borrowed payload.<br/>
            /// Calling disposal more than once is harmless.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                hasCurrent = false;
                currentPayloadOffset = 0;
                currentPayloadLength = 0;
                currentIdentity = 0;
                currentNaturalOrdinal = -1;
                windowLength = 0;
                if (window.Length != 0)
                {
                    ArrayPool<byte>.Shared.Return(window);
                    window = Array.Empty<byte>();
                }
                if (ownsLocations && locations.Length != 0)
                    ArrayPool<GroupRecordLocation>.Shared.Return(locations);
            }

            /// <summary>
            /// Reads a bounded contiguous window beginning at one authoritative record header.<br/>
            /// </summary>
            /// <param name="offset">Physical offset of the required record header.<br/></param>
            /// <param name="requiredLength">Minimum bytes needed for the complete header and payload.<br/></param>
            private void FillWindow(ulong offset, int requiredLength)
            {
                ulong fileLength = checked((ulong)owner.fData.LogicalLength);
                if (offset > fileLength || fileLength - offset < (ulong)requiredLength)
                {
                    throw new InvalidDataException(
                        $"Bulk record range at offset {offset} exceeds the current Fractal data file.");
                }

                int desiredLength = Math.Max(readWindowBytes, requiredLength);
                int readLength = checked((int)Math.Min((ulong)desiredLength, fileLength - offset));
                if (window.Length < readLength)
                {
                    if (window.Length != 0)
                        ArrayPool<byte>.Shared.Return(window);
                    window = ArrayPool<byte>.Shared.Rent(readLength);
                }

                owner.fData.ReadRange(window.AsSpan(0, readLength), offset);
                windowOffset = offset;
                windowLength = readLength;
            }

            /// <summary>
            /// Compares every persisted header field that governs identity, payload integrity, collision topology, mutation history, or physical sizing.<br/>
            /// </summary>
            /// <param name="left">Header observed in the current data-file window.<br/></param>
            /// <param name="right">Header captured from the authoritative topology snapshot.<br/></param>
            /// <returns><see langword="true"/> only when the headers describe the same physical record generation.<br/></returns>
            internal static bool HeaderEquals(in RecordHeader left, in RecordHeader right)
                => left.ID == right.ID &&
                   left.NextRecordOffset == right.NextRecordOffset &&
                   left.PriorMutationOffset == right.PriorMutationOffset &&
                   left.DataChecksum == right.DataChecksum &&
                   left.NextRecordSize == right.NextRecordSize &&
                   left.PriorMutationSize == right.PriorMutationSize &&
                   left.RecordSize == right.RecordSize &&
                   left.DataLength == right.DataLength;

            /// <summary>
            /// Rejects borrowed-payload access before the first successful read or after advance/disposal invalidated the current record.<br/>
            /// </summary>
            private void EnsureCurrent()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!hasCurrent)
                    throw new InvalidOperationException("The bulk reader is not positioned on a record.");
            }
        }

        /// <summary>
        /// Reads one logical Fractal group without iterator-state allocation or per-record payload copying.<br/>
        /// The reader owns the current pooled record; callers borrow its identity and payload until the reader advances.<br/>
        /// </summary>
        public sealed class GroupRecordReader : IDisposable
        {
            private const ulong GroupMask = 0xFF00_0000_0000_0000UL;
            private readonly FractalStore owner;
            private readonly FIndex.BucketReader buckets;
            private readonly ulong expected;
            private Record current;
            private ulong nextOffset;
            private uint nextSize;
            private ulong lastAdvancedIdentity;
            private long ordinal = -1;
            private bool hasCurrent;
            private bool disposed;

            /// <summary>
            /// Creates a reader over one upper-byte logical group.<br/>
            /// </summary>
            /// <param name="owner">Open Fractal store that owns the index and data files.<br/></param>
            /// <param name="groupKey">Upper-byte identity group to expose.<br/></param>
            internal GroupRecordReader(FractalStore owner, byte groupKey)
            {
                this.owner = owner;
                buckets = owner.fIndex.OpenBucketReader();
                expected = (ulong)groupKey << 56;
            }

            /// <summary>
            /// Gets the zero-based ordinal of the current matching record, or -1 before the first successful read.<br/>
            /// </summary>
            public long Ordinal => ordinal;

            /// <summary>
            /// Gets the identity most recently consumed by either <see cref="Read"/> or <see cref="Skip(int)"/>.<br/>
            /// The value remains available after a skip even though borrowed current-record payload access is intentionally invalidated.<br/>
            /// Zero is returned before the reader has consumed its first matching record.<br/>
            /// </summary>
            public ulong LastAdvancedIdentity => lastAdvancedIdentity;

            /// <summary>
            /// Gets the identity of the current record.<br/>
            /// </summary>
            public ulong ID
            {
                get
                {
                    EnsureCurrent();
                    return current.Header.ID;
                }
            }

            /// <summary>
            /// Gets borrowed payload memory for the current record without copying.<br/>
            /// The memory is valid only until the next call to <see cref="Read"/>, <see cref="Skip"/>, or <see cref="Dispose"/>.<br/>
            /// </summary>
            public ReadOnlyMemory<byte> Data
            {
                get
                {
                    EnsureCurrent();
                    return current.Data;
                }
            }

            /// <summary>
            /// Gets a borrowed payload span for the current record without copying.<br/>
            /// The span is valid only until the next call to <see cref="Read"/>, <see cref="Skip"/>, or <see cref="Dispose"/>.<br/>
            /// </summary>
            public ReadOnlySpan<byte> DataSpan
            {
                get
                {
                    EnsureCurrent();
                    return current.DataSpan;
                }
            }

            /// <summary>
            /// Advances to the next live record in the configured group.<br/>
            /// Advancing first returns the prior pooled record, then follows any remaining collision chain before advancing the index-bucket reader.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when a matching current record is available; otherwise <see langword="false"/> at end of input.<br/></returns>
            public bool Read()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                ReleaseCurrent();

                while (true)
                {
                    if (nextOffset == 0)
                    {
                        if (!buckets.Read())
                            return false;

                        BucketEntry entry = buckets.Current;
                        nextOffset = entry.RecordOffset;
                        nextSize = entry.RecordSize;
                    }

                    ulong recordOffset = nextOffset;
                    uint recordSize = nextSize;
                    nextOffset = 0;
                    nextSize = 0;

                    Record candidate = owner.fData.ReadRecord(recordOffset, recordSize);
                    ulong id = candidate.Header.ID;
                    nextOffset = candidate.Header.NextRecordOffset;
                    nextSize = candidate.Header.NextRecordSize;
                    if (id == 0 || (id & GroupMask) != expected)
                    {
                        candidate.Dispose();
                        continue;
                    }

                    current = candidate;
                    hasCurrent = true;
                    lastAdvancedIdentity = id;
                    ordinal++;
                    return true;
                }
            }

            /// <summary>
            /// Advances to the next live record in the configured group.<br/>
            /// This is an alias for <see cref="Read"/> for compatibility with ordinary forward-reader conventions.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when a matching current record is available; otherwise <see langword="false"/>.<br/></returns>
            public bool MoveNext() => Read();

            /// <summary>
            /// Advances past at most <paramref name="count"/> matching records without reading their payload bodies.<br/>
            /// Fractal walks index buckets and collision-chain headers only, preserving exact live/group filtering and the next unread chain position.<br/>
            /// A future index format may skip an entire bucket or tier when it persists a trustworthy matching-live-record count.<br/>
            /// </summary>
            /// <param name="count">Maximum number of matching records to skip; must not be negative.<br/></param>
            /// <returns>The number of records actually skipped before end of input.<br/></returns>
            public int Skip(int count)
            {
                if (count < 0)
                    throw new ArgumentOutOfRangeException(nameof(count));
                ObjectDisposedException.ThrowIf(disposed, this);
                ReleaseCurrent();

                int skipped = 0;
                while (skipped < count)
                {
                    if (nextOffset == 0)
                    {
                        if (!buckets.Read())
                            break;

                        BucketEntry entry = buckets.Current;
                        nextOffset = entry.RecordOffset;
                        nextSize = entry.RecordSize;
                    }

                    ulong recordOffset = nextOffset;
                    nextOffset = 0;
                    nextSize = 0;

                    RecordHeader header = owner.fData.ReadRecordHeader(recordOffset);
                    nextOffset = header.NextRecordOffset;
                    nextSize = header.NextRecordSize;
                    ulong id = header.ID;
                    if (id == 0 || (id & GroupMask) != expected)
                        continue;

                    lastAdvancedIdentity = id;
                    skipped++;
                    ordinal++;
                }
                return skipped;
            }

            /// <summary>
            /// Returns the current pooled record and releases index traversal state.<br/>
            /// Calling disposal more than once is harmless.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                ReleaseCurrent();
                buckets.Dispose();
                nextOffset = 0;
                nextSize = 0;
            }

            /// <summary>
            /// Returns the current pooled record exactly once.<br/>
            /// </summary>
            private void ReleaseCurrent()
            {
                if (!hasCurrent)
                    return;
                current.Dispose();
                current = default;
                hasCurrent = false;
            }

            /// <summary>
            /// Rejects access before the first successful read or after the current record has been released.<br/>
            /// </summary>
            private void EnsureCurrent()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!hasCurrent)
                    throw new InvalidOperationException("The reader is not positioned on a record.");
            }
        }

        /// <summary>
        /// Reads one logical Fractal group's identities without payload reads, compiler-generated recursive index iterators, or per-record managed buffers.<br/>
        /// Collision chains remain authoritative: each header supplies both the current identity and the next physical chain member.<br/>
        /// </summary>
        public sealed class GroupIdentityReader : IDisposable
        {
            private const ulong GroupMask = 0xFF00_0000_0000_0000UL;
            private readonly FractalStore owner;
            private readonly FIndex.BucketReader buckets;
            private readonly ulong expected;
            private ulong currentIdentity;
            private ulong nextOffset;
            private long ordinal = -1;
            private bool hasCurrent;
            private bool disposed;

            /// <summary>
            /// Creates a header-only reader over one upper-byte logical group.<br/>
            /// </summary>
            /// <param name="owner">Open Fractal store that owns the index and data files.<br/></param>
            /// <param name="groupKey">Upper-byte identity group to expose.<br/></param>
            internal GroupIdentityReader(FractalStore owner, byte groupKey)
            {
                this.owner = owner;
                buckets = owner.fIndex.OpenBucketReader();
                expected = (ulong)groupKey << 56;
            }

            /// <summary>
            /// Gets the zero-based ordinal of the current matching identity, or -1 before the first successful read.<br/>
            /// </summary>
            public long Ordinal => ordinal;

            /// <summary>
            /// Gets the current matching record identity.<br/>
            /// </summary>
            public ulong ID
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    if (!hasCurrent)
                        throw new InvalidOperationException("The reader is not positioned on an identity.");
                    return currentIdentity;
                }
            }

            /// <summary>
            /// Advances to the next live identity in the configured group.<br/>
            /// The reader follows the current collision chain before advancing to the next live leaf bucket.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when a matching identity is current; otherwise <see langword="false"/> at end of input.<br/></returns>
            public bool Read()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                hasCurrent = false;

                while (true)
                {
                    if (nextOffset == 0)
                    {
                        if (!buckets.Read())
                            return false;
                        nextOffset = buckets.Current.RecordOffset;
                    }

                    ulong recordOffset = nextOffset;
                    nextOffset = 0;

                    RecordHeader header = owner.fData.ReadRecordHeader(recordOffset);
                    nextOffset = header.NextRecordOffset;
                    ulong id = header.ID;
                    if (id == 0 || (id & GroupMask) != expected)
                        continue;

                    currentIdentity = id;
                    hasCurrent = true;
                    ordinal++;
                    return true;
                }
            }

            /// <summary>
            /// Advances to the next live identity in the configured group.<br/>
            /// This is an alias for <see cref="Read"/> for compatibility with ordinary forward-reader conventions.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when a matching identity is current; otherwise <see langword="false"/>.<br/></returns>
            public bool MoveNext() => Read();

            /// <summary>
            /// Releases the index traversal state owned by this reader.<br/>
            /// Calling disposal more than once is harmless.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                hasCurrent = false;
                nextOffset = 0;
                buckets.Dispose();
            }
        }

        /// <summary>Enumerates records visible at the index's current leaf entries.<br/></summary>
        /// <returns>Disposable records; dispose each yielded record after consuming its payload.<br/></returns>
        public IEnumerable<Record> IterateRecords()
        {

            foreach (var key in fIndex.IterateKeys())
            {
                if (key.IsUnset) continue;

                var record = fData.ReadRecord(key.RecordOffset, key.RecordSize);
                yield return record;
            }
        }

        internal IEnumerable<ulong> IterateRecordsIDs()
        {

            foreach (var key in fIndex.IterateKeys())
            {
                if (key.IsUnset) continue;

                var record = fData.ReadRecordHeader(key.RecordOffset);
                yield return record.ID;
            }
        }

        /// <summary>
        /// Determines whether persisted and current path/name pairs identify the same logical filesystem location under the host operating system's stable Fractal path contract.<br/>
        /// Windows comparisons are ordinal and case-insensitive because drive, directory, and filename casing does not ordinarily distinguish logical locations, while non-Windows comparisons preserve the existing ordinal case-sensitive behavior.<br/>
        /// This method deliberately does not resolve symbolic links, junctions, or alternate mount aliases: a different logical path remains an ownership change even when the operating system can reach the same physical file through it.<br/>
        /// No normalization or case-converted strings are allocated, and the persisted header bytes are never rewritten merely because the caller supplied different Windows casing.<br/>
        /// </summary>
        /// <param name="storedPath">Directory path decoded from the Fractal file header.<br/></param>
        /// <param name="storedName">Filename decoded from the Fractal file header.<br/></param>
        /// <param name="currentPath">Directory path reported by the currently opened file controller.<br/></param>
        /// <param name="currentName">Filename reported by the currently opened file controller.<br/></param>
        /// <returns><see langword="true"/> only when both path and filename are equivalent under the host-specific comparison contract.<br/></returns>
        internal static bool StoredLocationMatches(
            string? storedPath,
            string? storedName,
            string? currentPath,
            string? currentName)
        {
            if (storedPath is null || storedName is null || currentPath is null || currentName is null)
                return false;

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(storedPath, currentPath, comparison) &&
                string.Equals(storedName, currentName, comparison);
        }

        /// <summary>
        /// Resets the index and data headers to the current folder/name after intentionally moving or copying a store.<br/>
        /// This rewrites only the stored path/name metadata and header checksums; no payload or index data is moved.<br/>
        /// Call on an offline store before <see cref="Open"/> when reclaiming a relocated copy.<br/>
        /// </summary>
        public static void TakeOwnership(string folderPath, string name, FractalStoreConfiguration config)
        {
            if (config is null)
                throw new ArgumentNullException(nameof(config));

            var index = new FIndex(folderPath, name, config);
            var data = new FData(folderPath, name, config);
            try
            {
                index.TakeOwnership();
                data.TakeOwnership();
                index.fc.FlushToDisk();
                data.fc.FlushToDisk();
                index.fc.atomics.Flush(flushToDisk: true);
                data.fc.atomics.Flush(flushToDisk: true);
            }
            finally
            {
                try { index.fc.Dispose(); } catch { }
                try { data.fc.Dispose(); } catch { }
                try { index.fc.atomics.Dispose(); } catch { }
                try { data.fc.atomics.Dispose(); } catch { }
            }
        }

        /// <summary>Initializes and opens the data and index files for record operations.<br/></summary>
        public void Open()
        {
            fData.Init();
            fIndex.Init();
            telemetry = new FractalStoreTelemetry
            {
                DataFileSyscallsInternal = fData.fc.syscallPerfTracker,
                IndexFileSyscallsInternal = fIndex.fc.syscallPerfTracker,
                BucketTelemetryInternal = new BucketTierTelemetry()
            };

            fIndex.fc.BucketTelemetry = telemetry.BucketTelemetry;
            BucketTier.ConfigureCycleDebug(config.EnableIndexCycleDebug);
        }

        /// <summary>
        /// Writes a known record sequence under one Fractal mutation scope using either one bounded sequential loop or the configured native worker channel.<br/>
        /// Call-scoped worker selection permits higher layers to compare identical bulk semantics without mutating shared store configuration.<br/>
        /// Optional progress is emitted at bounded intervals and callback failures are contained so diagnostics cannot change durable mutation outcome.<br/>
        /// Cancellation after a partial sequential write flushes the completed prefix, then throws rather than reporting full-batch success.<br/>
        /// Parallel cancellation also waits for every worker to stop before flushing and reporting a partial result.<br/>
        /// </summary>
        /// <param name="records">Record identities and borrowed payload memory to persist.<br/></param>
        /// <param name="recordsCount">Known input count; when omitted the sequence is counted before mutation.<br/></param>
        /// <param name="tokenSource">Optional cooperative cancellation source shared with worker failures.<br/></param>
        /// <param name="maximumDegreeOfParallelism">Optional call-scoped worker maximum; one retains the bulk scope while using a sequential physical loop.<br/></param>
        /// <param name="progress">Optional bounded observer receiving processed-record counts from the physical Fractal stage.<br/></param>
        public void PutMany(
            IEnumerable<(ulong ID, Memory<byte> Data)> records,
            int? recordsCount = default,
            CancellationTokenSource? tokenSource = null,
            int? maximumDegreeOfParallelism = default,
            Action<FractalPutManyProgress>? progress = null)
        {
            using var mutation = EnterMutationScope();
            if (!records.Any())
            {
                try { progress?.Invoke(new FractalPutManyProgress(0, 0, 1, true)); } catch { }
                return;
            }

            recordsCount ??= records.Count();
            var token = tokenSource?.Token ?? CancellationToken.None;
            int totalCount = recordsCount.GetValueOrDefault();
            int workerCount = maximumDegreeOfParallelism ?? config.PutManyParallelThreadCount;
            if (workerCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumDegreeOfParallelism), "Maximum degree of parallelism must be positive when supplied.");
            workerCount = Math.Min(workerCount, Math.Max(1, totalCount));
            int reportInterval = Math.Max(1, (totalCount + 199) / 200);
            int nextReportAt = reportInterval;
            var progressGate = new object();

            void ReportProgress(int processedCount, bool completed)
            {
                if (progress is null)
                    return;
                if (!completed && processedCount < Volatile.Read(ref nextReportAt))
                    return;

                lock (progressGate)
                {
                    if (!completed && processedCount < nextReportAt)
                        return;
                    int processed = Math.Clamp(processedCount, 0, totalCount);
                    nextReportAt = completed || processed >= totalCount
                        ? int.MaxValue
                        : Math.Min(totalCount, processed + reportInterval);
                    try
                    {
                        progress(new FractalPutManyProgress(processed, totalCount, workerCount, completed));
                    }
                    catch
                    {
                        // Progress is diagnostic and must never change Fractal mutation outcome.
                    }
                }
            }

            if (totalCount < config.PutManyParallelMinRecordCount || workerCount == 1)
            {
                bool anyChanged = false;
                int processedCount = 0;
                foreach (var (id, data) in records)
                {
                    if (token.IsCancellationRequested) break;
                    if (PutOneInternal(id, data.Span, deferUsnFlush: true))
                        anyChanged = true;
                    ReportProgress(++processedCount, completed: false);
                }

                if (anyChanged)
                    FlushStoreUsnIfImmediate();
                if (processedCount < totalCount)
                    token.ThrowIfCancellationRequested();
                ReportProgress(processedCount, completed: true);
            }
            else
            {
                ConcurrentDictionary<int, byte> threadIds = new();
                var procChannel = Channel.CreateUnbounded<(ulong ID, Memory<byte> Data)>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
                var reader = procChannel.Reader;
                var writer = procChannel.Writer;
                var workers = new List<Task>();
                int anyChanged = 0;
                int processedCount = 0;
                for (int i = 0; i < workerCount; i++)
                {

                    workers.Add(Task.Run(async () =>
                    {
                        threadIds.TryAdd(Environment.CurrentManagedThreadId, 0);
                        try
                        {
                            while (await reader.WaitToReadAsync(token))
                            {
                                while (reader.TryRead(out var item))
                                {
                                    if (token.IsCancellationRequested)
                                        break;
                                    if (PutOneInternal(item.ID, item.Data.Span, deferUsnFlush: true))
                                        Interlocked.Exchange(ref anyChanged, 1); // worker logic
                                    int processed = Interlocked.Increment(ref processedCount);
                                    ReportProgress(processed, completed: false);
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            tokenSource?.Cancel();
                            writer.TryComplete(ex);
                            throw;
                        }
                    }));
                }

                foreach (var rec in records)
                {
                    if (token.IsCancellationRequested)
                        break;
                    writer.TryWrite((rec.ID, rec.Data));
                }
                writer.TryComplete();

                try
                {
                    // Cancellation must not release the store mutation scope while workers still own physical writes.
                    Task.WaitAll(workers.ToArray());
                }
                finally
                {
                    if (anyChanged == 1)
                        FlushStoreUsnIfImmediate();
                }
                if (processedCount < totalCount)
                    token.ThrowIfCancellationRequested();
                Console.WriteLine("Threads actually used: " + threadIds.Count);

                ReportProgress(processedCount, completed: true);

            }
        }

        /// <summary>Creates or replaces one record payload under the supplied numeric identity.<br/></summary>
        /// <param name="recordID">Nonzero logical identity of the record to persist.<br/></param>
        /// <param name="data">Payload bytes copied into the durable Fractal data file.<br/></param>
        public void PutOne(ulong recordID, ReadOnlySpan<byte> data)
        {
            using var mutation = EnterMutationScope();
            _ = PutOneInternal(recordID, data);
        }

        internal void PutOneFast(ulong recordID, ReadOnlySpan<byte> data)
        {
            using var mutation = EnterMutationScope();
            _ = PutOneInternal(recordID, data);
        }

        /// <summary>
        /// Counts all non-deleted records by walking every leaf bucket chain.
        /// Buckets flagged as single-record chains contribute a constant cost of one,
        /// while the remaining buckets fall back to a full chain walk.
        /// </summary>
        public ulong RecordCount() => RecordCountInternal(useMask: false, 0, 0);

        /// <summary>
        /// Counts all non-deleted records whose identifiers satisfy the specified mask comparison.<br/>
        /// Example: count only records for a specific store by masking the upper 8 bits that encode the store ID.
        /// </summary>
        /// <param name="mask">Bitmask applied to each record ID before comparison.</param>
        /// <param name="expectedValue">Value compared against the masked record ID.</param>
        /// <returns>The number of records whose IDs satisfy <c>(ID &amp; mask) == expectedValue</c>.</returns>
        public ulong RecordCount(ulong mask, ulong expectedValue)
        {
            if (mask == 0)
                throw new ArgumentOutOfRangeException(nameof(mask), "Mask must be non-zero.");

            expectedValue &= mask;
            return RecordCountInternal(useMask: true, mask, expectedValue);
        }

        /// <summary>Determines whether a live record with the supplied identity exists without loading its payload.<br/></summary>
        /// <param name="recordID">Logical identity to look up.<br/></param>
        /// <returns><see langword="true"/> when the record is present; otherwise <see langword="false"/>.<br/></returns>
        public bool RecordExists(ulong recordID)
        {
            var ret = GetRecordInfo(recordID);
            return ret.found;
        }

        /// <summary>Starts the background online-defragmentation worker when it is not already running.<br/></summary>
        public void StartOnlineDefrag() => _onlineDefrag.Start();

        /// <summary>Requests that the background online-defragmentation worker stop.<br/></summary>
        public void StopOnlineDefrag() => _onlineDefrag.Stop();

        /// <summary>Attempts to load one record without throwing for an absent identity.<br/></summary>
        /// <param name="recordID">Logical identity to load.<br/></param>
        /// <param name="record">When successful, receives a disposable record containing header and payload.<br/></param>
        /// <returns><see langword="true"/> when the record was loaded; otherwise <see langword="false"/>.<br/></returns>
        public bool TryGetOne(ulong recordID, out Record record)
        {
            record = default;

            var (found, recordOffset, chainIndex, header, tier, bucketIndex) = GetRecordInfo(recordID);
            if (!found)
                return false;

            record = fData.ReadRecord(recordOffset, header.DataLength);
            return true;
        }








        //====== TYPES ======
        /// <summary>Identifies the current lifecycle phase of the online-defragmentation worker.<br/></summary>
        public enum OnlineDefragState
        {
            /// <summary>The worker is not active.<br/></summary>
            Stopped,
            /// <summary>The worker is preparing its first scan.<br/></summary>
            Starting,
            /// <summary>The worker is locating reclaimable data-file regions.<br/></summary>
            Scanning,
            /// <summary>The worker is relocating live records to reclaim space.<br/></summary>
            Moving,
            /// <summary>The worker is idle and waiting before another scan.<br/></summary>
            IdleBackoff,
            /// <summary>The worker is completing its stop sequence.<br/></summary>
            Stopping
        }








        /// <summary>Supplies state and statistics for an online-defragmentation event.<br/></summary>
        public sealed class OnlineDefragEventArgs : EventArgs
        {
            //======  CONSTRUCTORS  ======
            /// <summary>Initializes one online-defragmentation event snapshot.<br/></summary>
            /// <param name="state">Worker state associated with the event.<br/></param>
            /// <param name="stats">Point-in-time statistics associated with the event.<br/></param>
            public OnlineDefragEventArgs(OnlineDefragState state, OnlineDefragStats stats)
            {
                State = state;
                Stats = stats ?? throw new ArgumentNullException(nameof(stats));
            }








            //======  PROPERTIES  ======
            /// <summary>Gets the worker state associated with the event.<br/></summary>
            public OnlineDefragState State { get; }
            /// <summary>Gets the point-in-time statistics associated with the event.<br/></summary>
            public OnlineDefragStats Stats { get; }
        }

        /// <summary>Reports point-in-time counters and positions for online defragmentation.<br/></summary>
        public sealed class OnlineDefragStats
        {
            //======  PROPERTIES  ======
            /// <summary>Gets the number of bytes moved by completed relocation operations.<br/></summary>
            public long BytesMoved { get; internal set; }
            /// <summary>Gets the logical bytes reclaimed since the current worker run began.<br/></summary>
            public long BytesReclaimed => Math.Max(0, LogicalLengthAtStart - LogicalLengthCurrent);
            /// <summary>Gets the number of reusable holes encountered by the worker.<br/></summary>
            public long HolesReused { get; internal set; }
            /// <summary>Gets whether the online-defragmentation worker is active.<br/></summary>
            public bool IsRunning { get; internal set; }
            /// <summary>Gets the number of scan-and-move worker iterations performed.<br/></summary>
            public long Iterations { get; internal set; }
            /// <summary>Gets the UTC time of the worker's most recent activity.<br/></summary>
            public DateTime LastActivityUtc { get; internal set; }
            /// <summary>Gets the physical offset of the most recently considered reclaimable hole.<br/></summary>
            public ulong LastHoleOffset { get; internal set; }
            /// <summary>Gets the UTC time of the most recent state transition.<br/></summary>
            public DateTime LastStateChangeUtc { get; internal set; }
            /// <summary>Gets the most recently observed logical data-file tail offset.<br/></summary>
            public ulong LastTailOffset { get; internal set; }
            /// <summary>Gets the logical data-file length captured when the current run began.<br/></summary>
            public long LogicalLengthAtStart { get; internal set; }
            /// <summary>Gets the latest logical data-file length observed by the worker.<br/></summary>
            public long LogicalLengthCurrent { get; internal set; }
            /// <summary>Gets the number of relocation operations attempted.<br/></summary>
            public long MovesAttempted { get; internal set; }
            /// <summary>Gets the number of relocation operations completed.<br/></summary>
            public long MovesCompleted { get; internal set; }
            /// <summary>Gets the worker's current lifecycle state.<br/></summary>
            public OnlineDefragState State { get; internal set; }








            //------ Public Methods -----
            //======  METHODS  ======
            /// <summary>Creates an independent shallow snapshot of the current statistics.<br/></summary>
            /// <returns>A copy whose scalar values are independent of later worker updates.<br/></returns>
            public OnlineDefragStats Clone()
                            => (OnlineDefragStats)MemberwiseClone();
        }

        private sealed class OnlineDefragmenter
        {
            //======  FIELDS  ======
            private CancellationTokenSource? _cts;
            private DateTime _lastProgressUtc = DateTime.MinValue;
            private readonly FractalStore _owner;
            private OnlineDefragStats _stats = new OnlineDefragStats();
            private readonly object _sync = new();
            private Task? _task;








            //======  CONSTRUCTORS  ======
            internal OnlineDefragmenter(FractalStore owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }








            //======  PROPERTIES  ======
            internal bool IsRunning
            {
                get
                {
                    lock (_sync)
                    {
                        return _stats.IsRunning;
                    }
                }
            }








            //======  METHODS  ======
            internal OnlineDefragStats GetStats()
            {
                lock (_sync)
                {
                    _stats.LogicalLengthCurrent = _owner.fData.LogicalLength;
                    return _stats.Clone();
                }
            }
            // --------- Public-facing entry points (called by outer FractalStore) ---------

            internal void Start()
            {
                OnlineDefragStats snapshot;

                DefragLog(OnlineDefragLogLevel.Info, "StartOnlineDefrag requested.");

                lock (_sync)
                {
                    if (_task is { IsCompleted: false })
                        return; // already running

                    _cts = new CancellationTokenSource();
                    var token = _cts.Token;
                    var now = DateTime.UtcNow;
                    var logicalLength = _owner.fData.LogicalLength;

                    _stats = new OnlineDefragStats
                    {
                        IsRunning = true,
                        State = OnlineDefragState.Starting,
                        LastActivityUtc = now,
                        LastStateChangeUtc = now,
                        LogicalLengthAtStart = logicalLength,
                        LogicalLengthCurrent = logicalLength
                    };

                    _lastProgressUtc = DateTime.MinValue;
                    _task = Task.Run(() => RunLoop(token), token);
                    snapshot = _stats.Clone();
                }

                _owner.RaiseOnlineDefragStarted(snapshot);
                _owner.RaiseOnlineDefragProgress(snapshot);
            }

            internal void Stop()
            {
                CancellationTokenSource? cts;

                DefragLog(OnlineDefragLogLevel.Info, "StopOnlineDefrag requested.");
                Task? task;
                bool wasRunning;
                OnlineDefragStats? stoppingSnapshot = null;

                lock (_sync)
                {
                    wasRunning = _task is { IsCompleted: false };

                    if (wasRunning)
                    {
                        var now = DateTime.UtcNow;
                        SetState(OnlineDefragState.Stopping, now);
                        _stats.LastActivityUtc = now;
                        stoppingSnapshot = _stats.Clone();
                    }

                    cts = _cts;
                    task = _task;
                    _cts = null;
                    _task = null;
                }

                if (stoppingSnapshot is not null)
                    PublishProgress(stoppingSnapshot, force: true);

                if (cts is not null)
                {
                    cts.Cancel();
                    try
                    {
                        task?.Wait();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
                    {
                    }
                }

                if (cts is not null)
                    return; // RunLoop will publish stopped when it exits on cancellation.

                if (!wasRunning)
                {
                    var stopped = SetStoppedSnapshot();
                    _owner.RaiseOnlineDefragStopped(stopped);
                    PublishProgress(stopped, force: true);
                }
            }








            private void PublishProgress(OnlineDefragStats snapshot, bool force = false)
            {
                var now = DateTime.UtcNow;
                bool shouldPublish;

                lock (_sync)
                {
                    shouldPublish = force || now - _lastProgressUtc >= TimeSpan.FromSeconds(1);

                    if (shouldPublish)
                        _lastProgressUtc = now;
                }

                if (shouldPublish)
                    _owner.RaiseOnlineDefragProgress(snapshot);
            }

            /// <summary>
            /// Re-links the moved record into the bucket chain and/or A/B index.
            /// Must be called only while the appropriate bucket stripe lock is held.
            /// </summary>
            private void RelinkRecordAfterMove(ulong recordID, ulong fromOffset, ulong toOffset, uint dataLength)
            {
                var fData = _owner.fData;
                var fIndex = _owner.fIndex;

                // Locate the bucket where this record lives.
                var (tier, bucketIndex, bucket) = fIndex.GetBucketEntryInfo(recordID);

                if (bucket.IsUnset)
                    return; // Nothing we can do; treat as best-effort.

                // NOTE: bucket.RecordOffset should give you the active A/B offset.
                ulong currentOffset = bucket.RecordOffset;
                ulong prevOffset = 0;

                while (true)
                {
                    if (currentOffset == fromOffset)
                        break;

                    var h = fData.ReadRecordHeader(currentOffset);

                    if (h.NextRecordOffset == 0)
                    {
                        currentOffset = 0;
                        break;
                    }

                    prevOffset = currentOffset;
                    currentOffset = h.NextRecordOffset;
                }

                if (currentOffset == 0)
                {
                    // This ID isn't actually in this bucket's chain anymore;
                    // someone may have mutated it concurrently. Bail out.
                    return;
                }

                // Case 1: record is the head of the bucket chain (prevOffset == 0)
                if (prevOffset == 0)
                {
                    // Keep the A/B durability dance at the index level.
                    fIndex.UpdateRecordOffset(recordID, toOffset, dataLength);
                }
                else
                {
                    // Case 2: non-head: just fix the prev.NextRecordOffset.
                    var prevHeader = fData.ReadRecordHeader(prevOffset);
                    prevHeader.NextRecordOffset = toOffset;
                    prevHeader.NextRecordSize = dataLength;
                    fData.UpdateRecordHeader(prevOffset, prevHeader);
                }

                // The moved record's own NextRecordOffset/Size were preserved when we
                // copied the header into the hole, so the rest of the chain is already correct.
            }
            // --------- Background loop ---------

            private void RunLoop(CancellationToken token)
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        lock (_sync)
                        {
                            var now = DateTime.UtcNow;
                            SetState(OnlineDefragState.Scanning, now);
                            _stats.LogicalLengthCurrent = _owner.fData.LogicalLength;
                        }

                        bool moved = false;
                        DefragLog(OnlineDefragLogLevel.Trace, $"Scan pass. LogicalLength={_owner.fData.LogicalLength} bytes.");


                        try
                        {
                            moved = TryDefragTailOnce(token);
                        }
                        catch
                        {
                            // TODO: hook logging if desired
                            moved = false;
                        }

                        OnlineDefragStats snapshot;
                        lock (_sync)
                        {
                            var now = DateTime.UtcNow;
                            _stats.Iterations++;
                            _stats.LastActivityUtc = now;
                            _stats.LogicalLengthCurrent = _owner.fData.LogicalLength;
                            SetState(moved ? OnlineDefragState.Moving : OnlineDefragState.IdleBackoff, now);
                            snapshot = _stats.Clone();
                        }

                        if (IsDefragLogEnabled(OnlineDefragLogLevel.Debug))
                            DefragLog(OnlineDefragLogLevel.Debug, $"Iteration={_stats.Iterations} moved={moved} state={snapshot.State} bytesMoved={snapshot.BytesMoved} bytesReclaimed={snapshot.BytesReclaimed}.");

                        PublishProgress(snapshot, force: moved);

                        if (!moved)
                        {
                            // Nothing to do right now; back off a bit
                            Task.Delay(TimeSpan.FromMilliseconds(250), token).Wait(token);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    var stoppedSnapshot = SetStoppedSnapshot();
                    _owner.RaiseOnlineDefragStopped(stoppedSnapshot);
                    PublishProgress(stoppedSnapshot, force: true);
                }
            }

            private void SetState(OnlineDefragState state, DateTime now)
            {
                _stats.State = state;
                _stats.LastStateChangeUtc = now;
            }

            private OnlineDefragStats SetStoppedSnapshot()
            {
                lock (_sync)
                {
                    var now = DateTime.UtcNow;
                    _stats.IsRunning = false;
                    SetState(OnlineDefragState.Stopped, now);
                    _stats.LastActivityUtc = now;
                    _stats.LogicalLengthCurrent = _owner.fData.LogicalLength;
                    return _stats.Clone();
                }
            }
            // --------- Bucket-stripe coordination (same mapping as PutOne) ---------

            private bool TryAcquireBucketStripeForRecord(ulong recordID, out int lockIndex)
            {
                var indexHeader = _owner.fIndex.Header;
                uint rootCount = indexHeader.RootBucketCount;

                // Same root bucket mapping as in PutOne
                ulong hash = FIndex.HashRecordID(recordID);
                ulong rootIndex = hash & (rootCount - 1);

                ulong bucketKey = rootIndex;
                ulong bucketHash = FIndex.HashRecordID(bucketKey);

                int stripe = (int)(bucketHash & BucketStripeMask);
                lockIndex = BucketStripeBase + stripe;

                var atomics = _owner.fIndex.fc.atomics;
                var spin = new SpinWait();

                const int MaxSpins = 32;
                for (int i = 0; i < MaxSpins; i++)
                {
                    if (atomics.TryAcquireSpinLock32(lockIndex))
                        return true;

                    spin.SpinOnce();
                }

                lockIndex = -1;
                return false;
            }
            // --------- Core single-pass defrag step ---------

            private bool TryDefragTailOnce(CancellationToken token)
            {
                using var mutation = _owner.EnterMutationScope();
                var fData = _owner.fData;
                var fIndex = _owner.fIndex;

                ulong fileEnd = (ulong)fData.LogicalLength;
                if (IsDefragLogEnabled(OnlineDefragLogLevel.Trace))
                    DefragLog(OnlineDefragLogLevel.Trace, $"TryDefragTailOnce: fileEnd={fileEnd}.");
                var firstRecordOffset = (ulong)fData.header.FirstRecordOffset;
                if (fileEnd <= firstRecordOffset)
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: fileEnd <= first record offset, nothing to do.");
                    return false;
                }

                // Fast-trim safe mode: only perform aggressive truncate when every record is dead.
                // If any live record is detected, skip fast-trim and let normal defrag handle movement safely.
                var fastTrimBatch = Math.Max(0, _owner.config.FastTrimTailBatchRecords);
                if (IsDefragLogEnabled(OnlineDefragLogLevel.Trace))
                    DefragLog(OnlineDefragLogLevel.Trace, $"FastTrimTail start batch={fastTrimBatch} fileEnd={fileEnd}.");
                if (fastTrimBatch > 0)
                {
                    var scanOffset = (ulong)fData.header.FirstRecordOffset;
                    var foundLive = false;

                    while (scanOffset + (uint)RecordHeader.SizeOf <= fileEnd)
                    {
                        token.ThrowIfCancellationRequested();

                        var header = fData.ReadRecordHeader(scanOffset);
                        if (header.RecordSize == 0)
                            break;

                        if (!fData.IsDeadRecord(fIndex, scanOffset, header))
                        {
                            foundLive = true;
                            break;
                        }

                        scanOffset += header.RecordSize;
                    }

                    if (!foundLive)
                    {
                        var newEnd = (ulong)fData.header.FirstRecordOffset;
                        if (newEnd < fileEnd)
                        {
                            if (IsDefragLogEnabled(OnlineDefragLogLevel.Debug))
                                DefragLog(OnlineDefragLogLevel.Debug, $"FastTrimTail aggressive oldEnd={fileEnd} newEnd={newEnd}.");

                            var trimmed = fData.TryTruncateTail(fileEnd, newEnd);
                            if (IsDefragLogEnabled(OnlineDefragLogLevel.Trace))
                                DefragLog(OnlineDefragLogLevel.Trace, $"FastTrimTail truncate attempt oldEnd={fileEnd} newEnd={newEnd} success={trimmed}.");

                            if (trimmed)
                                fileEnd = newEnd;
                            else if (IsDefragLogEnabled(OnlineDefragLogLevel.Debug))
                                DefragLog(OnlineDefragLogLevel.Debug, "FastTrimTail failed: EOF changed.");
                        }
                    }
                    else if (IsDefragLogEnabled(OnlineDefragLogLevel.Trace))
                    {
                        DefragLog(OnlineDefragLogLevel.Trace, "FastTrimTail skipped: live record detected.");
                    }
                }
                var holes = new List<(ulong offset, RecordHeader header)>();
                ulong offset = (ulong)fData.header.FirstRecordOffset;
                ulong lastLiveOffset = 0;
                RecordHeader lastLiveHeader = default;

                while (offset + (uint)RecordHeader.SizeOf <= fileEnd)
                {
                    token.ThrowIfCancellationRequested();

                    var readHeader = fData.ReadRecordHeader(offset);
                    if (readHeader.RecordSize == 0)
                        break;

                    bool isDead = fData.IsDeadRecord(fIndex, offset, readHeader);

                    if (isDead)
                        holes.Add((offset, readHeader));
                    else
                    {
                        lastLiveOffset = offset;
                        lastLiveHeader = readHeader;
                    }

                    offset += readHeader.RecordSize;
                }

                if (IsDefragLogEnabled(OnlineDefragLogLevel.Trace))
                    DefragLog(OnlineDefragLogLevel.Trace, $"TryDefragTailOnce: holes={holes.Count} lastLiveOffset=0x{lastLiveOffset:X} tailSize={lastLiveHeader.RecordSize}.");

                if (lastLiveOffset == 0)
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: no live records found.");
                    return false;
                }

                // Require: last live record must be at tail for this step
                if (lastLiveOffset + lastLiveHeader.RecordSize != fileEnd)
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: last live record is not at tail.");
                    return false;
                }

                if (holes.Count == 0)
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: no holes found.");
                    return false;
                }

                // Best-fit hole before lastLiveOffset
                ulong requiredSize = lastLiveHeader.RecordSize;
                ulong bestHoleOffset = 0;
                ulong bestHoleSlack = ulong.MaxValue;

                foreach (var (holeOffset, holeHeader) in holes)
                {
                    if (holeOffset >= lastLiveOffset)
                        continue;

                    ulong holeSize = holeHeader.RecordSize;
                    if (holeSize < requiredSize)
                        continue;

                    ulong slack = holeSize - requiredSize;
                    if (slack < bestHoleSlack)
                    {
                        bestHoleSlack = slack;
                        bestHoleOffset = holeOffset;

                        if (slack == 0)
                            break;
                    }
                }

                if (bestHoleOffset == 0)
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: no suitable hole found.");
                    return false;
                }

                // Coordinate with writers on the bucket stripe for this ID.
                int stripeLockIndex;
                if (!TryAcquireBucketStripeForRecord(lastLiveHeader.ID, out stripeLockIndex))
                {
                    DefragLog(OnlineDefragLogLevel.Trace, "TryDefragTailOnce: failed to acquire stripe lock.");
                    return false;
                }

                try
                {
                    bool moved = TryMoveTailRecordUnderStripeLock(
                        lastLiveHeader.ID,
                        lastLiveOffset,
                        lastLiveHeader,
                        bestHoleOffset);

                    lock (_sync)
                    {
                        _stats.MovesAttempted++;

                        if (moved)
                        {
                            _stats.MovesCompleted++;
                            _stats.BytesMoved += lastLiveHeader.DataLength;
                            _stats.HolesReused++;
                            _stats.LastTailOffset = lastLiveOffset;
                            _stats.LastHoleOffset = bestHoleOffset;
                            _stats.LastActivityUtc = DateTime.UtcNow;
                        }
                    }

                    return moved;
                }
                finally
                {
                    _owner.fIndex.fc.atomics.ReleaseSpinLock32(stripeLockIndex);
                }
            }
            // --------- Move + relink under stripe lock ---------

            private bool TryMoveTailRecordUnderStripeLock(
                ulong recordID,
                ulong lastLiveOffset,
                RecordHeader lastLiveHeader,
                ulong bestHoleOffset)
            {
                var fData = _owner.fData;
                var fIndex = _owner.fIndex;

                // 1) Recheck EOF and tail header under the stripe lock.
                ulong fileEndNow = (ulong)fData.LogicalLength;
                if (fileEndNow <= (ulong)FDataHeader.SizeOf)
                    return false;

                var tailHeaderNow = fData.ReadRecordHeader(lastLiveOffset);

                if (tailHeaderNow.ID != recordID ||
                    tailHeaderNow.RecordSize != lastLiveHeader.RecordSize ||
                    lastLiveOffset + tailHeaderNow.RecordSize != fileEndNow)
                {
                    // Record moved/resized or is no longer the tail; abort.
                    return false;
                }

                // 2) Recheck that the hole is still dead and large enough.
                var holeHeaderNow = fData.ReadRecordHeader(bestHoleOffset);

                if (holeHeaderNow.RecordSize < tailHeaderNow.RecordSize)
                    return false;

                if (!fData.IsDeadRecord(fIndex, bestHoleOffset, holeHeaderNow))
                    return false;

                // 3) Read full tail record (header + payload) from disk.
                var (data, header) = fData.ReadRecord(recordID, lastLiveOffset);

                // Make sure header matches what we just validated.
                header.RecordSize = tailHeaderNow.RecordSize;
                header.DataLength = tailHeaderNow.DataLength;

                // 4a) Write the record into the hole.
                fData.RewriteRecord(bestHoleOffset, header, data.Span);

                // 4b) Rewire the bucket chain + A/B index entries while stripe lock is held.
                RelinkRecordAfterMove(recordID, lastLiveOffset, bestHoleOffset, header.DataLength);

                // 4c) Tombstone the old tail location.
                fData.ZeroRecordID(lastLiveOffset);

                // 4d) Try to shrink logical/file EOF using atomics[0] as the arbiter.
                //     If someone appended after fileEndNow, CAS in TryTruncateTail will fail
                //     and we’ll leave the physical length as-is (no data loss).
                var truncated = fData.TryTruncateTail(fileEndNow, lastLiveOffset);
                if (IsDefragLogEnabled(OnlineDefragLogLevel.Debug))
                    DefragLog(OnlineDefragLogLevel.Debug, $"TryTruncateTail oldEnd={fileEndNow} newEnd={lastLiveOffset} success={truncated}.");

                return true;
            }
        }

        /// <summary>
        /// Creates one coherent live backup of the Fractal data file, index file, and both persisted atomic-memory sidecars.<br/>
        /// The operation pauses online defragmentation, drains mutations admitted through this <see cref="FractalStore"/>, durably flushes all four source files, and copies them while mutation publication is exclusively blocked.<br/>
        /// Source hashes are accumulated during the exclusive copy window; staged files are independently rehashed and structurally validated after mutation admission resumes.<br/>
        /// This first contract deliberately requires <see cref="FractalStoreConfiguration.AssumeSingleProcess"/> because direct physical-file mutation or another process does not participate in this instance's admission gate.<br/>
        /// </summary>
        /// <param name="targetDirectory">Existing or creatable destination directory that will receive the four files under their original store name.<br/></param>
        /// <param name="options">Optional overwrite and copy-buffer behavior; null uses conservative defaults.<br/></param>
        /// <param name="cancellationToken">Token observed while draining mutations and strictly between copy blocks before installation begins.<br/></param>
        /// <returns>Exact installed file count, byte count, timestamp, and defragmenter-resume metadata.<br/></returns>
        public FractalBackupResult Backup(
            string targetDirectory,
            FractalBackupOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
            options ??= new FractalBackupOptions();
            if (options.CopyBufferBytes < 4096)
                throw new ArgumentOutOfRangeException(nameof(options), "CopyBufferBytes must be at least 4096.");
            if (!config.AssumeSingleProcess)
            {
                throw new NotSupportedException(
                    "Live Fractal backup currently requires AssumeSingleProcess=true because cross-process mutation admission is not yet connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirectory = ResolveDirectoryIdentity(folderPath);
            var destinationDirectory = Path.GetFullPath(targetDirectory);
            Directory.CreateDirectory(destinationDirectory);
            destinationDirectory = ResolveDirectoryIdentity(destinationDirectory);
            if (string.Equals(
                    sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The backup destination resolves to the live Fractal source directory.", nameof(targetDirectory));
            }

            var sourcePaths = new[]
            {
                fData.fc.FullName,
                fData.fc.FullName + ".shm",
                fIndex.fc.FullName,
                fIndex.fc.FullName + ".shm"
            };
            for (var i = 0; i < sourcePaths.Length; i++)
            {
                if (!File.Exists(sourcePaths[i]))
                    throw new FileNotFoundException("A required live Fractal backup member is missing.", sourcePaths[i]);
            }

            var destinationPaths = sourcePaths
                .Select(path => Path.Combine(destinationDirectory, Path.GetFileName(path)))
                .ToArray();
            if (!options.Overwrite)
            {
                for (var i = 0; i < destinationPaths.Length; i++)
                {
                    if (File.Exists(destinationPaths[i]))
                        throw new IOException($"Fractal backup destination already exists: '{destinationPaths[i]}'.");
                }
            }

            var stagingDirectory = Path.Combine(
                destinationDirectory,
                "." + name + ".fractal-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagedPaths = sourcePaths
                .Select(path => Path.Combine(stagingDirectory, Path.GetFileName(path)))
                .ToArray();
            var sourceHashes = new byte[sourcePaths.Length][];
            long totalBytes = 0;
            var resumeOnlineDefrag = IsOnlineDefragRunning;

            try
            {
                StopOnlineDefrag();
                try
                {
                    using (EnterSnapshotExclusiveScope(cancellationToken))
                    {
                        fData.fc.FlushToDisk();
                        fIndex.fc.FlushToDisk();
                        fData.fc.atomics.Flush(flushToDisk: true);
                        fIndex.fc.atomics.Flush(flushToDisk: true);

                        for (var i = 0; i < sourcePaths.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var copied = CopySnapshotMember(
                                sourcePaths[i],
                                stagedPaths[i],
                                options.CopyBufferBytes,
                                cancellationToken);
                            sourceHashes[i] = copied.Hash;
                            totalBytes = checked(totalBytes + copied.Bytes);
                        }
                    }
                }
                finally
                {
                    if (resumeOnlineDefrag)
                        StartOnlineDefrag();
                }

                for (var i = 0; i < stagedPaths.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var staged = new FileStream(
                        stagedPaths[i],
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        options.CopyBufferBytes,
                        FileOptions.SequentialScan);
                    var stagedHash = SHA256.HashData(staged);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHashes[i], stagedHash))
                        throw new InvalidDataException($"Fractal backup member hash mismatch: '{Path.GetFileName(stagedPaths[i])}'.");
                }

                ValidateSnapshotPair(stagedPaths[0], stagedPaths[1], isIndex: false);
                ValidateSnapshotPair(stagedPaths[2], stagedPaths[3], isIndex: true);
                cancellationToken.ThrowIfCancellationRequested();
                InstallSnapshotMembers(stagedPaths, destinationPaths, options.Overwrite);

                return new FractalBackupResult(
                    destinationDirectory,
                    name,
                    sourcePaths.Length,
                    checked((ulong)totalBytes),
                    DateTime.UtcNow,
                    resumeOnlineDefrag);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingDirectory))
                        Directory.Delete(stagingDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// Copies one quiescent source member into destination-local staging while computing the exact source bytes' SHA-256 hash.<br/>
        /// Cancellation is checked before each read, so an accepted block is always written and flushed as a complete block before cancellation is observed.<br/>
        /// </summary>
        /// <param name="sourcePath">Live source file held stable by snapshot-exclusive admission.<br/></param>
        /// <param name="stagedPath">New destination-local staging file.<br/></param>
        /// <param name="bufferBytes">Positive reusable copy-buffer size.<br/></param>
        /// <param name="cancellationToken">Token observed between blocks.<br/></param>
        /// <returns>The exact copied byte count and source-stream hash.<br/></returns>
        private static (long Bytes, byte[] Hash) CopySnapshotMember(
            string sourcePath,
            string stagedPath,
            int bufferBytes,
            CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferBytes,
                    FileOptions.SequentialScan);
                using var staged = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferBytes,
                    FileOptions.SequentialScan);

                long copied = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                        break;
                    staged.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                    copied = checked(copied + read);
                }

                staged.Flush(flushToDisk: true);
                return (copied, hash.GetHashAndReset());
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Validates one staged Fractal primary file against its staged atomic sidecar without taking ownership or changing backup bytes.<br/>
        /// Validation checks header magic/checksum and requires the persisted logical EOF to fit inside the exact copied physical file.<br/>
        /// </summary>
        /// <param name="primaryPath">Staged <c>.fractD</c> or <c>.fractX</c> file.<br/></param>
        /// <param name="atomicPath">Matching staged <c>.shm</c> file.<br/></param>
        /// <param name="isIndex">Whether to validate an <see cref="FIndexHeader"/> instead of an <see cref="FDataHeader"/>.<br/></param>
        private static void ValidateSnapshotPair(string primaryPath, string atomicPath, bool isIndex)
        {
            var primaryLength = new FileInfo(primaryPath).Length;
            var headerSize = checked((int)(isIndex ? FIndexHeader.SizeOf : FDataHeader.SizeOf));
            if (primaryLength < headerSize)
                throw new InvalidDataException($"Fractal backup member '{primaryPath}' is shorter than its header.");

            var headerBytes = new byte[headerSize];
            using (var primary = new FileStream(primaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                primary.ReadExactly(headerBytes);
            }

            if (isIndex)
            {
                var header = MemoryMarshal.Read<FIndexHeader>(headerBytes);
                if (header.Magic != (ulong)Consts.FINDEX_MAGIC || header.CalculateChecksum() != header.HeaderChecksum)
                    throw new InvalidDataException($"Fractal index backup header validation failed for '{primaryPath}'.");
            }
            else
            {
                var header = MemoryMarshal.Read<FDataHeader>(headerBytes);
                if (header.Magic != (ulong)Consts.FINDEX_MAGIC || header.CalculateChecksum() != header.HeaderChecksum)
                    throw new InvalidDataException($"Fractal data backup header validation failed for '{primaryPath}'.");
            }

            Span<byte> logicalLengthBytes = stackalloc byte[sizeof(long)];
            using (var atomics = new FileStream(atomicPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (atomics.Length < logicalLengthBytes.Length)
                    throw new InvalidDataException($"Fractal atomic backup member '{atomicPath}' is incomplete.");
                atomics.ReadExactly(logicalLengthBytes);
            }

            var logicalLength = MemoryMarshal.Read<long>(logicalLengthBytes);
            if (logicalLength < headerSize || logicalLength > primaryLength)
            {
                throw new InvalidDataException(
                    $"Fractal backup logical length {logicalLength:n0} is outside '{primaryPath}' physical bounds {headerSize:n0}..{primaryLength:n0}.");
            }
        }

        /// <summary>
        /// Installs all validated staging members as one rollback-protected destination set.<br/>
        /// Existing members are renamed rather than overwritten in place, so a symbolic or hard-linked destination cannot redirect writes into a live source file.<br/>
        /// </summary>
        /// <param name="stagedPaths">Validated destination-local staging members.<br/></param>
        /// <param name="destinationPaths">Final paths corresponding by ordinal to <paramref name="stagedPaths"/>.<br/></param>
        /// <param name="overwrite">Whether existing destination members may be replaced.<br/></param>
        private static void InstallSnapshotMembers(string[] stagedPaths, string[] destinationPaths, bool overwrite)
        {
            var rollbackSuffix = ".fractal-backup-rollback-" + Guid.NewGuid().ToString("N");
            var rollbackPaths = new string?[destinationPaths.Length];
            var installed = new bool[destinationPaths.Length];
            try
            {
                for (var i = 0; i < destinationPaths.Length; i++)
                {
                    if (File.Exists(destinationPaths[i]))
                    {
                        if (!overwrite)
                            throw new IOException($"Fractal backup destination already exists: '{destinationPaths[i]}'.");
                        rollbackPaths[i] = destinationPaths[i] + rollbackSuffix;
                        File.Move(destinationPaths[i], rollbackPaths[i]!);
                    }

                    File.Move(stagedPaths[i], destinationPaths[i]);
                    installed[i] = true;
                }

                for (var i = 0; i < rollbackPaths.Length; i++)
                {
                    if (rollbackPaths[i] is not null && File.Exists(rollbackPaths[i]))
                        File.Delete(rollbackPaths[i]!);
                }
            }
            catch
            {
                for (var i = destinationPaths.Length - 1; i >= 0; i--)
                {
                    if (installed[i] && File.Exists(destinationPaths[i]))
                        File.Delete(destinationPaths[i]);
                    if (rollbackPaths[i] is not null && File.Exists(rollbackPaths[i]))
                        File.Move(rollbackPaths[i]!, destinationPaths[i]);
                }
                throw;
            }
        }

        /// <summary>
        /// Resolves every existing reparse-point component in one directory path to its final target for same-source backup rejection.<br/>
        /// Nonexistent trailing components remain normalized beneath the last existing resolved parent.<br/>
        /// </summary>
        /// <param name="path">Directory path to normalize and resolve.<br/></param>
        /// <returns>The absolute directory identity after resolving junction and symbolic-link components.<br/></returns>
        private static string ResolveDirectoryIdentity(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath)
                ?? throw new ArgumentException("Directory path has no filesystem root.", nameof(path));
            var relative = fullPath[root.Length..];
            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                var info = new DirectoryInfo(current);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
                    continue;
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Could not resolve backup directory alias '{info.FullName}'.");
            }

            return Path.GetFullPath(current);
        }

        /// <summary>
        /// Returns the current on-disk size of the Fractal data and/or index files.<br/>
        /// Sizes are read from the underlying <see cref="System.IO.FileInfo"/> instances and refreshed on each call.<br/>
        /// </summary>
        /// <param name="detailFlags">Specifies which Fractal files to include in the total.<br/></param>
        /// <returns>Total size in bytes for the requested Fractal files.<br/></returns>
        public ulong GetFileSize(FractalFileSizeDetailFlags detailFlags = FractalFileSizeDetailFlags.All)
        {
            if (detailFlags == FractalFileSizeDetailFlags.None)
                return 0;

            ulong total = 0;
            if (detailFlags.HasFlag(FractalFileSizeDetailFlags.DataFile))
                total += GetFileLength(fData?.fc);
            if (detailFlags.HasFlag(FractalFileSizeDetailFlags.IndexFile))
                total += GetFileLength(fIndex?.fc);

            return total;
        }

        private static ulong GetFileLength(FileController? controller)
        {
            return controller?.GetFileSize() ?? 0;
        }

        /// <summary>
        /// Enumerates every live logical identity exactly once across root and expanded bucket tiers.<br/>
        /// Deleted records, expansion markers, and chained back-references are skipped; ordinary collision chains are followed through their record headers.<br/>
        /// Callers that require a quiescent snapshot must provide their own mutation or maintenance boundary.<br/>
        /// </summary>
        /// <returns>A lazy stream of live logical record identities.<br/></returns>
        public IEnumerable<ulong> IterateLiveRecordIDs()
        {
            foreach (var entry in fIndex.IterateKeys())
            {
                if (entry.IsUnset || entry.IsExpanded || entry.IsChainedBackref)
                    continue;

                if (entry.HasSingleRecord)
                {
                    var header = fData.ReadRecordHeader(entry.RecordOffset);
                    if (header.ID != 0)
                        yield return header.ID;
                    continue;
                }

                var currentOffset = entry.RecordOffset;
                while (currentOffset != 0)
                {
                    var header = fData.ReadRecordHeader(currentOffset);
                    if (header.ID != 0)
                        yield return header.ID;
                    currentOffset = header.NextRecordOffset;
                }
            }
        }

        /// <summary>
        /// Creates and validates a dense four-file Fractal family containing every current live record except identities whose upper-byte group equals <paramref name="excludedGroupKey"/>.<br/>
        /// The source remains authoritative and unchanged; mutation admission and online defragmentation are quiesced while records and persisted allocator/USN values are copied.<br/>
        /// The destination must be a distinct unused folder/name family. Installation or deletion of either family remains the caller's responsibility so a higher-level owner can coordinate multi-engine replacement and recovery.<br/>
        /// </summary>
        /// <param name="destinationDirectory">Directory that will receive the new data, data-atomic, index, and index-atomic files.<br/></param>
        /// <param name="destinationName">Filename stem for the new Fractal family.<br/></param>
        /// <param name="excludedGroupKey">Upper-byte logical group omitted from the dense copy.<br/></param>
        /// <param name="cancellationToken">Token observed before each record copy and validation lookup while the source remains authoritative.<br/></param>
        /// <returns>Exact destination paths, copied/excluded record counts, and physical bytes for the validated family.<br/></returns>
        public FractalCompactedCopyResult CreateCompactedCopyExcludingGroup(
            string destinationDirectory,
            string destinationName,
            byte excludedGroupKey,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
            var destinationRoot = Path.GetFullPath(destinationDirectory);
            var sourceRoot = Path.GetFullPath(folderPath);
            if (string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, destinationName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The compacted Fractal destination must differ from the live source family.");
            }

            Directory.CreateDirectory(destinationRoot);
            var destinationPaths = new[]
            {
                Path.Combine(destinationRoot, destinationName + ".fractD"),
                Path.Combine(destinationRoot, destinationName + ".fractD.shm"),
                Path.Combine(destinationRoot, destinationName + ".fractX"),
                Path.Combine(destinationRoot, destinationName + ".fractX.shm")
            };
            for (var i = 0; i < destinationPaths.Length; i++)
            {
                if (File.Exists(destinationPaths[i]))
                    throw new IOException($"Compacted Fractal destination already exists: '{destinationPaths[i]}'.");
            }

            var resumeOnlineDefrag = IsOnlineDefragRunning;
            ulong copiedCount = 0;
            ulong excludedCount = 0;
            StopOnlineDefrag();
            try
            {
                using (EnterSnapshotExclusiveScope(cancellationToken))
                {
                    fData.fc.FlushToDisk();
                    fIndex.fc.FlushToDisk();
                    fData.fc.atomics.Flush(flushToDisk: true);
                    fIndex.fc.atomics.Flush(flushToDisk: true);

                    var allocator = fIndex.fc.atomics.Read64(RecordIdAtomicSlot);
                    var storeUsns = new long[byte.MaxValue + 1];
                    for (var storeId = 1; storeId <= byte.MaxValue; storeId++)
                        storeUsns[storeId] = fIndex.fc.atomics.Read64(StoreUsnBaseIndex64 + storeId);

                    var shadowConfiguration = config with
                    {
                        CreateIfNotExists = true,
                        AssumeSingleProcess = true
                    };
                    var shadow = new FractalStore(destinationRoot, destinationName, shadowConfiguration);
                    shadow.Open();
                    try
                    {
                        foreach (var recordID in IterateLiveRecordIDs())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (GetStoreIdFromRecordId(recordID) == excludedGroupKey)
                            {
                                excludedCount++;
                                continue;
                            }

                            using var record = GetOne(recordID);
                            shadow.PutOne(recordID, record.Data.Span);
                            copiedCount++;
                        }

                        shadow.fIndex.fc.atomics.Write64(RecordIdAtomicSlot, allocator);
                        for (var storeId = 1; storeId <= byte.MaxValue; storeId++)
                            shadow.fIndex.fc.atomics.Write64(StoreUsnBaseIndex64 + storeId, storeUsns[storeId]);
                        shadow.fData.fc.FlushToDisk();
                        shadow.fIndex.fc.FlushToDisk();
                        shadow.fData.fc.atomics.Flush(flushToDisk: true);
                        shadow.fIndex.fc.atomics.Flush(flushToDisk: true);

                        if (shadow.RecordCount() != copiedCount ||
                            shadow.IterateRecordIDsByGroupKey(excludedGroupKey).Any())
                        {
                            throw new InvalidDataException("Compacted Fractal copy restored an unexpected record count or retained the excluded group.");
                        }

                        foreach (var recordID in IterateLiveRecordIDs())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (GetStoreIdFromRecordId(recordID) == excludedGroupKey)
                                continue;
                            if (!TryReadRecordHeader(recordID, out var sourceHeader) ||
                                !shadow.TryReadRecordHeader(recordID, out var shadowHeader) ||
                                sourceHeader.DataLength != shadowHeader.DataLength ||
                                sourceHeader.DataChecksum != shadowHeader.DataChecksum)
                            {
                                throw new InvalidDataException(
                                    $"Compacted Fractal validation failed for surviving record {recordID}.");
                            }
                        }
                    }
                    finally
                    {
                        shadow.Close();
                    }
                }
            }
            catch
            {
                for (var i = 0; i < destinationPaths.Length; i++)
                {
                    try
                    {
                        if (File.Exists(destinationPaths[i]))
                            File.Delete(destinationPaths[i]);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
            finally
            {
                if (resumeOnlineDefrag)
                    StartOnlineDefrag();
            }

            ulong totalBytes = 0;
            for (var i = 0; i < destinationPaths.Length; i++)
                totalBytes = checked(totalBytes + (ulong)new FileInfo(destinationPaths[i]).Length);
            return new FractalCompactedCopyResult(
                destinationRoot,
                destinationName,
                destinationPaths,
                copiedCount,
                excludedCount,
                totalBytes,
                DateTime.UtcNow,
                resumeOnlineDefrag);
        }

        /// <summary>
        /// Deletes every live record whose upper identity byte equals <paramref name="groupKey"/> in one exclusive bucket-chain pass.<br/>
        /// The scan resolves direct physical targets once; small groups are applied sequentially and groups at or above the configured bulk threshold apply those independent targets in parallel.<br/>
        /// Matching single-record buckets are unset, matching collision-chain members are tombstoned in place, and the group's USN advances once after any successful mutation.<br/>
        /// Unlike <see cref="DeleteMany(IEnumerable{ulong}, bool)"/>, this maintenance primitive does not repeat hash and collision-chain lookup for every selected identity or flush one USN update per record.<br/>
        /// Mutation admission is held exclusively for the complete pass so concurrent writers cannot add, move, or replace a matching record between traversal and tombstoning.<br/>
        /// When residual-data overwrite is requested, every discoverable physical mutation payload is zeroed through the same validated durable contract as <see cref="DeleteOne(ulong, bool)"/>.<br/>
        /// </summary>
        /// <param name="groupKey">Nonzero developer-defined group carried in the upper eight bits of each target record identity.<br/></param>
        /// <param name="zeroWrite">Whether to overwrite all discoverable physical payload copies before tombstoning each matching identity.<br/></param>
        /// <param name="cancellationToken">Token observed between leaf buckets and collision-chain records before further mutation is performed.<br/></param>
        /// <returns>The exact number of live logical records deleted.<br/></returns>
        public ulong DeleteByGroupKey(
            byte groupKey,
            bool zeroWrite = false,
            CancellationToken cancellationToken = default)
        {
            if (groupKey == 0)
                throw new ArgumentOutOfRangeException(nameof(groupKey), "Group key zero is reserved for Fractal system records.");

            const ulong groupMask = 0xFF00_0000_0000_0000UL;
            ulong expected = (ulong)groupKey << 56;
            long deletedCount = 0;
            using (EnterSnapshotExclusiveScope(cancellationToken))
            {
                try
                {
                    var targets = new List<GroupDeleteTarget>();
                    using var reader = fIndex.OpenBucketReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = reader.Current;
                        if (entry.HasSingleRecord)
                        {
                            var header = fData.ReadRecordHeader(entry.RecordOffset);
                            if (header.ID == 0 || (header.ID & groupMask) != expected)
                                continue;
                            targets.Add(new GroupDeleteTarget(
                                entry.RecordOffset,
                                header.ID,
                                reader.CurrentTier,
                                reader.CurrentIndex));
                            continue;
                        }

                        var currentOffset = entry.RecordOffset;
                        while (currentOffset != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var header = fData.ReadRecordHeader(currentOffset);
                            var nextOffset = header.NextRecordOffset;
                            if (header.ID != 0 && (header.ID & groupMask) == expected)
                                targets.Add(new GroupDeleteTarget(currentOffset,header.ID,null,0));
                            currentOffset = nextOffset;
                        }
                    }

                    void ApplyTarget(GroupDeleteTarget target)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (zeroWrite)
                            _ = fData.ZeroMutationChain(target.RecordOffset,target.RecordID);
                        else if (!target.IsSingleRecordBucket)
                            fData.ZeroRecordID(target.RecordOffset);
                        if (target.IsSingleRecordBucket)
                            target.Tier!.UnsetEntry(target.BucketIndex);
                        Interlocked.Increment(ref deletedCount);
                    }

                    if (targets.Count < config.PutManyParallelMinRecordCount)
                    {
                        for (var index = 0; index < targets.Count; index++)
                            ApplyTarget(targets[index]);
                    }
                    else
                    {
                        Parallel.ForEach(
                            targets,
                            new ParallelOptions
                            {
                                CancellationToken = cancellationToken,
                                MaxDegreeOfParallelism = config.PutManyParallelThreadCount
                            },
                            ApplyTarget);
                    }
                }
                finally
                {
                    if (deletedCount != 0)
                    {
                        IncrementStoreUsn(groupKey, deferFlush: true);
                        FlushStoreUsnIfImmediate();
                    }
                }
            }

            return checked((ulong)deletedCount);
        }

        //====== TYPES ======
        /// <summary>
        /// Flags describing which Fractal store files to include when computing file sizes.<br/>
        /// </summary>
        [Flags]
        public enum FractalFileSizeDetailFlags
        {
            /// <summary>Includes no files in the size calculation.<br/></summary>
            None = 0,
            /// <summary>Includes the Fractal data file.<br/></summary>
            DataFile = 1,
            /// <summary>Includes the Fractal index file.<br/></summary>
            IndexFile = 2,
            /// <summary>Includes both Fractal data and index files.<br/></summary>
            All = DataFile | IndexFile
        }

        /// <summary>
        /// Controls installation and copy-buffer behavior for <see cref="Backup(string, FractalBackupOptions?, CancellationToken)"/>.<br/>
        /// </summary>
        public sealed class FractalBackupOptions
        {
            /// <summary>
            /// Gets or sets whether an existing four-member destination set may be replaced through rollback-protected renames.<br/>
            /// The default is <see langword="false"/> so an existing backup is never changed implicitly.<br/>
            /// </summary>
            public bool Overwrite { get; set; }

            /// <summary>
            /// Gets or sets the reusable buffer size used for each sequential snapshot copy.<br/>
            /// The default is one mebibyte; values smaller than 4096 bytes are rejected.<br/>
            /// </summary>
            public int CopyBufferBytes { get; set; } = 1024 * 1024;
        }

        /// <summary>
        /// Describes one successfully installed coherent Fractal live backup.<br/>
        /// </summary>
        /// <param name="TargetDirectory">Final directory containing all four required files.<br/></param>
        /// <param name="StoreName">Fractal store name retained by the installed filenames.<br/></param>
        /// <param name="FileCount">Installed member count; currently data, data atomics, index, and index atomics.<br/></param>
        /// <param name="TotalBytes">Exact sum of installed member bytes.<br/></param>
        /// <param name="UtcTimestamp">Completion time after validation and installation.<br/></param>
        /// <param name="OnlineDefragResumed">Whether an online defragmenter running at admission was restarted after the copy window.<br/></param>
        public readonly record struct FractalBackupResult(
            string TargetDirectory,
            string StoreName,
            int FileCount,
            ulong TotalBytes,
            DateTime UtcTimestamp,
            bool OnlineDefragResumed);

        /// <summary>
        /// Describes one validated dense Fractal family created without a selected upper-byte logical group.<br/>
        /// The result describes staging reality only; no live source files were replaced or removed by the copy operation.<br/>
        /// </summary>
        /// <param name="TargetDirectory">Directory containing the validated four-member family.<br/></param>
        /// <param name="StoreName">Filename stem used by the staged family.<br/></param>
        /// <param name="Paths">Exact data, data-atomic, index, and index-atomic paths in installation order.<br/></param>
        /// <param name="CopiedRecordCount">Number of live logical records retained in the dense family.<br/></param>
        /// <param name="ExcludedRecordCount">Number of live logical records omitted because they belonged to the excluded group.<br/></param>
        /// <param name="TotalBytes">Exact total physical bytes across all four staged files.<br/></param>
        /// <param name="UtcTimestamp">Completion time after copy, allocator/USN preservation, flush, and validation.<br/></param>
        /// <param name="OnlineDefragResumed">Whether source online defragmentation was running and resumed after the snapshot window.<br/></param>
        public readonly record struct FractalCompactedCopyResult(
            string TargetDirectory,
            string StoreName,
            IReadOnlyList<string> Paths,
            ulong CopiedRecordCount,
            ulong ExcludedRecordCount,
            ulong TotalBytes,
            DateTime UtcTimestamp,
            bool OnlineDefragResumed);

        /// <summary>
        /// Releases one active mutation lease without relying on thread affinity.<br/>
        /// </summary>
        private sealed class MutationScope : IDisposable
        {
            private readonly FractalStore owner;
            private int disposed;

            /// <summary>
            /// Captures the owner whose mutation count was already incremented.<br/>
            /// </summary>
            /// <param name="owner">Live Fractal store that owns the admission boundary.<br/></param>
            internal MutationScope(FractalStore owner)
            {
                this.owner = owner;
            }

            /// <summary>
            /// Releases the mutation count exactly once and wakes a pending snapshot when the final mutation drains.<br/>
            /// </summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;
                lock (owner.snapshotGate)
                {
                    owner.activeMutations--;
                    if (owner.activeMutations == 0)
                        Monitor.PulseAll(owner.snapshotGate);
                }
            }
        }

        /// <summary>
        /// Reopens mutation admission after one snapshot-exclusive window.<br/>
        /// </summary>
        private sealed class SnapshotExclusiveScope : IDisposable
        {
            private readonly FractalStore owner;
            private int disposed;

            /// <summary>
            /// Captures the owner after it has atomically observed quiescence and closed mutation admission.<br/>
            /// </summary>
            /// <param name="owner">Live Fractal store that owns the admission boundary.<br/></param>
            internal SnapshotExclusiveScope(FractalStore owner)
            {
                this.owner = owner;
            }

            /// <summary>
            /// Reopens mutation admission exactly once and wakes all blocked callers.<br/>
            /// </summary>
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;
                lock (owner.snapshotGate)
                {
                    owner.snapshotExclusive = false;
                    Monitor.PulseAll(owner.snapshotGate);
                }
            }
        }

        /// <summary>
        /// Identifies one already-resolved physical record mutation for a grouped delete pass.<br/>
        /// A non-null tier marks a single-record bucket that can be unset directly; a null tier marks one collision-chain header that must be tombstoned in place.<br/>
        /// </summary>
        /// <param name="RecordOffset">Physical Fractal data-file offset of the live record header.<br/></param>
        /// <param name="RecordID">Logical identity validated during the exclusive discovery pass.<br/></param>
        /// <param name="Tier">Owning leaf tier for a single-record bucket, or null for a collision-chain member.<br/></param>
        /// <param name="BucketIndex">Owning leaf ordinal when <paramref name="Tier"/> is non-null.<br/></param>
        private readonly record struct GroupDeleteTarget(
            ulong RecordOffset,
            ulong RecordID,
            BucketTier? Tier,
            ulong BucketIndex)
        {
            /// <summary>
            /// Gets whether this target owns a complete single-record leaf that should be removed from the index rather than data-tombstoned.<br/>
            /// </summary>
            internal bool IsSingleRecordBucket => Tier is not null;
        }

        /// <summary>
        /// Owns one authoritative physical-order location snapshot and exposes it as disjoint byte-balanced group readers.<br/>
        /// The topology and physical sort are paid once regardless of partition count; each opened reader borrows a contiguous immutable range and owns an independent pooled read window.<br/>
        /// This type intentionally supplies no global result order beyond ascending partition number followed by each partition's physical order.<br/>
        /// </summary>
        public sealed class GroupBulkRecordPartitionSet : IDisposable
        {
            private readonly int[] partitionStarts;
            private readonly int[] partitionEnds;
            private readonly int readWindowBytes;
            private bool disposed;

            /// <summary>
            /// Creates and byte-balances one complete group-location snapshot.<br/>
            /// Every non-empty partition owns at least one record and partitions remain contiguous in physical offset order.<br/>
            /// </summary>
            /// <param name="owner">Open Fractal store owning the data and index files.<br/></param>
            /// <param name="groupKey">Upper-byte identity group to expose.<br/></param>
            /// <param name="partitionCount">Requested maximum number of non-empty partitions.<br/></param>
            /// <param name="readWindowBytes">Preferred pooled window size for each opened reader.<br/></param>
            internal GroupBulkRecordPartitionSet(
                FractalStore owner,
                byte groupKey,
                int partitionCount,
                int readWindowBytes)
            {
                ArgumentNullException.ThrowIfNull(owner);
                if (partitionCount < 1)
                    throw new ArgumentOutOfRangeException(nameof(partitionCount));
                if (readWindowBytes < RecordHeader.SizeOf)
                    throw new ArgumentOutOfRangeException(nameof(readWindowBytes));

                Owner = owner;
                this.readWindowBytes = readWindowBytes;
                GroupRecordLocationBuffer locationBuffer = owner.RentGroupRecordLocationsByPhysicalOrder(groupKey);
                Locations = locationBuffer.Buffer;
                LocationCount = locationBuffer.Count;

                int actualPartitions = Math.Min(partitionCount, LocationCount);
                partitionStarts = new int[actualPartitions];
                partitionEnds = new int[actualPartitions];
                if (actualPartitions == 0)
                    return;

                ulong remainingBytes = 0;
                for (int index = 0; index < LocationCount; index++)
                {
                    remainingBytes = checked(
                        remainingBytes +
                        (ulong)RecordHeader.SizeOf +
                        Locations[index].Header.DataLength);
                }

                int nextIndex = 0;
                for (int partition = 0; partition < actualPartitions; partition++)
                {
                    partitionStarts[partition] = nextIndex;
                    int remainingPartitions = actualPartitions - partition;
                    int maximumExclusive = LocationCount - (remainingPartitions - 1);
                    ulong targetBytes = checked(
                        (remainingBytes + (ulong)remainingPartitions - 1UL) /
                        (ulong)remainingPartitions);
                    ulong partitionBytes = 0;
                    do
                    {
                        partitionBytes = checked(
                            partitionBytes +
                            (ulong)RecordHeader.SizeOf +
                            Locations[nextIndex].Header.DataLength);
                        nextIndex++;
                    }
                    while (nextIndex < maximumExclusive && partitionBytes < targetBytes);

                    partitionEnds[partition] = nextIndex;
                    remainingBytes -= partitionBytes;
                }
            }

            /// <summary>Gets the number of non-empty physical partitions available for opening.<br/></summary>
            public int PartitionCount => partitionStarts.Length;

            /// <summary>Gets the total authoritative record count represented by this snapshot.<br/></summary>
            public int RecordCount => LocationCount;

            /// <summary>
            /// Opens one forward-only reader over a single non-overlapping physical range.<br/>
            /// Multiple partitions may be consumed concurrently while this set remains undisposed and the owning store is not mutated.<br/>
            /// </summary>
            /// <param name="partitionIndex">Zero-based partition number in ascending physical order.<br/></param>
            /// <returns>A borrowed-range reader that owns its own pooled data window.<br/></returns>
            public GroupBulkRecordReader OpenPartition(int partitionIndex)
            {
                EnsureUsable();
                if ((uint)partitionIndex >= (uint)partitionStarts.Length)
                    throw new ArgumentOutOfRangeException(nameof(partitionIndex));
                return new GroupBulkRecordReader(
                    this,
                    partitionStarts[partitionIndex],
                    partitionEnds[partitionIndex],
                    readWindowBytes);
            }

            /// <summary>
            /// Returns the pooled location snapshot after all partition readers have completed.<br/>
            /// Disposal is idempotent; using an existing or new partition reader afterward fails explicitly.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                if (Locations.Length != 0)
                    ArrayPool<GroupRecordLocation>.Shared.Return(Locations);
            }

            /// <summary>Gets the Fractal owner used by borrowed partition readers.<br/></summary>
            internal FractalStore Owner { get; }

            /// <summary>Gets the shared immutable physical-location array.<br/></summary>
            internal GroupRecordLocation[] Locations { get; }

            /// <summary>Gets the populated prefix length in <see cref="Locations"/>.<br/></summary>
            internal int LocationCount { get; }

            /// <summary>
            /// Rejects access after the shared pooled snapshot has been returned.<br/>
            /// </summary>
            internal void EnsureUsable()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
        }

        /// <summary>
        /// Produces complete physical-order record blocks from one authoritative group-location snapshot.<br/>
        /// One calling thread owns production; returned blocks may be transferred to worker threads and borrow the producer's immutable location snapshot until disposed.<br/>
        /// </summary>
        public sealed class GroupBulkRecordBlockProducer : IDisposable
        {
            private readonly int blockBytes;
            private int nextLocationIndex;
            private bool disposed;

            internal GroupBulkRecordBlockProducer(FractalStore owner, byte groupKey, int blockBytes)
            {
                ArgumentNullException.ThrowIfNull(owner);
                if (blockBytes < RecordHeader.SizeOf)
                    throw new ArgumentOutOfRangeException(nameof(blockBytes));

                Owner = owner;
                this.blockBytes = blockBytes;
                GroupRecordLocationBuffer locations = owner.RentGroupRecordLocationsByPhysicalOrder(groupKey);
                Locations = locations.Buffer;
                LocationCount = locations.Count;
            }

            /// <summary>Gets the number of authoritative records represented by this producer.<br/></summary>
            public int RecordCount => LocationCount;

            /// <summary>
            /// Reads the next bounded physical span into one pooled block.<br/>
            /// Records separated by a gap that would exceed <c>blockBytes</c> begin a later block, while one oversized record remains intact.<br/>
            /// </summary>
            /// <returns>The next leased block, or null after every authoritative location has been produced.<br/></returns>
            public GroupBulkRecordBlock? ReadBlock()
            {
                EnsureUsable();
                if (nextLocationIndex >= LocationCount)
                    return null;

                int startIndex = nextLocationIndex;
                ulong blockOffset = Locations[startIndex].Offset;
                int endIndex = startIndex + 1;
                ulong blockEnd = checked(
                    Locations[startIndex].Offset +
                    (ulong)RecordHeader.SizeOf +
                    Locations[startIndex].Header.DataLength);
                ulong preferredEnd = checked(blockOffset + (ulong)blockBytes);
                while (endIndex < LocationCount)
                {
                    GroupRecordLocation next = Locations[endIndex];
                    ulong nextEnd = checked(next.Offset + (ulong)RecordHeader.SizeOf + next.Header.DataLength);
                    if (nextEnd > preferredEnd)
                        break;

                    blockEnd = nextEnd;
                    endIndex++;
                }

                int length = checked((int)(blockEnd - blockOffset));
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    Owner.fData.ReadRange(buffer.AsSpan(0, length), blockOffset);
                    nextLocationIndex = endIndex;
                    return new GroupBulkRecordBlock(this, buffer, length, blockOffset, startIndex, endIndex);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
            }

            /// <summary>
            /// Returns the pooled location snapshot after every produced block has been disposed.<br/>
            /// </summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                if (Locations.Length != 0)
                    ArrayPool<GroupRecordLocation>.Shared.Return(Locations);
            }

            internal FractalStore Owner { get; }
            internal GroupRecordLocation[] Locations { get; }
            internal int LocationCount { get; }

            internal void EnsureUsable() => ObjectDisposedException.ThrowIf(disposed, this);
        }

        /// <summary>
        /// Owns one pooled physical data window containing one or more authoritative Fractal records.<br/>
        /// A single worker may advance through borrowed payloads without copying; all payload memory becomes invalid when the block is disposed.<br/>
        /// </summary>
        public sealed class GroupBulkRecordBlock : IDisposable
        {
            private readonly GroupBulkRecordBlockProducer owner;
            private readonly ulong blockOffset;
            private readonly int startLocationIndex;
            private readonly int endLocationIndex;
            private byte[] buffer;
            private int bufferLength;
            private int nextLocationIndex;
            private int currentPayloadOffset;
            private int currentPayloadLength;
            private ulong currentIdentity;
            private bool hasCurrent;
            private bool disposed;

            internal GroupBulkRecordBlock(
                GroupBulkRecordBlockProducer owner,
                byte[] buffer,
                int bufferLength,
                ulong blockOffset,
                int startLocationIndex,
                int endLocationIndex)
            {
                this.owner = owner;
                this.buffer = buffer;
                this.bufferLength = bufferLength;
                this.blockOffset = blockOffset;
                this.startLocationIndex = startLocationIndex;
                nextLocationIndex = startLocationIndex;
                this.endLocationIndex = endLocationIndex;
            }

            /// <summary>Gets the number of authoritative records retained in this block.<br/></summary>
            public int RecordCount => endLocationIndex - startLocationIndex;

            /// <summary>Gets the identity of the current record after a successful <see cref="Read"/>.<br/></summary>
            public ulong ID
            {
                get
                {
                    EnsureCurrent();
                    return currentIdentity;
                }
            }

            /// <summary>Gets current borrowed payload memory valid until this block is disposed.<br/></summary>
            public ReadOnlyMemory<byte> Data
            {
                get
                {
                    EnsureCurrent();
                    return buffer.AsMemory(currentPayloadOffset, currentPayloadLength);
                }
            }

            /// <summary>Gets current borrowed payload bytes valid until this block is disposed.<br/></summary>
            public ReadOnlySpan<byte> DataSpan
            {
                get
                {
                    EnsureCurrent();
                    return buffer.AsSpan(currentPayloadOffset, currentPayloadLength);
                }
            }

            /// <summary>
            /// Advances to the next authoritative record already present in this pooled block.<br/>
            /// </summary>
            /// <returns><see langword="true"/> when identity and borrowed payload properties are available.<br/></returns>
            public bool Read()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                owner.EnsureUsable();
                hasCurrent = false;
                if (nextLocationIndex >= endLocationIndex)
                    return false;

                GroupRecordLocation location = owner.Locations[nextLocationIndex++];
                int relativeOffset = checked((int)(location.Offset - blockOffset));
                int requiredLength = checked(RecordHeader.SizeOf + (int)location.Header.DataLength);
                if (relativeOffset < 0 || relativeOffset > bufferLength - requiredLength)
                    throw new InvalidDataException("A Fractal pooled record block does not contain its planned record extent.");

                RecordHeader observed = MemoryMarshal.Read<RecordHeader>(
                    buffer.AsSpan(relativeOffset, RecordHeader.SizeOf));
                if (!GroupBulkRecordReader.HeaderEquals(observed, location.Header))
                    throw new InvalidDataException($"Record '{location.Header.ID}' changed while its Fractal pooled-block snapshot was active.");

                currentIdentity = observed.ID;
                currentPayloadOffset = relativeOffset + RecordHeader.SizeOf;
                currentPayloadLength = checked((int)observed.DataLength);
                hasCurrent = true;
                return true;
            }

            /// <summary>Returns the pooled physical window and invalidates every borrowed payload from this block.<br/></summary>
            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                hasCurrent = false;
                currentIdentity = 0;
                currentPayloadOffset = 0;
                currentPayloadLength = 0;
                bufferLength = 0;
                byte[] owned = buffer;
                buffer = Array.Empty<byte>();
                if (owned.Length != 0)
                    ArrayPool<byte>.Shared.Return(owned);
            }

            private void EnsureCurrent()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!hasCurrent)
                    throw new InvalidOperationException("The pooled record block is not positioned on a record.");
            }
        }
    }

    /// <summary>
    /// Reports bounded physical progress for one <see cref="FractalStore.PutMany"/> operation.<br/>
    /// The snapshot contains no payload or identity references and is safe to retain after the callback returns.<br/>
    /// </summary>
    /// <param name="ProcessedCount">Number of records whose physical put attempt completed.<br/></param>
    /// <param name="TotalCount">Exact number of records submitted to the operation.<br/></param>
    /// <param name="WorkerCount">Resolved maximum physical workers used by the call.<br/></param>
    /// <param name="Completed">Whether the complete Fractal batch reached its terminal success boundary.<br/></param>
    public readonly record struct FractalPutManyProgress(
        int ProcessedCount,
        int TotalCount,
        int WorkerCount,
        bool Completed);
}
