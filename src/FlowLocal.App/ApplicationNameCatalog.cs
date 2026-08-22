using FlowLocal.Core;
using System.IO;

namespace FlowLocal.App;

public static class ApplicationNameCatalog
{
    private static readonly Dictionary<string, (string DisplayName, BrowserIdentity? Browser)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = ("Google Chrome", BrowserIdentity.Chrome),
            ["msedge"] = ("Microsoft Edge", BrowserIdentity.Edge),
            ["firefox"] = ("Mozilla Firefox", BrowserIdentity.Firefox),
            ["outlook"] = ("Microsoft Outlook", null),
            ["olk"] = ("Microsoft Outlook", null),
            ["winword"] = ("Microsoft Word", null),
            ["notepad"] = ("Notepad", null),
            ["slack"] = ("Slack", null),
            ["teams"] = ("Microsoft Teams", null),
            ["discord"] = ("Discord", null),
            ["whatsapp"] = ("WhatsApp", null),
            ["telegram"] = ("Telegram", null),
            ["notion"] = ("Notion", null),
            ["code"] = ("Visual Studio Code", null),
            ["cursor"] = ("Cursor", null),
            ["windsurf"] = ("Windsurf", null),
            ["windowsterminal"] = ("Windows Terminal", null)
        };

    public static (string ExecutableName, string DisplayName, BrowserIdentity? Browser) Normalize(string? executableName)
    {
        var basename = Path.GetFileName(executableName?.Trim() ?? "");
        if (basename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            basename = basename[..^4];

        var normalized = basename.ToLowerInvariant();
        return Known.TryGetValue(normalized, out var known)
            ? (normalized, known.DisplayName, known.Browser)
            : (normalized, basename, null);
    }
}
