using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

internal enum AppLanguage
{
	Chinese,
	English
}

internal static class UiLanguage
{
	private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		{ "Codex 对话与项目管理器", "Codex Conversation Manager" },
		{ "对话与项目管理器", "Conversation Manager" },
		{ "本地运行", "Runs locally" },
		{ "全部", "All" }, { "未归档", "Active" }, { "已归档", "Archived" }, { "归档所选", "Archive selected" }, { "取消归档", "Unarchive" }, { "数据目录", "Data folder" }, { "恢复中断操作", "Recover interrupted operation" },
		{ "最小化到任务栏", "Minimize to taskbar" },
		{ "关闭预览", "Close preview" },
		{ "最小化", "Minimize" },
		{ "最大化", "Maximize" },
		{ "还原", "Restore" },
		{ "关闭", "Close" },
		{ "创建备份", "Create backup" },
		{ "恢复备份", "Restore backup" },
		{ "管理与备份", "Manage and Back Up" },
		{ "导入与恢复", "Import and Restore" },
		{ "1 · 选择备份内容", "1 · Choose what to back up" },
		{ "项目＋对话", "Projects + conversations" },
		{ "仅对话", "Conversations only" },
		{ "生成 .codexproject：项目目录、空目录、全部主对话和子代理对话", "Create a .codexproject containing project folders, empty folders, main conversations, and subagent conversations" },
		{ "生成 .codexchat：跨项目挑选主对话，不包含项目目录", "Create a .codexchat with selected main conversations from multiple projects, without project folders" },
		{ "项目＋对话备份：项目文件与全部关联对话放在同一个文件里。", "Project + conversation backup: project files and all linked conversations are stored in one file." },
		{ "项目＋对话备份：项目文件、主对话和子代理对话放在同一个文件里。", "Project + conversation backup: project files, main conversations, and subagent conversations are stored in one file." },
		{ "仅对话备份：可跨项目勾选主对话，不包含项目文件。", "Conversation-only backup: select main conversations across projects; project files are not included." },
		{ "2 · 选择保存位置", "2 · Choose a save location" },
		{ "迁移包会自动保存到这个文件夹", "Backup packages are saved to this folder automatically" },
		{ "选择文件夹", "Choose folder" },
		{ "自动命名；同名时追加序号，不会覆盖旧备份。", "Files are named automatically; a number is appended instead of overwriting an existing backup." },
		{ "刷新", "Refresh" },
		{ "刷新/修复", "Refresh / Repair" },
		{ "重新读取本地项目和对话，并检测可安全修复的侧边栏残留", "Reload local projects and conversations, and detect sidebar remnants that can be repaired safely" },
		{ "回收站", "Trash" },
		{ "恢复或永久删除已移入回收站的会话，也可处理对应项目", "Restore or permanently delete trashed conversations and optionally process their project folders" },
		{ "等待检测", "Waiting for detection" },
		{ "勾选要迁移的项目", "Select projects to migrate" },
		{ "项目目录和全部对话会进入同一个包", "Project folders and all conversations are stored in one package" },
		{ "选择对话所在项目", "Choose a conversation project" },
		{ "可跨项目勾选要备份的主对话", "Select main conversations to back up across projects" },
		{ "全选项目", "Select all projects" },
		{ "清空", "Clear" },
		{ "将完整项目与全部对话加入批量迁移包", "Add the complete project and all conversations to the migration package" },
		{ "将完整项目、主对话和子代理对话一起加入备份", "Add the complete project, main conversations, and subagent conversations to the backup" },
		{ "选择一个项目", "Select a project" },
		{ "项目路径", "Project path" },
		{ "复制路径", "Copy path" },
		{ "项目文件：尚未统计", "Project files: not measured" },
		{ "主对话和子代理对话已分开管理。", "Main and subagent conversations are managed separately." },
		{ "主对话与项目目录", "Main conversation and project folder" },
		{ "搜索标题、Thread ID 或路径", "Search title, Thread ID, or path" },
		{ "筛选显示：标题、Thread ID 或路径", "Filter displayed items: title, Thread ID, or path" },
		{ "搜索只筛选列表显示；全选和删除只处理当前显示结果", "Search filters the list; Select all and Delete selected affect only the displayed results" },
		{ "主对话", "Main conversations" },
		{ "子代理", "Subagents" },
		{ "全选", "Select all" },
		{ "全不选", "Clear all" },
		{ "选中当前页的全部对话；再次点击可全部取消", "Select every conversation on this page; click again to clear the selection" },
		{ "选择当前搜索结果中的全部对话；再次点击可全部取消", "Select every conversation in the current search results; click again to clear the selection" },
		{ "删除所选", "Delete selected" },
		{ "删除所选主对话", "Delete selected main conversations" },
		{ "删除所选子代理", "Delete selected subagents" },
		{ "删除已选择的主对话", "Delete selected main conversations" },
		{ "删除已选择的子代理", "Delete selected subagents" },
		{ "删除已勾选的对话；选中项目全部主对话后可同时处理项目目录", "Delete selected conversations; select every main conversation in the project to also process its folder" },
		{ "删除已勾选的子代理对话；项目目录保持不变", "Delete selected subagent conversations; the project folder remains unchanged" },
		{ "删除已勾选的全部主对话，并可选择同时处理项目目录", "Delete all selected main conversations and optionally process the project folder" },
		{ "删除已勾选的主对话；选中该项目全部主对话后可同时处理项目目录", "Delete selected main conversations; select every main conversation in this project to also process its folder" },
		{ "只删除当前页已经勾选的对话，项目目录保持不变", "Delete only selected conversations on this page; project folders are left unchanged" },
		{ "删除当前显示结果中已勾选的对话；搜索隐藏项和项目目录保持不变", "Delete selected conversations in the displayed results; hidden search results and the project folder remain unchanged" },
		{ "选择此对话：仅对话模式下用于备份，也可用于批量删除", "Select this conversation for conversation-only backup or batch deletion" },
		{ "查看", "View" },
		{ "查看对话内容", "View conversation" },
		{ "删除…", "Delete…" },
		{ "主对话可同时处理项目；删除存在后代的对话时会一并处理其子代理", "A main conversation can also process its project; deleting a conversation also handles its spawned descendants" },
		{ "没有符合条件的对话", "No matching conversations" },
		{ "已选 0 个项目＋对话", "0 projects + conversations selected" },
		{ "生成 .codexproject：项目目录与全部关联对话一起备份", "Create a .codexproject containing project folders and all linked conversations" },
		{ "生成 .codexchat：只包含勾选的主对话，不包含项目文件", "Create a .codexchat containing only selected main conversations, without project files" },
		{ "备份仅对话", "Back up conversations" },
		{ "生成 .codexchat；可跨多个项目，不包含项目目录", "Create a .codexchat across multiple projects without project folders" },
		{ "备份项目＋对话", "Back up projects + conversations" },
		{ "生成 .codexproject；完整项目目录与全部对话进入同一个文件", "Create a .codexproject containing complete project folders and all conversations" },
		{ "恢复到这台电脑", "Restore to this computer" },
		{ "项目放到你选择的位置；对话仍由 C 盘 Codex 管理。", "Projects go to the location you choose; conversations remain managed by Codex on drive C." },
		{ "1 · 选择迁移包", "1 · Choose a backup package" },
		{ "浏览…", "Browse…" },
		{ "等待选择迁移包", "Waiting for a backup package" },
		{ "仅对话 .codexchat · 项目＋对话 .codexproject · 兼容旧版 .codexpack", "Conversations: .codexchat · Projects + conversations: .codexproject · Legacy .codexpack supported" },
		{ "2 · 选择这台电脑上的项目目录", "2 · Choose the project folder on this computer" },
		{ "目录可以与旧电脑不同，导入时会自动重写对话里的 cwd。", "The folder may differ from the old computer; conversation cwd values are rewritten during import." },
		{ "把对话关联到上面的项目位置", "Link conversations to the project location above" },
		{ "还原包内项目文件", "Restore project files from the package" },
		{ "目标目录已有文件时", "When the destination already contains files" },
		{ "要求目标目录为空（推荐）", "Require an empty destination (recommended)" },
		{ "保留现有同名文件，只补充缺失文件", "Keep existing files and add only missing files" },
		{ "覆盖同名文件（覆盖前自动备份）", "Overwrite matching files (back up before overwrite)" },
		{ "项目文件将还原到上面的目录，会话仍导入 C 盘 Codex 目录。", "Project files are restored to the folder above; conversations are still imported into the Codex folder on drive C." },
		{ "3 · 选择对话导入方式", "3 · Choose how to import conversations" },
		{ "按原始编号合并", "Merge by original ID" },
		{ "只在目标项目内按原始编号查找；找到后合并，首次迁入生成新编号", "Search by original ID only inside the destination project; merge matches and create a new ID on first import" },
		{ "作为新对话复制", "Copy as new conversations" },
		{ "每次都生成全新编号和独立文件，删除一份不会影响另一份", "Create new IDs and independent files every time; deleting one copy does not affect another" },
		{ "推荐 · 按目标项目 + 原始编号识别同一对话；首次迁入生成新编号，以后仍能准确合并。", "Recommended · Identify the same conversation by destination project + original ID; create a new ID on first import and merge accurately later." },
		{ "推荐流程：先检查，通过后再导入。", "Recommended: inspect first, then import after validation passes." },
		{ "先检查（不写入）", "Inspect first (no writes)" },
		{ "开始导入", "Start import" },
		{ "检查与导入记录", "Inspection and import log" },
		{ "项目到所选位置 · 对话回 C 盘", "Projects to selected folders · conversations to drive C" },
		{ "正在准备导入", "Preparing import" },
		{ "界面仍可响应，请勿重复点击。", "The app remains responsive. Do not click repeatedly." },
		{ "等待选择迁移包……", "Waiting for a backup package…" },
		{ "准备就绪", "Ready" },
		{ "对话预览", "Conversation preview" },
		{ "正在读取……", "Loading…" },
		{ "只读预览，不会修改原会话", "Read-only preview · the original conversation is not modified" },
		{ "消息位置导航：点击刻度跳转", "Message navigation: click a tick to jump" },
		{ "消息位置导航", "Message navigation" },
		{ "用户消息导航", "User-message navigation" },
		{ "复制 Thread ID", "Copy Thread ID" },

		{ "知道了", "OK" },
		{ "当前操作尚未完成，请等待完成后再关闭。", "The current operation is not finished. Wait for it to complete before closing." },
		{ "操作进行中", "Operation in progress" },
		{ "暂时不能关闭窗口", "The window cannot be closed yet" },
		{ "当前正在写入或校验本地数据。完成后即可安全关闭。", "Local data is currently being written or verified. You can safely close the window after it completes." },
		{ "继续等待", "Keep waiting" },
		{ "继续", "Continue" },
		{ "取消", "Cancel" },
		{ "重新输入", "Try again" },
		{ "返回设置", "Back to settings" },
		{ "重新选择", "Choose again" },
		{ "修改目录", "Change folder" },
		{ "我先退出 Codex", "I will exit Codex" },
		{ "返回导入页", "Back to import" },
		{ "查看记录", "View log" },
		{ "完成", "Done" },
		{ "选择删除方式", "Choose how to delete" },
		{ "删除对话", "Delete conversation" },
		{ "删除子代理对话", "Delete subagent conversation" },
		{ "未命名会话", "Untitled conversation" },
		{ "会话文件", "Conversation file" },
		{ "移入软件回收站（推荐）", "Move to app trash (recommended)" },
		{ "保留完整会话备份，之后可恢复或永久删除。", "Keep a complete conversation backup that can later be restored or permanently deleted." },
		{ "永久删除会话", "Permanently delete conversation" },
		{ "立即删除本地 JSONL，会话内容无法从本工具恢复。", "Delete the local JSONL immediately; this app cannot restore the conversation afterward." },
		{ "对应项目目录", "Related project folder" },
		{ "同时处理该会话对应的项目目录", "Also process the project folder linked to this conversation" },
		{ "同时处理会话对应的项目目录", "Also process the project folder linked to this conversation" },
		{ "同时处理该项目的目录", "Also process this project folder" },
		{ "已选中该项目全部主对话；可在下方选择是否同时处理项目目录。", "All main conversations in this project are selected; choose below whether to also process the project folder." },
		{ "已选中全部主对话，但项目目录当前不可处理；具体原因见下方。", "All main conversations are selected, but the project folder cannot currently be processed; see the reason below." },
		{ "只处理已选择的主对话；未选择的主对话、全部子代理和项目目录不会被删除。", "Only selected main conversations are processed; unselected main conversations, all subagents, and the project folder remain unchanged." },
		{ "只处理已选择的子代理；未选择的子代理、全部主对话和项目目录不会被删除。", "Only selected subagents are processed; unselected subagents, all main conversations, and the project folder remain unchanged." },
		{ "未记录项目路径", "No project path recorded" },
		{ "移入 Windows 回收站（可恢复）", "Move to Windows Recycle Bin (recoverable)" },
		{ "永久删除项目目录（不可恢复）", "Permanently delete project folder (not recoverable)" },
		{ "永久删除项目目录", "Permanently delete project folder" },
		{ "项目目录可在 Windows 回收站中恢复。", "The project folder can be recovered from the Windows Recycle Bin." },
		{ "递归删除全部项目文件，无法从本工具恢复。", "Recursively delete all project files; this app cannot restore them." },
		{ "确认项目永久删除", "Confirm permanent project deletion" },
		{ "确认删除对话和项目", "Confirm conversation and project deletion" },
		{ "项目名称不匹配", "Project name does not match" },
		{ "项目文件夹名输入不正确，未执行删除。请重新输入后再继续。", "The project folder name is incorrect. Nothing was deleted; enter it again to continue." },
		{ "永久删除所选", "Permanently delete selected" },
		{ "处理项目目录", "Process project folder" },
		{ "删除回收站会话对应的项目", "Delete the project linked to the trashed conversation" },
		{ "软件回收站", "App trash" },
		{ "这里只显示由“删除对话”移入回收站的会话。恢复不会自动恢复已删除的项目目录。", "Only conversations moved here through Delete Conversation are shown. Restoring a conversation does not restore a deleted project folder." },
		{ "会话", "Conversation" },
		{ "删除时间", "Deleted" },
		{ "大小", "Size" },
		{ "项目", "Project" },
		{ "回收站中没有可管理的会话备份。", "There are no conversation backups in the trash." },
		{ "请选择一条会话。", "Select a conversation." },
		{ "恢复会话", "Restore conversation" },
		{ "处理对应项目", "Process related project" },
		{ "永久删除备份", "Permanently delete backup" },
		{ "原位置：", "Original location: " },
		{ "备份位置：", "Backup location: " },
		{ "输入项目文件夹名进行确认", "Enter the project folder name to confirm" },

		{ "未找到", "Not found" },
		{ "读取失败", "Read failed" },
		{ "此项目没有子代理对话", "This project has no subagent conversations" },
		{ "此项目没有符合条件的主对话", "This project has no matching main conversations" },
		{ "勾选要处理的主对话；选中项目全部主对话后，可同时处理项目目录。", "Select main conversations to process. After selecting every main conversation in the project, you can also process the project folder." },
		{ "已选中该项目全部主对话；“删除所选”中可选择同时处理项目目录。", "All main conversations in this project are selected; Delete selected can also process the project folder." },
		{ "选择项目＋对话", "Select projects + conversations" },
		{ "选择仅对话", "Select conversations only" },
		{ "生成 .codexproject，包含项目目录和全部关联对话", "Create a .codexproject containing project folders and all linked conversations" },
		{ "生成 .codexchat，只包含勾选的主对话", "Create a .codexchat containing only selected main conversations" },
		{ "提示", "Notice" },
		{ "无法打开", "Unable to open" },
		{ "你", "You" },
		{ "时间未知", "Unknown time" },
		{ "未知", "Unknown" },
		{ "（无标题对话）", "(Untitled conversation)" },
		{ "子代理对话", "Subagent conversation" },
		{ "所属主对话 · ", "Parent conversation · " },
		{ "未找到所属主对话", "Parent conversation not found" },
		{ "项目文件：正在统计……", "Project files: measuring…" },
		{ "项目文件：无法统计", "Project files: unavailable" },
		{ "项目文件：无法统计 · ", "Project files: unavailable · " },
		{ "目录不存在", "Folder does not exist" },
		{ "（已处理）", " (processed)" },
		{ "（不存在）", " (missing)" },
		{ "原始 .codexbundle", "Raw .codexbundle" },
		{ "无法读取迁移包", "Unable to read backup package" },
		{ "全部对话备份", "All-conversation backup" },
		{ "已选对话备份", "Selected-conversation backup" },
		{ "要求目标目录为空", "Require an empty destination" },
		{ "覆盖同名文件，并先创建恢复备份", "Overwrite matching files after creating a recovery backup" },
		{ "已选择只导入包内对话，不还原项目文件。", "Only conversations will be imported; project files will not be restored." },
		{ "正在检查…", "Inspecting…" },
		{ "正在导入…", "Importing…" },
		{ "准备安全检查", "Preparing safety inspection" },
		{ "准备导入", "Preparing import" },
		{ "正在初始化，请稍候。", "Initializing. Please wait." },
		{ "选择 Codex 迁移包", "Choose a Codex backup package" },
		{ "选择迁移包保存文件夹", "Choose a folder for backup packages" },
		{ "选择这台电脑上的项目目录", "Choose the project folder on this computer" },
		{ "Codex 正式备份 (*.codexchat;*.codexproject)", "Codex backups (*.codexchat;*.codexproject)" },
		{ "旧版备份 (*.codexpack;*.codexbundle)", "Legacy backups (*.codexpack;*.codexbundle)" },
		{ "所有文件 (*.*)", "All files (*.*)" },
		{ "切换为英文", "Switch to English" },
	};

	private static readonly KeyValuePair<string, string>[] AllMappings = English.Concat(UiEnglishCatalog.Entries).Concat(UiEnglishCatalog2.Entries).Concat(UiEnglishCatalog3.Entries).Concat(UiEnglishCatalog4.Entries)
		.OrderByDescending((KeyValuePair<string, string> pair) => pair.Key.Length)
		.ToArray();

	private static readonly KeyValuePair<string, string>[] Phrases = AllMappings
		.Where((KeyValuePair<string, string> pair) => pair.Key.Length >= 3)
		.OrderByDescending((KeyValuePair<string, string> pair) => pair.Key.Length)
		.ToArray();

	public static AppLanguage Current { get; private set; } = AppLanguage.Chinese;

	public static bool IsEnglish => Current == AppLanguage.English;

	public static string Code => IsEnglish ? "en" : "zh-CN";

	public static void Initialize(string overrideCode = null)
	{
		if (TryParse(overrideCode, out AppLanguage overridden))
		{
			Current = overridden;
			return;
		}
		try
		{
			string path = SettingsPath();
			if (!File.Exists(path))
			{
				path = LegacySettingsPath();
			}
			if (File.Exists(path) && TryParse(File.ReadAllText(path, Encoding.UTF8).Trim(), out AppLanguage saved))
			{
				Current = saved;
			}
		}
		catch
		{
			Current = AppLanguage.Chinese;
		}
	}

	public static void SetAndSave(AppLanguage language)
	{
		Current = language;
		try
		{
			string path = SettingsPath();
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, Code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
		}
	}

	public static string T(string text) => Brand(TranslateOriginal(text));

	private static string Brand(string text) => !BackendContext.IsDsh ? text : text?.Replace("Codex", "DeepSeek").Replace(".codexchat", ".dshchat").Replace(".codexproject", ".dshproject")
        .Replace(" · 兼容旧版 .codexpack", "").Replace(" · Legacy .codexpack supported", "")
        .Replace("C 盘 DeepSeek", "本机 DSH").Replace("DeepSeek on drive C", "local DSH")
        .Replace("对话回 C 盘", "对话回 DSH 数据目录").Replace("会话仍导入 C 盘", "会话仍导入 DSH 数据目录")
        .Replace("conversations to drive C", "conversations to DSH data")
        .Replace("DeepSeek folder on drive C", "DSH data folder")
        .Replace("把原始编号保存在会话中供下次识别", "把原始编号保存在 DSH 同源索引中供下次识别")
        .Replace("本次操作会先通过 DeepSeek 官方删除接口更新任务目录，再处理本地会话文件与索引。", "本次操作会处理 DSH 本地会话目录、工作区索引与投影缓存。")
        .Replace("The DeepSeek thread/delete protocol is applied first, followed by local file and index cleanup.", "This operation updates local DSH session folders, workspace indexes and projection caches.")
        .Replace("each will receive its own recoverable trash copy", "they will be recoverable together with the parent batch");

	private static string TranslateOriginal(string text)
	{
		if (!IsEnglish || string.IsNullOrEmpty(text))
		{
			return text ?? string.Empty;
		}
		if (English.TryGetValue(text, out string exact))
		{
			return exact;
		}
		if (UiEnglishCatalog.Entries.TryGetValue(text, out string extendedExact))
		{
			return extendedExact;
		}
		if (UiEnglishCatalog2.Entries.TryGetValue(text, out string extendedExact2))
		{
			return extendedExact2;
		}
		if (UiEnglishCatalog3.Entries.TryGetValue(text, out string extendedExact3))
		{
			return extendedExact3;
		}
		if (UiEnglishCatalog4.Entries.TryGetValue(text, out string extendedExact4))
		{
			return extendedExact4;
		}
		string translated = text;
		foreach (KeyValuePair<string, string> pair in Phrases)
		{
			if (translated.IndexOf(pair.Key, StringComparison.Ordinal) >= 0)
			{
				translated = translated.Replace(pair.Key, pair.Value);
			}
		}
		return TranslatePatterns(translated);
	}

	public static string LoadXaml(string path)
	{
		string xaml = File.ReadAllText(path, Encoding.UTF8);
		if (!IsEnglish)
		{
			return Brand(xaml);
		}
		foreach (KeyValuePair<string, string> pair in AllMappings)
		{
			xaml = xaml.Replace(pair.Key, pair.Value);
		}
		return Brand(TranslatePatterns(xaml));
	}

	private static string TranslatePatterns(string text)
	{
		return text
			.Replace(" 个主对话", " main conversations")
			.Replace(" 个子代理对话", " subagent conversations")
			.Replace(" 个子代理", " subagents")
			.Replace(" 个项目＋对话", " projects + conversations")
			.Replace(" 个项目", " projects")
			.Replace(" 个对话", " conversations")
			.Replace(" 条文本消息", " text messages")
			.Replace(" 个文件", " files")
			.Replace("最近更新 ", "Last updated ")
			.Replace("正在读取 ", "Loading ")
			.Replace("读取失败：", "Read failed: ")
			.Replace("复制失败：", "Copy failed: ")
			.Replace("删除失败：", "Delete failed: ")
			.Replace("恢复失败：", "Restore failed: ")
			.Replace("永久删除失败：", "Permanent deletion failed: ")
			.Replace("项目处理失败：", "Project processing failed: ")
			.Replace("操作失败：", "Operation failed: ")
			.Replace("备份失败：", "Backup failed: ")
			.Replace("批量备份失败：", "Batch backup failed: ")
			.Replace("已复制项目路径：", "Project path copied: ")
			.Replace("已复制 Thread ID：", "Thread ID copied: ")
			.Replace("已选择：", "Selected: ")
			.Replace("创建时间：", "Created: ")
			.Replace("项目文件：", "Project files: ")
			.Replace("原项目：", "Source project: ")
			.Replace("包含项目：", "Projects: ")
			.Replace("目标：", "Destination: ")
			.Replace("目标目录：", "Destination: ")
			.Replace("索引备份：", "Index backup: ")
			.Replace("项目覆盖备份：", "Project overwrite backup: ")
			.Replace("桌面项目状态备份：", "Desktop project state backup: ")
			.Replace("正在校验", "Validating ")
			.Replace("正在封装", "Packaging ")
			.Replace("正在压缩", "Compressing ")
			.Replace("正在备份", "Backing up ")
			.Replace("正在还原", "Restoring ")
			.Replace("正在导入", "Importing ")
			.Replace("正在检查", "Inspecting ")
			.Replace("正在处理", "Processing ")
			.Replace(" 个。", ".")
			.Replace(" 项", " items")
			.Replace(" 个", "")
			.Replace(" 条", "")
			.Replace("，", ", ")
			.Replace("。", ".")
			.Replace("；", "; ")
			.Replace("：", ": ")
			.Replace("（", " (")
			.Replace("）", ")")
			.Replace("共 ", "Total ")
			.Replace("有 ", "There are ")
			.Replace("失败: ", "Failed: ")
			.Replace("失败：", "Failed: ");
	}

	private static bool TryParse(string value, out AppLanguage language)
	{
		string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
		if (normalized == "en" || normalized == "en-us" || normalized == "english")
		{
			language = AppLanguage.English;
			return true;
		}
		if (normalized == "zh" || normalized == "zh-cn" || normalized == "chinese")
		{
			language = AppLanguage.Chinese;
			return true;
		}
		language = AppLanguage.Chinese;
		return false;
	}

	private static string SettingsPath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexConversationManager", "language.txt");
	}

	private static string LegacySettingsPath()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexConversationMigrator", "language.txt");
	}
}
