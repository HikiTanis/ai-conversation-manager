using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexConversationManager;

internal static class DesktopOperations
{
    private static readonly string[] Actions = { "backup", "packageInfo", "inspect", "import", "trash", "restore", "purge", "trashProject", "planDelete", "delete", "archive", "unarchive", "recover", "planRepair", "repair" };
    public static bool Supports(string action) => Actions.Contains(action);
    internal static string Hash(string value) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
    private static object Message(string message) => new { message };
    private static string L(string zh, string en) => UiLanguage.IsEnglish ? en : zh;
    private static void EnsureClosed(string home) { if (BackendContext.IsDsh) DshService.EnsureClosed(); else CodexDesktopProjectRegistry.EnsureImportCanWrite(home); }
    private static ProjectDeleteMode ProjectMode(string value) => value == null || value == "none" ? ProjectDeleteMode.None
        : value == "recycle" ? ProjectDeleteMode.RecycleBin : value == "permanent" ? ProjectDeleteMode.Permanent
        : throw new InvalidDataException("Unsupported project deletion mode.");

    public static object Execute(DesktopReadBridge.Request request, string home)
    {
        switch (request.action) {
            case "packageInfo": return DesktopPackages.Describe(request.package);
            case "inspect": case "import":
                return Message(DesktopPackages.Import(request.package, request.target, request.map, request.mode, request.restoreFiles, request.conflict, request.action == "inspect"));
            case "recover":
                if (!BackendContext.IsDsh) throw new InvalidOperationException("Recovery is available for DSH.");
                EnsureClosed(home);
                DshService.Call<object>(new { action = "recover", home });
                return Message(L("中断操作恢复完成。", "Recovery completed."));
            case "trash":
                return new { items = ProviderStorage.ReadTrash().Select(t => new { id = Hash(t.BackupPath), title = t.DisplayTitle, path = t.ProjectPath, originalPath = t.OriginalPath, deleted = t.DisplayDeletedAt, bytes = t.SizeBytes, projectHandled = !string.IsNullOrEmpty(t.ProjectDeleteMode) }).ToArray() };
            case "restore": case "purge": case "trashProject": {
                var matches = ProviderStorage.ReadTrash().Where(t => Hash(t.BackupPath) == request.id).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("Trash entry changed. Refresh and try again.");
                var item = matches[0];
                EnsureClosed(home);
                if (request.action == "restore") ProviderStorage.Restore(item);
                else if (request.action == "purge") ProviderStorage.DeleteFromTrash(item);
                else {
                    var mode = ProjectMode(request.projectMode);
                    if (mode == ProjectDeleteMode.None) throw new InvalidDataException("Select a project action.");
                    if (!string.IsNullOrEmpty(item.ProjectDeleteMode)) throw new InvalidOperationException("Project was already processed.");
                    var projectPath = ProviderStorage.ValidateProjectPath(item.ProjectPath);
                    if (mode == ProjectDeleteMode.Permanent) RequireProjectConfirmation(new[] { projectPath }, request.projectConfirmation);
                    ProviderStorage.DeleteProject(projectPath, mode);
                    ProviderStorage.MarkProjectHandled(item, mode);
                }
                return Message(L("已完成：", "Completed: ") + item.DisplayTitle);
            }
            case "planRepair": case "repair": {
                if (BackendContext.IsDsh) throw new InvalidOperationException("Sidebar repair is available for Codex.");
                var missing = ConversationIndexMaintenance.FindOrphanedThreads(home);
                string diagnostic = L("检查完成。发现 ", "Check completed. Found ") + missing.Count +
                    L(" 条文件路径缺失的索引记录。已保留所有会话、索引、缓存和恢复快照；不会自动删除。请从备份恢复缺失内容。", " index entries with missing rollout paths. All conversations, indexes, caches and recovery snapshots were preserved. No automatic deletion is performed; restore missing content from a backup.");
                if (request.action == "planRepair") return new { token = "read-only", ids = new string[0], snapshotCount = 0, cacheCleanup = false, diagnostic,
                    blocked = ConversationIndexMaintenance.FindLiveDescendants(home, missing.Select(t => t.Id)).Select(d => new { id = d.ThreadId, rootId = d.RootThreadId, title = d.Title, path = d.RolloutPath }).ToArray() };
                return Message(diagnostic);
            }
        }
        List<ProjectGroup> projects = BackendContext.IsDsh
            ? DshService.ToProjects(DshService.Call<DshScan>(new { action = "scan", home }))
            : CodexCatalog.Build(NativeSessionCatalog.Scan(home), home).Projects;
        var all = projects.SelectMany(p => p.Sessions).ToList();
        var selected = new List<SessionInfo>();
        foreach (string id in (request.ids ?? new string[0]).Distinct(StringComparer.OrdinalIgnoreCase)) {
            var matches = all.Where(s => string.Equals(s.ThreadId, id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Conversation is missing or ambiguous. Refresh: " + id);
            selected.Add(matches[0]);
        }
        if (request.action == "backup" && (request.projectIds?.Length ?? 0) > 0) {
            foreach (string id in request.projectIds.Distinct()) {
                var project = projects.SingleOrDefault(p => p.ProjectId == id) ?? throw new InvalidOperationException("Project changed. Refresh.");
                if (request.includeFiles && !project.CanBackupFiles) throw new InvalidOperationException(L("项目目录不存在或只包含孤立子代理，不能备份项目文件：", "Project folder is missing or contains only orphan subagents: ") + project.DisplayName);
                selected.AddRange(project.Sessions.Where(s => request.includeFiles || !s.IsSubagent));
            }
            selected = selected.Distinct().ToList();
        }
        if (selected.Count == 0) throw new InvalidOperationException("Select conversations first.");
        if (request.action == "backup") {
            DesktopPackages.Create(projects.Where(p => p.Sessions.Any(selected.Contains)).ToList(), selected, request.includeFiles, request.output);
            return Message(L("备份已保存：", "Backup saved: ") + request.output);
        }
        if (request.action == "archive" || request.action == "unarchive") {
            if (!BackendContext.IsDsh) throw new InvalidOperationException("Archive management is available for DSH.");
            EnsureClosed(home);
            string[] ids = selected.Select(s => s.ThreadId).ToArray();
            var plan = DshService.Call<DshPlan>(new { action = "plan", home, ids, operation = request.action });
            DshService.Call<DshChange>(new { action = "change", home, ids, operation = request.action, plan.token });
            return Message(L("归档状态已更新。", "Archive state updated."));
        }
        var closures = selected.ToDictionary(s => s, s => Closure(s, all));
        var roots = selected.Where(s => !selected.Any(other => other != s && closures[other].Contains(s))).ToList();
        if (roots.Count == 0) throw new InvalidDataException("Cyclic conversation ancestry. Repair the source data first.");
        var affected = roots.SelectMany(s => closures[s]).Distinct().ToList();
        bool batchDelete = request.batch || selected.Count > 1;
        var modeProject = ProjectMode(request.projectMode);
        var paths = modeProject == ProjectDeleteMode.None ? new List<string>() : roots.Select(s => {
            if (s.IsSubagent) throw new InvalidOperationException("Project deletion is unavailable for subagents.");
            return ProviderStorage.ValidateProjectPath(ProviderStorage.ResolveProjectPath(s, projects.First(p => p.Sessions.Contains(s))));
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (modeProject != ProjectDeleteMode.None && batchDelete) {
            var batchPaths = new List<string>();
            foreach (var project in projects.Where(p => p.Sessions.Any(selected.Contains))) {
                var scope = MainWindowController.BuildBatchProjectDeleteScope(project, selected.Where(project.Sessions.Contains), projects);
                if (!string.IsNullOrWhiteSpace(scope.AvailabilityBlockReason)) throw new InvalidOperationException(scope.AvailabilityBlockReason);
                if (!scope.AllMainConversationsSelected) throw new InvalidOperationException(L("只有选中该目录关联的全部主对话后，才能批量处理项目目录。", "Select every related main conversation before processing the project folder in a batch."));
                batchPaths.Add(ProviderStorage.ValidateProjectPath(scope.ProjectPath));
            }
            paths = batchPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var remainingSessions = all.Where(s => !affected.Contains(s) && paths.Any(p => !string.IsNullOrWhiteSpace(s.Cwd) && TextHelpers.IsWithin(s.Cwd, p))).ToList();
        var deletionFiles = BackendContext.IsDsh ? affected.Select(s => s.SessionPath).ToList() :
            roots.SelectMany(root => ConversationStorage.PreviewDeletionFiles(root, closures[root])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string fileFingerprint = string.Join("\n", deletionFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => path + "|" + ProjectPayloadService.Sha256File(path)));
        string fingerprint = Hash(fileFingerprint + "\n" + home + "\n" + batchDelete + "\n" + request.permanent + "\n" + modeProject + "\n" + string.Join("\n", paths.OrderBy(p => p)) + "\n" + string.Join("\n", remainingSessions.OrderBy(s => s.ThreadId).Select(s => s.ThreadId + "|" + s.Cwd)) + "\n" + string.Join("\n", affected.OrderBy(s => s.ThreadId).Select(s => {
            var info = new FileInfo(s.SessionPath);
            return s.ThreadId + "|" + s.ParentThreadId + "|" + s.SessionPath + "|" + (info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "missing");
        })));
        if (request.action == "planDelete") return new { token = fingerprint, sessions = affected.Select(s => new { id = s.ThreadId, title = s.DisplayTitle, subagent = s.IsSubagent }).ToArray(), paths,
            remaining = remainingSessions.Count, fileCount = deletionFiles.Count };
        if (request.action != "delete") throw new InvalidDataException("Unsupported operation.");
        if (request.token != fingerprint) throw new InvalidOperationException("Deletion scope or files changed. Review the refreshed scope before deleting.");
        if (modeProject == ProjectDeleteMode.Permanent) RequireProjectConfirmation(paths, request.projectConfirmation);
        EnsureClosed(home);
        var completed = new List<string>();
        var projectByRoot = roots.ToDictionary(s => s, s => batchDelete ? paths.FirstOrDefault(p => TextHelpers.IsWithin(s.Cwd, p)) ?? s.Cwd : s.Cwd);
        try {
            var results = new List<DeletedSessionResult>();
            foreach (var root in roots) {
                results.Add(request.permanent ? ProviderStorage.DeletePermanently(root, closures[root]) : ProviderStorage.MoveToTrash(root, projectByRoot[root], closures[root]));
                completed.Add(root.DisplayTitle);
            }
            foreach (string path in paths) {
                ProviderStorage.DeleteProject(path, modeProject);
                foreach (var result in results.Where(r => !r.PermanentlyDeleted))
                    foreach (string backup in result.BackupPaths.Count > 0 ? result.BackupPaths : new List<string> { result.BackupPath })
                        // Only mark records associated with this project.
                        if (roots.Any(r => r.SessionPath == result.OriginalPath && string.Equals(projectByRoot[r], path, StringComparison.OrdinalIgnoreCase)))
                            ProviderStorage.MarkProjectHandled(backup, path, modeProject);
            }
        } catch (Exception error) { throw new InvalidOperationException(error.Message + L("\n已确认完成的主对话：\n", "\nCompleted conversation roots:\n") + (completed.Count == 0 ? L("无", "None") : string.Join("\n", completed)), error); }
        return Message(L("已处理 " + affected.Count + " 条对话。", "Deleted " + affected.Count + " conversation(s)."));
    }

    private static List<SessionInfo> Closure(SessionInfo root, List<SessionInfo> all)
    {
        var result = new List<SessionInfo> { root };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ThreadId };
        for (int i = 0; i < result.Count; i++)
            foreach (var child in all.Where(s => string.Equals(s.ParentThreadId, result[i].ThreadId, StringComparison.OrdinalIgnoreCase)))
                if (visited.Add(child.ThreadId)) result.Add(child);
        return result;
    }
    private static void RequireProjectConfirmation(IEnumerable<string> paths, string supplied)
    {
        string expected = string.Join("\n", paths.Select(DeleteOptionsDialog.SafeDirectoryName));
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, (supplied ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L("项目文件夹名不匹配，未执行永久删除。", "Project folder names do not match; permanent deletion was not performed."));
    }
}
