using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexConversationManager;

internal static class ExactBundleWriter
{
	private static readonly Regex ThreadIdPattern = new Regex(
		"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	public static void CreateSingleSessionBundle(SessionInfo session, string outputPath)
	{
		CreateBundle(new SessionInfo[1] { session }, outputPath, null);
	}

	public static void CreateBundle(IEnumerable<SessionInfo> sourceSessions, string outputPath, Action<int, int, SessionInfo> progress)
	{
		if (sourceSessions == null)
		{
			throw new ArgumentNullException("sourceSessions");
		}
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			throw new ArgumentException("备份输出路径不能为空。", "outputPath");
		}

		string fullOutputPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(outputPath));
		string outputDirectory = Path.GetDirectoryName(fullOutputPath);
		if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(Path.GetFileName(fullOutputPath)))
		{
			throw new ArgumentException("备份输出路径无效。", "outputPath");
		}

		List<SessionInfo> candidates = sourceSessions.Where(x => x != null).ToList();
		if (candidates.Count == 0)
		{
			throw new InvalidOperationException("没有可封装的会话。");
		}

		Dictionary<string, SessionInfo> uniqueById = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> sourceById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> idBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (SessionInfo candidate in candidates)
		{
			if (string.IsNullOrWhiteSpace(candidate.ThreadId))
			{
				throw new InvalidDataException("所选对话缺少 Thread ID，已取消备份。");
			}
			string sourcePath = ResolveSourcePath(candidate);
			ValidateSourceFormat(sourcePath);
			ValidateFileNameThreadId(sourcePath, candidate.ThreadId);
			string normalizedSource = NormalizePath(sourcePath);
			if (string.Equals(normalizedSource, NormalizePath(fullOutputPath), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("备份输出文件不能与 rollout 源文件相同：" + TextHelpers.StripExtendedPrefix(sourcePath));
			}
			if (sourceById.TryGetValue(candidate.ThreadId, out string existingSource))
			{
				if (!string.Equals(NormalizePath(existingSource), normalizedSource, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("检测到重复 Thread ID 指向不同 rollout 文件，已取消备份：" + candidate.ThreadId + Environment.NewLine + TextHelpers.StripExtendedPrefix(existingSource) + Environment.NewLine + TextHelpers.StripExtendedPrefix(sourcePath));
				}
				continue;
			}
			if (idBySource.TryGetValue(normalizedSource, out string existingId) &&
				!string.Equals(existingId, candidate.ThreadId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("同一个 rollout 文件不能对应多个 Thread ID，已取消备份：" + TextHelpers.StripExtendedPrefix(sourcePath));
			}
			uniqueById.Add(candidate.ThreadId, candidate);
			sourceById.Add(candidate.ThreadId, sourcePath);
			idBySource[normalizedSource] = candidate.ThreadId;
		}

		List<SessionInfo> sessions = uniqueById.Values.ToList();
		string codexHome = CodexCatalog.ResolveCodexHome();
		string snapshotDirectory = Path.Combine(Path.GetTempPath(), "codex-exact-bundle-" + Guid.NewGuid().ToString("N"));
		string temporaryOutputPath = null;
		Directory.CreateDirectory(snapshotDirectory);
		try
		{
			List<BundleSession> manifestSessions = new List<BundleSession>();
			Dictionary<string, string> bundleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int index = 0;
			foreach (SessionInfo session in sessions)
			{
				index++;
				progress?.Invoke(index, sessions.Count, session);
				string sourcePath = sourceById[session.ThreadId];
				string snapshotPath = Path.Combine(snapshotDirectory, index.ToString("D4", CultureInfo.InvariantCulture) + "-" + session.ThreadId + ".jsonl");
				if (RolloutStorage.IsCompressed(sourcePath))
				{
					string compressedSnapshot = snapshotPath + ".zst";
					CopyConsistentSnapshot(sourcePath, compressedSnapshot);
					RolloutStorage.DecodeFile(compressedSnapshot, snapshotPath);
				}
				else CopyConsistentSnapshot(sourcePath, snapshotPath);
				ValidateEmbeddedThreadId(snapshotPath, session.ThreadId, sourcePath);

				string relativePath = session.RelativePath;
				if (string.IsNullOrWhiteSpace(relativePath))
				{
					relativePath = DeriveRelativePath(sourcePath, codexHome);
				}
				relativePath = NormalizeBundleRelativePath(RolloutStorage.PlainPath(relativePath));
				string prefix = session.Archived ? "archived_sessions/" : "sessions/";
				string bundlePath = prefix + relativePath;
				if (bundleSources.ContainsKey(bundlePath))
				{
					bundlePath = prefix + DateTime.Now.ToString("yyyy/MM/dd/", CultureInfo.InvariantCulture) + session.ThreadId + "-" + Path.GetFileName(RolloutStorage.PlainPath(sourcePath));
				}
				if (bundleSources.ContainsKey(bundlePath))
				{
					throw new InvalidDataException("多个 rollout 文件生成了相同的包内路径，已取消备份：" + bundlePath);
				}

				FileInfo snapshotInfo = new FileInfo(snapshotPath);
				FileInfo sourceInfo = new FileInfo(sourcePath);
				string sha = Sha256File(snapshotPath);
				manifestSessions.Add(new BundleSession
				{
					thread_id = session.ThreadId,
					origin_thread_id = string.IsNullOrWhiteSpace(session.OriginThreadId) ? session.ThreadId : session.OriginThreadId,
					original_path = TextHelpers.StripExtendedPrefix(sourcePath),
					bundle_path = bundlePath,
					original_cwd = session.Cwd,
					preview = TextHelpers.CleanLine(session.Preview, 240, session.DisplayTitle),
					first_user_message = session.DisplayTitle,
					created_at = NormalizeUtc(session.CreatedAt, sourceInfo.CreationTimeUtc),
					updated_at = session.UpdatedDate == DateTime.MinValue
						? sourceInfo.LastWriteTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
						: session.UpdatedDate.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
					source = FriendlyManifestSource(session.Source),
					model_provider = string.IsNullOrWhiteSpace(session.ModelProvider) ? "openai" : session.ModelProvider,
					archived = session.Archived,
					compressed = false,
					size_bytes = snapshotInfo.Length,
					sha256 = sha
				});
				bundleSources.Add(bundlePath, snapshotPath);
			}

			BundleManifest manifest = new BundleManifest
			{
				format_version = "codex-sync-bundle-v1",
				created_at = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
				created_by_device = Environment.MachineName,
				source_os = "windows",
				source_codex_home = codexHome,
				codex_version = sessions.Select(x => x.CliVersion).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
				sessions = manifestSessions
			};
			string manifestPath = Path.Combine(snapshotDirectory, "manifest.json");
			File.WriteAllText(manifestPath, JsonSerialization.NewSerializer().Serialize(manifest), new UTF8Encoding(false));
			Dictionary<string, string> checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "manifest.json", Sha256File(manifestPath) }
			};
			foreach (BundleSession manifestSession in manifestSessions)
			{
				checksums.Add(manifestSession.bundle_path, manifestSession.sha256);
			}
			string checksumsPath = Path.Combine(snapshotDirectory, "checksums.json");
			File.WriteAllText(checksumsPath, JsonSerialization.NewSerializer().Serialize(checksums), new UTF8Encoding(false));

			Directory.CreateDirectory(outputDirectory);
			temporaryOutputPath = CreateTemporaryOutputPath(outputDirectory, Path.GetFileName(fullOutputPath), idBySource.Keys);
			using (ZipArchive destination = ZipFile.Open(temporaryOutputPath, ZipArchiveMode.Create))
			{
				foreach (KeyValuePair<string, string> entry in bundleSources)
				{
					destination.CreateEntryFromFile(entry.Value, entry.Key, CompressionLevel.Optimal);
				}
				destination.CreateEntryFromFile(manifestPath, "manifest.json", CompressionLevel.Optimal);
				destination.CreateEntryFromFile(checksumsPath, "checksums.json", CompressionLevel.Optimal);
			}
			if (File.Exists(fullOutputPath))
			{
				File.Replace(temporaryOutputPath, fullOutputPath, null);
			}
			else
			{
				File.Move(temporaryOutputPath, fullOutputPath);
			}
			temporaryOutputPath = null;
		}
		finally
		{
			TryDeleteFile(temporaryOutputPath);
			TryDeleteDirectory(snapshotDirectory);
		}
	}

	private static string ResolveSourcePath(SessionInfo session)
	{
		string stripped = TextHelpers.StripExtendedPrefix(session.SessionPath);
		if (!string.IsNullOrWhiteSpace(stripped) && File.Exists(stripped))
		{
			return Path.GetFullPath(stripped);
		}
		if (!string.IsNullOrWhiteSpace(session.SessionPath) && File.Exists(session.SessionPath))
		{
			return session.SessionPath;
		}
		throw new FileNotFoundException("找不到该对话对应的 rollout 文件。", stripped);
	}

	private static void ValidateSourceFormat(string sourcePath)
	{
		if (!RolloutStorage.PlainPath(sourcePath).EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("rollout must be .jsonl or .jsonl.zst: " + sourcePath);
	}

	private static void ValidateFileNameThreadId(string sourcePath, string expectedThreadId)
	{
		if (!Guid.TryParse(expectedThreadId, out Guid expected))
		{
			throw new InvalidDataException("SessionInfo.ThreadId 不是有效 GUID：" + expectedThreadId);
		}
		MatchCollection matches = ThreadIdPattern.Matches(Path.GetFileName(sourcePath) ?? string.Empty);
		if (matches.Count != 1 || !Guid.TryParse(matches[0].Value, out Guid fileId))
		{
			throw new InvalidDataException("普通 JSONL 的文件名必须包含且只包含一个 Thread GUID：" + TextHelpers.StripExtendedPrefix(sourcePath));
		}
		if (fileId != expected)
		{
			throw new InvalidDataException("rollout 文件名 GUID 与 SessionInfo.ThreadId 不一致，已取消备份：" + TextHelpers.StripExtendedPrefix(sourcePath));
		}
	}

	private static void ValidateEmbeddedThreadId(string snapshotPath, string expectedThreadId, string sourcePath)
	{
		string firstLine;
		using (FileStream stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
		using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
		{
			firstLine = reader.ReadLine();
		}
		if (string.IsNullOrWhiteSpace(firstLine))
		{
			throw new InvalidDataException("rollout 文件为空或缺少首行 session_meta：" + TextHelpers.StripExtendedPrefix(sourcePath));
		}
		Dictionary<string, object> root;
		try
		{
			root = JsonSerialization.NewSerializer().DeserializeObject(firstLine) as Dictionary<string, object>;
		}
		catch (Exception ex)
		{
			throw new InvalidDataException("rollout 首行不是有效 JSON：" + TextHelpers.StripExtendedPrefix(sourcePath), ex);
		}
		if (root == null || !string.Equals(ConversationLineage.GetString(root, "type"), "session_meta", StringComparison.OrdinalIgnoreCase) ||
			!root.TryGetValue("payload", out object payloadValue) || !(payloadValue is Dictionary<string, object> payload))
		{
			throw new InvalidDataException("rollout 首行缺少有效的 session_meta.payload：" + TextHelpers.StripExtendedPrefix(sourcePath));
		}
		string embeddedId = ConversationLineage.ResolveCurrentThreadId(payload, string.Empty);
		if (!Guid.TryParse(embeddedId, out Guid embedded) || !Guid.TryParse(expectedThreadId, out Guid expected) || embedded != expected)
		{
			throw new InvalidDataException("rollout 首行嵌入 ID 与 SessionInfo.ThreadId 不一致，已取消备份：" + TextHelpers.StripExtendedPrefix(sourcePath));
		}
	}

	private static void CopyConsistentSnapshot(string sourcePath, string snapshotPath)
	{
		using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
		using (FileStream destination = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
		{
			long remaining = source.Length;
			byte[] buffer = new byte[65536];
			while (remaining > 0)
			{
				int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
				if (read <= 0)
				{
					throw new IOException("rollout 在建立一致快照时被截断，请稍后重试：" + TextHelpers.StripExtendedPrefix(sourcePath));
				}
				destination.Write(buffer, 0, read);
				remaining -= read;
			}
			destination.Flush(true);
		}
	}

	private static string CreateTemporaryOutputPath(string outputDirectory, string outputFileName, IEnumerable<string> normalizedSourcePaths)
	{
		HashSet<string> sourcePaths = new HashSet<string>(normalizedSourcePaths ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
		for (int attempt = 0; attempt < 100; attempt++)
		{
			string candidate = Path.Combine(outputDirectory, "." + outputFileName + "." + Guid.NewGuid().ToString("N") + ".tmp.zip");
			if (!File.Exists(candidate) && !sourcePaths.Contains(NormalizePath(candidate)))
			{
				return candidate;
			}
		}
		throw new IOException("无法在输出目录创建唯一的临时 ZIP 文件。");
	}

	private static string NormalizeBundleRelativePath(string relativePath)
	{
		string normalized = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
		if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized.Replace('/', '\\')))
		{
			throw new InvalidDataException("rollout 的包内相对路径无效：" + relativePath);
		}
		string[] parts = normalized.Split('/');
		if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == ".."))
		{
			throw new InvalidDataException("rollout 的包内相对路径不安全：" + relativePath);
		}
		return normalized;
	}

	private static string NormalizePath(string path)
	{
		return Path.GetFullPath(TextHelpers.StripExtendedPrefix(path)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	private static void TryDeleteFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			{
				Directory.Delete(path, true);
			}
		}
		catch
		{
		}
	}

	private static string DeriveRelativePath(string sourcePath, string codexHome)
	{
		string text = Path.Combine(codexHome, "sessions").TrimEnd('\\') + "\\";
		string text2 = Path.Combine(codexHome, "archived_sessions").TrimEnd('\\') + "\\";
		string stripped = TextHelpers.StripExtendedPrefix(sourcePath);
		if (stripped.StartsWith(text, StringComparison.OrdinalIgnoreCase))
		{
			return stripped.Substring(text.Length);
		}
		if (stripped.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
		{
			return stripped.Substring(text2.Length);
		}
		return DateTime.Now.ToString("yyyy/MM/dd/", CultureInfo.InvariantCulture) + Path.GetFileName(stripped);
	}

	private static string FriendlyManifestSource(string source)
	{
		string text = (source ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return "app";
		}
		if (!text.StartsWith("{"))
		{
			return text;
		}
		if (text.IndexOf("subagent", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "subagent";
		}
		if (text.IndexOf("vscode", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "vscode";
		}
		if (text.IndexOf("cli", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return "cli";
		}
		return "app";
	}

	private static string NormalizeUtc(string value, DateTime fallbackUtc)
	{
		if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var result))
		{
			return result.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
		}
		return fallbackUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
	}

	private static string Sha256File(string path)
	{
		using SHA256 sHA = SHA256.Create();
		using FileStream inputStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		byte[] array = sHA.ComputeHash(inputStream);
		StringBuilder stringBuilder = new StringBuilder(array.Length * 2);
		byte[] array2 = array;
		foreach (byte b in array2)
		{
			stringBuilder.Append(b.ToString("x2"));
		}
		return stringBuilder.ToString();
	}
}
