using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CodexConversationManager;

internal sealed class DshRow
{
    public string id { get; set; } public string title { get; set; }
    public string cwd { get; set; } public string project { get; set; }
    public string owner { get; set; } public string parent { get; set; }
    public string kind { get; set; } public string updated { get; set; }
    public string relative { get; set; } public string file { get; set; }
    public bool archived { get; set; } public bool pinned { get; set; }
    public long bytes { get; set; }
}
internal sealed class DshProject { public string id { get; set; } public string title { get; set; } public string path { get; set; } }
internal sealed class DshScan { public List<DshRow> rows { get; set; } public List<DshProject> projects { get; set; } public List<string> errors { get; set; } public List<DshTrash> trash { get; set; } }
internal sealed class DshTrash { public string batch { get; set; } public string state { get; set; } public string title { get; set; } public string created { get; set; } public string projectMode { get; set; } public int count { get; set; } public List<DshRow> rows { get; set; } }
internal sealed class DshPlan { public List<DshRow> rows { get; set; } public string token { get; set; } public int count { get; set; } }
internal sealed class DshChange { public string batch { get; set; } public int count { get; set; } }
internal sealed class DshMessages { public List<ConversationMessage> messages { get; set; } }
internal sealed class DshImportResult { public string token { get; set; } public int count { get; set; } public int skipped { get; set; } public string batch { get; set; } public List<string> warnings { get; set; } }

internal static class DshService
{
    public static string Home { get; set; } = Path.GetFullPath(Environment.GetEnvironmentVariable("DSH_MANAGER_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));
    private static string node;
    public static T Call<T>(object request)
    {
        node ??= FindNode();
        string body = JsonSerialization.NewSerializer().Serialize(request);
        StringBuilder ascii = new StringBuilder();
        foreach (char c in body) { if (c > 127) ascii.Append("\\u" + ((int)c).ToString("x4")); else ascii.Append(c); }
        ProcessStartInfo info = new ProcessStartInfo(node, "\"" + Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backend.cjs") + "\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
        };
        using Process p = Process.Start(info);
        Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
        p.StandardInput.Write(ascii.ToString()); p.StandardInput.Close();
        p.WaitForExit();
        var serializer = JsonSerialization.NewSerializer();
        Dictionary<string, object> response;
        try { response = serializer.Deserialize<Dictionary<string, object>>(stdout.Result); }
        catch { throw new InvalidOperationException("DSH 后端未返回有效结果：" + stderr.Result); }
        if (response == null || !response.ContainsKey("ok")) throw new InvalidOperationException("DSH 后端未返回有效结果：" + stderr.Result);
        if (!(bool)response["ok"]) throw new InvalidOperationException(Convert.ToString(response["error"]));
        return serializer.Deserialize<T>(serializer.Serialize(response["result"]));
    }
    public static Task<T> CallAsync<T>(object request) => Task.Run(() => Call<T>(request));
    public static void EnsureClosed() => Call<object>(new { action = "offline", home = Home });
    private static string FindNode()
    {
        var paths = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runtime/node.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh/dsh-runtimes/dsh-primary-runtime/dependencies/node/bin/node.exe") };
        paths.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Path.Combine(s.Trim('"'), "node.exe")));
        foreach (string p in paths.Where(File.Exists).Distinct()) {
            try {
                using Process process = Process.Start(new ProcessStartInfo(p, "-e \"process.exit(typeof require('node:zlib').zstdDecompressSync==='function'?0:1)\"") { UseShellExecute = false, CreateNoWindow = true });
                if (process.WaitForExit(5000) && process.ExitCode == 0) return p;
            } catch { }
        }
        throw new InvalidOperationException("未找到支持 Zstandard 的 Node.js。请安装 DSH 桌面端，或安装 Node.js 22.15+ / 24+。");
    }
    public static ConversationReadResult ReadConversation(SessionInfo session)
    {
        var result = Call<DshMessages>(new { action = "preview", home = Home, id = session.ThreadId });
        foreach (ConversationMessage message in result.messages) {
            message.RoleLabel = message.IsUser ? UiLanguage.T("你") : "DeepSeek";
            if (DateTimeOffset.TryParse(message.DisplayTime, out var time)) message.DisplayTime = time.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        }
        if (result.messages.Count == 0) result.messages.Add(new ConversationMessage { IsNotice = true, RoleLabel = UiLanguage.T("提示"), Text = UiLanguage.T("没有可显示的用户或助手消息。") });
        return new ConversationReadResult { Messages = result.messages, SessionPath = session.SessionPath };
    }
    public static List<ProjectGroup> ToProjects(DshScan scan)
    {
        var groups = scan.projects.ToDictionary(p => p.id, p => new ProjectGroup { ProjectId = p.id, DisplayName = p.title, ProjectPath = p.path, Sessions = new List<SessionInfo>() });
        foreach (DshRow row in scan.rows) {
            string key = !string.IsNullOrWhiteSpace(row.owner) ? row.owner : "cwd:" + row.cwd;
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new ProjectGroup { ProjectId = key, DisplayName = row.project, ProjectPath = row.cwd, Sessions = new List<SessionInfo>() };
            DateTime.TryParse(row.updated, out var date);
            group.Sessions.Add(new SessionInfo { ThreadId = row.id, Title = row.title, Cwd = row.cwd, Archived = row.archived, UpdatedAt = row.updated, UpdatedDate = date.ToLocalTime(), Source = "dsh-desktop", SessionPath = Path.Combine(Home, row.file), RelativePath = row.relative, Compressed = row.file.EndsWith(".zstd"), IsSubagent = row.kind == "子代理", ParentThreadId = row.parent, SizeBytes = row.bytes, MetadataVerified = true });
        }
        var sessions = groups.Values.SelectMany(g => g.Sessions).ToDictionary(s => s.ThreadId);
        foreach (var s in sessions.Values) if (!string.IsNullOrEmpty(s.ParentThreadId) && sessions.TryGetValue(s.ParentThreadId, out var parent)) s.ParentDisplayTitle = parent.Title;
        return groups.Values.OrderByDescending(g => g.LastUpdated).ToList();
    }
}

internal static class DshStorage
{
    public static string TrashRoot => Path.Combine(DshService.Home, "conversation-manager-trash");
    public static DeletedSessionResult MoveToTrash(SessionInfo session, string projectPath, IEnumerable<SessionInfo> affectedSessions)
    {
        string[] ids = { session.ThreadId };
        var plan = DshService.Call<DshPlan>(new { action = "plan", home = DshService.Home, ids, operation = "trash" });
        var expected = new HashSet<string>(affectedSessions.Select(s => s.ThreadId), StringComparer.Ordinal);
        if (!expected.SetEquals(plan.rows.Select(s => s.id))) throw new InvalidOperationException("子代理关系已变化，请刷新后重新确认删除范围。");
        var result = DshService.Call<DshChange>(new { action = "change", home = DshService.Home, ids, operation = "trash", plan.token });
        return new DeletedSessionResult { OriginalPath = session.SessionPath, BackupPath = Path.Combine(TrashRoot, result.batch), AffectedConversationCount = result.count };
    }
    public static DeletedSessionResult DeletePermanently(SessionInfo session, IEnumerable<SessionInfo> affectedSessions)
    {
        var result = MoveToTrash(session, session.Cwd, affectedSessions);
        try { DshService.Call<object>(new { action = "purge", home = DshService.Home, batch = Path.GetFileName(result.BackupPath) }); }
        catch (Exception e) { throw new IOException("会话已移入回收站，但永久清除未完成，可在回收站重试：" + e.Message, e); }
        result.PermanentlyDeleted = true; return result;
    }
    public static List<TrashSessionInfo> ReadTrash() => DshService.Call<List<DshTrash>>(new { action = "trash", home = DshService.Home }).Where(t => t.state == "trashed" || t.state == "purging").Select(t => new TrashSessionInfo {
        ThreadId = t.batch, Title = t.title + " · " + t.count + UiLanguage.T(" 条对话（整批恢复）"), BackupPath = Path.Combine(TrashRoot, t.batch), OriginalPath = string.Join("\n", t.rows.Select(r => Path.Combine(DshService.Home, r.relative))), ProjectPath = t.rows.FirstOrDefault()?.cwd, SizeBytes = t.rows.Sum(r => r.bytes), DeletedAt = DateTimeOffset.Parse(t.created), ProjectDeleteMode = t.projectMode
    }).ToList();
    public static void Restore(TrashSessionInfo item) => DshService.Call<object>(new { action = "restore", home = DshService.Home, batch = item.ThreadId });
    public static void DeleteFromTrash(TrashSessionInfo item) => DshService.Call<object>(new { action = "purge", home = DshService.Home, batch = item.ThreadId });
    public static string ResolveProjectPath(SessionInfo session, ProjectGroup project) => ConversationStorage.ResolveProjectPath(session, project);
    public static string ValidateProjectPath(string path) => ConversationStorage.ValidateProjectPath(path);
    public static void DeleteProject(string path, ProjectDeleteMode mode) => ConversationStorage.DeleteProject(path, mode);
    public static void MarkProjectHandled(string backupPath, string projectPath, ProjectDeleteMode mode) => DshService.Call<object>(new { action = "markProject", home = DshService.Home, batch = Path.GetFileName(backupPath), mode = mode.ToString() });
    public static void MarkProjectHandled(TrashSessionInfo item, ProjectDeleteMode mode) => MarkProjectHandled(item.BackupPath, item.ProjectPath, mode);
}
