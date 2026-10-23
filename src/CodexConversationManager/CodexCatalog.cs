using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexConversationManager;

internal static class CodexCatalog
{
	public static Dictionary<string, string> ReadIndexedThreadCwds()
	{
		string text = ResolveCodexHome();
		if (!Directory.Exists(text))
		{
			return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		}
		string text2 = WinSqliteMaintenance.FindActiveDatabase(text);
		string[] array = (string.IsNullOrWhiteSpace(text2) ? new string[0] : new string[1] { text2 });
		string[] array2 = array;
		foreach (string databasePath in array2)
		{
			try
			{
				return (from x in WinSqliteReader.ReadThreads(databasePath)
					where x != null && !string.IsNullOrWhiteSpace(x.Id)
					select x).GroupBy((DbThread x) => x.Id, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, DbThread> group) => group.Key, (IGrouping<string, DbThread> group) => TextHelpers.StripExtendedPrefix(group.First().Cwd), StringComparer.OrdinalIgnoreCase);
			}
			catch
			{
			}
		}
		return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	}

	public static CatalogResult Build(List<SessionInfo> sessions)
	{
		return Build(sessions, ResolveCodexHome());
	}

	public static CatalogResult Build(List<SessionInfo> sessions, string codexHome)
	{
		CatalogResult catalogResult = new CatalogResult();
		catalogResult.Projects = new List<ProjectGroup>();
		catalogResult.Diagnostic = string.Empty;
		catalogResult.UsedCodexIndex = false;
		CatalogResult catalogResult2 = catalogResult;
		string text = string.IsNullOrWhiteSpace(codexHome) ? ResolveCodexHome() : Path.GetFullPath(codexHome);
		string statePath = Path.Combine(text, ".codex-global-state.json");
		string path = Path.Combine(text, "session_index.jsonl");
		int num = ApplyRolloutMetadata(sessions, text);
		ApplyIndexedParentRelationships(sessions, text);
        var currentPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string activeDatabase = WinSqliteMaintenance.FindActiveDatabase(text);
        if (!string.IsNullOrWhiteSpace(activeDatabase))
            foreach (var thread in WinSqliteReader.ReadThreads(activeDatabase))
                if (!string.IsNullOrWhiteSpace(thread.Id) && !string.IsNullOrWhiteSpace(thread.RolloutPath)) currentPaths[thread.Id] = TextHelpers.CanonicalPath(RolloutStorage.ResolveExistingPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath)));
		Dictionary<string, SessionInfo> dictionary = sessions.Where((SessionInfo x) => !string.IsNullOrWhiteSpace(x.ThreadId)).GroupBy((SessionInfo x) => x.ThreadId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, SessionInfo> group) => group.Key, (IGrouping<string, SessionInfo> group) => (from x in @group
			orderby (currentPaths.TryGetValue(x.ThreadId, out string currentPath) && currentPath == TextHelpers.CanonicalPath(x.SessionPath)) descending, x.MetadataVerified descending, x.UpdatedDate descending
			select x).First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> dictionary2 = ReadSessionIndex(path);
		catalogResult2.UsedCodexIndex = num > 0 && dictionary2.Count > 0;
		foreach (SessionInfo value4 in dictionary.Values)
		{
			if (dictionary2.TryGetValue(value4.ThreadId, out var value) && !string.IsNullOrWhiteSpace(value))
			{
				value4.Title = value;
			}
		}
		ReadDesktopProjects(statePath, out var definitions, out var projectless, out var assignments);
		Dictionary<string, ProjectGroup> dictionary3 = new Dictionary<string, ProjectGroup>(StringComparer.OrdinalIgnoreCase);
		foreach (ProjectDefinition item in definitions)
		{
			string path2 = item.RootPaths.FirstOrDefault() ?? string.Empty;
			dictionary3[item.Id] = new ProjectGroup
			{
				ProjectId = item.Id,
				DisplayName = (string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileName(path2) : item.Name),
				ProjectPath = TextHelpers.StripExtendedPrefix(path2),
				SortIndex = item.SortIndex,
				Sessions = new List<SessionInfo>()
			};
		}
		foreach (SessionInfo session in dictionary.Values)
		{
			ProjectGroup value2 = null;
			if (assignments.TryGetValue(session.ThreadId, out var value3))
			{
				dictionary3.TryGetValue(value3, out value2);
			}
			if (value2 == null && !projectless.Contains(session.ThreadId))
			{
				ProjectDefinition projectDefinition = (from x in definitions
					where x.RootPaths.Any((string root) => TextHelpers.IsWithin(session.Cwd, root))
					orderby x.RootPaths.Max((string root) => TextHelpers.CanonicalPath(root).Length) descending
					select x).FirstOrDefault();
				if (projectDefinition != null)
				{
					dictionary3.TryGetValue(projectDefinition.Id, out value2);
				}
			}
			if (value2 == null)
			{
				string text2 = TextHelpers.CanonicalPath(session.Cwd);
				if (text2.Length == 0)
				{
					text2 = "unassigned";
				}
				string text3 = "derived:" + text2;
				if (!dictionary3.TryGetValue(text3, out value2))
				{
					string text4 = TextHelpers.StripExtendedPrefix(session.Cwd);
					string text5 = Path.GetFileName(text4.TrimEnd('\\'));
					if (string.IsNullOrWhiteSpace(text5))
					{
						text5 = "独立任务";
					}
					value2 = (dictionary3[text3] = new ProjectGroup
					{
						ProjectId = text3,
						DisplayName = (projectless.Contains(session.ThreadId) ? ("独立 · " + text5) : text5),
						ProjectPath = text4,
						SortIndex = int.MaxValue,
						Sessions = new List<SessionInfo>()
					});
				}
			}
			value2.Sessions.Add(session);
		}
		foreach (ProjectGroup value5 in dictionary3.Values)
		{
			value5.Sessions = value5.Sessions.OrderByDescending((SessionInfo x) => x.UpdatedDate).ToList();
			ApplySubagentLabels(value5);
			if (value5.MainCount == 0 && value5.InternalCount > 0)
			{
				value5.DisplayName = (UiLanguage.IsEnglish ? "Orphaned subagents · " : "孤立子代理 · ") + value5.DisplayName;
				value5.SortIndex = int.MaxValue;
			}
		}
		catalogResult2.Projects = (from x in dictionary3.Values
			where x.MainCount > 0 || x.InternalCount > 0
			orderby x.SortIndex, x.LastUpdated descending
			select x).ToList();
		catalogResult2.MainCount = catalogResult2.Projects.Sum((ProjectGroup x) => x.MainCount);
		catalogResult2.InternalCount = catalogResult2.Projects.Sum((ProjectGroup x) => x.InternalCount);
		if (string.IsNullOrWhiteSpace(catalogResult2.Diagnostic))
		{
			catalogResult2.Diagnostic = (catalogResult2.UsedCodexIndex ? "已按 Codex 标题与 session_meta 校准" : "部分旧记录缺少 session_meta，已使用兼容识别");
		}
		return catalogResult2;
	}

	private static void ApplyIndexedParentRelationships(IEnumerable<SessionInfo> sessions, string codexHome)
	{
		try
		{
			string databasePath = WinSqliteMaintenance.FindActiveDatabase(codexHome);
			if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
			{
				return;
			}
			Dictionary<string, DbThread> indexed = WinSqliteReader.ReadThreads(databasePath).Where((DbThread item) => item != null && !string.IsNullOrWhiteSpace(item.Id)).GroupBy((DbThread item) => item.Id, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, DbThread> group) => group.Key, (IGrouping<string, DbThread> group) => group.First(), StringComparer.OrdinalIgnoreCase);
			foreach (SessionInfo session in sessions ?? Enumerable.Empty<SessionInfo>())
			{
				if (session == null || string.IsNullOrWhiteSpace(session.ThreadId) || !indexed.TryGetValue(session.ThreadId, out DbThread thread))
				{
					continue;
				}
				if (thread.IsSubagent || !string.IsNullOrWhiteSpace(thread.ParentThreadId))
				{
					session.IsSubagent = true;
				}
				if (!string.IsNullOrWhiteSpace(thread.ParentThreadId))
				{
					session.ParentThreadId = thread.ParentThreadId;
				}
			}
		}
		catch
		{
		}
	}

	public static string ResolveCodexHome()
	{
		string environmentVariable = Environment.GetEnvironmentVariable("CODEX_HOME");
		if (!string.IsNullOrWhiteSpace(environmentVariable) && Directory.Exists(environmentVariable))
		{
			return Path.GetFullPath(environmentVariable);
		}
		string text = Environment.GetEnvironmentVariable("USERPROFILE");
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}
		return Path.Combine(text, ".codex");
	}

	private static int ApplyRolloutMetadata(IEnumerable<SessionInfo> sessions, string codexHome)
	{
		int num = 0;
		JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
		foreach (SessionInfo session in sessions)
		{
			string text = ResolveSessionPath(session, codexHome);
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				continue;
			}
			try
			{
				session.SessionPath = TextHelpers.StripExtendedPrefix(text);
				session.SizeBytes = new FileInfo(text).Length;
			}
			catch
			{
			}
			if (text.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			try
			{
				string text2;
				using (FileStream stream = new FileStream(text, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096);
					text2 = streamReader.ReadLine();
				}
				if (string.IsNullOrWhiteSpace(text2) || !(javaScriptSerializer.DeserializeObject(text2) is Dictionary<string, object> dictionary))
				{
					continue;
				}
				object value;
				Dictionary<string, object> dictionary2 = (dictionary.TryGetValue("payload", out value) ? (value as Dictionary<string, object>) : null);
				if (dictionary2 == null)
				{
					continue;
				}
				string metadataSessionId = GetString(dictionary2, "session_id");
				string text3 = GetString(dictionary2, "id");
				if (string.IsNullOrWhiteSpace(text3))
				{
					text3 = metadataSessionId;
				}
				if (!string.IsNullOrWhiteSpace(text3))
				{
					session.ThreadId = text3;
				}
				session.CreatedAt = GetString(dictionary2, "timestamp");
				session.OriginThreadId = ConversationLineage.ResolveOriginThreadId(dictionary2, session.ThreadId);
				session.CliVersion = GetString(dictionary2, "cli_version");
				session.ModelProvider = GetString(dictionary2, "model_provider");
				string text4 = GetString(dictionary2, "cwd");
				if (!string.IsNullOrWhiteSpace(text4))
				{
					session.Cwd = TextHelpers.StripExtendedPrefix(text4);
				}
				string a = GetString(dictionary2, "thread_source");
				string text5 = string.Empty;
				bool flag = false;
				if (dictionary2.TryGetValue("source", out var value2) && value2 != null)
				{
					text5 = value2 as string;
					if (text5 == null)
					{
						text5 = javaScriptSerializer.Serialize(value2);
						flag = value2 is Dictionary<string, object> dictionary3 && dictionary3.ContainsKey("subagent");
					}
				}
				if (!string.IsNullOrWhiteSpace(text5))
				{
					session.Source = text5;
				}
				session.IsSubagent = string.Equals(a, "subagent", StringComparison.OrdinalIgnoreCase) || flag || text5.IndexOf("\"subagent\"", StringComparison.OrdinalIgnoreCase) >= 0;
				if (session.IsSubagent)
				{
					string parentThreadId = GetString(dictionary2, "parent_thread_id");
					if (string.IsNullOrWhiteSpace(parentThreadId) && !string.IsNullOrWhiteSpace(metadataSessionId) && !string.Equals(metadataSessionId, session.ThreadId, StringComparison.OrdinalIgnoreCase))
					{
						parentThreadId = metadataSessionId;
					}
					session.ParentThreadId = parentThreadId;
				}
				if (session.IsSubagent)
				{
					session.SubagentPurpose = ResolveSubagentPurpose(text5, text);
				}
				session.MetadataVerified = true;
				num++;
			}
			catch
			{
			}
		}
		return num;
	}

	private static void ApplySubagentLabels(ProjectGroup project)
	{
		if (project?.Sessions == null)
		{
			return;
		}
		Dictionary<string, SessionInfo> mainById = project.Sessions.Where((SessionInfo session) => !session.IsSubagent && !string.IsNullOrWhiteSpace(session.ThreadId)).GroupBy((SessionInfo session) => session.ThreadId, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, SessionInfo> group) => group.Key, (IGrouping<string, SessionInfo> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (SessionInfo subagent in project.Sessions.Where((SessionInfo session) => session.IsSubagent).OrderBy((SessionInfo session) => session.UpdatedDate))
		{
			string parentKey = string.IsNullOrWhiteSpace(subagent.ParentThreadId) ? "unlinked" : subagent.ParentThreadId;
			ordinals.TryGetValue(parentKey, out int ordinal);
			ordinal++;
			ordinals[parentKey] = ordinal;
			SessionInfo parent = null;
			if (!string.IsNullOrWhiteSpace(subagent.ParentThreadId))
			{
				mainById.TryGetValue(subagent.ParentThreadId, out parent);
			}
			string missingParent = string.IsNullOrWhiteSpace(subagent.ParentThreadId)
				? UiLanguage.T("未找到所属主对话")
				: (UiLanguage.IsEnglish ? "Parent conversation no longer exists · " : "父对话已不存在 · ") + subagent.ParentThreadId;
			subagent.ParentDisplayTitle = parent?.DisplayTitle ?? missingParent;
			string descriptor = !string.IsNullOrWhiteSpace(subagent.SubagentPurpose) ? subagent.SubagentPurpose : (parent?.DisplayTitle ?? string.Empty);
			subagent.SubagentDisplayTitle = (UiLanguage.IsEnglish ? "Subagent conversation " : "子代理对话 ") + ordinal + (string.IsNullOrWhiteSpace(descriptor) ? string.Empty : " · " + descriptor);
		}
	}

	private static string ResolveSubagentPurpose(string source, string sessionPath)
	{
		if ((source ?? string.Empty).IndexOf("guardian", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return UiLanguage.IsEnglish ? "Approval guardian" : "内部审批守卫（guardian）";
		}
		return ConversationReader.ReadTitleCandidate(sessionPath);
	}

	public static string ResolveSessionPath(SessionInfo session)
	{
		return ResolveSessionPath(session, ResolveCodexHome());
	}

	private static string ResolveSessionPath(SessionInfo session, string codexHome)
	{
		if (!string.IsNullOrWhiteSpace(session.SessionPath))
		{
			string text = TextHelpers.StripExtendedPrefix(session.SessionPath);
			if (File.Exists(text))
			{
				return text;
			}
			if (File.Exists(session.SessionPath))
			{
				return session.SessionPath;
			}
		}
		if (string.IsNullOrWhiteSpace(session.RelativePath))
		{
			return string.Empty;
		}
		string path = session.RelativePath.Replace('/', Path.DirectorySeparatorChar);
		string text2 = Path.Combine(codexHome, "sessions", path);
		if (File.Exists(text2))
		{
			return text2;
		}
		string text3 = Path.Combine(codexHome, "archived_sessions", path);
		if (!File.Exists(text3))
		{
			return string.Empty;
		}
		return text3;
	}

	private static string FirstLine(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		string[] array = value.Replace("\r", string.Empty).Split('\n');
		return TextHelpers.CleanLine((array.Length == 0) ? value : array[0], 160, string.Empty);
	}

	private static Dictionary<string, string> ReadSessionIndex(string path)
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(path))
		{
			return dictionary;
		}
		JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
		foreach (string item in File.ReadLines(path, Encoding.UTF8))
		{
			if (string.IsNullOrWhiteSpace(item))
			{
				continue;
			}
			try
			{
				if (javaScriptSerializer.DeserializeObject(item) is Dictionary<string, object> dictionary2 && dictionary2.TryGetValue("id", out var value) && dictionary2.TryGetValue("thread_name", out var value2))
				{
					string text = Convert.ToString(value);
					string value3 = Convert.ToString(value2);
					if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(value3))
					{
						dictionary[text] = value3;
					}
				}
			}
			catch
			{
			}
		}
		return dictionary;
	}

	private static void ReadDesktopProjects(string statePath, out List<ProjectDefinition> definitions, out HashSet<string> projectless, out Dictionary<string, string> assignments)
	{
		definitions = new List<ProjectDefinition>();
		projectless = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (!File.Exists(statePath))
		{
			return;
		}
		try
		{
			JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
			if (!(javaScriptSerializer.DeserializeObject(File.ReadAllText(statePath, Encoding.UTF8)) is Dictionary<string, object> dictionary))
			{
				return;
			}
			List<string> list = new List<string>();
			if (dictionary.TryGetValue("project-order", out var value) && value is object[] source)
			{
				list.AddRange(source.Select(Convert.ToString));
			}
			object value2;
			Dictionary<string, object> dictionary2 = (dictionary.TryGetValue("local-projects", out value2) ? (value2 as Dictionary<string, object>) : null);
			if (dictionary2 != null)
			{
				foreach (KeyValuePair<string, object> pair in dictionary2)
				{
					KeyValuePair<string, object> keyValuePair = pair;
					if (!(keyValuePair.Value is Dictionary<string, object> dictionary3))
					{
						continue;
					}
					string name = GetString(dictionary3, "name");
					List<string> list2 = new List<string>();
					if (dictionary3.TryGetValue("rootPaths", out var value3) && value3 is object[] source2)
					{
						list2.AddRange(source2.Select((object x) => TextHelpers.StripExtendedPrefix(Convert.ToString(x))));
					}
					if (list2.Count != 0)
					{
						int num = list.FindIndex(delegate(string x)
						{
							KeyValuePair<string, object> keyValuePair3 = pair;
							return string.Equals(x, keyValuePair3.Key, StringComparison.OrdinalIgnoreCase);
						});
						List<ProjectDefinition> obj = definitions;
						ProjectDefinition projectDefinition = new ProjectDefinition();
						KeyValuePair<string, object> keyValuePair2 = pair;
						projectDefinition.Id = keyValuePair2.Key;
						projectDefinition.Name = name;
						projectDefinition.RootPaths = list2;
						projectDefinition.SortIndex = ((num < 0) ? 2147483646 : num);
						obj.Add(projectDefinition);
					}
				}
			}
			if (dictionary.TryGetValue("projectless-thread-ids", out var value4) && value4 is object[] array)
			{
				object[] array2 = array;
				foreach (object obj2 in array2)
				{
					projectless.Add(Convert.ToString(obj2));
				}
			}
			object value5;
			Dictionary<string, object> dictionary4 = (dictionary.TryGetValue("thread-project-assignments", out value5) ? (value5 as Dictionary<string, object>) : null);
			if (dictionary4 == null)
			{
				return;
			}
			foreach (KeyValuePair<string, object> item2 in dictionary4)
			{
				if (item2.Value is Dictionary<string, object> item)
				{
					string value6 = GetString(item, "projectId");
					if (!string.IsNullOrWhiteSpace(value6))
					{
						assignments[item2.Key] = value6;
					}
				}
			}
		}
		catch
		{
		}
	}

	private static string GetString(Dictionary<string, object> item, string key)
	{
		if (!item.TryGetValue(key, out var value) || value == null)
		{
			return string.Empty;
		}
		return Convert.ToString(value);
	}
}
