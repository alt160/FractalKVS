using Standart.Hash.xxHash;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;








namespace FractalKVS
{
    //====== TYPES ======
    internal class FData
    {
        //======  FIELDS  ======
        FractalStoreConfiguration config;
        internal FDataHeader header;


        private const uint DefaultFirstRecordOffset = 4096;





        internal FileController fc;








        //======  CONSTRUCTORS  ======
        public FData(string folderPath, string name, FractalStoreConfiguration config)
        {
            var fifx = new FileInfo($"{folderPath}\\{name}.fractD");
            if (!fifx.Exists)
                if (config.CreateIfNotExists)
                    fc = FileController.CreateOrOpen($"{folderPath}\\{name}.fractD", config.WriteBehavior);
                else
                    throw new FileNotFoundException($"File '{fifx.FullName}' not found and config.CreateIfNotExists=false.");
            else
                fc = FileController.Open($"{folderPath}\\{name}.fractD", config.WriteBehavior);

            this.config = config;
        }








        //------ Public Methods -----
        //======  METHODS  ======
        public (ulong recordOffset, Record record) AppendRecord(ulong recordID, ReadOnlySpan<byte> data)
        {
            return AppendRecord(recordID, 0ul, default, data);
        }

        public (ulong recordOffset, Record record) AppendRecord(ulong recordID, ulong priorRecordOffset, RecordHeader priorRecordHeader, ReadOnlySpan<byte> data)
        {
            var recordBytes = ArrayPool<byte>.Shared.Rent(RecordHeader.SizeOf + sizeof(ulong) + data.Length);
            var recordHeader = new RecordHeader()
            {
                ID = recordID,
                DataLength = (uint)data.Length,
                RecordSize = (uint)(RecordHeader.SizeOf + data.Length + sizeof(ulong)),
                PriorMutationOffset = priorRecordOffset,
                PriorMutationSize = priorRecordHeader.DataLength,
                DataChecksum = xxHash64.ComputeHash(data, data.Length),
            };


            // using no-alloc methods, write record header into the start of the recordBytes buffer 
            var headerBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref recordHeader, 1));
            Unsafe.WriteUnaligned<RecordHeader>(ref MemoryMarshal.GetReference(recordBytes.AsSpan()), recordHeader);

            // copy data into recordBytes buffer after header
            Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetReference(recordBytes.AsSpan().Slice(RecordHeader.SizeOf)), ref MemoryMarshal.GetReference(data), (uint)data.Length);

            // write record size at end of recordBytes buffer
            Unsafe.WriteUnaligned<ulong>(ref MemoryMarshal.GetReference(recordBytes.AsSpan().Slice(RecordHeader.SizeOf + data.Length)), (ulong)(RecordHeader.SizeOf + data.Length));

            var recordOffset = fc.WriteAtEnd(recordBytes.AsSpan().Slice(0, RecordHeader.SizeOf + data.Length + sizeof(ulong)));
            return (recordOffset, new Record(recordBytes, (uint)data.Length));
        }

        public void DeleteAllRecords()
        {
            fc.SetLength(header.FirstRecordOffset);
            fc.FlushToDisk();
        }

        public IEnumerable<RecordHeader> EnumerateRecordHeaders()
        {
            ulong offset = FDataHeader.SizeOf;
            while (offset < (ulong)fc.Length)
            {
                var recordHeader = MemoryMarshal.Read<RecordHeader>(fc.ReadSpan(offset, (uint)RecordHeader.SizeOf));
                yield return recordHeader;
                ulong recordSize = MemoryMarshal.Read<ulong>(fc.ReadSpan(offset + (ulong)RecordHeader.SizeOf + (ulong)recordHeader.DataLength, 8));
                offset += recordSize;
            }
        }

        public void EraseRecord(ulong recordID, ulong recordOffset)
        {
            var recordHeader = MemoryMarshal.Read<RecordHeader>(fc.ReadSpan(recordOffset, (uint)RecordHeader.SizeOf));
            if (recordHeader.ID != recordID)
                throw new RecordNotFoundException($"Record ID {recordID} not found at offset {recordOffset} or in its mutation chain.");

            uint remaining = recordHeader.DataLength;
            ulong writePos = recordOffset + (ulong)RecordHeader.SizeOf;
            Span<byte> zeroChunk = stackalloc byte[32 * 1024]; // 32KB stack buffer for writes

            while (remaining > 0)
            {
                int toWrite = (int)Math.Min((uint)zeroChunk.Length, remaining);
                fc.Write(zeroChunk.Slice(0, toWrite), writePos);
                writePos += (ulong)toWrite;
                remaining -= (uint)toWrite;
            }

            // Compute checksum for the zeroed payload without allocating a big buffer.
            using var zs = new ZeroStream(recordHeader.DataLength);
            var zeroChecksum = xxHash64.ComputeHash(zs);

            recordHeader.DataChecksum = zeroChecksum;
            var recordHeaderBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref recordHeader, 1));
            fc.Write(recordHeaderBytes, recordOffset);
        }

        public void Init()
        {
            if (fc.IsNew || fc.Length < FDataHeader.SizeOf)
            {
                // build and write file header
                header = new FDataHeader();
                header.CreatedTicks = DateTime.UtcNow.Ticks;
                header.Uuid = Guid.NewGuid();
                header.Magic = (ulong)Consts.FINDEX_MAGIC;
                header.PageSizeLogical = fc.DiskGeometry.ClusterSize;
                header.PageSizePhysical = fc.DiskGeometry.PhysicalSectorSize;
                header.FileNameOffset = FDataHeader.SizeOf;
                Span<byte> fName = stackalloc byte[1024];
                var fNameWritten = System.Text.Encoding.UTF8.GetBytes(fc.Name, fName);
                header.FileNameLength = (ushort)fNameWritten;
                Span<byte> fPath = stackalloc byte[1024];
                var fPathWritten = System.Text.Encoding.UTF8.GetBytes(fc.DirectoryName, fPath);
                header.FilePathLength = (ushort)fPathWritten;
                header.FilePathOffset = (uint)(header.FileNameOffset + fNameWritten);
                header.FirstRecordOffset = Math.Max(FDataHeader.SizeOf + header.FilePathLength + header.FileNameLength, Math.Max(DefaultFirstRecordOffset, fc.DiskGeometry.ClusterSize)); // align first record to at least 4KB or cluster size
                header.Version = 0x1_0000_0000_0000;

                header.HeaderChecksum = header.CalculateChecksum();
                var headerBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1));


                fc.Write(headerBytes, 0);
                fc.Write(fName, header.FileNameOffset);
                fc.Write(fPath, header.FilePathOffset);

            }
            else
            {
                Span<byte> headerBytes = fc.ReadSpan(0, (uint)FDataHeader.SizeOf);
                header = MemoryMarshal.Read<FDataHeader>(headerBytes);

                if (header.Magic != (ulong)Consts.FINDEX_MAGIC)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is not a valid fractX file.  Incorrect magic number.");


                var curPath = fc.DirectoryName;
                var curName = fc.Name;

                if (header.FileNameOffset < FDataHeader.SizeOf || header.FileNameLength == 0)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");
                if (header.FilePathOffset < header.FileNameOffset + header.FileNameLength || header.FilePathLength == 0)
                    throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot initialize.");

                var fPath = fc.ReadSpan(header.FilePathOffset, header.FilePathLength);
                var fName = fc.ReadSpan(header.FileNameOffset, header.FileNameLength);

                var fPathStr = System.Text.Encoding.UTF8.GetString(fPath);
                var fNameStr = System.Text.Encoding.UTF8.GetString(fName);

                if (!FractalStore.StoredLocationMatches(fPathStr, fNameStr, curPath, curName))
                    throw new FileCopiedOrMovedException(fPathStr, fNameStr, curPath, curName, header.Uuid, isIndexFile: false);

            }
        }

        /// <summary>
        /// Rewrites the header's stored file name and directory path to the current file location without moving any records.<br/>
        /// Use after intentionally moving or copying the data file while the store is offline.<br/>
        /// Throws if the new UTF-8 path or name cannot fit inside the reserved header space.<br/>
        /// </summary>
        public void TakeOwnership()
        {
            if (fc.Length < FDataHeader.SizeOf)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete. Cannot take ownership.");

            Span<byte> headerBytes = fc.ReadSpan(0, (uint)FDataHeader.SizeOf);
            var newHeader = MemoryMarshal.Read<FDataHeader>(headerBytes);

            if (newHeader.Magic != (ulong)Consts.FINDEX_MAGIC)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is not a valid fractX file.  Incorrect magic number.");

            var checksum = newHeader.CalculateChecksum();
            if (checksum != newHeader.HeaderChecksum)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' is corrupted or incomplete  Cannot take ownership.");

            if (newHeader.FileNameOffset < FDataHeader.SizeOf || newHeader.FilePathOffset <= newHeader.FileNameOffset)
                throw new System.IO.InvalidDataException($"File '{fc.FullName}' has invalid header offsets.  Cannot take ownership.");
            if (newHeader.FirstRecordOffset <= newHeader.FilePathOffset)
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
            int availablePathBytes = checked((int)(newHeader.FirstRecordOffset - newHeader.FilePathOffset));

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

        public (ulong recordOffset, Record record) MutateRecord(ulong recordID, ulong currentRecordOffset, ReadOnlySpan<byte> data)
        {
            // Load the current head for this key.
            var recordHeader = MemoryMarshal.Read<RecordHeader>(
                fc.ReadSpan(currentRecordOffset, (uint)RecordHeader.SizeOf));

            if (recordHeader.ID != recordID)
                throw new RecordNotFoundException(
                    $"Record ID {recordID} not found at offset {currentRecordOffset} or in its mutation chain.");


            // ------------------------------------------------
            // 0. SKIP MUTATION IF DATA IS IDENTICAL
            // ------------------------------------------------
            if (data.Length == recordHeader.DataLength)
            {
                ulong incomingHash = xxHash64.ComputeHash(data, data.Length);

                if (incomingHash == recordHeader.DataChecksum)
                {
                    // Payload is identical: no file change, no header update.
                    // Just return the existing on-disk record.
                    var record = ReadRecord(currentRecordOffset, recordHeader.DataLength);
                    return (currentRecordOffset, record);
                }
            }

            // First mutation: always append. This seeds A/B durability.
            if (recordHeader.PriorMutationOffset == 0)
                return AppendRecord(recordID, currentRecordOffset, recordHeader, data);

            // For further mutations, look at the *immediate* prior copy.
            var priorRecordHeader = MemoryMarshal.Read<RecordHeader>(
                fc.ReadSpan(recordHeader.PriorMutationOffset, (uint)RecordHeader.SizeOf));

            if (priorRecordHeader.ID != recordID)
                throw new RecordNotFoundException(
                    $"Record ID {recordID} not found at offset {recordHeader.PriorMutationOffset} or in its mutation chain.");

            // If the new data won't fit in the prior slot, append again.
            if (data.Length > priorRecordHeader.DataLength)
                return AppendRecord(recordID, currentRecordOffset, priorRecordHeader, data);

            // Otherwise, overwrite the prior slot in-place and make it the new head,
            // pointing back to the old head (A/B ping-pong).
            var recordBytes = ArrayPool<byte>.Shared.Rent(RecordHeader.SizeOf + sizeof(ulong) + data.Length);

            try
            {
                // Rewire the prior header to represent the new head.
                priorRecordHeader.PriorMutationOffset = currentRecordOffset;
                priorRecordHeader.PriorMutationSize = recordHeader.DataLength;
                priorRecordHeader.DataLength = (uint)data.Length;
                priorRecordHeader.DataChecksum = xxHash64.ComputeHash(data, data.Length);

                // Layout: [header][data][stored-record-size]
                var span = recordBytes.AsSpan(0, RecordHeader.SizeOf + data.Length + sizeof(ulong));

                // header
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference(span),
                    priorRecordHeader);

                // data
                data.CopyTo(span.Slice(RecordHeader.SizeOf, data.Length));

                // Overwrite the prior slot.
                fc.Write(span, recordHeader.PriorMutationOffset);

                // New head for this key is now at priorMutationOffset.
                return (recordHeader.PriorMutationOffset, new Record(recordBytes, (uint)data.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(recordBytes);
            }
        }


        public (Memory<byte> data, RecordHeader header) ReadRecord(ulong? recordID, ulong recordOffset)
        {

            var recordHeader = MemoryMarshal.Read<RecordHeader>(fc.ReadSpan(recordOffset, (uint)RecordHeader.SizeOf));
            if (recordID is not null && recordHeader.ID != recordID)
                throw new RecordNotFoundException($"Record ID {recordID} not found at offset {recordOffset} or in its mutation chain.");
            var data = fc.Read(recordOffset + (ulong)RecordHeader.SizeOf, recordHeader.DataLength);
            return (data, recordHeader);
        }

        public Span<byte> ReadRecordData(RecordHeader header, ulong recordOffset)
        {
            return fc.ReadSpan(recordOffset + (ulong)RecordHeader.SizeOf, header.DataLength);
        }

        public Record ReadRecord(ulong recordOffset, uint dataLength)
        {
            int rLen = (int)dataLength + RecordHeader.SizeOf;
            var data = ArrayPool<byte>.Shared.Rent(rLen);
            fc.Read(data.AsSpan(), recordOffset, (uint)rLen);
            return new Record(data, (uint)dataLength);
        }

        public RecordHeader ReadRecordHeader(ulong recordOffset)
        {
            Span<byte> headerBytes = stackalloc byte[RecordHeader.SizeOf];
            fc.Read(headerBytes, recordOffset, (uint)RecordHeader.SizeOf);
            return MemoryMarshal.Read<RecordHeader>(headerBytes);
        }

        /// <summary>
        /// Reads one caller-owned contiguous data-file range without allocating an intermediate managed array.<br/>
        /// The caller is responsible for validating that the requested range remains inside the current logical file length.<br/>
        /// </summary>
        /// <param name="destination">Caller-owned destination receiving the complete requested range.<br/></param>
        /// <param name="offset">Physical data-file offset of the first requested byte.<br/></param>
        internal void ReadRange(Span<byte> destination, ulong offset)
            => fc.Read(destination, offset, checked((uint)destination.Length));

        /// <summary>
        /// Reads record headers for an ascending set of physical offsets through bounded contiguous windows.<br/>
        /// Callers retain responsibility for logical ordering; this method returns one header at the same ordinal as each supplied sorted offset.<br/>
        /// The window buffer is rented once, which replaces thousands of tiny synchronous file calls without introducing a persisted-format dependency.<br/>
        /// </summary>
        /// <param name="sortedRecordOffsets">Strictly ascending live record offsets whose complete headers fit inside the current data file.<br/></param>
        /// <param name="headers">Destination span with exactly one slot per supplied offset.<br/></param>
        /// <param name="readWindowBytes">Maximum contiguous physical read size; values smaller than one header are rejected.<br/></param>
        /// <exception cref="ArgumentException">Thrown when destination length differs from the offset count or offsets are not strictly ascending.<br/></exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the window cannot hold one record header.<br/></exception>
        /// <exception cref="InvalidDataException">Thrown when a requested header lies outside the current data file.<br/></exception>
        internal void ReadRecordHeadersCoalesced(
            ReadOnlySpan<ulong> sortedRecordOffsets,
            Span<RecordHeader> headers,
            int readWindowBytes = 256 * 1024)
        {
            if (headers.Length != sortedRecordOffsets.Length)
                throw new ArgumentException("Header destination length must match the record-offset count.", nameof(headers));
            if (readWindowBytes < RecordHeader.SizeOf)
                throw new ArgumentOutOfRangeException(nameof(readWindowBytes));
            if (sortedRecordOffsets.Length == 0)
                return;

            ulong fileLength = checked((ulong)fc.Length);
            byte[] window = ArrayPool<byte>.Shared.Rent(readWindowBytes);
            try
            {
                int requestIndex = 0;
                ulong priorOffset = 0;
                while (requestIndex < sortedRecordOffsets.Length)
                {
                    ulong windowStart = sortedRecordOffsets[requestIndex];
                    if (requestIndex > 0 && windowStart <= priorOffset)
                        throw new ArgumentException("Record offsets must be strictly ascending.", nameof(sortedRecordOffsets));
                    if (windowStart > fileLength || fileLength - windowStart < (ulong)RecordHeader.SizeOf)
                        throw new InvalidDataException($"Record header offset {windowStart} is outside the current Fractal data file.");

                    int windowLength = checked((int)Math.Min((ulong)readWindowBytes, fileLength - windowStart));
                    fc.Read(window.AsSpan(0, windowLength), windowStart, checked((uint)windowLength));
                    ulong windowLastHeaderStart = windowStart + checked((ulong)(windowLength - RecordHeader.SizeOf));
                    while (requestIndex < sortedRecordOffsets.Length)
                    {
                        ulong recordOffset = sortedRecordOffsets[requestIndex];
                        if (recordOffset > windowLastHeaderStart)
                            break;
                        if (requestIndex > 0 && recordOffset <= priorOffset)
                            throw new ArgumentException("Record offsets must be strictly ascending.", nameof(sortedRecordOffsets));

                        int relativeOffset = checked((int)(recordOffset - windowStart));
                        headers[requestIndex] = MemoryMarshal.Read<RecordHeader>(
                            window.AsSpan(relativeOffset, RecordHeader.SizeOf));
                        priorOffset = recordOffset;
                        requestIndex++;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(window);
            }
        }

        public void UpdateRecordHeader(ulong recordOffset, RecordHeader header)
        {
            var headerBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1));
            fc.Write(headerBytes, recordOffset);
        }
        /// <summary>
        /// Overwrites every physical payload copy in one validated mutation chain and tombstones each corresponding record header.<br/>
        /// Traversal completes before the first overwrite so zeroing an ID cannot hide a later chain member from this operation.<br/>
        /// The path is intentionally allocation-tolerant because it is selected only for explicit residual-data removal, while ordinary deletion retains its existing low-overhead tombstone behavior.<br/>
        /// </summary>
        /// <param name="recordOffset">The physical offset of the current live record head.<br/></param>
        /// <param name="recordID">The expected logical identity for every physical mutation copy.<br/></param>
        /// <returns>The exact payload-byte count submitted for zero overwrite across all physical copies.<br/></returns>
        /// <exception cref="InvalidDataException">Thrown before any overwrite when the mutation chain escapes the current data file or resolves to another identity.<br/></exception>
        internal ulong ZeroMutationChain(ulong recordOffset, ulong recordID)
        {
            var members = new List<(ulong Offset, uint PayloadCapacity)>();
            var visited = new HashSet<ulong>();
            var currentOffset = recordOffset;
            var fileLength = checked((ulong)fc.Length);
            while (currentOffset != 0)
            {
                if (!visited.Add(currentOffset))
                    break;
                if (currentOffset > fileLength ||
                    fileLength - currentOffset < (ulong)RecordHeader.SizeOf)
                {
                    throw new InvalidDataException(
                        $"Mutation-chain offset {currentOffset} for record {recordID} is outside the Fractal data file.");
                }

                var header = ReadRecordHeader(currentOffset);
                if (header.ID != recordID)
                {
                    throw new InvalidDataException(
                        $"Mutation-chain offset {currentOffset} belongs to record {header.ID} instead of expected record {recordID}.");
                }
                var structuralBytes = checked((uint)(RecordHeader.SizeOf + sizeof(ulong)));
                if (header.RecordSize < structuralBytes)
                {
                    throw new InvalidDataException(
                        $"Mutation-chain record at offset {currentOffset} for record {recordID} has invalid physical size {header.RecordSize}.");
                }
                if ((ulong)header.RecordSize > fileLength - currentOffset)
                {
                    throw new InvalidDataException(
                        $"Mutation-chain record at offset {currentOffset} for record {recordID} exceeds the Fractal data file.");
                }

                var payloadCapacity = header.RecordSize - structuralBytes;
                members.Add((currentOffset, payloadCapacity));
                currentOffset = header.PriorMutationOffset;
            }

            ulong zeroedBytes = 0;
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                fc.WriteZero(
                    checked(member.Offset + (ulong)RecordHeader.SizeOf),
                    member.PayloadCapacity);
                fc.WriteZero(member.Offset, sizeof(ulong));
                zeroedBytes = checked(zeroedBytes + member.PayloadCapacity);
            }

            fc.FlushToDisk();
            return zeroedBytes;
        }

        /// <summary>
        /// Writes the deleted-record sentinel into one physical record header without changing its payload or structural chain fields.<br/>
        /// This is the ordinary tombstone primitive used by deletion and defragmentation paths that do not request residual-data overwrite.<br/>
        /// </summary>
        /// <param name="recordOffset">Physical offset of the record header whose identity lane should be cleared.<br/></param>
        internal void ZeroRecordID(ulong recordOffset)
        {
            fc.WriteZero(recordOffset, sizeof(ulong));
        }


        internal long LogicalLength => fc.Length;

        internal void TruncateTo(ulong newLength)
        {
            fc.SetLength(newLength);
        }
        internal bool TryTruncateTail(ulong expectedOldEnd, ulong newLength)
        {
            return fc.TryTruncateTail(expectedOldEnd, newLength);
        }

        internal void RewriteRecord(ulong targetOffset, in RecordHeader header, ReadOnlySpan<byte> data)
        {
            // Layout: [header][data][stored-record-size]
            int totalSize = RecordHeader.SizeOf + data.Length + sizeof(ulong);
            var buffer = ArrayPool<byte>.Shared.Rent(totalSize);

            try
            {
                var span = buffer.AsSpan(0, totalSize);

                // header
                Unsafe.WriteUnaligned(
                    ref MemoryMarshal.GetReference(span),
                    header);

                // data
                data.CopyTo(span.Slice(RecordHeader.SizeOf, data.Length));

                // stored record size at tail (same as header.RecordSize)
                ulong storedSize = header.RecordSize;
                MemoryMarshal.Write(span.Slice(RecordHeader.SizeOf + data.Length, sizeof(ulong)), in storedSize);

                fc.Write(span, targetOffset);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // You will need something like this in FData (or as an internal helper on FractalStore).
        internal bool IsDeadRecord(FIndex index, ulong recordOffset, RecordHeader header)
        {
            if (header.ID == 0)
                return true;

            // Reuse the mutation/index logic we designed earlier:
            //   - lookup recordID in index
            //   - if not present => dead
            //   - if present => only head + immediate prior are live; older copies dead.
            //
            // For brevity here, assume you’ve implemented:
            //    (bool exists, ulong headOffset, ulong priorOffset) = ComputeLiveMutationOffsets(index, header.ID);
            //
            var info = ComputeLiveMutationOffsets(index, header.ID);
            if (!info.exists)
                return true;

            return recordOffset != info.headOffset && recordOffset != info.priorOffset;
        }


        /// <summary>
        /// Scans physical records and yields tombstones, unindexed records, and mutations outside each live key's retained head/prior pair.<br/>
        /// Caches index-derived live offsets only for the lifetime of this enumeration.<br/>
        /// </summary>
        /// <remarks>
        /// The caller must keep the data and index stable throughout enumeration; this iterator does not acquire a maintenance or mutation scope.<br/>
        /// Revalidate any yielded candidate before reclamation if the protecting scope has been released.<br/>
        /// Header reads use a separate call-scoped stack buffer, so live runs between yields do not accumulate stack allocations.<br/>
        /// </remarks>
        /// <param name="fIndex">Matching index used to identify retained live mutations.<br/></param>
        /// <returns>Dead-record candidates in ascending physical-offset order, with copied headers.<br/></returns>
        internal IEnumerable<(ulong recordOffset, RecordHeader header)> IterateDeadRecords(FIndex fIndex)
        {
            ulong fileEnd = (ulong)fc.Length;

            if (header.FirstRecordOffset == 0 || header.FirstRecordOffset >= fileEnd)
                yield break;

            // Cache per-recordID mutation info so we don't keep re-walking
            // the index / chains for the same key.
            var liveById = new System.Collections.Generic.Dictionary<ulong, (bool exists, ulong headOffset, ulong priorOffset)>();

            ulong offset = header.FirstRecordOffset;

            // Require enough room for at least a header.
            while (offset + (uint)RecordHeader.SizeOf <= fileEnd)
            {
                var recordHeader = ReadRecordHeader(offset);

                if (recordHeader.RecordSize == 0)
                {
                    // Can't safely advance if RecordSize is zero.
                    yield break;
                }

                bool isDead;

                if (recordHeader.ID == 0)
                {
                    // Explicit tombstone / logically deleted.
                    isDead = true;
                }
                else
                {
                    if (!liveById.TryGetValue(recordHeader.ID, out var info))
                    {
                        info = ComputeLiveMutationOffsets(fIndex, recordHeader.ID);
                        liveById[recordHeader.ID] = info;
                    }

                    if (!info.exists)
                    {
                        // Key no longer present anywhere in the index:
                        // every record for this ID is dead.
                        isDead = true;
                    }
                    else
                    {
                        // For live keys we only consider:
                        //  - current head offset
                        //  - immediate prior mutation offset
                        //
                        // Anything else with this ID is an old mutation copy
                        // beyond the A/B pair and can be reclaimed.
                        isDead = offset != info.headOffset && offset != info.priorOffset;
                    }
                }

                if (isDead)
                    yield return (offset, recordHeader);

                offset += recordHeader.RecordSize;
            }
        }

        private (bool exists, ulong headOffset, ulong priorOffset) ComputeLiveMutationOffsets(FIndex index, ulong recordID)
        {
            // 1) Locate the bucket/tier for this ID
            var (tier, bucketIndex, bucket) = index.GetBucketEntryInfo(recordID);

            if (bucket.IsUnset)
                return (false, 0ul, 0ul);

            // 2) Resolve in-tier backref if needed
            var resolved = bucket;

            if (bucket.IsChainedBackref)
            {
                ulong anchorBucketIndex = bucket.OffsetA;
                resolved = tier.GetEntryRef(anchorBucketIndex);

                if (resolved.IsChainedBackref)
                    throw new InvalidOperationException("Invalid backref chain: backref pointing to backref.");
            }

            ulong currentOffset = resolved.RecordOffset;

            // 3) Walk the collision chain until we find the node with this recordID
            var currentHeader = ReadRecordHeader(currentOffset);

            if (currentHeader.ID != recordID)
            {
                while (true)
                {
                    if (currentHeader.NextRecordOffset == 0)
                    {
                        // Hash bucket exists but this specific ID is not in its chain.
                        // Treat as non-existent key.
                        return (false, 0ul, 0ul);
                    }

                    currentOffset = currentHeader.NextRecordOffset;
                    currentHeader = ReadRecordHeader(currentOffset);

                    if (currentHeader.ID == recordID)
                        break;
                }
            }

            // currentOffset/currentHeader == current head for this key.
            ulong headOffset = currentOffset;
            ulong priorOffset = currentHeader.PriorMutationOffset;

            // 4) Sanity-check the immediate prior copy: if it points to a record with
            //    a different ID, it's likely corruption; ignore it.
            if (priorOffset != 0)
            {
                var priorHeader = ReadRecordHeader(priorOffset);
                if (priorHeader.ID != recordID)
                    priorOffset = 0;
            }

            return (true, headOffset, priorOffset);
        }

    }
}
