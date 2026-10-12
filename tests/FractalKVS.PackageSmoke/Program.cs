using FractalKVS;
using System.Security.Cryptography;

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
VerifyStorePaths();
VerifyInvalidNames();
VerifyLegacyRejection();
VerifyReleaseBoundary();

Console.WriteLine($"FractalKVS package smoke test passed on .NET {Environment.Version}; read/write, maintenance, reopen, and path safety.");

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

/// <summary>Checks canonical file placement and relative/trailing-separator directories through creation, maintenance, and a new instance's reopen.<br/></summary>
void VerifyStorePaths()
{
    VerifyMemberPlacement(storeDirectory, storeName);
    foreach (bool relative in new[] { false, true })
    {
        string absoluteDirectory = Path.Combine(storeDirectory, relative ? "relative" : "trailing");
        string directory = relative
            ? Path.GetRelativePath(Environment.CurrentDirectory, absoluteDirectory)
            : absoluteDirectory + Path.DirectorySeparatorChar;
        var store = new FractalStore(directory, "paths", configuration);
        store.Open();
        try
        {
            store.PutOne(42, payload);
            store.RebuildIndex();
            VerifyPayload(store);
        }
        finally { store.Close(); }

        var reopened = new FractalStore(directory, "paths", configuration with { CreateIfNotExists = false });
        reopened.Open();
        try { VerifyPayload(reopened); }
        finally { reopened.Close(); }
        FractalStore.TakeOwnership(directory, "paths", configuration with { CreateIfNotExists = false });
        VerifyMemberPlacement(absoluteDirectory, "paths");
    }
}

/// <summary>Requires both primary members and their coordination sidecars inside the supplied directory, never in the old non-Windows layouts.<br/></summary>
/// <param name="directory">Absolute fixture directory.<br/></param>
/// <param name="name">Fixture filename base.<br/></param>
void VerifyMemberPlacement(string directory, string name)
{
    foreach (string suffix in new[] { ".fractX", ".fractX.shm", ".fractD", ".fractD.shm" })
    {
        if (!File.Exists(Path.Combine(directory, name + suffix)))
            throw new InvalidOperationException("A canonical store member is missing: " + suffix);
        if (!OperatingSystem.IsWindows() &&
            (File.Exists(directory + "\\" + name + suffix) || File.Exists(Path.Combine(directory, "\\" + name + suffix))))
            throw new InvalidOperationException("Creation leaked a member into the legacy backslash layout.");
    }
}

/// <summary>Checks invalid names before creation and ownership, with no new directories or files on either public entry point.<br/></summary>
void VerifyInvalidNames()
{
    string[] invalidNames = { "", " ", ".", "..", "../escape", "sub/records", "sub\\records", "\\records", "/records", "C:\\records", "bad\0name" };
    for (int i = 0; i < invalidNames.Length; i++)
    {
        string directory = Path.Combine(storeDirectory, "invalid-" + i);
        foreach (bool ownership in new[] { false, true })
        {
            try
            {
                if (ownership) FractalStore.TakeOwnership(directory, invalidNames[i], configuration);
                else new FractalStore(directory, invalidNames[i], configuration).Close();
                throw new InvalidOperationException("An invalid store name was accepted.");
            }
            catch (ArgumentException error) when (error.ParamName == "name") { }
            if (Directory.Exists(directory)) throw new InvalidOperationException("Invalid-name rejection created a directory.");
        }
    }
}

/// <summary>
/// On non-Windows hosts, tests each orphaned legacy member, both historical separator forms, differing caller spellings, and old/new ambiguity.<br/>
/// Creation and ownership must reject before any directory/member mutation; complete fixture inventories and SHA-256 hashes must remain unchanged.<br/>
/// Fixtures are isolated and retained for inspection; these tests perform no automatic migration or deletion.<br/>
/// </summary>
void VerifyLegacyRejection()
{
    if (OperatingSystem.IsWindows()) return;
    string[] suffixes = { ".fractX", ".fractX.shm", ".fractD", ".fractD.shm" };
    foreach (bool child in new[] { false, true })
    foreach (bool trailing in new[] { false, true })
    foreach (bool ambiguous in new[] { false, true })
    foreach (string suffix in suffixes)
    {
        string fixture = Path.Combine(storeDirectory, "legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string directory = Path.Combine(fixture, "store");
        if (child || ambiguous) Directory.CreateDirectory(directory);
        string legacyBase = child ? Path.Combine(directory, "\\records") : directory + "\\records";
        File.WriteAllBytes(legacyBase + suffix, new byte[] { 31, 47, 59 });
        if (ambiguous) File.WriteAllBytes(Path.Combine(directory, "records" + suffix), new byte[] { 61, 71, 83 });
        string callerDirectory = trailing ? directory + Path.DirectorySeparatorChar : directory;
        string before = FixtureFingerprint(fixture);
        foreach (bool ownership in new[] { false, true })
        {
            try
            {
                if (ownership) FractalStore.TakeOwnership(callerDirectory, "records", configuration);
                else new FractalStore(callerDirectory, "records", configuration).Close();
                throw new InvalidOperationException("A legacy or ambiguous store layout was accepted.");
            }
            catch (InvalidDataException error) when (error.Message.Contains("Explicit offline migration", StringComparison.Ordinal)) { }
            if (FixtureFingerprint(fixture) != before)
                throw new InvalidOperationException("Legacy rejection changed the fixture's directories or file bytes.");
        }
    }
    Console.WriteLine("PASS 32 legacy layout fixtures; creation and ownership reject without directory/file changes.");
}

/// <summary>Fingerprints all fixture directories and each file's full contents to detect both new members and mutations of old members.<br/></summary>
/// <param name="fixture">Owned isolated fixture root, never a user store.<br/></param>
/// <returns>A deterministic listing of relative directories and file hashes.<br/></returns>
string FixtureFingerprint(string fixture)
{
    return string.Join("\n", Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories)
        .Select(path => "D:" + Path.GetRelativePath(fixture, path))
        .Concat(Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Select(path => "F:" + Path.GetRelativePath(fixture, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(entry => entry, StringComparer.Ordinal));
}
