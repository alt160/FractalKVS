using System;
using System.Collections.Generic;
using System.Linq;








namespace FractalKVS
{
    /// <summary>Configures FractalKVS creation, index growth, concurrency, and durability behavior.<br/></summary>
    public record FractalStoreConfiguration()
    {
        //======  FIELDS  ======
        private FanOutAlgorithms indexGrowthStrategy = FanOutAlgorithms.Pow2;
        private int lowerTiersExpandCount = 0;








        //======  PROPERTIES  ======
        /// <summary>Gets or sets whether a missing store family may be created when opened.<br/></summary>
        public bool CreateIfNotExists { get; set; } = true;
        /// <summary>Gets or sets the algorithm used when a bucket requires an additional index tier.<br/></summary>
        public FanOutAlgorithms IndexGrowthStrategy
        {
            get => indexGrowthStrategy;
            set
            {
                indexGrowthStrategy = value;
                if (value == FanOutAlgorithms.Pow2xChain4x) lowerTiersExpandCount = MaxChainedRecords * 4;
                if (value == FanOutAlgorithms.Pow4xChain4x) lowerTiersExpandCount = MaxChainedRecords * 4;
            }
        }
        /// <summary>
        /// Gets or sets the maximum number of megabytes allocated for caching index tier data.
        /// </summary>
        /// <remarks>Increasing this value may improve lookup performance by allowing more index data to
        /// be cached, but will also increase memory usage. The optimal value depends on the expected workload and
        /// available system resources.</remarks>
        public long MaxCachedIndexTierMB { get; set; } = 8; // 8 MB, approx 325k+ total keys cached.
        /// <summary>Gets or sets the collision-chain length that prompts index expansion.<br/></summary>
        public int MaxChainedRecords { get; set; } = 4;
        /// <summary>Gets or sets the minimum batch size at which <c>PutMany</c> may use parallel workers.<br/></summary>
        public int PutManyParallelMinRecordCount { get; set; } = 35_556;
        /// <summary>Gets or sets the default maximum worker count for large <c>PutMany</c> operations.<br/></summary>
        public int PutManyParallelThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
        /// <summary>Gets or sets the default maximum worker count for multi-record reads.<br/></summary>
        public int GetManyParallelThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
        /// <summary>Gets or sets whether index-cycle diagnostics are enabled for troubleshooting.<br/></summary>
        public bool EnableIndexCycleDebug { get; set; } = false;
        /// <summary>
        /// The initial count of expected or possible records for the store.<br/>
        /// Initial file size for the index file will be InitialCapacity * 24 bytes plus the size of the header.<br/>
        /// Smaller numbers here will use less disk space initially, but may lead to more frequent index tier expansions as records are added.<br/>
        /// Larger numbers will use more disk space initially, but may reduce the frequency of index tier expansions.<br/>
        /// </summary>
        public uint RootTierBucketCount { get; set; } = 64;
        /// <summary>
        /// <see cref="FractalStoreWriteBehavior.Deferred"/> is the default and recommended setting for high write performance but has slightly less durability.<br/>
        /// <see cref="FractalStoreWriteBehavior.Immediate"/> provides the highest durability by flushing to disk on every write, but will dramatically lower write performance.<br/>
        /// </summary>
        public FractalStoreWriteBehavior WriteBehavior { get; set; } = FractalStoreWriteBehavior.Deferred;
        /// <summary>
        /// When true (default), optimizes for single-process usage by allowing higher layers to skip cross-process validation paths (for example, cache coherence checks).<br/>
        /// Set to false when multiple processes may concurrently access the same store so callers preserve cross-process safety checks.<br/>
        /// </summary>
        public bool AssumeSingleProcess { get; set; } = true;
        /// <summary>
        /// Maximum number of contiguous tombstoned tail records to trim in a single fast-trim pass.<br/>
        /// This is a batch size for tail compaction before the normal defrag logic runs.<br/>
        /// Set to 0 to disable fast trimming.<br/>
        /// </summary>
        public int FastTrimTailBatchRecords { get; set; } = 256;
    }

    /// <summary>
    /// Selects the durability behavior for Fractal store file writes.<br/>
    /// </summary>
    public enum FractalStoreWriteBehavior : byte
    {
        /// <summary>
        /// Requests write-through behavior so each write waits for the operating system's durable-write path before returning.<br/>
        /// </summary>
        Immediate,

        /// <summary>
        /// Allows the operating system page cache to defer physical persistence for higher write throughput.<br/>
        /// </summary>
        Deferred
    }
}
