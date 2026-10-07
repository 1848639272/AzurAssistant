namespace AzurAssistant.Contracts;

/// <summary>One owner; calls and disposal are serialized by the preview service.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>A TimeoutException means no frame arrived in the bounded wait; the same source supports another read.
    /// Cancellation and invalid-window errors are not timeouts. Recovery preserves increasing frame IDs and timestamps.</summary>
    Task<FrameSnapshot> ReadAsync(CancellationToken cancellationToken);
}
