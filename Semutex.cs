using System.Diagnostics;

namespace FractalKVS;

/// <summary>
/// Named cross-process exclusion with instance-owned, cross-thread release.<br/>
/// One shared background thread owns the native mutexes on behalf of callers, including async callers.<br/>
/// Only native abandoned ownership permits recovery; timeouts never revoke a living owner's lease.<br/>
/// Names share the native synchronization namespace: a running legacy semaphore with the same name
/// causes construction to fail rather than allowing two independent lock domains.<br/>
/// </summary>
internal sealed class Semutex : IDisposable
{
    private readonly string _name;
    private readonly Gate _gate;
    private bool _disposed;
    private bool _held;
    private Request? _pending;
    private Task<bool>? _disposeTask;
    private bool _releasing;
    private int _detectAbandonment;

    /// <summary>
    /// Opens one named native gate without acquiring it.<br/>
    /// Windows defaults to the global namespace; Unix preserves the prior prefix-stripping convention.<br/>
    /// Heartbeat arguments remain source-compatible but are no longer used: elapsed time is not evidence of abandonment.<br/>
    /// Native access/name/platform failures are propagated; there is no process-local fallback.<br/>
    /// </summary>
    public Semutex(string name, bool useWindowsLocal = false, bool enableAbandonDetection = false,
        TimeSpan? heartbeatInterval = null, TimeSpan? staleAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!OperatingSystem.IsWindows() &&
            ((!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) ||
             !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported || Type.GetType("Mono.Runtime") is not null))
            throw new PlatformNotSupportedException("Semutex requires Windows or the Linux/macOS CoreCLR runtime with cross-process named mutex support.");
        _name = NormalizeName(name, useWindowsLocal);
        _detectAbandonment = enableAbandonDetection ? 1 : 0;
        _gate = Broker.Open(_name);
    }

    /// <summary>
    /// Controls notification after native abandoned ownership has been safely acquired.<br/>
    /// When enabled, acquisition throws AbandonedSemutexException with AlreadyAcquired=true;
    /// the caller must still release/dispose the acquired gate.<br/>
    /// When disabled, native recovery succeeds normally. This never disables native exclusion or enables timed stealing.<br/>
    /// Notification is not a durable transaction log and does not certify the protected data's consistency.<br/>
    /// </summary>
    public bool AbandonDetectionEnabled
    {
        get => Volatile.Read(ref _detectAbandonment) != 0;
        set => Volatile.Write(ref _detectAbandonment, value ? 1 : 0);
    }

    /// <summary>
    /// Acquires this instance or returns false when the timeout expires.<br/>
    /// A zero timeout performs one ownership attempt; -1 waits indefinitely.<br/>
    /// Concurrent or recursive acquisitions on one instance are rejected; use separate instances for independent contenders.<br/>
    /// </summary>
    public bool WaitOne(int millisecondsTimeout = Timeout.Infinite)
        => Broker.Acquire(this, millisecondsTimeout, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Acquires without occupying a thread-pool worker while contended.<br/>
    /// Cancellation is observed while queued and at the ownership-transfer boundary;
    /// after a successful transfer, the caller owns the gate and must release it.<br/>
    /// The timeout includes dispatch time; zero still receives one immediate native attempt.<br/>
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        double milliseconds = timeout.TotalMilliseconds;
        if (milliseconds < -1 || milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return Broker.Acquire(this, (int)milliseconds, ct);
    }

    /// <summary>
    /// Releases an acquired instance from any caller thread after its protected work has stopped.<br/>
    /// Returns only after the owning broker thread has released native ownership.<br/>
    /// An unheld instance or a concurrent second release is rejected.<br/>
    /// </summary>
    public void ReleaseMutex() => Broker.Release(this).GetAwaiter().GetResult();

    /// <summary>
    /// Cancels this instance's pending acquisition, releases any acquired ownership and closes its reference.<br/>
    /// Concurrent disposal is idempotent and waits for the same completed cleanup.<br/>
    /// Callers must stop their protected work before disposal; disposal is an explicit relinquishment, not a heartbeat inference.<br/>
    /// </summary>
    public void Dispose() => Broker.Close(this).GetAwaiter().GetResult();

    /// <summary>
    /// Preserves named-gate identity while normalizing recognized Windows namespace prefixes.<br/>
    /// Unix names omit the Windows-only prefix, matching the previous non-Windows naming intent.<br/>
    /// </summary>
    private static string NormalizeName(string name, bool local)
    {
        bool globalPrefix = name.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase);
        bool localPrefix = name.StartsWith("Local\\", StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return globalPrefix || localPrefix ? name[(globalPrefix ? 7 : 6)..] : name;
        if (globalPrefix) return "Global\\" + name[7..];
        if (localPrefix) return "Local\\" + name[6..];
        return (local ? "Local\\" : "Global\\") + name;
    }

    /// <summary>One process-local identity for a native named mutex; Holder prevents broker-thread recursion across instances.<br/></summary>
    private sealed class Gate
    {
        internal readonly string Name;
        internal readonly Mutex Mutex;
        internal int References;
        internal Semutex? Holder;

        /// <summary>Creates an unowned native mutex; any legacy named-semaphore collision fails closed.<br/></summary>
        internal Gate(string name)
        {
            Name = name;
            Mutex = new Mutex(false, name);
        }
    }

    private enum Operation { Acquire, Release, Close }

    /// <summary>One independently completed request; asynchronous continuations never execute on the native ownership thread.<br/></summary>
    private sealed class Request
    {
        internal readonly Semutex Owner;
        internal readonly Operation Operation;
        internal readonly int Timeout;
        internal readonly CancellationToken Token;
        internal readonly long Started = Stopwatch.GetTimestamp();
        internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation;
        internal bool Attempted;

        /// <summary>Captures one operation's timeout and cancellation independently from later instance reuse.<br/></summary>
        internal Request(Semutex owner, Operation operation, int timeout = -1, CancellationToken token = default)
        { Owner = owner; Operation = operation; Timeout = timeout; Token = token; }

        /// <summary>Returns the remaining monotonic wait budget; -1 means unbounded and zero means expired.<br/></summary>
        internal int Remaining()
            => Timeout < 0 ? -1 : (int)Math.Max(0, Timeout - Stopwatch.GetElapsedTime(Started).TotalMilliseconds);
    }

    /// <summary>
    /// Single native ownership thread shared by every Fractal/Abraxas wrapper in this loaded assembly.<br/>
    /// All bookkeeping uses Sync; only this thread acquires/releases/disposes native mutexes.<br/>
    /// It sleeps on native handles, not heartbeats, and remains asleep when no work is pending.<br/>
    /// </summary>
    private static class Broker
    {
        private static readonly object Sync = new();
        private static readonly AutoResetEvent Wake = new(false);
        private static readonly Dictionary<string, Gate> Gates = new(StringComparer.Ordinal);
        private static readonly Queue<Request> Commands = new();
        private static readonly List<Request> Pending = new();
        private static readonly WaitHandle[]?[] WaitArrays = new WaitHandle[65][];
        private static readonly Gate?[] WaitGates = new Gate?[64];
        // Diagnostic override exercises Unix's single-wait strategy on a Windows test host.
        private static readonly bool MultiWait = OperatingSystem.IsWindows() &&
            !(AppContext.TryGetSwitch("FractalKVS.Semutex.ForceSingleWait", out bool singleWait) && singleWait);
        private static int _rotation;

        /// <summary>Starts one background owner; an unhandled invariant failure terminates the process rather than silently abandoning live work.<br/></summary>
        static Broker() => new Thread(Run) { IsBackground = true, Name = "Semutex native owner" }.Start();

        /// <summary>Shares one native handle for the exact normalized identity and retains it until all wrappers close.<br/></summary>
        internal static Gate Open(string name)
        {
            lock (Sync)
            {
                if (!Gates.TryGetValue(name, out Gate? gate))
                {
                    gate = new Gate(name);
                    Gates.Add(name, gate);
                }
                gate.References++;
                return gate;
            }
        }

        /// <summary>Reserves one acquisition per instance and arranges cancellation to wake, never release, the ownership thread.<br/></summary>
        internal static Task<bool> Acquire(Semutex owner, int timeout, CancellationToken token)
        {
            if (timeout < -1) throw new ArgumentOutOfRangeException(nameof(timeout));
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(owner._disposed, owner);
                if (owner._held || owner._pending is not null || owner._releasing)
                    throw new SynchronizationLockException("This Semutex instance already holds or is waiting for ownership.");
                if (token.IsCancellationRequested) return Task.FromCanceled<bool>(token);
                var request = new Request(owner, Operation.Acquire, timeout, token);
                if (token.CanBeCanceled)
                    request.Cancellation = token.UnsafeRegister(static _ => Wake.Set(), null);
                owner._pending = request;
                Pending.Add(request);
                Wake.Set();
                return request.Completion.Task;
            }
        }

        /// <summary>Queues release without transferring the thread-affine native handle to a caller.<br/></summary>
        internal static Task<bool> Release(Semutex owner)
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(owner._disposed, owner);
                if (!owner._held || owner._releasing)
                    throw new SynchronizationLockException("Not currently held by this instance, or release already pending.");
                owner._releasing = true;
                var request = new Request(owner, Operation.Release);
                Commands.Enqueue(request);
                Wake.Set();
                return request.Completion.Task;
            }
        }

        /// <summary>Publishes disposal before enqueueing cleanup so no queued acquisition can be granted afterward.<br/></summary>
        internal static Task<bool> Close(Semutex owner)
        {
            lock (Sync)
            {
                if (owner._disposeTask is not null) return owner._disposeTask;
                owner._disposed = true;
                var request = new Request(owner, Operation.Close);
                owner._disposeTask = request.Completion.Task;
                Commands.Enqueue(request);
                Wake.Set();
                return owner._disposeTask;
            }
        }

        /// <summary>Services cleanup before waiters and uses WaitAny for prompt native release/cancellation notification.<br/></summary>
        private static void Run()
        {
            while (true)
            {
                WaitHandle[] handles;
                int timeout;
                lock (Sync)
                {
                    ProcessCommands();
                    ProcessPending();
                    handles = BuildWaitSet(out timeout);
                }
                int signaled;
                bool abandoned = false;
                try { signaled = WaitHandle.WaitAny(handles, timeout); }
                catch (AbandonedMutexException ex) { signaled = ex.MutexIndex; abandoned = true; }
                if (signaled == WaitHandle.WaitTimeout || signaled == 0) continue;
                if (signaled < 1 || signaled >= handles.Length)
                    throw new InvalidOperationException("Native mutex wait returned an invalid ownership index.");
                lock (Sync)
                {
                    Gate gate = WaitGates[signaled]!;
                    Request? winner = null;
                    foreach (Request request in Pending)
                    {
                        if (ReferenceEquals(request.Owner._gate, gate) && !request.Owner._disposed &&
                            !request.Token.IsCancellationRequested && request.Remaining() != 0)
                        { winner = request; break; }
                    }
                    if (winner is null) gate.Mutex.ReleaseMutex();
                    else
                    {
                        Pending.Remove(winner);
                        Grant(winner, abandoned);
                    }
                }
            }
        }

        /// <summary>Releases owned handles and closes only unreferenced identities; native ownership failures remain process-fatal invariants.<br/></summary>
        private static void ProcessCommands()
        {
            while (Commands.TryDequeue(out Request? request))
            {
                Semutex owner = request.Owner;
                Gate gate = owner._gate;
                if (owner._held)
                {
                    gate.Mutex.ReleaseMutex();
                    gate.Holder = null;
                    owner._held = false;
                }
                owner._releasing = false;
                if (request.Operation == Operation.Close)
                {
                    if (owner._pending is Request pending)
                    {
                        Pending.Remove(pending);
                        Finish(pending, new ObjectDisposedException(nameof(Semutex)));
                    }
                    if (--gate.References == 0)
                    {
                        Gates.Remove(gate.Name);
                        gate.Mutex.Dispose();
                    }
                }
                request.Completion.SetResult(true);
            }
        }

        /// <summary>Attempts pending gates without recursion, then completes canceled/expired requests without leaving acquired ownership behind.<br/></summary>
        private static void ProcessPending()
        {
            for (int i = 0; i < Pending.Count;)
            {
                Request request = Pending[i];
                Semutex owner = request.Owner;
                Exception? error = null;
                bool acquired = false, abandoned = false;
                if (owner._disposed) error = new ObjectDisposedException(nameof(Semutex));
                else if (!request.Token.IsCancellationRequested &&
                    ((request.Timeout == 0 && !request.Attempted) || request.Remaining() != 0) && owner._gate.Holder is null)
                {
                    try { acquired = owner._gate.Mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; abandoned = true; }
                }
                request.Attempted = true;
                if (acquired)
                {
                    Pending.RemoveAt(i);
                    if (request.Token.IsCancellationRequested)
                    {
                        owner._gate.Mutex.ReleaseMutex();
                        Finish(request, null);
                    }
                    else Grant(request, abandoned);
                }
                else if (error is not null || request.Token.IsCancellationRequested || request.Remaining() == 0)
                {
                    Pending.RemoveAt(i);
                    Finish(request, error);
                }
                else i++;
            }
        }

        /// <summary>Records the unique wrapper owner before publishing success or an acquired-abandonment notification.<br/></summary>
        private static void Grant(Request request, bool abandoned)
        {
            Semutex owner = request.Owner;
            owner._held = true;
            owner._gate.Holder = owner;
            owner._pending = null;
            request.Cancellation.Dispose();
            if (abandoned && owner.AbandonDetectionEnabled)
                request.Completion.SetException(new AbandonedSemutexException(owner._name, true));
            else request.Completion.SetResult(true);
        }

        /// <summary>Completes a non-owning request; no heartbeat state or native permit is manufactured during failure.<br/></summary>
        private static void Finish(Request request, Exception? error)
        {
            request.Owner._pending = null;
            request.Cancellation.Dispose();
            if (error is not null) request.Completion.SetException(error);
            else if (request.Token.IsCancellationRequested) request.Completion.SetCanceled(request.Token);
            else request.Completion.SetResult(false);
        }

        /// <summary>
        /// Builds cached native wait arrays with the wake event first and at most 63 distinct unowned gates.<br/>
        /// Larger Windows contention sets rotate with a bounded 10 ms wait; ordinary sets wait directly on native handles.<br/>
        /// Unix cannot multi-wait on named mutexes: only the local wake event is waited, with 1 ms checks while remote gates contend.<br/>
        /// Already-held local identities never enter WaitAny, preventing recursive mutex acquisition by the shared thread.<br/>
        /// </summary>
        private static WaitHandle[] BuildWaitSet(out int timeout)
        {
            timeout = Timeout.Infinite;
            int count = 1;
            bool overflow = false;
            for (int n = 0; n < Pending.Count; n++)
            {
                Request request = Pending[(_rotation + n) % Pending.Count];
                int remaining = request.Remaining();
                if (remaining >= 0) timeout = timeout < 0 ? remaining : Math.Min(timeout, remaining);
                Gate gate = request.Owner._gate;
                if (gate.Holder is not null) continue;
                if (!MultiWait)
                {
                    timeout = timeout < 0 ? 1 : Math.Min(timeout, 1);
                    continue;
                }
                bool seen = false;
                for (int i = 1; i < count; i++) if (ReferenceEquals(WaitGates[i], gate)) { seen = true; break; }
                if (seen) continue;
                if (count == 64) { overflow = true; continue; }
                WaitGates[count++] = gate;
            }
            if (Pending.Count != 0) _rotation = (_rotation + 1) % Pending.Count;
            else _rotation = 0;
            if (overflow) timeout = timeout < 0 ? 10 : Math.Min(timeout, 10);
            WaitHandle[] handles = WaitArrays[count] ??= new WaitHandle[count];
            handles[0] = Wake;
            for (int i = 1; i < count; i++) handles[i] = WaitGates[i]!.Mutex;
            return handles;
        }
    }
}
