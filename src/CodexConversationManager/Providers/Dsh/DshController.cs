using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodexConversationManager;

internal sealed partial class MainWindowController
{
    private DshScan dshScan;
    private bool MatchesArchiveFilter(SessionInfo s) => Find<RadioButton>("ArchiveAllRadio").IsChecked == true || (Find<RadioButton>("ArchiveDoneRadio").IsChecked == true ? s.Archived : !s.Archived);
    private void WireDshEvents()
    {
        foreach (string name in new[] { "ArchiveAllRadio", "ArchiveActiveRadio", "ArchiveDoneRadio" }) Find<RadioButton>(name).Checked += delegate { RefreshSessionView(); UpdateDshControls(); };
        Find<Button>("ArchiveSelectedButton").Click += async delegate { await ArchiveDshAsync(true); };
        Find<Button>("UnarchiveSelectedButton").Click += async delegate { await ArchiveDshAsync(false); };
        Find<Button>("DshHomeButton").Click += async delegate {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = UiLanguage.T("选择 DSH 数据目录（.dsh）"), SelectedPath = DshService.Home };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            string previous = DshService.Home;
            try { await DshService.CallAsync<DshScan>(new { action = "scan", home = dialog.SelectedPath }); DshService.Home = dialog.SelectedPath; await RefreshDataAsync(); }
            catch (Exception e) { DshService.Home = previous; AppDialog.ShowCompat(window, e.Message, "读取会话失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        Find<Button>("DshRecoveryButton").Click += async delegate {
            if (isBusy || !AppDialog.Confirm(window, "恢复中断操作", "恢复中断操作", "完全退出 DSH 后，撤销未完成的管理器写入，并恢复数据。", AppDialogTone.Warning, "恢复", "取消")) return;
            SetBusy(true, "正在恢复中断操作……");
            try { await DshService.CallAsync<object>(new { action = "recover", home = DshService.Home }); SetBusy(false, null); await RefreshDataAsync(); }
            catch (Exception e) { AppDialog.ShowCompat(window, e.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
            finally { SetBusy(false, null); }
        };
        projectList.SelectionChanged += delegate { UpdateDshControls(); };
        Find<Button>("DshHomeButton").ToolTip = DshService.Home;
    }
    private void UpdateDshControls()
    {
        var sessions = CurrentSessionTypeItems();
        var selected = CurrentVisibleSessionTypeItems().Where(s => s.IsSelected).ToList();
        Find<Button>("ArchiveSelectedButton").IsEnabled = BackendContext.IsDsh && !isBusy && selected.Any(s => !s.Archived);
        Find<Button>("UnarchiveSelectedButton").IsEnabled = BackendContext.IsDsh && !isBusy && selected.Any(s => s.Archived);
        Find<Button>("DshHomeButton").IsEnabled = !isBusy;
        Find<Button>("DshRecoveryButton").IsEnabled = !isBusy;
        foreach (string name in new[] { "ArchiveAllRadio", "ArchiveActiveRadio", "ArchiveDoneRadio" }) Find<RadioButton>(name).IsEnabled = !isBusy;
        Find<RadioButton>("ArchiveAllRadio").Content = UiLanguage.T("全部") + " (" + sessions.Count + ")";
        Find<RadioButton>("ArchiveActiveRadio").Content = UiLanguage.T("未归档") + " (" + sessions.Count(s => !s.Archived) + ")";
        Find<RadioButton>("ArchiveDoneRadio").Content = UiLanguage.T("已归档") + " (" + sessions.Count(s => s.Archived) + ")";
    }
    private bool EnsureDshClosed()
    {
        try { DshService.EnsureClosed(); return true; }
        catch (Exception e) { AppDialog.ShowCompat(window, e.Message, "请先退出 DeepSeek Harness", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private async Task RefreshDshAsync()
    {
        if (isBusy) return;
        string previousProject = selectedProject?.ProjectId;
        SetBusy(true, "正在读取 DSH 本地项目与对话……");
        try {
            dshScan = await DshService.CallAsync<DshScan>(new { action = "scan", home = DshService.Home });
            projects = DshService.ToProjects(dshScan);
            foreach (var p in projects) { p.PropertyChanged += ProjectPropertyChanged; foreach (var s in p.Sessions) s.PropertyChanged += SessionPropertyChanged; }
            projectList.ItemsSource = projects;
            projectList.SelectedItem = projects.FirstOrDefault(p => p.ProjectId == previousProject) ?? projects.FirstOrDefault();
            if (projects.Count == 0) { selectedProject = null; sessionList.ItemsSource = null; projectTitleText.Text = UiLanguage.T("选择一个项目"); projectPathText.Text = projectMetaText.Text = projectSizeText.Text = ""; }
            UpdateSelectedCount(); UpdateDshControls();
            bool pending = dshScan.trash.Any(t => t.state == "prepared" || t.state == "invalid");
            string summary = $"已载入 {projects.Count} 个项目 · {dshScan.rows.Count(r => r.kind == "主对话")} 个主对话 · {dshScan.rows.Count(r => r.kind == "子代理")} 个子代理 · 已归档 {dshScan.rows.Count(r => r.archived)} 条";
            if (dshScan.errors.Count > 0) { summary += $" · {dshScan.errors.Count} 项扫描异常（见导入与恢复记录）"; foreach (string error in dshScan.errors) AppendLog(error); }
            if (pending) summary += " · 存在未完成操作，请点击“恢复中断操作”";
            SetStatus(summary, pending || dshScan.errors.Count > 0);
            Find<Button>("DshHomeButton").ToolTip = DshService.Home;
        } catch (Exception e) { SetStatus(e.Message, true); AppDialog.ShowCompat(window, e.Message, "读取会话失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, null); }
    }
    private async Task ArchiveDshAsync(bool archive)
    {
        if (isBusy || !BackendContext.IsDsh) return;
        string[] ids = CurrentVisibleSessionTypeItems().Where(s => s.IsSelected && s.Archived != archive).Select(s => s.ThreadId).ToArray();
        if (ids.Length == 0) return;
        string verb = archive ? "归档" : "取消归档";
        if (!AppDialog.Confirm(window, verb, verb + "所选对话", "将" + verb + " " + ids.Length + " 条对话，并同步 DSH 原生归档分类。" + (archive ? "\n归档会取消置顶，保留原工作区位置。" : "\n对话回到原工作区位置。") + "\n\n请先完全退出 DSH。", AppDialogTone.Info, verb, "取消")) return;
        SetBusy(true, "正在" + verb + "……");
        try {
            string operation = archive ? "archive" : "unarchive";
            var p = await DshService.CallAsync<DshPlan>(new { action = "plan", home = DshService.Home, ids, operation });
            await DshService.CallAsync<DshChange>(new { action = "change", home = DshService.Home, ids, operation, p.token });
            SetBusy(false, null); await RefreshDataAsync();
        } catch (Exception e) { SetStatus(e.Message, true); AppDialog.ShowCompat(window, e.Message, verb + "失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, null); }
    }
    private async Task CreateDshPackageAsync(IList<BackupProjectSelection> selections, bool includeProjectFiles, string output)
    {
        if (isBusy) return;
        SetBusy(true, "正在创建 DSH 备份……");
        try {
            await Task.Run(() => DshPackages.Create(selections.Select(s => s.Project).ToList(), selections.SelectMany(s => s.Sessions).Select(s => s.ThreadId).ToArray(), includeProjectFiles, output));
            SetStatus("备份已创建：" + output, false);
            AppDialog.ShowCompat(window, output, "备份完成", MessageBoxButton.OK, MessageBoxImage.Information);
        } catch (Exception e) { SetStatus(e.Message, true); AppDialog.ShowCompat(window, e.Message, "备份失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false, null); }
    }
    private async Task LoadDshPackageSummaryAsync(string path)
    {
        try {
            var manifest = await Task.Run(() => DshPackages.ReadManifest(path));
            loadedManifest = manifest; loadedIsRawBundle = false; loadedPackagePath = Path.GetFullPath(path);
            int count = manifest.sessions?.Count ?? 0;
            packageSummaryText.Text = UiLanguage.T($"{manifest.projects.Count} 个项目 · {count} 条对话");
            packageProjectText.Text = string.Join("\n", manifest.projects.Select(p => p.source_project));
            restoreProjectFilesCheck.IsChecked = manifest.projects.Any(p => p.project_payload != null);
            targetPathBox.Text = manifest.projects.Count == 1 ? manifest.projects[0].source_project : SuggestProjectTarget(manifest);
            AppendLog("已选择：" + path);
        } catch (Exception e) { loadedManifest = null; loadedPackagePath = ""; packageSummaryText.Text = UiLanguage.T("无法读取迁移包"); packageProjectText.Text = e.Message; }
        UpdateProjectRestoreControls();
    }
    private async Task ImportDshPackageAsync(bool dryRun)
    {
        if (isBusy) return;
        string package = packagePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(package)) { AppDialog.ShowCompat(window, "请先选择迁移包。", "等待选择迁移包", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        string mode = copyModeRadio.IsChecked == true ? "copy" : "merge";
        string target = targetPathBox.Text.Trim(); bool map = mapPathCheck.IsChecked == true, files = ShouldRestoreProjectFiles();
        var conflict = CurrentProjectFileConflictMode();
        if (!dryRun && !AppDialog.Confirm(window, "开始导入", "导入 DSH 对话与项目", "导入目标：" + target + "\n\n" + (mode == "copy" ? "每条对话生成新编号。" : "在目标项目按同源编号检查历史；仅接受一致或前缀延续的历史。") + "\n归档分类一同保留。请先完全退出 DSH。", AppDialogTone.Warning, "开始导入", "取消")) return;
        SetBusy(true, dryRun ? "正在检查…" : "正在导入…"); BeginImportProgress(dryRun);
        try {
            string result = await Task.Run(() => DshPackages.Import(package, target, map, mode, files, conflict, dryRun));
            AppendLog(result); SetStatus(dryRun ? "检查通过，可以开始导入。" : "导入完成。", false);
            if (!dryRun) { SetBusy(false, null); await RefreshDataAsync(); AppDialog.ShowCompat(window, result, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information); }
        } catch (Exception e) { AppendLog(e.Message); SetStatus(e.Message, true); AppDialog.ShowCompat(window, e.Message, dryRun ? "检查失败" : "导入失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { EndImportProgress(); SetBusy(false, null); }
    }
}
