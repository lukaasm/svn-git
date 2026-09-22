using Sg.Core;

namespace Sg.App;

/// <summary>Typed result navigation shared by task receipts and destination validation.</summary>
internal static class TaskNavigation
{
    public static void Open(string root, TaskFollowUp link, NavHost? navigation)
    {
        if (link.Kind == TaskTargetKind.Folder)
        {
            if (Directory.Exists(link.Path)) Session.OpenInExplorer(link.Path);
            else OutputWindow.Show("The result folder is no longer available: " + link.Path);
            return;
        }
        if (!string.Equals(root.TrimEnd('/', '\\'), Session.Root?.RootPath.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
            || navigation == null) return;
        switch (link.Kind)
        {
            case TaskTargetKind.Replay when Directory.Exists(link.Path):
                navigation.Go(() => new ConflictPage(link.Path), "resolve:" + link.Path); break;
            case TaskTargetKind.Update when Directory.Exists(link.Path):
                navigation.Go(() => new UpdateBranchPage(link.Path), "update-branch:" + link.Path); break;
            case TaskTargetKind.Backup:
                navigation.Go(() => new BackupPage(), "backup"); break;
            default:
                navigation.Go(() => new ActivityPage(), "activity"); break;
        }
    }
}
