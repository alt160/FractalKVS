using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace FractalKVS;

/// <summary>
/// Validates offline snapshot bytes without creating controllers, adopting ownership, or initializing missing storage.<br/>
/// This internal component boundary is deliberately separate from ordinary reads and writes.<br/>
/// </summary>
internal static class SnapshotValidator
{
    /// <summary>
    /// Checks complete native headers/atomic EOFs and optionally every reachable routing tier and selected record chain.<br/>
    /// Full validation uses pooled entry blocks and per-tier/per-chain bookkeeping, never payload-sized allocations.<br/>
    /// It cannot establish that separately valid legacy members came from the same snapshot.<br/>
    /// </summary>
    /// <param name="directory">Offline directory containing the quartet.<br/></param>
    /// <param name="name">Exact store basename.<br/></param>
    /// <param name="deep">Whether to walk all reachable routing and selected record headers.<br/></param>
    /// <param name="ct">Cancellation observed between tiers, entry blocks and chain records.<br/></param>
    internal static void Validate(string directory, string name, bool deep, CancellationToken ct)
    {
        using var index = new FileStream(Path.Combine(directory, name + ".fractX"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var data = new FileStream(Path.Combine(directory, name + ".fractD"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var ih = Read<FIndexHeader>(index, 0);
        var dh = Read<FDataHeader>(data, 0);
        if (ih.Magic != (ulong)Consts.FINDEX_MAGIC || ih.HeaderChecksum != ih.CalculateChecksum() ||
            dh.Magic != (ulong)Consts.FINDEX_MAGIC || dh.HeaderChecksum != dh.CalculateChecksum() ||
            ih.Version != 0x1_0000_0000_0000 || dh.Version != 0x1_0000_0000_0000)
            throw new InvalidDataException("Invalid or unsupported Fractal snapshot header.");
        ValidateReservation(ih.FileNameOffset, ih.FileNameLength, ih.FilePathOffset, ih.FilePathLength, ih.RootBucketOffset, FIndexHeader.SizeOf);
        ValidateReservation(dh.FileNameOffset, dh.FileNameLength, dh.FilePathOffset, dh.FilePathLength, dh.FirstRecordOffset, FDataHeader.SizeOf);
        ulong indexEnd = ReadLogicalEnd(index, Path.Combine(directory, name + ".fractX.shm"), ih.RootBucketOffset);
        ulong dataEnd = ReadLogicalEnd(data, Path.Combine(directory, name + ".fractD.shm"), dh.FirstRecordOffset);
        var root = ReadTier(index, ih.RootBucketOffset, 0, ih.RootBucketOffset, indexEnd);
        if (root.BucketCount != ih.RootBucketCount)
            throw new InvalidDataException("Fractal root tier and header disagree.");
        if (!deep)
            return;

        const int entriesPerBlock = 2048;
        byte[] rented = ArrayPool<byte>.Shared.Rent(entriesPerBlock * (int)BucketEntry.SizeOf);
        try
        {
            var tiers = new List<(ulong Offset, BucketTierHeader Header)> { (ih.RootBucketOffset, root) };
            var seen = new HashSet<ulong> { ih.RootBucketOffset };
            var chainSeen = new HashSet<ulong>();
            for (int t = 0; t < tiers.Count; t++)
            {
                var tier = tiers[t];
                for (uint first = 0; first < tier.Header.BucketCount;)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(entriesPerBlock, tier.Header.BucketCount - first);
                    var block = rented.AsSpan(0, count * (int)BucketEntry.SizeOf);
                    index.Position = checked((long)(tier.Offset + BucketTierHeader.SizeOf + first * (ulong)BucketEntry.SizeOf));
                    index.ReadExactly(block);
                    foreach (ref readonly var entry in MemoryMarshal.Cast<byte, BucketEntry>(block))
                    {
                        if (entry.IsUnset) continue;
                        if (entry.IsChainedBackref)
                        {
                            if (entry.IsExpanded || entry.OffsetA >= tier.Header.BucketCount ||
                                Read<BucketEntry>(index, tier.Offset + BucketTierHeader.SizeOf + entry.OffsetA * BucketEntry.SizeOf).IsChainedBackref)
                                throw new InvalidDataException("Invalid Fractal routing back-reference.");
                            continue;
                        }
                        if (entry.IsExpanded)
                        {
                            if (!seen.Add(entry.RecordOffset))
                                throw new InvalidDataException("Cyclic or multiply owned Fractal routing tier.");
                            var child = ReadTier(index, entry.RecordOffset, checked((ushort)(tier.Header.Tier + 1)), ih.RootBucketOffset, indexEnd);
                            tiers.Add((entry.RecordOffset, child));
                        }
                        else
                            ValidateChain(data, entry.RecordOffset, entry.RecordSize, dh.FirstRecordOffset, dataEnd, chainSeen, ct);
                    }
                    first += (uint)count;
                }
            }
            ulong previousEnd = ih.RootBucketOffset;
            foreach (var tier in tiers.OrderBy(value => value.Offset))
            {
                if (tier.Offset < previousEnd)
                    throw new InvalidDataException("Overlapping reachable Fractal routing tiers.");
                previousEnd = checked(tier.Offset + BucketTierHeader.SizeOf + tier.Header.BucketCount * (ulong)BucketEntry.SizeOf);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>Checks persisted ownership offsets without decoding or rewriting their contents.<br/></summary>
    /// <param name="nameOffset">Name start.<br/></param><param name="nameLength">Name byte count.<br/></param>
    /// <param name="pathOffset">Path start.<br/></param><param name="pathLength">Path byte count.<br/></param>
    /// <param name="end">End of ownership reservation.<br/></param><param name="headerSize">Packed header size.<br/></param>
    private static void ValidateReservation(uint nameOffset, ushort nameLength, uint pathOffset, ushort pathLength, uint end, uint headerSize)
    {
        if (nameOffset < headerSize || nameLength == 0 || pathLength == 0 ||
            (ulong)nameOffset + nameLength > pathOffset || (ulong)pathOffset + pathLength > end)
            throw new InvalidDataException("Invalid Fractal ownership reservation.");
    }

    /// <summary>Reads a complete persisted atomic region and bounds its logical EOF by the primary file.<br/></summary>
    /// <param name="primary">Read-only primary stream.<br/></param><param name="path">Atomic sidecar path.<br/></param>
    /// <param name="minimum">Minimum logical EOF.<br/></param><returns>Validated logical EOF.<br/></returns>
    private static ulong ReadLogicalEnd(FileStream primary, string path, ulong minimum)
    {
        using var atomics = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (atomics.Length != AtomicIntegers.RegionSize)
            throw new InvalidDataException("Incomplete or unsupported Fractal atomic region.");
        long end = Read<long>(atomics, 0);
        if (end < 0 || (ulong)end < minimum || end > primary.Length)
            throw new InvalidDataException("Fractal logical EOF is outside its primary file.");
        return (ulong)end;
    }

    /// <summary>Reads a bounded power-of-two tier at the required depth without materializing its entries.<br/></summary>
    /// <param name="stream">Read-only index stream.<br/></param><param name="offset">Tier start.<br/></param>
    /// <param name="level">Expected depth.<br/></param><param name="minimum">First allowed tier offset.<br/></param>
    /// <param name="end">Logical index EOF.<br/></param><returns>Validated packed tier header.<br/></returns>
    private static BucketTierHeader ReadTier(FileStream stream, ulong offset, ushort level, ulong minimum, ulong end)
    {
        if (offset < minimum || offset > end || end - offset < BucketTierHeader.SizeOf)
            throw new InvalidDataException("Fractal tier is outside the index.");
        var header = Read<BucketTierHeader>(stream, offset);
        ulong length = BucketTierHeader.SizeOf + header.BucketCount * (ulong)BucketEntry.SizeOf;
        if (header.Tier != level || header.BucketCount == 0 || !BitOperations.IsPow2(header.BucketCount) || length > int.MaxValue || length > end - offset)
            throw new InvalidDataException("Invalid Fractal tier depth, count or bounds.");
        return header;
    }

    /// <summary>Validates each selected collision-chain record's bounds and payload lengths without reading its payload.<br/></summary>
    /// <param name="stream">Read-only data stream.<br/></param><param name="offset">Selected chain head.<br/></param>
    /// <param name="size">Selected payload size, not allocation size.<br/></param><param name="minimum">First record offset.<br/></param>
    /// <param name="end">Logical data EOF.<br/></param><param name="seen">Reusable per-chain cycle-detection storage.<br/></param>
    /// <param name="ct">Cancellation between records.<br/></param>
    private static void ValidateChain(FileStream stream, ulong offset, uint size, ulong minimum, ulong end, HashSet<ulong> seen, CancellationToken ct)
    {
        seen.Clear();
        do
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(offset) || offset < minimum || offset > end || end - offset < (ulong)RecordHeader.SizeOf)
                throw new InvalidDataException("Cyclic or out-of-bounds Fractal record chain.");
            var header = Read<RecordHeader>(stream, offset);
            // Deleted members legitimately remain in collision chains with ID=0; readers skip them but still follow their next links.
            if (header.DataLength != size || header.RecordSize < (ulong)RecordHeader.SizeOf + header.DataLength + sizeof(ulong) ||
                header.RecordSize > end - offset)
                throw new InvalidDataException("Invalid selected Fractal record header.");
            offset = header.NextRecordOffset;
            size = header.NextRecordSize;
        } while (offset != 0);
    }

    /// <summary>Reads exactly one packed unmanaged value, rejecting truncated bytes rather than zero-filling them.<br/></summary>
    /// <typeparam name="T">Existing native format value.<br/></typeparam><param name="stream">Read-only member stream.<br/></param>
    /// <param name="offset">Absolute byte offset.<br/></param><returns>The complete packed value.<br/></returns>
    private static T Read<T>(FileStream stream, ulong offset) where T : unmanaged
    {
        Span<T> value = stackalloc T[1];
        stream.Position = checked((long)offset);
        stream.ReadExactly(MemoryMarshal.AsBytes(value));
        return value[0];
    }
}
