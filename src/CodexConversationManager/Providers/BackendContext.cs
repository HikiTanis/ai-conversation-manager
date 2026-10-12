using System;
using System.IO;
using System.Linq;

namespace CodexConversationManager;

internal static class BackendContext
{
    public static bool IsDsh { get; set; }
    public static string Home => IsDsh ? DshService.Home : CodexCatalog.ResolveCodexHome();
    public static string Name => IsDsh ? "DSH" : "Codex";
    public static bool PersistSelection { get; private set; } = true;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexConversationManager", "provider.txt");
    public static void Initialize(string[] args)
    {
        PersistSelection = !args.Any(a => a.EndsWith("-test", StringComparison.OrdinalIgnoreCase));
        string selected = "codex";
        if (PersistSelection) try { selected = File.ReadAllText(SettingsPath).Trim(); } catch { }
        int index = Array.IndexOf(args, "--provider");
        if (index >= 0 && index + 1 < args.Length) selected = args[index + 1];
        if (args.Contains("--dsh-ui-test")) selected = "dsh";
        IsDsh = string.Equals(selected, "dsh", StringComparison.OrdinalIgnoreCase);
    }
    public static void Save()
    {
        if (!PersistSelection) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)); File.WriteAllText(SettingsPath, IsDsh ? "dsh" : "codex"); } catch { }
    }
}
