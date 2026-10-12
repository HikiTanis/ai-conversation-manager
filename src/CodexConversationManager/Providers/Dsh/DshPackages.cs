using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

internal static class DshPackages
{
    public static PackManifest ReadManifest(string package)
    {
        var m = OuterPackageArchive.ReadManifest(package);
        if (m.schema != 1 || m.engine != "deepseek-harness" || m.projects == null || m.native_sessions == null || m.sessions == null) throw new InvalidDataException("这不是支持的 DSH 备份包。请使用 .dshchat 或 .dshproject。");
        if (m.projects.Count == 0 || m.projects.Select(p => p.project_key).Distinct(StringComparer.Ordinal).Count() != m.projects.Count) throw new InvalidDataException("项目清单为空或包含重复标识。");
        return m;
    }
    private static string Stage()
    {
        string root = Path.Combine(Path.GetTempPath(), "dsh-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); return root;
    }
    private static void Cleanup(string root) { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    public static void Create(IList<ProjectGroup> projects, string[] ids, bool includeFiles, string output)
    {
        string staging = Stage();
        try {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = projects.Select((p, i) => {
                string folder = TextHelpers.SafeFileName(p.DisplayName);
                if (string.IsNullOrWhiteSpace(folder)) folder = "project";
                if (!used.Add(folder)) { folder += "-" + (i + 1); used.Add(folder); }
                return new PackProject { project_key = p.ProjectId, source_project = p.ProjectPath, source_project_name = p.DisplayName, target_folder = folder, bundles = new List<string>() };
            }).ToList();
            var m = DshService.Call<PackManifest>(new { action = "export", home = DshService.Home, ids, projects = entries, staging, includeProjectFiles = includeFiles });
            if (includeFiles) for (int i = 0; i < m.projects.Count; i++) {
                string relative = "project-files/project-" + i + ".zip";
                var info = ProjectPayloadService.CreateArchive(m.projects[i].source_project, Path.Combine(staging, relative), output, null);
                info.archive_file = relative; m.projects[i].project_payload = info;
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerialization.NewSerializer().Serialize(m), new UTF8Encoding(false));
            OuterPackageArchive.CreateFromDirectoryAtomic(staging, output);
        } finally { Cleanup(staging); }
    }
    public static string Import(string package, string target, bool map, string mode, bool restoreFiles, ProjectFileConflictMode conflict, bool dryRun)
    {
        var m = ReadManifest(package); string staging = Stage();
        var restored = new List<ProjectRestoreResult>();
        try {
            OuterPackageArchive.ExtractSafely(package, staging);
            var paths = map || restoreFiles ? MainWindowController.BuildProjectTargetPaths(m.projects, target) : m.projects.Select(p => Path.GetFullPath(p.source_project)).ToList();
            var targets = m.projects.Select((p, i) => new { p.project_key, path = paths[i] }).ToDictionary(p => p.project_key, p => p.path);
            for (int i = 0; i < m.projects.Count; i++) if (restoreFiles && m.projects[i].project_payload != null) {
                var payload = m.projects[i].project_payload;
                ProjectPayloadService.InspectArchive(ProjectPayloadService.ResolvePayloadArchivePath(staging, payload), payload, paths[i], conflict);
            }
            var plan = DshService.Call<DshImportResult>(new { action = "importPlan", home = DshService.Home, staging, targets, mode });
            string report = $"会话：写入 {plan.count} 条；目标历史相同或更新，保留 {plan.skipped} 条。\n" + string.Join("\n", targets.Values) + "\n" + string.Join("\n", plan.warnings);
            if (dryRun) return "只读检查通过。\n" + report;
            DshService.EnsureClosed();
            for (int i = 0; i < m.projects.Count; i++) if (restoreFiles && m.projects[i].project_payload != null) {
                var payload = m.projects[i].project_payload;
                restored.Add(ProjectPayloadService.RestoreArchive(ProjectPayloadService.ResolvePayloadArchivePath(staging, payload), payload, paths[i], conflict));
            }
            DshService.Call<DshImportResult>(new { action = "importApply", home = DshService.Home, staging, targets, mode, plan.token });
            return "导入完成。重启 DeepSeek Harness 后查看。\n" + report + string.Join("", restored.Where(r => !string.IsNullOrWhiteSpace(r.BackupPath)).Select(r => "\n项目覆盖前备份：" + r.BackupPath));
        } catch (Exception e) {
            if (restored.Count > 0) throw new InvalidOperationException(e.Message + "\n部分项目文件已还原；会话导入有独立回滚记录。项目路径：\n" + string.Join("\n", restored.Select(r => r.TargetPath + (string.IsNullOrEmpty(r.BackupPath) ? "" : "\n覆盖前备份：" + r.BackupPath))), e);
            throw;
        } finally { Cleanup(staging); }
    }
}
