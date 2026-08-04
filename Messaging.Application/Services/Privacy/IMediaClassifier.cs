namespace Messaging.Application.Services.Privacy;

/// <summary>What a classifier concluded about one piece of media.</summary>
public enum MediaClassification
{
    /// <summary>Nothing is known about it. The answer from the no-op classifier, and the answer a
    /// real one must give when its backend is unavailable rather than guessing.
    ///
    /// <para>Treated as <i>allowed</i>. That is the one place in this spec's enforcement where the
    /// permissive answer is correct: with no classifier wired in, every attachment is Unknown, and
    /// refusing them all would take DM attachments away from the entire product in the name of a
    /// filter that is not actually running.</para></summary>
    Unknown,

    Safe,

    Explicit,
}

/// <summary>One attachment, as much as a classifier needs to identify it.</summary>
public sealed record MediaClassificationRequest(
    string AttachmentId,
    string FileName,
    string? ContentType);

/// <summary>
/// T2-20's seam. Scan/classification integration is explicitly out of scope for the privacy spec;
/// what is in scope is that the control exists, is honoured, and starts working the moment a real
/// implementation is registered in place of <see cref="NoOpMediaClassifier"/>.
/// </summary>
public interface IMediaClassifier
{
    Task<IReadOnlyDictionary<string, MediaClassification>> ClassifyAsync(
        IReadOnlyCollection<MediaClassificationRequest> media, CancellationToken ct = default);

    /// <summary>Whether this implementation can actually decide anything. False lets the
    /// enforcement point skip the recipient-policy lookups entirely, so an unconfigured deployment
    /// pays nothing for a filter that cannot fire.</summary>
    bool IsOperational { get; }
}

/// <summary>The default. Classifies nothing and says so.</summary>
public sealed class NoOpMediaClassifier : IMediaClassifier
{
    public bool IsOperational => false;

    public Task<IReadOnlyDictionary<string, MediaClassification>> ClassifyAsync(
        IReadOnlyCollection<MediaClassificationRequest> media, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, MediaClassification>>(
            media.ToDictionary(m => m.AttachmentId, _ => MediaClassification.Unknown, StringComparer.Ordinal));
}
