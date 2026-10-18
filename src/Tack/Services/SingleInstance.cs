namespace Tack.Services;

/// <summary>
/// Ensures one Tack per user session. A second launch just signals the first one to open its panel.
/// The wait is a registered OS wait handle: no thread is spent polling.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle signal)
    {
        _mutex = mutex;
        _signal = signal;
    }

    /// <summary>Returns null (after signalling the running instance) if Tack is already running.</summary>
    public static SingleInstance? TryAcquire()
    {
        // TACK_INSTANCE (development only) lets a test copy run beside your installed Tack; give it
        // its own data with a TackData folder next to its exe (portable mode).
        string id = $"Tack.{Environment.UserName}{Environment.GetEnvironmentVariable("TACK_INSTANCE")}";
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.Instance", out bool created);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Show");
        if (created) return new SingleInstance(mutex, signal);

        AllowSetForegroundWindow(-1); // let the running instance come to the front
        signal.Set();
        signal.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Invokes the callback (on a thread-pool thread) whenever another launch asks us to show.</summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _registration?.Unregister(null);
        _signal.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* not owned */ }
        _mutex.Dispose();
    }
}
