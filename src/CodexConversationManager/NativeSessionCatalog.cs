using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexConversationManager;

/// <summary>
/// Builds the raw list consumed by CodexCatalog.Build directly from a Codex home.
/// It performs no external process calls.
/// </summary>
internal static class NativeSessionCatalog
{
	private const int PreviewLineLimit = 240;
	private const int PreviewCharacterLimit = 2000000;
	private static readonly Regex ThreadIdPattern = new Regex(
		"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static List<SessionInfo> ScanDefault()
	{
		return Scan(CodexCatalog.ResolveCodexHome());
	}

	public static List<SessionInfo> Scan(string codexHome)
	{
		if (string.IsNullOrWhiteSpace(codexHome))
		{
			throw new ArgumentException("Codex Home 不能为空。", "codexHome");
		}
		string home = Path.GetFullPath(Environment.ExpandEnvironmentVariables(codexHome));
		if (!Directory.Exists(home))
		{
			return new List<SessionInfo>();
		}

		Dictionary<string, DbThread> indexByPath;
		Dictionary<string, DbThread> indexById;
		ReadIndex(home, out indexByPath, out indexById);
		List<SessionInfo> result = new List<SessionInfo>();
		AddFolder(home, "sessions", false, indexByPath, indexById, result);
		AddFolder(home, "archived_sessions", true, indexByPath, indexById, result);
		return result.OrderByDescending(item => item.UpdatedDate)
			.ThenBy(item => item.SessionPath, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static void AddFolder(string home, string name, bool archived,
		IDictionary<string, DbThread> indexByPath, IDictionary<string, DbThread> indexById,
		ICollection<SessionInfo> output)
	{
		string root = Path.GetFullPath(Path.Combine(home, name));
		if (!Directory.Exists(root))
		{
			return;
		}
		foreach (string path in EnumerateSessionFiles(root))
		{
			try
			{
				output.Add(ReadSession(path, root, archived, indexByPath, indexById));
			}
			catch
			{
				// Codex may replace a rollout while it is being scanned. Keep a minimal
				// entry instead of failing the complete refresh.
				if (IsRegularFile(path))
				{
					output.Add(CreateFallback(path, root, archived, null));
				}
			}
		}
	}

	private static SessionInfo ReadSession(string path, string root, bool archived,
		IDictionary<string, DbThread> indexByPath, IDictionary<string, DbThread> indexById)
	{
		string fullPath = Path.GetFullPath(path);
		if (!IsRegularFile(fullPath))
		{
			throw new InvalidDataException("会话文件是重解析点或已不可访问。");
		}
		DbThread indexedByPath = null;
		indexByPath.TryGetValue(NormalizePath(fullPath), out indexedByPath);
		JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
		Dictionary<string, object> payload;
		string preview;
		using (Stream stream = RolloutStorage.OpenRead(fullPath))
		using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
		{
			string firstLine = reader.ReadLine();
			payload = ReadPayload(firstLine, serializer);
			preview = FindFirstUserMessage(firstLine, reader, serializer);
		}

		string threadId = ResolveThreadId(payload, IdFromFileName(fullPath));
		DbThread indexed = ResolveIndexedThread(indexedByPath, threadId, indexById);
		SessionInfo session = CreateFallback(fullPath, root, archived, indexed, threadId);
		session.Preview = First(preview, session.Preview);
		if (payload == null)
		{
			return session;
		}

		string source = ReadSource(payload, serializer);
		string threadSource = Value(payload, "thread_source");
		string parentId = Value(payload, "parent_thread_id");
		if (string.IsNullOrWhiteSpace(parentId) && payload.TryGetValue("source", out object sourceValue))
		{
			parentId = FindNested(sourceValue, "parent_thread_id");
		}
		object rawSource = null;
		payload.TryGetValue("source", out rawSource);
		bool isSubagent = IsSubagentSource(threadSource, rawSource, source);
		string metadataSessionId = Value(payload, "session_id");
		if (isSubagent && string.IsNullOrWhiteSpace(parentId) &&
			!string.IsNullOrWhiteSpace(metadataSessionId) &&
			!string.Equals(metadataSessionId, threadId, StringComparison.OrdinalIgnoreCase))
		{
			parentId = metadataSessionId;
		}

		session.ThreadId = First(threadId, session.ThreadId);
		session.OriginThreadId = ConversationLineage.ResolveOriginThreadId(payload, session.ThreadId);
		session.Cwd = TextHelpers.StripExtendedPrefix(First(Value(payload, "cwd"), session.Cwd));
		session.Source = First(source, session.Source);
		session.ModelProvider = Value(payload, "model_provider");
		session.CliVersion = Value(payload, "cli_version");
		session.CreatedAt = First(Value(payload, "timestamp"), session.CreatedAt);
		session.IsSubagent = isSubagent || session.IsSubagent;
		session.ParentThreadId = First(parentId, session.ParentThreadId);
		session.MetadataVerified = true;
		return session;
	}

	private static SessionInfo CreateFallback(string path, string root, bool archived,
		DbThread indexed, string id = null, bool compressed = false)
	{
		string fullPath = Path.GetFullPath(path);
		FileInfo info = new FileInfo(fullPath);
		DateTime indexedTime = indexed == null ? DateTime.MinValue :
			TextHelpers.FromUnixMilliseconds(indexed.UpdatedAtMilliseconds);
		DateTime updated = indexedTime == DateTime.MinValue ? SafeLastWrite(info) : indexedTime;
		DateTime created = SafeCreation(info);
		string threadId = First(id, indexed == null ? null : indexed.Id,
			IdFromFileName(fullPath), FileStem(fullPath));
		return new SessionInfo
		{
			ThreadId = threadId,
			OriginThreadId = threadId,
			SessionPath = TextHelpers.StripExtendedPrefix(fullPath),
			RelativePath = RelativePath(root, fullPath),
			Cwd = indexed == null ? string.Empty : TextHelpers.StripExtendedPrefix(indexed.Cwd),
			Title = indexed == null ? string.Empty : indexed.Title,
			Preview = string.Empty,
			Source = indexed == null ? string.Empty : indexed.Source,
			CreatedAt = created == DateTime.MinValue ? string.Empty :
				created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
			UpdatedAt = updated == DateTime.MinValue ? string.Empty :
				updated.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
			UpdatedDate = updated,
			Archived = archived,
			Compressed = compressed || fullPath.EndsWith(".zst", StringComparison.OrdinalIgnoreCase),
			IsSubagent = indexed != null && indexed.IsSubagent,
			ParentThreadId = indexed == null ? string.Empty : indexed.ParentThreadId,
			SizeBytes = SafeLength(info),
			MetadataVerified = false
		};
	}

	private static IEnumerable<string> EnumerateSessionFiles(string root)
	{
		Stack<string> pending = new Stack<string>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			string[] files;
			try { files = Directory.GetFiles(directory); }
			catch { files = new string[0]; }
			foreach (string file in files)
			{
				if ((file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
					file.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)) &&
					IsRegularFile(file))
				{
					yield return file;
				}
			}
			string[] children;
			try { children = Directory.GetDirectories(directory); }
			catch { children = new string[0]; }
			foreach (string child in children)
			{
				try
				{
					if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
					{
						pending.Push(child);
					}
				}
				catch { }
			}
		}
	}

	private static Dictionary<string, object> ReadPayload(string line, JavaScriptSerializer serializer)
	{
		if (string.IsNullOrWhiteSpace(line)) return null;
		try
		{
			Dictionary<string, object> record = serializer.DeserializeObject(line) as Dictionary<string, object>;
			if (record == null || !string.Equals(Value(record, "type"), "session_meta", StringComparison.OrdinalIgnoreCase))
				return null;
			return record.TryGetValue("payload", out object value) ? value as Dictionary<string, object> : null;
		}
		catch { return null; }
	}

	private static string FindFirstUserMessage(string firstLine, StreamReader reader,
		JavaScriptSerializer serializer)
	{
		int lines = 0;
		int characters = 0;
		string line = firstLine;
		while (line != null && lines < PreviewLineLimit && characters < PreviewCharacterLimit)
		{
			lines++;
			characters += line.Length;
			string candidate = ReadUserMessage(line, serializer);
			if (!string.IsNullOrWhiteSpace(candidate))
				return TextHelpers.CleanLine(candidate, 320, string.Empty);
			line = reader.ReadLine();
		}
		return string.Empty;
	}

	private static string ReadUserMessage(string line, JavaScriptSerializer serializer)
	{
		if (string.IsNullOrWhiteSpace(line)) return string.Empty;
		try
		{
			Dictionary<string, object> record = serializer.DeserializeObject(line) as Dictionary<string, object>;
			if (record == null || !record.TryGetValue("payload", out object raw) ||
				!(raw is Dictionary<string, object> payload)) return string.Empty;
			string type = Value(payload, "type");
			if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) &&
				string.Equals(Value(payload, "role"), "user", StringComparison.OrdinalIgnoreCase) &&
				payload.TryGetValue("content", out object contentValue) && contentValue is object[] content)
			{
				List<string> parts = new List<string>();
				foreach (Dictionary<string, object> part in content.OfType<Dictionary<string, object>>())
				{
					string partType = Value(part, "type");
					if (string.Equals(partType, "input_text", StringComparison.OrdinalIgnoreCase) ||
						string.Equals(partType, "text", StringComparison.OrdinalIgnoreCase))
						parts.Add(Value(part, "text"));
				}
				return StripContext(string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()));
			}
			if (string.Equals(type, "user_message", StringComparison.OrdinalIgnoreCase))
				return StripContext(Value(payload, "message"));
		}
		catch { }
		return string.Empty;
	}

	private static string StripContext(string value)
	{
		string text = value ?? string.Empty;
		text = Regex.Replace(text,
			"<(recommended_plugins|environment_context|in-app-browser-context)\\b[^>]*>.*?</\\1>",
			string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
		return Regex.Replace(text, "<image\\b[^>]*>", "［图片］", RegexOptions.IgnoreCase).Trim();
	}

	private static string ReadSource(IDictionary<string, object> payload, JavaScriptSerializer serializer)
	{
		if (payload == null || !payload.TryGetValue("source", out object value) || value == null)
			return Value(payload, "thread_source");
		return value as string ?? serializer.Serialize(value);
	}

	private static string ResolveThreadId(IDictionary<string, object> payload, string fileNameId)
	{
		// Older subagent records use session_id for the parent conversation. Prefer
		// the ID stored explicitly for this record, then its rollout file name, and
		// only use session_id when neither of those forms exists.
		return First(Value(payload, "id"), Value(payload, "thread_id"), fileNameId,
			Value(payload, "session_id"));
	}

	private static DbThread ResolveIndexedThread(DbThread indexedByPath, string threadId,
		IDictionary<string, DbThread> indexById)
	{
		if (string.IsNullOrWhiteSpace(threadId))
		{
			return indexedByPath;
		}
		if (indexedByPath != null &&
			string.Equals(indexedByPath.Id, threadId, StringComparison.OrdinalIgnoreCase))
		{
			return indexedByPath;
		}
		DbThread indexedById;
		return indexById.TryGetValue(threadId, out indexedById) ? indexedById : null;
	}

	private static bool IsSubagentSource(string threadSource, object rawSource, string source)
	{
		if (string.Equals(threadSource, "subagent", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (rawSource is string sourceText &&
			string.Equals(sourceText.Trim(), "subagent", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (rawSource is Dictionary<string, object> dictionary &&
			dictionary.Keys.Any(key => string.Equals(key, "subagent", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return (source ?? string.Empty).IndexOf("\"subagent\"",
			StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string FindNested(object value, string key)
	{
		if (value is Dictionary<string, object> dictionary)
		{
			if (dictionary.TryGetValue(key, out object direct) && direct != null)
				return Convert.ToString(direct, CultureInfo.InvariantCulture);
			foreach (object child in dictionary.Values)
			{
				string found = FindNested(child, key);
				if (!string.IsNullOrWhiteSpace(found)) return found;
			}
		}
		else if (value is object[] array)
		{
			foreach (object child in array)
			{
				string found = FindNested(child, key);
				if (!string.IsNullOrWhiteSpace(found)) return found;
			}
		}
		return string.Empty;
	}

	private static void ReadIndex(string home, out Dictionary<string, DbThread> byPath,
		out Dictionary<string, DbThread> byId)
	{
		byPath = new Dictionary<string, DbThread>(StringComparer.OrdinalIgnoreCase);
		byId = new Dictionary<string, DbThread>(StringComparer.OrdinalIgnoreCase);
		try
		{
			string database = WinSqliteMaintenance.FindActiveDatabase(home);
			if (string.IsNullOrWhiteSpace(database) || !File.Exists(database)) return;
			foreach (DbThread thread in WinSqliteReader.ReadThreads(database).Where(x => x != null))
			{
				string pathKey = NormalizeIndexPath(home, thread.RolloutPath);
				if (!string.IsNullOrWhiteSpace(pathKey)) byPath[pathKey] = thread;
				if (!string.IsNullOrWhiteSpace(thread.Id)) byId[thread.Id] = thread;
			}
		}
		catch { }
	}

	private static string NormalizePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return string.Empty;
		try
		{
			return Path.GetFullPath(TextHelpers.StripExtendedPrefix(path))
				.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
		}
		catch
		{
			return TextHelpers.StripExtendedPrefix(path).Replace('/', '\\')
				.Trim().TrimEnd('\\').ToLowerInvariant();
		}
	}

	private static string NormalizeIndexPath(string home, string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return string.Empty;
		string clean = TextHelpers.StripExtendedPrefix(path).Replace('/', '\\');
		try
		{
			if (!Path.IsPathRooted(clean))
			{
				clean = Path.Combine(home, clean);
			}
		}
		catch { }
		return NormalizePath(clean);
	}

	private static string RelativePath(string root, string path)
	{
		string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string relative = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			? path.Substring(prefix.Length) : Path.GetFileName(path);
		return relative.Replace(Path.DirectorySeparatorChar, '/');
	}

	private static string IdFromFileName(string path)
	{
		MatchCollection matches = ThreadIdPattern.Matches(Path.GetFileName(path) ?? string.Empty);
		return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Value;
	}

	private static string FileStem(string path)
	{
		string name = Path.GetFileName(path) ?? string.Empty;
		if (name.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase))
			return name.Substring(0, name.Length - ".jsonl.zst".Length);
		if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
			return name.Substring(0, name.Length - ".jsonl".Length);
		return Path.GetFileNameWithoutExtension(name);
	}

	private static string First(params string[] values)
	{
		return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
	}

	private static string Value(IDictionary<string, object> dictionary, string key)
	{
		if (dictionary == null || !dictionary.TryGetValue(key, out object value) || value == null)
			return string.Empty;
		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private static bool IsRegularFile(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
		}
		catch
		{
			return false;
		}
	}

	private static long SafeLength(FileInfo info)
	{
		try { return info.Exists ? info.Length : 0L; }
		catch { return 0L; }
	}

	private static DateTime SafeLastWrite(FileInfo info)
	{
		try { return info.Exists ? info.LastWriteTime : DateTime.MinValue; }
		catch { return DateTime.MinValue; }
	}

	private static DateTime SafeCreation(FileInfo info)
	{
		try { return info.Exists ? info.CreationTime : DateTime.MinValue; }
		catch { return DateTime.MinValue; }
	}
}
