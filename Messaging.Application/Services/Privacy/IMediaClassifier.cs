namespace Messaging.Application.Services.Privacy;

/// <summary>What a classifier concluded about one piece of media.</summary>
public enum MediaClassification
{
    /// <summary>Nothing is known about it.</summary>
    Unknown,

    Safe,

    Explicit,
}

/// <summary>One attachment, as much as a classifier needs to identify it.</summary>
public sealed record MediaClassificationRequest(
    string AttachmentId,
    string FileName,
    string? ContentType);

/// <summary>T2-20's seam.</summary>
public interface IMediaClassifier
{
    Task<IReadOnlyDictionary<string, MediaClassification>> ClassifyAsync(
        IReadOnlyCollection<MediaClassificationRequest> media, CancellationToken ct = default);

    /// <summary>Whether this implementation can actually decide anything.</summary>
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
