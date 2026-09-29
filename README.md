# FractalKVS

FractalKVS is a .NET-native, file-backed durable high-performance dictionary for `ulong` keys and binary values. It is for applications that need predictable, hash-directed lookup, large incrementally grown stores, concurrent reads and writes, and direct control over binary payloads—without a server, fixed store preallocation, a document model, or a query language.

It is deliberately a storage engine, not a general-purpose database. FractalKVS keeps the application in charge of record identity, serialization, schema, and higher-level relationships.

## Why FractalKVS

Many embedded stores are excellent choices when their model is what an application needs. FractalKVS exists for a narrower case: for an application that needs a durable local dictionary, handles it's own serialization to binary, can use numeric identities, and needs that dictionary to remain practical as the store grows - possibly very large.

- **No fixed store size up front.** A store begins with a configurable root index and grows index tiers only when its data requires them. A small initial store does not require reserving the final store's index footprint.
- **Separate index and data files.** The index (`.fractX`) and record data (`.fractD`) are intentionally independent. This avoids having to balance index placement against payload placement inside one file and permits deliberate storage-tier choices when a deployment benefits from them.
- **Predictable hash-directed lookup.** A lookup selects a root bucket and follows only the required expansion tiers and bounded collision path; it does not scan the store. In practical terms this is near-constant-time O(1) behavior regardless of file size.
- **Tunable rather than pre-committed.** The default configuration is for general use. Advanced callers can tune root-bucket count, fan-out strategy, collision-chain threshold, index-cache budget, and bulk-operation parallelism to match a known workload.
- **Large binary records.** Values are native binary payloads rather than documents. The on-disk record format uses a 32-bit payload length; the current managed implementation also requires a practical payload to fit a contiguous managed buffer. Treat the usable maximum as an application-and-runtime limit of under 2GB per record.
- **Concurrent operational paths.** Normal reads do not take a global reader lock. Writers coordinate by bucket stripe, and file access uses per-thread handles. This enables concurrent reads and writes while preserving per-record update coordination.
- **Record-local update durability.** Normal updates use paired A/B record and index locations rather than a separate write-ahead log or transaction journal. Choose immediate or deferred write behavior according to the application's durability and throughput requirements.

## When it fits

FractalKVS is a strong fit when you need one or more of the following:

- A durable, embedded `ulong` → `byte[]` dictionary with no database service to deploy.
- An application-owned binary contract, such as a custom codec, `Span<byte>`-based format, or fixed record ID scheme.
- Large, growing record sets where reserving a complete index in advance is undesirable.
- High-volume reads, writes, or bulk ingestion where avoiding object mapping and unnecessary payload transformation matters.
- Group-oriented traversal: the upper eight bits of a record ID may represent an application-defined group such as tenant, store, record type, or partition.
- A deployment that benefits from placing index and payload files independently.

## When another store is a better fit

Choose a different tool when its higher-level model is the actual requirement:

- Use a relational database when SQL, joins, ad-hoc queries, or multi-record transactional workflows are central.
- Use a document database when mapped objects, documents, secondary indexes, and document queries matter more than an application-owned byte payload.
- Use an ordered key-value engine when arbitrary ordered keys and range scans are first-class requirements.
- Use a cache when persistence and recovery after process restart are not required.

FractalKVS intentionally provides none of those abstractions. Its narrow API is the point: numeric identity in, binary payload out.

## Quick start

```csharp
using FractalKVS;

var configuration = new FractalStoreConfiguration
{
    // Deferred is the default: favor throughput through the OS page cache.
    WriteBehavior = FractalStoreWriteBehavior.Deferred
};

var store = new FractalStore(
    folderPath: @"D:\ApplicationData",
    name: "records",
    config: configuration);

store.Open();
try
{
    store.PutOne(42UL, new byte[] { 1, 2, 3, 4 });

    if (store.TryGetOne(42UL, out var record))
    {
        using (record)
        {
            ReadOnlySpan<byte> value = record.Data.Span;
            // Consume value while record is still in scope.
        }
    }
}
finally
{
    store.Close();
}
```

`GetOne` throws when an ID is absent; `TryGetOne` returns `false` instead. `PutMany` accepts a sequence of `(ulong ID, Memory<byte> Data)` values and can use configured or call-scoped parallelism for larger batches.

## Record ownership is explicit

A returned `Record` may hold a rented buffer. Dispose it as soon as its payload is no longer needed:

```csharp
using Record record = store.GetOne(42UL);
Process(record.Data.Span);
```

Do not retain `record.Data`, `record.DataSpan`, or a span derived from either after disposing the `Record`. Copy the bytes first when the value must outlive the read scope. This explicit lifetime is how FractalKVS avoids forcing a new long-lived allocation for every read.

## IDs and grouping

The public key type is `ulong`. Applications that already have numeric IDs can use them directly. Applications that begin with strings should establish their own numeric identity policy; FractalKVS deliberately does not present hashed strings as a collision-free key contract.

For group traversal, FractalKVS treats the upper eight bits as an application-defined group key. For example:

```csharp
byte tenant = 7;
ulong localID = 12345;
ulong recordID = ((ulong)tenant << 56) | localID;

foreach (ulong id in store.IterateRecordIDsByGroupKey(tenant))
{
    // IDs belonging to tenant 7.
}
```

The group APIs include identity-only traversal, natural record traversal, physical-order bulk traversal, and partition/block producers for advanced pipelines. Physical bulk order is an I/O-oriented order, not a promise of numeric or insertion order.

## Tuning growth and memory behavior

The defaults target general use. Tune only after measuring a representative workload:

```csharp
var configuration = new FractalStoreConfiguration
{
    // Start with a larger root index when the initial record population is known.
    RootTierBucketCount = 4_096,

    // Permit more cached index-tier data when memory is available.
    MaxCachedIndexTierMB = 64,

    // Select an expansion strategy appropriate for the workload.
    IndexGrowthStrategy = FanOutAlgorithms.Pow2,

    // Configure batch parallelism for large PutMany calls.
    PutManyParallelThreadCount = Environment.ProcessorCount
};
```

`RootTierBucketCount` must be a power of two. A smaller value conserves initial index space and can cause more tier expansion; a larger value spends more index space up front in exchange for fewer early expansions. Keep the default unless the expected initial population or workload makes a different tradeoff clear.

## Concurrency and durability

FractalKVS is designed for normal concurrent reads and writes within its supported operating model. Reads proceed without a global reader lock; writes coordinate at bucket-stripe granularity so unrelated writes can make progress concurrently.

The configuration exposes two write behaviors:

| Behavior | Intended tradeoff |
| --- | --- |
| `Deferred` (default) | Higher write throughput; the OS may defer physical persistence through its page cache. |
| `Immediate` | Requests write-through behavior for each write; stronger persistence timing at a potentially substantial throughput cost. |

An update is represented through paired A/B locations in both the data mutation path and the index head. This is record-level resilience, not a claim of general multi-record transactions. If a workflow needs all-or-nothing semantics across several records, establish that protocol above FractalKVS.

`AssumeSingleProcess` defaults to `true`. Set it to `false` when multiple processes may access the same store so cross-process validation paths remain enabled. Exclusive maintenance operations, such as index rebuild, still require callers to gate concurrent writers.

## Store lifecycle and maintenance

Call `Open()` before normal operations and `Close()` when finished. Stores create a data file (`.fractD`), index file (`.fractX`), and coordination sidecars as needed.

- Use `Backup` for a store backup; observe its API requirements for the configured operating mode.
- Use `RebuildIndex` only as an offline, exclusive maintenance operation.
- `StartOnlineDefrag` and `StopOnlineDefrag` control online space-reclamation work.
- If a store is intentionally copied or moved, call `FractalStore.TakeOwnership(folderPath, name, configuration)` on the offline relocated store before opening it. This rebinds stored path/name metadata; it does not move data for you.

## Design boundaries

FractalKVS makes a few intentional tradeoffs:

- It stores binary values, not objects. Bring your own serialization.
- It uses numeric keys. String hashing is a convenience, not a collision-free string-key contract.
- It does not provide SQL, object mapping, secondary indexes, range scans, or a general query language.
- Its fast lookup behavior depends on normal hash distribution and bounded collision/tier growth; benchmark your own keys and access pattern.
- Large payloads are supported by the record format, but the usable size is bounded by managed contiguous-memory limits and available resources.
- Record disposal is part of the API contract for low-allocation reads.

Those boundaries are what let FractalKVS remain a focused, durable dictionary rather than accumulating database layers an application may not need.

## License

Apache License 2.0. See [LICENSE](LICENSE).
