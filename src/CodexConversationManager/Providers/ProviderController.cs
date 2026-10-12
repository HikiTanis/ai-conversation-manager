using System;
using System.Windows;
using System.Windows.Controls;

namespace CodexConversationManager;

internal sealed partial class MainWindowController
{
    private void WireProviderEvents()
    {
        Find<RadioButton>("PrimaryProviderRadio").Content = "Codex";
        Find<RadioButton>("DshProviderRadio").Content = "DSH";
        Find<RadioButton>("PrimaryProviderRadio").IsChecked = !BackendContext.IsDsh;
        Find<RadioButton>("DshProviderRadio").IsChecked = BackendContext.IsDsh;
        Find<RadioButton>("PrimaryProviderRadio").Checked += delegate { SwitchProvider(false); };
        Find<RadioButton>("DshProviderRadio").Checked += delegate { SwitchProvider(true); };
        Find<Button>("DshHomeButton").Visibility = BackendContext.IsDsh ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("DshRecoveryButton").Visibility = BackendContext.IsDsh ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("ArchiveSelectedButton").Visibility = BackendContext.IsDsh ? Visibility.Visible : Visibility.Collapsed;
        Find<Button>("UnarchiveSelectedButton").Visibility = BackendContext.IsDsh ? Visibility.Visible : Visibility.Collapsed;
        window.Title = (UiLanguage.IsEnglish ? "Conversation Manager · " : "对话与项目管理器 · ") + BackendContext.Name;
        window.SizeChanged += delegate { UpdateProviderLayout(); };
    }
    private void UpdateProviderLayout()
    {
        bool compact = window.ActualHeight < 700;
        Find<RowDefinition>("BackupOptionsRow").Height = new GridLength(compact ? 106 : 118);
        Find<RowDefinition>("ProjectHeaderRow").MinHeight = compact ? 98 : 126;
        var header = Find<Border>("ProjectHeaderBorder");
        header.MinHeight = compact ? 98 : 126;
        header.Padding = compact ? new Thickness(18,7,18,7) : new Thickness(20,13,20,14);
        Find<RowDefinition>("SessionToolbarRow").Height = new GridLength(compact ? 92 : 110);
        Find<Grid>("SessionToolbarGrid").Margin = new Thickness(18,compact ? 8 : 13,18,compact ? 8 : 13);
        Find<RowDefinition>("SessionSearchRow").Height = new GridLength(compact ? 36 : 40);
        Find<RowDefinition>("SessionArchiveRow").Height = new GridLength(compact ? 40 : 44);
        Find<RowDefinition>("SessionFooterRow").Height = new GridLength(compact ? 64 : 82);
    }
    // Reuse the same window replacement pattern as the original language switch.
    // Constructing a fresh controller also clears selections, open previews and
    // staged import paths, so a command cannot reuse another provider's data.
    private void SwitchProvider(bool dsh)
    {
        if (BackendContext.IsDsh == dsh) return;
        if (isBusy) {
            Find<RadioButton>(BackendContext.IsDsh ? "DshProviderRadio" : "PrimaryProviderRadio").IsChecked = true;
            return;
        }
        bool previous = BackendContext.IsDsh;
        MainWindowController replacementController = null;
        try {
            BackendContext.IsDsh = dsh;
            replacementController = new MainWindowController();
            Window replacement = replacementController.Window;
            replacement.Width = Math.Max(replacement.MinWidth, window.ActualWidth);
            replacement.Height = Math.Max(replacement.MinHeight, window.ActualHeight);
            replacement.WindowStartupLocation = WindowStartupLocation.Manual;
            replacement.Left = window.Left; replacement.Top = window.Top;
            if (importPage.Visibility == Visibility.Visible) replacementController.ShowImportPage();
            Application.Current.MainWindow = replacement;
            replacement.Show();
            if (window.WindowState == WindowState.Maximized) replacement.WindowState = WindowState.Maximized;
            BackendContext.Save();
            window.Close();
        } catch (Exception e) {
            BackendContext.IsDsh = previous;
            Application.Current.MainWindow = window;
            replacementController?.Window.Close();
            Find<RadioButton>(previous ? "DshProviderRadio" : "PrimaryProviderRadio").IsChecked = true;
            AppDialog.ShowCompat(window, e.Message, "切换数据源失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
