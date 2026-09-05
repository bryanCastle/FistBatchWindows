namespace PhotoOrganizer.Models;

internal sealed class PhotoItem
{
    public required string Path { get; init; }
    public required string FileName { get; init; }

    /// <summary>
    /// Capture time read from EXIF, or null when the file carries no usable date. Undated
    /// pictures are grouped separately rather than being given a misleading file timestamp.
    /// </summary>
    public required DateTimeOffset? TakenAt { get; init; }

    public required bool IsRawFormat { get; init; }

    /// <summary>
    /// EXIF orientation, where 1 means upright. Cameras store portrait shots as landscape
    /// pixels plus a rotation flag, and XAML does not apply that flag on its own.
    /// </summary>
    public required int Orientation { get; init; }

    /// <summary>
    /// Embedded JPEG previews discovered while reading the header. Empty for standard formats,
    /// and also empty for RAW containers whose previews must be located by a deeper scan.
    /// </summary>
    public required IReadOnlyList<PreviewLocation> Previews { get; init; }

    public bool HasDate => TakenAt.HasValue;
}
