using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace CodexConversationManager;

internal sealed partial class MainWindowController
{
	private const double ConversationPreviewInset = 16.0;

	private sealed class BackupProjectSelection
	{
		public ProjectGroup Project { get; set; }

		public List<SessionInfo> Sessions { get; set; }
	}

	private sealed class ImportProjectContext
	{
		public PackProject Project { get; set; }

		public string TargetPath { get; set; }

		public List<string> BundlePaths { get; } = new List<string>();

		public List<ConversationImportPlan> ImportPlans { get; } = new List<ConversationImportPlan>();

		public string ProjectArchivePath { get; set; }

		public ProjectRestorePlan Plan { get; set; }

		public ProjectRestoreResult RestoreResult { get; set; }
	}

	private sealed class ConversationNavigationItem
	{
		public int MessageIndex { get; set; }

		public ConversationMessage UserMessage { get; set; }

		public ConversationMessage ResponseMessage { get; set; }

		public System.Windows.Controls.Button Button { get; set; }

		public Border Marker { get; set; }
	}

	private readonly Window window;

	private readonly System.Windows.Controls.TextBox backupFolderBox;

	private readonly System.Windows.Controls.RadioButton projectBackupModeRadio;

	private readonly System.Windows.Controls.RadioButton conversationBackupModeRadio;

	private readonly TextBlock backupModeHelpText;

	private readonly FrameworkElement projectSelectionTools;

	private readonly FrameworkElement sessionSelectionTools;

	private readonly TextBlock projectPaneTitle;

	private readonly TextBlock projectPaneSubtitle;

	private readonly TextBlock sessionModeHint;

	private readonly TextBlock selectionHelpText;

	private readonly System.Windows.Controls.ListBox projectList;

	private readonly System.Windows.Controls.ListBox sessionList;

	private readonly System.Windows.Controls.TextBox searchBox;

	private readonly System.Windows.Controls.RadioButton mainSessionsTabRadio;

	private readonly System.Windows.Controls.RadioButton subagentSessionsTabRadio;

	private readonly TextBlock projectTitleText;

	private readonly TextBlock projectPathText;

	private readonly TextBlock projectMetaText;

	private readonly TextBlock projectSizeText;

	private readonly TextBlock selectedCountText;

	private readonly TextBlock emptySessionsText;

	private readonly TextBlock statusText;

	private readonly Ellipse statusDot;

	private readonly System.Windows.Controls.ProgressBar busyProgress;

	private readonly Grid backupPage;

	private readonly Grid importPage;

	private readonly Grid importWorkflowGrid;

	private readonly Border importActionBar;

	private readonly Border backupTabIndicator;

	private readonly Border importTabIndicator;

	private readonly System.Windows.Controls.Button languageButton;
	private readonly System.Windows.Controls.Button closeButton;


	private readonly System.Windows.Controls.Button backupTabButton;

	private readonly System.Windows.Controls.Button importTabButton;

	private readonly System.Windows.Controls.Button refreshButton;

	private readonly System.Windows.Controls.Button trashButton;

	private readonly System.Windows.Controls.Button browseBackupFolderButton;

	private readonly System.Windows.Controls.Button selectAllProjectsButton;

	private readonly System.Windows.Controls.Button clearProjectsButton;

	private readonly System.Windows.Controls.Button toggleSessionSelectionButton;

	private readonly System.Windows.Controls.Button deleteSelectedSessionsButton;

	private readonly System.Windows.Controls.Button copyProjectPathButton;

	private readonly System.Windows.Controls.Button backupSelectedButton;

	private readonly System.Windows.Controls.Button backupProjectFilesButton;

	private readonly System.Windows.Controls.Button inspectButton;

	private readonly System.Windows.Controls.Button importButton;

	private readonly System.Windows.Controls.Button browsePackageButton;

	private readonly System.Windows.Controls.Button browseTargetButton;

	private readonly Border importProgressPanel;

	private readonly TextBlock importStageText;

	private readonly TextBlock importStageDetailText;

	private readonly TextBlock importElapsedText;

	private readonly System.Windows.Controls.ProgressBar importStageProgress;

	private readonly System.Windows.Controls.TextBox packagePathBox;

	private readonly System.Windows.Controls.TextBox targetPathBox;

	private readonly TextBlock targetPathLabel;

	private readonly TextBlock targetPathHelpText;

	private readonly System.Windows.Controls.CheckBox mapPathCheck;

	private readonly Border projectRestorePanel;

	private readonly System.Windows.Controls.CheckBox restoreProjectFilesCheck;

	private readonly System.Windows.Controls.ComboBox projectConflictCombo;

	private readonly TextBlock projectRestoreHelpText;

	private readonly System.Windows.Controls.RadioButton mergeModeRadio;


	private readonly System.Windows.Controls.RadioButton copyModeRadio;

	private readonly TextBlock importModeHelpText;

	private readonly TextBlock packageSummaryText;

	private readonly TextBlock packageProjectText;

	private readonly System.Windows.Controls.TextBox importLog;

	private readonly Grid conversationOverlay;

	private readonly TextBlock conversationTitleText;

	private readonly TextBlock conversationMetaText;

	private readonly System.Windows.Controls.ListBox conversationList;

	private readonly Border conversationNavigationHost;

	private readonly ScrollViewer conversationNavigationScroller;

	private readonly StackPanel conversationNavigationRail;

	private readonly Popup conversationNavigationPreviewPopup;

	private readonly TextBlock conversationNavigationPreviewTitle;

	private readonly TextBlock conversationNavigationPreviewResponse;

	private readonly Canvas conversationCanvas;

	private readonly FrameworkElement conversationDialogHost;

	private readonly System.Windows.Controls.Button conversationMinimizeButton;

	private readonly System.Windows.Controls.Button conversationCloseButton;

	private readonly System.Windows.Controls.Button copyThreadIdButton;

	private readonly FrameworkElement maximizeGlyph;

	private readonly FrameworkElement restoreGlyph;

	private List<ProjectGroup> projects = new List<ProjectGroup>();

	private ProjectGroup selectedProject;

	private ICollectionView sessionView;

	private PackManifest loadedManifest;

	private bool loadedIsRawBundle;

	private string loadedPackagePath = string.Empty;

	private bool isBusy;

	private DateTime importStartedAt;


	private bool projectBackupMode;

	private bool showSubagentSessions;

	private string previewedThreadId = string.Empty;

	private IList<ConversationMessage> previewMessages = Array.Empty<ConversationMessage>();

	private ScrollViewer conversationScrollViewer;

	private int activeConversationMessageIndex = -1;

	private readonly List<ConversationNavigationItem> conversationNavigationItems = new List<ConversationNavigationItem>();

	private int activeConversationNavigationIndex = -1;

	private bool conversationNavigationScrubbing;

	private bool conversationNavigationMoved;

	private Point conversationNavigationPressPoint;

	private ConversationNavigationItem conversationNavigationPressedItem;

	private int conversationPreviewRequestVersion;

	private bool conversationDialogInitialized;

	private readonly TaskCompletionSource<bool> initialLoadCompletion = new TaskCompletionSource<bool>();

	public Window Window => window;

	public Task InitialLoadTask => initialLoadCompletion.Task;

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	public MainWindowController()
	{
		string text = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexConversationManager.xaml");
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("界面文件不存在。", text);
		}
		string localizedXaml = UiLanguage.LoadXaml(text);
		window = (Window)XamlReader.Parse(localizedXaml.Replace("Icon=\"assets/app-icon.ico\"", ""));
		window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets/app-icon.ico")));
		WindowWorkArea.Attach(window);
		window.MinWidth = UiLanguage.IsEnglish ? 1040.0 : 900.0;
		window.Tag = this;
		backupFolderBox = Find<System.Windows.Controls.TextBox>("BackupFolderBox");
		projectBackupModeRadio = Find<System.Windows.Controls.RadioButton>("ProjectBackupModeRadio");
		conversationBackupModeRadio = Find<System.Windows.Controls.RadioButton>("ConversationBackupModeRadio");
		backupModeHelpText = Find<TextBlock>("BackupModeHelpText");
		projectSelectionTools = Find<FrameworkElement>("ProjectSelectionTools");
		sessionSelectionTools = Find<FrameworkElement>("SessionSelectionTools");
		projectPaneTitle = Find<TextBlock>("ProjectPaneTitle");
		projectPaneSubtitle = Find<TextBlock>("ProjectPaneSubtitle");
		sessionModeHint = Find<TextBlock>("SessionModeHint");
		selectionHelpText = Find<TextBlock>("SelectionHelpText");
		projectList = Find<System.Windows.Controls.ListBox>("ProjectList");
		sessionList = Find<System.Windows.Controls.ListBox>("SessionList");
		searchBox = Find<System.Windows.Controls.TextBox>("SearchBox");
		mainSessionsTabRadio = Find<System.Windows.Controls.RadioButton>("MainSessionsTabRadio");
		subagentSessionsTabRadio = Find<System.Windows.Controls.RadioButton>("SubagentSessionsTabRadio");
		projectTitleText = Find<TextBlock>("ProjectTitleText");
		projectPathText = Find<TextBlock>("ProjectPathText");
		projectMetaText = Find<TextBlock>("ProjectMetaText");
		projectSizeText = Find<TextBlock>("ProjectSizeText");
		selectedCountText = Find<TextBlock>("SelectedCountText");
		emptySessionsText = Find<TextBlock>("EmptySessionsText");
		statusText = Find<TextBlock>("StatusText");
		statusDot = Find<Ellipse>("StatusDot");
		busyProgress = Find<System.Windows.Controls.ProgressBar>("BusyProgress");
		backupPage = Find<Grid>("BackupPage");
		importPage = Find<Grid>("ImportPage");
		importWorkflowGrid = Find<Grid>("ImportWorkflowGrid");
		importActionBar = Find<Border>("ImportActionBar");
		backupTabIndicator = Find<Border>("BackupTabIndicator");
		importTabIndicator = Find<Border>("ImportTabIndicator");
		backupTabButton = Find<System.Windows.Controls.Button>("BackupTabButton");
		importTabButton = Find<System.Windows.Controls.Button>("ImportTabButton");
		refreshButton = Find<System.Windows.Controls.Button>("RefreshButton");
		trashButton = Find<System.Windows.Controls.Button>("TrashButton");
		browseBackupFolderButton = Find<System.Windows.Controls.Button>("BrowseBackupFolderButton");
		selectAllProjectsButton = Find<System.Windows.Controls.Button>("SelectAllProjectsButton");
		languageButton = Find<System.Windows.Controls.Button>("LanguageButton");
		closeButton = Find<System.Windows.Controls.Button>("CloseButton");
		clearProjectsButton = Find<System.Windows.Controls.Button>("ClearProjectsButton");
		toggleSessionSelectionButton = Find<System.Windows.Controls.Button>("ToggleSessionSelectionButton");
		deleteSelectedSessionsButton = Find<System.Windows.Controls.Button>("DeleteSelectedSessionsButton");
		copyProjectPathButton = Find<System.Windows.Controls.Button>("CopyProjectPathButton");
		backupSelectedButton = Find<System.Windows.Controls.Button>("BackupSelectedButton");
		backupProjectFilesButton = Find<System.Windows.Controls.Button>("BackupProjectFilesButton");
		inspectButton = Find<System.Windows.Controls.Button>("InspectButton");
		importButton = Find<System.Windows.Controls.Button>("ImportButton");
		packagePathBox = Find<System.Windows.Controls.TextBox>("PackagePathBox");
		browsePackageButton = Find<System.Windows.Controls.Button>("BrowsePackageButton");
		browseTargetButton = Find<System.Windows.Controls.Button>("BrowseTargetButton");
		importProgressPanel = Find<Border>("ImportProgressPanel");
		importStageText = Find<TextBlock>("ImportStageText");
		importStageDetailText = Find<TextBlock>("ImportStageDetailText");
		importElapsedText = Find<TextBlock>("ImportElapsedText");
		importStageProgress = Find<System.Windows.Controls.ProgressBar>("ImportStageProgress");
		targetPathBox = Find<System.Windows.Controls.TextBox>("TargetPathBox");
		targetPathLabel = Find<TextBlock>("TargetPathLabel");
		targetPathHelpText = Find<TextBlock>("TargetPathHelpText");
		mapPathCheck = Find<System.Windows.Controls.CheckBox>("MapPathCheck");
		projectRestorePanel = Find<Border>("ProjectRestorePanel");
		restoreProjectFilesCheck = Find<System.Windows.Controls.CheckBox>("RestoreProjectFilesCheck");
		projectConflictCombo = Find<System.Windows.Controls.ComboBox>("ProjectConflictCombo");
		projectRestoreHelpText = Find<TextBlock>("ProjectRestoreHelpText");
		mergeModeRadio = Find<System.Windows.Controls.RadioButton>("MergeModeRadio");
		copyModeRadio = Find<System.Windows.Controls.RadioButton>("CopyModeRadio");
		importModeHelpText = Find<TextBlock>("ImportModeHelpText");
		packageSummaryText = Find<TextBlock>("PackageSummaryText");
		packageProjectText = Find<TextBlock>("PackageProjectText");
		importLog = Find<System.Windows.Controls.TextBox>("ImportLog");
		conversationOverlay = Find<Grid>("ConversationOverlay");
		conversationTitleText = Find<TextBlock>("ConversationTitleText");
		conversationMetaText = Find<TextBlock>("ConversationMetaText");
		conversationList = Find<System.Windows.Controls.ListBox>("ConversationList");
		conversationNavigationHost = Find<Border>("ConversationNavigationHost");
		conversationNavigationScroller = Find<ScrollViewer>("ConversationNavigationScroller");
		conversationNavigationRail = Find<StackPanel>("ConversationNavigationRail");
		conversationNavigationPreviewPopup = Find<Popup>("ConversationNavigationPreviewPopup");
		conversationNavigationPreviewTitle = Find<TextBlock>("ConversationNavigationPreviewTitle");
		conversationNavigationPreviewResponse = Find<TextBlock>("ConversationNavigationPreviewResponse");
		conversationCanvas = Find<Canvas>("ConversationCanvas");
		conversationDialogHost = Find<FrameworkElement>("ConversationDialogHost");
		conversationMinimizeButton = Find<System.Windows.Controls.Button>("ConversationMinimizeButton");
		conversationCloseButton = Find<System.Windows.Controls.Button>("ConversationCloseButton");
		copyThreadIdButton = Find<System.Windows.Controls.Button>("CopyThreadIdButton");
		maximizeGlyph = Find<FrameworkElement>("MaximizeGlyph");
		restoreGlyph = Find<FrameworkElement>("RestoreGlyph");
		backupFolderBox.Text = DefaultBackupFolder();
		languageButton.Content = UiLanguage.IsEnglish ? "中文" : "EN";
		languageButton.ToolTip = UiLanguage.IsEnglish ? "Switch to Chinese" : "切换到英文";
		WireEvents();
		WireDshEvents();
		WireProviderEvents();
		ShowBackupPage();
	}

	public bool SelectProjectForTest(string displayName)
	{
		ProjectGroup projectGroup = string.IsNullOrWhiteSpace(displayName)
			? projects.FirstOrDefault((ProjectGroup x) => x.MainCount > 0 && x.InternalCount > 0) ?? projects.FirstOrDefault((ProjectGroup x) => x.MainCount > 0) ?? projects.FirstOrDefault()
			: projects.FirstOrDefault((ProjectGroup x) => string.Equals(x.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
		if (projectGroup == null)
		{
			return false;
		}
		projectList.SelectedItem = projectGroup;
		projectList.ScrollIntoView(projectGroup);
		return ReferenceEquals(selectedProject, projectGroup) && string.Equals(projectTitleText.Text, projectGroup.DisplayName, StringComparison.OrdinalIgnoreCase);
	}

	public bool ShowSubagentViewForTest(string displayName)
	{
		if (!SelectProjectForTest(displayName))
		{
			return false;
		}
		subagentSessionsTabRadio.IsChecked = true;
		UpdateSessionTypeView();
		List<SessionInfo> visible = sessionView?.Cast<object>().OfType<SessionInfo>().ToList() ?? new List<SessionInfo>();
		return visible.Count > 0 && visible.All((SessionInfo session) => session.IsSubagent) && visible.All((SessionInfo session) => !string.IsNullOrWhiteSpace(session.DisplayPath) && session.SizeBytes > 0L);
	}

	public bool IsConversationBackupDefaultForTest()
	{
		return conversationBackupModeRadio.IsChecked == true &&
			projectBackupModeRadio.IsChecked != true &&
			!projectBackupMode &&
			backupSelectedButton.Visibility == Visibility.Visible &&
			backupProjectFilesButton.Visibility == Visibility.Collapsed &&
			projectSelectionTools.Visibility == Visibility.Collapsed &&
			string.Equals(Convert.ToString(projectList.Tag), "Conversation", StringComparison.Ordinal);
	}

	public bool ShowMainSessionViewForTest(string displayName)
	{
		if (!SelectProjectForTest(displayName))
		{
			return false;
		}
		mainSessionsTabRadio.IsChecked = true;
		UpdateSessionTypeView();
		List<SessionInfo> visible = sessionView?.Cast<object>().OfType<SessionInfo>().ToList() ?? new List<SessionInfo>();
		return visible.Count > 0 && visible.All((SessionInfo session) => !session.IsSubagent);
	}

	public bool TestMainSelectionToggleForTest()
	{
		if (showSubagentSessions || !TestCurrentSessionSelectionToggleForTest())
		{
			return false;
		}
		List<SessionInfo> sessions = CurrentSessionTypeItems();
		foreach (SessionInfo session in sessions)
		{
			session.IsSelected = false;
		}
		UpdateSessionSelectionControls();
		ToggleSessionSelection();
		string projectFolderTerm = UiLanguage.IsEnglish ? "folder" : "项目目录";
		bool projectOptionAdvertised = sessions.Count > 0 &&
			sessions.All((SessionInfo session) => session.IsSelected) &&
			(sessionModeHint.Text ?? string.Empty).IndexOf(projectFolderTerm, StringComparison.OrdinalIgnoreCase) >= 0 &&
			(Convert.ToString(deleteSelectedSessionsButton.ToolTip) ?? string.Empty).IndexOf(projectFolderTerm, StringComparison.OrdinalIgnoreCase) >= 0;
		ToggleSessionSelection();
		return projectOptionAdvertised;
	}

	public bool TestSubagentSelectionToggleForTest()
	{
		return showSubagentSessions && TestCurrentSessionSelectionToggleForTest();
	}

	public bool TestFilteredSelectionStateForTest()
	{
		List<SessionInfo> sessions = CurrentSessionTypeItems();
		if (sessions.Count == 0)
		{
			return false;
		}
		foreach (SessionInfo session in sessions)
		{
			session.IsSelected = false;
		}
		sessions[0].IsSelected = true;
		UpdateSessionSelectionControls();
		string originalSearch = searchBox.Text;
		searchBox.Text = "__no_matching_conversation_for_selection_test__";
		bool hiddenState = CurrentVisibleSessionTypeItems().Count == 0 &&
			!deleteSelectedSessionsButton.IsEnabled &&
			!toggleSessionSelectionButton.IsEnabled &&
			(Convert.ToString(deleteSelectedSessionsButton.Content) ?? string.Empty).IndexOf("(1)", StringComparison.Ordinal) < 0;
		searchBox.Text = originalSearch;
		bool restoredState = CurrentVisibleSessionTypeItems().Contains(sessions[0]) &&
			deleteSelectedSessionsButton.IsEnabled &&
			(Convert.ToString(deleteSelectedSessionsButton.Content) ?? string.Empty).IndexOf("1", StringComparison.Ordinal) >= 0;
		return hiddenState && restoredState;
	}

	public bool TestSessionTypeSwitchSelectionStateForTest()
	{
		if (selectedProject == null || selectedProject.MainCount == 0 || selectedProject.InternalCount == 0)
		{
			return false;
		}
		foreach (SessionInfo session in selectedProject.Sessions)
		{
			session.IsSelected = false;
		}
		mainSessionsTabRadio.IsChecked = true;
		SessionInfo main = CurrentVisibleSessionTypeItems().FirstOrDefault();
		if (main == null)
		{
			return false;
		}
		main.IsSelected = true;
		UpdateSessionSelectionControls();
		subagentSessionsTabRadio.IsChecked = true;
		bool subagentState = showSubagentSessions &&
			CurrentVisibleSessionTypeItems().All(session => session.IsSubagent) &&
			!deleteSelectedSessionsButton.IsEnabled;
		mainSessionsTabRadio.IsChecked = true;
		bool mainState = !showSubagentSessions &&
			CurrentVisibleSessionTypeItems().Contains(main) &&
			deleteSelectedSessionsButton.IsEnabled &&
			(Convert.ToString(deleteSelectedSessionsButton.Content) ?? string.Empty).IndexOf("1", StringComparison.Ordinal) >= 0;
		return subagentState && mainState;
	}

	private bool TestCurrentSessionSelectionToggleForTest()
	{
		List<SessionInfo> sessions = CurrentSessionTypeItems();
		if (sessions.Count == 0)
		{
			return false;
		}
		foreach (SessionInfo session in sessions)
		{
			session.IsSelected = false;
		}
		UpdateSessionSelectionControls();
		ToggleSessionSelection();
		bool allSelected = sessions.All((SessionInfo session) => session.IsSelected) && string.Equals(Convert.ToString(toggleSessionSelectionButton.Content), UiLanguage.T("全不选"), StringComparison.Ordinal);
		ToggleSessionSelection();
		bool noneSelected = sessions.All((SessionInfo session) => !session.IsSelected) && string.Equals(Convert.ToString(toggleSessionSelectionButton.Content), UiLanguage.T("全选"), StringComparison.Ordinal);
		sessions[0].IsSelected = true;
		UpdateSessionSelectionControls();
		bool selectedDeleteReady = deleteSelectedSessionsButton.IsEnabled && (Convert.ToString(deleteSelectedSessionsButton.Content) ?? string.Empty).IndexOf("1", StringComparison.Ordinal) >= 0;
		return allSelected && noneSelected && selectedDeleteReady;
	}

	public async Task<bool> ShowFirstSubagentConversationForTest(string displayName)
	{
		if (!ShowSubagentViewForTest(displayName))
		{
			return false;
		}
		sessionList.UpdateLayout();
		SessionInfo session = sessionView.Cast<object>().OfType<SessionInfo>().FirstOrDefault();
		if (session == null)
		{
			return false;
		}
		sessionList.ScrollIntoView(session);
		sessionList.UpdateLayout();
		ListBoxItem item = sessionList.ItemContainerGenerator.ContainerFromItem(session) as ListBoxItem;
		System.Windows.Controls.Button viewButton = FindNamedButton(item, "ViewSessionButton");
		if (viewButton == null)
		{
			return false;
		}
		viewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, viewButton));
		for (int i = 0; i < 40; i++)
		{
			if (conversationOverlay.Visibility == Visibility.Visible && conversationList.ItemsSource != null && (conversationMetaText.Text ?? string.Empty).IndexOf(UiLanguage.IsEnglish ? "Loading" : "正在", StringComparison.OrdinalIgnoreCase) < 0)
			{
				await window.Dispatcher.InvokeAsync(delegate { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
				return true;
			}
			await Task.Delay(50);
		}
		return false;
	}

	public async Task<bool> WaitForSelectedProjectStorageForTest()
	{
		for (int i = 0; i < 80; i++)
		{
			if (selectedProject != null && selectedProject.StorageScanStarted && (selectedProject.ProjectStorageSummary ?? string.Empty).IndexOf(UiLanguage.IsEnglish ? "measuring" : "正在统计", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return true;
			}
			await Task.Delay(50);
		}
		return false;
	}

	public void ShowImportForTest()
	{
		ShowImportPage();
	}

	public void ShowImportProgressForTest()
	{
		ShowImportPage();
		BeginImportProgress(dryRun: false);
		SetBusy(busy: true, "正在恢复项目与对话……");
		UpdateImportStage("3 / 4 · 导入对话", "正在导入第 2/4 个对话包；文件处理在后台执行，界面仍可响应。");
	}

	public void EndBusyForTest()
	{
		SetBusy(busy: false, null);
	}

	public bool TestImportLayoutForTest()
	{
		window.UpdateLayout();
		bool projectControlsVisible = projectConflictCombo.IsVisible;
		if (projectControlsVisible)
		{
			projectConflictCombo.BringIntoView();
			window.UpdateLayout();
			projectConflictCombo.ApplyTemplate();
		}
		importWorkflowGrid.UpdateLayout();
		Point buttonBottom = importButton.TranslatePoint(new Point(0.0, importButton.ActualHeight), importActionBar);
		bool actionButtonsFit = importActionBar.ActualHeight >= 72.0 &&
			inspectButton.ActualHeight >= 37.0 &&
			importButton.ActualHeight >= 37.0 &&
			buttonBottom.Y <= importActionBar.ActualHeight - importActionBar.Padding.Bottom + 0.5;
		bool customConflictField = !projectControlsVisible || (projectConflictCombo.ActualHeight >= 39.0 &&
			projectConflictCombo.Template != null &&
			projectConflictCombo.Template.FindName("FieldSurface", projectConflictCombo) != null);
		bool popupWorks = true;
		if (projectControlsVisible)
		{
			Popup conflictPopup = projectConflictCombo.Template?.FindName("PART_Popup", projectConflictCombo) as Popup;
			projectConflictCombo.IsDropDownOpen = true;
			window.UpdateLayout();
			popupWorks = conflictPopup != null && conflictPopup.IsOpen;
			projectConflictCombo.IsDropDownOpen = false;
		}
		if (!actionButtonsFit || !customConflictField || !popupWorks)
		{
			throw new InvalidOperationException($"导入布局诊断：ActionBar={importActionBar.ActualHeight:0.##}, Inspect={inspectButton.ActualHeight:0.##}, Import={importButton.ActualHeight:0.##}, ButtonBottom={buttonBottom.Y:0.##}, PaddingBottom={importActionBar.Padding.Bottom:0.##}, Combo={projectConflictCombo.ActualHeight:0.##}, CustomTemplate={customConflictField}, Popup={popupWorks}");
		}
		return true;
	}

	public bool SelectProjectBackupForTest(string displayName)
	{
		projectBackupModeRadio.IsChecked = true;
		UpdateBackupMode();
		if (!SelectProjectForTest(displayName) || selectedProject == null)
		{
			return false;
		}
		selectedProject.IsBatchSelected = true;
		UpdateSelectedCount();
		return true;
	}

	public bool SelectConversationBackupForTest(string displayName)
	{
		conversationBackupModeRadio.IsChecked = true;
		UpdateBackupMode();
		if (!SelectProjectForTest(displayName))
		{
			return false;
		}
		SessionInfo session = ((sessionView == null) ? null : sessionView.Cast<object>().OfType<SessionInfo>().FirstOrDefault((SessionInfo x) => !x.IsSubagent));
		if (session == null)
		{
			return false;
		}
		session.IsSelected = true;
		UpdateSelectedCount();
		return true;
	}

	public void ShowProjectRestoreForTest()
	{
		loadedManifest = new PackManifest
		{
			schema = 4,
			mode = "batch_projects_with_files",
			projects = new List<PackProject>
			{
				new PackProject
				{
					project_key = "project-001",
					source_project = "D:\\OldComputer\\Projects\\example-app",
					source_project_name = "example-app",
					target_folder = "example-app",
					project_payload = new ProjectPayloadInfo
					{
						archive_file = "projects/project-001/project-files.zip",
						file_count = 128,
						directory_count = 24,
						uncompressed_bytes = 12582912L,
						sha256 = new string('0', 64)
					}
				},
				new PackProject
				{
					project_key = "project-002",
					source_project = "D:\\OldComputer\\Projects\\data-service",
					source_project_name = "data-service",
					target_folder = "data-service",
					project_payload = new ProjectPayloadInfo
					{
						archive_file = "projects/project-002/project-files.zip",
						file_count = 76,
						directory_count = 15,
						uncompressed_bytes = 7340032L,
						sha256 = new string('1', 64)
					}
				}
			}
		};
		packageSummaryText.Text = UiLanguage.T("2 个项目 + 对话完整包 · 11 个主对话");
		packageProjectText.Text = UiLanguage.T("项目：example-app、data-service\n项目文件：204 个（19 MB）\n创建时间：2026-08-04 15:20");
		targetPathBox.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex 项目迁入-20260804");
		restoreProjectFilesCheck.IsChecked = true;
		projectConflictCombo.SelectedIndex = 0;
		UpdateProjectRestoreControls();
		projectConflictCombo.BringIntoView();
	}

	public bool TestImportModeHelpForTest()
	{
		copyModeRadio.IsChecked = true;
		bool copyHelp = (importModeHelpText.Text ?? string.Empty).Contains(UiLanguage.IsEnglish ? "new Thread ID" : "全新 Thread ID");
		mergeModeRadio.IsChecked = true;
		bool mergeHelp = (importModeHelpText.Text ?? string.Empty).Contains(UiLanguage.IsEnglish ? "original ID" : "原始编号");
		return copyHelp && mergeHelp;
	}


	public async Task<bool> ShowFirstConversationForTest(string projectName)
	{
		if (!SelectProjectForTest(projectName) || selectedProject == null)
		{
			return false;
		}
		sessionList.UpdateLayout();
		SessionInfo session = ((sessionView == null) ? null : sessionView.Cast<object>().OfType<SessionInfo>().FirstOrDefault((SessionInfo x) => !x.IsSubagent));
		if (session == null)
		{
			return false;
		}
		sessionList.ScrollIntoView(session);
		sessionList.UpdateLayout();
		ListBoxItem item = sessionList.ItemContainerGenerator.ContainerFromItem(session) as ListBoxItem;
		System.Windows.Controls.Button viewButton = FindNamedButton(item, "ViewSessionButton");
		if (viewButton == null)
		{
			return false;
		}
		viewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, viewButton));
		for (int i = 0; i < 40; i++)
		{
			if (conversationOverlay.Visibility == Visibility.Visible && conversationList.ItemsSource != null && (conversationMetaText.Text ?? string.Empty).IndexOf(UiLanguage.IsEnglish ? "Loading" : "正在", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return true;
			}
			await Task.Delay(50);
		}
		return false;
	}

	public bool TestLongConversationPreviewForTest()
	{
		if (conversationOverlay.Visibility != Visibility.Visible || previewMessages.Count <= 1200)
		{
			return false;
		}
		conversationList.UpdateLayout();
		InitializeConversationDialog();
		conversationScrollViewer = ResolveConversationScrollViewer();
		bool startsAtLatest = conversationScrollViewer != null &&
			conversationScrollViewer.ScrollableHeight > 0.0 &&
			conversationScrollViewer.VerticalOffset >= conversationScrollViewer.ScrollableHeight - 1.0 &&
			activeConversationMessageIndex == previewMessages.Count - 1;
		bool dialogLayout = TestConversationAutomaticLayoutForTest();
		int expectedUserMessages = previewMessages.Count((ConversationMessage message) => message.IsUser && !message.IsNotice);
		bool railVisible = conversationNavigationHost.Visibility == Visibility.Visible &&
			conversationNavigationItems.Count == expectedUserMessages &&
			activeConversationNavigationIndex == conversationNavigationItems.Count - 1;
		bool pixelScrolling = VirtualizingPanel.GetScrollUnit(conversationList) == ScrollUnit.Pixel;
		ScrollConversationToIndex(0, alignToEnd: false);
		conversationList.UpdateLayout();
		conversationScrollViewer = ResolveConversationScrollViewer();
		bool reachedFirst = conversationScrollViewer != null && conversationScrollViewer.VerticalOffset <= 1.0 && activeConversationMessageIndex == 0;
		ScrollConversationToIndex(previewMessages.Count - 1, alignToEnd: true);
		conversationList.UpdateLayout();
		conversationScrollViewer = ResolveConversationScrollViewer();
		bool returnedToLatest = conversationScrollViewer != null &&
			conversationScrollViewer.VerticalOffset >= conversationScrollViewer.ScrollableHeight - 1.0 &&
			activeConversationMessageIndex == previewMessages.Count - 1;
		return startsAtLatest && dialogLayout && railVisible && pixelScrolling && reachedFirst && returnedToLatest;
	}

	public bool ShowConversationNavigationPreviewForTest()
	{
		if (conversationOverlay.Visibility != Visibility.Visible || conversationNavigationItems.Count < 4)
		{
			return false;
		}
		int index = Math.Max(0, Math.Min(conversationNavigationItems.Count - 1, activeConversationNavigationIndex));
		EnsureConversationNavigationItemVisible(index);
		ExpandConversationNavigationAround(index);
		ShowConversationNavigationPreview(conversationNavigationItems[index]);
		conversationNavigationRail.UpdateLayout();
		return conversationNavigationPreviewPopup.IsOpen &&
			(double)conversationNavigationItems[index].Marker.GetAnimationBaseValue(FrameworkElement.WidthProperty) >= 25.0 &&
			!string.IsNullOrWhiteSpace(conversationNavigationPreviewTitle.Text);
	}

	public bool TestConversationFollowsWindowResizeForTest()
	{
		if (conversationOverlay.Visibility != Visibility.Visible || window.WindowState != WindowState.Normal)
		{
			return false;
		}
		InitializeConversationDialog();
		double originalWindowWidth = window.Width;
		double originalWindowHeight = window.Height;
		double originalCanvasWidth = conversationCanvas.ActualWidth;
		double originalCanvasHeight = conversationCanvas.ActualHeight;
		double originalDialogWidth = conversationDialogHost.ActualWidth;
		double originalDialogHeight = conversationDialogHost.ActualHeight;
		window.Width = originalWindowWidth + 140.0;
		window.Height = originalWindowHeight + 100.0;
		window.UpdateLayout();
		conversationOverlay.UpdateLayout();
		bool expanded = conversationCanvas.ActualWidth > originalCanvasWidth + 100.0 &&
			conversationCanvas.ActualHeight > originalCanvasHeight + 70.0 &&
			Math.Abs((conversationDialogHost.ActualWidth - originalDialogWidth) - (conversationCanvas.ActualWidth - originalCanvasWidth)) <= 3.0 &&
			Math.Abs((conversationDialogHost.ActualHeight - originalDialogHeight) - (conversationCanvas.ActualHeight - originalCanvasHeight)) <= 3.0 &&
			TestConversationAutomaticLayoutForTest();
		window.Width = originalWindowWidth;
		window.Height = originalWindowHeight;
		window.UpdateLayout();
		conversationOverlay.UpdateLayout();
		bool restored = Math.Abs(conversationCanvas.ActualWidth - originalCanvasWidth) <= 2.0 &&
			Math.Abs(conversationCanvas.ActualHeight - originalCanvasHeight) <= 2.0 &&
			Math.Abs(conversationDialogHost.ActualWidth - originalDialogWidth) <= 3.0 &&
			Math.Abs(conversationDialogHost.ActualHeight - originalDialogHeight) <= 3.0;
		return expanded && restored && TestConversationAutomaticLayoutForTest();
	}

	public bool TestConversationAutomaticLayoutForTest()
	{
		if (conversationOverlay.Visibility != Visibility.Visible)
		{
			return false;
		}
		InitializeConversationDialog();
		conversationOverlay.UpdateLayout();
		FrameworkElement contentLayout = Find<FrameworkElement>("ConversationContentLayout");
		double rightInset = conversationCanvas.ActualWidth - DialogLeft() - conversationDialogHost.ActualWidth;
		double bottomInset = conversationCanvas.ActualHeight - DialogTop() - conversationDialogHost.ActualHeight;
		bool fixedInsets = Math.Abs(DialogLeft() - ConversationPreviewInset) <= 1.5 &&
			Math.Abs(DialogTop() - ConversationPreviewInset) <= 1.5 &&
			Math.Abs(rightInset - ConversationPreviewInset) <= 1.5 &&
			Math.Abs(bottomInset - ConversationPreviewInset) <= 1.5;
		bool contentUsesAvailableWidth = contentLayout.ActualWidth > 0.0 &&
			contentLayout.ActualWidth >= conversationDialogHost.ActualWidth - 42.0;
		bool navigationFits = conversationNavigationHost.Visibility != Visibility.Visible ||
			conversationNavigationHost.ActualHeight <= contentLayout.ActualHeight + 0.5;
		return fixedInsets && contentUsesAvailableWidth && navigationFits;
	}

	private static System.Windows.Controls.Button FindNamedButton(DependencyObject root, string name)
	{
		if (root == null)
		{
			return null;
		}
		if (root is System.Windows.Controls.Button button && string.Equals(button.Name, name, StringComparison.Ordinal))
		{
			return button;
		}
		int num = 0;
		try
		{
			num = VisualTreeHelper.GetChildrenCount(root);
		}
		catch
		{
		}
		for (int i = 0; i < num; i++)
		{
			System.Windows.Controls.Button button2 = FindNamedButton(VisualTreeHelper.GetChild(root, i), name);
			if (button2 != null)
			{
				return button2;
			}
		}
		return null;
	}

	private T Find<T>(string name) where T : class
	{
		if (!(window.FindName(name) is T result))
		{
			throw new InvalidOperationException("界面缺少控件：" + name);
		}
		return result;
	}

	private void WireEvents()
	{
		window.Closing += delegate(object sender, CancelEventArgs e)
		{
			if (!isBusy)
			{
				return;
			}
			e.Cancel = true;
			SetStatus("当前操作尚未完成，请等待完成后再关闭。", error: false);
			AppDialog.Show(window, "操作进行中", "暂时不能关闭窗口", "当前正在写入或校验本地数据。完成后即可安全关闭。", AppDialogTone.Warning, "继续等待");
		};
		window.Closed += delegate
		{
			InvalidateConversationPreviewRequests();
		};
		closeButton.Click += delegate
		{
			window.Close();
		};
		Find<System.Windows.Controls.Button>("MinimizeButton").Click += delegate
		{
			window.WindowState = WindowState.Minimized;
		};
		Find<System.Windows.Controls.Button>("MaximizeButton").Click += delegate
		{
			ToggleMaximize();
		};
		window.StateChanged += delegate
		{
			UpdateMaximizeGlyph();
		};
		window.SourceInitialized += delegate
		{
			PreferRoundedWindow();
		};
		backupTabButton.Click += delegate
		{
			ShowBackupPage();
		};
		importTabButton.Click += delegate
		{
			ShowImportPage();
		};
		refreshButton.Click += async delegate
		{
			await RefreshDataAsync();
		};
		trashButton.Click += async delegate
		{
			await ShowTrashManagerAsync();
		};
		languageButton.Click += delegate
		{
			SwitchLanguage();
		};
		browseBackupFolderButton.Click += delegate
		{
			BrowseBackupFolder();
		};
		projectBackupModeRadio.Checked += delegate
		{
			UpdateBackupMode();
		};
		conversationBackupModeRadio.Checked += delegate
		{
			UpdateBackupMode();
		};
		selectAllProjectsButton.Click += delegate
		{
			SetProjectSelection(value: true);
		};
		clearProjectsButton.Click += delegate
		{
			SetProjectSelection(value: false);
		};
		projectList.SelectionChanged += ProjectListSelectionChanged;
		projectList.PreviewMouseLeftButtonDown += ProjectListPreviewMouseLeftButtonDown;
		sessionList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(SessionActionButtonClick), handledEventsToo: true);
		searchBox.TextChanged += delegate
		{
			RefreshSessionView();
		};
		mainSessionsTabRadio.Checked += delegate
		{
			showSubagentSessions = false;
			UpdateSessionTypeView();
		};
		subagentSessionsTabRadio.Checked += delegate
		{
			showSubagentSessions = true;
			UpdateSessionTypeView();
		};
		toggleSessionSelectionButton.Click += delegate
		{
			ToggleSessionSelection();
		};
		deleteSelectedSessionsButton.Click += async delegate
		{
			await DeleteSelectedSessionsAsync();
		};
		copyProjectPathButton.Click += delegate
		{
			CopySelectedProjectPath();
		};
		backupSelectedButton.Click += async delegate
		{
			await BackupSelectedAsync();
		};
		backupProjectFilesButton.Click += async delegate
		{
			await BackupProjectWithFilesAsync();
		};
		browsePackageButton.Click += async delegate
		{
			await BrowsePackageAsync();
		};
		browseTargetButton.Click += delegate
		{
			BrowseTarget();
		};
		inspectButton.Click += async delegate
		{
			await ImportPackageAsync(dryRun: true);
		};
		importButton.Click += async delegate
		{
			await ImportPackageAsync(dryRun: false);
		};
		mergeModeRadio.Checked += delegate
		{
			UpdateImportModeHelp();
		};
		copyModeRadio.Checked += delegate
		{
			UpdateImportModeHelp();
		};
		restoreProjectFilesCheck.Checked += delegate
		{
			UpdateProjectRestoreControls();
		};
		restoreProjectFilesCheck.Unchecked += delegate
		{
			UpdateProjectRestoreControls();
		};
		projectConflictCombo.SelectionChanged += delegate
		{
			UpdateProjectRestoreControls();
		};
		UpdateImportModeHelp();
		UpdateProjectRestoreControls();
		conversationMinimizeButton.Click += delegate
		{
			window.WindowState = WindowState.Minimized;
		};
		conversationCloseButton.Click += delegate
		{
			HideConversation();
		};
		conversationCanvas.SizeChanged += ConversationCanvasSizeChanged;
		conversationNavigationHost.MouseLeave += delegate
		{
			if (!conversationNavigationScrubbing)
			{
				CloseConversationNavigationPreview();
				ExpandConversationNavigationAround(-1);
			}
		};
		conversationNavigationHost.LostKeyboardFocus += delegate
		{
			if (!conversationNavigationHost.IsKeyboardFocusWithin && !conversationNavigationScrubbing)
			{
				CloseConversationNavigationPreview();
				ExpandConversationNavigationAround(-1);
			}
		};
		conversationNavigationRail.MouseMove += ConversationNavigationRailMouseMove;
		conversationNavigationRail.MouseLeftButtonUp += ConversationNavigationRailMouseLeftButtonUp;
		conversationNavigationRail.LostMouseCapture += delegate
		{
			conversationNavigationScrubbing = false;
			conversationNavigationMoved = false;
			conversationNavigationPressedItem = null;
		};
		conversationList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ConversationListScrollChanged));
		copyThreadIdButton.Click += delegate
		{
			CopyPreviewedThreadId();
		};
		UpdateBackupMode();
		UpdateSessionTypeView();
		window.PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e)
		{
			if (e.Key == Key.Escape && conversationOverlay.Visibility == Visibility.Visible)
			{
				HideConversation();
				e.Handled = true;
			}
		};
		window.Loaded += async delegate
		{
			int num = default(int);
			_ = num;
			_ = 0;
			try
			{
				await RefreshDataAsync();
			}
			finally
			{
				initialLoadCompletion.TrySetResult(result: true);
			}
		};
	}

	private void PreferRoundedWindow()
	{
		try
		{
			IntPtr handle = new WindowInteropHelper(window).Handle;
			int value = 2;
			DwmSetWindowAttribute(handle, 33, ref value, 4);
		}
		catch
		{
		}
	}
	private void SwitchLanguage()
	{
		if (isBusy)
		{
			return;
		}
		AppLanguage previous = UiLanguage.Current;
		UiLanguage.SetAndSave(UiLanguage.IsEnglish ? AppLanguage.Chinese : AppLanguage.English);
		try
		{
			MainWindowController replacementController = new MainWindowController();
			Window replacement = replacementController.Window;
			replacement.Width = window.ActualWidth > 0.0 ? window.ActualWidth : window.Width;
			replacement.Height = window.ActualHeight > 0.0 ? window.ActualHeight : window.Height;
			if (window.WindowState == WindowState.Normal)
			{
				replacement.WindowStartupLocation = WindowStartupLocation.Manual;
				replacement.Left = window.Left;
				replacement.Top = window.Top;
			}
			replacementController.backupFolderBox.Text = backupFolderBox.Text;
			replacementController.packagePathBox.Text = packagePathBox.Text;
			replacementController.targetPathBox.Text = targetPathBox.Text;
			replacementController.searchBox.Text = searchBox.Text;
			if (importPage.Visibility == Visibility.Visible)
			{
				replacementController.ShowImportPage();
			}
			System.Windows.Application.Current.MainWindow = replacement;
			replacement.Show();
			if (window.WindowState == WindowState.Maximized)
			{
				replacement.WindowState = WindowState.Maximized;
			}
			window.Close();
		}
		catch (Exception ex)
		{
			UiLanguage.SetAndSave(previous);
			AppDialog.Show(window, "切换语言失败", "无法重新加载界面", ex.Message, AppDialogTone.Error);
		}
	}

	private void ToggleMaximize()
	{
		window.WindowState = ((window.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
	}

	private void UpdateMaximizeGlyph()
	{
		bool maximized = window.WindowState == WindowState.Maximized;
		maximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
		restoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
		Find<System.Windows.Controls.Button>("MaximizeButton").ToolTip = UiLanguage.T(maximized ? "还原" : "最大化");
	}

	private void ConversationCanvasSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (conversationDialogInitialized && e.NewSize.Width > 0.0 && e.NewSize.Height > 0.0)
		{
			FitConversationDialogToCanvas();
		}
	}

	private void InitializeConversationDialog()
	{
		conversationOverlay.UpdateLayout();
		if (conversationCanvas.ActualWidth > 0.0 && conversationCanvas.ActualHeight > 0.0)
		{
			conversationDialogInitialized = true;
			FitConversationDialogToCanvas();
		}
	}

	private void FitConversationDialogToCanvas()
	{
		if (!conversationDialogInitialized || conversationCanvas.ActualWidth <= 0.0 || conversationCanvas.ActualHeight <= 0.0)
		{
			return;
		}
		conversationDialogHost.Width = Math.Max(conversationDialogHost.MinWidth, conversationCanvas.ActualWidth - ConversationPreviewInset * 2.0);
		conversationDialogHost.Height = Math.Max(conversationDialogHost.MinHeight, conversationCanvas.ActualHeight - ConversationPreviewInset * 2.0);
		Canvas.SetLeft(conversationDialogHost, ConversationPreviewInset);
		Canvas.SetTop(conversationDialogHost, ConversationPreviewInset);
	}

	private double DialogLeft()
	{
		double left = Canvas.GetLeft(conversationDialogHost);
		if (!double.IsNaN(left))
		{
			return left;
		}
		return 0.0;
	}

	private double DialogTop()
	{
		double top = Canvas.GetTop(conversationDialogHost);
		if (!double.IsNaN(top))
		{
			return top;
		}
		return 0.0;
	}

	private void ShowBackupPage()
	{
		backupPage.Visibility = Visibility.Visible;
		importPage.Visibility = Visibility.Collapsed;
		backupTabIndicator.Visibility = Visibility.Visible;
		importTabIndicator.Visibility = Visibility.Collapsed;
		backupTabButton.Foreground = Brush("#171916");
		importTabButton.Foreground = Brush("#747973");
		UpdateBackupMode();
	}

	private void ShowImportPage()
	{
		backupPage.Visibility = Visibility.Collapsed;
		importPage.Visibility = Visibility.Visible;
		backupTabIndicator.Visibility = Visibility.Collapsed;
		importTabIndicator.Visibility = Visibility.Visible;
		backupTabButton.Foreground = Brush("#747973");
		importTabButton.Foreground = Brush("#171916");
	}

	private async Task RefreshDataAsync()
	{
		if (BackendContext.IsDsh) { await RefreshDshAsync(); return; }
		if (isBusy)
		{
			return;
		}
		SetBusy(busy: true, "正在读取 Codex 本地索引与会话……");
		try
		{
			string codexHome = CodexCatalog.ResolveCodexHome();
			DesktopTaskCacheInvalidationResult completedRepairCacheCleanup = new DesktopTaskCacheInvalidationResult();
			string desktopCacheCleanupError = string.Empty;
			try
			{
				completedRepairCacheCleanup = await Task.Run(() => ConversationIndexMaintenance.InvalidateCompletedRepairCaches(codexHome));
			}
			catch (Exception cleanupError)
			{
				desktopCacheCleanupError = cleanupError.Message;
				AppendLog("清理 Codex 新版侧边栏目录或任务缓存失败：" + cleanupError.Message);
			}
			List<DbThread> orphanedThreads = new List<DbThread>();
			List<DbThread> deletedSidebarRemnants = new List<DbThread>();
			string orphanDetectionError = string.Empty;
			try
			{
				orphanedThreads = await Task.Run(() => ConversationIndexMaintenance.FindOrphanedThreads(codexHome));
			}
			catch (Exception detectionError)
			{
				orphanDetectionError = detectionError.Message;
				AppendLog("检测侧边栏失效项失败：" + detectionError.Message);
			}
			try
			{
				deletedSidebarRemnants = await Task.Run(() => ConversationIndexMaintenance.FindDeletedSidebarRemnants(codexHome));
			}
			catch (Exception detectionError)
			{
				orphanDetectionError = string.IsNullOrWhiteSpace(orphanDetectionError) ? detectionError.Message : orphanDetectionError + "；" + detectionError.Message;
				AppendLog("检测旧版删除的侧边栏残留失败：" + detectionError.Message);
			}
			OrphanedSnapshotCleanupResult orphanedSnapshots = new OrphanedSnapshotCleanupResult();
			if (Environment.GetEnvironmentVariable("CODEX_MIGRATOR_SKIP_SNAPSHOT_MAINTENANCE") != "1" &&
				Environment.GetEnvironmentVariable("CODEX_MIGRATOR_SKIP_LEGACY_CCT_MAINTENANCE") != "1")
			{
				try
				{
					orphanedSnapshots = await Task.Run(() => ImportSnapshotMaintenance.MoveOrphanedSnapshotsToTrash(codexHome));
				}
				catch (Exception cleanupError)
				{
					AppendLog("整理遗留事务安全快照失败：" + cleanupError.Message);
				}
			}
			List<SessionInfo> raw = await Task.Run(() => NativeSessionCatalog.Scan(codexHome));
			CatalogResult catalog = await Task.Run(() => CodexCatalog.Build(raw, codexHome));
			projects = catalog.Projects;
			foreach (ProjectGroup project in projects)
			{
				project.PropertyChanged += ProjectPropertyChanged;
				foreach (SessionInfo session in project.Sessions)
				{
					session.PropertyChanged += SessionPropertyChanged;
				}
			}
			projectList.ItemsSource = projects;
			if (projects.Count > 0)
			{
				projectList.SelectedIndex = 0;
			}
			else
			{
				selectedProject = null;
				sessionList.ItemsSource = null;
			}
			UpdateSelectedCount();
			string cleanupSummary = orphanedSnapshots.MovedToTrashCount == 0 ? string.Empty : $" · 遗留事务快照已转入回收站 {orphanedSnapshots.MovedToTrashCount} 个，清理重复 {orphanedSnapshots.RedundantDeletedCount} 个";
			if (completedRepairCacheCleanup.RemovedCatalogEntryCount > 0 || completedRepairCacheCleanup.ClearedDirectoryCount > 0)
			{
				cleanupSummary += UiLanguage.IsEnglish
					? $" · Removed {completedRepairCacheCleanup.RemovedCatalogEntryCount} stale desktop catalog entries; refreshed {completedRepairCacheCleanup.ClearedDirectoryCount} cache location(s)"
					: $" · 已移除新版侧边栏目录残留 {completedRepairCacheCleanup.RemovedCatalogEntryCount} 个，刷新缓存 {completedRepairCacheCleanup.ClearedDirectoryCount} 处";
			}
			if (!string.IsNullOrWhiteSpace(desktopCacheCleanupError))
			{
				cleanupSummary += UiLanguage.IsEnglish ? " · Desktop task cache cleanup failed" : " · 桌面任务缓存清理失败";
			}
			string orphanSummary = string.Empty;
			bool orphanError = !string.IsNullOrWhiteSpace(orphanDetectionError) || !string.IsNullOrWhiteSpace(desktopCacheCleanupError);
			int staleSidebarCount = orphanedThreads.Count + deletedSidebarRemnants.Count;
			if (staleSidebarCount > 0)
			{
				if (CodexDesktopProjectRegistry.IsDesktopRunning(codexHome))
				{
					orphanSummary = $" · 检测到侧边栏失效项 {staleSidebarCount} 个（完全退出 Codex 后点击刷新可处理）";
				}
				else
				{
					string[] staleThreadIds = orphanedThreads.Concat(deletedSidebarRemnants).Select((DbThread thread) => thread.Id).Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
					List<LiveDescendantInfo> liveDescendants = await Task.Run(() => ConversationIndexMaintenance.FindLiveDescendants(codexHome, staleThreadIds));
					HashSet<string> blockedRootIds = new HashSet<string>(liveDescendants.Select((LiveDescendantInfo item) => item.RootThreadId), StringComparer.OrdinalIgnoreCase);
					List<DbThread> repairableOrphans = orphanedThreads.Where((DbThread thread) => !blockedRootIds.Contains(thread.Id)).ToList();
					List<DbThread> repairableLegacyRemnants = deletedSidebarRemnants.Where((DbThread thread) => !blockedRootIds.Contains(thread.Id)).ToList();
					int repairableCount = repairableOrphans.Count + repairableLegacyRemnants.Count;
					IEnumerable<string> currentPreview = orphanedThreads.Select((DbThread thread) => (UiLanguage.IsEnglish ? "• [Current index] " : "• [当前索引] ") + (string.IsNullOrWhiteSpace(thread.Title) ? thread.Id : thread.Title + " · " + thread.Id));
					IEnumerable<string> legacyPreview = deletedSidebarRemnants.Select((DbThread thread) => (UiLanguage.IsEnglish ? "• [Legacy deletion] " : "• [旧版删除] ") + (string.IsNullOrWhiteSpace(thread.Title) ? thread.Id : thread.Title + " · " + thread.Id));
					string itemPreview = string.Join("\n", currentPreview.Concat(legacyPreview).Take(8));
					if (staleSidebarCount > 8)
					{
						itemPreview += UiLanguage.IsEnglish ? $"\n…{staleSidebarCount - 8} more" : $"\n…另有 {staleSidebarCount - 8} 个";
					}
					string categorySummary = UiLanguage.IsEnglish
						? $"Current index remnants: {orphanedThreads.Count}; legacy partial-deletion remnants: {deletedSidebarRemnants.Count}."
						: $"当前索引残留 {orphanedThreads.Count} 个；旧版半删除残留 {deletedSidebarRemnants.Count} 个。";
					string blockedSummary = string.Empty;
					if (liveDescendants.Count > 0)
					{
						blockedSummary = UiLanguage.IsEnglish
							? $"\n\n{blockedRootIds.Count} stale parent tasks still have {liveDescendants.Count} descendant conversations with local files. To avoid cascade deletion, those parent tasks will be left unchanged. " + (repairableCount > 0 ? $"The other {repairableCount} tasks will be cleaned first, then the first descendant will be selected automatically." : "The first descendant will be selected automatically so you can move it to app trash or back it up first.")
							: $"\n\n其中 {blockedRootIds.Count} 个失效父对话仍关联 {liveDescendants.Count} 个存在本地文件的子代理。为避免官方删除级联误删，这些父记录暂不清理。" + (repairableCount > 0 ? $"确认后会先清理其余 {repairableCount} 个记录，再自动定位并勾选第一条关联子代理。" : "确认后会自动定位并勾选第一条关联子代理，方便你先移入软件回收站或备份。");
					}
					string repairPrompt = UiLanguage.IsEnglish
						? $"Detected {staleSidebarCount} confirmed unreadable sidebar tasks.\n{categorySummary}\n\n{itemPreview}{blockedSummary}\n\n" + (liveDescendants.Count > 0 ? "Continue with the safe cleanup and descendant location workflow?" : "Clean these task-directory records through Codex's official deletion interface? This does not delete project files or any conversation file that still exists.")
						: $"检测到 {staleSidebarCount} 个已确认打不开的侧边栏任务。\n{categorySummary}\n\n{itemPreview}{blockedSummary}\n\n" + (liveDescendants.Count > 0 ? "是否继续执行安全清理与子代理定位？" : "是否通过 Codex 官方删除接口清理这些任务目录记录？本操作不会删除任何项目文件，也不会删除任何仍存在的会话文件。");
					bool repairConfirmed = AppDialog.Confirm(window, "清理侧边栏失效项", "清理侧边栏失效项", repairPrompt, liveDescendants.Count > 0 ? AppDialogTone.Warning : AppDialogTone.Info, liveDescendants.Count > 0 ? (repairableCount > 0 ? "清理并定位" : "定位子代理") : "继续", "取消");
					if (repairConfirmed)
					{
						try
						{
							int repairedCount = 0;
							int clearedDesktopCacheCount = 0;
							int removedDesktopCatalogCount = 0;
							bool desktopRestarted = false;
							List<string> backupPaths = new List<string>();
							if (repairableOrphans.Count > 0)
							{
								OrphanIndexRepairResult currentRepair = await Task.Run(() => ConversationIndexMaintenance.RepairSelectedOrphans(codexHome, repairableOrphans.Select((DbThread thread) => thread.Id)));
								repairedCount += currentRepair.RepairedCount;
								clearedDesktopCacheCount += currentRepair.ClearedDesktopCacheCount;
								removedDesktopCatalogCount += currentRepair.RemovedDesktopCatalogCount;
								desktopRestarted |= currentRepair.DesktopRunning;
								if (!string.IsNullOrWhiteSpace(currentRepair.IndexBackupPath))
								{
									backupPaths.Add(currentRepair.IndexBackupPath);
								}
								if (!string.IsNullOrWhiteSpace(currentRepair.DesktopCatalogBackupPath))
								{
									backupPaths.Add(currentRepair.DesktopCatalogBackupPath);
								}
							}
							if (!desktopRestarted && repairableLegacyRemnants.Count > 0)
							{
								OrphanIndexRepairResult legacyRepair = await Task.Run(() => ConversationIndexMaintenance.RepairDeletedSidebarRemnants(codexHome, repairableLegacyRemnants.Select((DbThread thread) => thread.Id)));
								repairedCount += legacyRepair.RepairedCount;
								clearedDesktopCacheCount += legacyRepair.ClearedDesktopCacheCount;
								removedDesktopCatalogCount += legacyRepair.RemovedDesktopCatalogCount;
								desktopRestarted |= legacyRepair.DesktopRunning;
								if (!string.IsNullOrWhiteSpace(legacyRepair.IndexBackupPath))
								{
									backupPaths.Add(legacyRepair.IndexBackupPath);
								}
								if (!string.IsNullOrWhiteSpace(legacyRepair.DesktopCatalogBackupPath))
								{
									backupPaths.Add(legacyRepair.DesktopCatalogBackupPath);
								}
							}
							if (desktopRestarted)
							{
								orphanSummary = " · Codex 已重新启动，未清理失效项";
								orphanError = true;
							}
							else if (liveDescendants.Count > 0)
							{
								bool focused = FocusLiveDescendant(liveDescendants);
								orphanSummary = UiLanguage.IsEnglish ? $" · Cleaned {repairedCount}; {blockedRootIds.Count} parent tasks await descendant handling" : $" · 已清理 {repairedCount} 个；{blockedRootIds.Count} 个父记录需先处理子代理";
								orphanError = true;
								string descendantDetails = string.Join("\n\n", liveDescendants.Take(8).Select((LiveDescendantInfo item) => "• " + item.Title + "\n  Thread ID: " + item.ThreadId + "\n  " + (UiLanguage.IsEnglish ? "Parent: " : "父对话：") + item.RootThreadId + "\n  " + (UiLanguage.IsEnglish ? "Project: " : "项目：") + item.Cwd + "\n  " + (UiLanguage.IsEnglish ? "File: " : "文件：") + item.RolloutPath));
								string descendantGuide = UiLanguage.IsEnglish
									? (focused ? "The application has switched to the orphaned-subagents project, opened the Subagents tab, searched for the exact Thread ID, and selected the first item. Close this dialog, click Delete selected, and choose app trash. Refresh again afterward to clean the remaining parent task." : "The descendant could not be selected automatically. Use its exact Thread ID below in the Subagents search, move it to app trash, then refresh again.") + "\n\n" + descendantDetails
									: (focused ? "软件已经自动切换到“孤立子代理”项目和“子代理”页，按准确 Thread ID 搜索并勾选了第一条。关闭提示后直接点击“删除所选”，选择“移入软件回收站”；完成后再次刷新，即可清理剩余父记录。" : "软件未能自动选中该子代理。请在“子代理”页使用下面的准确 Thread ID 搜索，先移入软件回收站，再次刷新。") + "\n\n" + descendantDetails;
								AppendLog(UiLanguage.IsEnglish ? $"Deferred {blockedRootIds.Count} stale parent tasks and located {liveDescendants.Count} live descendants." : $"暂缓清理 {blockedRootIds.Count} 个失效父记录，已定位 {liveDescendants.Count} 个仍存在的子代理。");
								AppDialog.ShowCompat(window, descendantGuide, UiLanguage.IsEnglish ? "Descendant located" : "已定位关联子代理", MessageBoxButton.OK, MessageBoxImage.Warning);
							}
							else
							{
								orphanSummary = UiLanguage.IsEnglish ? $" · Cleaned stale sidebar entries through the official Codex interface: {repairedCount}" : $" · 已通过 Codex 官方接口清理侧边栏失效项 {repairedCount} 个";
								AppendLog(UiLanguage.IsEnglish
									? $"Cleaned stale sidebar entries through the official Codex interface: {repairedCount}; removed desktop catalog entries: {removedDesktopCatalogCount}; refreshed desktop task cache locations: {clearedDesktopCacheCount}." + (backupPaths.Count == 0 ? string.Empty : " Index backup: " + string.Join("; ", backupPaths.Distinct(StringComparer.OrdinalIgnoreCase)))
									: $"已通过 Codex 官方接口清理侧边栏失效项 {repairedCount} 个；移除新版侧边栏目录记录 {removedDesktopCatalogCount} 个；刷新桌面任务缓存 {clearedDesktopCacheCount} 处。" + (backupPaths.Count == 0 ? string.Empty : "索引备份：" + string.Join("；", backupPaths.Distinct(StringComparer.OrdinalIgnoreCase))));
							}
						}
						catch (Exception repairError)
						{
							orphanSummary = " · 侧边栏失效项清理失败，详见操作记录";
							orphanError = true;
							AppendLog("清理侧边栏失效项失败：" + repairError.Message);
							bool focused = repairError is LiveDescendantRepairException liveError && FocusLiveDescendant(liveError.Descendants);
							string repairMessage = repairError.Message + (focused ? (UiLanguage.IsEnglish ? "\n\nThe first descendant was selected automatically in the Subagents view." : "\n\n软件已在“子代理”页自动定位并勾选第一条记录。") : string.Empty);
							AppDialog.ShowCompat(window, repairMessage, "清理失败", MessageBoxButton.OK, MessageBoxImage.Warning);
						}
					}
					else
					{
						orphanSummary = UiLanguage.IsEnglish ? $" · Kept stale sidebar entries: {staleSidebarCount}" : $" · 已保留侧边栏失效项 {staleSidebarCount} 个";
					}
				}
			}
			else if (orphanError)
			{
				orphanSummary = !string.IsNullOrWhiteSpace(orphanDetectionError)
					? " · 侧边栏失效项检测失败，详见操作记录"
					: (UiLanguage.IsEnglish ? " · Desktop sidebar catalog cleanup is pending; fully exit Codex and refresh again" : " · 新版侧边栏目录待清理；请完全退出 Codex 后再次刷新");
			}
			SetStatus($"已载入 {projects.Count} 个项目 · {catalog.MainCount} 个主对话 · {catalog.InternalCount} 个子代理对话 · {catalog.Diagnostic}{cleanupSummary}{orphanSummary}", error: orphanError);
			if (completedRepairCacheCleanup.RemovedCatalogEntryCount > 0 || completedRepairCacheCleanup.ClearedDirectoryCount > 0)
			{
				string cacheMessage = UiLanguage.IsEnglish
					? $"Removed {completedRepairCacheCleanup.RemovedCatalogEntryCount} deleted task(s) from the current Codex desktop sidebar catalog and refreshed {completedRepairCacheCleanup.ClearedDirectoryCount} matching cache location(s). You can now reopen Codex; those invalid sidebar entries should be gone."
					: $"已从当前 Codex 新版侧边栏目录移除 {completedRepairCacheCleanup.RemovedCatalogEntryCount} 个已删除任务，并刷新匹配缓存 {completedRepairCacheCleanup.ClearedDirectoryCount} 处。现在可以重新打开 Codex，这些失效条目应不再显示。";
				AppDialog.ShowCompat(window, cacheMessage, UiLanguage.IsEnglish ? "Sidebar catalog cleaned" : "侧边栏目录已清理", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			}
			else if (!string.IsNullOrWhiteSpace(desktopCacheCleanupError))
			{
				string cacheErrorMessage = UiLanguage.IsEnglish
					? "The deleted task is still present in the current Codex desktop sidebar catalog or cache, but it cannot be updated while Codex is running. Fully exit Codex (make sure no ChatGPT process remains), reopen this application, and click Refresh again."
					: "已删除任务仍存在于当前 Codex 新版侧边栏目录或缓存中，但 Codex 运行时不能安全更新。请完全退出 Codex（确认任务管理器中没有 ChatGPT 进程），重新打开本工具并再次点击“刷新”。";
				AppDialog.ShowCompat(window, cacheErrorMessage + Environment.NewLine + Environment.NewLine + desktopCacheCleanupError, UiLanguage.IsEnglish ? "Close Codex to finish cleanup" : "完全退出 Codex 后完成清理", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}
		catch (Exception ex)
		{
			SetStatus("读取失败：" + ex.Message, error: true);
			AppDialog.ShowCompat(window, ex.Message, "读取会话失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			SetBusy(busy: false, null);
		}
	}

	private async void ProjectListSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		selectedProject = projectList.SelectedItem as ProjectGroup;
		if (selectedProject != null)
		{
			projectTitleText.Text = selectedProject.DisplayName;
			projectPathText.Text = selectedProject.ProjectPath;
			projectMetaText.Text = selectedProject.HeaderMeta;
			projectSizeText.Text = selectedProject.ProjectStorageSummary;
			sessionView = CollectionViewSource.GetDefaultView(selectedProject.Sessions);
			sessionView.Filter = SessionFilter;
			sessionList.ItemsSource = sessionView;
			UpdateSessionTypeView();
			UpdateSelectedCount();
			await EnsureProjectStorageMetricsAsync(selectedProject);
		}
	}

	private async Task EnsureProjectStorageMetricsAsync(ProjectGroup project)
	{
		if (project == null)
		{
			return;
		}
		if (!project.StorageScanStarted)
		{
			project.BeginStorageScan();
			if (ReferenceEquals(project, selectedProject))
			{
				projectSizeText.Text = project.ProjectStorageSummary;
			}
			if (project.IsSubagentOnly)
			{
				return;
			}
			ProjectStorageSummary summary = await Task.Run(() => ProjectStorageMetrics.Measure(project.ProjectPath));
			project.CompleteStorageScan(summary);
		}
		if (ReferenceEquals(project, selectedProject))
		{
			projectSizeText.Text = project.ProjectStorageSummary;
		}
	}

	private void CopySelectedProjectPath()
	{
		string path = selectedProject?.ProjectPath;
		if (string.IsNullOrWhiteSpace(path))
		{
			SetStatus("当前项目没有可复制的路径。", error: true);
			return;
		}
		try
		{
			System.Windows.Clipboard.SetText(path);
			SetStatus("已复制项目路径：" + path, error: false);
		}
		catch (Exception ex)
		{
			SetStatus("复制项目路径失败：" + ex.Message, error: true);
		}
	}

	private void ProjectListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		ListBoxItem listBoxItem = ((!(e.OriginalSource is DependencyObject element)) ? null : (ItemsControl.ContainerFromElement(projectList, element) as ListBoxItem));
		if (listBoxItem != null && !listBoxItem.IsSelected)
		{
			listBoxItem.IsSelected = true;
			projectList.ScrollIntoView(listBoxItem.DataContext);
		}
	}

	private bool SessionFilter(object item)
	{
		if (!(item is SessionInfo sessionInfo))
		{
			return false;
		}
		if (!MatchesArchiveFilter(sessionInfo)) return false;
		if (sessionInfo.IsSubagent != showSubagentSessions)
		{
			return false;
		}
		string text = (searchBox.Text ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return true;
		}
		return sessionInfo.DisplayTitle.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
			(sessionInfo.ThreadId ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
			(sessionInfo.DisplayPath ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
			(sessionInfo.ParentDisplayTitle ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private bool FocusLiveDescendant(IEnumerable<LiveDescendantInfo> descendants)
	{
		HashSet<string> ids = new HashSet<string>((descendants ?? Enumerable.Empty<LiveDescendantInfo>()).Select((LiveDescendantInfo item) => item.ThreadId).Where((string id) => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
		if (ids.Count == 0)
		{
			return false;
		}
		ProjectGroup targetProject = projects.FirstOrDefault((ProjectGroup project) => (project.Sessions ?? new List<SessionInfo>()).Any((SessionInfo session) => session.IsSubagent && ids.Contains(session.ThreadId)));
		SessionInfo targetSession = targetProject?.Sessions?.FirstOrDefault((SessionInfo session) => session.IsSubagent && ids.Contains(session.ThreadId));
		if (targetProject == null || targetSession == null)
		{
			return false;
		}
		foreach (SessionInfo session in projects.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>()).Where((SessionInfo session) => session.IsSubagent))
		{
			session.IsSelected = ReferenceEquals(session, targetSession);
		}
		projectList.SelectedItem = targetProject;
		projectList.ScrollIntoView(targetProject);
		subagentSessionsTabRadio.IsChecked = true;
		showSubagentSessions = true;
		searchBox.Text = targetSession.ThreadId;
		UpdateSessionTypeView();
		sessionList.ScrollIntoView(targetSession);
		return true;
	}

	private void UpdateSessionTypeView()
	{
		showSubagentSessions = subagentSessionsTabRadio.IsChecked == true;
		int mainCount = selectedProject?.MainCount ?? 0;
		int subagentCount = selectedProject?.InternalCount ?? 0;
		mainSessionsTabRadio.Content = UiLanguage.IsEnglish ? "Main " + mainCount : "主对话 " + mainCount;
		subagentSessionsTabRadio.Content = UiLanguage.IsEnglish ? "Subagents " + subagentCount : "子代理 " + subagentCount;
		sessionList.Tag = showSubagentSessions ? "Subagent" : "Conversation";
		sessionSelectionTools.Visibility = Visibility.Visible;
		emptySessionsText.Text = UiLanguage.T(showSubagentSessions ? "此项目没有子代理对话" : "此项目没有符合条件的主对话");
		if (selectedProject != null)
		{
			sessionModeHint.Text = UiLanguage.T(showSubagentSessions ? "勾选要处理的子代理，再使用右侧“删除所选”。" : "勾选要处理的主对话；选中项目全部主对话后，可同时处理项目目录。");
		}
		RefreshSessionView();
	}

	private void RefreshSessionView()
	{
		if (sessionView != null)
		{
			sessionView.Refresh();
			bool flag = sessionView.Cast<object>().Any();
			emptySessionsText.Visibility = (flag ? Visibility.Collapsed : Visibility.Visible);
			UpdateSelectedCount();
			UpdateSessionSelectionControls();
		}
	}

	private void SessionPropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "IsSelected")
		{
			UpdateSelectedCount();
			UpdateSessionSelectionControls();
		}
	}

	private void ProjectPropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == "IsBatchSelected")
		{
			UpdateSelectedCount();
		}
		else if (e.PropertyName == "ProjectStorageSummary" && ReferenceEquals(sender, selectedProject))
		{
			projectSizeText.Text = selectedProject.ProjectStorageSummary;
		}
	}

	private void UpdateSelectedCount()
	{
		int selectedProjects = projects.Count((ProjectGroup project) => project.IsBatchSelected);
		int selectedConversations = projects.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>()).Count((SessionInfo session) => !session.IsSubagent && session.IsSelected);
		if (projectBackupMode)
		{
			selectedCountText.Text = UiLanguage.T("已选 " + selectedProjects + " 个项目＋对话");
			selectionHelpText.Text = UiLanguage.T("生成 .codexproject：项目目录、主对话和子代理对话一起备份");
			backupProjectFilesButton.IsEnabled = !isBusy && selectedProjects > 0;
		}
		else
		{
			int selectedFromProjects = projects.Count((ProjectGroup project) => (project.Sessions ?? new List<SessionInfo>()).Any((SessionInfo session) => !session.IsSubagent && session.IsSelected));
			selectedCountText.Text = UiLanguage.T("已选 " + selectedConversations + " 个对话 · 来自 " + selectedFromProjects + " 个项目");
			selectionHelpText.Text = UiLanguage.T("生成 .codexchat：只备份勾选的主对话，不包含项目目录");
			backupSelectedButton.IsEnabled = !isBusy && selectedConversations > 0;
		}
	}

	private List<SessionInfo> CurrentSessionTypeItems()
	{
		return selectedProject?.Sessions.Where((SessionInfo session) => session.IsSubagent == showSubagentSessions).ToList() ?? new List<SessionInfo>();
	}
	private List<SessionInfo> CurrentVisibleSessionTypeItems()
	{
		return sessionView?.Cast<object>().OfType<SessionInfo>().Where((SessionInfo session) => session.IsSubagent == showSubagentSessions).ToList() ?? new List<SessionInfo>();
	}


	private void UpdateSessionSelectionControls()
	{
		UpdateDshControls();
		List<SessionInfo> sessions = CurrentVisibleSessionTypeItems();
		int selectedCount = sessions.Count((SessionInfo session) => session.IsSelected);
		bool allSelected = sessions.Count > 0 && selectedCount == sessions.Count;
		toggleSessionSelectionButton.Content = UiLanguage.T(allSelected ? "全不选" : "全选");
		toggleSessionSelectionButton.IsEnabled = !isBusy && sessions.Count > 0;
		deleteSelectedSessionsButton.Content = selectedCount > 0 ? UiLanguage.T("删除所选") + " (" + selectedCount + ")" : UiLanguage.T("删除所选");
		deleteSelectedSessionsButton.IsEnabled = !isBusy && selectedCount > 0;
		if (selectedProject != null)
		{
			string typeLabel = showSubagentSessions ? "子代理" : "主对话";
			List<SessionInfo> allTypeSessions = CurrentSessionTypeItems();
			int totalSelected = allTypeSessions.Count((SessionInfo session) => session.IsSelected);
			BatchProjectDeleteScope projectScope = showSubagentSessions ? null : BuildBatchProjectDeleteScope(selectedProject, sessions.Where((SessionInfo session) => session.IsSelected));
			bool allProjectMainSelected = projectScope != null && projectScope.AllMainConversationsSelected && string.IsNullOrWhiteSpace(projectScope.AvailabilityBlockReason) && Directory.Exists(projectScope.ProjectPath);
			bool allVisibleMainSelected = !showSubagentSessions && allTypeSessions.Count > 0 && sessions.Count == allTypeSessions.Count && totalSelected == allTypeSessions.Count;
			if (allProjectMainSelected)
			{
				sessionModeHint.Text = UiLanguage.T("已选中该项目全部主对话；“删除所选”中可选择同时处理项目目录。");
			}
			else if (allVisibleMainSelected && projectScope != null && !string.IsNullOrWhiteSpace(projectScope.AvailabilityBlockReason))
			{
				sessionModeHint.Text = projectScope.AvailabilityBlockReason;
			}
			else if (allVisibleMainSelected && projectScope != null && !Directory.Exists(projectScope.ProjectPath))
			{
				sessionModeHint.Text = UiLanguage.IsEnglish ? "All main conversations are selected, but the recorded project folder does not exist." : "已选中全部主对话，但记录的项目目录不存在，不能同时处理项目目录。";
			}
			else
			{
				sessionModeHint.Text = UiLanguage.T("勾选要处理的" + typeLabel + " · 已选 " + totalSelected + "/" + allTypeSessions.Count + " 个；全部选中后再次点击“全不选”。");
			}
			deleteSelectedSessionsButton.ToolTip = showSubagentSessions
				? UiLanguage.T("删除已勾选的子代理对话；项目目录保持不变")
				: allProjectMainSelected
					? UiLanguage.T("删除已勾选的全部主对话，并可选择同时处理项目目录")
					: projectScope != null && !string.IsNullOrWhiteSpace(projectScope.AvailabilityBlockReason)
						? projectScope.AvailabilityBlockReason
						: UiLanguage.T("删除已勾选的主对话；选中该项目全部主对话后可同时处理项目目录");
		}
	}

	private void ToggleSessionSelection()
	{
		List<SessionInfo> sessions = CurrentVisibleSessionTypeItems();
		bool selectAll = sessions.Count > 0 && sessions.Any((SessionInfo session) => !session.IsSelected);
		foreach (SessionInfo session in sessions)
		{
			session.IsSelected = selectAll;
		}
		UpdateSessionSelectionControls();
	}

	private void UpdateBackupMode()
	{
		bool newProjectMode = projectBackupModeRadio.IsChecked == true;
		if (newProjectMode != projectBackupMode)
		{
			if (newProjectMode)
			{
				foreach (SessionInfo session in projects.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>()))
				{
					session.IsSelected = false;
				}
			}
			else
			{
				foreach (ProjectGroup project in projects)
				{
					project.IsBatchSelected = false;
				}
			}
		}
		projectBackupMode = newProjectMode;
		projectList.Tag = projectBackupMode ? "Project" : "Conversation";
		projectSelectionTools.Visibility = projectBackupMode ? Visibility.Visible : Visibility.Collapsed;
		backupProjectFilesButton.Visibility = projectBackupMode ? Visibility.Visible : Visibility.Collapsed;
		backupSelectedButton.Visibility = projectBackupMode ? Visibility.Collapsed : Visibility.Visible;
		projectPaneTitle.Text = UiLanguage.T(projectBackupMode ? "选择项目＋对话" : "选择仅对话");
		projectPaneSubtitle.Text = UiLanguage.T(projectBackupMode ? "生成 .codexproject，包含项目目录和全部关联对话" : "生成 .codexchat，只包含勾选的主对话");
		backupModeHelpText.Text = UiLanguage.T(projectBackupMode ? "项目＋对话备份：项目文件、主对话和子代理对话放在同一个文件里。" : "仅对话备份：可跨项目勾选主对话，不包含项目文件。");
		UpdateSessionTypeView();
		UpdateSelectedCount();
	}

	private void SetProjectSelection(bool value)
	{
		foreach (ProjectGroup project in projects)
		{
			if (project.CanBackupFiles)
			{
				project.IsBatchSelected = value;
			}
		}
		UpdateSelectedCount();
	}

	private void SetVisibleSelection(bool value)
	{
		if (sessionView == null)
		{
			return;
		}
		foreach (SessionInfo item in sessionView.Cast<object>().OfType<SessionInfo>())
		{
			if (item.CanSelect)
			{
				item.IsSelected = value;
			}
		}
		UpdateSelectedCount();
	}

	private async void SessionActionButtonClick(object sender, RoutedEventArgs e)
	{
		System.Windows.Controls.Button button = ResolveActionButton(e.OriginalSource as DependencyObject) ?? (e.Source as System.Windows.Controls.Button);
		if (button != null && button.CommandParameter is SessionInfo session)
		{
			if (string.Equals(button.Name, "ViewSessionButton", StringComparison.Ordinal))
			{
				e.Handled = true;
				await ShowConversationAsync(session);
			}
			else if (string.Equals(button.Name, "DeleteSessionButton", StringComparison.Ordinal))
			{
				e.Handled = true;
				await DeleteSessionAsync(session);
			}
		}
	}

	private static System.Windows.Controls.Button ResolveActionButton(DependencyObject source)
	{
		DependencyObject dependencyObject = source;
		while (dependencyObject != null)
		{
			if (dependencyObject is System.Windows.Controls.Button result)
			{
				return result;
			}
			DependencyObject dependencyObject2 = null;
			try
			{
				dependencyObject2 = VisualTreeHelper.GetParent(dependencyObject);
			}
			catch
			{
			}
			if (dependencyObject2 == null)
			{
				try
				{
					dependencyObject2 = LogicalTreeHelper.GetParent(dependencyObject);
				}
				catch
				{
				}
			}
			dependencyObject = dependencyObject2;
		}
		return null;
	}

	private async Task ShowConversationAsync(SessionInfo session)
	{
		if (session == null)
		{
			return;
		}
		int requestVersion = unchecked(++conversationPreviewRequestVersion);
		string requestedThreadId = session.ThreadId ?? string.Empty;
		previewedThreadId = requestedThreadId;
		conversationTitleText.Text = session.DisplayTitle;
		conversationMetaText.Text = UiLanguage.T("正在读取本地会话……") + " · " + session.ShortId;
		previewMessages = new ConversationMessage[1]
		{
			new ConversationMessage
			{
				RoleLabel = UiLanguage.T("提示"),
				Text = UiLanguage.T("正在整理对话内容……"),
				IsNotice = true
			}
		};
		conversationList.ItemsSource = previewMessages;
		activeConversationMessageIndex = -1;
		RedrawConversationNavigationRail();
		conversationOverlay.Visibility = Visibility.Visible;
		InitializeConversationDialog();
		try
		{
			ConversationReadResult result = await Task.Run(() => BackendContext.IsDsh ? DshService.ReadConversation(session) : ConversationReader.Read(session));
			if (!IsConversationPreviewRequestCurrent(requestVersion, requestedThreadId))
			{
				return;
			}
			previewMessages = result.Messages;
			conversationList.ItemsSource = previewMessages;
			int visibleCount = result.Messages.Count((ConversationMessage x) => !x.IsNotice);
			conversationMetaText.Text = UiLanguage.IsEnglish ? string.Format("{0} text messages · Thread {1}", visibleCount, session.ThreadId) : string.Format("{0} 条文本消息 · Thread {1}", visibleCount, session.ThreadId);
			activeConversationMessageIndex = result.Messages.Count - 1;
			RedrawConversationNavigationRail();
			if (result.Messages.Count > 0)
			{
				_ = window.Dispatcher.BeginInvoke(new Action(delegate
				{
					if (!IsConversationPreviewRequestCurrent(requestVersion, requestedThreadId) || !ReferenceEquals(conversationList.ItemsSource, result.Messages))
					{
						return;
					}
					ScrollConversationToIndex(result.Messages.Count - 1, alignToEnd: true);
				}), System.Windows.Threading.DispatcherPriority.Loaded);
			}
		}
		catch (Exception ex)
		{
			if (!IsConversationPreviewRequestCurrent(requestVersion, requestedThreadId))
			{
				return;
			}
			previewMessages = new ConversationMessage[1]
			{
				new ConversationMessage
				{
					RoleLabel = UiLanguage.T("无法打开"),
					Text = ex.Message,
					IsNotice = true
				}
			};
			conversationList.ItemsSource = previewMessages;
			activeConversationMessageIndex = 0;
			RedrawConversationNavigationRail();
			conversationMetaText.Text = UiLanguage.T("读取失败") + " · " + session.ShortId;
		}
	}

	private void HideConversation()
	{
		InvalidateConversationPreviewRequests();
		conversationOverlay.Visibility = Visibility.Collapsed;
		conversationList.ItemsSource = null;
		previewMessages = Array.Empty<ConversationMessage>();
		conversationScrollViewer = null;
		activeConversationMessageIndex = -1;
		RedrawConversationNavigationRail();
		previewedThreadId = string.Empty;
	}

	private void InvalidateConversationPreviewRequests()
	{
		conversationPreviewRequestVersion = unchecked(conversationPreviewRequestVersion + 1);
	}

	private bool IsConversationPreviewRequestCurrent(int requestVersion, string threadId)
	{
		return requestVersion == conversationPreviewRequestVersion &&
			conversationOverlay.Visibility == Visibility.Visible &&
			string.Equals(previewedThreadId, threadId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private void ConversationNavigationMarkerMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is System.Windows.Controls.Button button && button.Tag is ConversationNavigationItem item)
		{
			int navigationIndex = conversationNavigationItems.IndexOf(item);
			ExpandConversationNavigationAround(navigationIndex);
			ShowConversationNavigationPreview(item);
		}
	}

	private void ConversationNavigationMarkerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left || !(sender is System.Windows.Controls.Button button) || !(button.Tag is ConversationNavigationItem item))
		{
			return;
		}
		button.Focus();
		conversationNavigationScrubbing = true;
		conversationNavigationMoved = false;
		conversationNavigationPressedItem = item;
		conversationNavigationPressPoint = e.GetPosition(conversationNavigationRail);
		conversationNavigationRail.CaptureMouse();
		ExpandConversationNavigationAround(conversationNavigationItems.IndexOf(item));
		ShowConversationNavigationPreview(item);
		e.Handled = true;
	}

	private void ConversationNavigationRailMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (!conversationNavigationScrubbing || e.LeftButton != MouseButtonState.Pressed || conversationNavigationItems.Count == 0)
		{
			return;
		}
		Point position = e.GetPosition(conversationNavigationRail);
		if (!conversationNavigationMoved && Math.Abs(position.Y - conversationNavigationPressPoint.Y) < 2.0)
		{
			return;
		}
		conversationNavigationMoved = true;
		int navigationIndex = Math.Max(0, Math.Min(conversationNavigationItems.Count - 1, (int)Math.Floor(position.Y / 10.0)));
		ConversationNavigationItem item = conversationNavigationItems[navigationIndex];
		if (!ReferenceEquals(item, conversationNavigationPressedItem))
		{
			conversationNavigationPressedItem = item;
			ScrollConversationToIndex(item.MessageIndex, alignToEnd: false);
			ExpandConversationNavigationAround(navigationIndex);
			ShowConversationNavigationPreview(item);
		}
		e.Handled = true;
	}

	private void ConversationNavigationRailMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (!conversationNavigationScrubbing || e.ChangedButton != MouseButton.Left)
		{
			return;
		}
		ConversationNavigationItem item = conversationNavigationPressedItem;
		bool moved = conversationNavigationMoved;
		conversationNavigationScrubbing = false;
		conversationNavigationMoved = false;
		conversationNavigationPressedItem = null;
		conversationNavigationRail.ReleaseMouseCapture();
		if (!moved && item != null)
		{
			ScrollConversationToIndex(item.MessageIndex, alignToEnd: false);
		}
		e.Handled = true;
	}

	private void ConversationNavigationMarkerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (!(sender is System.Windows.Controls.Button button) || !(button.Tag is ConversationNavigationItem item) || conversationNavigationItems.Count == 0)
		{
			return;
		}
		int current = conversationNavigationItems.IndexOf(item);
		int target = current;
		switch (e.Key)
		{
		case Key.Enter:
		case Key.Space:
			ScrollConversationToIndex(item.MessageIndex, alignToEnd: false);
			e.Handled = true;
			return;
		case Key.Home:
			target = 0;
			break;
		case Key.End:
			target = conversationNavigationItems.Count - 1;
			break;
		case Key.Up:
			target = Math.Max(0, current - 1);
			break;
		case Key.Down:
			target = Math.Min(conversationNavigationItems.Count - 1, current + 1);
			break;
		case Key.PageUp:
			target = Math.Max(0, current - 10);
			break;
		case Key.PageDown:
			target = Math.Min(conversationNavigationItems.Count - 1, current + 10);
			break;
		case Key.Escape:
			CloseConversationNavigationPreview();
			ExpandConversationNavigationAround(-1);
			e.Handled = true;
			return;
		default:
			return;
		}
		ConversationNavigationItem targetItem = conversationNavigationItems[target];
		targetItem.Button.Focus();
		EnsureConversationNavigationItemVisible(target);
		ExpandConversationNavigationAround(target);
		ShowConversationNavigationPreview(targetItem);
		e.Handled = true;
	}

	private void ConversationListScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		ScrollViewer mainScroller = conversationScrollViewer ?? ResolveConversationScrollViewer();
		if (mainScroller == null || !ReferenceEquals(e.OriginalSource, mainScroller))
		{
			return;
		}
		conversationScrollViewer = mainScroller;
		if (previewMessages.Count == 0)
		{
			UpdateConversationNavigationActive(-1);
			return;
		}
		if (mainScroller.ScrollableHeight <= 0.0 || mainScroller.VerticalOffset >= mainScroller.ScrollableHeight - 0.5)
		{
			UpdateConversationNavigationActive(previewMessages.Count - 1);
			return;
		}
		VirtualizingStackPanel panel = FindVisualDescendant<VirtualizingStackPanel>(conversationList);
		int firstVisibleIndex = -1;
		double closestTop = double.MaxValue;
		if (panel != null)
		{
			foreach (UIElement child in panel.Children)
			{
				if (!(child is ListBoxItem item))
				{
					continue;
				}
				try
				{
					double top = item.TransformToAncestor(conversationList).Transform(new Point(0.0, 0.0)).Y;
					double bottom = top + item.ActualHeight;
					if (bottom >= 0.0 && top < closestTop)
					{
						closestTop = top;
						firstVisibleIndex = conversationList.ItemContainerGenerator.IndexFromContainer(item);
					}
				}
				catch
				{
				}
			}
		}
		if (firstVisibleIndex >= 0)
		{
			UpdateConversationNavigationActive(firstVisibleIndex);
		}
	}

	private void ScrollConversationToIndex(int index, bool alignToEnd)
	{
		if (previewMessages.Count == 0)
		{
			return;
		}
		int safeIndex = Math.Max(0, Math.Min(previewMessages.Count - 1, index));
		ConversationMessage message = previewMessages[safeIndex];
		conversationList.SelectedIndex = safeIndex;
		conversationList.ScrollIntoView(message);
		conversationList.UpdateLayout();
		conversationScrollViewer = conversationScrollViewer ?? ResolveConversationScrollViewer();
		if (alignToEnd)
		{
			conversationScrollViewer?.ScrollToEnd();
		}
		UpdateConversationNavigationActive(safeIndex);
	}

	private ScrollViewer ResolveConversationScrollViewer()
	{
		conversationList.ApplyTemplate();
		return conversationList.Template?.FindName("PART_ScrollViewer", conversationList) as ScrollViewer ?? FindVisualDescendant<ScrollViewer>(conversationList);
	}

	private void RedrawConversationNavigationRail()
	{
		CloseConversationNavigationPreview();
		conversationNavigationItems.Clear();
		conversationNavigationRail.Children.Clear();
		for (int messageIndex = 0; messageIndex < previewMessages.Count; messageIndex++)
		{
			ConversationMessage userMessage = previewMessages[messageIndex];
			if (!userMessage.IsUser || userMessage.IsNotice)
			{
				continue;
			}
			ConversationMessage responseMessage = null;
			for (int responseIndex = messageIndex + 1; responseIndex < previewMessages.Count; responseIndex++)
			{
				ConversationMessage candidate = previewMessages[responseIndex];
				if (candidate.IsUser && !candidate.IsNotice)
				{
					break;
				}
				if (!candidate.IsUser && !candidate.IsNotice)
				{
					responseMessage = candidate;
					break;
				}
			}
			Border marker = new Border
			{
				Width = 6.0,
				Height = 2.0,
				CornerRadius = new CornerRadius(1.0),
				Background = Brush("#B6BBB5"),
				Opacity = 0.78,
				IsHitTestVisible = false,
				SnapsToDevicePixels = true
			};
			System.Windows.Controls.Button button = new System.Windows.Controls.Button
			{
				Style = (Style)window.FindResource("ConversationNavigationButton"),
				Content = marker
			};
			ConversationNavigationItem item = new ConversationNavigationItem
			{
				MessageIndex = messageIndex,
				UserMessage = userMessage,
				ResponseMessage = responseMessage,
				Button = button,
				Marker = marker
			};
			button.Tag = item;
			System.Windows.Automation.AutomationProperties.SetName(button, UiLanguage.IsEnglish
				? "Jump to user message " + (conversationNavigationItems.Count + 1)
				: "跳转到第 " + (conversationNavigationItems.Count + 1) + " 条用户消息");
			button.MouseEnter += ConversationNavigationMarkerMouseEnter;
			button.PreviewMouseLeftButtonDown += ConversationNavigationMarkerMouseLeftButtonDown;
			button.KeyDown += ConversationNavigationMarkerKeyDown;
			button.GotKeyboardFocus += delegate
			{
				int focusedIndex = conversationNavigationItems.IndexOf(item);
				ExpandConversationNavigationAround(focusedIndex);
				ShowConversationNavigationPreview(item);
			};
			conversationNavigationItems.Add(item);
			conversationNavigationRail.Children.Add(button);
		}
		bool visible = conversationNavigationItems.Count >= 4;
		conversationNavigationHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		conversationNavigationHost.ToolTip = null;
		UpdateConversationNavigationActive(activeConversationMessageIndex);
	}

	private void UpdateConversationNavigationActive(int index)
	{
		activeConversationMessageIndex = index;
		int navigationIndex = -1;
		if (index >= 0)
		{
			for (int i = 0; i < conversationNavigationItems.Count; i++)
			{
				if (conversationNavigationItems[i].MessageIndex > index)
				{
					break;
				}
				navigationIndex = i;
			}
			if (navigationIndex < 0 && conversationNavigationItems.Count > 0)
			{
				navigationIndex = 0;
			}
		}
		activeConversationNavigationIndex = navigationIndex;
		for (int i = 0; i < conversationNavigationItems.Count; i++)
		{
			bool active = i == navigationIndex;
			conversationNavigationItems[i].Marker.Background = Brush(active ? "#343A35" : "#B6BBB5");
			conversationNavigationItems[i].Marker.Opacity = active ? 1.0 : 0.78;
		}
		if (navigationIndex >= 0 && conversationNavigationHost.Visibility == Visibility.Visible)
		{
			EnsureConversationNavigationItemVisible(navigationIndex);
		}
	}

	private void EnsureConversationNavigationItemVisible(int navigationIndex)
	{
		if (navigationIndex < 0 || navigationIndex >= conversationNavigationItems.Count || conversationNavigationScroller.ViewportHeight <= 0.0)
		{
			return;
		}
		double top = navigationIndex * 10.0;
		double bottom = top + 10.0;
		double viewportTop = conversationNavigationScroller.VerticalOffset;
		double viewportBottom = viewportTop + conversationNavigationScroller.ViewportHeight;
		if (top < viewportTop + 10.0)
		{
			conversationNavigationScroller.ScrollToVerticalOffset(Math.Max(0.0, top - 22.0));
		}
		else if (bottom > viewportBottom - 10.0)
		{
			conversationNavigationScroller.ScrollToVerticalOffset(Math.Max(0.0, bottom - conversationNavigationScroller.ViewportHeight + 22.0));
		}
	}

	private void ExpandConversationNavigationAround(int navigationIndex)
	{
		for (int i = 0; i < conversationNavigationItems.Count; i++)
		{
			int distance = navigationIndex < 0 ? int.MaxValue : Math.Abs(i - navigationIndex);
			double width = distance switch
			{
				0 => 26.0,
				1 => 20.0,
				2 => 14.0,
				3 => 10.0,
				_ => 6.0
			};
			AnimateConversationNavigationMarker(conversationNavigationItems[i].Marker, width);
		}
	}

	private static void AnimateConversationNavigationMarker(Border marker, double targetWidth)
	{
		double startWidth = marker.ActualWidth > 0.0 ? marker.ActualWidth : marker.Width;
		marker.BeginAnimation(FrameworkElement.WidthProperty, null);
		marker.Width = targetWidth;
		if (Math.Abs(startWidth - targetWidth) < 0.1)
		{
			return;
		}
		DoubleAnimation animation = new DoubleAnimation(startWidth, targetWidth, TimeSpan.FromMilliseconds(150.0))
		{
			EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		};
		marker.BeginAnimation(FrameworkElement.WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);
	}

	private void ShowConversationNavigationPreview(ConversationNavigationItem item)
	{
		if (item == null || item.Button == null)
		{
			return;
		}
		conversationNavigationPreviewTitle.Text = CompactConversationPreview(item.UserMessage?.Text, 150, UiLanguage.IsEnglish ? "User message" : "用户消息");
		conversationNavigationPreviewResponse.Text = item.ResponseMessage == null
			? (UiLanguage.IsEnglish ? "No text response follows this message." : "这条用户消息后没有文本回复。")
			: CompactConversationPreview(item.ResponseMessage.Text, 420, UiLanguage.IsEnglish ? "Response" : "回复");
		conversationNavigationPreviewPopup.PlacementTarget = item.Button;
		conversationNavigationPreviewPopup.IsOpen = true;
	}

	private static string CompactConversationPreview(string text, int maximumCharacters, string fallback)
	{
		string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
		while (normalized.Contains("\n\n\n"))
		{
			normalized = normalized.Replace("\n\n\n", "\n\n");
		}
		if (normalized.Length == 0)
		{
			return fallback;
		}
		if (normalized.Length > maximumCharacters)
		{
			normalized = normalized.Substring(0, maximumCharacters).TrimEnd() + "…";
		}
		return normalized;
	}

	private void CloseConversationNavigationPreview()
	{
		conversationNavigationPreviewPopup.IsOpen = false;
		conversationNavigationPreviewPopup.PlacementTarget = null;
	}

	private static T FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
	{
		if (root == null)
		{
			return null;
		}
		int childCount;
		try
		{
			childCount = VisualTreeHelper.GetChildrenCount(root);
		}
		catch
		{
			return null;
		}
		for (int index = 0; index < childCount; index++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, index);
			if (child is T match)
			{
				return match;
			}
			T nested = FindVisualDescendant<T>(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	private void CopyPreviewedThreadId()
	{
		if (string.IsNullOrWhiteSpace(previewedThreadId))
		{
			return;
		}
		try
		{
			System.Windows.Clipboard.SetText(previewedThreadId);
			SetStatus("已复制 Thread ID：" + previewedThreadId, error: false);
		}
		catch (Exception ex)
		{
			SetStatus("复制失败：" + ex.Message, error: true);
		}
	}

	private async Task DeleteSessionAsync(SessionInfo session)
	{
		if (isBusy || session == null)
		{
			return;
		}
		if (!EnsureCodexClosedForConversationWrite())
		{
			return;
		}
		bool deletingSubagent = session.IsSubagent;
		List<SessionInfo> affectedSessions = BuildDeletionClosure(session);
		int descendantCount = Math.Max(0, affectedSessions.Count - 1);
		string projectPath = ProviderStorage.ResolveProjectPath(session, selectedProject);
		int relatedConversationCount = CountRelatedConversations(projectPath);
		DeleteOptions options = DeleteOptionsDialog.Show(window, session, projectPath, relatedConversationCount, allowProjectActions: !deletingSubagent);
		if (options == null)
		{
			return;
		}
		if (options.ProjectMode != ProjectDeleteMode.None)
		{
			try
			{
				projectPath = ProviderStorage.ValidateProjectPath(projectPath);
			}
			catch (Exception ex)
			{
				AppDialog.ShowCompat(window, ex.Message, "不能处理项目目录", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
		}
		string conversationSummary = options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "会话：移入软件回收站（可恢复）" : "会话：永久删除（不可恢复）";
		string projectSummary = options.ProjectMode switch
		{
			ProjectDeleteMode.RecycleBin => "\n项目：移入 Windows 回收站",
			ProjectDeleteMode.Permanent => "\n项目：永久递归删除（不可恢复）",
			_ => "\n项目：保留不动"
		};
		string cascadeSummary = descendantCount > 0
			? (options.ConversationMode == ConversationDeleteMode.MoveToTrash
				? "\n关联子代理：" + descendantCount + " 个；会分别移入软件回收站，可单独恢复"
				: "\n关联子代理：" + descendantCount + " 个；会随主对话永久删除")
			: string.Empty;
		string confirmationText = UiLanguage.IsEnglish
			? "Confirm this operation:\n\n" + session.DisplayTitle + "\n\nConversation: " + (options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "move to the app trash (recoverable)" : "permanently delete") + (descendantCount > 0 ? "\nSpawned descendants: " + descendantCount + (options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "; each will receive its own recoverable trash copy" : "; all will also be permanently deleted") : string.Empty) + (options.ProjectMode == ProjectDeleteMode.RecycleBin ? "\nProject: move to Windows Recycle Bin" : options.ProjectMode == ProjectDeleteMode.Permanent ? "\nProject: permanently delete" : "\nProject: keep unchanged") + "\n\nThe Codex thread/delete protocol is applied first, followed by local file and index cleanup."
			: "请确认本次操作：\n\n" + session.DisplayTitle + "\n\n" + conversationSummary + cascadeSummary + projectSummary + "\n\n本次操作会先通过 Codex 官方删除接口更新任务目录，再处理本地会话文件与索引。";
		string confirmationTitle = UiLanguage.IsEnglish ? (deletingSubagent ? "Confirm subagent deletion" : "Confirm deletion") : (deletingSubagent ? "确认删除子代理对话" : "确认删除");
		MessageBoxResult answer = AppDialog.ShowCompat(window, confirmationText, confirmationTitle, MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
		if (answer != MessageBoxResult.Yes)
		{
			return;
		}
		string projectId = ((selectedProject == null) ? string.Empty : selectedProject.ProjectId);
		SetBusy(busy: true, options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "正在把会话移入软件回收站……" : "正在永久删除会话……");
		DeleteOperationResult operation = null;
		try
		{
			try
			{
				string selectedProjectPath = projectPath;
				operation = await Task.Run(delegate
				{
					DeletedSessionResult conversation = options.ConversationMode == ConversationDeleteMode.MoveToTrash ? ProviderStorage.MoveToTrash(session, selectedProjectPath, affectedSessions) : ProviderStorage.DeletePermanently(session, affectedSessions);
					DeleteOperationResult result = new DeleteOperationResult
					{
						Conversation = conversation,
						ProjectPath = selectedProjectPath,
						ProjectMode = options.ProjectMode
					};
					if (options.ProjectMode != ProjectDeleteMode.None)
					{
						try
						{
							ProviderStorage.DeleteProject(selectedProjectPath, options.ProjectMode);
							if (!conversation.PermanentlyDeleted)
							{
								try
								{
									foreach (string backupPath in conversation.BackupPaths.Count > 0 ? conversation.BackupPaths : new List<string> { conversation.BackupPath })
									{
										ProviderStorage.MarkProjectHandled(backupPath, selectedProjectPath, options.ProjectMode);
									}
								}
								catch
								{
								}
							}
						}
						catch (Exception ex)
						{
							result.ProjectError = ex.Message;
						}
					}
					return result;
				});
			}
			catch (Exception ex)
			{
				SetStatus("删除失败：" + ex.Message, error: true);
				AppDialog.ShowCompat(window, ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Hand);
				return;
			}
		}
		finally
		{
			SetBusy(busy: false, null);
		}
		HideConversation();
		await RefreshDataAsync();
		ProjectGroup previousProject = projects.FirstOrDefault((ProjectGroup x) => string.Equals(x.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
		if (previousProject != null)
		{
			projectList.SelectedItem = previousProject;
		}
		string completion;
		if (UiLanguage.IsEnglish)
		{
			completion = operation.Conversation.PermanentlyDeleted ? "The conversation was permanently deleted." : "The conversation was moved to app trash:\n" + operation.Conversation.BackupPath;
			if (operation.Conversation.AffectedConversationCount > 1)
			{
				completion += operation.Conversation.PermanentlyDeleted ? "\n\n" + (operation.Conversation.AffectedConversationCount - 1) + " spawned descendant conversations were also permanently deleted." : "\n\n" + (operation.Conversation.AffectedConversationCount - 1) + " spawned descendant conversations were separately moved to app trash.";
			}
			if (operation.ProjectMode != ProjectDeleteMode.None)
			{
				completion += operation.ProjectSucceeded ? (operation.ProjectMode == ProjectDeleteMode.RecycleBin ? "\n\nThe project was moved to the Windows Recycle Bin." : "\n\nThe project was permanently deleted.") : "\n\nThe conversation operation completed, but project processing failed:\n" + operation.ProjectError;
			}
			completion += "\n\nAfter reopening Codex, the deleted conversation will no longer appear in the sidebar.";
		}
		else
		{
			completion = operation.Conversation.PermanentlyDeleted ? "会话已永久删除。" : "会话已移入软件回收站：\n" + operation.Conversation.BackupPath;
			if (operation.Conversation.AffectedConversationCount > 1)
			{
				completion += operation.Conversation.PermanentlyDeleted ? "\n\n同时永久删除 " + (operation.Conversation.AffectedConversationCount - 1) + " 个关联子代理对话。" : "\n\n另有 " + (operation.Conversation.AffectedConversationCount - 1) + " 个关联子代理对话已分别移入软件回收站。";
			}
			if (operation.ProjectMode != ProjectDeleteMode.None)
			{
				completion += operation.ProjectSucceeded ? (operation.ProjectMode == ProjectDeleteMode.RecycleBin ? "\n\n项目已移入 Windows 回收站。" : "\n\n项目已永久删除。") : "\n\n会话操作已完成，但项目处理失败：\n" + operation.ProjectError;
			}
			completion += "\n\n重新打开 Codex 后，该会话不会再出现在侧边栏。";
		}
		SetStatus(operation.ProjectSucceeded ? (operation.Conversation.PermanentlyDeleted ? "会话已永久删除。" : "会话已移入软件回收站。") : "会话已删除，但项目处理失败。", error: !operation.ProjectSucceeded);
		AppDialog.ShowCompat(window, completion, operation.ProjectSucceeded ? "删除完成" : "部分完成", MessageBoxButton.OK, operation.ProjectSucceeded ? MessageBoxImage.Asterisk : MessageBoxImage.Warning);
	}

	private async Task DeleteSelectedSessionsAsync()
	{
		if (isBusy || selectedProject == null)
		{
			return;
		}
		if (!EnsureCodexClosedForConversationWrite())
		{
			return;
		}
		bool deletingSubagents = showSubagentSessions;
		List<SessionInfo> selectedSessions = CurrentVisibleSessionTypeItems().Where((SessionInfo session) => session.IsSelected).ToList();
		string typeLabel = deletingSubagents ? "子代理" : "主对话";
		string otherTypeLabel = deletingSubagents ? "主对话" : "子代理";
		if (selectedSessions.Count == 0)
		{
			AppDialog.ShowCompat(window, "请先勾选一个或多个" + typeLabel + "。", "尚未选择" + typeLabel, MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		ProjectGroup sourceProject = selectedProject;
		List<SessionDeletionPlan> deletionPlans = BuildDeletionPlans(selectedSessions);
		HashSet<string> selectedIds = new HashSet<string>(selectedSessions.Select((SessionInfo item) => item.ThreadId), StringComparer.OrdinalIgnoreCase);
		List<SessionInfo> allAffectedSessions = deletionPlans.SelectMany((SessionDeletionPlan plan) => plan.AffectedSessions).GroupBy((SessionInfo item) => item.ThreadId, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, SessionInfo> group) => group.First()).ToList();
		int additionalDescendantCount = allAffectedSessions.Count((SessionInfo item) => !selectedIds.Contains(item.ThreadId));
		BatchProjectDeleteScope projectScope = BuildBatchProjectDeleteScope(sourceProject, selectedSessions);
		bool allMainConversationsSelected = !deletingSubagents && projectScope.AllMainConversationsSelected;
		string projectPath = projectScope.ProjectPath;
		DeleteOptions options = SessionBatchDeleteDialog.Show(window, sourceProject, selectedSessions, projectPath, allMainConversationsSelected, projectScope.TotalMainConversationCount, projectScope.AvailabilityBlockReason);
		if (options == null)
		{
			return;
		}
		if (options.ProjectMode != ProjectDeleteMode.None)
		{
			try
			{
				if (!allMainConversationsSelected || !string.IsNullOrWhiteSpace(projectScope.AvailabilityBlockReason))
				{
					throw new InvalidOperationException(projectScope.AvailabilityBlockReason ?? (UiLanguage.IsEnglish ? "The project folder cannot be processed unless every related main conversation is selected." : "只有选中该目录关联的全部主对话后，才能处理项目目录。"));
				}
				projectPath = ProviderStorage.ValidateProjectPath(projectPath);
			}
			catch (Exception ex)
			{
				AppDialog.ShowCompat(window, ex.Message, "不能处理项目目录", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
		}
		if (options.ConversationMode == ConversationDeleteMode.Permanent || additionalDescendantCount > 0 || options.ProjectMode != ProjectDeleteMode.None)
		{
			string impactText;
			string impactTitle;
			string projectImpact = options.ProjectMode switch
			{
				ProjectDeleteMode.RecycleBin => UiLanguage.IsEnglish ? "\n\nProject folder: move to the Windows Recycle Bin after all conversations succeed." : "\n\n项目目录：全部会话处理成功后移入 Windows 回收站。",
				ProjectDeleteMode.Permanent => UiLanguage.IsEnglish ? "\n\nProject folder: permanently delete after all conversations succeed." : "\n\n项目目录：全部会话处理成功后永久删除（不可恢复）。",
				_ => UiLanguage.IsEnglish ? "\n\nThe project folder remains unchanged." : "\n\n项目目录保持不变。"
			};
			if (UiLanguage.IsEnglish)
			{
				impactText = options.ConversationMode == ConversationDeleteMode.Permanent
					? "Permanently delete " + allAffectedSessions.Count + " conversations (" + TextHelpers.FormatBytes(allAffectedSessions.Sum((SessionInfo item) => item.SizeBytes)) + "), including " + additionalDescendantCount + " spawned descendants that were not separately selected."
					: "Move " + allAffectedSessions.Count + " conversations to the app trash, including " + additionalDescendantCount + " spawned descendants that were not separately selected. Each receives an independent recoverable copy.";
				impactText += projectImpact;
				impactTitle = options.ConversationMode == ConversationDeleteMode.Permanent
					? "Confirm permanent deletion"
					: options.ProjectMode != ProjectDeleteMode.None ? "Confirm conversation and project deletion" : "Confirm descendant handling";
			}
			else
			{
				impactText = options.ConversationMode == ConversationDeleteMode.Permanent
					? "将永久删除共 " + allAffectedSessions.Count + " 个对话（" + TextHelpers.FormatBytes(allAffectedSessions.Sum((SessionInfo item) => item.SizeBytes)) + "），其中包含 " + additionalDescendantCount + " 个未单独勾选、但由所选对话生成的子代理。"
					: "将把共 " + allAffectedSessions.Count + " 个对话移入软件回收站，其中包含 " + additionalDescendantCount + " 个未单独勾选、但由所选对话生成的子代理；每个对话都会保留独立、可恢复的副本。";
				impactText += projectImpact;
				impactTitle = options.ConversationMode == ConversationDeleteMode.Permanent
					? "确认永久删除"
					: options.ProjectMode != ProjectDeleteMode.None ? "确认删除对话和项目" : "确认处理关联子代理";
			}
			MessageBoxResult permanentAnswer = AppDialog.ShowCompat(window, impactText, impactTitle, MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
			if (permanentAnswer != MessageBoxResult.Yes)
			{
				return;
			}
		}

		string projectId = sourceProject.ProjectId;
		string busyMessage = UiLanguage.IsEnglish
			? options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "Moving the selected " + (deletingSubagents ? "subagent conversations" : "main conversations") + " to app trash…" : "Permanently deleting the selected " + (deletingSubagents ? "subagent conversations" : "main conversations") + "…"
			: options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "正在把所选" + typeLabel + "移入软件回收站……" : "正在永久删除所选" + typeLabel + "……";
		SetBusy(busy: true, busyMessage);
		int completed = 0;
		int affectedCompleted = 0;
		List<string> errors = new List<string>();
		List<DeletedSessionResult> deletionResults = new List<DeletedSessionResult>();
		bool projectProcessed = false;
		string projectError = string.Empty;
		try
		{
			await Task.Run(delegate
			{
				foreach (SessionDeletionPlan plan in deletionPlans)
				{
					SessionInfo session = plan.Root;
					try
					{
						DeletedSessionResult deletionResult;
						if (options.ConversationMode == ConversationDeleteMode.MoveToTrash)
						{
							deletionResult = ProviderStorage.MoveToTrash(session, projectPath, plan.AffectedSessions);
						}
						else
						{
							deletionResult = ProviderStorage.DeletePermanently(session, plan.AffectedSessions);
						}
						deletionResults.Add(deletionResult);
						completed += plan.AffectedSessions.Count((SessionInfo item) => selectedIds.Contains(item.ThreadId));
						affectedCompleted += plan.AffectedSessions.Count;
					}
					catch (Exception ex)
					{
						errors.Add(session.ShortId + " · " + ex.Message);
					}
				}
				if (options.ProjectMode == ProjectDeleteMode.None)
				{
					return;
				}
				if (errors.Count > 0)
				{
					projectError = UiLanguage.IsEnglish
						? "The project folder was not processed because one or more conversation operations failed."
						: "有会话处理失败；为避免数据丢失，项目目录未处理。";
					return;
				}
				try
				{
					ProviderStorage.DeleteProject(projectPath, options.ProjectMode);
					projectProcessed = true;
					if (options.ConversationMode == ConversationDeleteMode.MoveToTrash)
					{
						List<string> markErrors = new List<string>();
						IEnumerable<string> backupPaths = deletionResults
							.SelectMany((DeletedSessionResult item) => item.BackupPaths.Count > 0 ? item.BackupPaths : new List<string> { item.BackupPath })
							.Where((string path) => !string.IsNullOrWhiteSpace(path))
							.Distinct(StringComparer.OrdinalIgnoreCase);
						foreach (string backupPath in backupPaths)
						{
							try
							{
								ProviderStorage.MarkProjectHandled(backupPath, projectPath, options.ProjectMode);
							}
							catch (Exception ex)
							{
								markErrors.Add(ex.Message);
							}
						}
						if (markErrors.Count > 0)
						{
							projectError = UiLanguage.IsEnglish
								? "The project folder was processed, but some app-trash metadata could not be updated: " + markErrors[0]
								: "项目目录已处理，但部分软件回收站记录未能更新：" + markErrors[0];
						}
					}
				}
				catch (Exception ex)
				{
					projectError = ex.Message;
				}
			});
		}
		finally
		{
			SetBusy(busy: false, null);
		}

		HideConversation();
		await RefreshDataAsync();
		ProjectGroup previousProject = projects.FirstOrDefault((ProjectGroup project) => string.Equals(project.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
		if (previousProject != null)
		{
			projectList.SelectedItem = previousProject;
		}
		if (deletingSubagents)
		{
			subagentSessionsTabRadio.IsChecked = true;
		}
		else
		{
			mainSessionsTabRadio.IsChecked = true;
		}
		UpdateSessionTypeView();
		string action = options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "移入软件回收站" : "永久删除";
		string resultText = UiLanguage.IsEnglish
			? (options.ConversationMode == ConversationDeleteMode.MoveToTrash ? "Moved " : "Permanently deleted ") + completed + " selected " + (deletingSubagents ? "subagent conversations" : "main conversations") + "; " + affectedCompleted + " conversations were processed in total."
			: "已" + action + " " + completed + " 个所选" + typeLabel + "，实际处理 " + affectedCompleted + " 个对话。";
		if (options.ProjectMode == ProjectDeleteMode.None)
		{
			resultText += UiLanguage.IsEnglish
				? "\n\nUnselected conversations outside the selected descendant relationships and the project folder remain unchanged."
				: "\n\n不属于所选对话后代关系的未选" + typeLabel + "、" + otherTypeLabel + "和项目目录保持不变。";
		}
		else if (projectProcessed)
		{
			resultText += UiLanguage.IsEnglish
				? (options.ProjectMode == ProjectDeleteMode.RecycleBin ? "\n\nThe project folder was moved to the Windows Recycle Bin." : "\n\nThe project folder was permanently deleted.")
				: (options.ProjectMode == ProjectDeleteMode.RecycleBin ? "\n\n项目目录已移入 Windows 回收站。" : "\n\n项目目录已永久删除。");
		}
		else
		{
			resultText += UiLanguage.IsEnglish ? "\n\nProject folder not processed: " + projectError : "\n\n项目目录未处理：" + projectError;
		}
		if (errors.Count > 0)
		{
			resultText += UiLanguage.IsEnglish ? "\n\n" + errors.Count + " operations failed:\n" + string.Join("\n", errors.Take(8)) : "\n\n有 " + errors.Count + " 个处理失败：\n" + string.Join("\n", errors.Take(8));
		}
		else if (projectProcessed && !string.IsNullOrWhiteSpace(projectError))
		{
			resultText += UiLanguage.IsEnglish ? "\n\nProject note: " + projectError : "\n\n项目提示：" + projectError;
		}
		resultText += UiLanguage.IsEnglish ? "\n\nAfter reopening Codex, deleted conversations will no longer appear in the sidebar." : "\n\n重新打开 Codex 后，已删除的会话不会再出现在侧边栏。";
		bool hasFailure = errors.Count > 0 || !string.IsNullOrWhiteSpace(projectError);
		string statusMessage = UiLanguage.IsEnglish
			? hasFailure ? "Some selected " + (deletingSubagents ? "subagent conversations" : "main conversations") + " or the project could not be processed." : "The selected " + (deletingSubagents ? "subagent conversations" : "main conversations") + " were processed."
			: hasFailure ? "部分所选" + typeLabel + "或项目处理失败。" : "所选" + typeLabel + "已处理。";
		string resultTitle = UiLanguage.IsEnglish
			? hasFailure ? "Selection partially processed" : "Selection processed"
			: hasFailure ? typeLabel + "部分处理完成" : "所选" + typeLabel + "处理完成";
		SetStatus(statusMessage, error: hasFailure);
		AppDialog.ShowCompat(window, resultText, resultTitle, MessageBoxButton.OK, hasFailure ? MessageBoxImage.Warning : MessageBoxImage.Asterisk);
	}

	private BatchProjectDeleteScope BuildBatchProjectDeleteScope(ProjectGroup sourceProject, IEnumerable<SessionInfo> selectedSessions)
	{
		BatchProjectDeleteScope result = new BatchProjectDeleteScope
		{
			ProjectPath = sourceProject?.ProjectPath ?? string.Empty
		};
		if (sourceProject == null || string.IsNullOrWhiteSpace(sourceProject.ProjectPath))
		{
			result.TotalMainConversationCount = sourceProject?.MainCount ?? 0;
			result.AvailabilityBlockReason = UiLanguage.IsEnglish ? "This project does not have a stable recorded folder, so project processing is unavailable." : "该项目没有稳定的已记录目录，当前不能处理项目目录。";
			return result;
		}
		try
		{
			result.ProjectPath = System.IO.Path.GetFullPath(TextHelpers.StripExtendedPrefix(sourceProject.ProjectPath));
		}
		catch
		{
			result.TotalMainConversationCount = sourceProject.MainCount;
			result.AvailabilityBlockReason = UiLanguage.IsEnglish ? "The recorded project folder is invalid, so project processing is unavailable." : "记录的项目目录无效，当前不能处理项目目录。";
			return result;
		}

		string canonicalProjectPath = TextHelpers.CanonicalPath(result.ProjectPath);
		List<SessionInfo> allMainSessions = projects
			.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>())
			.Where((SessionInfo session) => session != null && !session.IsSubagent && !string.IsNullOrWhiteSpace(session.ThreadId))
			.GroupBy((SessionInfo session) => session.ThreadId, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, SessionInfo> group) => group.First())
			.ToList();
		List<SessionInfo> samePathGroupSessions = projects
			.Where((ProjectGroup project) => string.Equals(TextHelpers.CanonicalPath(project.ProjectPath), canonicalProjectPath, StringComparison.OrdinalIgnoreCase))
			.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>())
			.Where((SessionInfo session) => session != null && !session.IsSubagent && !string.IsNullOrWhiteSpace(session.ThreadId))
			.ToList();
		List<SessionInfo> relatedMainSessions = samePathGroupSessions
			.Concat(allMainSessions.Where((SessionInfo session) => !string.IsNullOrWhiteSpace(session.Cwd) && TextHelpers.IsWithin(session.Cwd, result.ProjectPath)))
			.GroupBy((SessionInfo session) => session.ThreadId, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, SessionInfo> group) => group.First())
			.ToList();
		result.TotalMainConversationCount = relatedMainSessions.Count;

		int inconsistentCount = samePathGroupSessions
			.GroupBy((SessionInfo session) => session.ThreadId, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, SessionInfo> group) => group.First())
			.Count((SessionInfo session) => string.IsNullOrWhiteSpace(session.Cwd) || !TextHelpers.IsWithin(session.Cwd, result.ProjectPath));
		if (inconsistentCount > 0)
		{
			result.AvailabilityBlockReason = UiLanguage.IsEnglish
				? inconsistentCount + " main conversation(s) assigned to this project record a different folder. Project processing is disabled to prevent deleting the wrong directory."
				: "该项目中有 " + inconsistentCount + " 个主对话记录了不同目录；为避免误删，当前不提供项目目录处理。";
			return result;
		}

		HashSet<string> currentGroupIds = new HashSet<string>((sourceProject.Sessions ?? new List<SessionInfo>())
			.Where((SessionInfo session) => session != null && !session.IsSubagent && !string.IsNullOrWhiteSpace(session.ThreadId))
			.Select((SessionInfo session) => session.ThreadId), StringComparer.OrdinalIgnoreCase);
		HashSet<string> relatedIds = new HashSet<string>(relatedMainSessions.Select((SessionInfo session) => session.ThreadId), StringComparer.OrdinalIgnoreCase);
		int outsideGroupCount = relatedIds.Count((string id) => !currentGroupIds.Contains(id));
		if (outsideGroupCount > 0)
		{
			result.AvailabilityBlockReason = UiLanguage.IsEnglish
				? "The same folder is also linked to " + outsideGroupCount + " main conversation(s) in other project groups. Project processing is disabled so those conversations are not left pointing to a deleted folder."
				: "同一目录还关联其他项目分组中的 " + outsideGroupCount + " 个主对话；为避免留下指向已删除目录的对话，当前不提供项目目录处理。";
			return result;
		}

		HashSet<string> selectedIdsForScope = new HashSet<string>((selectedSessions ?? Enumerable.Empty<SessionInfo>())
			.Where((SessionInfo session) => session != null && !session.IsSubagent && !string.IsNullOrWhiteSpace(session.ThreadId))
			.Select((SessionInfo session) => session.ThreadId), StringComparer.OrdinalIgnoreCase);
		result.AllMainConversationsSelected = relatedIds.Count > 0 && selectedIdsForScope.SetEquals(relatedIds);
		return result;
	}

	private int CountRelatedConversations(string projectPath)
	{
		if (string.IsNullOrWhiteSpace(projectPath))
		{
			return 0;
		}
		return projects.SelectMany((ProjectGroup project) => project.Sessions).Where((SessionInfo item) => !item.IsSubagent && TextHelpers.IsWithin(item.Cwd, projectPath)).Select((SessionInfo item) => item.ThreadId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
	}

	private List<SessionInfo> BuildDeletionClosure(SessionInfo root)
	{
		List<SessionInfo> allSessions = projects.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>()).Where((SessionInfo item) => item != null && !string.IsNullOrWhiteSpace(item.ThreadId)).GroupBy((SessionInfo item) => item.ThreadId, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, SessionInfo> group) => group.First()).ToList();
		Dictionary<string, List<SessionInfo>> children = allSessions.Where((SessionInfo item) => !string.IsNullOrWhiteSpace(item.ParentThreadId)).GroupBy((SessionInfo item) => item.ParentThreadId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, SessionInfo> group) => group.Key, (IGrouping<string, SessionInfo> group) => group.ToList(), StringComparer.OrdinalIgnoreCase);
		List<SessionInfo> closure = new List<SessionInfo> { root };
		HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ThreadId };
		Queue<string> pending = new Queue<string>();
		pending.Enqueue(root.ThreadId);
		while (pending.Count > 0)
		{
			string parentId = pending.Dequeue();
			if (!children.TryGetValue(parentId, out List<SessionInfo> directChildren))
			{
				continue;
			}
			foreach (SessionInfo child in directChildren)
			{
				if (visited.Add(child.ThreadId))
				{
					closure.Add(child);
					pending.Enqueue(child.ThreadId);
				}
			}
		}
		return closure;
	}

	private List<SessionDeletionPlan> BuildDeletionPlans(IEnumerable<SessionInfo> selectedSessions)
	{
		List<SessionInfo> selected = (selectedSessions ?? Enumerable.Empty<SessionInfo>()).Where((SessionInfo item) => item != null && !string.IsNullOrWhiteSpace(item.ThreadId)).GroupBy((SessionInfo item) => item.ThreadId, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, SessionInfo> group) => group.First()).ToList();
		HashSet<string> selectedIds = new HashSet<string>(selected.Select((SessionInfo item) => item.ThreadId), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, SessionInfo> allById = projects.SelectMany((ProjectGroup project) => project.Sessions ?? new List<SessionInfo>()).Where((SessionInfo item) => item != null && !string.IsNullOrWhiteSpace(item.ThreadId)).GroupBy((SessionInfo item) => item.ThreadId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, SessionInfo> group) => group.Key, (IGrouping<string, SessionInfo> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		List<SessionDeletionPlan> plans = new List<SessionDeletionPlan>();
		foreach (SessionInfo candidate in selected)
		{
			HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string parentId = candidate.ParentThreadId;
			bool hasSelectedAncestor = false;
			while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
			{
				if (selectedIds.Contains(parentId))
				{
					hasSelectedAncestor = true;
					break;
				}
				if (!allById.TryGetValue(parentId, out SessionInfo parent))
				{
					break;
				}
				parentId = parent.ParentThreadId;
			}
			if (!hasSelectedAncestor)
			{
				plans.Add(new SessionDeletionPlan { Root = candidate, AffectedSessions = BuildDeletionClosure(candidate) });
			}
		}
		return plans;
	}

	private async Task ShowTrashManagerAsync()
	{
		if (isBusy)
		{
			return;
		}
		while (true)
		{
			List<TrashSessionInfo> items;
			try
			{
				items = await Task.Run(ProviderStorage.ReadTrash);
			}
			catch (Exception ex)
			{
				AppDialog.ShowCompat(window, ex.Message, "读取软件回收站失败", MessageBoxButton.OK, MessageBoxImage.Hand);
				return;
			}
			TrashActionRequest request = TrashManagerDialog.Show(window, items);
			if (request == null || request.Action == TrashAction.None || request.Item == null)
			{
				return;
			}
			if (request.Action == TrashAction.Restore)
			{
				if (!EnsureCodexClosedForConversationWrite())
				{
					continue;
				}
				MessageBoxResult answer = AppDialog.ShowCompat(window, "把这个会话恢复到原位置吗？\n\n" + request.Item.DisplayTitle + "\n\n" + request.Item.OriginalPath, "恢复会话", MessageBoxButton.YesNo, MessageBoxImage.Question);
				if (answer != MessageBoxResult.Yes)
				{
					continue;
				}
				SetBusy(busy: true, "正在恢复会话……");
				try
				{
					await Task.Run(() => ProviderStorage.Restore(request.Item));
					SetStatus("会话及侧边栏索引均已恢复。", error: false);
					SetBusy(false, null);
					await RefreshDataAsync();
					AppDialog.ShowCompat(window, "会话已恢复到原位置，侧边栏索引也已重新登记。\n\n重新打开 Codex 后即可查看。", "恢复完成", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				}
				catch (Exception ex)
				{
					SetStatus("恢复失败：" + ex.Message, error: true);
					AppDialog.ShowCompat(window, ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Hand);
				}
				finally
				{
					SetBusy(busy: false, null);
				}
				continue;
			}
			if (request.Action == TrashAction.DeletePermanently)
			{
				if (!EnsureCodexClosedForConversationWrite())
				{
					continue;
				}
				MessageBoxResult answer = AppDialog.ShowCompat(window, "永久删除这个回收站会话备份吗？\n\n" + request.Item.DisplayTitle + "\n\n删除后无法从本工具恢复。", "永久删除回收站备份", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
				if (answer != MessageBoxResult.Yes)
				{
					continue;
				}
				SetBusy(busy: true, "正在永久删除回收站备份……");
				try
				{
					await Task.Run(() => ProviderStorage.DeleteFromTrash(request.Item));
					SetStatus("回收站会话备份已永久删除。", error: false);
				}
				catch (Exception ex)
				{
					SetStatus("永久删除失败：" + ex.Message, error: true);
					AppDialog.ShowCompat(window, ex.Message, "永久删除失败", MessageBoxButton.OK, MessageBoxImage.Hand);
				}
				finally
				{
					SetBusy(busy: false, null);
				}
				continue;
			}
			if (request.Action == TrashAction.DeleteProject)
			{
				string validatedProjectPath;
				try
				{
					validatedProjectPath = ProviderStorage.ValidateProjectPath(request.Item.ProjectPath);
				}
				catch (Exception ex)
				{
					AppDialog.ShowCompat(window, ex.Message, "不能处理项目目录", MessageBoxButton.OK, MessageBoxImage.Warning);
					continue;
				}
				ProjectDeleteMode mode = ProjectDeleteOptionsDialog.Show(window, validatedProjectPath, request.Item.DisplayTitle);
				if (mode == ProjectDeleteMode.None)
				{
					continue;
				}
				string modeText = mode == ProjectDeleteMode.RecycleBin ? "移入 Windows 回收站" : "永久递归删除";
				MessageBoxResult answer = AppDialog.ShowCompat(window, "确定要" + modeText + "这个项目目录吗？\n\n" + validatedProjectPath, "确认处理项目目录", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
				if (answer != MessageBoxResult.Yes)
				{
					continue;
				}
				SetBusy(busy: true, "正在处理项目目录……");
				try
				{
					await Task.Run(delegate
					{
						ProviderStorage.DeleteProject(validatedProjectPath, mode);
						try
						{
							ProviderStorage.MarkProjectHandled(request.Item, mode);
						}
						catch
						{
						}
					});
					SetStatus(mode == ProjectDeleteMode.RecycleBin ? "项目已移入 Windows 回收站。" : "项目已永久删除。", error: false);
					await RefreshDataAsync();
				}
				catch (Exception ex)
				{
					SetStatus("项目处理失败：" + ex.Message, error: true);
					AppDialog.ShowCompat(window, ex.Message, "项目处理失败", MessageBoxButton.OK, MessageBoxImage.Hand);
				}
				finally
				{
					SetBusy(busy: false, null);
				}
			}
		}
	}

	private bool EnsureCodexClosedForConversationWrite()
	{
		if (BackendContext.IsDsh) return EnsureDshClosed();
		try
		{
			CodexDesktopProjectRegistry.EnsureImportCanWrite(CodexCatalog.ResolveCodexHome());
			return true;
		}
		catch (Exception ex)
		{
			SetStatus("请先完全退出 Codex，再执行会话删除或恢复。", error: true);
			AppDialog.ShowCompat(window, ex.Message, "请先退出 Codex", MessageBoxButton.OK, MessageBoxImage.Warning);
			return false;
		}
	}

	private async Task BackupSelectedAsync()
	{
		List<BackupProjectSelection> selections = projects.Select((ProjectGroup project) => new BackupProjectSelection
		{
			Project = project,
			Sessions = (from session in project.Sessions
				where !session.IsSubagent && session.IsSelected
				orderby session.UpdatedDate descending
				select session).ToList()
		}).Where((BackupProjectSelection selection) => selection.Sessions.Count > 0).ToList();
		int selectedCount = selections.Sum((BackupProjectSelection selection) => selection.Sessions.Count);
		if (selectedCount == 0)
		{
			AppDialog.ShowCompat(window, "请在右侧勾选一个或多个主对话。可以切换项目继续勾选，选择会保留。", "尚未选择对话", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string prefix = selections.Count == 1 ? TextHelpers.SafeFileName(selections[0].Project.DisplayName) : selections.Count + "个项目";
		string name = prefix + "-仅对话-已选" + selectedCount + "个-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + BackupPackageFormat.ConversationExtension;
		if (TryGetBackupOutput(name, out string output))
		{
			if (selections.Count == 1)
			{
				await CreatePackAsync(selections[0].Project, selections[0].Sessions, wholeProject: false, includeProjectFiles: false, output);
			}
			else
			{
				await CreateBatchPackAsync(selections, includeProjectFiles: false, output);
			}
		}
	}

	private static string DefaultBackupFolder()
	{
		string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		if (string.IsNullOrWhiteSpace(documents))
		{
			documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		return System.IO.Path.Combine(documents, UiLanguage.T(UiLanguage.IsEnglish ? "Codex Migration Packages" : "Codex 迁移包"));
	}

	private void BrowseBackupFolder()
	{
		using FolderBrowserDialog dialog = new FolderBrowserDialog
		{
			Description = UiLanguage.T("选择迁移包保存文件夹"),
			ShowNewFolderButton = true
		};
		if (Directory.Exists(backupFolderBox.Text))
		{
			dialog.SelectedPath = backupFolderBox.Text;
		}
		if (dialog.ShowDialog() == DialogResult.OK)
		{
			backupFolderBox.Text = dialog.SelectedPath;
		}
	}

	private bool TryGetBackupOutput(string defaultName, out string output)
	{
		output = string.Empty;
		try
		{
			string folder = backupFolderBox.Text.Trim();
			if (string.IsNullOrWhiteSpace(folder))
			{
				throw new InvalidOperationException("请先选择迁移包保存文件夹。");
			}
			folder = System.IO.Path.GetFullPath(folder);
			if (File.Exists(folder))
			{
				throw new IOException("备份位置是一个文件，不是文件夹：\n" + folder);
			}
			Directory.CreateDirectory(folder);
			backupFolderBox.Text = folder;
			string extension = System.IO.Path.GetExtension(defaultName);
			if (!string.Equals(extension, BackupPackageFormat.ConversationExtension, StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, BackupPackageFormat.ProjectExtension, StringComparison.OrdinalIgnoreCase))
			{
				extension = BackupPackageFormat.ConversationExtension;
			}
			string baseName = TextHelpers.SafeFileName(System.IO.Path.GetFileNameWithoutExtension(defaultName));
			string safeName = baseName + extension;
			string candidate = System.IO.Path.Combine(folder, safeName);
			int suffix = 2;
			while (File.Exists(candidate))
			{
				candidate = System.IO.Path.Combine(folder, baseName + "-" + suffix + extension);
				suffix++;
			}
			output = candidate;
			return true;
		}
		catch (Exception ex)
		{
			AppDialog.ShowCompat(window, ex.Message, "备份位置不可用", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return false;
		}
	}

	private async Task BackupProjectWithFilesAsync()
	{
		List<ProjectGroup> selectedProjects = projects.Where((ProjectGroup project) => project.IsBatchSelected).ToList();
		if (selectedProjects.Count == 0)
		{
			AppDialog.ShowCompat(window, "请先勾选左侧的一个或多个项目。", "尚未选择项目", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		List<ProjectGroup> unavailable = selectedProjects.Where((ProjectGroup project) => !project.CanBackupFiles).ToList();
		if (unavailable.Count > 0)
		{
			AppDialog.ShowCompat(window, "以下项目目录不存在，无法加入完整迁移包：\n\n" + string.Join("\n", unavailable.Select((ProjectGroup project) => project.DisplayName + "：" + project.ProjectPath)), "找不到项目目录", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		int conversationCount = selectedProjects.Sum((ProjectGroup project) => project.Sessions.Count);
		string projectListText = string.Join("\n", selectedProjects.Take(8).Select((ProjectGroup project) => "• " + project.DisplayName));
		if (selectedProjects.Count > 8)
		{
			projectListText += "\n• 另外 " + (selectedProjects.Count - 8) + " 个项目";
		}
		MessageBoxResult answer = AppDialog.ShowCompat(window, "将创建一个批量迁移包：\n\n" + projectListText + "\n\n共 " + selectedProjects.Count + " 个项目、" + conversationCount + " 条主对话/子代理对话。项目普通文件、空目录和全部对应对话都会进入同一个包；目录联接和符号链接不会跟随。\n\n迁移包可能包含源码、密钥、构建产物和大文件，请妥善保管。是否继续？", "确认迁移所选项目", MessageBoxButton.YesNo, MessageBoxImage.Warning);
		if (answer != MessageBoxResult.Yes)
		{
			return;
		}
		string prefix = selectedProjects.Count == 1 ? TextHelpers.SafeFileName(selectedProjects[0].DisplayName) : selectedProjects.Count + "个项目";
		string name = prefix + "-项目与对话-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + BackupPackageFormat.ProjectExtension;
		if (TryGetBackupOutput(name, out string output))
		{
			if (selectedProjects.Count == 1)
			{
				await CreatePackAsync(selectedProjects[0], selectedProjects[0].Sessions, wholeProject: true, includeProjectFiles: true, output);
			}
			else
			{
				await CreateBatchPackAsync(selectedProjects.Select((ProjectGroup project) => new BackupProjectSelection
				{
					Project = project,
					Sessions = project.Sessions
				}).ToList(), includeProjectFiles: true, output);
			}
		}
	}

	private async Task CreateBatchPackAsync(IList<BackupProjectSelection> selections, bool includeProjectFiles, string output)
	{
		if (BackendContext.IsDsh) { await CreateDshPackageAsync(selections, includeProjectFiles, output); return; }
		if (selections == null || selections.Count < 2)
		{
			throw new InvalidOperationException("批量迁移至少需要两个项目。");
		}
		string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-batch-pack-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temp);
		SetBusy(busy: true, includeProjectFiles ? "正在创建多项目完整迁移包……" : "正在创建多项目对话包……");
		try
		{
			PackManifest manifest = new PackManifest
			{
				schema = 5,
				created_at = DateTimeOffset.Now.ToString("o"),
				mode = includeProjectFiles ? "batch_projects_with_files" : "batch_selected_conversations",
				source_project = string.Empty,
				source_project_name = selections.Count + " 个项目",
				includes_subagents = includeProjectFiles,
				engine = "native",
				engine_version = typeof(MainWindowController).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
				bundles = new List<string>(),
				sessions = new List<PackSession>(),
				projects = new List<PackProject>()
			};
			HashSet<string> targetFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int projectIndex = 0;
			foreach (BackupProjectSelection selection in selections)
			{
				projectIndex++;
				int currentProjectIndex = projectIndex;
				ProjectGroup project = selection.Project;
				string projectKey = "project-" + currentProjectIndex.ToString("000");
				string projectRelativeDirectory = "projects/" + projectKey;
				string projectTempDirectory = System.IO.Path.Combine(temp, "projects", projectKey);
				Directory.CreateDirectory(projectTempDirectory);
				PackProject packProject = new PackProject
				{
					project_key = projectKey,
					source_project = project.ProjectPath,
					source_project_name = project.DisplayName,
					target_folder = UniqueProjectFolder(project.DisplayName, currentProjectIndex, targetFolders),
					bundles = new List<string>()
				};
				if (includeProjectFiles)
				{
					SetStatus($"正在封装第 {currentProjectIndex}/{selections.Count} 个项目的对话：{project.DisplayName}", error: false);
					string bundleRelative = projectRelativeDirectory + "/project.codexbundle";
					string bundlePath = System.IO.Path.Combine(projectTempDirectory, "project.codexbundle");
						await Task.Run(delegate
						{
							ExactBundleWriter.CreateBundle(selection.Sessions, bundlePath, delegate(int index, int count, SessionInfo item)
							{
								if (index == 1 || index == count || index % 20 == 0)
								{
									window.Dispatcher.BeginInvoke((Action)delegate
									{
										SetStatus($"项目 {currentProjectIndex}/{selections.Count} · 对话 {index}/{count}：{item.DisplayTitle}", error: false);
									});
								}
							});
						});
					packProject.bundles.Add(bundleRelative);
					manifest.bundles.Add(bundleRelative);
					foreach (SessionInfo session in selection.Sessions)
					{
						PackSession packed = ToPackSession(session, bundleRelative);
						packed.project_key = projectKey;
						manifest.sessions.Add(packed);
					}
					SetStatus($"正在压缩第 {currentProjectIndex}/{selections.Count} 个项目文件：{project.DisplayName}", error: false);
					string archivePath = System.IO.Path.Combine(projectTempDirectory, "project-files.zip");
					ProjectPayloadInfo payload = await Task.Run(delegate
					{
						return ProjectPayloadService.CreateArchive(project.ProjectPath, archivePath, output, delegate(int index, int count, string relativePath)
						{
							if (index == 1 || index == count || index % 50 == 0)
							{
								window.Dispatcher.BeginInvoke((Action)delegate
								{
									SetStatus($"项目 {currentProjectIndex}/{selections.Count} · 文件 {index}/{count}：{relativePath}", error: false);
								});
							}
						});
					});
					payload.archive_file = projectRelativeDirectory + "/project-files.zip";
					packProject.project_payload = payload;
				}
				else
				{
					int sessionIndex = 0;
					foreach (SessionInfo session in selection.Sessions)
					{
						sessionIndex++;
						SetStatus($"项目 {currentProjectIndex}/{selections.Count} · 正在备份对话 {sessionIndex}/{selection.Sessions.Count}：{session.DisplayTitle}", error: false);
						string bundleFileName = sessionIndex.ToString("000") + "-" + session.ThreadId + ".codexbundle";
						string conversationsDirectory = System.IO.Path.Combine(projectTempDirectory, "conversations");
						Directory.CreateDirectory(conversationsDirectory);
						string bundleRelative = projectRelativeDirectory + "/conversations/" + bundleFileName;
						await Task.Run(() => ExactBundleWriter.CreateSingleSessionBundle(session, System.IO.Path.Combine(conversationsDirectory, bundleFileName)));
						packProject.bundles.Add(bundleRelative);
						manifest.bundles.Add(bundleRelative);
						PackSession packed = ToPackSession(session, bundleRelative);
						packed.project_key = projectKey;
						manifest.sessions.Add(packed);
					}
				}
				manifest.projects.Add(packProject);
			}
			File.WriteAllText(System.IO.Path.Combine(temp, "manifest.json"), JsonSerialization.NewSerializer().Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (File.Exists(output))
			{
				throw new IOException("备份文件在创建期间已存在，请重试：\n" + output);
			}
			OuterPackageArchive.CreateFromDirectoryAtomic(temp, output);
			long fileCount = manifest.projects.Where((PackProject project) => project.project_payload != null).Sum((PackProject project) => (long)project.project_payload.file_count);
			long bytes = manifest.projects.Where((PackProject project) => project.project_payload != null).Sum((PackProject project) => project.project_payload.uncompressed_bytes);
			SetStatus("批量迁移包创建完成：" + output, error: false);
			string payloadSummary = includeProjectFiles ? $"\n项目文件：{fileCount} 个（{ProjectPayloadService.FormatBytes(bytes)}）" : string.Empty;
			AppDialog.ShowCompat(window, $"备份完成！\n\n项目：{selections.Count} 个\n对话记录：{manifest.sessions.Count}{payloadSummary}\n\n已保存到：\n{output}", "批量备份成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			SetStatus("批量备份失败：" + ex.Message, error: true);
			AppDialog.ShowCompat(window, ex.Message, "批量备份失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			TryDeleteDirectory(temp);
			SetBusy(busy: false, null);
		}
	}

	private static string UniqueProjectFolder(string displayName, int index, ISet<string> used)
	{
		string baseName = TextHelpers.SafeFileName(displayName).Trim().TrimEnd('.', ' ');
		if (string.IsNullOrWhiteSpace(baseName))
		{
			baseName = "项目-" + index.ToString("000");
		}
		if (IsReservedProjectFolder(baseName))
		{
			baseName = "_" + baseName;
		}
		string candidate = baseName;
		int suffix = 2;
		while (!used.Add(candidate))
		{
			candidate = baseName + "-" + suffix;
			suffix++;
		}
		return candidate;
	}

	private async Task CreatePackAsync(ProjectGroup project, IList<SessionInfo> sessions, bool wholeProject, bool includeProjectFiles, string output)
	{
		if (BackendContext.IsDsh) { await CreateDshPackageAsync(new[] { new BackupProjectSelection { Project = project, Sessions = sessions.ToList() } }, includeProjectFiles, output); return; }
		string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-pack-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temp);
		SetBusy(busy: true, "正在创建迁移包……");
		try
		{
			PackManifest manifest = new PackManifest
			{
				schema = (includeProjectFiles ? 3 : 2),
				created_at = DateTimeOffset.Now.ToString("o"),
				mode = (includeProjectFiles ? "project_with_files" : (wholeProject ? "project" : "selected")),
				source_project = project.ProjectPath,
				source_project_name = project.DisplayName,
				includes_subagents = wholeProject,
				engine = "native",
				engine_version = typeof(MainWindowController).Assembly.GetName().Version?.ToString(3) ?? string.Empty,
				bundles = new List<string>(),
				sessions = new List<PackSession>(),
				project_payload = null
			};
			if (wholeProject)
			{
				SetStatus("正在完整备份项目（包含子代理对话）……", error: false);
				string bundleName = "project.codexbundle";
				string bundlePath = System.IO.Path.Combine(temp, bundleName);
					await Task.Run(delegate
					{
						ExactBundleWriter.CreateBundle(project.Sessions, bundlePath, delegate(int index, int count, SessionInfo item)
						{
							window.Dispatcher.BeginInvoke((Action)delegate
							{
								SetStatus($"正在校验并封装第 {index}/{count} 条记录：{item.DisplayTitle}", error: false);
							});
						});
					});
				manifest.bundles.Add(bundleName);
				foreach (SessionInfo session2 in sessions)
				{
					manifest.sessions.Add(ToPackSession(session2, bundleName));
				}
			}
			else
			{
				int current2 = 0;
				foreach (SessionInfo session in sessions)
				{
					current2++;
					SetStatus($"正在备份第 {current2}/{sessions.Count} 个对话：{session.DisplayTitle}", error: false);
					string bundleName2 = current2.ToString("000") + "-" + session.ThreadId + ".codexbundle";
					string bundlePath2 = System.IO.Path.Combine(temp, bundleName2);
					await Task.Run(delegate
					{
						ExactBundleWriter.CreateSingleSessionBundle(session, bundlePath2);
					});
					manifest.bundles.Add(bundleName2);
					manifest.sessions.Add(ToPackSession(session, bundleName2));
				}
			}
			if (includeProjectFiles)
			{
				SetStatus("正在扫描并压缩项目目录……", error: false);
				string projectArchivePath = System.IO.Path.Combine(temp, "project-files.zip");
				manifest.project_payload = await Task.Run(delegate
				{
					return ProjectPayloadService.CreateArchive(project.ProjectPath, projectArchivePath, output, delegate(int index, int count, string relativePath)
					{
						if (index == 1 || index == count || index % 25 == 0)
						{
							window.Dispatcher.BeginInvoke((Action)delegate
							{
								SetStatus($"正在压缩项目文件 {index}/{count}：{relativePath}", error: false);
							});
						}
					});
				});
				SetStatus($"项目文件封装完成：{manifest.project_payload.file_count} 个文件，{ProjectPayloadService.FormatBytes(manifest.project_payload.uncompressed_bytes)}", error: false);
			}
			File.WriteAllText(System.IO.Path.Combine(temp, "manifest.json"), JsonSerialization.NewSerializer().Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (File.Exists(output))
			{
				throw new IOException("备份文件在创建期间已存在，请重试：\n" + output);
			}
			OuterPackageArchive.CreateFromDirectoryAtomic(temp, output);
			SetStatus("迁移包创建完成：" + output, error: false);
			string projectFilesSummary = (manifest.project_payload == null) ? string.Empty : $"\n项目文件：{manifest.project_payload.file_count} 个（{ProjectPayloadService.FormatBytes(manifest.project_payload.uncompressed_bytes)}）" + ((manifest.project_payload.skipped_reparse_points > 0) ? $"\n跳过重解析点：{manifest.project_payload.skipped_reparse_points} 个" : string.Empty);
			AppDialog.ShowCompat(window, $"备份完成！\n\n项目：{project.DisplayName}\n对话记录：{sessions.Count}" + projectFilesSummary + $"\n文件：{output}", "备份成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			SetStatus("备份失败：" + ex.Message, error: true);
			AppDialog.ShowCompat(window, ex.Message, "备份失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			TryDeleteDirectory(temp);
			SetBusy(busy: false, null);
		}
	}

	private static PackSession ToPackSession(SessionInfo session, string bundleName)
	{
		PackSession packSession = new PackSession();
		packSession.thread_id = session.ThreadId;
		packSession.origin_thread_id = string.IsNullOrWhiteSpace(session.OriginThreadId) ? session.ThreadId : session.OriginThreadId;
		packSession.title = session.DisplayTitle;
		packSession.preview = session.Preview;
		packSession.source = session.Source;
		packSession.updated_at = session.UpdatedAt;
		packSession.archived = session.Archived;
		packSession.compressed = session.Compressed;
		packSession.is_subagent = session.IsSubagent;
		packSession.bundle_file = bundleName;
		return packSession;
	}

	private async Task BrowsePackageAsync()
	{
		Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
		openFileDialog.Title = UiLanguage.T("选择 Codex 迁移包");
		openFileDialog.Filter = BackupPackageFormat.OpenDialogFilter;
		Microsoft.Win32.OpenFileDialog openFileDialog2 = openFileDialog;
		if (openFileDialog2.ShowDialog(window) == true)
		{
			packagePathBox.Text = openFileDialog2.FileName;
			SetBusy(busy: true, "正在读取迁移包清单……");
			try
			{
				await Task.Yield();
				await LoadPackageSummaryAsync(openFileDialog2.FileName);
			}
			finally
			{
				SetBusy(busy: false, null);
			}
		}
	}

	private void BrowseTarget()
	{
		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		folderBrowserDialog.Description = UiLanguage.T("选择这台电脑上的项目目录");
		folderBrowserDialog.ShowNewFolderButton = true;
		if (Directory.Exists(targetPathBox.Text))
		{
			folderBrowserDialog.SelectedPath = targetPathBox.Text;
		}
		if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
		{
			targetPathBox.Text = folderBrowserDialog.SelectedPath;
		}
	}

	private async Task LoadPackageSummaryAsync(string path)
	{
		if (BackendContext.IsDsh) { await LoadDshPackageSummaryAsync(path); return; }
		loadedManifest = null;
		loadedIsRawBundle = false;
		loadedPackagePath = string.Empty;
		projectRestorePanel.Visibility = Visibility.Collapsed;
		targetPathLabel.Text = UiLanguage.T("2 · 选择这台电脑上的项目目录");
		targetPathHelpText.Text = UiLanguage.T("目录可以与旧电脑不同，导入时会自动重写对话里的 cwd。");
		try
		{
			if (!BackupPackageFormat.IsSupportedImport(path)) throw new InvalidDataException("当前为 Codex 模式，请选择 Codex 备份包；DSH 包请切换到 DSH 后导入。");
			if (BackupPackageFormat.IsFormalPackage(path))
			{
				loadedManifest = await Task.Run(() => OuterPackageArchive.ReadManifest(path));
				if (loadedManifest?.engine == "deepseek-harness") throw new InvalidDataException("这是 DSH 备份包，请切换到 DSH 后导入。");
				if (loadedManifest == null)
				{
					throw new InvalidDataException("迁移包清单格式无效。");
				}
				int num = ((loadedManifest.sessions != null) ? loadedManifest.sessions.Count((PackSession x) => (loadedManifest.schema < 2) ? (!string.Equals(x.source, "subagent", StringComparison.OrdinalIgnoreCase)) : (!x.is_subagent)) : 0);
				List<PackProject> packProjects = ManifestProjects(loadedManifest);
				int payloadProjectCount = packProjects.Count((PackProject project) => project.project_payload != null);
				long payloadFileCount = packProjects.Where((PackProject project) => project.project_payload != null).Sum((PackProject project) => (long)project.project_payload.file_count);
				long payloadBytes = packProjects.Where((PackProject project) => project.project_payload != null).Sum((PackProject project) => project.project_payload.uncompressed_bytes);
				if (packProjects.Count > 1)
				{
					packageSummaryText.Text = string.Format(UiLanguage.T(payloadProjectCount > 0 ? "多项目完整迁移包 · {0} 个项目 · {1} 个主对话" : "多项目对话包 · {0} 个项目 · {1} 个主对话"), packProjects.Count, num);
					string names = string.Join("、", packProjects.Take(4).Select((PackProject project) => project.source_project_name));
					if (packProjects.Count > 4)
					{
						names += "等";
					}
					string payloadLine = payloadProjectCount > 0 ? "\n项目文件：" + payloadFileCount + " 个（" + ProjectPayloadService.FormatBytes(payloadBytes) + "）" : string.Empty;
					packageProjectText.Text = UiLanguage.T("包含项目：" + names + payloadLine + "\n创建时间：" + loadedManifest.created_at);
					restoreProjectFilesCheck.IsChecked = payloadProjectCount > 0;
					projectConflictCombo.SelectedIndex = 0;
					targetPathBox.Text = SuggestProjectTarget(loadedManifest);
					targetPathLabel.Text = UiLanguage.T("2 · 选择新电脑上的项目总目录");
					targetPathHelpText.Text = UiLanguage.T("每个项目会放入这个总目录下的独立子文件夹，并分别重写对应对话的 cwd。");
				}
				else if (LoadedHasProjectPayload())
				{
					ProjectPayloadInfo payload = packProjects[0].project_payload;
					packageSummaryText.Text = string.Format(UiLanguage.T("项目 + 对话完整包 · {0} 个主对话"), num);
					packageProjectText.Text = UiLanguage.T("原项目：" + loadedManifest.source_project + "\n项目文件：" + payload.file_count + " 个（" + ProjectPayloadService.FormatBytes(payload.uncompressed_bytes) + "）\n创建时间：" + loadedManifest.created_at);
					restoreProjectFilesCheck.IsChecked = true;
					projectConflictCombo.SelectedIndex = 0;
					targetPathBox.Text = SuggestProjectTarget(loadedManifest);
				}
				else
				{
					packageSummaryText.Text = string.Format(UiLanguage.T("{0} · {1} 个主对话"), UiLanguage.T((loadedManifest.mode == "project") ? "全部对话备份" : "已选对话备份"), num);
					string sourceProject = packProjects.FirstOrDefault()?.source_project ?? loadedManifest.source_project;
					packageProjectText.Text = UiLanguage.T("原项目：" + sourceProject + "\n创建时间：" + loadedManifest.created_at);
					restoreProjectFilesCheck.IsChecked = false;
					if (Directory.Exists(sourceProject))
					{
						targetPathBox.Text = sourceProject;
					}
				}
			}
			else
			{
				loadedIsRawBundle = true;
				packageSummaryText.Text = UiLanguage.T("原始 .codexbundle");
				packageProjectText.Text = UiLanguage.T("导入时会把包内项目映射到选定目录。");
				restoreProjectFilesCheck.IsChecked = false;
			}
			AppendLog("已选择：" + path);
			loadedPackagePath = System.IO.Path.GetFullPath(path);
		}
		catch (Exception ex)
		{
			loadedManifest = null;
			loadedIsRawBundle = false;
			loadedPackagePath = string.Empty;
			packageSummaryText.Text = UiLanguage.T("无法读取迁移包");
			packageProjectText.Text = UiLanguage.T(ex.Message);
			AppendLog("读取失败：" + ex.Message);
		}
		UpdateProjectRestoreControls();
	}

	private bool LoadedHasProjectPayload()
	{
		return ManifestProjects(loadedManifest).Any((PackProject project) => project.project_payload != null && !string.IsNullOrWhiteSpace(project.project_payload.archive_file));
	}

	private static List<PackProject> ManifestProjects(PackManifest manifest)
	{
		if (manifest == null)
		{
			return new List<PackProject>();
		}
		if ((manifest.schema >= 4 || manifest.engine == "deepseek-harness") && manifest.projects != null && manifest.projects.Count > 0)
		{
			return manifest.projects.Where((PackProject project) => project != null).ToList();
		}
		return new List<PackProject>
		{
			new PackProject
			{
				project_key = "project-001",
				source_project = manifest.source_project,
				source_project_name = manifest.source_project_name,
				target_folder = TextHelpers.SafeFileName(manifest.source_project_name),
				bundles = manifest.bundles ?? new List<string>(),
				project_payload = manifest.project_payload
			}
		};
	}

	private bool ShouldRestoreProjectFiles()
	{
		return LoadedHasProjectPayload() && restoreProjectFilesCheck.IsChecked == true;
	}

	private ProjectFileConflictMode CurrentProjectFileConflictMode()
	{
		System.Windows.Controls.ComboBoxItem item = projectConflictCombo.SelectedItem as System.Windows.Controls.ComboBoxItem;
		string tag = item?.Tag as string;
		if (string.Equals(tag, "skip", StringComparison.OrdinalIgnoreCase))
		{
			return ProjectFileConflictMode.SkipExisting;
		}
		if (string.Equals(tag, "overwrite", StringComparison.OrdinalIgnoreCase))
		{
			return ProjectFileConflictMode.OverwriteWithBackup;
		}
		return ProjectFileConflictMode.RequireEmpty;
	}

	private static string ProjectConflictModeText(ProjectFileConflictMode mode)
	{
		return mode switch
		{
			ProjectFileConflictMode.SkipExisting => "保留现有同名文件，只补充缺失文件",
			ProjectFileConflictMode.OverwriteWithBackup => "覆盖同名文件，并先创建恢复备份",
			_ => "要求目标目录为空"
		};
	}

	private void UpdateProjectRestoreControls()
	{
		bool hasPayload = LoadedHasProjectPayload();
		int projectCount = ManifestProjects(loadedManifest).Count;
		projectRestorePanel.Visibility = hasPayload ? Visibility.Visible : Visibility.Collapsed;
		restoreProjectFilesCheck.IsEnabled = hasPayload && !isBusy;
		bool restore = hasPayload && restoreProjectFilesCheck.IsChecked == true;
		projectConflictCombo.IsEnabled = restore && !isBusy;
		if (restore)
		{
			mapPathCheck.IsChecked = true;
			mapPathCheck.IsEnabled = false;
			ProjectFileConflictMode mode = CurrentProjectFileConflictMode();
			projectRestoreHelpText.Text = UiLanguage.T(mode switch
			{
				ProjectFileConflictMode.SkipExisting => (projectCount > 1 ? "每个项目只补充缺失文件；已有同名文件保持不变。" : "只补充目标目录缺失的项目文件；已有同名文件保持不变。") + " 会话仍导入 C 盘 Codex 目录。",
				ProjectFileConflictMode.OverwriteWithBackup => (projectCount > 1 ? "各项目的同名文件会被覆盖" : "同名项目文件会被覆盖") + "；覆盖前自动备份到 C 盘 Codex 配置目录。会话仍导入 C 盘。",
				_ => (projectCount > 1 ? "每个项目的目标子目录必须为空" : "目标目录必须为空") + "；项目文件还原后，会话仍导入 C 盘 Codex 目录。"
			});
		}
		else
		{
			mapPathCheck.IsEnabled = !isBusy;
			projectRestoreHelpText.Text = UiLanguage.T("已选择只导入包内对话，不还原项目文件。");
		}
	}

	private static string SuggestProjectTarget(PackManifest manifest)
	{
		string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		if (string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents))
		{
			documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		List<PackProject> packProjects = ManifestProjects(manifest);
		string name = packProjects.Count > 1 ? (UiLanguage.IsEnglish ? "Codex Imported Projects-" : "Codex 项目迁入-") + DateTime.Now.ToString("yyyyMMdd") : TextHelpers.SafeFileName(!string.IsNullOrWhiteSpace(packProjects.FirstOrDefault()?.source_project_name) ? packProjects[0].source_project_name : packProjects.FirstOrDefault()?.project_payload?.root_name);
		string candidate = System.IO.Path.Combine(documents, name);
		if (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any())
		{
			return candidate;
		}
		string baseCandidate = candidate + (UiLanguage.IsEnglish ? "-imported" : "-迁入");
		candidate = baseCandidate;
		int suffix = 2;
		while (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
		{
			candidate = baseCandidate + "-" + suffix;
			suffix++;
		}
		return candidate;
	}

	private string CurrentConflictMode()
	{
		if (copyModeRadio.IsChecked == true)
		{
			return "copy";
		}
		return "merge";
	}

	private void UpdateImportModeHelp()
	{
		if (copyModeRadio.IsChecked == true)
		{
			importModeHelpText.Text = UiLanguage.T("独立复制 · 每次都为主对话和子代理对话生成全新 Thread ID，并同步重写父子关系；原始编号只用于记录来源，两份文件互不共用。");
		}
		else
		{
			importModeHelpText.Text = UiLanguage.T("推荐 · 先按目标项目 + 原始编号查找已有对话：找到后合并；首次迁入则生成新 Thread ID，并把原始编号保存在会话中供下次识别。");
		}
	}


	private string ConflictModeConfirmation(string mode)
	{
		if (string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase))
		{
			return "独立复制：所有导入对话都生成全新 Thread ID，主对话与子代理的父子编号一起更新；原会话和复制后的会话使用不同文件，删除任意一份不会影响另一份。";
		}
		return "智能合并：只在目标项目内按原始编号查找；找到对应对话就合并，找不到则生成新 Thread ID。新编号会继续保存原始编号，便于以后再次备份和合并。";
	}


	internal static List<string> BuildProjectTargetPaths(IList<PackProject> packProjects, string selectedTarget)
	{
		if (packProjects == null || packProjects.Count == 0)
		{
			throw new InvalidDataException("迁移包没有项目清单。");
		}
		if (string.IsNullOrWhiteSpace(selectedTarget))
		{
			throw new InvalidOperationException(packProjects.Count > 1 ? "请选择新电脑上的项目总目录。" : "请选择新电脑上的项目目录。");
		}
		string root = System.IO.Path.GetFullPath(TextHelpers.StripExtendedPrefix(selectedTarget));
		HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<string> targets = new List<string>();
		for (int index = 0; index < packProjects.Count; index++)
		{
			PackProject project = packProjects[index];
			if (project == null || string.IsNullOrWhiteSpace(project.project_key) || !keys.Add(project.project_key))
			{
				throw new InvalidDataException("迁移包包含空白或重复的项目标识。");
			}
			if (packProjects.Count == 1)
			{
				targets.Add(root);
				continue;
			}
			string folder = ValidateProjectTargetFolder(project.target_folder, project.source_project_name, index + 1);
			if (!folders.Add(folder))
			{
				throw new InvalidDataException("迁移包包含重复的项目目标文件夹：" + folder);
			}
			string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, folder));
			string prefix = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
			if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("项目目标文件夹越界：" + folder);
			}
			targets.Add(candidate);
		}
		return targets;
	}

	private static string ValidateProjectTargetFolder(string value, string fallback, int index)
	{
		string folder = string.IsNullOrWhiteSpace(value) ? TextHelpers.SafeFileName(fallback) : value.Trim();
		folder = folder.TrimEnd('.', ' ');
		if (string.IsNullOrWhiteSpace(folder))
		{
			folder = "项目-" + index.ToString("000");
		}
		if (folder == "." || folder == ".." || System.IO.Path.IsPathRooted(folder) || folder.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || !string.Equals(System.IO.Path.GetFileName(folder), folder, StringComparison.Ordinal) || IsReservedProjectFolder(folder))
		{
			throw new InvalidDataException("迁移包包含无效的项目目标文件夹：" + folder);
		}
		return folder;
	}

	private static bool IsReservedProjectFolder(string folder)
	{
		string name = System.IO.Path.GetFileNameWithoutExtension(folder).ToUpperInvariant();
		if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL")
		{
			return true;
		}
		return name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] >= '1' && name[3] <= '9';
	}

	private static string ResolveExtractedPackageFile(string extractedRoot, string relativePath, string description)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			throw new InvalidDataException("迁移包包含空白的" + description + "路径。");
		}
		string root = System.IO.Path.GetFullPath(extractedRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
		string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(extractedRoot, relativePath));
		if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("迁移包包含越界的" + description + "路径：" + relativePath);
		}
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException("迁移包内缺少" + description + "：" + relativePath, fullPath);
		}
		return fullPath;
	}

	private static List<ImportProjectContext> BuildImportContexts(PackManifest manifest, string extractedRoot, string selectedTarget, bool mapProjectPath, bool restoreProjectFiles)
	{
		List<PackProject> packProjects = ManifestProjects(manifest);
		List<string> targetPaths = mapProjectPath ? BuildProjectTargetPaths(packProjects, selectedTarget) : Enumerable.Repeat(string.Empty, packProjects.Count).ToList();
		HashSet<string> assignedBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> bundleOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		List<ImportProjectContext> contexts = new List<ImportProjectContext>();
		for (int index = 0; index < packProjects.Count; index++)
		{
			PackProject project = packProjects[index];
			ImportProjectContext context = new ImportProjectContext
			{
				Project = project,
				TargetPath = targetPaths[index]
			};
			foreach (string bundleName in project.bundles ?? new List<string>())
			{
				if (!assignedBundles.Add(bundleName))
				{
					throw new InvalidDataException("迁移包把同一个对话包分配给了多个项目：" + bundleName);
				}
				bundleOwners[bundleName] = project.project_key;
				context.BundlePaths.Add(ResolveExtractedPackageFile(extractedRoot, bundleName, "对话包"));
			}
			if (restoreProjectFiles && project.project_payload != null)
			{
				context.ProjectArchivePath = ProjectPayloadService.ResolvePayloadArchivePath(extractedRoot, project.project_payload);
				if (!File.Exists(context.ProjectArchivePath))
				{
					throw new FileNotFoundException("迁移包内缺少项目文件载荷：" + project.project_payload.archive_file, context.ProjectArchivePath);
				}
			}
			contexts.Add(context);
		}
		HashSet<string> declaredBundles = new HashSet<string>(manifest.bundles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
		if ((manifest.bundles ?? new List<string>()).Count != declaredBundles.Count)
		{
			throw new InvalidDataException("迁移包的总对话包清单包含重复路径。");
		}
		if (manifest.schema >= 4 && !declaredBundles.SetEquals(assignedBundles))
		{
			throw new InvalidDataException("迁移包的总对话包清单与各项目清单不一致。");
		}
		if (assignedBundles.Count == 0)
		{
			throw new InvalidDataException("迁移包没有可导入的对话包。");
		}
		if (manifest.schema >= 4)
		{
			foreach (PackSession session in manifest.sessions ?? new List<PackSession>())
			{
				if (session == null || string.IsNullOrWhiteSpace(session.bundle_file) || !bundleOwners.TryGetValue(session.bundle_file, out string owner) || !string.Equals(owner, session.project_key, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("迁移包的对话与项目归属清单不一致：" + (session?.thread_id ?? "未知对话"));
				}
			}
		}
		return contexts;
	}

	private async Task ImportPackageAsync(bool dryRun)
	{
		if (BackendContext.IsDsh) { await ImportDshPackageAsync(dryRun); return; }
		if (isBusy)
		{
			return;
		}
		string package = packagePathBox.Text.Trim();
		if (!BackupPackageFormat.IsSupportedImport(package)) { AppDialog.ShowCompat(window, "当前为 Codex 模式，请选择 Codex 备份包；DSH 包请切换到 DSH 后导入。", "备份包与数据源不匹配", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
		if (!File.Exists(package))
		{
			AppDialog.Show(window, "找不到备份文件", "请选择有效文件", "文件路径不存在或已经移动。请选择 .codexchat、.codexproject；旧版 .codexpack 和 .codexbundle 仍可导入。", AppDialogTone.Warning, "重新选择");
			return;
		}
		if (string.IsNullOrWhiteSpace(loadedPackagePath) || !string.Equals(TextHelpers.CanonicalPath(loadedPackagePath), TextHelpers.CanonicalPath(package), StringComparison.OrdinalIgnoreCase))
		{
			await LoadPackageSummaryAsync(package);
			if (BackupPackageFormat.IsFormalPackage(package) && loadedManifest == null)
			{
				AppDialog.Show(window, "迁移包无效", "无法读取迁移清单", "该文件没有可用的 manifest.json，或清单格式不受支持。详细原因已写入右侧操作记录。", AppDialogTone.Error);
				return;
			}
		}
		string target = targetPathBox.Text.Trim();
		bool restoreProjectFiles = ShouldRestoreProjectFiles();
		bool mapProjectPath = restoreProjectFiles || mapPathCheck.IsChecked == true;
		List<PackProject> previewProjects = loadedManifest == null ? new List<PackProject>
		{
			new PackProject { project_key = "raw-project", source_project_name = "原始对话包", bundles = new List<string>() }
		} : ManifestProjects(loadedManifest);
		List<string> previewTargets = null;
		try
		{
			if (mapProjectPath)
			{
				previewTargets = BuildProjectTargetPaths(previewProjects, target);
				if (!restoreProjectFiles)
				{
					List<string> missingTargets = previewTargets.Where((string path) => !Directory.Exists(path)).ToList();
					if (missingTargets.Count > 0)
					{
						throw new DirectoryNotFoundException("只导入对话时，目标项目目录必须已经存在：\n\n" + string.Join("\n", missingTargets));
					}
				}
			}
		}
		catch (Exception ex)
		{
			AppDialog.Show(window, "目标目录无效", "无法使用这个项目位置", ex.Message, AppDialogTone.Warning, "修改目录");
			return;
		}
		string codexHome = CodexCatalog.ResolveCodexHome();
		if (!dryRun)
		{
			try
			{
				CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
			}
			catch (Exception ex)
			{
				AppDialog.Show(window, "请先退出 Codex", "Codex 桌面端仍在运行", ex.Message, AppDialogTone.Warning, "我先退出 Codex");
				return;
			}
		}
		string conflictMode = CurrentConflictMode();
		ProjectFileConflictMode projectConflictMode = CurrentProjectFileConflictMode();
		if (!dryRun)
		{
			string targetLabel = previewProjects.Count > 1 ? "项目总目录" : "项目目录";
			string projectAction = restoreProjectFiles ? ("\n\n" + previewProjects.Count + " 个项目的文件将还原到" + targetLabel + "：\n" + target + "\n项目文件冲突策略：" + ProjectConflictModeText(projectConflictMode)) : "\n\n本次不还原项目文件。";
			bool answer = AppDialog.Confirm(window, "确认导入", "确认项目与对话的去向", "对话将导入本机 C 盘 Codex 目录；项目文件进入你选择的位置。\n\n对话冲突策略：\n" + ConflictModeConfirmation(conflictMode) + projectAction, restoreProjectFiles && projectConflictMode == ProjectFileConflictMode.OverwriteWithBackup ? AppDialogTone.Warning : AppDialogTone.Info, "开始导入");
			if (!answer)
			{
				return;
			}
		}
		BeginImportProgress(dryRun);
		SetBusy(busy: true, dryRun ? "正在检查迁移包……" : "正在恢复项目与对话……");
		await Task.Yield();
		string rewriteTemp = null;
		UpdateImportStage("1 / 4 · 读取迁移包", "正在解压清单并验证对话包结构。界面仍可响应，请勿重复点击。");
		string temp = null;
		NativeImportTransaction importTransaction = null;
		List<ImportProjectContext> contexts = new List<ImportProjectContext>();
		try
		{
			List<string> bundlePaths = new List<string>();
			if (loadedIsRawBundle || !BackupPackageFormat.IsFormalPackage(package))
			{
				ImportProjectContext rawContext = new ImportProjectContext
				{
					Project = previewProjects[0],
					TargetPath = mapProjectPath ? previewTargets[0] : string.Empty
				};
				rawContext.BundlePaths.Add(package);
				contexts.Add(rawContext);
				bundlePaths.Add(package);
			}
			else
			{
				temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-import-" + Guid.NewGuid().ToString("N"));
				Directory.CreateDirectory(temp);
				await Task.Run(() => OuterPackageArchive.ExtractSafely(package, temp));
				contexts = BuildImportContexts(loadedManifest, temp, target, mapProjectPath, restoreProjectFiles);
				bundlePaths.AddRange(contexts.SelectMany((ImportProjectContext context) => context.BundlePaths));
			}
			int sourcePreflightBundleCount = 0;
			int sourcePreflightSessionCount = 0;
			foreach (ImportProjectContext context in contexts)
			{
				string sourceCwd = context.Project.source_project;
				if (string.IsNullOrWhiteSpace(sourceCwd) && context.Project.project_payload != null)
				{
					sourceCwd = context.Project.project_payload.source_path;
				}
				string targetCwd = mapProjectPath ? context.TargetPath : null;
				foreach (string sourceBundle in context.BundlePaths)
				{
					sourcePreflightBundleCount++;
					SetStatus($"正在严格预检第 {sourcePreflightBundleCount}/{bundlePaths.Count} 个原始对话包：{context.Project.source_project_name}", error: false);
					sourcePreflightSessionCount += await Task.Run(() =>
						NativeBundleImporter.PreflightBundle(
							sourceBundle,
							mapProjectPath ? sourceCwd : null,
							targetCwd));
				}
			}
			UpdateImportStage("2 / 4 · 检查项目文件", restoreProjectFiles ? "正在核对项目载荷、目标目录和同名文件冲突。" : "本次只处理对话，正在核对目标项目目录。");
			importLog.Clear();
			AppendLog(dryRun ? "开始安全检查（不会写入项目或会话）" : "开始正式导入");
			AppendLog($"严格原生预检完成：{sourcePreflightBundleCount} 个原始对话包、{sourcePreflightSessionCount} 个对话的路径、限额、清单、哈希、身份与 cwd 均已通过；随后才会规划合并或重编号。");
			if (restoreProjectFiles)
			{
				int inspectIndex = 0;
				foreach (ImportProjectContext context in contexts.Where((ImportProjectContext item) => item.Project.project_payload != null))
				{
					inspectIndex++;
					SetStatus($"正在校验项目载荷 {inspectIndex}/{contexts.Count}：{context.Project.source_project_name}", error: false);
					context.Plan = await Task.Run(() => ProjectPayloadService.InspectArchive(context.ProjectArchivePath, context.Project.project_payload, context.TargetPath, projectConflictMode));
					context.TargetPath = context.Plan.TargetPath;
					AppendLog($"项目校验通过：{context.Project.source_project_name} · {context.Plan.FileCount} 个文件 · 新增 {context.Plan.NewFileCount} · 同名 {context.Plan.ExistingFileCount}\n目标：{context.TargetPath}");
				}
			}
			rewriteTemp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codex-lineage-import-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(rewriteTemp);
			ConversationImportPlanner importPlanner = await Task.Run(() => new ConversationImportPlanner(codexHome));
			bool independentCopy = string.Equals(conflictMode, "copy", StringComparison.OrdinalIgnoreCase);
			int planIndex = 0;
			foreach (ImportProjectContext context in contexts)
			{
				foreach (string sourceBundle in context.BundlePaths)
				{
					planIndex++;
					string rewrittenBundle = System.IO.Path.Combine(rewriteTemp, planIndex.ToString("000") + ".codexbundle");
					ConversationImportPlan plan = await Task.Run(() => importPlanner.CreatePlan(sourceBundle, rewrittenBundle, context.TargetPath, independentCopy));
					context.ImportPlans.Add(plan);
				}
			}
			List<ConversationImportPlan> importPlans = contexts.SelectMany(context => context.ImportPlans).ToList();
			List<string> effectiveBundlePaths = importPlans.Select(plan => plan.EffectiveBundlePath).ToList();
			Dictionary<string, string> targetByEffectiveBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (ConversationImportPlan plan in importPlans)
			{
				targetByEffectiveBundle[System.IO.Path.GetFullPath(plan.EffectiveBundlePath)] = plan.TargetPath;
			}
			int lineageMatchedCount = importPlans.Sum(plan => plan.MatchedCount);
			int lineageCreatedCount = importPlans.Sum(plan => plan.CreatedCount);
			if (independentCopy)
			{
				AppendLog("独立复制计划完成：将为 " + lineageCreatedCount + " 个对话生成全新 Thread ID；原始编号保留用于识别来源。");
			}
			else
			{
				AppendLog("智能合并计划完成：按目标项目 + 原始编号匹配 " + lineageMatchedCount + " 个；首次迁入并生成新 Thread ID " + lineageCreatedCount + " 个。");
			}
			HashSet<string> filesBeforeImport = await Task.Run(() => dryRun ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : TargetedThreadIndexer.SnapshotSessionFiles(codexHome));
			if (!dryRun)
			{
				int preflightCount = 0;
				foreach (ImportProjectContext context in contexts)
				{
					string preflightSource = context.Project.source_project;
					if (string.IsNullOrWhiteSpace(preflightSource) && context.Project.project_payload != null)
					{
						preflightSource = context.Project.project_payload.source_path;
					}
					string preflightTarget = mapProjectPath ? context.TargetPath : null;
					foreach (ConversationImportPlan plan in context.ImportPlans)
					{
						preflightCount++;
						SetStatus($"正在预检第 {preflightCount}/{effectiveBundlePaths.Count} 个对话包：{context.Project.source_project_name}", error: false);
						await Task.Run(() => NativeBundleImporter.Import(
							plan.EffectiveBundlePath,
							codexHome,
							mapProjectPath ? preflightSource : null,
							preflightTarget,
							NativeBundleImportMode.Merge,
							dryRun: true));
					}
				}
				AppendLog($"原生导入预检完成：{preflightCount} 个对话包的路径、清单、哈希、cwd 与合并关系均已通过。");
				importTransaction = await Task.Run(() => NativeImportTransaction.Begin(codexHome));
			}
			if (!dryRun && restoreProjectFiles)
			{
				List<ImportProjectContext> payloadContexts = contexts.Where((ImportProjectContext item) => item.Project.project_payload != null).ToList();
				for (int index = 0; index < payloadContexts.Count; index++)
				{
					ImportProjectContext context = payloadContexts[index];
					SetStatus($"正在还原项目 {index + 1}/{payloadContexts.Count}：{context.Project.source_project_name}", error: false);
					context.RestoreResult = await Task.Run(() => ProjectPayloadService.RestoreArchive(context.ProjectArchivePath, context.Project.project_payload, context.TargetPath, projectConflictMode));
					AppendLog($"项目还原完成：{context.Project.source_project_name} · 新增 {context.RestoreResult.CreatedFileCount} · 覆盖 {context.RestoreResult.OverwrittenFileCount} · 跳过 {context.RestoreResult.SkippedFileCount}\n目标：{context.RestoreResult.TargetPath}");
					if (!string.IsNullOrWhiteSpace(context.RestoreResult.BackupPath))
					{
						AppendLog("被覆盖项目文件的备份：" + context.RestoreResult.BackupPath);
					}
				}
			}
			UpdateImportStage("3 / 4 · " + (dryRun ? "模拟导入对话" : "导入对话"), $"共 {bundlePaths.Count} 个对话包；逐包处理时界面仍可响应。");
			int current = 0;
			foreach (ImportProjectContext context in contexts)
			{
				foreach (ConversationImportPlan plan in context.ImportPlans)
				{
					string bundle = plan.EffectiveBundlePath;
					current++;
					SetStatus(string.Format("{0}第 {1}/{2} 个对话包 · {3}", dryRun ? "检查" : "导入", current, bundlePaths.Count, context.Project.source_project_name), error: false);
					string sourceProjectPath = null;
					string targetProjectPath = null;
					if (mapProjectPath)
					{
						sourceProjectPath = context.Project.source_project;
						if (string.IsNullOrWhiteSpace(sourceProjectPath) && context.Project.project_payload != null)
						{
							sourceProjectPath = context.Project.project_payload.source_path;
						}
						targetProjectPath = context.TargetPath;
						if (!string.IsNullOrWhiteSpace(sourceProjectPath) && string.Equals(TextHelpers.CanonicalPath(sourceProjectPath), TextHelpers.CanonicalPath(targetProjectPath), StringComparison.OrdinalIgnoreCase))
						{
							AppendLog("源项目与目标项目相同；原生引擎将保留现有 cwd。");
						}
					}
					List<string> plannedIds = plan.IdMap.Keys.Concat(plan.IdMap.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					NativeBundleImportResult nativeResult = await Task.Run(() => NativeBundleImporter.Import(
						bundle,
						codexHome,
						sourceProjectPath,
						targetProjectPath,
						NativeBundleImportMode.Merge,
						dryRun));
					AppendLog($"原生引擎：新增 {nativeResult.CreatedCount}，快进合并 {nativeResult.MergedCount}，替换 {nativeResult.ReplacedCount}，无需写入 {nativeResult.SkippedCount}。");
					if (!dryRun && importTransaction != null)
					{
						await Task.Run(() => importTransaction.TrackImportedSessionFiles(nativeResult.TouchedSessionFiles, plannedIds));
					}
				}
			}
			if (dryRun)
			{
				long inspectedFiles = contexts.Where((ImportProjectContext context) => context.Plan != null).Sum((ImportProjectContext context) => (long)context.Plan.FileCount);
				long inspectedBytes = contexts.Where((ImportProjectContext context) => context.Plan != null).Sum((ImportProjectContext context) => context.Plan.UncompressedBytes);
				int existingFiles = contexts.Where((ImportProjectContext context) => context.Plan != null).Sum((ImportProjectContext context) => context.Plan.ExistingFileCount);
				string projectCheck = restoreProjectFiles ? $"\n\n项目检查：{contexts.Count} 个项目、{inspectedFiles} 个文件（{ProjectPayloadService.FormatBytes(inspectedBytes)}），同名 {existingFiles} 个。" : string.Empty;
				SetStatus("安全检查完成，没有写入项目或会话。", error: false);
				AppDialog.Show(window, "检查完成", "迁移包可以导入", "本次检查没有写入项目文件或会话。" + projectCheck + $"\n\n对话包：{effectiveBundlePaths.Count} 个通过\n按原始编号匹配：{lineageMatchedCount} 个\n将生成全新编号：{lineageCreatedCount} 个\n\n确认右侧操作记录后即可正式导入。", AppDialogTone.Success, "返回导入页");
				return;
			}
			AppendLog("正在备份 Codex 索引与桌面项目状态，并只登记本次导入的会话……");
			UpdateImportStage("4 / 4 · 验证项目归属", "正在核对索引路径、历史模式、会话文件、子代理父子关系和桌面侧栏项目归属。");
			Dictionary<string, string> titleHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (PackSession session in loadedManifest?.sessions ?? new List<PackSession>())
			{
				if (session != null && !string.IsNullOrWhiteSpace(session.thread_id))
				{
					titleHints[session.thread_id] = session.title;
				}
			}
			foreach (ConversationImportPlan plan in importPlans)
			{
				foreach (KeyValuePair<string, string> pair in plan.IdMap)
				{
					if (titleHints.TryGetValue(pair.Key, out string title))
					{
						titleHints[pair.Value] = title;
					}
				}
			}
			Dictionary<string, string> indexTargets = mapProjectPath ? targetByEffectiveBundle : null;
			TargetedIndexResult indexResult = await Task.Run(() => indexTargets == null ? TargetedThreadIndexer.IndexImportedSessions(codexHome, effectiveBundlePaths, filesBeforeImport, copiesOnly: false, null, titleHints) : TargetedThreadIndexer.IndexImportedSessionsMapped(codexHome, effectiveBundlePaths, filesBeforeImport, copiesOnly: false, indexTargets, titleHints));
			AppendLog($"索引兼容性验证：{indexResult.VisibilityVerifiedCount}/{indexResult.IndexedCount} 条通过；项目路径与会话历史模式均已核对。");
			string paginatedWarning = BuildPaginatedImportWarning(indexResult.PaginatedCount);
			if (!string.IsNullOrWhiteSpace(paginatedWarning))
			{
				AppendLog("\n" + paginatedWarning);
			}
			if (indexResult.DesktopStateFound)
			{
				AppendLog($"桌面项目归属：{indexResult.DesktopAssignmentVerifiedCount}/{indexResult.DesktopAssignmentExpectedCount} 条主对话通过；已登记 {indexResult.DesktopProjectCount} 个项目。");
				if (!string.IsNullOrWhiteSpace(indexResult.DesktopStateBackupPath))
				{
					AppendLog("桌面项目状态备份：" + indexResult.DesktopStateBackupPath);
				}
			}
			else
			{
				AppendLog("未检测到 Codex 桌面项目状态文件；已按 CLI 环境跳过桌面侧栏项目归属。");
			}
			UpdateImportStage("4 / 4 · 验证通过", indexResult.DesktopStateFound ? $"{indexResult.VisibilityVerifiedCount}/{indexResult.IndexedCount} 条索引兼容性、{indexResult.DesktopAssignmentVerifiedCount}/{indexResult.DesktopAssignmentExpectedCount} 条桌面项目归属均已核验。" : $"{indexResult.VisibilityVerifiedCount}/{indexResult.IndexedCount} 条会话索引兼容性已核验。");
			AppendLog($"定点索引完成：新增 {indexResult.InsertedCount} 条，更新 {indexResult.UpdatedCount} 条；全局回填状态未修改。");
			int removedSnapshots = importTransaction == null ? 0 : await Task.Run(() => importTransaction.CommitAndDeleteTemporaryBackups());
			if (removedSnapshots > 0)
			{
				AppendLog("导入验证完成，已清理 " + removedSnapshots + " 个事务安全快照。");
			}
			if (!string.IsNullOrWhiteSpace(indexResult.BackupPath))
			{
				AppendLog("索引备份：" + indexResult.BackupPath);
			}
			List<ImportProjectContext> restored = contexts.Where((ImportProjectContext context) => context.RestoreResult != null).ToList();
			int createdFiles = restored.Sum((ImportProjectContext context) => context.RestoreResult.CreatedFileCount);
			int overwrittenFiles = restored.Sum((ImportProjectContext context) => context.RestoreResult.OverwrittenFileCount);
			int skippedFiles = restored.Sum((ImportProjectContext context) => context.RestoreResult.SkippedFileCount);
			string projectSuccess = restored.Count == 0 ? string.Empty : $"已还原 {restored.Count} 个项目到：\n{target}\n新增 {createdFiles} 个文件，覆盖 {overwrittenFiles} 个，跳过 {skippedFiles} 个。\n\n";
			string desktopSuccess = indexResult.DesktopStateFound ? $"\n桌面项目归属：{indexResult.DesktopAssignmentVerifiedCount}/{indexResult.DesktopAssignmentExpectedCount} 条主对话通过" : "\n桌面项目归属：未检测到桌面状态文件，已跳过";
			string desktopBackup = string.IsNullOrWhiteSpace(indexResult.DesktopStateBackupPath) ? string.Empty : "\n\n桌面项目状态备份：\n" + indexResult.DesktopStateBackupPath;
			bool needsPaginatedVerification = indexResult.PaginatedCount > 0;
			SetStatus(needsPaginatedVerification ? (UiLanguage.IsEnglish ? "Import completed; verify paginated conversations in Codex." : "导入完成，请在 Codex 中验证分页会话。") : (restored.Count == 0 ? "对话导入完成。" : "项目与对话迁移完成。"), error: false);
			string completionTitle = needsPaginatedVerification ? (UiLanguage.IsEnglish ? "Import completed · verification required" : "迁移完成 · 需要验证") : "迁移完成";
			string completionHeading = needsPaginatedVerification ? (UiLanguage.IsEnglish ? "Indexing passed; verify paginated history in Codex" : "索引已通过，请验证分页历史") : "索引、历史模式与桌面项目归属均已验证";
			AppDialog.Show(window, completionTitle, completionHeading, projectSuccess + $"对话已导入 C 盘 Codex 目录，并分别关联到对应项目。\n\n新增索引：{indexResult.InsertedCount} 条\n更新索引：{indexResult.UpdatedCount} 条\n索引兼容性验证：{indexResult.VisibilityVerifiedCount}/{indexResult.IndexedCount} 条通过" + desktopSuccess + (string.IsNullOrWhiteSpace(paginatedWarning) ? string.Empty : "\n\n" + paginatedWarning) + "\n\n现在重新打开 Codex，再打开迁入后的项目目录并实际打开对应对话。" + (string.IsNullOrWhiteSpace(indexResult.BackupPath) ? string.Empty : "\n\n索引备份：\n" + indexResult.BackupPath) + desktopBackup, needsPaginatedVerification ? AppDialogTone.Warning : AppDialogTone.Success, "完成");
		}
		catch (OperationCanceledException ex)
		{
			await RollbackNativeImportAsync(importTransaction);
			AppendLog("\n" + ex.Message);
			SetStatus("操作已取消，没有继续导入。", error: false);
		}
		catch (Exception ex)
		{
			await RollbackNativeImportAsync(importTransaction);
			List<string> restoredTargets = contexts.Where((ImportProjectContext context) => context.RestoreResult != null).Select((ImportProjectContext context) => context.TargetPath).ToList();
			if (restoredTargets.Count > 0)
			{
				AppendLog("\n注意：以下项目文件已还原，但后续会话导入未完成：\n" + string.Join("\n", restoredTargets) + "\n修复问题后可重新导入同一迁移包；也可取消“还原项目文件”或选择“跳过同名文件”，避免再次改动已还原的项目文件。");
			}
			AppendLog("\n失败：" + ex.Message);
			SetStatus("操作失败：" + ex.Message, error: true);
			AppDialog.Show(window, dryRun ? "检查失败" : "导入失败", dryRun ? "迁移包没有通过检查" : "导入没有完成", ex.Message + "\n\n项目文件若已还原，右侧操作记录会明确列出；修复问题后可重新导入同一迁移包；也可取消“还原项目文件”或选择“跳过同名文件”，避免再次改动已还原的项目文件。", AppDialogTone.Error, "查看记录");
		}
		finally
		{
			if (rewriteTemp != null)
			{
				await Task.Run(() => TryDeleteDirectory(rewriteTemp));
			}
			if (temp != null)
			{
				await Task.Run(() => TryDeleteDirectory(temp));
			}
			EndImportProgress();
			SetBusy(busy: false, null);
		}
	}

	private async Task RollbackNativeImportAsync(NativeImportTransaction transaction)
	{
		if (transaction == null)
		{
			return;
		}
		try
		{
			ImportTransactionRollbackResult rollback = await Task.Run(() => transaction.RollbackAndDeleteTemporaryBackups());
			if (rollback.RestoredCount > 0 || rollback.DeletedCount > 0 || rollback.RemovedImportedCount > 0)
			{
				AppendLog($"导入未完成：已恢复 {rollback.RestoredCount} 个原会话，移除 {rollback.RemovedImportedCount} 个本次新增会话，并清理 {rollback.DeletedCount} 个事务安全快照。");
			}
		}
		catch (Exception cleanupError)
		{
			AppendLog("警告：清理导入事务安全快照失败：" + cleanupError.Message);
		}
	}

	internal static string BuildPaginatedImportWarning(int count)
	{
		if (count <= 0)
		{
			return string.Empty;
		}
		return UiLanguage.IsEnglish
			? $"This import contains {count} paginated conversation(s). Their structure and index metadata passed validation, but full-history opening and resumption still depend on the destination Codex version. Keep the source package until you have opened and verified them in Codex."
			: $"本次包含 {count} 个分页型（paginated）会话。其文件结构与索引元数据已通过检查，但完整历史打开和恢复仍取决于目标 Codex 版本；请在 Codex 中实际打开验证，确认前保留源迁移包。";
	}

	private void BeginImportProgress(bool dryRun)
	{
		importStartedAt = DateTime.UtcNow;
		importProgressPanel.Visibility = Visibility.Visible;
		importStageProgress.IsIndeterminate = true;
		inspectButton.Content = UiLanguage.T(dryRun ? "正在检查…" : "先检查（不写入）");
		importButton.Content = UiLanguage.T(dryRun ? "开始导入" : "正在导入…");
		UpdateImportStage(dryRun ? "准备安全检查" : "准备导入", "正在初始化，请稍候。");
	}

	private void UpdateImportStage(string stage, string detail)
	{
		if (importStartedAt == DateTime.MinValue)
		{
			importStartedAt = DateTime.UtcNow;
		}
		TimeSpan elapsed = DateTime.UtcNow - importStartedAt;
		importStageText.Text = UiLanguage.T(stage);
		importStageDetailText.Text = UiLanguage.T(detail);
		importElapsedText.Text = elapsed.TotalHours >= 1.0 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
		importProgressPanel.Visibility = Visibility.Visible;
		SetStatus(stage, error: false);
	}

	private void EndImportProgress()
	{
		importElapsedText.Text = string.Empty;
		importProgressPanel.Visibility = Visibility.Collapsed;
		inspectButton.Content = UiLanguage.T("先检查（不写入）");
		importButton.Content = UiLanguage.T("开始导入");
		importStartedAt = DateTime.MinValue;
	}
	private void SetBusy(bool busy, string message)
	{
		isBusy = busy;
		busyProgress.Visibility = ((!busy) ? Visibility.Collapsed : Visibility.Visible);
		window.Cursor = (busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow);
		refreshButton.IsEnabled = !busy;
		closeButton.IsEnabled = !busy;
		browseBackupFolderButton.IsEnabled = !busy;
		browsePackageButton.IsEnabled = !busy;
		browseTargetButton.IsEnabled = !busy;
		packagePathBox.IsReadOnly = busy;
		targetPathBox.IsReadOnly = busy;
		mapPathCheck.IsEnabled = !busy;
		backupTabButton.IsEnabled = !busy;
		importTabButton.IsEnabled = !busy;
		trashButton.IsEnabled = !busy;
		backupFolderBox.IsEnabled = !busy;
		projectBackupModeRadio.IsEnabled = !busy;
		conversationBackupModeRadio.IsEnabled = !busy;
		mainSessionsTabRadio.IsEnabled = !busy;
		subagentSessionsTabRadio.IsEnabled = !busy;
		selectAllProjectsButton.IsEnabled = !busy;
		clearProjectsButton.IsEnabled = !busy;
		inspectButton.IsEnabled = !busy;
		importButton.IsEnabled = !busy;
		sessionList.IsEnabled = !busy;
		mergeModeRadio.IsEnabled = !busy;
		copyModeRadio.IsEnabled = !busy;
		projectList.IsEnabled = !busy;
		UpdateSessionTypeView();
		UpdateSelectedCount();
		UpdateProjectRestoreControls();
		if (!string.IsNullOrWhiteSpace(message))
		{
			SetStatus(message, error: false);
		}
		languageButton.IsEnabled = !busy;
		Find<System.Windows.Controls.RadioButton>("PrimaryProviderRadio").IsEnabled = !busy;
		Find<System.Windows.Controls.RadioButton>("DshProviderRadio").IsEnabled = !busy;
		UpdateDshControls();
	}

	private void SetStatus(string text, bool error)
	{
		statusText.Text = UiLanguage.T(text);
		statusDot.Fill = (error ? Brush("#D6534D") : Brush("#0D9F76"));
	}

	private void AppendLog(string text)
	{
		if (!string.IsNullOrEmpty(text))
		{
			string localized = UiLanguage.T(text);
			if (importLog.Text == UiLanguage.T("等待选择迁移包……"))
			{
				importLog.Clear();
			}
			importLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + localized + Environment.NewLine);
			importLog.CaretIndex = importLog.Text.Length;
			importLog.ScrollToEnd();
		}
	}

	private static Brush Brush(string color)
	{
		return (Brush)new BrushConverter().ConvertFromString(color);
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}

	private sealed class SessionDeletionPlan
	{
		public SessionInfo Root { get; set; }

		public List<SessionInfo> AffectedSessions { get; set; }
	}

	private sealed class BatchProjectDeleteScope
	{
		public string ProjectPath { get; set; }

		public int TotalMainConversationCount { get; set; }

		public bool AllMainConversationsSelected { get; set; }

		public string AvailabilityBlockReason { get; set; }
	}
}
