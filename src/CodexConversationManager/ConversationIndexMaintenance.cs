using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexConversationManager;

internal static class ConversationIndexMaintenance
{
	private const string RepairLedgerName = "sidebar-remnant-repairs.json";
	private static readonly Regex MissingThreadIdPattern = new Regex(@"(?:no rollout found for thread id|thread not loaded:)\s*(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
	private static readonly Regex ConversationIdPattern = new Regex(@"conversationId=(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

	internal static string LogRootOverride { get; set; }

	internal static bool HasCompletedRepairs(string codexHome) => false; // Old repair ledgers are evidence, never deletion authority.

	public static List<DbThread> FindOrphanedThreads(string codexHome)
	{
		string databasePath = WinSqliteMaintenance.FindActiveDatabase(codexHome);
		if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
		{
			return new List<DbThread>();
		}
		string sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions"));
		string archivedRoot = Path.GetFullPath(Path.Combine(codexHome, "archived_sessions"));
		List<DbThread> result = new List<DbThread>();
		foreach (DbThread thread in WinSqliteReader.ReadThreads(databasePath))
		{
			if (thread == null || string.IsNullOrWhiteSpace(thread.Id) || string.IsNullOrWhiteSpace(thread.RolloutPath))
			{
				continue;
			}
			try
			{
				string path = Path.GetFullPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath));
				if ((TextHelpers.IsWithin(path, sessionsRoot) || TextHelpers.IsWithin(path, archivedRoot)) && !RolloutStorage.ExistsInEitherEncoding(path))
				{
					result.Add(thread);
				}
			}
			catch
			{
			}
		}
		return result;
	}

	public static OrphanIndexRepairResult RepairSelectedOrphans(string codexHome, IEnumerable<string> confirmedThreadIds)
	{
		var ids = new HashSet<string>(confirmedThreadIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		var missing = FindOrphanedThreads(codexHome).Where(t => ids.Contains(t.Id)).ToList();
		foreach (var thread in missing) EnsureNoLiveDescendants(codexHome, thread.Id);
		// A missing rollout is not evidence of an intentional deletion. Preserve its
		// history, index, caches and any recovery material for explicit recovery.
		return new OrphanIndexRepairResult { DetectedCount = missing.Count };
	}

	public static List<DbThread> FindDeletedSidebarRemnants(string codexHome)
	{
		string databasePath = WinSqliteMaintenance.FindActiveDatabase(codexHome);
		if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
		{
			return new List<DbThread>();
		}
		string backupPath = FindLatestIndexBackup(codexHome);
		if (string.IsNullOrWhiteSpace(backupPath))
		{
			return new List<DbThread>();
		}
		HashSet<string> logConfirmedIds = FindLogConfirmedMissingThreadIds(backupPath);
		if (logConfirmedIds.Count == 0)
		{
			return new List<DbThread>();
		}
		HashSet<string> activeIds = new HashSet<string>(WinSqliteReader.ReadThreads(databasePath).Select((DbThread thread) => thread.Id), StringComparer.OrdinalIgnoreCase);
		HashSet<string> completedIds = ReadRepairLedger(codexHome);
		return WinSqliteReader.ReadThreads(backupPath)
			.Where((DbThread thread) => thread != null && !string.IsNullOrWhiteSpace(thread.Id) && logConfirmedIds.Contains(thread.Id) && !activeIds.Contains(thread.Id) && !completedIds.Contains(thread.Id) && IsMissingSessionPath(codexHome, thread.RolloutPath))
			.GroupBy((DbThread thread) => thread.Id, StringComparer.OrdinalIgnoreCase)
			.Select((IGrouping<string, DbThread> group) => group.First())
			.OrderByDescending((DbThread thread) => thread.UpdatedAtMilliseconds)
			.ToList();
	}

	public static OrphanIndexRepairResult RepairDeletedSidebarRemnants(string codexHome, IEnumerable<string> confirmedThreadIds)
	{
		var ids = new HashSet<string>(confirmedThreadIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		return new OrphanIndexRepairResult { DetectedCount = FindDeletedSidebarRemnants(codexHome).Count(t => ids.Contains(t.Id)) };
	}

	public static DesktopTaskCacheInvalidationResult InvalidateCompletedRepairCaches(string codexHome)
	{
		// A restored thread can have an ID in a historical repair ledger.
		// Refresh must never replay old deletions against current data.
		return new DesktopTaskCacheInvalidationResult();
	}

	private static HashSet<string> FindLogConfirmedMissingThreadIds(string backupPath)
	{
		HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string logRoot = string.IsNullOrWhiteSpace(LogRootOverride)
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex", "Logs")
			: Path.GetFullPath(LogRootOverride);
		if (!Directory.Exists(logRoot))
		{
			return result;
		}
		DateTime earliest = File.GetLastWriteTimeUtc(backupPath).AddMinutes(-10.0);
		IEnumerable<string> logs;
		try
		{
			logs = Directory.EnumerateFiles(logRoot, "*.log", SearchOption.AllDirectories)
				.Where((string path) => File.GetLastWriteTimeUtc(path) >= earliest)
				.OrderByDescending(File.GetLastWriteTimeUtc)
				.Take(120)
				.ToArray();
		}
		catch
		{
			return result;
		}
		foreach (string logPath in logs)
		{
			try
			{
				using (FileStream stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						bool rolloutFailure = line.IndexOf("rollout_not_found", StringComparison.OrdinalIgnoreCase) >= 0 ||
							line.IndexOf("no rollout found for thread id", StringComparison.OrdinalIgnoreCase) >= 0 ||
							line.IndexOf("thread not loaded:", StringComparison.OrdinalIgnoreCase) >= 0 ||
							(line.IndexOf("failed to resolve rollout path", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("file does not exist", StringComparison.OrdinalIgnoreCase) >= 0);
						if (!rolloutFailure)
						{
							continue;
						}
						foreach (Match match in MissingThreadIdPattern.Matches(line))
						{
							result.Add(match.Groups["id"].Value);
						}
						if (line.IndexOf("rollout_not_found", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							Match conversation = ConversationIdPattern.Match(line);
							if (conversation.Success)
							{
								result.Add(conversation.Groups["id"].Value);
							}
						}
					}
				}
			}
			catch
			{
			}
		}
		return result;
	}

	public static List<LiveDescendantInfo> FindLiveDescendants(string codexHome, IEnumerable<string> rootThreadIds)
	{
		string[] roots = (rootThreadIds ?? Enumerable.Empty<string>()).Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		string databasePath = WinSqliteMaintenance.FindActiveDatabase(codexHome);
		if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath) || roots.Length == 0)
		{
			return new List<LiveDescendantInfo>();
		}
		List<DbThread> threads = WinSqliteReader.ReadThreads(databasePath);
		Dictionary<string, string> parentByChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, ThreadHeaderInfo> headerByThread = new Dictionary<string, ThreadHeaderInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, DbThread> byId = threads.Where((DbThread item) => item != null && !string.IsNullOrWhiteSpace(item.Id)).GroupBy((DbThread item) => item.Id, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, DbThread> group) => group.Key, (IGrouping<string, DbThread> group) => group.First(), StringComparer.OrdinalIgnoreCase);
		foreach (DbThread thread in byId.Values)
		{
			string parentId = thread.ParentThreadId;
			string rolloutPath = RolloutStorage.ResolveExistingPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath));
			if (!string.IsNullOrWhiteSpace(rolloutPath) && File.Exists(rolloutPath))
			{
				try
				{
					ThreadHeaderInfo header = ReadThreadHeader(rolloutPath);
					headerByThread[thread.Id] = header;
					if (!string.IsNullOrWhiteSpace(header.ParentThreadId))
					{
						parentId = header.ParentThreadId;
					}
				}
				catch
				{
				}
			}
			if (!string.IsNullOrWhiteSpace(parentId))
			{
				parentByChild[thread.Id] = parentId;
			}
		}
		Dictionary<string, List<string>> childrenByParent = parentByChild.GroupBy((KeyValuePair<string, string> relation) => relation.Value, StringComparer.OrdinalIgnoreCase).ToDictionary((IGrouping<string, KeyValuePair<string, string>> group) => group.Key, (IGrouping<string, KeyValuePair<string, string>> group) => group.Select((KeyValuePair<string, string> relation) => relation.Key).ToList(), StringComparer.OrdinalIgnoreCase);
		List<LiveDescendantInfo> result = new List<LiveDescendantInfo>();
		foreach (string rootThreadId in roots)
		{
			HashSet<string> descendants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Queue<string> pending = new Queue<string>();
			pending.Enqueue(rootThreadId);
			while (pending.Count > 0)
			{
				string parentId = pending.Dequeue();
				if (!childrenByParent.TryGetValue(parentId, out List<string> children))
				{
					continue;
				}
				foreach (string childId in children)
				{
					if (descendants.Add(childId))
					{
						pending.Enqueue(childId);
					}
				}
			}
			foreach (string descendantId in descendants)
			{
				if (!byId.TryGetValue(descendantId, out DbThread thread))
				{
					continue;
				}
				string rolloutPath = RolloutStorage.ResolveExistingPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath));
				if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
				{
					continue;
				}
				headerByThread.TryGetValue(descendantId, out ThreadHeaderInfo header);
				bool guardian = (thread.Source ?? string.Empty).IndexOf("guardian", StringComparison.OrdinalIgnoreCase) >= 0 || (header?.Source ?? string.Empty).IndexOf("guardian", StringComparison.OrdinalIgnoreCase) >= 0;
				result.Add(new LiveDescendantInfo
				{
					RootThreadId = rootThreadId,
					ThreadId = descendantId,
					Title = guardian ? (UiLanguage.IsEnglish ? "Approval guardian" : "内部审批守卫（guardian）") : TextHelpers.CleanLine(thread.Title, 160, UiLanguage.T("子代理对话")),
					Cwd = TextHelpers.StripExtendedPrefix(thread.Cwd),
					RolloutPath = rolloutPath
				});
			}
		}
		return result.GroupBy((LiveDescendantInfo item) => item.RootThreadId + "|" + item.ThreadId, StringComparer.OrdinalIgnoreCase).Select((IGrouping<string, LiveDescendantInfo> group) => group.First()).OrderBy((LiveDescendantInfo item) => item.Cwd).ThenBy((LiveDescendantInfo item) => item.Title).ToList();
	}

	private static void EnsureNoLiveDescendants(string codexHome, string rootThreadId)
	{
		List<LiveDescendantInfo> liveDescendants = FindLiveDescendants(codexHome, new string[1] { rootThreadId });
		if (liveDescendants.Count > 0)
		{
			string details = string.Join("\n\n", liveDescendants.Take(8).Select((LiveDescendantInfo item) => "• " + item.Title + "\n  Thread ID: " + item.ThreadId + "\n  " + (UiLanguage.IsEnglish ? "Parent: " : "父对话：") + item.RootThreadId + "\n  " + (UiLanguage.IsEnglish ? "Project: " : "项目：") + item.Cwd + "\n  " + (UiLanguage.IsEnglish ? "File: " : "文件：") + item.RolloutPath));
			string message = UiLanguage.IsEnglish
				? "This stale parent conversation still has " + liveDescendants.Count + " descendant conversations with local files. Repair stopped before Codex could cascade-delete them. Move these descendants to app trash or back them up first.\n\n" + details
				: "该失效父对话仍关联 " + liveDescendants.Count + " 个存在本地文件的子代理。为防止 Codex 官方删除级联清除这些记录，本次侧边栏修复已停止。请先将这些子代理移入软件回收站或另行备份。\n\n" + details;
			throw new LiveDescendantRepairException(message, liveDescendants);
		}
	}

	private static ThreadHeaderInfo ReadThreadHeader(string path)
	{
		using Stream stream = RolloutStorage.OpenRead(path);
		using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 8192);
		string firstLine = reader.ReadLine();
		Dictionary<string, object> root = string.IsNullOrWhiteSpace(firstLine) ? null : JsonSerialization.NewSerializer().DeserializeObject(firstLine) as Dictionary<string, object>;
		Dictionary<string, object> payload = root != null && root.TryGetValue("payload", out object payloadValue) ? payloadValue as Dictionary<string, object> : null;
		if (payload == null)
		{
			return new ThreadHeaderInfo();
		}
		string id = HeaderValue(payload, "id");
		string sessionId = HeaderValue(payload, "session_id");
		string parentId = HeaderValue(payload, "parent_thread_id");
		object sourceValue = payload.TryGetValue("source", out object rawSource) ? rawSource : null;
		if (string.IsNullOrWhiteSpace(parentId))
		{
			parentId = FindNestedHeaderValue(sourceValue, "parent_thread_id", 0);
		}
		string threadSource = HeaderValue(payload, "thread_source");
		if (string.IsNullOrWhiteSpace(parentId) && string.Equals(threadSource, "subagent", StringComparison.OrdinalIgnoreCase) && !string.Equals(sessionId, id, StringComparison.OrdinalIgnoreCase))
		{
			parentId = sessionId;
		}
		return new ThreadHeaderInfo
		{
			ParentThreadId = parentId,
			Source = sourceValue == null ? string.Empty : JsonSerialization.NewSerializer().Serialize(sourceValue)
		};
	}

	private static string HeaderValue(Dictionary<string, object> source, string key)
	{
		return source != null && source.TryGetValue(key, out object value) && value != null ? Convert.ToString(value) ?? string.Empty : string.Empty;
	}

	private static string FindNestedHeaderValue(object value, string key, int depth)
	{
		if (value == null || depth > 12)
		{
			return string.Empty;
		}
		if (value is Dictionary<string, object> dictionary)
		{
			if (dictionary.TryGetValue(key, out object direct) && direct != null)
			{
				return Convert.ToString(direct) ?? string.Empty;
			}
			foreach (object nested in dictionary.Values)
			{
				string found = FindNestedHeaderValue(nested, key, depth + 1);
				if (!string.IsNullOrWhiteSpace(found))
				{
					return found;
				}
			}
		}
		else if (value is object[] array)
		{
			foreach (object nested in array)
			{
				string found = FindNestedHeaderValue(nested, key, depth + 1);
				if (!string.IsNullOrWhiteSpace(found))
				{
					return found;
				}
			}
		}
		return string.Empty;
	}

	private sealed class ThreadHeaderInfo
	{
		public string ParentThreadId { get; set; }

		public string Source { get; set; }
	}

	private static string ValidateMissingSessionPath(string codexHome, DbThread thread)
	{
		if (thread == null || string.IsNullOrWhiteSpace(thread.Id) || string.IsNullOrWhiteSpace(thread.RolloutPath))
		{
			throw new InvalidDataException("失效会话缺少 Thread ID 或原始路径。");
		}
		if (!Guid.TryParse(thread.Id, out _))
		{
			throw new InvalidDataException("失效会话的 Thread ID 无效：" + thread.Id);
		}
		string path = Path.GetFullPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath));
		string sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions"));
		string archivedRoot = Path.GetFullPath(Path.Combine(codexHome, "archived_sessions"));
		if (!TextHelpers.IsWithin(path, sessionsRoot) && !TextHelpers.IsWithin(path, archivedRoot))
		{
			throw new InvalidDataException("失效会话路径不在 Codex 会话目录内：\n" + path);
		}
		if (File.Exists(path))
		{
			throw new IOException("失效会话路径已被其他文件占用，未执行修复：\n" + path);
		}
		return path;
	}

	private static bool IsMissingSessionPath(string codexHome, string rolloutPath)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(rolloutPath))
			{
				return false;
			}
			string path = Path.GetFullPath(TextHelpers.StripExtendedPrefix(rolloutPath));
			string sessionsRoot = Path.GetFullPath(Path.Combine(codexHome, "sessions"));
			string archivedRoot = Path.GetFullPath(Path.Combine(codexHome, "archived_sessions"));
			return (TextHelpers.IsWithin(path, sessionsRoot) || TextHelpers.IsWithin(path, archivedRoot)) && !RolloutStorage.ExistsInEitherEncoding(path);
		}
		catch
		{
			return false;
		}
	}

	private static string FindLatestIndexBackup(string codexHome)
	{
		string directory = Path.Combine(Path.GetFullPath(codexHome), "conversation-migrator-index-backups");
		if (!Directory.Exists(directory))
		{
			return string.Empty;
		}
		foreach (string path in Directory.EnumerateFiles(directory, "*.sqlite", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc))
		{
			try
			{
				WinSqliteReader.ReadThreads(path);
				return path;
			}
			catch
			{
			}
		}
		return string.Empty;
	}

	private static HashSet<string> ReadRepairLedger(string codexHome)
	{
		HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string path = RepairLedgerPath(codexHome);
		if (!RolloutStorage.ExistsInEitherEncoding(path))
		{
			return result;
		}
		try
		{
			Dictionary<string, object> root = JsonSerialization.NewSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
			if (root != null && root.TryGetValue("thread_ids", out object value) && value is object[] ids)
			{
				foreach (object id in ids)
				{
					string text = Convert.ToString(id);
					if (!string.IsNullOrWhiteSpace(text))
					{
						result.Add(text);
					}
				}
			}
		}
		catch
		{
		}
		return result;
	}

	private static void WriteRepairLedger(string codexHome, IEnumerable<string> threadIds)
	{
		HashSet<string> ids = ReadRepairLedger(codexHome);
		ids.UnionWith((threadIds ?? Enumerable.Empty<string>()).Where((string id) => !string.IsNullOrWhiteSpace(id)));
		string path = RepairLedgerPath(codexHome);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			Dictionary<string, object> root = new Dictionary<string, object>
			{
				{ "schema", 1 },
				{ "updated_at", DateTimeOffset.Now.ToString("o") },
				{ "thread_ids", ids.OrderBy((string id) => id, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray() }
			};
			File.WriteAllText(temporary, JsonSerialization.NewSerializer().Serialize(root), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			if (File.Exists(path))
			{
				File.Replace(temporary, path, null, ignoreMetadataErrors: true);
			}
			else
			{
				File.Move(temporary, path);
			}
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static string RepairLedgerPath(string codexHome)
	{
		return Path.Combine(Path.GetFullPath(codexHome), "conversation-migrator-index-backups", RepairLedgerName);
	}
}
