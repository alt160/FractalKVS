using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections;
using System.Threading;

namespace FractalKVS
{
    internal sealed class FractalStoreTelemetry
    {
        internal FileSyscallCounters? DataFileSyscallsInternal;
        internal FileSyscallCounters? IndexFileSyscallsInternal;

        internal BucketTierTelemetry? BucketTelemetryInternal;

        public FileSyscallCounters DataFileSyscalls => DataFileSyscallsInternal!;
        public FileSyscallCounters IndexFileSyscalls => IndexFileSyscallsInternal!;
        public BucketTierTelemetry BucketTelemetry => BucketTelemetryInternal!;

        private long _putCalls;
        private long _putCallsWithExpansion;
        private long _putCallsWithoutExpansion;
        private long _syscallsInPutsWithExpansion;
        private long _syscallsInPutsWithoutExpansion;

        private long _getCalls;
        private long _syscallsInGets;
        private long _recordCountCalls;
        private long _recordCountResults;
        private long _deletedCountCalls;
        private long _deletedCountResults;

        public long PutCalls => Interlocked.Read(ref _putCalls);
        public long PutCallsWithExpansion => Interlocked.Read(ref _putCallsWithExpansion);
        public long PutCallsWithoutExpansion => Interlocked.Read(ref _putCallsWithoutExpansion);

        public long SyscallsInPutsWithExpansion => Interlocked.Read(ref _syscallsInPutsWithExpansion);
        public long SyscallsInPutsWithoutExpansion => Interlocked.Read(ref _syscallsInPutsWithoutExpansion);

        public long GetCalls => Interlocked.Read(ref _getCalls);
        public long SyscallsInGets => Interlocked.Read(ref _syscallsInGets);

        public long RecordCountCalls => Interlocked.Read(ref _recordCountCalls);
        public long RecordCountResults => Interlocked.Read(ref _recordCountResults);

        public long DeletedRecordCountCalls => Interlocked.Read(ref _deletedCountCalls);
        public long DeletedRecordCountResults => Interlocked.Read(ref _deletedCountResults);

        public double AvgSyscallsPerPutWithExpansion =>
            PutCallsWithExpansion == 0 ? 0.0 : (double)SyscallsInPutsWithExpansion / PutCallsWithExpansion;

        public double AvgSyscallsPerPutWithoutExpansion =>
            PutCallsWithoutExpansion == 0 ? 0.0 : (double)SyscallsInPutsWithoutExpansion / PutCallsWithoutExpansion;

        public double AvgSyscallsPerGet =>
            GetCalls == 0 ? 0.0 : (double)SyscallsInGets / GetCalls;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordPut(long syscallsDelta, bool hadExpansion)
        {
            Interlocked.Increment(ref _putCalls);

            if (hadExpansion)
            {
                Interlocked.Increment(ref _putCallsWithExpansion);
                Interlocked.Add(ref _syscallsInPutsWithExpansion, syscallsDelta);
            }
            else
            {
                Interlocked.Increment(ref _putCallsWithoutExpansion);
                Interlocked.Add(ref _syscallsInPutsWithoutExpansion, syscallsDelta);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordGet(long syscallsDelta)
        {
            Interlocked.Increment(ref _getCalls);
            Interlocked.Add(ref _syscallsInGets, syscallsDelta);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordLiveRecordCount(ulong result)
        {
            Interlocked.Increment(ref _recordCountCalls);
            Interlocked.Add(ref _recordCountResults, SaturateToInt64(result));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordDeletedRecordCount(ulong result)
        {
            Interlocked.Increment(ref _deletedCountCalls);
            Interlocked.Add(ref _deletedCountResults, SaturateToInt64(result));
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref _putCalls, 0);
            Interlocked.Exchange(ref _putCallsWithExpansion, 0);
            Interlocked.Exchange(ref _putCallsWithoutExpansion, 0);
            Interlocked.Exchange(ref _syscallsInPutsWithExpansion, 0);
            Interlocked.Exchange(ref _syscallsInPutsWithoutExpansion, 0);
            Interlocked.Exchange(ref _getCalls, 0);
            Interlocked.Exchange(ref _syscallsInGets, 0);
            Interlocked.Exchange(ref _recordCountCalls, 0);
            Interlocked.Exchange(ref _recordCountResults, 0);
            Interlocked.Exchange(ref _deletedCountCalls, 0);
            Interlocked.Exchange(ref _deletedCountResults, 0);

            // Assuming FileSyscallCounters has a Reset method; if not, adjust accordingly
            DataFileSyscallsInternal?.Reset();
            IndexFileSyscallsInternal?.Reset();
            BucketTelemetry?.Reset();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long SaturateToInt64(ulong value)
        {
            return value >= long.MaxValue
                ? long.MaxValue
                : (long)value;
        }

        /// <summary>Creates engine-owned telemetry; Open wires the reporting components before publishing it.<br/></summary>
        internal FractalStoreTelemetry() { }
    }
    internal sealed class BucketTierTelemetry
    {
        // Key: (TierOffset, BucketIndex)
        internal readonly ConcurrentDictionary<(ulong tierOffset, uint bucketIndex), long> _bucketWriteCalls = new();
        internal readonly ConcurrentDictionary<(ulong tierOffset, uint bucketIndex), long> _bucketClearCalls = new();

        private readonly BucketCounterView writeView;
        private readonly BucketCounterView clearView;

        /// <summary>Gets a cached live read-only view of recorded bucket writes; no snapshot or per-access wrapper is allocated.<br/></summary>
        public IReadOnlyDictionary<(ulong tierOffset, uint bucketIndex), long> BucketWriteCalls => writeView;
        /// <summary>Gets a cached live read-only view of recorded bucket clears; callers cannot mutate the underlying engine counters.<br/></summary>
        public IReadOnlyDictionary<(ulong tierOffset, uint bucketIndex), long> BucketClearCalls => clearView;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordWrite(ulong tierOffset, ulong bucketIndex)
        {
            _bucketWriteCalls.AddOrUpdate(
                (tierOffset, (uint)bucketIndex),
                static _ => 1L,
                static (_, old) => old + 1L);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecordClear(ulong tierOffset, ulong bucketIndex)
        {
            _bucketClearCalls.AddOrUpdate(
                (tierOffset, (uint)bucketIndex),
                static _ => 1L,
                static (_, old) => old + 1L);
        }

        internal void Reset()
        {
            _bucketWriteCalls.Clear();
            _bucketClearCalls.Clear();
        }

        /// <summary>
        /// Creates engine-owned bucket counters and two cached read-only reporting wrappers.<br/>
        /// Wrappers are allocated once per telemetry instance, never on the bucket-update path.<br/>
        /// </summary>
        internal BucketTierTelemetry()
        {
            writeView = new(_bucketWriteCalls);
            clearView = new(_bucketClearCalls);
        }
    }

    /// <summary>
    /// Projects a live concurrent counter dictionary without exposing any mutation interface.<br/>
    /// Keys and Values retain ConcurrentDictionary's fresh-per-request snapshot semantics rather than caching an old snapshot.<br/>
    /// </summary>
    internal sealed class BucketCounterView : IReadOnlyDictionary<(ulong tierOffset, uint bucketIndex), long>
    {
        private readonly ConcurrentDictionary<(ulong tierOffset, uint bucketIndex), long> counters;

        /// <summary>Creates one reusable reporting view over engine-owned counters; no data is copied.<br/></summary>
        /// <param name="counters">The internally supplied concurrent dictionary to report.<br/></param>
        internal BucketCounterView(ConcurrentDictionary<(ulong tierOffset, uint bucketIndex), long> counters)
            => this.counters = counters;

        public int Count => counters.Count;
        public IEnumerable<(ulong tierOffset, uint bucketIndex)> Keys => counters.Keys;
        public IEnumerable<long> Values => counters.Values;
        public long this[(ulong tierOffset, uint bucketIndex) key] => counters[key];

        /// <summary>Reports whether the specified counter is present without changing it.<br/></summary>
        /// <param name="key">Tier and bucket coordinates to query.<br/></param>
        /// <returns>Whether that counter exists.<br/></returns>
        public bool ContainsKey((ulong tierOffset, uint bucketIndex) key) => counters.ContainsKey(key);

        /// <summary>Retrieves one current counter value using the underlying concurrent lookup.<br/></summary>
        /// <param name="key">Tier and bucket coordinates to query.<br/></param>
        /// <param name="value">The current value, or zero when absent.<br/></param>
        /// <returns>Whether that counter exists.<br/></returns>
        public bool TryGetValue((ulong tierOffset, uint bucketIndex) key, out long value) => counters.TryGetValue(key, out value);

        /// <summary>Enumerates counters using the underlying dictionary's concurrency semantics, without a copied dictionary.<br/></summary>
        /// <returns>The existing concurrent dictionary enumerator.<br/></returns>
        public IEnumerator<KeyValuePair<(ulong tierOffset, uint bucketIndex), long>> GetEnumerator() => counters.GetEnumerator();

        /// <summary>Provides the same read-only enumeration through the nongeneric enumeration contract.<br/></summary>
        /// <returns>An enumerator over current counter entries.<br/></returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }




}
