using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexConversationManager;

internal static class DeleteOptionsDialog
{
	public static DeleteOptions Show(Window owner, SessionInfo session, string projectPath, int relatedConversationCount, bool allowProjectActions = true)
	{
		Window dialog = DialogUi.CreateWindow(owner, session != null && session.IsSubagent ? "删除子代理对话" : "删除对话", 650.0, allowProjectActions ? 570.0 : 430.0);
		Grid root = DialogUi.CreateRoot();
		dialog.Content = root;
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		TextBlock heading = DialogUi.Text("选择删除方式", 23.0, FontWeights.SemiBold, "#20231F");
		root.Children.Add(heading);

		TextBlock sessionText = DialogUi.Text(session == null ? "未命名会话" : session.DisplayTitle, 13.0, FontWeights.SemiBold, "#4F5650");
		sessionText.Margin = new Thickness(0.0, 8.0, 0.0, 18.0);
		sessionText.TextTrimming = TextTrimming.CharacterEllipsis;
		Grid.SetRow(sessionText, 1);
		root.Children.Add(sessionText);

		GroupBox conversationGroup = DialogUi.Group("会话文件");
		Grid.SetRow(conversationGroup, 2);
		root.Children.Add(conversationGroup);
		StackPanel conversationPanel = new StackPanel();
		conversationGroup.Content = conversationPanel;
		RadioButton moveToTrash = DialogUi.Radio("移入软件回收站（推荐）", "保留完整会话备份，之后可恢复或永久删除。", isChecked: true);
		RadioButton permanent = DialogUi.Radio("永久删除会话", "立即删除本地 JSONL，会话内容无法从本工具恢复。", isChecked: false);
		conversationPanel.Children.Add(moveToTrash);
		conversationPanel.Children.Add(permanent);

		GroupBox projectGroup = DialogUi.Group("对应项目目录");
		projectGroup.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
		projectGroup.Visibility = allowProjectActions ? Visibility.Visible : Visibility.Collapsed;
		Grid.SetRow(projectGroup, 3);
		root.Children.Add(projectGroup);
		StackPanel projectPanel = new StackPanel();
		projectGroup.Content = projectPanel;
		bool projectAvailable = allowProjectActions && !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
		CheckBox processProject = new CheckBox
		{
			Content = UiLanguage.T("同时处理该会话对应的项目目录"),
			FontWeight = FontWeights.SemiBold,
			Foreground = DialogUi.Brush("#30352F"),
			IsEnabled = projectAvailable
		};
		projectPanel.Children.Add(processProject);

		TextBlock pathText = DialogUi.Text(string.IsNullOrWhiteSpace(projectPath) ? "未记录项目路径" : projectPath, 11.5, FontWeights.Normal, "#777D76");
		pathText.Margin = new Thickness(22.0, 7.0, 0.0, 0.0);
		pathText.TextWrapping = TextWrapping.Wrap;
		projectPanel.Children.Add(pathText);

		if (relatedConversationCount > 1)
		{
			TextBlock relatedWarning = DialogUi.Text("此项目还关联 " + (relatedConversationCount - 1) + " 个其他主对话；删除项目不会自动删除那些会话记录。", 11.5, FontWeights.Normal, "#A15B18");
			relatedWarning.Margin = new Thickness(22.0, 7.0, 0.0, 0.0);
			relatedWarning.TextWrapping = TextWrapping.Wrap;
			projectPanel.Children.Add(relatedWarning);
		}

		ComboBox projectMode = new ComboBox
		{
			Margin = new Thickness(22.0, 11.0, 0.0, 0.0),
			Height = 40.0,
			IsEnabled = false,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		projectMode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("移入 Windows 回收站（可恢复）"), Tag = ProjectDeleteMode.RecycleBin });
		projectMode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("永久删除项目目录（不可恢复）"), Tag = ProjectDeleteMode.Permanent });
		projectMode.SelectedIndex = 0;
		projectPanel.Children.Add(projectMode);

		StackPanel typedPanel = new StackPanel
		{
			Margin = new Thickness(22.0, 10.0, 0.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		string projectName = SafeDirectoryName(projectPath);
		typedPanel.Children.Add(DialogUi.Text("请输入项目文件夹名 “" + projectName + "” 以确认永久删除：", 11.5, FontWeights.Normal, "#9D403B"));
		TextBox typedConfirmation = new TextBox { Height = 36.0, Margin = new Thickness(0.0, 6.0, 0.0, 0.0), Padding = new Thickness(11.0, 0.0, 11.0, 0.0) };
		typedPanel.Children.Add(typedConfirmation);
		projectPanel.Children.Add(typedPanel);

		processProject.Checked += delegate
		{
			projectMode.IsEnabled = true;
			UpdateTypedPanel(projectMode, typedPanel);
		};
		processProject.Unchecked += delegate
		{
			projectMode.IsEnabled = false;
			typedPanel.Visibility = Visibility.Collapsed;
			typedConfirmation.Text = string.Empty;
		};
		projectMode.SelectionChanged += delegate
		{
			UpdateTypedPanel(projectMode, typedPanel);
		};

		StackPanel buttons = DialogUi.ButtonBar();
		Grid.SetRow(buttons, 4);
		root.Children.Add(buttons);
		Button cancel = DialogUi.Button("取消", primary: false);
		cancel.IsCancel = true;
		Button confirm = DialogUi.Button("继续", primary: true);
		confirm.IsDefault = true;
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);

		DeleteOptions result = null;
		confirm.Click += delegate
		{
			ProjectDeleteMode selectedProjectMode = ProjectDeleteMode.None;
			if (processProject.IsChecked == true)
			{
				selectedProjectMode = SelectedProjectMode(projectMode);
				if (selectedProjectMode == ProjectDeleteMode.Permanent && !string.Equals((typedConfirmation.Text ?? string.Empty).Trim(), projectName, StringComparison.OrdinalIgnoreCase))
				{
					AppDialog.Show(dialog, "确认项目永久删除", "项目名称不匹配", "项目文件夹名输入不正确，未执行删除。请重新输入后再继续。", AppDialogTone.Warning, "重新输入");
					typedConfirmation.Focus();
					return;
				}
			}
			result = new DeleteOptions
			{
				ConversationMode = permanent.IsChecked == true ? ConversationDeleteMode.Permanent : ConversationDeleteMode.MoveToTrash,
				ProjectMode = selectedProjectMode
			};
			dialog.DialogResult = true;
		};
		cancel.Click += delegate
		{
			dialog.DialogResult = false;
		};
		dialog.ShowDialog();
		return result;
	}

	private static void UpdateTypedPanel(ComboBox mode, FrameworkElement typedPanel)
	{
		typedPanel.Visibility = SelectedProjectMode(mode) == ProjectDeleteMode.Permanent && mode.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
	}

	internal static ProjectDeleteMode SelectedProjectMode(ComboBox combo)
	{
		ComboBoxItem item = combo.SelectedItem as ComboBoxItem;
		return item != null && item.Tag is ProjectDeleteMode mode ? mode : ProjectDeleteMode.RecycleBin;
	}

	internal static string SafeDirectoryName(string projectPath)
	{
		try
		{
			return new DirectoryInfo(projectPath).Name;
		}
		catch
		{
			return string.Empty;
		}
	}
}

internal static class SessionBatchDeleteDialog
{
	public static DeleteOptions Show(Window owner, ProjectGroup project, IList<SessionInfo> sessions, string projectPath, bool allMainConversationsSelected, int totalMainConversationCount, string projectAvailabilityBlockReason)
	{
		if (project == null || sessions == null || sessions.Count == 0)
		{
			return null;
		}
		bool deletingSubagents = sessions.All((SessionInfo session) => session.IsSubagent);
		string typeLabel = deletingSubagents ? "子代理" : "主对话";
		bool projectExists = !string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath);
		bool projectAvailable = !deletingSubagents && allMainConversationsSelected && projectExists && string.IsNullOrWhiteSpace(projectAvailabilityBlockReason);
		Window dialog = DialogUi.CreateWindow(owner, "删除所选" + typeLabel, 700.0, deletingSubagents ? 520.0 : 670.0);
		dialog.ResizeMode = ResizeMode.CanResize;
		dialog.MinWidth = 620.0;
		dialog.MinHeight = deletingSubagents ? 470.0 : 580.0;
		Grid root = DialogUi.CreateRoot();
		dialog.Content = root;
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		root.Children.Add(DialogUi.Text("删除已选择的" + typeLabel, 23.0, FontWeights.SemiBold, "#20231F"));
		TextBlock context = DialogUi.Text(project.DisplayName + "\n" + sessions.Count + " 个" + typeLabel + " · 共 " + TextHelpers.FormatBytes(sessions.Sum((SessionInfo session) => session.SizeBytes)) + "\n" + projectPath, 12.0, FontWeights.Normal, "#646A63");
		context.Margin = new Thickness(0.0, 8.0, 0.0, 16.0);
		context.TextWrapping = TextWrapping.Wrap;
		Grid.SetRow(context, 1);
		root.Children.Add(context);

		ScrollViewer optionScroller = new ScrollViewer
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
		};
		Grid.SetRow(optionScroller, 2);
		root.Children.Add(optionScroller);
		StackPanel options = new StackPanel();
		optionScroller.Content = options;

		GroupBox conversationGroup = DialogUi.Group("会话文件");
		StackPanel conversationPanel = new StackPanel();
		conversationGroup.Content = conversationPanel;
		RadioButton moveToTrash = DialogUi.Radio("移入软件回收站（推荐）", "所选记录会逐个保留，可在本工具回收站中恢复或永久删除。", isChecked: true);
		RadioButton permanent = DialogUi.Radio("永久删除所选", "立即删除所选" + typeLabel + " JSONL，之后无法从本工具恢复。", isChecked: false);
		conversationPanel.Children.Add(moveToTrash);
		conversationPanel.Children.Add(permanent);
		options.Children.Add(conversationGroup);

		Border warning = new Border
		{
			Margin = new Thickness(0.0, 14.0, 0.0, 0.0),
			Padding = new Thickness(12.0, 10.0, 12.0, 10.0),
			Background = DialogUi.Brush("#FFF6E8"),
			BorderBrush = DialogUi.Brush("#ECD8B4"),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(10.0),
			Child = DialogUi.Text(deletingSubagents
				? "只处理已选择的子代理；未选择的子代理、全部主对话和项目目录不会被删除。"
				: projectAvailable
					? "已选中该项目全部主对话；可在下方选择是否同时处理项目目录。"
					: allMainConversationsSelected
						? "已选中全部主对话，但项目目录当前不可处理；具体原因见下方。"
					: "只处理已选择的主对话；未选择的主对话、全部子代理和项目目录不会被删除。", 11.5, FontWeights.Normal, "#866426")
		};
		options.Children.Add(warning);

		GroupBox projectGroup = DialogUi.Group("对应项目目录");
		projectGroup.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
		projectGroup.Visibility = deletingSubagents ? Visibility.Collapsed : Visibility.Visible;
		options.Children.Add(projectGroup);
		StackPanel projectPanel = new StackPanel();
		projectGroup.Content = projectPanel;
		CheckBox processProject = new CheckBox
		{
			Content = UiLanguage.T("同时处理该项目的目录"),
			FontWeight = FontWeights.SemiBold,
			Foreground = DialogUi.Brush("#30352F"),
			IsEnabled = projectAvailable
		};
		projectPanel.Children.Add(processProject);

		TextBlock pathText = DialogUi.Text(string.IsNullOrWhiteSpace(projectPath) ? "未记录项目路径" : projectPath, 11.5, FontWeights.Normal, "#777D76");
		pathText.Margin = new Thickness(22.0, 7.0, 0.0, 0.0);
		pathText.TextWrapping = TextWrapping.Wrap;
		projectPanel.Children.Add(pathText);

		string availabilityText;
		if (!string.IsNullOrWhiteSpace(projectAvailabilityBlockReason))
		{
			availabilityText = projectAvailabilityBlockReason;
		}
		else if (projectAvailable)
		{
			availabilityText = UiLanguage.IsEnglish
				? "All " + totalMainConversationCount + " main conversations in this project are selected. The project is processed only after every conversation operation succeeds."
				: "已选中该项目全部 " + totalMainConversationCount + " 个主对话；只有全部会话处理成功后，才会处理项目目录。";
		}
		else if (!allMainConversationsSelected)
		{
			availabilityText = UiLanguage.IsEnglish
				? "Select all " + totalMainConversationCount + " main conversations in this project to enable project processing. Clear the search to reveal hidden items."
				: "需选中该项目全部 " + totalMainConversationCount + " 个主对话后，才能处理项目目录；如有搜索条件，请先清除以显示隐藏项。";
		}
		else
		{
			availabilityText = UiLanguage.IsEnglish
				? "The recorded project folder does not exist, so it cannot be processed."
				: "记录的项目目录不存在，当前不能处理。";
		}
		TextBlock availability = DialogUi.Text(availabilityText, 11.5, FontWeights.Normal, projectAvailable ? "#5D8F7F" : "#A15B18");
		availability.Margin = new Thickness(22.0, 7.0, 0.0, 0.0);
		availability.TextWrapping = TextWrapping.Wrap;
		projectPanel.Children.Add(availability);

		ComboBox projectMode = new ComboBox
		{
			Margin = new Thickness(22.0, 11.0, 0.0, 0.0),
			Height = 40.0,
			IsEnabled = false,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		projectMode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("移入 Windows 回收站（可恢复）"), Tag = ProjectDeleteMode.RecycleBin });
		projectMode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("永久删除项目目录（不可恢复）"), Tag = ProjectDeleteMode.Permanent });
		projectMode.SelectedIndex = 0;
		projectPanel.Children.Add(projectMode);

		string projectName = DeleteOptionsDialog.SafeDirectoryName(projectPath);
		StackPanel typedPanel = new StackPanel
		{
			Margin = new Thickness(22.0, 10.0, 0.0, 0.0),
			Visibility = Visibility.Collapsed
		};
		typedPanel.Children.Add(DialogUi.Text("请输入项目文件夹名 “" + projectName + "” 以确认永久删除：", 11.5, FontWeights.Normal, "#9D403B"));
		TextBox typedConfirmation = new TextBox
		{
			Height = 36.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
			Padding = new Thickness(11.0, 0.0, 11.0, 0.0)
		};
		typedPanel.Children.Add(typedConfirmation);
		projectPanel.Children.Add(typedPanel);

		processProject.Checked += delegate
		{
			projectMode.IsEnabled = true;
			typedPanel.Visibility = DeleteOptionsDialog.SelectedProjectMode(projectMode) == ProjectDeleteMode.Permanent ? Visibility.Visible : Visibility.Collapsed;
		};
		processProject.Unchecked += delegate
		{
			projectMode.IsEnabled = false;
			typedPanel.Visibility = Visibility.Collapsed;
			typedConfirmation.Text = string.Empty;
		};
		projectMode.SelectionChanged += delegate
		{
			typedPanel.Visibility = projectMode.IsEnabled && DeleteOptionsDialog.SelectedProjectMode(projectMode) == ProjectDeleteMode.Permanent ? Visibility.Visible : Visibility.Collapsed;
		};

		StackPanel buttons = DialogUi.ButtonBar();
		Grid.SetRow(buttons, 3);
		root.Children.Add(buttons);
		Button cancel = DialogUi.Button("取消", primary: false);
		cancel.IsCancel = true;
		Button confirm = DialogUi.Button("继续", primary: true);
		confirm.IsDefault = true;
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);

		DeleteOptions result = null;
		confirm.Click += delegate
		{
			ProjectDeleteMode selectedProjectMode = ProjectDeleteMode.None;
			if (processProject.IsChecked == true)
			{
				selectedProjectMode = DeleteOptionsDialog.SelectedProjectMode(projectMode);
				if (selectedProjectMode == ProjectDeleteMode.Permanent && !string.Equals((typedConfirmation.Text ?? string.Empty).Trim(), projectName, StringComparison.OrdinalIgnoreCase))
				{
					AppDialog.Show(dialog, "确认项目永久删除", "项目名称不匹配", "项目文件夹名输入不正确，未执行删除。请重新输入后再继续。", AppDialogTone.Warning, "重新输入");
					typedConfirmation.Focus();
					return;
				}
			}
			result = new DeleteOptions
			{
				ConversationMode = permanent.IsChecked == true ? ConversationDeleteMode.Permanent : ConversationDeleteMode.MoveToTrash,
				ProjectMode = selectedProjectMode
			};
			dialog.DialogResult = true;
		};
		cancel.Click += delegate { dialog.DialogResult = false; };
		dialog.ShowDialog();
		return result;
	}
}

internal static class ProjectDeleteOptionsDialog
{
	public static ProjectDeleteMode Show(Window owner, string projectPath, string conversationTitle)
	{
		Window dialog = DialogUi.CreateWindow(owner, "处理项目目录", 620.0, 430.0);
		Grid root = DialogUi.CreateRoot();
		dialog.Content = root;
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		root.Children.Add(DialogUi.Text("删除回收站会话对应的项目", 22.0, FontWeights.SemiBold, "#20231F"));
		TextBlock context = DialogUi.Text((conversationTitle ?? "未命名会话") + "\n" + (projectPath ?? "未记录项目路径"), 12.0, FontWeights.Normal, "#646A63");
		context.Margin = new Thickness(0.0, 9.0, 0.0, 16.0);
		context.TextWrapping = TextWrapping.Wrap;
		Grid.SetRow(context, 1);
		root.Children.Add(context);

		StackPanel options = new StackPanel();
		Grid.SetRow(options, 2);
		root.Children.Add(options);
		RadioButton recycle = DialogUi.Radio("移入 Windows 回收站（推荐）", "项目目录可在 Windows 回收站中恢复。", isChecked: true);
		RadioButton permanent = DialogUi.Radio("永久删除项目目录", "递归删除全部项目文件，无法从本工具恢复。", isChecked: false);
		options.Children.Add(recycle);
		options.Children.Add(permanent);
		string projectName = DeleteOptionsDialog.SafeDirectoryName(projectPath);
		StackPanel typedPanel = new StackPanel { Margin = new Thickness(25.0, 8.0, 0.0, 0.0), Visibility = Visibility.Collapsed };
		typedPanel.Children.Add(DialogUi.Text("请输入项目文件夹名 “" + projectName + "” 以确认：", 11.5, FontWeights.Normal, "#9D403B"));
		TextBox typed = new TextBox { Height = 36.0, Margin = new Thickness(0.0, 6.0, 0.0, 0.0), Padding = new Thickness(11.0, 0.0, 11.0, 0.0) };
		typedPanel.Children.Add(typed);
		options.Children.Add(typedPanel);
		permanent.Checked += delegate { typedPanel.Visibility = Visibility.Visible; };
		permanent.Unchecked += delegate
		{
			typedPanel.Visibility = Visibility.Collapsed;
			typed.Text = string.Empty;
		};

		StackPanel buttons = DialogUi.ButtonBar();
		Grid.SetRow(buttons, 3);
		root.Children.Add(buttons);
		Button cancel = DialogUi.Button("取消", primary: false);
		cancel.IsCancel = true;
		Button confirm = DialogUi.Button("继续", primary: true);
		confirm.IsDefault = true;
		buttons.Children.Add(cancel);
		buttons.Children.Add(confirm);
		ProjectDeleteMode result = ProjectDeleteMode.None;
		confirm.Click += delegate
		{
			if (permanent.IsChecked == true && !string.Equals((typed.Text ?? string.Empty).Trim(), projectName, StringComparison.OrdinalIgnoreCase))
			{
				AppDialog.Show(dialog, "确认项目永久删除", "项目名称不匹配", "项目文件夹名输入不正确，未执行删除。请重新输入后再继续。", AppDialogTone.Warning, "重新输入");
				typed.Focus();
				return;
			}
			result = permanent.IsChecked == true ? ProjectDeleteMode.Permanent : ProjectDeleteMode.RecycleBin;
			dialog.DialogResult = true;
		};
		cancel.Click += delegate { dialog.DialogResult = false; };
		dialog.ShowDialog();
		return result;
	}
}

internal static class TrashManagerDialog
{
	public static TrashActionRequest Show(Window owner, IList<TrashSessionInfo> items)
	{
		Window dialog = DialogUi.CreateWindow(owner, "软件回收站", 1050.0, 640.0);
		dialog.MinWidth = 820.0;
		dialog.MinHeight = 520.0;
		dialog.ResizeMode = ResizeMode.CanResize;
		Grid root = DialogUi.CreateRoot();
		dialog.Content = root;
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		root.Children.Add(DialogUi.Text("软件回收站", 23.0, FontWeights.SemiBold, "#20231F"));
		TextBlock help = DialogUi.Text("这里只显示由“删除对话”移入回收站的会话。恢复不会自动恢复已删除的项目目录。", 12.0, FontWeights.Normal, "#697069");
		help.Margin = new Thickness(0.0, 7.0, 0.0, 14.0);
		Grid.SetRow(help, 1);
		root.Children.Add(help);

		ListView list = new ListView
		{
			ItemsSource = items,
			BorderBrush = DialogUi.Brush("#DADDD7"),
			BorderThickness = new Thickness(1.0),
			Background = Brushes.White,
			HorizontalContentAlignment = HorizontalAlignment.Stretch
		};
		GridView view = new GridView();
		view.Columns.Add(new GridViewColumn { Header = UiLanguage.T("会话"), Width = 260.0, DisplayMemberBinding = new Binding("DisplayTitle") });
		view.Columns.Add(new GridViewColumn { Header = UiLanguage.T("删除时间"), Width = 150.0, DisplayMemberBinding = new Binding("DisplayDeletedAt") });
		view.Columns.Add(new GridViewColumn { Header = UiLanguage.T("大小"), Width = 80.0, DisplayMemberBinding = new Binding("DisplaySize") });
		view.Columns.Add(new GridViewColumn { Header = UiLanguage.T("项目"), Width = 440.0, DisplayMemberBinding = new Binding("DisplayProject") });
		list.View = view;
		Grid.SetRow(list, 2);
		root.Children.Add(list);

		TextBlock detail = DialogUi.Text(items == null || items.Count == 0 ? "回收站中没有可管理的会话备份。" : "请选择一条会话。", 11.5, FontWeights.Normal, "#777D76");
		detail.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
		detail.TextWrapping = TextWrapping.Wrap;
		Grid.SetRow(detail, 3);
		root.Children.Add(detail);

		StackPanel buttons = DialogUi.ButtonBar();
		Grid.SetRow(buttons, 4);
		root.Children.Add(buttons);
		Button restore = DialogUi.Button("恢复会话", primary: true);
		Button deleteProject = DialogUi.Button("处理对应项目", primary: false);
		Button deletePermanently = DialogUi.Button("永久删除备份", primary: false);
		deletePermanently.Foreground = DialogUi.Brush("#A33D38");
		Button close = DialogUi.Button("关闭", primary: false);
		close.IsCancel = true;
		restore.IsEnabled = false;
		deleteProject.IsEnabled = false;
		deletePermanently.IsEnabled = false;
		buttons.Children.Add(restore);
		buttons.Children.Add(deleteProject);
		buttons.Children.Add(deletePermanently);
		buttons.Children.Add(close);

		list.SelectionChanged += delegate
		{
			TrashSessionInfo selected = list.SelectedItem as TrashSessionInfo;
			bool hasSelection = selected != null;
			restore.IsEnabled = hasSelection;
			deletePermanently.IsEnabled = hasSelection;
			deleteProject.IsEnabled = hasSelection && string.IsNullOrWhiteSpace(selected.ProjectDeleteMode) && !string.IsNullOrWhiteSpace(selected.ProjectPath) && Directory.Exists(selected.ProjectPath);
			detail.Text = UiLanguage.T(hasSelection ? "原位置：" + selected.OriginalPath + "\n备份位置：" + selected.BackupPath : "请选择一条会话。");
		};
		if (items != null && items.Count > 0)
		{
			list.SelectedIndex = 0;
		}

		TrashActionRequest request = new TrashActionRequest { Action = TrashAction.None };
		Action<TrashAction> choose = delegate(TrashAction action)
		{
			TrashSessionInfo selected = list.SelectedItem as TrashSessionInfo;
			if (selected == null)
			{
				return;
			}
			request.Action = action;
			request.Item = selected;
			dialog.DialogResult = true;
		};
		restore.Click += delegate { choose(TrashAction.Restore); };
		deleteProject.Click += delegate { choose(TrashAction.DeleteProject); };
		deletePermanently.Click += delegate { choose(TrashAction.DeletePermanently); };
		close.Click += delegate { dialog.DialogResult = false; };
		list.MouseDoubleClick += delegate
		{
			if (list.SelectedItem != null)
			{
				choose(TrashAction.Restore);
			}
		};
		dialog.ShowDialog();
		return request;
	}
}

internal static class DialogUi
{
	public static Window CreateWindow(Window owner, string title, double width, double height)
	{
		Window dialog = new Window
		{
			Owner = owner,
			Title = UiLanguage.T(title),
			Width = width,
			Height = height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ShowInTaskbar = false,
			ResizeMode = ResizeMode.NoResize,
			Background = Brush("#F7F7F4"),
			FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI")
		};
		if (owner?.Icon != null)
		{
			dialog.Icon = owner.Icon;
		}
		dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
		{
			Source = new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DialogTheme.xaml"), UriKind.Absolute)
		});
		return dialog;
	}

	public static Grid CreateRoot()
	{
		return new Grid { Margin = new Thickness(24.0) };
	}

	public static TextBlock Text(string text, double size, FontWeight weight, string color)
	{
		return new TextBlock
		{
			Text = UiLanguage.T(text),
			FontSize = size,
			FontWeight = weight,
			Foreground = Brush(color)
		};
	}

	public static GroupBox Group(string header)
	{
		return new GroupBox
		{
			Header = UiLanguage.T(header),
			Padding = new Thickness(14.0, 10.0, 14.0, 12.0),
			BorderBrush = Brush("#DADDD7"),
			Foreground = Brush("#30352F"),
			FontWeight = FontWeights.SemiBold
		};
	}

	public static RadioButton Radio(string title, string description, bool isChecked)
	{
		StackPanel text = new StackPanel();
		text.Children.Add(Text(title, 13.0, FontWeights.SemiBold, "#30352F"));
		TextBlock detail = Text(description, 11.5, FontWeights.Normal, "#70766F");
		detail.Margin = new Thickness(0.0, 3.0, 0.0, 0.0);
		text.Children.Add(detail);
		return new RadioButton
		{
			Content = text,
			IsChecked = isChecked,
			Margin = new Thickness(0.0, 3.0, 0.0, 9.0),
			VerticalContentAlignment = VerticalAlignment.Top
		};
	}

	public static StackPanel ButtonBar()
	{
		return new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 18.0, 0.0, 0.0)
		};
	}

	public static Button Button(string text, bool primary)
	{
		return new Button
		{
			Content = UiLanguage.T(text),
			MinWidth = 94.0,
			Height = 36.0,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Padding = new Thickness(14.0, 0.0, 14.0, 0.0),
			Background = primary ? Brush("#0D9F76") : Brush("#F8F9F6"),
			Foreground = primary ? Brushes.White : Brush("#343934"),
			BorderBrush = primary ? Brush("#0B8865") : Brush("#DADDD7"),
			BorderThickness = new Thickness(1.0),
			FontWeight = FontWeights.SemiBold,
			Cursor = Cursors.Hand
		};
	}

	public static Window CreateThemePreviewForTest()
	{
		Window dialog = CreateWindow(null, "删除对话 · 组件统一预览", 650.0, 570.0);
		Grid root = CreateRoot();
		dialog.Content = root;
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.Children.Add(Text("选择删除方式", 23.0, FontWeights.SemiBold, "#20231F"));
		TextBlock caption = Text("主对话与项目目录", 12.0, FontWeights.Normal, "#697069");
		caption.Margin = new Thickness(0.0, 7.0, 0.0, 16.0);
		Grid.SetRow(caption, 1);
		root.Children.Add(caption);

		StackPanel content = new StackPanel();
		Grid.SetRow(content, 2);
		root.Children.Add(content);
		GroupBox conversation = Group("会话文件");
		StackPanel conversationPanel = new StackPanel();
		conversationPanel.Children.Add(Radio("移入软件回收站（推荐）", "保留完整会话备份，之后可恢复或永久删除。", isChecked: true));
		conversationPanel.Children.Add(Radio("永久删除会话", "立即删除本地 JSONL，会话内容无法从本工具恢复。", isChecked: false));
		conversation.Content = conversationPanel;
		content.Children.Add(conversation);

		GroupBox project = Group("对应项目目录");
		project.Margin = new Thickness(0.0, 14.0, 0.0, 0.0);
		StackPanel projectPanel = new StackPanel();
		projectPanel.Children.Add(new CheckBox { Content = UiLanguage.T("同时处理会话对应的项目目录"), IsChecked = true, FontWeight = FontWeights.SemiBold });
		ComboBox mode = new ComboBox { Margin = new Thickness(26.0, 11.0, 0.0, 0.0), SelectedIndex = 1 };
		mode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("移入 Windows 回收站（可恢复）") });
		mode.Items.Add(new ComboBoxItem { Content = UiLanguage.T("永久删除项目目录（不可恢复）") });
		projectPanel.Children.Add(mode);
		projectPanel.Children.Add(new TextBox { Height = 36.0, Margin = new Thickness(26.0, 10.0, 0.0, 0.0), Text = UiLanguage.T("输入项目文件夹名进行确认") });
		project.Content = projectPanel;
		content.Children.Add(project);

		StackPanel buttons = ButtonBar();
		Grid.SetRow(buttons, 3);
		buttons.Children.Add(Button("取消", primary: false));
		buttons.Children.Add(Button("继续", primary: true));
		dialog.Tag = mode;
		root.Children.Add(buttons);
		return dialog;
	}

	public static bool VerifyThemeForTest()
	{
		Window dialog = CreateThemePreviewForTest();
		bool verified = HasControlTemplate(dialog.TryFindResource(typeof(ComboBox)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(ComboBoxItem)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(TextBox)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(Button)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(CheckBox)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(RadioButton)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(GroupBox)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(ListViewItem)) as Style) &&
			HasControlTemplate(dialog.TryFindResource(typeof(GridViewColumnHeader)) as Style);
		dialog.Close();
		return verified;
	}

	private static bool HasControlTemplate(Style style)
	{
		return style != null && style.Setters.OfType<Setter>().Any((Setter setter) => setter.Property == Control.TemplateProperty);
	}

	private static T FindVisualChild<T>(DependencyObject root) where T : DependencyObject
	{
		if (root == null)
		{
			return null;
		}
		for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			if (child is T match)
			{
				return match;
			}
			T nested = FindVisualChild<T>(child);
			if (nested != null)
			{
				return nested;
			}
		}
		return null;
	}

	public static Brush Brush(string hex)
	{
		return (Brush)new BrushConverter().ConvertFromString(hex);
	}
}
