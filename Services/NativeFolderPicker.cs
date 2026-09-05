using System.Runtime.InteropServices;

namespace PhotoOrganizer.Services;

internal static class NativeFolderPicker
{
    private const int CancelledHResult = unchecked((int)0x800704C7);
    private static readonly Guid FileOpenDialogClassId = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    public static string? PickFolder(nint ownerWindow)
    {
        var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClassId, throwOnError: true)!;
        var dialog = (IFileDialog)Activator.CreateInstance(dialogType)!;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options
                | FileOpenOptions.PickFolders
                | FileOpenOptions.ForceFileSystem
                | FileOpenOptions.PathMustExist
                | FileOpenOptions.DontAddToRecent);
            dialog.SetTitle("Select Folder");
            dialog.SetOkButtonLabel("Select Folder");

            var result = dialog.Show(ownerWindow);
            if (result == CancelledHResult)
            {
                return null;
            }
            Marshal.ThrowExceptionForHR(result);

            dialog.GetResult(out var shellItem);
            try
            {
                shellItem.GetDisplayName(ShellDisplayName.FileSystemPath, out var pathPointer);
                try
                {
                    return Marshal.PtrToStringUni(pathPointer);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPointer);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shellItem);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    [Flags]
    private enum FileOpenOptions : uint
    {
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800,
        DontAddToRecent = 0x02000000,
    }

    private enum ShellDisplayName : uint
    {
        FileSystemPath = 0x80058000,
    }

    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig]
        int Show(nint parent);

        void SetFileTypes(uint fileTypeCount, nint filterSpecifications);
        void SetFileTypeIndex(uint fileTypeIndex);
        void GetFileTypeIndex(out uint fileTypeIndex);
        void Advise(nint events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FileOpenOptions options);
        void GetOptions(out FileOpenOptions options);
        void SetDefaultFolder(IShellItem shellItem);
        void SetFolder(IShellItem shellItem);
        void GetFolder(out IShellItem shellItem);
        void GetCurrentSelection(out IShellItem shellItem);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem shellItem);
        void AddPlace(IShellItem shellItem, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hResult);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(nint filter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint bindContext, ref Guid handler, ref Guid interfaceId, out nint result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(ShellDisplayName displayName, out nint name);
        void GetAttributes(uint requestedAttributes, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
