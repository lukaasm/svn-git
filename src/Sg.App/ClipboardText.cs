namespace Sg.App;

/// <summary>Clipboard ownership and failure handling shared by paths, reports, and task results.</summary>
internal static class ClipboardText
{
    public static bool Copy(string text)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            return true;
        }
        catch (Exception ex)
        {
            Session.Log.Warn("Cannot copy to the clipboard: " + ex.Message);
            return false;
        }
    }
}
