using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexConversationManager;

internal static class TargetedThreadIndexer
{
	private sealed class BundleDescriptor
	{
		public string Id { get; set; }

		public string BundlePath { get; set; }

		public string FilePrefix { get; set; }

		public string Title { get; set; }

		public string Preview { get; set; }

		public string FirstUserMessage { get; set; }

		public string SourceBundlePath { get; set; }

		public string CreatedAt { get; set; }

		public string UpdatedAt { get; set; }
	}

	public static HashSet<string> SnapshotSessionFiles(string codexHome)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string[] array = new string[2] { "sessions", "archived_sessions" };
		foreach (string path in array)
		{
			string path2 = Path.Combine(codexHome, path);
			foreach (string item in EnumerateSessionFilesSafely(path2))
			{
				hashSet.Add(item);
			}
		}
		return hashSet;
	}

	private static IEnumerable<string> EnumerateSessionFilesSafely(string root)
	{
		string fullRoot;
		try
		{
			fullRoot = Path.GetFullPath(root);
			FileAttributes rootAttributes = File.GetAttributes(fullRoot);
			if ((rootAttributes & FileAttributes.Directory) == 0 || (rootAttributes & FileAttributes.ReparsePoint) != 0)
			{
				yield break;
			}
		}
		catch (IOException)
		{
			yield break;
		}
		catch (UnauthorizedAccessException)
		{
			yield break;
		}

		Stack<string> pending = new Stack<string>();
		pending.Push(fullRoot);
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			string[] files;
			try
			{
				files = Directory.GetFiles(directory, "*.jsonl*", SearchOption.TopDirectoryOnly)
					.Where(path => RolloutStorage.PlainPath(path).EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).ToArray();
			}
			catch (IOException)
			{
				files = Array.Empty<string>();
			}
			catch (UnauthorizedAccessException)
			{
				files = Array.Empty<string>();
			}

			foreach (string file in files)
			{
				string fullPath;
				try
				{
					FileAttributes attributes = File.GetAttributes(file);
					if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					fullPath = Path.GetFullPath(file);
				}
				catch (IOException)
				{
					continue;
				}
				catch (UnauthorizedAccessException)
				{
					continue;
				}
				yield return fullPath;
			}

			string[] directories;
			try
			{
				directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
			}
			catch (IOException)
			{
				directories = Array.Empty<string>();
			}
			catch (UnauthorizedAccessException)
			{
				directories = Array.Empty<string>();
			}

			foreach (string child in directories)
			{
				try
				{
					FileAttributes attributes = File.GetAttributes(child);
					if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
					{
						pending.Push(child);
					}
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
	}

	public static void ValidateBundles(IEnumerable<string> bundlePaths)
	{
		ReadBundleDescriptors(bundlePaths, null);
	}

	public static TargetedIndexResult IndexImportedSessions(string codexHome, IEnumerable<string> bundlePaths, ISet<string> filesBeforeImport, bool copiesOnly, string requiredCwd, IDictionary<string, string> titleHints)
	{
		return IndexImportedSessionsCore(codexHome, bundlePaths, filesBeforeImport, copiesOnly, requiredCwd, null, titleHints);
	}

	public static TargetedIndexResult IndexImportedSessionsMapped(string codexHome, IEnumerable<string> bundlePaths, ISet<string> filesBeforeImport, bool copiesOnly, IDictionary<string, string> requiredCwdByBundle, IDictionary<string, string> titleHints)
	{
		return IndexImportedSessionsCore(codexHome, bundlePaths, filesBeforeImport, copiesOnly, null, requiredCwdByBundle, titleHints);
	}

	public static ThreadIndexMetadata ReadMetadataForIndexing(string path, string title, string preview)
	{
		return ReadMetadata(path, new BundleDescriptor
		{
			Id = string.Empty,
			Title = title,
			Preview = preview,
			FirstUserMessage = title
		});
	}

    // Identity checks must not require a standalone importable history. Native
    // paginated rollouts can be fragments whose first ordinal is non-zero.
    internal static ThreadIndexMetadata ReadIdentityMetadata(string path)
    {
        return ReadMetadata(path, new BundleDescriptor(), identityOnly: true);
    }

	public static TargetedIndexResult IndexSessionFile(string codexHome, string path, string title, string preview)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			throw new FileNotFoundException("找不到需要重新登记的会话文件。", path);
		}
		return RegisterMetadata(codexHome, new ThreadIndexMetadata[1]
		{
			ReadMetadataForIndexing(path, title, preview)
		});
	}

	public static TargetedIndexResult IndexMetadata(string codexHome, ThreadIndexMetadata metadata)
	{
		if (metadata == null || string.IsNullOrWhiteSpace(metadata.Id))
		{
			throw new InvalidDataException("需要重新登记的会话元数据无效。");
		}
		return RegisterMetadata(codexHome, new ThreadIndexMetadata[1] { metadata });
	}

	private static TargetedIndexResult IndexImportedSessionsCore(string codexHome, IEnumerable<string> bundlePaths, ISet<string> filesBeforeImport, bool copiesOnly, string requiredCwd, IDictionary<string, string> requiredCwdByBundle, IDictionary<string, string> titleHints)
	{
		List<BundleDescriptor> list = ReadBundleDescriptors(bundlePaths, titleHints);
		HashSet<string> hashSet = SnapshotSessionFiles(codexHome);
		Dictionary<string, BundleDescriptor> dictionary = new Dictionary<string, BundleDescriptor>(StringComparer.OrdinalIgnoreCase);
		if (copiesOnly)
		{
			foreach (BundleDescriptor item in list)
			{
				string text = ResolveExpectedPath(codexHome, item.BundlePath);
				if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
				{
					dictionary[Path.GetFullPath(text)] = item;
				}
			}
			foreach (string item2 in hashSet)
			{
				if (filesBeforeImport == null || !filesBeforeImport.Contains(item2))
				{
					string name = Path.GetFileName(item2);
					List<BundleDescriptor> candidates = list.Where((BundleDescriptor item) => name.StartsWith(item.FilePrefix, StringComparison.OrdinalIgnoreCase)).ToList();
					BundleDescriptor bundleDescriptor = MatchCopiedDescriptor(item2, candidates);
					if (bundleDescriptor != null)
					{
						dictionary[item2] = bundleDescriptor;
					}
				}
			}
		}
		else
		{
			foreach (BundleDescriptor descriptor in list)
			{
				string text2 = ResolveExpectedPath(codexHome, descriptor.BundlePath);
				if (!string.IsNullOrWhiteSpace(text2) && File.Exists(text2))
				{
					dictionary[Path.GetFullPath(text2)] = descriptor;
					continue;
				}
				string text3 = hashSet.FirstOrDefault((string path) => Path.GetFileName(path).IndexOf(descriptor.Id, StringComparison.OrdinalIgnoreCase) >= 0);
				if (string.IsNullOrWhiteSpace(text3))
				{
					throw new FileNotFoundException("原生导入完成，但没有找到导入后的会话文件：" + descriptor.Id);
				}
				dictionary[text3] = descriptor;
			}
		}
		List<ThreadIndexMetadata> list2 = new List<ThreadIndexMetadata>();
		foreach (KeyValuePair<string, BundleDescriptor> item3 in dictionary)
		{
			ThreadIndexMetadata threadIndexMetadata = ReadMetadata(item3.Key, item3.Value);
			if (!copiesOnly && !string.Equals(threadIndexMetadata.Id, item3.Value.Id, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("导入文件的任务 ID 与迁移包不一致：" + item3.Key);
			}
			string expectedCwd = requiredCwd;
			if (requiredCwdByBundle != null && !string.IsNullOrWhiteSpace(item3.Value.SourceBundlePath))
			{
				requiredCwdByBundle.TryGetValue(Path.GetFullPath(item3.Value.SourceBundlePath), out expectedCwd);
			}
			if (!string.IsNullOrWhiteSpace(expectedCwd) && !string.Equals(TextHelpers.CanonicalPath(threadIndexMetadata.Cwd), TextHelpers.CanonicalPath(expectedCwd), StringComparison.OrdinalIgnoreCase))
			{
				if (!copiesOnly || filesBeforeImport == null || !filesBeforeImport.Contains(Path.GetFullPath(item3.Key)))
				{
					throw new InvalidDataException("导入文件仍登记在错误项目路径，已拒绝写入索引：" + threadIndexMetadata.Id + "\n实际：" + threadIndexMetadata.Cwd + "\n目标：" + expectedCwd);
				}
			}
			else
			{
				list2.Add(threadIndexMetadata);
			}
		}
		if (list2.Count == 0)
		{
			TargetedIndexResult targetedIndexResult = new TargetedIndexResult();
			targetedIndexResult.DatabasePath = string.Empty;
			targetedIndexResult.BackupPath = string.Empty;
			targetedIndexResult.BackfillState = "unchanged";
			targetedIndexResult.IndexedCount = 0;
			return targetedIndexResult;
		}
		return RegisterMetadata(codexHome, list2);
	}

	private static TargetedIndexResult RegisterMetadata(string codexHome, IEnumerable<ThreadIndexMetadata> metadata)
	{
		List<ThreadIndexMetadata> list = (metadata ?? Enumerable.Empty<ThreadIndexMetadata>()).ToList();
		TargetedIndexResult result = WinSqliteMaintenance.UpsertImportedThreads(codexHome, list);
		result.PaginatedCount = list.Count(item => string.Equals(item.HistoryMode, CodexHistoryMode.Paginated, StringComparison.Ordinal));
		DesktopProjectRegistrationResult desktop;
		try
		{
			desktop = CodexDesktopProjectRegistry.RegisterImportedThreads(codexHome, list);
		}
		catch (Exception ex)
		{
			try
			{
				WinSqliteMaintenance.RestoreConsistentBackup(result.DatabasePath, result.BackupPath);
			}
			catch (Exception restoreError)
			{
				throw new AggregateException("桌面项目归属登记失败，而且 Codex 索引无法自动恢复。请保留索引备份并停止重试。", ex, restoreError);
			}
			throw new InvalidOperationException("桌面项目归属登记失败，Codex 索引已恢复到导入前状态。", ex);
		}
		result.DesktopStateFound = desktop.StateFileFound;
		result.DesktopAssignmentExpectedCount = desktop.ExpectedThreadCount;
		result.DesktopAssignmentVerifiedCount = desktop.VerifiedThreadCount;
		result.DesktopProjectCount = desktop.RegisteredProjectCount;
		result.DesktopStateBackupPath = desktop.BackupPath;
		return result;
	}

	private static BundleDescriptor MatchCopiedDescriptor(string path, IList<BundleDescriptor> candidates)
	{
		if (candidates == null || candidates.Count == 0)
		{
			return null;
		}
		if (candidates.Count == 1)
		{
			return candidates[0];
		}
		string firstUser = FindFirstUserMessage(path, JsonSerialization.NewSerializer());
		if (!string.IsNullOrWhiteSpace(firstUser))
		{
			List<BundleDescriptor> list = candidates.Where((BundleDescriptor item) => string.Equals(StripContext(item.FirstUserMessage), firstUser, StringComparison.Ordinal)).ToList();
			if (list.Count == 1)
			{
				return list[0];
			}
		}
		return candidates[0];
	}

	public static ThreadIndexMetadata ReadMetadataForTest(string path, string title, string preview)
	{
		return ReadMetadata(path, new BundleDescriptor
		{
			Id = string.Empty,
			Title = title,
			Preview = preview,
			FirstUserMessage = title
		});
	}

	private static List<BundleDescriptor> ReadBundleDescriptors(IEnumerable<string> bundlePaths, IDictionary<string, string> titleHints)
	{
		List<BundleDescriptor> list = new List<BundleDescriptor>();
		foreach (string item in bundlePaths ?? Enumerable.Empty<string>())
		{
			using ZipArchive zipArchive = ZipFile.OpenRead(item);
			ZipArchiveEntry entry = zipArchive.GetEntry("manifest.json");
			if (entry == null)
			{
				throw new InvalidDataException("会话包缺少 manifest.json。");
			}
			BundleManifest bundleManifest;
			using (StreamReader streamReader = new StreamReader(entry.Open(), Encoding.UTF8))
			{
				bundleManifest = JsonSerialization.NewSerializer().Deserialize<BundleManifest>(streamReader.ReadToEnd());
			}
			foreach (BundleSession item2 in bundleManifest.sessions ?? new List<BundleSession>())
			{
				if (item2 != null && !string.IsNullOrWhiteSpace(item2.thread_id))
				{
					if (titleHints == null || !titleHints.TryGetValue(item2.thread_id, out var value))
					{
						value = item2.first_user_message;
					}
					string fileName = Path.GetFileName((item2.bundle_path ?? string.Empty).Replace('/', '\\'));
					int num = fileName.IndexOf(item2.thread_id, StringComparison.OrdinalIgnoreCase);
					list.Add(new BundleDescriptor
					{
						Id = item2.thread_id,
						BundlePath = RolloutStorage.PlainPath(item2.bundle_path),
						FilePrefix = ((num < 0) ? Path.GetFileNameWithoutExtension(fileName) : fileName.Substring(0, num)),
						Title = value,
						Preview = item2.preview,
						FirstUserMessage = item2.first_user_message,
						CreatedAt = item2.created_at,
						UpdatedAt = item2.updated_at,
						SourceBundlePath = Path.GetFullPath(item)
					});
				}
			}
		}
		return (from @group in list.GroupBy((BundleDescriptor item) => item.Id, StringComparer.OrdinalIgnoreCase)
			select @group.First()).ToList();
	}

	private static string ResolveExpectedPath(string codexHome, string bundlePath)
	{
		string text = (bundlePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
		string path = "sessions";
		if (text.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase))
		{
			text = text.Substring("sessions/".Length);
		}
		else if (text.StartsWith("archived_sessions/", StringComparison.OrdinalIgnoreCase))
		{
			path = "archived_sessions";
			text = text.Substring("archived_sessions/".Length);
		}
		string path2 = text.Replace('/', Path.DirectorySeparatorChar);
		string text2 = Path.GetFullPath(Path.Combine(codexHome, path)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(Path.Combine(text2, path2));
		if (!fullPath.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("会话包包含不安全的索引路径。");
		}
		return fullPath;
	}

	private static ThreadIndexMetadata ReadMetadata(string path, BundleDescriptor descriptor, bool identityOnly = false)
	{
		string text;
		using (Stream stream = RolloutStorage.OpenRead(path))
		{
			using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 65536);
			text = streamReader.ReadLine();
		}
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidDataException("会话文件缺少 session_meta：" + path);
		}
		JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
		object value;
		Dictionary<string, object> dictionary = ((javaScriptSerializer.DeserializeObject(text) is Dictionary<string, object> dictionary2 && string.Equals(Value(dictionary2, "type"), "session_meta", StringComparison.Ordinal) && dictionary2.TryGetValue("payload", out value)) ? (value as Dictionary<string, object>) : null);
		if (dictionary == null)
		{
			throw new InvalidDataException("会话首行缺少 payload：" + path);
		}
		string text2 = Value(dictionary, "id");
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = Value(dictionary, "session_id");
		}
		if (!Guid.TryParse(text2, out var _))
		{
			throw new InvalidDataException("会话任务 ID 无效：" + path);
		}
		object value2;
		string text3 = ((dictionary.TryGetValue("source", out value2) && value2 != null) ? (value2 as string) : null);
		if (value2 != null && text3 == null)
		{
			text3 = javaScriptSerializer.Serialize(value2);
		}
		if (string.IsNullOrWhiteSpace(text3))
		{
			text3 = "unknown";
		}
		string text4 = Value(dictionary, "thread_source");
		if (string.IsNullOrWhiteSpace(text4) && text3.IndexOf("\"subagent\"", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			text4 = "subagent";
		}
		string text5 = identityOnly ? string.Empty : FindFirstUserMessage(path, javaScriptSerializer);
		string text6 = ((!string.IsNullOrWhiteSpace(text5)) ? text5 : descriptor.FirstUserMessage);
		string value3 = ((!string.IsNullOrWhiteSpace(descriptor.Preview)) ? descriptor.Preview : text6);
		string value4 = ((!string.IsNullOrWhiteSpace(descriptor.Title)) ? descriptor.Title : text6);
		value4 = TextHelpers.CleanLine(value4, 1200, "导入的 Codex 对话");
		value3 = TextHelpers.CleanLine(value3, 1200, value4);
		text6 = NormalizeMessage(text6);
		DateTime dateTime = ParseUtc(Value(dictionary, "timestamp"), ParseUtc(descriptor.CreatedAt, File.GetCreationTimeUtc(path)));
		DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
		DateTime dateTime2 = ParseUtc(descriptor.UpdatedAt, lastWriteTimeUtc);
		DateTime dateTime3 = ((lastWriteTimeUtc.Year >= 2000) ? lastWriteTimeUtc : dateTime2);
		if (dateTime3 < dateTime)
		{
			dateTime3 = ((dateTime2 >= dateTime) ? dateTime2 : dateTime);
		}
		object value5;
		Dictionary<string, object> dictionary3 = (dictionary.TryGetValue("git", out value5) ? (value5 as Dictionary<string, object>) : null);
		string fullPath = Path.GetFullPath(path);
		ThreadIndexMetadata threadIndexMetadata = new ThreadIndexMetadata();
		threadIndexMetadata.Id = text2;
		threadIndexMetadata.RolloutPath = TextHelpers.ToCodexIndexPath(fullPath);
		threadIndexMetadata.CreatedAtSeconds = EpochMilliseconds(dateTime) / 1000;
		threadIndexMetadata.UpdatedAtSeconds = EpochMilliseconds(dateTime3) / 1000;
		threadIndexMetadata.CreatedAtMilliseconds = EpochMilliseconds(dateTime);
		threadIndexMetadata.UpdatedAtMilliseconds = EpochMilliseconds(dateTime3);
		threadIndexMetadata.Source = text3;
		threadIndexMetadata.HistoryMode = identityOnly ? Value(dictionary, "history_mode") : CodexHistoryMode.Normalize(Value(dictionary, "history_mode"), path);
		if (!identityOnly) CodexHistoryMode.ValidateSessionFile(path, threadIndexMetadata.HistoryMode);
		threadIndexMetadata.ThreadSource = (string.IsNullOrWhiteSpace(text4) ? null : text4);
		string parentThreadId = Value(dictionary, "parent_thread_id");
		if (string.IsNullOrWhiteSpace(parentThreadId))
		{
			parentThreadId = FindNestedString(value2, "parent_thread_id");
		}
		if (string.IsNullOrWhiteSpace(parentThreadId) && string.Equals(text4, "subagent", StringComparison.OrdinalIgnoreCase))
		{
			string legacyParentId = Value(dictionary, "session_id");
			if (!string.Equals(legacyParentId, text2, StringComparison.OrdinalIgnoreCase))
			{
				parentThreadId = legacyParentId;
			}
		}
		threadIndexMetadata.ParentThreadId = parentThreadId;
		threadIndexMetadata.ModelProvider = Default(Value(dictionary, "model_provider"), "openai");
		threadIndexMetadata.Cwd = TextHelpers.ToCodexIndexPath(Value(dictionary, "cwd"));
		threadIndexMetadata.CliVersion = Value(dictionary, "cli_version");
		threadIndexMetadata.Title = value4;
		threadIndexMetadata.Preview = value3;
		threadIndexMetadata.FirstUserMessage = text6;
		threadIndexMetadata.HasUserEvent = !string.IsNullOrWhiteSpace(text6);
		threadIndexMetadata.Archived = fullPath.IndexOf(Path.DirectorySeparatorChar + "archived_sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
		threadIndexMetadata.GitSha = ((dictionary3 == null) ? null : Value(dictionary3, "commit_hash"));
		threadIndexMetadata.GitBranch = ((dictionary3 == null) ? null : Value(dictionary3, "branch"));
		threadIndexMetadata.GitOriginUrl = ((dictionary3 == null) ? null : Value(dictionary3, "repository_url"));
		return threadIndexMetadata;
	}

	private static string FindFirstUserMessage(string path, JavaScriptSerializer serializer)
	{
		try
		{
			int num = 0;
			using Stream stream = RolloutStorage.OpenRead(path);
			using StreamReader streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 65536);
			string input;
			while ((input = streamReader.ReadLine()) != null && num++ < 2000)
			{
				Dictionary<string, object> dictionary;
				try
				{
					dictionary = serializer.DeserializeObject(input) as Dictionary<string, object>;
				}
				catch
				{
					continue;
				}
				object value;
				Dictionary<string, object> dictionary2 = ((dictionary != null && dictionary.TryGetValue("payload", out value)) ? (value as Dictionary<string, object>) : null);
				if (dictionary2 == null)
				{
					continue;
				}
				string a = Value(dictionary2, "role");
				string a2 = Value(dictionary2, "type");
				if (string.Equals(a, "user", StringComparison.OrdinalIgnoreCase) && string.Equals(a2, "message", StringComparison.OrdinalIgnoreCase))
				{
					object value2;
					object[] array = (dictionary2.TryGetValue("content", out value2) ? (value2 as object[]) : null);
					if (array == null)
					{
						continue;
					}
					object[] array2 = array;
					foreach (object obj2 in array2)
					{
						if (obj2 is Dictionary<string, object> dictionary3 && string.Equals(Value(dictionary3, "type"), "input_text", StringComparison.OrdinalIgnoreCase))
						{
							string text = StripContext(Value(dictionary3, "text"));
							if (!string.IsNullOrWhiteSpace(text))
							{
								return text;
							}
						}
					}
				}
				if (string.Equals(a2, "user_message", StringComparison.OrdinalIgnoreCase))
				{
					string text2 = StripContext(Value(dictionary2, "message"));
					if (!string.IsNullOrWhiteSpace(text2))
					{
						return text2;
					}
				}
			}
		}
		catch
		{
		}
		return string.Empty;
	}

	private static string StripContext(string value)
	{
		string input = NormalizeMessage(value);
		input = Regex.Replace(input, "^\\s*<environment_context>.*?</environment_context>\\s*", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
		return input.Trim();
	}

	private static string NormalizeMessage(string value)
	{
		string text = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
		if (text.Length > 32000)
		{
			text = text.Substring(0, 32000);
		}
		return Regex.Replace(text, "\\n{3,}", "\n\n");
	}

	private static string FindNestedString(object value, string key)
	{
		if (value is Dictionary<string, object> dictionary)
		{
			if (dictionary.TryGetValue(key, out var value2) && value2 != null)
			{
				return Convert.ToString(value2, CultureInfo.InvariantCulture);
			}
			foreach (object value4 in dictionary.Values)
			{
				string text = FindNestedString(value4, key);
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
		}
		if (value is object[] array)
		{
			object[] array2 = array;
			foreach (object value3 in array2)
			{
				string text2 = FindNestedString(value3, key);
				if (!string.IsNullOrWhiteSpace(text2))
				{
					return text2;
				}
			}
		}
		return null;
	}

	private static string Value(IDictionary<string, object> dictionary, string key)
	{
		if (dictionary == null || !dictionary.TryGetValue(key, out var value) || value == null)
		{
			return string.Empty;
		}
		return Convert.ToString(value, CultureInfo.InvariantCulture);
	}

	private static string Default(string value, string fallback)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		return fallback;
	}

	private static DateTime ParseUtc(string value, DateTime fallback)
	{
		if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
		{
			return fallback.ToUniversalTime();
		}
		return result.ToUniversalTime();
	}

	private static long EpochMilliseconds(DateTime value)
	{
		return (long)(value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
	}
}
