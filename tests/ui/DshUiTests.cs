using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexConversationManager;

internal static class DshUiTests
{
    [STAThread]
    public static int Run(string[] args)
    {
        string output = null;
        try {
            int languageAt = Array.IndexOf(args, "--language");
            UiLanguage.Initialize(languageAt >= 0 && languageAt + 1 < args.Length ? args[languageAt + 1] : null);
            bool test = args.Length >= 2 && (args[0] == "--ui-test" || args[0] == "--render-test");
            if (test) { output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output); }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var controller = new MainWindowController(); app.MainWindow = controller.Window;
            if (test) {
                bool started = false;
                controller.Window.ContentRendered += async delegate {
                    if (started) return; started = true;
                    try {
                        await controller.InitialLoadTask; await Task.Delay(250);
                        if (args[0] == "--ui-test") {
                            Check(controller.IsConversationBackupDefaultForTest(), "默认仅对话模式");
                            Check(DialogUi.VerifyThemeForTest(), "原版删除/回收站对话框主题");
                            Check(controller.SelectProjectForTest("演示项目"), "项目列表");
                            Check(controller.TestDshArchiveFilters(), "归档分类筛选");
                            Check(controller.TestMainSelectionToggleForTest(), "主对话全选/全不选");
                            Check(controller.ShowSubagentViewForTest("演示项目"), "子代理列表");
                            Check(controller.TestSubagentSelectionToggleForTest(), "子代理全选/全不选");
                            Check(controller.TestSessionTypeSwitchSelectionStateForTest(), "主对话/子代理选择隔离");
                            controller.ShowMainSessionViewForTest("演示项目");
                        }
                        Render(controller.Window, Path.Combine(output, "manage.png"));
                        if (args[0] == "--ui-test") {
                            Check(await controller.ShowFirstConversationForTest("演示项目"), "原版查看按钮与预览浮层");
                            await Task.Delay(150);
                            Check(controller.TestConversationAutomaticLayoutForTest(), "预览自动布局");
                            Check(controller.TestConversationFollowsWindowResizeForTest(), "预览随窗口缩放");
                            Render(controller.Window, Path.Combine(output, "conversation.png"));
                            ((System.Windows.Controls.Button)controller.Window.FindName("ConversationCloseButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            controller.ShowImportForTest(); Render(controller.Window, Path.Combine(output, "import.png"));
                            Check(controller.TestImportLayoutForTest(), "导入布局");
                            var groups = DshService.ToProjects(DshService.Call<DshScan>(new { action = "scan", home = DshService.Home }));
                            var group = groups.First(g => g.DisplayName == "演示项目");
                            string fixtureProject = Path.Combine(output, "project-source");
                            Directory.CreateDirectory(fixtureProject); File.WriteAllText(Path.Combine(fixtureProject, "sample.txt"), "项目文件测试");
                            group.ProjectPath = fixtureProject;
                            foreach (bool projectFiles in new[] { false, true }) {
                                string package = Path.Combine(output, projectFiles ? "sample.dshproject" : "sample.dshchat");
                                DshPackages.Create(new[] { group }, group.Sessions.Select(s => s.ThreadId).ToArray(), projectFiles, package);
                                Check(DshPackages.ReadManifest(package).native_sessions != null, "备份包读取");
                                DshPackages.Import(package, Path.Combine(output, "restore-target"), true, "merge", projectFiles, ProjectFileConflictMode.RequireEmpty, true);
                            }
                            controller.Window.Width = UiLanguage.IsEnglish ? 1040 : 900; controller.Window.Height = 620;
                            controller.Window.UpdateLayout(); Render(controller.Window, Path.Combine(output, "compact.png"));
                        }
                        File.WriteAllText(Path.Combine(output, "result.txt"), "PASS: original UI + DSH adapter; " + UiLanguage.Code);
                        controller.EndBusyForTest(); controller.Window.Close();
                    } catch (Exception e) { File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString()); controller.EndBusyForTest(); app.Shutdown(1); }
                };
            }
            return app.Run(controller.Window);
        } catch (Exception e) {
            if (output != null) File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString());
            else MessageBox.Show(e.Message, "DeepSeek 对话与项目管理器", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
    private static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("界面验证失败：" + name); }
    private static void Render(Window window, string file)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
    }
}

internal sealed partial class MainWindowController
{
    public bool TestDshArchiveFilters()
    {
        projectList.SelectedItem = projects.FirstOrDefault(p => p.DisplayName == "演示项目");
        mainSessionsTabRadio.IsChecked = true;
        Find<RadioButton>("ArchiveActiveRadio").IsChecked = true;
        if (CurrentVisibleSessionTypeItems().Count != 1 || CurrentVisibleSessionTypeItems().Any(s => s.Archived)) return false;
        Find<RadioButton>("ArchiveDoneRadio").IsChecked = true;
        if (CurrentVisibleSessionTypeItems().Count != 1 || CurrentVisibleSessionTypeItems().Any(s => !s.Archived)) return false;
        Find<RadioButton>("ArchiveAllRadio").IsChecked = true;
        return CurrentVisibleSessionTypeItems().Count == 2;
    }
}
