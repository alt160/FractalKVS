using FractalKVS;

var storeDirectory = Path.Combine(AppContext.BaseDirectory, "package-smoke", Guid.NewGuid().ToString("N"));
var storeName = "records";
var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
var configuration = new FractalStoreConfiguration
{
    WriteBehavior = FractalStoreWriteBehavior.Immediate
};

WriteAndVerify();
ReopenAndVerify();
RebuildAndVerify();
ReopenAndVerify();
VerifyReleaseBoundary();

Console.WriteLine($"FractalKVS package smoke test passed on .NET {Environment.Version}; read/write, maintenance, and reopen.");

void WriteAndVerify()
{
    var store = new FractalStore(storeDirectory, storeName, configuration);
    store.Open();
    try
    {
        store.PutOne(42UL, payload);
        VerifyPayload(store);
    }
    finally
    {
        store.Close();
    }
}

void ReopenAndVerify()
{
    var store = new FractalStore(storeDirectory, storeName, configuration);
    store.Open();
    try
    {
        VerifyPayload(store);
    }
    finally
    {
        store.Close();
    }
}

void VerifyPayload(FractalStore store)
{
    if (!store.TryGetOne(42UL, out var record))
        throw new InvalidOperationException("The package consumer could not retrieve the written record.");

    using (record)
    {
        if (!record.Data.Span.SequenceEqual(payload))
            throw new InvalidOperationException("The package consumer read a payload different from the written payload.");
    }
}

/// <summary>Exercises healthy-index replacement using only the package's public API and an isolated store.<br/></summary>
void RebuildAndVerify()
{
    var store = new FractalStore(storeDirectory, storeName, configuration);
    store.Open();
    try
    {
        Guid identity = store.UUID;
        for (ulong id = 100; id < 300; id++) store.PutOne(id, BitConverter.GetBytes(id));
        store.PutOne(150, new byte[] { 9, 8, 7 });
        if (!store.DeleteOne(151)) throw new InvalidOperationException("The maintenance fixture could not delete a record.");
        store.RebuildIndex();
        if (store.UUID != identity) throw new InvalidOperationException("Maintenance changed the store identity.");
        VerifyPayload(store);
        for (ulong id = 100; id < 300; id++)
        {
            if (id == 151)
            {
                if (store.TryGetOne(id, out var deleted))
                {
                    deleted.Dispose();
                    throw new InvalidOperationException("Maintenance resurrected a deleted record.");
                }
                continue;
            }
            if (!store.TryGetOne(id, out var record)) throw new InvalidOperationException("Maintenance lost a current record.");
            using (record)
            {
                byte[] expected = id == 150 ? new byte[] { 9, 8, 7 } : BitConverter.GetBytes(id);
                if (!record.Data.Span.SequenceEqual(expected)) throw new InvalidOperationException("Maintenance selected an obsolete payload.");
            }
        }
        store.PutOne(350, payload);
    }
    finally { store.Close(); }
}

/// <summary>Rejects accidental harness inclusion and confirms the required component-only snapshot API is present but not public.<br/></summary>
void VerifyReleaseBoundary()
{
    var assembly = typeof(FractalStore).Assembly;
    if (assembly.GetTypes().Any(type => type.Name is "Program" or "SmokeRecord" || type.Name.Contains("TestHarness", StringComparison.Ordinal)))
        throw new InvalidOperationException("A harness type leaked into the runtime package.");
    if (assembly.GetReferencedAssemblies().Any(reference => reference.Name?.Contains("TestHarness", StringComparison.Ordinal) == true))
        throw new InvalidOperationException("The runtime package references a harness assembly.");
    var validator = assembly.GetType("FractalKVS.SnapshotValidator");
    if (validator is null || validator.IsPublic)
        throw new InvalidOperationException("The required internal snapshot validator is absent or public.");
}
