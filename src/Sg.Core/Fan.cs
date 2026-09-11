using System.Runtime.ExceptionServices;

namespace Sg.Core;

/// <summary>
/// The same read over several worktrees, working copies or repositories at once. Each one spends its
/// time waiting on a child process, on the disk or on the server, so running them one after another
/// only added the waits up.
/// </summary>
public static class Fan
{
    /// <summary>How many run at once. Half the cores, because none of this work is CPU bound.</summary>
    public static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Maps every item and hands the results back in the order the items came in, so what is built
    /// from them does not move about between runs. A failure comes back as itself, not wrapped in an
    /// AggregateException: its message is what the user reads.
    /// </summary>
    public static TResult[] Map<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, TResult> map)
    {
        var results = new TResult[items.Count];
        if (items.Count == 0) return results;
        if (items.Count == 1) { results[0] = map(items[0]); return results; }

        Exception? failure = null;
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = Workers }, i =>
        {
            try { results[i] = map(items[i]); }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        });
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return results;
    }

    /// <summary>
    /// Two reads of different kinds at once. A page that needs both before it can draw used to wait for
    /// the slow one and then start the quick one; this way it waits once. Failures come back as
    /// themselves, the same way Map hands them over.
    /// </summary>
    public static (TA First, TB Second) Two<TA, TB>(Func<TA> a, Func<TB> b)
    {
        TA ra = default!;
        TB rb = default!;
        Exception? failure = null;
        void Catch(Action work)
        {
            try { work(); }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
        }
        Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = 2 },
            () => Catch(() => ra = a()),
            () => Catch(() => rb = b()));
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return (ra, rb);
    }
}
