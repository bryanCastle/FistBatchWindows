using Microsoft.VisualBasic.FileIO;
using PhotoOrganizer.Models;

namespace PhotoOrganizer.Services;

internal static class PhotoLibraryService
{
    public const string LikedFolderName = "Liked";
    public const string DeletedFolderName = "Deleted";

    private const string AlbumListFileName = "PhotoOrganizer.albums";

    private static readonly HashSet<string> ReservedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        LikedFolderName,
        DeletedFolderName,
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "LPT1",
    };

    private static IReadOnlyList<string> FindPictures(string folderPath)
    {
        // Pictures the app has already sorted are skipped, or a delete would simply reappear in
        // the timeline the next time the folder is opened. Named folders created from the timeline
        // are skipped for the same reason.
        var sortedRoots = GetSkippedFolderNames(folderPath)
            .Select(name => BuildFolderPrefix(folderPath, name))
            .ToArray();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = System.IO.FileAttributes.ReparsePoint,
        };

        return Directory
            .EnumerateFiles(folderPath, "*", options)
            .Where(path => PictureFormats.IsSupported(Path.GetExtension(path)))
            .Where(path => !IsInside(sortedRoots, path))
            .ToList();
    }

    private static string BuildFolderPrefix(string folderPath, string folderName)
        => Path.GetFullPath(Path.Combine(folderPath, folderName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsInside(IReadOnlyList<string> folderPrefixes, string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var prefix in folderPrefixes)
        {
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads headers in parallel because the work is dominated by small independent reads.
    /// </summary>
    public static IReadOnlyList<PhotoItem> LoadLibrary(string folderPath, CancellationToken cancellationToken)
    {
        var paths = FindPictures(folderPath);
        var photos = new PhotoItem[paths.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken,
        };

        Parallel.For(0, paths.Count, options, index => photos[index] = CreatePhotoItem(paths[index]));
        return photos;
    }

    private static PhotoItem CreatePhotoItem(string path)
    {
        var isRawFormat = PictureFormats.IsRaw(Path.GetExtension(path));
        var metadata = ImageMetadataReader.Read(path);

        return new PhotoItem
        {
            Path = path,
            FileName = Path.GetFileName(path),
            TakenAt = metadata.DateTaken,
            IsRawFormat = isRawFormat,
            Orientation = metadata.Orientation,
            Previews = metadata.Previews,
        };
    }

    public static string MoveToLiked(string sourcePath, string selectedFolder)
        => MoveToNamedFolder(sourcePath, selectedFolder, LikedFolderName);

    /// <summary>
    /// Moves a picture into a folder the user named from the timeline. The folder sits beside the
    /// library, like Liked, so the move is a rename on the same volume.
    /// </summary>
    public static string MoveToNamedFolder(string sourcePath, string selectedFolder, string folderName)
    {
        var destinationFolder = Path.Combine(selectedFolder, folderName);
        Directory.CreateDirectory(destinationFolder);

        var destination = GetUniqueDestination(destinationFolder, Path.GetFileName(sourcePath));
        File.Move(sourcePath, destination);
        RememberAlbum(selectedFolder, folderName);
        return destination;
    }

    /// <summary>
    /// A folder name that can sit beside the library. Liked and Deleted are reserved because they
    /// already mean something, and Windows forbids a handful of device names.
    /// </summary>
    public static string? ValidateFolderName(string? proposed)
    {
        var name = proposed?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return "Enter a folder name.";
        }

        if (name.Length > 80)
        {
            return "Use a shorter folder name.";
        }

        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' '))
        {
            return "That name is not allowed on Windows.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "Remove characters that Windows does not allow in folder names.";
        }

        if (ReservedFolderNames.Contains(name))
        {
            return $"\"{name}\" is already used by FirstBatch.";
        }

        return null;
    }

    /// <summary>
    /// Holds a deleted picture in a folder beside the library rather than the Recycle Bin. Removable
    /// and network drives usually have no Recycle Bin at all, so a delete there is permanent and can
    /// never be undone. Staying on the same volume also makes every delete instant, because the
    /// file is renamed rather than copied however large it is.
    /// </summary>
    public static string MoveToDeleted(string sourcePath, string selectedFolder)
    {
        var destination = BuildDeletedDestination(sourcePath, selectedFolder);
        var destinationFolder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationFolder))
        {
            Directory.CreateDirectory(destinationFolder);
        }

        File.Move(sourcePath, destination);
        return destination;
    }

    /// <summary>
    /// Recreates the sub-folder the picture came from, which keeps the Deleted folder browsable and
    /// stops identically named pictures from different sub-folders colliding.
    /// </summary>
    private static string BuildDeletedDestination(string sourcePath, string selectedFolder)
    {
        var deletedRoot = Path.Combine(selectedFolder, DeletedFolderName);
        var relativePath = Path.GetRelativePath(selectedFolder, sourcePath);
        var relativeFolder = Path.GetDirectoryName(relativePath);

        // A picture from outside the selected folder has no meaningful relative path to mirror.
        var isBelowSelectedFolder = !Path.IsPathRooted(relativePath)
            && !relativePath.StartsWith("..", StringComparison.Ordinal);
        var destinationFolder = isBelowSelectedFolder && !string.IsNullOrEmpty(relativeFolder)
            ? Path.Combine(deletedRoot, relativeFolder)
            : deletedRoot;

        return GetUniqueDestination(destinationFolder, Path.GetFileName(sourcePath));
    }

    /// <summary>
    /// Kept as a second chance for when the Deleted folder cannot be written, such as a library on
    /// a read-only share.
    /// </summary>
    public static void MoveToRecycleBin(string path)
    {
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>
    /// Returns a liked picture to where it came from. Reports failure rather than overwriting when
    /// something already occupies the original path, so an undo cannot destroy a newer file.
    /// </summary>
    public static bool TryMoveBack(string movedPath, string originalPath)
    {
        if (File.Exists(originalPath) || !File.Exists(movedPath))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.Move(movedPath, originalPath);
        return true;
    }

    private static IEnumerable<string> GetSkippedFolderNames(string folderPath)
    {
        yield return LikedFolderName;
        yield return DeletedFolderName;
        foreach (var album in ReadAlbumNames(folderPath))
        {
            yield return album;
        }
    }

    private static IReadOnlyList<string> ReadAlbumNames(string folderPath)
    {
        var path = Path.Combine(folderPath, AlbumListFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && ValidateFolderName(line) is null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void RememberAlbum(string selectedFolder, string folderName)
    {
        if (folderName.Equals(LikedFolderName, StringComparison.OrdinalIgnoreCase)
            || folderName.Equals(DeletedFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existing = ReadAlbumNames(selectedFolder);
        if (existing.Contains(folderName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(selectedFolder, AlbumListFileName),
                folderName + Environment.NewLine);
        }
        catch (Exception)
        {
            // The pictures have already moved. Forgetting the name only means they might reappear
            // the next time this folder is opened, which is recoverable by moving them again.
        }
    }

    private static string GetUniqueDestination(string folder, string fileName)
    {
        var destination = Path.Combine(folder, fileName);
        if (!File.Exists(destination))
        {
            return destination;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var suffix = 2;
        do
        {
            destination = Path.Combine(folder, $"{name} ({suffix}){extension}");
            suffix++;
        }
        while (File.Exists(destination));

        return destination;
    }
}
