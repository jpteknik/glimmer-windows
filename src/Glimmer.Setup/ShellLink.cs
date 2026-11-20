// New file, no Swift original: a Start menu shortcut, made with the shell's ShellLink object (IShellLinkW, saved through
// IPersistFile). The interfaces are declared in shobjidl_core.h's and objidl.h's order, every method up to the last one
// used, and marshalled by .NET's source-generated COM, which trimming keeps intact.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace Glimmer.Setup;

[SupportedOSPlatform("windows")]
internal static partial class ShellLink
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-c000-000000000046");

    public static void Create(string linkPath, string target, string workingDirectory, string description)
    {
        _ = CoInitializeEx(IntPtr.Zero, 0x2);   // COINIT_APARTMENTTHREADED; already initialized is fine
        var iid = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ShellLinkClass, IntPtr.Zero, 0x1 /* CLSCTX_INPROC_SERVER */, iid, out var pointer));
        try
        {
            var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr instance);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214f9-0000-0000-c000-000000000046")]
internal partial interface IShellLinkW
{
    [PreserveSig] int GetPath(IntPtr file, int size, IntPtr findData, uint flags);
    [PreserveSig] int GetIDList(out IntPtr list);
    [PreserveSig] int SetIDList(IntPtr list);
    [PreserveSig] int GetDescription(IntPtr name, int size);
    void SetDescription(string name);
    [PreserveSig] int GetWorkingDirectory(IntPtr directory, int size);
    void SetWorkingDirectory(string directory);
    [PreserveSig] int GetArguments(IntPtr arguments, int size);
    [PreserveSig] int SetArguments(string arguments);
    [PreserveSig] int GetHotkey(out ushort hotkey);
    [PreserveSig] int SetHotkey(ushort hotkey);
    [PreserveSig] int GetShowCmd(out int show);
    [PreserveSig] int SetShowCmd(int show);
    [PreserveSig] int GetIconLocation(IntPtr path, int size, out int index);
    void SetIconLocation(string path, int index);
    [PreserveSig] int SetRelativePath(string path, uint reserved);
    [PreserveSig] int Resolve(IntPtr window, uint flags);
    void SetPath(string file);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("0000010b-0000-0000-c000-000000000046")]
internal partial interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid classId);   // IPersist
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load(string fileName, uint mode);
    void Save(string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
}
