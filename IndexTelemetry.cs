using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;


namespace FractalKVS
{
    internal readonly struct BucketTierOccupancy
    {
        public BucketTierOccupancy(
            ushort tier,
            ulong tierOffset,
            uint bucketCount,
            uint unused,
            uint expanded,
            uint leaf)
        {
            Tier = tier;
            TierOffset = tierOffset;
            BucketCount = bucketCount;
            Unused = unused;
            Expanded = expanded;
            Leaf = leaf;
        }

        public ushort Tier { get; }
        public ulong TierOffset { get; }
        public uint BucketCount { get; }
        public uint Unused { get; }
        public uint Expanded { get; }
        public uint Leaf { get; }

        public double PercentUnused => BucketCount == 0 ? 0.0 : (double)Unused / BucketCount;
        public double PercentExpanded => BucketCount == 0 ? 0.0 : (double)Expanded / BucketCount;
        public double PercentLeaf => BucketCount == 0 ? 0.0 : (double)Leaf / BucketCount;
    }

    internal readonly struct BucketFillStat
    {
        public BucketFillStat(
            ushort parentTier,
            ulong tierOffset,
            ulong bucketIndex,
            uint childBucketCount,
            uint childUsedBuckets)
        {
            ParentTier = parentTier;
            TierOffset = tierOffset;
            BucketIndex = bucketIndex;
            ChildBucketCount = childBucketCount;
            ChildUsedBuckets = childUsedBuckets;
        }

        public ushort ParentTier { get; }
        public ulong TierOffset { get; }
        public ulong BucketIndex { get; }
        public uint ChildBucketCount { get; }
        public uint ChildUsedBuckets { get; }

        public double FillRatio => ChildBucketCount == 0 ? 0.0 : (double)ChildUsedBuckets / ChildBucketCount;
    }

    internal readonly struct BucketStructureSnapshot
    {
        public BucketStructureSnapshot(
            BucketTierOccupancy[] perTier,
            BucketFillStat[] hottestByFill,
            BucketFillStat[] coolestByFill)
        {
            PerTier = perTier;
            HottestByFill = hottestByFill;
            CoolestByFill = coolestByFill;
        }

        public BucketTierOccupancy[] PerTier { get; }
        public BucketFillStat[] HottestByFill { get; }
        public BucketFillStat[] CoolestByFill { get; }
    }

    internal sealed class BucketExpansionTelemetry
    {
        private long _totalExpansions;
        private int _maxParentTier;

        // parent tier → expansion count
        private readonly ConcurrentDictionary<ushort, long> _byParentTier =
            new ConcurrentDictionary<ushort, long>();

        // (tierOffset, bucketIndex) → expansion count
        private readonly ConcurrentDictionary<(ulong tierOffset, ulong bucketIndex), long> _byBucket =
            new ConcurrentDictionary<(ulong tierOffset, ulong bucketIndex), long>();

        public long TotalExpansions => Interlocked.Read(ref _totalExpansions);
        public int MaxParentTier => Volatile.Read(ref _maxParentTier);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(BucketTier parentTier, ulong parentBucketIndex)
        {
            Interlocked.Increment(ref _totalExpansions);

            ushort tier = parentTier.Header.Tier;
            _byParentTier.AddOrUpdate(
                tier,
                1,
                static (_, current) => current + 1
            );

            var key = (parentTier.TierOffset, parentBucketIndex);
            _byBucket.AddOrUpdate(
                key,
                1,
                static (_, current) => current + 1
            );

            int t = tier;
            int snapshot;
            // track max parent tier seen (lock-free-ish)
            while ((snapshot = _maxParentTier) < t)
            {
                if (Interlocked.CompareExchange(ref _maxParentTier, t, snapshot) == snapshot)
                    break;
            }
        }

        public BucketExpansionSnapshot GetSnapshot(int topBucketCount = 16)
        {
            if (topBucketCount <= 0)
                topBucketCount = 16;

            // ---- per-tier stats ----
            var tierKvp = _byParentTier.ToArray();
            Array.Sort(tierKvp, (a, b) => a.Key.CompareTo(b.Key));

            var perTier = new BucketExpansionTierStat[tierKvp.Length];
            for (int i = 0; i < tierKvp.Length; i++)
            {
                perTier[i] = new BucketExpansionTierStat(
                    tier: tierKvp[i].Key,
                    expansions: tierKvp[i].Value
                );
            }

            // ---- hot buckets ----
            var bucketKvp = _byBucket.ToArray();
            Array.Sort(bucketKvp, (a, b) => b.Value.CompareTo(a.Value));
            int take = Math.Min(topBucketCount, bucketKvp.Length);

            var hottest = new BucketExpansionBucketStat[take];
            for (int i = 0; i < take; i++)
            {
                var kvp = bucketKvp[i];
                hottest[i] = new BucketExpansionBucketStat(
                    kvp.Key.tierOffset,
                    kvp.Key.bucketIndex,
                    kvp.Value
                );
            }

            return new BucketExpansionSnapshot(
                TotalExpansions,
                MaxParentTier,
                perTier,
                hottest
            );
        }
    }

    internal readonly struct BucketExpansionTierStat
    {
        public BucketExpansionTierStat(ushort tier, long expansions)
        {
            Tier = tier;
            Expansions = expansions;
        }

        public ushort Tier { get; }
        public long Expansions { get; }
    }

    internal readonly struct BucketExpansionBucketStat
    {
        public BucketExpansionBucketStat(ulong tierOffset, ulong bucketIndex, long expansions)
        {
            TierOffset = tierOffset;
            BucketIndex = bucketIndex;
            Expansions = expansions;
        }

        public ulong TierOffset { get; }
        public ulong BucketIndex { get; }
        public long Expansions { get; }
    }

    internal readonly struct BucketExpansionSnapshot
    {
        public BucketExpansionSnapshot(
            long totalExpansions,
            int maxParentTier,
            BucketExpansionTierStat[] perTier,
            BucketExpansionBucketStat[] hottestBuckets)
        {
            TotalExpansions = totalExpansions;
            MaxParentTier = maxParentTier;
            PerTier = perTier;
            HottestBuckets = hottestBuckets;
        }

        public long TotalExpansions { get; }
        /// <summary>Highest parent tier index that ever expanded (root = 0).</summary>
        public int MaxParentTier { get; }
        public BucketExpansionTierStat[] PerTier { get; }
        /// <summary>Top N buckets by expansion count (tierOffset + bucketIndex).</summary>
        public BucketExpansionBucketStat[] HottestBuckets { get; }
    }
}
