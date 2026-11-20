// The editor's main-thread discipline (Swift's @MainActor, Task.sleep and Task.detached), made explicit so it can be
// driven by hand in tests. No Swift file of its own.

namespace Glimmer.Editor;

/// <summary>The UI thread as the editor sees it: run something there soon, later, or start work in the background
/// whose result comes back there.</summary>
public interface IUiScheduler
{
    /// <summary>Runs <paramref name="action"/> on the UI thread after the current event (Swift: Task { @MainActor in … }).</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="delay"/>, unless the returned handle
    /// is disposed first (Swift: a Task that sleeps, then checks Task.isCancelled).</summary>
    IDisposable Schedule(TimeSpan delay, Action action);

    /// <summary>Runs <paramref name="work"/> away from the UI thread (Swift: Task.detached), then <paramref name="then"/> on
    /// the UI thread (Swift: await MainActor.run). The work catches its own errors; <paramref name="then"/> runs either way.</summary>
    void RunInBackground(Action work, Action? then = null);
}

/// <summary>The scheduler the app uses: the SynchronizationContext of the UI thread (WPF's Dispatcher), timers from the
/// thread pool, background work on the thread pool.</summary>
public sealed class SynchronizationContextScheduler : IUiScheduler
{
    private readonly SynchronizationContext context;

    /// <summary>Captures SynchronizationContext.Current when <paramref name="context"/> is null; create it on the UI thread.</summary>
    public SynchronizationContextScheduler(SynchronizationContext? context = null)
    {
        this.context = context ?? SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the scheduler on the UI thread, or pass its SynchronizationContext.");
    }

    public void Post(Action action) => context.Post(_ => action(), null);

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var handle = new Scheduled();
        handle.Timer = new Timer(_ =>
        {
            handle.Timer?.Dispose();
            if (handle.IsCancelled) return;
            context.Post(_ => { if (!handle.IsCancelled) action(); }, null);
        }, null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return handle;
    }

    public void RunInBackground(Action work, Action? then = null) =>
        _ = Task.Run(() =>
        {
            try { work(); }
            finally { if (then is not null) Post(then); }
        });

    private sealed class Scheduled : IDisposable
    {
        private int cancelled;
        public Timer? Timer;
        public bool IsCancelled => Volatile.Read(ref cancelled) != 0;

        public void Dispose()
        {
            Interlocked.Exchange(ref cancelled, 1);
            Timer?.Dispose();
        }
    }
}

/// <summary>Helpers over <see cref="IUiScheduler"/>.</summary>
public static class UiSchedulerExtensions
{
    /// <summary>Computes <paramref name="work"/> in the background and hands the result to <paramref name="then"/> on the UI
    /// thread, unless <paramref name="isCancelled"/> says otherwise by then. An exception reaches <paramref name="failed"/>.</summary>
    public static void Background<T>(this IUiScheduler scheduler, Func<T> work, Action<T> then, Func<bool>? isCancelled = null,
                                     Action<Exception>? failed = null)
    {
        T result = default!;
        Exception? error = null;
        scheduler.RunInBackground(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
        }, () =>
        {
            if (isCancelled?.Invoke() == true) return;
            if (error is not null) failed?.Invoke(error);
            else then(result);
        });
    }
}

/// <summary>A repeating timer on the UI thread (Swift's Timer.scheduledTimer(withTimeInterval:repeats: true)).</summary>
internal sealed class RepeatingTimer : IDisposable
{
    private readonly IUiScheduler scheduler;
    private readonly TimeSpan interval;
    private readonly Action tick;
    private IDisposable? next;
    private bool stopped;

    public RepeatingTimer(IUiScheduler scheduler, TimeSpan interval, Action tick)
    {
        this.scheduler = scheduler;
        this.interval = interval;
        this.tick = tick;
        Arm();
    }

    private void Arm()
    {
        if (stopped) return;
        next = scheduler.Schedule(interval, () =>
        {
            if (stopped) return;
            tick();
            Arm();
        });
    }

    public void Dispose()
    {
        stopped = true;
        next?.Dispose();
    }
}
