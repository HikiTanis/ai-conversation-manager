using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

// UI-independent package operations. Both frontends use the same archive,
// lineage, transaction, payload, and index validators.
internal static class DesktopPackages
{
    private static string L(string zh, string en) => UiLanguage.IsEnglish ? en : zh;
    public static object Describe(string package)
    {
        if (string.IsNullOrWhiteSpace(package) || !Path.IsPathRooted(package) || !File.Exists(package)) throw new FileNotFoundException(L("请选择现有备份包。", "Select an existing backup package."), package);
        if (!BackupPackageFormat.IsSupportedImport(package)) throw new InvalidDataException(L("备份包与当前数据源不匹配。", "The backup package does not match the selected provider."));
        bool raw = !BackupPackageFormat.IsFormalPackage(package);
        PackManifest manifest = raw ? null : BackendContext.IsDsh ? DshPackages.ReadManifest(package) : OuterPackageArchive.ReadManifest(package);
        if (!BackendContext.IsDsh && manifest?.engine == "deepseek-harness") throw new InvalidDataException(L("这是 DSH 备份包，请切换数据源。", "This is a DSH package. Switch providers."));
        var projects = manifest == null ? new List<PackProject>() : MainWindowController.ManifestProjects(manifest);
        var payloads = projects.Where(p => p.project_payload != null).Select(p => p.project_payload).ToList();
        var sessions = manifest?.sessions ?? new List<PackSession>();
        int children = sessions.Count(s => manifest.schema < 2 && !BackendContext.IsDsh ? string.Equals(s.source, "subagent", StringComparison.OrdinalIgnoreCase) : s.is_subagent);
        string target = payloads.Count > 0 ? MainWindowController.SuggestProjectTarget(manifest)
            : projects.Count == 1 && Directory.Exists(projects[0].source_project) ? projects[0].source_project : "";
        return new { raw, created = manifest?.created_at ?? "", bytes = new FileInfo(package).Length,
            mainCount = sessions.Count - children, subagentCount = children,
            projects = projects.Select(p => new { title = p.source_project_name, path = p.source_project, targetFolder = p.target_folder, hasFiles = p.project_payload != null }).ToArray(),
            hasFiles = payloads.Count > 0, fileCount = payloads.Sum(p => (long)p.file_count), fileBytes = payloads.Sum(p => p.uncompressed_bytes), suggestedTarget = target };
    }
    public static void Create(IList<ProjectGroup> projects, IList<SessionInfo> sessions, bool includeFiles, string output)
    {
        if (projects.Count == 0 || sessions.Count == 0) throw new InvalidOperationException("Select conversations to back up.");
        if (!Path.IsPathRooted(output)) throw new InvalidDataException("Output must be an absolute path.");
        if (File.Exists(output)) throw new IOException("Backup already exists: " + output);
        if (BackendContext.IsDsh) { DshPackages.Create(projects, sessions.Select(s => s.ThreadId).ToArray(), includeFiles, output); return; }
        string staging = Path.Combine(Path.GetTempPath(), "ccm-desktop-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try {
            var manifest = new PackManifest {
                schema = 5, created_at = DateTimeOffset.Now.ToString("o"),
                mode = includeFiles ? "batch_projects_with_files" : "batch_selected_conversations",
                source_project = "", source_project_name = projects.Count + " projects",
                includes_subagents = sessions.Any(s => s.IsSubagent), engine = "native",
                engine_version = typeof(DesktopPackages).Assembly.GetName().Version.ToString(3),
                bundles = new List<string>(), sessions = new List<PackSession>(), projects = new List<PackProject>()
            };
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < projects.Count; i++) {
                var project = projects[i];
                var chosen = sessions.Where(s => project.Sessions.Contains(s)).ToList();
                if (chosen.Count == 0) continue;
                string key = "project-" + (i + 1).ToString("000");
                string relative = "projects/" + key;
                string directory = Path.Combine(staging, "projects", key);
                Directory.CreateDirectory(directory);
                string bundle = relative + "/project.codexbundle";
                ExactBundleWriter.CreateBundle(chosen, Path.Combine(directory, "project.codexbundle"), null);
                var packed = new PackProject {
                    project_key = key, source_project = project.ProjectPath, source_project_name = project.DisplayName,
                    target_folder = MainWindowController.UniqueProjectFolder(project.DisplayName, i + 1, folders),
                    bundles = new List<string> { bundle }
                };
                if (includeFiles) {
                    packed.project_payload = ProjectPayloadService.CreateArchive(project.ProjectPath, Path.Combine(directory, "project-files.zip"), output, null);
                    packed.project_payload.archive_file = relative + "/project-files.zip";
                }
                manifest.projects.Add(packed);
                manifest.bundles.Add(bundle);
                foreach (var session in chosen) {
                    var entry = MainWindowController.ToPackSession(session, bundle);
                    entry.project_key = key;
                    manifest.sessions.Add(entry);
                }
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerialization.NewSerializer().Serialize(manifest), new UTF8Encoding(false));
            OuterPackageArchive.CreateFromDirectoryAtomic(staging, output);
        } finally { Cleanup(staging); }
    }

    public static string Import(string package, string target, bool map, string mode, bool restoreFiles, string conflict, bool dryRun)
    {
        if (mode != "merge" && mode != "copy") throw new InvalidDataException("Unsupported conversation conflict policy.");
        ProjectFileConflictMode fileMode;
        switch (conflict) {
            case "empty": fileMode = ProjectFileConflictMode.RequireEmpty; break;
            case "skip": fileMode = ProjectFileConflictMode.SkipExisting; break;
            case "overwrite": fileMode = ProjectFileConflictMode.OverwriteWithBackup; break;
            default: throw new InvalidDataException("Unsupported project file conflict policy.");
        }
        if (!Path.IsPathRooted(package) || !File.Exists(package)) throw new FileNotFoundException("Select an existing package.", package);
        map |= restoreFiles;
        if (BackendContext.IsDsh) return DshPackages.Import(package, target, map, mode, restoreFiles, fileMode, dryRun);
        if (!BackupPackageFormat.IsSupportedImport(package)) throw new InvalidDataException("Select a Codex package.");
        string home = CodexCatalog.ResolveCodexHome();
        if (string.IsNullOrEmpty(WinSqliteMaintenance.FindActiveDatabase(home)))
            throw new InvalidOperationException("The destination has no Codex index. Open Codex once to initialize this data folder before importing.");
        if (!dryRun) CodexDesktopProjectRegistry.EnsureImportCanWrite(home);
        string staging = Path.Combine(Path.GetTempPath(), "ccm-desktop-import-" + Guid.NewGuid().ToString("N"));
        string rewrite = Path.Combine(Path.GetTempPath(), "ccm-desktop-lineage-" + Guid.NewGuid().ToString("N"));
        var contexts = new List<MainWindowController.ImportProjectContext>();
        NativeImportTransaction transaction = null;
        var log = new StringBuilder();
        try {
            PackManifest manifest = null;
            if (BackupPackageFormat.IsFormalPackage(package)) {
                manifest = OuterPackageArchive.ReadManifest(package);
                OuterPackageArchive.ExtractSafely(package, staging);
                contexts = MainWindowController.BuildImportContexts(manifest, staging, target, map, restoreFiles);
            } else {
                var project = new PackProject { project_key = "raw-project", source_project_name = "Raw bundle", bundles = new List<string>() };
                var context = new MainWindowController.ImportProjectContext {
                    Project = project, TargetPath = map ? MainWindowController.BuildProjectTargetPaths(new[] { project }, target)[0] : ""
                };
                context.BundlePaths.Add(package);
                contexts.Add(context);
            }
            if (map && !restoreFiles && contexts.Any(c => !Directory.Exists(c.TargetPath)))
                throw new DirectoryNotFoundException("Conversation-only import requires existing target project directories.");
            foreach (var context in contexts) {
                string source = Source(context);
                foreach (string bundle in context.BundlePaths)
                    NativeBundleImporter.PreflightBundle(bundle, map ? source : null, map ? context.TargetPath : null);
                if (restoreFiles && context.Project.project_payload != null)
                    context.Plan = ProjectPayloadService.InspectArchive(context.ProjectArchivePath, context.Project.project_payload, context.TargetPath, fileMode);
            }
            Directory.CreateDirectory(rewrite);
            var planner = new ConversationImportPlanner(home);
            int ordinal = 0;
            foreach (var context in contexts)
                foreach (string bundle in context.BundlePaths)
                    context.ImportPlans.Add(planner.CreatePlan(bundle, Path.Combine(rewrite, (++ordinal).ToString("000") + ".codexbundle"), context.TargetPath, mode == "copy"));
            var plans = contexts.SelectMany(c => c.ImportPlans).ToList();
            // Validate every effective bundle before the first write, including
            // when the UI already ran a separate inspection.
            foreach (var context in contexts)
                foreach (var plan in context.ImportPlans)
                    NativeBundleImporter.Import(plan.EffectiveBundlePath, home, map ? Source(context) : null, map ? context.TargetPath : null, NativeBundleImportMode.Merge, true);
            log.AppendLine(L("匹配同源对话：", "Matched: ") + plans.Sum(p => p.MatchedCount) + L("；新建编号：", "; new identities: ") + plans.Sum(p => p.CreatedCount));
            foreach (var context in contexts) log.AppendLine(context.Project.source_project_name + " → " + (map ? context.TargetPath : context.Project.source_project));
            if (dryRun) return L("检查通过，未写入项目或会话数据。\n", "Inspection passed; no project or conversation data written.\n") + log;

            var before = TargetedThreadIndexer.SnapshotSessionFiles(home);
            transaction = NativeImportTransaction.Begin(home);
            foreach (var context in contexts) {
                if (restoreFiles && context.Project.project_payload != null) {
                    context.RestoreResult = ProjectPayloadService.RestoreArchive(context.ProjectArchivePath, context.Project.project_payload, context.TargetPath, fileMode);
                    log.AppendLine(L("项目已还原：", "Project restored: ") + context.RestoreResult.TargetPath);
                    if (!string.IsNullOrEmpty(context.RestoreResult.BackupPath)) log.AppendLine(L("覆盖前项目备份：", "Project backup: ") + context.RestoreResult.BackupPath);
                }
                foreach (var plan in context.ImportPlans) {
                    var result = NativeBundleImporter.Import(plan.EffectiveBundlePath, home, map ? Source(context) : null, map ? context.TargetPath : null, NativeBundleImportMode.Merge, false);
                    transaction.TrackImportedSessionFiles(result.TouchedSessionFiles, plan.IdMap.Keys.Concat(plan.IdMap.Values).Distinct(StringComparer.OrdinalIgnoreCase));
                    log.AppendLine(L($"新增：{result.CreatedCount}；合并：{result.MergedCount}；跳过：{result.SkippedCount}", $"Created: {result.CreatedCount}; merged: {result.MergedCount}; skipped: {result.SkippedCount}"));
                }
            }
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in manifest?.sessions ?? new List<PackSession>())
                if (session != null && !string.IsNullOrWhiteSpace(session.thread_id)) titles[session.thread_id] = session.title;
            foreach (var plan in plans)
                foreach (var pair in plan.IdMap)
                    if (titles.TryGetValue(pair.Key, out string title)) titles[pair.Value] = title;
            var bundles = plans.Select(p => p.EffectiveBundlePath).ToList();
            var targets = plans.ToDictionary(p => Path.GetFullPath(p.EffectiveBundlePath), p => p.TargetPath, StringComparer.OrdinalIgnoreCase);
            var indexed = map ? TargetedThreadIndexer.IndexImportedSessionsMapped(home, bundles, before, false, targets, titles)
                : TargetedThreadIndexer.IndexImportedSessions(home, bundles, before, false, null, titles);
            log.AppendLine(L("索引验证通过：", "Index verified: ") + $"{indexed.VisibilityVerifiedCount}/{indexed.IndexedCount}");
            log.AppendLine(MainWindowController.BuildPaginatedImportWarning(indexed.PaginatedCount));
            transaction.CommitAndDeleteTemporaryBackups();
            transaction = null;
            return L("导入完成，重新打开 Codex 查看对话。\n", "Import completed. Reopen Codex to view conversations.\n") + log;
        } catch (Exception error) {
            string rollbackError = "";
            if (transaction != null) {
                try { transaction.RollbackAndDeleteTemporaryBackups(); }
                catch (Exception recovery) { rollbackError = "\nRollback requires attention: " + recovery.Message; }
            }
            var restored = contexts.Where(c => c.RestoreResult != null).Select(c => c.TargetPath + "\nBackup: " + c.RestoreResult.BackupPath).ToList();
            throw new InvalidOperationException(error.Message + rollbackError + (restored.Count == 0 ? "" : "\nProject files already restored:\n" + string.Join("\n", restored)), error);
        } finally { Cleanup(staging); Cleanup(rewrite); }
    }

    private static string Source(MainWindowController.ImportProjectContext context) =>
        string.IsNullOrWhiteSpace(context.Project.source_project) ? context.Project.project_payload?.source_path : context.Project.source_project;

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
    }
}
