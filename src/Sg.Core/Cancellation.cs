namespace Sg.Core;

/// <summary>
/// The cancel signal for the operation running on this async flow. Proc reads it and ends the child
/// process, so a first snapshot that hashes 16 GB can be stopped without killing the app.
/// One bridge operation runs at a time per root, so one ambient token is enough.
/// </summary>
public static class Cancellation
{
    static readonly AsyncLocal<CancellationToken> Slot = new();

    public static CancellationToken Current
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }

    /// <summary>Sets the token for the work inside the using, then puts back what was there.</summary>
    public static IDisposable Use(CancellationToken token)
    {
        var previous = Slot.Value;
        Slot.Value = token;
        return new Restore(previous);
    }

    public static void ThrowIfRequested() => Current.ThrowIfCancellationRequested();

    sealed class Restore(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}

/// <summary>
/// Thrown when the user stops a running operation. It derives from OperationCanceledException so a
/// caller that handles cancellation the standard way catches it, whether the child had started or not.
/// </summary>
public sealed class SgCancelledException() : OperationCanceledException("cancelled");
