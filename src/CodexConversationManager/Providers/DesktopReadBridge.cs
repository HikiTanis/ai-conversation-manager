using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

// Transitional read-only transport. Never route this through the WPF refresh/repair flow.
internal static class DesktopReadBridge
{
    internal sealed class Request
    {
        public string action { get; set; }
        public string provider { get; set; }
        public string home { get; set; }
        public string id { get; set; }
        public string sessionPath { get; set; }
        public string projectId { get; set; }
        public string language { get; set; }
        public int? offset { get; set; }
        public string[] ids { get; set; }
        public string[] projectIds { get; set; }
        public string output { get; set; }
        public string package { get; set; }
        public string target { get; set; }
        public string mode { get; set; }
        public string conflict { get; set; }
        public string token { get; set; }
        public string projectMode { get; set; }
        public bool includeFiles { get; set; }
        public bool map { get; set; }
        public bool restoreFiles { get; set; }
        public bool permanent { get; set; }
        public bool batch { get; set; }
        public string projectConfirmation { get; set; }
    }
    public static int Run(bool operations = false)
    {
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        var json = JsonSerialization.NewSerializer();
        try {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            char[] buffer = new char[operations ? 262145 : 16385]; int length = 0, read;
            while (length < buffer.Length && (read = input.Read(buffer, length, buffer.Length - length)) > 0) length += read;
            if (length == buffer.Length) throw new InvalidDataException("Request is too large.");
            var request = json.Deserialize<Request>(new string(buffer, 0, length));
            if (request == null || (request.provider != "codex" && request.provider != "dsh")) throw new InvalidDataException("Unsupported provider.");
            if (!operations && request.action != "list" && request.action != "preview" && request.action != "storage") throw new InvalidDataException("This bridge only supports catalog, preview and storage reads.");
            if (operations && !DesktopOperations.Supports(request.action)) throw new InvalidDataException("Unsupported operation.");
            UiLanguage.Initialize(request.language == "en" ? "en-US" : "zh-CN");
            BackendContext.IsDsh = request.provider == "dsh";
            string home = string.IsNullOrWhiteSpace(request.home) ? (BackendContext.IsDsh ? DshService.Home : CodexCatalog.ResolveCodexHome()) : request.home;
            if (!Path.IsPathRooted(home)) throw new InvalidDataException("Data folder must be an absolute path.");
            home = Path.GetFullPath(home);
            if (!Directory.Exists(home)) throw new DirectoryNotFoundException("Data folder does not exist: " + home);
            if (BackendContext.IsDsh) DshService.Home = home;
            else Environment.SetEnvironmentVariable("CODEX_HOME", home);
            if (operations) {
                // Cross-process exclusion also protects two desktop windows.
                string lockHome = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
                using var mutex = new System.Threading.Mutex(false, "Local\\CcmDesktopOperations-" + DesktopOperations.Hash(lockHome));
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (System.Threading.AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Another operation is running. Wait for it to finish.");
                try { writer.Write(json.Serialize(new { ok = true, result = DesktopOperations.Execute(request, home) })); return 0; }
                finally { mutex.ReleaseMutex(); }
            }
            if (request.action == "preview") {
                var cached = DesktopPreviewCache.TryRead(request, home);
                if (cached != null) { writer.Write(json.Serialize(new { ok = true, result = cached })); return 0; }
            }
            List<ProjectGroup> projects; var warnings = new List<string>();
            if (BackendContext.IsDsh) {
                DshService.Home = home;
                var scan = DshService.Call<DshScan>(new { action = "scan", home });
                projects = DshService.ToProjects(scan); warnings.AddRange(scan.errors ?? new List<string>());
            } else {
                Environment.SetEnvironmentVariable("CODEX_HOME", home); // child process only
                var catalog = CodexCatalog.Build(NativeSessionCatalog.Scan(home), home);
                projects = catalog.Projects;
                if (!string.IsNullOrEmpty(catalog.Diagnostic)) warnings.Add(catalog.Diagnostic);
            }
            object result;
            if (request.action == "list") {
                result = new { home, provider = request.provider, warnings, projects = projects.Select(p => new {
                    id = p.ProjectId, title = p.DisplayName, path = p.ProjectPath, canBackupFiles = p.CanBackupFiles,
                    sessions = p.Sessions.Select(s => new { id = s.ThreadId, title = s.DisplayTitle, path = s.SessionPath,
                        archived = s.Archived, subagent = s.IsSubagent, parentId = s.ParentThreadId,
                        parentTitle = s.ParentDisplayTitle, purpose = s.SubagentPurpose,
                        updated = s.DisplayTime, bytes = s.SizeBytes, source = s.DisplaySource, compressed = s.Compressed }).ToArray()
                }).ToArray() };
            } else if (request.action == "storage") {
                var project = projects.SingleOrDefault(p => p.ProjectId == request.projectId) ?? throw new InvalidDataException("Project changed. Refresh the list.");
                var summary = project.IsSubagentOnly ? new ProjectStorageSummary { Error = UiLanguage.IsEnglish ? "Orphan subagents: project files are not counted." : "孤立子代理：不统计项目文件。" } : ProjectStorageMetrics.Measure(project.ProjectPath);
                result = new { bytes = summary.TotalBytes, files = summary.FileCount, directories = summary.DirectoryCount, skipped = summary.SkippedCount, error = summary.Error };
            } else {
                int offset = request.offset ?? 0;
                if (offset < 0) throw new InvalidDataException("Invalid preview offset.");
                var matches = projects.Where(p => p.ProjectId == request.projectId).SelectMany(p => p.Sessions).Where(s => s.ThreadId == request.id).ToList();
                if (matches.Count != 1) throw new InvalidDataException("Conversation is missing or ambiguous. Refresh the list.");
                result = DesktopPreviewCache.Create(request, home, matches[0]);
            }
            writer.Write(json.Serialize(new { ok = true, result })); return 0;
        } catch (Exception e) { writer.Write(json.Serialize(new { ok = false, error = e.Message })); return 1; }
    }
}
