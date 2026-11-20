// Windows' own open, save and folder dialogs (the Mac's NSOpenPanel and NSSavePanel), with Glimmer's file types.
using System.IO;
using System.Windows;
using Glimmer.App.Localization;
using Glimmer.Editor;
using Microsoft.Win32;

namespace Glimmer.App.Platform;

public sealed class FileDialogs : IFileDialogs
{
    /// <summary>What Glimmer reads, by kind (DocumentOpener: .glimmer, images, PSD, web projects).</summary>
    public static readonly string[] DocumentExtensions = ["glimmer"];
    public static readonly string[] PhotoshopExtensions = ["psd", "psb"];
    public static readonly string[] ProjectExtensions = ["aips"];
    public static readonly string[] ImageExtensions =
        ["png", "jpg", "jpeg", "jpe", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "jxr", "wdp", "hdp", "ico"];
    public static readonly string[] RawExtensions = ["dng", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "orf", "rw2", "raf", "srw", "pef", "x3f", "3fr", "iiq", "erf", "kdc", "mrw"];

    /// <summary>Remembers the folder of each kind of dialog separately, as Windows does per app.</summary>
    private static readonly Guid OpenGuid = new("0b5f3c2e-6f0a-4c35-9a3e-2d61d9d3a101");
    private static readonly Guid PlaceGuid = new("0b5f3c2e-6f0a-4c35-9a3e-2d61d9d3a102");
    private static readonly Guid SaveGuid = new("0b5f3c2e-6f0a-4c35-9a3e-2d61d9d3a103");
    private static readonly Guid FolderGuid = new("0b5f3c2e-6f0a-4c35-9a3e-2d61d9d3a104");

    public IReadOnlyList<string> ChooseImages(string title, string prompt, bool multiple)
    {
        var images = ImageExtensions.Concat(RawExtensions).Concat(PhotoshopExtensions).ToArray();
        var dialog = new OpenFileDialog
        {
            Title = Loc.T(title), Multiselect = multiple, ClientGuid = PlaceGuid, CheckFileExists = true,
            Filter = Filter((Loc.T("Images"), images), (Loc.T("All Files"), ["*"])),
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public IReadOnlyList<string> ChooseDocuments(bool multiple = true)
    {
        var all = DocumentExtensions.Concat(ImageExtensions).Concat(RawExtensions).Concat(PhotoshopExtensions).Concat(ProjectExtensions).ToArray();
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Open"), Multiselect = multiple, ClientGuid = OpenGuid, CheckFileExists = true,
            Filter = Filter(
                (Loc.T("All Glimmer Files"), all),
                (Loc.T("Glimmer Documents"), DocumentExtensions),
                (Loc.T("Images"), ImageExtensions),
                (Loc.T("Camera RAW"), RawExtensions),
                (Loc.T("Photoshop Documents"), PhotoshopExtensions),
                (Loc.T("Web Projects"), ProjectExtensions),
                (Loc.T("All Files"), ["*"])),
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : [];
    }

    public string? ChooseSaveLocation(string suggestedName, IReadOnlyList<string> extensions, UiText? message = null)
    {
        var first = extensions.Count > 0 ? extensions[0] : "png";
        var dialog = new SaveFileDialog
        {
            Title = message is null ? Loc.T("Save As…") : Loc.T(message),
            FileName = Path.ChangeExtension(suggestedName, first), DefaultExt = first, AddExtension = true, OverwritePrompt = true,
            ClientGuid = SaveGuid,
            Filter = Filter([.. extensions.Select(e => (e.ToUpperInvariant(), new[] { e }))]),
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? ChooseFolder(string title, string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T(title), ClientGuid = FolderGuid, InitialDirectory = initial ?? "" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    /// <summary>"Images (*.png;*.jpg)|*.png;*.jpg|…": the description shows the patterns, as Windows' own apps do.</summary>
    private static string Filter(params (string Name, string[] Extensions)[] kinds) =>
        string.Join("|", kinds.Select(k =>
        {
            var patterns = string.Join(";", k.Extensions.Select(e => "*." + e));
            if (k.Extensions is ["*"]) patterns = "*.*";
            return $"{k.Name} ({patterns})|{patterns}";
        }));
}
