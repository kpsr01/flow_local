using FlowLocal.Core;

namespace FlowLocal.App;

internal static class ClassificationRules
{
    internal static readonly (string Domain, OutputContextCategory Category)[] Domains =
    [
        ("mail.google.com", OutputContextCategory.Email),
        ("outlook.live.com", OutputContextCategory.Email),
        ("outlook.office.com", OutputContextCategory.Email),
        ("outlook.office365.com", OutputContextCategory.Email),
        ("mail.yahoo.com", OutputContextCategory.Email),
        ("proton.me", OutputContextCategory.Email),
        ("protonmail.com", OutputContextCategory.Email),
        ("docs.google.com", OutputContextCategory.Document),
        ("chatgpt.com", OutputContextCategory.AiChat),
        ("openai.com", OutputContextCategory.AiChat),
        ("claude.ai", OutputContextCategory.AiChat),
        ("gemini.google.com", OutputContextCategory.AiChat),
        ("perplexity.ai", OutputContextCategory.AiChat),
        ("copilot.microsoft.com", OutputContextCategory.AiChat),
        ("slack.com", OutputContextCategory.WorkMessaging),
        ("chat.google.com", OutputContextCategory.WorkMessaging),
        ("mattermost.com", OutputContextCategory.WorkMessaging),
        ("whatsapp.com", OutputContextCategory.PersonalMessaging),
        ("telegram.org", OutputContextCategory.PersonalMessaging),
        ("messenger.com", OutputContextCategory.PersonalMessaging),
        ("notion.so", OutputContextCategory.Document),
        ("notion.site", OutputContextCategory.Document)
    ];

    internal static readonly Dictionary<string, OutputContextCategory> Applications =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["outlook"] = OutputContextCategory.Email,
            ["olk"] = OutputContextCategory.Email,
            ["winword"] = OutputContextCategory.Document,
            ["notepad"] = OutputContextCategory.Document,
            ["notion"] = OutputContextCategory.Document,
            ["obsidian"] = OutputContextCategory.Document,
            ["onenote"] = OutputContextCategory.Document,
            ["slack"] = OutputContextCategory.WorkMessaging,
            ["teams"] = OutputContextCategory.WorkMessaging,
            ["ms-teams"] = OutputContextCategory.WorkMessaging,
            ["whatsapp"] = OutputContextCategory.PersonalMessaging,
            ["telegram"] = OutputContextCategory.PersonalMessaging,
            ["signal"] = OutputContextCategory.PersonalMessaging,
            ["discord"] = OutputContextCategory.PersonalMessaging,
            ["code"] = OutputContextCategory.CodeEditor,
            ["cursor"] = OutputContextCategory.CodeEditor,
            ["windsurf"] = OutputContextCategory.CodeEditor,
            ["devenv"] = OutputContextCategory.CodeEditor,
            ["idea64"] = OutputContextCategory.CodeEditor,
            ["rider64"] = OutputContextCategory.CodeEditor,
            ["pycharm64"] = OutputContextCategory.CodeEditor,
            ["webstorm64"] = OutputContextCategory.CodeEditor,
            ["clion64"] = OutputContextCategory.CodeEditor,
            ["goland64"] = OutputContextCategory.CodeEditor,
            ["phpstorm64"] = OutputContextCategory.CodeEditor,
            ["rubymine64"] = OutputContextCategory.CodeEditor,
            ["datagrip64"] = OutputContextCategory.CodeEditor,
            ["windowsterminal"] = OutputContextCategory.Terminal,
            ["powershell"] = OutputContextCategory.Terminal,
            ["pwsh"] = OutputContextCategory.Terminal,
            ["cmd"] = OutputContextCategory.Terminal,
            ["mintty"] = OutputContextCategory.Terminal,
            ["wsl"] = OutputContextCategory.Terminal,
            ["wslhost"] = OutputContextCategory.Terminal
        };

    internal static bool HostMatches(string host, string rule) =>
        host.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith('.' + rule, StringComparison.OrdinalIgnoreCase);

    internal static OutputContextCategory? FromControlHint(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType))
            return null;

        return controlType.Trim().ToLowerInvariant() switch
        {
            "document" => OutputContextCategory.Document,
            "edit" or "textbox" or "text box" => OutputContextCategory.General,
            _ => null
        };
    }
}
