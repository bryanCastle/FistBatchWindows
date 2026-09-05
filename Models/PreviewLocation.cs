namespace PhotoOrganizer.Models;

/// <summary>
/// A self-contained JPEG image stored inside a picture file, described by its byte range.
/// RAW files embed one or more of these so cameras can render fast playback previews, and
/// FirstBatch displays them instead of decoding sensor data.
/// </summary>
internal readonly record struct PreviewLocation(long Offset, int Length, int PixelWidth);
