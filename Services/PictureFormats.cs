namespace PhotoOrganizer.Services;

internal static class PictureFormats
{
    /// <summary>
    /// Formats Windows can decode on its own through the Windows Imaging Component.
    /// </summary>
    private static readonly HashSet<string> StandardExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".bmp", ".tif", ".tiff",
    };

    /// <summary>
    /// Camera RAW formats. Windows ships no RAW decoder, so these are displayed from the
    /// JPEG preview the camera embedded in the file rather than by decoding sensor data.
    /// Still-image formats only: RAW video containers such as BRAW and R3D are excluded.
    /// </summary>
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".ari", ".arw", ".bay", ".cap", ".cr2", ".cr3", ".crw", ".dcr", ".dcs",
        ".dng", ".drf", ".eip", ".erf", ".fff", ".gpr", ".iiq", ".k25", ".kdc", ".mdc",
        ".mef", ".mos", ".mrw", ".nef", ".nrw", ".orf", ".pef", ".ptx", ".pxn", ".raf",
        ".raw", ".rw2", ".rwl", ".rwz", ".sr2", ".srf", ".srw", ".x3f",
    };

    public static bool IsSupported(string extension)
        => StandardExtensions.Contains(extension) || RawExtensions.Contains(extension);

    public static bool IsRaw(string extension) => RawExtensions.Contains(extension);
}
