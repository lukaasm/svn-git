using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Sg.App;

/// <summary>
/// The folder and file dialogs, asked for the way the shell has offered them since Vista, rather than
/// through Windows.Storage.Pickers.
///
/// The WinRT pickers go through a broker process, and the broker refuses a caller running elevated:
/// every one of them throws, and the sentence it throws is about no installed components, which says
/// nothing about elevation to whoever reads it. sg is a developer tool, and a developer tool gets
/// started from whatever terminal is already open - which, after an installer was run as admin, is an
/// elevated one, and every child of it is elevated too.
///
/// IFileDialog has no broker. It is the same dialog, it works at any integrity level, and it is what
/// the WinRT one is a wrapper over. The interop is long only because COM interfaces have to be spelled
/// out in full: nothing here is doing anything clever.
/// </summary>
static class NativePicker
{
    /// <summary>The folder chosen, or null when the dialog was cancelled.</summary>
    public static string? Folder(IntPtr owner, string? startIn = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS.PickFolders | FOS.ForceFileSystem | FOS.NoChangeDir | FOS.PathMustExist);
            StartIn(dialog, startIn);
            return Show(dialog, owner);
        }
        finally { Release(dialog); }
    }

    /// <summary>One file of one of these kinds, or null when the dialog was cancelled.</summary>
    public static string? OpenFile(IntPtr owner, IReadOnlyList<string> extensions, string? startIn = null)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS.ForceFileSystem | FOS.NoChangeDir | FOS.FileMustExist);
            Filters(dialog, extensions);
            StartIn(dialog, startIn);
            return Show(dialog, owner);
        }
        finally { Release(dialog); }
    }

    /// <summary>Where to write a file, or null when the dialog was cancelled. The shell asks about overwriting.</summary>
    public static string? SaveFile(IntPtr owner, string suggestedName, string extension, string? startIn = null)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS.ForceFileSystem | FOS.NoChangeDir | FOS.OverwritePrompt);
            Filters(dialog, [extension]);
            if (extension.StartsWith('.')) dialog.SetDefaultExtension(extension.TrimStart('.'));
            if (suggestedName.Length > 0) dialog.SetFileName(suggestedName);
            StartIn(dialog, startIn);
            return Show(dialog, owner);
        }
        finally { Release(dialog); }
    }

    // ---- the parts every one of them shares ----

    /// <summary>
    /// Modal to the window that asked, so it cannot be lost behind it and the window cannot be used
    /// while it is up. A cancel is a user saying no, not a failure, and comes back as null.
    /// </summary>
    static string? Show(IFileDialog dialog, IntPtr owner)
    {
        var hr = dialog.Show(owner);
        if (hr == HRESULT_CANCELLED) return null;
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        dialog.GetResult(out var item);
        try
        {
            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return path;
        }
        finally { Release(item); }
    }

    /// <summary>
    /// The folder it opens on. Only a hint: the shell remembers where the user was last, and a folder
    /// that is not there any more must not stop the dialog from opening at all.
    /// </summary>
    static void StartIn(IFileDialog dialog, string? folder)
    {
        if (folder is not { Length: > 0 } || !Directory.Exists(folder)) return;
        try
        {
            if (SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var item) < 0) return;
            try { dialog.SetFolder(item); }
            finally { Release(item); }
        }
        catch (COMException) { /* the dialog opens wherever the shell would have opened it */ }
    }

    static void Filters(IFileDialog dialog, IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0) return;
        var spec = string.Join(";", extensions.Select(x => "*" + (x.StartsWith('.') ? x : "." + x)));
        var specs = new[] { new COMDLG_FILTERSPEC { pszName = spec, pszSpec = spec } };
        dialog.SetFileTypes((uint)specs.Length, specs);
        dialog.SetFileTypeIndex(1);
    }

    static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    // ---- interop ----

    const int HRESULT_CANCELLED = unchecked((int)0x800704C7);
    const uint SIGDN_FILESYSPATH = 0x80058000;

    [Flags]
    enum FOS : uint
    {
        OverwritePrompt = 0x00000002,
        NoChangeDir = 0x00000008,
        PickFolders = 0x00000020,
        ForceFileSystem = 0x00000040,
        PathMustExist = 0x00000800,
        FileMustExist = 0x00001000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialogRcw { }

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    class FileSaveDialogRcw { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(FOS options);
        void GetOptions(out FOS options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog : IFileDialog
    {
        // Every method of the base has to be repeated: COM vtables are laid out in order, and a
        // .NET interface that inherits another does not inherit its slots.
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] filters);
        new void SetFileTypeIndex(uint index);
        new void GetFileTypeIndex(out uint index);
        new void Advise(IntPtr events, out uint cookie);
        new void Unadvise(uint cookie);
        new void SetOptions(FOS options);
        new void GetOptions(out FOS options);
        new void SetDefaultFolder(IShellItem item);
        new void SetFolder(IShellItem item);
        new void GetFolder(out IShellItem item);
        new void GetCurrentSelection(out IShellItem item);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        new void GetResult(out IShellItem item);
        new void AddPlace(IShellItem item, int alignment);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr filter);

        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("84bccd23-5fde-4cdb-aea4-af64b83d78ab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileSaveDialog : IFileDialog
    {
        [PreserveSig] new int Show(IntPtr parent);
        new void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] filters);
        new void SetFileTypeIndex(uint index);
        new void GetFileTypeIndex(out uint index);
        new void Advise(IntPtr events, out uint cookie);
        new void Unadvise(uint cookie);
        new void SetOptions(FOS options);
        new void GetOptions(out FOS options);
        new void SetDefaultFolder(IShellItem item);
        new void SetFolder(IShellItem item);
        new void GetFolder(out IShellItem item);
        new void GetCurrentSelection(out IShellItem item);
        new void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        new void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        new void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        new void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        new void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        new void GetResult(out IShellItem item);
        new void AddPlace(IShellItem item, int alignment);
        new void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        new void Close(int hr);
        new void SetClientGuid(ref Guid guid);
        new void ClearClientData();
        new void SetFilter(IntPtr filter);

        void SetSaveAsItem(IShellItem item);
        void SetProperties(IntPtr store);
        void SetCollectedProperties(IntPtr list, bool appendDefault);
        void GetProperties(out IntPtr store);
        void ApplyProperties(IShellItem item, IntPtr store, IntPtr hwnd, IntPtr sink);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr v);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
}
