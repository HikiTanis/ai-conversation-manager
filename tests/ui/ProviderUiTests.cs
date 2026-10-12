using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexConversationManager;

internal static class ProviderUiTests
{
    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args[Array.IndexOf(args, "--provider-test") + 1]);
        Directory.CreateDirectory(output);
        int li = Array.IndexOf(args, "--language"); UiLanguage.Initialize(li < 0 ? "zh-CN" : args[li + 1]);
        BackendContext.IsDsh = false;
        CodexDesktopTaskCache.UserDataRootOverride = Path.Combine(CodexCatalog.ResolveCodexHome(), "fixture-desktop-cache");
        ConversationIndexMaintenance.LogRootOverride = Path.Combine(CodexCatalog.ResolveCodexHome(), "fixture-logs");
        Environment.SetEnvironmentVariable("CODEX_MIGRATOR_SKIP_SNAPSHOT_MAINTENANCE", "1");
        Environment.SetEnvironmentVariable("CODEX_MIGRATOR_SKIP_LEGACY_CCT_MAINTENANCE", "1");
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        try {
            var first = new MainWindowController(); app.MainWindow = first.Window;
            bool started = false;
            first.Window.ContentRendered += async delegate {
                if (started) return; started = true;
                try {
                    await first.InitialLoadTask;
                    first.AssertProviderStateForTest(false);
                    Render(first.Window, Path.Combine(output, "codex.png"));
                    first.SeedProviderStateForTest();
                    first.AssertSwitchBlockedForTest();
                    var dsh = await first.SwitchProviderForTest(true);
                    dsh.AssertProviderStateForTest(true);
                    Render(dsh.Window, Path.Combine(output, "dsh.png"));
                    dsh.Window.Width = UiLanguage.IsEnglish ? 1040 : 900; dsh.Window.Height = 620;
                    Render(dsh.Window, Path.Combine(output, "dsh-compact.png"));
                    dsh.SeedProviderStateForTest();
                    var codex = await dsh.SwitchProviderForTest(false);
                    codex.AssertProviderStateForTest(false);
                    // A second round also verifies handlers aren't duplicated.
                    dsh = await codex.SwitchProviderForTest(true);
                    dsh.AssertProviderStateForTest(true);
                    codex = await dsh.SwitchProviderForTest(false);
                    codex.AssertProviderStateForTest(false);
                    File.WriteAllText(Path.Combine(output,"result.txt"),"PASS: Codex -> DSH -> Codex twice; isolated selection/import state, format filters, archive classification, busy guard, original layout.");
                    codex.Window.Close();
                } catch(Exception e) { File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString()); app.Shutdown(1); }
            };
            return app.Run(first.Window);
        } catch(Exception e) { File.WriteAllText(Path.Combine(output,"error.txt"),e.ToString()); return 1; }
    }
    private static void Render(Window window, string path)
    {
        window.UpdateLayout(); var b=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);b.Render(window);
        var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(b));using(var f=File.Create(path))e.Save(f);
    }
}
internal sealed partial class MainWindowController
{
    public async Task<MainWindowController> SwitchProviderForTest(bool dsh)
    {
        Find<RadioButton>(dsh ? "DshProviderRadio" : "PrimaryProviderRadio").IsChecked = true;
        var c = Application.Current.MainWindow.Tag as MainWindowController;
        if (c == null || c == this) throw new Exception("Provider switch did not replace controller.");
        await c.InitialLoadTask; await Task.Delay(100); return c;
    }
    public void AssertSwitchBlockedForTest()
    {
        bool before = BackendContext.IsDsh;
        SetBusy(true, null); SwitchProvider(!before);
        if (BackendContext.IsDsh != before || Find<RadioButton>("DshProviderRadio").IsEnabled) throw new Exception("Busy provider switch was allowed.");
        SetBusy(false,null);
    }
    public void SeedProviderStateForTest()
    {
        projects.SelectMany(p=>p.Sessions).First().IsSelected = true;
        searchBox.Text = "previous provider search";
        packagePathBox.Text = "previous-provider-package";
        targetPathBox.Text = "previous-provider-target";
        loadedManifest = new PackManifest { engine = BackendContext.Name };
        loadedPackagePath = "previous-provider-package";
    }
    public void AssertProviderStateForTest(bool dsh)
    {
        if (BackendContext.IsDsh != dsh || isBusy) throw new Exception("Wrong backend or loading did not finish.");
        var sessions = projects.SelectMany(p=>p.Sessions).ToList();
        if (sessions.Count != 3 || sessions.Any(s => !Path.GetFullPath(s.SessionPath).StartsWith(Path.GetFullPath(BackendContext.Home) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new Exception("Sessions leaked across providers or failed to load.");
        if (sessions.Any(s=>s.IsSelected) || searchBox.Text.Length>0 || packagePathBox.Text.Length>0 || targetPathBox.Text.Length>0 || loadedManifest != null || !string.IsNullOrEmpty(loadedPackagePath)) throw new Exception("Selection/import state leaked across providers.");
        if (BackupPackageFormat.ConversationExtension != (dsh ? ".dshchat" : ".codexchat") || BackupPackageFormat.IsSupportedImport(dsh ? "foreign.codexchat" : "foreign.dshchat")) throw new Exception("Package filter uses wrong provider.");
        if (Convert.ToString(Find<RadioButton>("PrimaryProviderRadio").Content) != "Codex" || Convert.ToString(Find<RadioButton>("DshProviderRadio").Content) != "DSH") throw new Exception("Provider label changed with branding.");
        if (Find<Button>("ArchiveSelectedButton").Visibility != (dsh ? Visibility.Visible : Visibility.Collapsed)) throw new Exception("Provider action availability is wrong.");
        if (ProviderStorage.ReadTrash().Count != 0) throw new Exception("Unexpected fixture trash entries.");
        projectList.SelectedItem = projects.First(p=>p.Sessions.Count>0);
        mainSessionsTabRadio.IsChecked = true;
        Find<RadioButton>("ArchiveDoneRadio").IsChecked = true;
        if (CurrentVisibleSessionTypeItems().Count != 1 || CurrentVisibleSessionTypeItems().Any(s=>!s.Archived)) throw new Exception("Archive filter failed.");
        Find<RadioButton>("ArchiveActiveRadio").IsChecked = true;
        if (CurrentVisibleSessionTypeItems().Count != 1 || CurrentVisibleSessionTypeItems().Any(s=>s.Archived)) throw new Exception("Active filter failed.");
        Find<RadioButton>("ArchiveAllRadio").IsChecked = true;
    }
}
