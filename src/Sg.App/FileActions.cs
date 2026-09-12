using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>Right click on a line in any file tree: open the containing folder, or open the file itself.</summary>
public static class FileActions
{
    /// <summary>
    /// Hangs a context menu off every line of a file tree. absolutePathOf turns one node into a
    /// full path on disk, or null when nothing on disk answers to it. extend lets the window add
    /// its own items for that node under the two file actions; a folder gets them too, and acts on
    /// every file under it.
    /// </summary>
    public static void Attach(ListView tree, Func<TreeNode, string?> absolutePathOf, Action<MenuFlyout, TreeNode>? extend = null)
    {
        tree.RightTapped += (_, e) =>
        {
            var node = NodeUnder(e.OriginalSource as DependencyObject);
            if (node == null) return;
            // Right clicking a line also selects it, so the diff follows the menu.
            if (tree.SelectionMode != ListViewSelectionMode.None && !ReferenceEquals(tree.SelectedItem, node))
                tree.SelectedItem = node;
            string? path;
            try { path = absolutePathOf(node); }
            catch (Exception) { path = null; }
            var menu = Menu(path);
            extend?.Invoke(menu, node);
            menu.ShowAt(tree, new FlyoutShowOptions { Position = e.GetPosition(tree) });
            e.Handled = true;
        };
    }

    static MenuFlyout Menu(string? path)
    {
        var parent = path != null ? Path.GetDirectoryName(path) : null;

        var folder = new MenuFlyoutItem
        {
            Text = "Open folder",
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = parent != null && Directory.Exists(parent),
        };
        folder.Click += (_, _) => OpenFolder(path!);
        ToolTipService.SetToolTip(folder, "Show the file selected in its folder. A folder opens in your file manager.");

        var edit = new MenuFlyoutItem
        {
            Text = "Edit file",
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = path != null && File.Exists(path),
        };
        edit.Click += (_, _) => EditFile(path!);
        ToolTipService.SetToolTip(edit, "Open the file in whatever Windows uses for its type.");

        var copy = new MenuFlyoutItem
        {
            Text = "Copy path",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            IsEnabled = path != null,
        };
        copy.Click += (_, _) => CopyText(path!);
        ToolTipService.SetToolTip(copy, "Put the full path on the clipboard.");

        var menu = new MenuFlyout();
        menu.Items.Add(folder);
        menu.Items.Add(edit);
        menu.Items.Add(copy);
        return menu;
    }

    /// <summary>The clipboard, for a path or anything else a list can hand over.</summary>
    public static void CopyText(string text)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
        catch (Exception ex) { Session.Log.Warn("cannot copy to the clipboard: " + ex.Message); }
    }

    /// <summary>
    /// A folder opens in whatever the shell opens folders with, the user's file manager included. A file
    /// that is there is shown selected in its folder, which only Explorer knows how to do; one that is
    /// gone opens the folder it was in.
    /// </summary>
    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) { Session.OpenInExplorer(path); return; }
        if (File.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }, path);
            return;
        }
        var folder = Path.GetDirectoryName(path);
        if (folder != null) Session.OpenInExplorer(folder);
    }

    public static void EditFile(string path) =>
        Start(new ProcessStartInfo(path) { UseShellExecute = true }, path);

    /// <summary>Windows Terminal when it is installed, otherwise PowerShell. Starts in the folder.</summary>
    public static void OpenTerminal(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var wt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt))
        {
            var psi = new ProcessStartInfo(wt) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(folder);
            Start(psi, folder);
            return;
        }
        Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true }, folder);
    }

    /// <summary>The editor command from Settings, or the folder's default app when there is none.</summary>
    public static void OpenEditor(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var command = Session.Settings.EditorCommand?.Trim();
        if (string.IsNullOrEmpty(command))
        {
            Start(new ProcessStartInfo(folder) { UseShellExecute = true }, folder);
            return;
        }
        var psi = new ProcessStartInfo(command) { UseShellExecute = true, WorkingDirectory = folder };
        psi.ArgumentList.Add(folder);
        Start(psi, folder);
    }

    static void Start(ProcessStartInfo psi, string path)
    {
        try { Process.Start(psi); }
        catch (Exception ex) { Session.Log.Warn("cannot open " + path + ": " + ex.Message); }
    }

    /// <summary>
    /// Which line was clicked. Every element inside the line carries the node as its DataContext,
    /// so this walks up from whatever was hit until it finds one.
    /// </summary>
    static TreeNode? NodeUnder(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement e && e.DataContext is TreeNode node) return node;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
