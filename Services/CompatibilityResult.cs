using MovieConcatenater.Models;

namespace MovieConcatenater.Services;

public enum ConcatMode
{
    Copy,
    Reencode,
}

public sealed class CompatibilityResult
{
    public required IReadOnlyList<VideoFileItem> Items { get; init; }
    public required VideoFileItem Reference { get; init; }
    public required IReadOnlyList<VideoFileItem> Mismatches { get; init; }

    public bool IsCompatible => Mismatches.Count == 0;
}
