namespace PhotoOrganizer.Services;

/// <summary>
/// Restores files that were sent to the Recycle Bin. The recycled shell item is moved back to the
/// folder it came from rather than invoking the shell's Restore verb, because verb names are
/// localised while a move behaves identically on every Windows language.
/// </summary>
internal static class RecycleBinService
{
    private const int RecycleBinFolderId = 10;
    private const string DeletedFromProperty = "System.Recycle.DeletedFrom";
    private const string DateDeletedProperty = "System.Recycle.DateDeleted";

    /// <summary>
    /// Silent, with no confirmation, rename or error dialogs, so an undo never stalls waiting on
    /// shell UI that the application cannot dismiss.
    /// </summary>
    private const int SilentMoveFlags = 4 | 16 | 512 | 1024;

    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestorePollInterval = TimeSpan.FromMilliseconds(50);

    public static Task<bool> TryRestoreAsync(string originalPath)
        => RunInSingleThreadedApartmentAsync(() => TryRestore(originalPath));

    /// <summary>
    /// Shell automation objects are apartment threaded, so they run on a dedicated STA thread
    /// rather than the thread pool. This also keeps the wait for the move off the UI thread.
    /// </summary>
    private static Task<bool> RunInSingleThreadedApartmentAsync(Func<bool> work)
    {
        var completion = new TaskCompletionSource<bool>();
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception)
            {
                completion.SetResult(false);
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool TryRestore(string originalPath)
    {
        // Refusing to overwrite means an undo can never destroy a file made since the delete.
        if (File.Exists(originalPath))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        Directory.CreateDirectory(folder);

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return false;
        }

        dynamic? shell = Activator.CreateInstance(shellType);
        if (shell is null)
        {
            return false;
        }

        var recycleBin = shell.NameSpace(RecycleBinFolderId);
        var destination = shell.NameSpace(folder);
        if (recycleBin is null || destination is null)
        {
            return false;
        }

        var recycledItem = FindMostRecentlyDeleted(recycleBin, originalPath);
        if (recycledItem is null)
        {
            return false;
        }

        destination.MoveHere(recycledItem, SilentMoveFlags);
        return WaitForRestoredFile(originalPath);
    }

    /// <summary>
    /// The newest match wins, so repeatedly deleting the same file name undoes in reverse order.
    /// </summary>
    private static object? FindMostRecentlyDeleted(dynamic recycleBin, string originalPath)
    {
        var fileName = Path.GetFileName(originalPath);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(originalPath);
        var folder = Path.GetDirectoryName(originalPath) ?? string.Empty;

        var items = recycleBin.Items();
        int itemCount = items.Count;
        object? newestMatch = null;
        var newestDeletedAt = DateTime.MinValue;

        for (var index = 0; index < itemCount; index++)
        {
            dynamic item = items.Item(index);
            string itemName = item.Name;

            // Explorer hides known extensions, so a recycled name may arrive without one.
            var nameMatches = itemName.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                || itemName.Equals(nameWithoutExtension, StringComparison.OrdinalIgnoreCase);
            if (!nameMatches || !CameFromFolder(item, folder))
            {
                continue;
            }

            var deletedAt = ReadDateDeleted(item);
            if (deletedAt >= newestDeletedAt)
            {
                newestDeletedAt = deletedAt;
                newestMatch = item;
            }
        }

        return newestMatch;
    }

    private static bool CameFromFolder(dynamic item, string folder)
    {
        var deletedFrom = ReadTextProperty(item, DeletedFromProperty);

        // Without the property the name match alone has to be trusted.
        return string.IsNullOrEmpty(deletedFrom)
            || string.Equals(
                TrimTrailingSeparator(deletedFrom),
                TrimTrailingSeparator(folder),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? ReadTextProperty(dynamic item, string propertyName)
    {
        try
        {
            object? value = item.ExtendedProperty(propertyName);
            return value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime ReadDateDeleted(dynamic item)
    {
        try
        {
            object? value = item.ExtendedProperty(DateDeletedProperty);
            return value is DateTime deletedAt ? deletedAt : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// The shell reports the move as started rather than finished, so the file itself is polled.
    /// </summary>
    private static bool WaitForRestoredFile(string path)
    {
        var deadline = DateTime.UtcNow + RestoreTimeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(RestorePollInterval);
        }

        return File.Exists(path);
    }
}
