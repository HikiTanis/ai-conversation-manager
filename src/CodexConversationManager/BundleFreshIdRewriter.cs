using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexConversationManager;

internal static class BundleFreshIdRewriter
{
	private const long MaximumInMemoryRewriteEntryBytes = 512L * 1024L * 1024L;

	private static readonly Regex GuidPattern = new Regex(
		@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex Sha256Pattern = new Regex(
		@"\A[0-9A-Fa-f]{64}\z",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private sealed class RewriteItem
	{
		public bool Compressed { get; set; }
		public BundleSession Session { get; set; }

		public string OldId { get; set; }

		public string NewId { get; set; }

		public string OriginId { get; set; }

		public string OldBundlePath { get; set; }

		public string NewBundlePath { get; set; }
	}

	private sealed class ValidatedBundle
	{
		public BundleManifest Manifest { get; set; }

		public Dictionary<string, ZipArchiveEntry> Entries { get; set; }
	}

	public static HashSet<string> FindIndexedPathMismatches(string bundlePath, IDictionary<string, string> indexedCwds, string targetPath)
	{
		HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (indexedCwds == null || indexedCwds.Count == 0 || string.IsNullOrWhiteSpace(targetPath))
		{
			return result;
		}
		foreach (BundleSessionLineage session in ReadLineages(bundlePath))
		{
			if (session != null &&
				!string.IsNullOrWhiteSpace(session.CurrentThreadId) &&
				indexedCwds.TryGetValue(session.CurrentThreadId, out string cwd) &&
				!string.Equals(TextHelpers.CanonicalPath(cwd), TextHelpers.CanonicalPath(targetPath), StringComparison.OrdinalIgnoreCase))
			{
				result.Add(session.CurrentThreadId);
			}
		}
		return result;
	}

	public static List<BundleSessionLineage> ReadLineages(string bundlePath)
	{
		if (string.IsNullOrWhiteSpace(bundlePath))
		{
			throw new ArgumentNullException(nameof(bundlePath));
		}
		string fullPath = Path.GetFullPath(bundlePath);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException("找不到需要读取的对话包。", fullPath);
		}
		using FileStream input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		using ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
		ValidatedBundle validated = ValidateBundleForRewrite(archive);
		return ReadValidatedLineages(validated.Manifest, validated.Entries);
	}

	public static FreshIdRewriteResult RewriteAsFresh(string inputPath, string outputPath)
	{
		Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (BundleSessionLineage session in ReadLineages(inputPath))
		{
			idMap[session.CurrentThreadId] = Guid.NewGuid().ToString("D");
		}
		return RewriteWithIdMap(inputPath, outputPath, idMap);
	}

	public static FreshIdRewriteResult Rewrite(string inputPath, string outputPath, ISet<string> threadIds)
	{
		Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (BundleSessionLineage session in ReadLineages(inputPath))
		{
			idMap[session.CurrentThreadId] = threadIds != null && threadIds.Contains(session.CurrentThreadId) ? Guid.NewGuid().ToString("D") : session.CurrentThreadId;
		}
		return RewriteWithIdMap(inputPath, outputPath, idMap);
	}

	public static FreshIdRewriteResult RewriteWithIdMap(string inputPath, string outputPath, IDictionary<string, string> requestedIdMap)
	{
		if (string.IsNullOrWhiteSpace(inputPath))
		{
			throw new ArgumentNullException("inputPath");
		}
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			throw new ArgumentNullException("outputPath");
		}
		string fullInputPath = Path.GetFullPath(inputPath);
		string fullOutputPath = Path.GetFullPath(outputPath);
		if (!File.Exists(fullInputPath))
		{
			throw new FileNotFoundException("找不到需要重写的对话包。", fullInputPath);
		}
		string outputDirectory = Path.GetDirectoryName(fullOutputPath);
		if (string.IsNullOrWhiteSpace(outputDirectory))
		{
			throw new InvalidDataException("无法确定重写包的输出目录。");
		}
		string temporaryOutput = Path.Combine(
			outputDirectory,
			"." + Path.GetFileName(fullOutputPath) + ".rewrite-" + Guid.NewGuid().ToString("N") + ".tmp");
		Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using (FileStream input = new FileStream(fullInputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (ZipArchive source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false))
			{
				// Nothing is written until the complete source archive, manifest and all
				// recorded hashes have been verified against the exact open file handle.
				ValidatedBundle validated = ValidateBundleForRewrite(source);
				BundleManifest manifest = validated.Manifest;
				List<BundleSessionLineage> lineages = ReadValidatedLineages(manifest, validated.Entries);
				Dictionary<string, BundleSessionLineage> lineageById = lineages
					.GroupBy(item => item.CurrentThreadId, StringComparer.OrdinalIgnoreCase)
					.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
				Dictionary<string, RewriteItem> itemsByPath = new Dictionary<string, RewriteItem>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> finalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> finalSessionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (BundleSession session in manifest.sessions)
				{
					string oldId = session.thread_id;
					string newId = oldId;
					if (requestedIdMap != null && requestedIdMap.TryGetValue(oldId, out string mapped) && !string.IsNullOrWhiteSpace(mapped))
					{
						if (!Guid.TryParse(mapped, out Guid parsedNewId))
						{
							throw new InvalidDataException("重编号映射包含无效 Thread ID：" + mapped);
						}
						newId = parsedNewId.ToString("D");
					}
					if (!finalIds.Add(newId))
					{
						throw new InvalidDataException("多个会话被映射到了同一个 Thread ID：" + newId);
					}
					string origin = lineageById.TryGetValue(oldId, out BundleSessionLineage lineage) && !string.IsNullOrWhiteSpace(lineage.OriginThreadId)
						? lineage.OriginThreadId
						: oldId;
					string oldBundlePath = NormalizeEntryPath(session.bundle_path, directory: false);
					string newBundlePath = string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)
						? oldBundlePath
						: ReplaceIdInPath(oldBundlePath, oldId, newId);
					newBundlePath = RolloutStorage.PlainPath(newBundlePath);
					if (!finalSessionPaths.Add(newBundlePath))
					{
						throw new InvalidDataException("重编号后的多个会话将使用同一个文件：" + newBundlePath);
					}
					itemsByPath.Add(oldBundlePath, new RewriteItem
					{
						Session = session,
						Compressed = session.compressed || RolloutStorage.IsCompressed(oldBundlePath),
						OldId = oldId,
						NewId = newId,
						OriginId = origin,
						OldBundlePath = oldBundlePath,
						NewBundlePath = newBundlePath
					});
					idMap.Add(oldId, newId);
					session.origin_thread_id = origin;
					session.thread_id = newId;
					session.bundle_path = newBundlePath;
					session.compressed = false;
					if (!string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
					{
						session.original_path = ReplaceTextIgnoreCase(session.original_path, oldId, newId);
					}
				}

				Directory.CreateDirectory(outputDirectory);
				HashSet<string> rewrittenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> destinationEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				using (ZipArchive destination = ZipFile.Open(temporaryOutput, ZipArchiveMode.Create))
				{
					Dictionary<string, string> checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					foreach (ZipArchiveEntry entry in source.Entries)
					{
						if (string.IsNullOrEmpty(entry.Name))
						{
							continue;
						}
						string entryPath = NormalizeEntryPath(entry.FullName, directory: false);
						if (string.Equals(entryPath, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
							string.Equals(entryPath, "checksums.json", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						string destinationPath = entryPath;
						byte[] data = ReadEntry(entry);
						RewriteItem item = null;
						if (itemsByPath.TryGetValue(entryPath, out item))
						{
							data = RolloutStorage.DecodeBytes(data, item.Compressed);
							data = RewriteSessionMetadata(data, idMap, item.OldId, item.NewId, item.OriginId);
							destinationPath = item.NewBundlePath;
							item.Session.size_bytes = data.LongLength;
							item.Session.sha256 = Sha256(data);
							rewrittenEntries.Add(entryPath);
						}
						if (!destinationEntries.Add(destinationPath))
						{
							throw new InvalidDataException("重编号后的包包含重复路径：" + destinationPath);
						}
						checksums[destinationPath] = Sha256(data);
						WriteEntry(destination, destinationPath, data);
					}
					if (rewrittenEntries.Count != itemsByPath.Count)
					{
						throw new InvalidDataException("对话包清单与实际会话文件不一致，无法安全重写编号。");
					}
					byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerialization.NewSerializer().Serialize(manifest));
					EnsureMetadataSize(manifestBytes, "重编号后的 manifest.json");
					checksums["manifest.json"] = Sha256(manifestBytes);
					WriteEntry(destination, "manifest.json", manifestBytes);
					byte[] checksumBytes = Encoding.UTF8.GetBytes(JsonSerialization.NewSerializer().Serialize(checksums));
					EnsureMetadataSize(checksumBytes, "重编号后的 checksums.json");
					WriteEntry(destination, "checksums.json", checksumBytes);
				}
			}
			ReplaceOutput(temporaryOutput, fullOutputPath);
		}
		catch
		{
			TryDelete(temporaryOutput);
			throw;
		}
		return new FreshIdRewriteResult
		{
			RewrittenCount = idMap.Count(pair => !string.Equals(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase)),
			IdMap = idMap
		};
	}

	private static ValidatedBundle ValidateBundleForRewrite(ZipArchive archive)
	{
		NativeBundleImporter.ValidateArchiveResourceLimits(archive);
		Dictionary<string, ZipArchiveEntry> entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			RejectSymbolicLink(entry);
			string path = NormalizeEntryPath(entry.FullName, string.IsNullOrEmpty(entry.Name));
			if (entries.ContainsKey(path))
			{
				throw new InvalidDataException("对话包包含重复路径：" + path);
			}
			entries.Add(path, entry);
		}
		if (!entries.TryGetValue("manifest.json", out ZipArchiveEntry manifestEntry) || string.IsNullOrEmpty(manifestEntry.Name))
		{
			throw new InvalidDataException("对话包缺少 manifest.json。");
		}
		if (!entries.TryGetValue("checksums.json", out ZipArchiveEntry checksumEntry) || string.IsNullOrEmpty(checksumEntry.Name))
		{
			throw new InvalidDataException("对话包缺少 checksums.json，无法确认原包是否完整。");
		}

		byte[] manifestBytes = NativeBundleImporter.ReadBoundedEntry(
			manifestEntry,
			NativeBundleImporter.MaximumMetadataBytes,
			"manifest.json");
		byte[] checksumBytes = NativeBundleImporter.ReadBoundedEntry(
			checksumEntry,
			NativeBundleImporter.MaximumMetadataBytes,
			"checksums.json");
		BundleManifest manifest;
		Dictionary<string, string> rawChecksums;
		try
		{
			manifest = JsonSerialization.NewSerializer().Deserialize<BundleManifest>(DecodeUtf8(manifestBytes));
			rawChecksums = JsonSerialization.NewSerializer().Deserialize<Dictionary<string, string>>(DecodeUtf8(checksumBytes));
		}
		catch (Exception ex)
		{
			throw new InvalidDataException("对话包的 manifest.json 或 checksums.json 不是有效 JSON。", ex);
		}
		if (manifest == null ||
			!string.Equals(manifest.format_version, NativeBundleImporter.FormatVersion, StringComparison.Ordinal) ||
			manifest.sessions == null ||
			manifest.sessions.Count == 0)
		{
			throw new InvalidDataException("对话包格式无效、版本不受支持或清单中没有 sessions。");
		}
		if (rawChecksums == null)
		{
			throw new InvalidDataException("checksums.json 不是有效的哈希表。");
		}

		Dictionary<string, string> checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, string> pair in rawChecksums)
		{
			string path = NormalizeEntryPath(pair.Key, directory: false);
			if (!Sha256Pattern.IsMatch(pair.Value ?? string.Empty))
			{
				throw new InvalidDataException("checksums.json 包含无效 SHA-256：" + path);
			}
			if (checksums.ContainsKey(path))
			{
				throw new InvalidDataException("checksums.json 包含重复路径：" + path);
			}
			checksums.Add(path, pair.Value.ToLowerInvariant());
		}
		foreach (KeyValuePair<string, ZipArchiveEntry> pair in entries)
		{
			if (string.IsNullOrEmpty(pair.Value.Name) || string.Equals(pair.Key, "checksums.json", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!checksums.ContainsKey(pair.Key))
			{
				throw new InvalidDataException("对话包文件没有 SHA-256 记录：" + pair.Key);
			}
		}
		foreach (string path in checksums.Keys)
		{
			if (string.Equals(path, "checksums.json", StringComparison.OrdinalIgnoreCase) ||
				!entries.TryGetValue(path, out ZipArchiveEntry entry) ||
				string.IsNullOrEmpty(entry.Name))
			{
				throw new InvalidDataException("checksums.json 引用了不存在或无效的文件：" + path);
			}
		}
		if (!checksums.TryGetValue("manifest.json", out string expectedManifestHash) ||
			!HashEquals(Sha256(manifestBytes), expectedManifestHash))
		{
			throw new InvalidDataException("manifest.json 的 SHA-256 与 checksums.json 不一致。");
		}

		HashSet<string> sessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> sessionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (BundleSession session in manifest.sessions)
		{
			if (session == null || !Guid.TryParse(session.thread_id, out _))
			{
				throw new InvalidDataException("对话包清单包含无效 Thread ID。");
			}
			if (!sessionIds.Add(session.thread_id))
			{
				throw new InvalidDataException("对话包清单包含重复 Thread ID：" + session.thread_id);
			}
			string sessionPath = NormalizeEntryPath(session.bundle_path, directory: false);
			if (!RolloutStorage.PlainPath(sessionPath).EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("对话文件不是 JSONL：" + sessionPath);
			}
			if (!sessionPaths.Add(sessionPath))
			{
				throw new InvalidDataException("多个会话引用了同一个文件：" + sessionPath);
			}
			if (!entries.TryGetValue(sessionPath, out ZipArchiveEntry sessionEntry) || string.IsNullOrEmpty(sessionEntry.Name))
			{
				throw new InvalidDataException("对话包清单引用的会话文件不存在：" + sessionPath);
			}
			if (!checksums.TryGetValue(sessionPath, out string expectedEntryHash))
			{
				throw new InvalidDataException("对话文件缺少 SHA-256：" + sessionPath);
			}
			if (!Sha256Pattern.IsMatch(session.sha256 ?? string.Empty))
			{
				throw new InvalidDataException("对话清单包含无效 SHA-256：" + sessionPath);
			}
			string actualHash;
			using (Stream sessionStream = sessionEntry.Open())
			{
				actualHash = Sha256(sessionStream);
			}
			if (!HashEquals(actualHash, expectedEntryHash) || !HashEquals(actualHash, session.sha256))
			{
				throw new InvalidDataException("对话文件内容与 manifest/checksums 中的 SHA-256 不一致：" + sessionPath);
			}
			if (session.size_bytes <= 0 || sessionEntry.Length <= 0 || session.size_bytes != sessionEntry.Length)
			{
				throw new InvalidDataException("对话包清单中的文件大小与实际内容不一致：" + sessionPath);
			}
			ValidateSessionIdentity(ReadFirstLine(sessionEntry, session.thread_id, session.compressed), session.thread_id);
			session.bundle_path = sessionPath;
		}

		foreach (KeyValuePair<string, ZipArchiveEntry> pair in entries)
		{
			if (string.IsNullOrEmpty(pair.Value.Name) ||
				string.Equals(pair.Key, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(pair.Key, "checksums.json", StringComparison.OrdinalIgnoreCase) ||
				sessionPaths.Contains(pair.Key))
			{
				continue;
			}
			using (Stream stream = pair.Value.Open())
			{
				if (!HashEquals(Sha256(stream), checksums[pair.Key]))
				{
					throw new InvalidDataException("对话包文件内容与 checksums.json 不一致：" + pair.Key);
				}
			}
		}
		return new ValidatedBundle
		{
			Manifest = manifest,
			Entries = entries
		};
	}

	private static List<BundleSessionLineage> ReadValidatedLineages(
		BundleManifest manifest,
		IDictionary<string, ZipArchiveEntry> entries)
	{
		List<BundleSessionLineage> result = new List<BundleSessionLineage>();
		foreach (BundleSession session in manifest.sessions)
		{
			Dictionary<string, object> payload = ReadPayload(
				ReadFirstLine(entries[session.bundle_path], session.thread_id, session.compressed),
				session.thread_id);
			string embeddedOrigin = ConversationLineage.ResolveOriginThreadId(payload, session.thread_id);
			string origin = !string.IsNullOrWhiteSpace(session.origin_thread_id)
				? session.origin_thread_id
				: embeddedOrigin;
			result.Add(new BundleSessionLineage
			{
				CurrentThreadId = session.thread_id,
				OriginThreadId = string.IsNullOrWhiteSpace(origin) ? session.thread_id : origin,
				OriginalCwd = session.original_cwd
			});
		}
		return result;
	}

	private static void ValidateSessionIdentity(byte[] data, string manifestThreadId)
	{
		Dictionary<string, object> payload = ReadPayload(data, manifestThreadId);
		string id = GetString(payload, "id");
		string threadId = GetString(payload, "thread_id");
		string sessionId = GetString(payload, "session_id");
		if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, manifestThreadId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("session_meta.id 与 manifest Thread ID 不一致：" + manifestThreadId);
		}
		if (!string.IsNullOrWhiteSpace(threadId) && !string.Equals(threadId, manifestThreadId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("session_meta.thread_id 与 manifest Thread ID 不一致：" + manifestThreadId);
		}
		if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(threadId) &&
			!string.Equals(sessionId, manifestThreadId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("session_meta 中找不到与 manifest 一致的会话编号：" + manifestThreadId);
		}
	}

	private static string ReplaceIdInPath(string path, string oldId, string newId)
	{
		string normalized = NormalizeEntryPath(path, directory: false);
		int slash = normalized.LastIndexOf('/');
		string directory = slash >= 0 ? normalized.Substring(0, slash + 1) : string.Empty;
		string fileName = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
		MatchCollection matches = GuidPattern.Matches(fileName);
		bool replacedOldId = false;
		string rewrittenName = GuidPattern.Replace(fileName, match =>
		{
			if (string.Equals(match.Value, oldId, StringComparison.OrdinalIgnoreCase))
			{
				replacedOldId = true;
				return newId;
			}
			return match.Value;
		});
		if (!replacedOldId)
		{
			if (matches.Count == 1)
			{
				rewrittenName = GuidPattern.Replace(fileName, newId, 1);
			}
			else if (matches.Count > 1)
			{
				throw new InvalidDataException("会话文件名包含多个无法判定归属的 GUID：" + path);
			}
			else
			{
				int extensionOffset = fileName.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase)
					? fileName.Length - ".jsonl.zst".Length
					: fileName.Length - Path.GetExtension(fileName).Length;
				rewrittenName = fileName.Substring(0, extensionOffset) + "-" + newId + fileName.Substring(extensionOffset);
			}
		}
		foreach (Match match in GuidPattern.Matches(rewrittenName))
		{
			if (!string.Equals(match.Value, newId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("重编号后的会话文件名仍包含其他 GUID：" + rewrittenName);
			}
		}
		if (rewrittenName.IndexOf(newId, StringComparison.OrdinalIgnoreCase) < 0)
		{
			throw new InvalidDataException("重编号后的会话文件名未包含新的 Thread ID：" + rewrittenName);
		}
		return NormalizeEntryPath(directory + rewrittenName, directory: false);
	}

	private static ZipArchiveEntry FindEntry(ZipArchive archive, string path)
	{
		string normalized = (path ?? string.Empty).Replace('\\', '/');
		return archive.Entries.FirstOrDefault(entry =>
			string.Equals(entry.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
	}

	private static string ReplaceTextIgnoreCase(string value, string oldText, string newText)
	{
		if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldText))
		{
			return value;
		}
		return Regex.Replace(value, Regex.Escape(oldText), newText.Replace("$", "$$"), RegexOptions.IgnoreCase);
	}

	private static Dictionary<string, object> ReadPayload(byte[] firstLine, string threadId)
	{
		string json = DecodeUtf8(firstLine);
		Dictionary<string, object> root = JsonSerialization.NewSerializer().DeserializeObject(json) as Dictionary<string, object>;
		if (root == null ||
			!root.TryGetValue("type", out object typeValue) ||
			!string.Equals(Convert.ToString(typeValue), "session_meta", StringComparison.OrdinalIgnoreCase) ||
			!root.TryGetValue("payload", out object value) ||
			!(value is Dictionary<string, object> payload))
		{
			throw new InvalidDataException("会话首行不是有效的 session_meta payload：" + threadId);
		}
		return payload;
	}

	private static byte[] RewriteSessionMetadata(byte[] data, IDictionary<string, string> idMap, string oldId, string newId, string originId)
	{
		int newline = Array.IndexOf(data, (byte)10);
		if (newline < 0)
		{
			throw new InvalidDataException("会话文件缺少首行 session_meta：" + oldId);
		}
		int lineEnd = newline > 0 && data[newline - 1] == 13 ? newline - 1 : newline;
		string firstLine = Encoding.UTF8.GetString(data, 0, lineEnd).TrimStart('\uFEFF');
		Dictionary<string, object> root = JsonSerialization.NewSerializer().DeserializeObject(firstLine) as Dictionary<string, object>;
		if (root == null || !root.TryGetValue("payload", out object value) || !(value is Dictionary<string, object> payload))
		{
			throw new InvalidDataException("会话首行缺少 payload：" + oldId);
		}
		string originalSessionId = GetString(payload, "session_id");
		ReplaceMappedStrings(payload, idMap);
		payload["id"] = newId;
		if (payload.ContainsKey("thread_id"))
		{
			payload["thread_id"] = newId;
		}
		if (payload.ContainsKey("session_id") &&
			!string.IsNullOrWhiteSpace(originalSessionId) &&
			idMap.TryGetValue(originalSessionId, out string mappedSessionId))
		{
			// session_id is the current ID in older rollouts and the parent ID in
			// subagent rollouts. In both cases it must follow the same ID map.
			payload["session_id"] = mappedSessionId;
		}
		payload[ConversationLineage.OriginThreadIdKey] = originId;
		if (!string.Equals(GetString(payload, "id"), newId, StringComparison.OrdinalIgnoreCase) ||
			(payload.ContainsKey("thread_id") && !string.Equals(GetString(payload, "thread_id"), newId, StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidDataException("重编号后的 session_meta 会话编号不一致：" + newId);
		}
		byte[] firstLineBytes = Encoding.UTF8.GetBytes(JsonSerialization.NewSerializer().Serialize(root));
		using (MemoryStream output = new MemoryStream(firstLineBytes.Length + data.Length - lineEnd))
		{
			output.Write(firstLineBytes, 0, firstLineBytes.Length);
			output.Write(data, lineEnd, data.Length - lineEnd);
			return output.ToArray();
		}
	}

	private static void ReplaceMappedStrings(object value, IDictionary<string, string> idMap)
	{
		if (value is IDictionary<string, object> dictionary)
		{
			foreach (string key in dictionary.Keys.ToList())
			{
				if (string.Equals(key, ConversationLineage.OriginThreadIdKey, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				object child = dictionary[key];
				if (child is string text && idMap.TryGetValue(text, out string mapped))
				{
					dictionary[key] = mapped;
				}
				else
				{
					ReplaceMappedStrings(child, idMap);
				}
			}
		}
		else if (value is IList list && !(value is string))
		{
			for (int index = 0; index < list.Count; index++)
			{
				if (list[index] is string text && idMap.TryGetValue(text, out string mapped))
				{
					list[index] = mapped;
				}
				else
				{
					ReplaceMappedStrings(list[index], idMap);
				}
			}
		}
	}

	private static string NormalizeEntryPath(string value, bool directory)
	{
		string normalized = (value ?? string.Empty).Replace('\\', '/');
		if (directory)
		{
			normalized = normalized.TrimEnd('/');
		}
		if (string.IsNullOrWhiteSpace(normalized) ||
			normalized.StartsWith("/", StringComparison.Ordinal) ||
			normalized.IndexOf('\0') >= 0)
		{
			throw new InvalidDataException("对话包包含无效路径：" + value);
		}
		foreach (string segment in normalized.Split('/'))
		{
			if (string.IsNullOrWhiteSpace(segment) ||
				segment == "." ||
				segment == ".." ||
				segment.EndsWith(".", StringComparison.Ordinal) ||
				segment.EndsWith(" ", StringComparison.Ordinal) ||
				segment.IndexOf(':') >= 0 ||
				segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
				IsReservedDeviceName(segment))
			{
				throw new InvalidDataException("对话包包含 Windows 不允许的路径：" + value);
			}
		}
		return normalized;
	}

	private static bool IsReservedDeviceName(string segment)
	{
		string name = Path.GetFileNameWithoutExtension(segment).ToUpperInvariant();
		if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL")
		{
			return true;
		}
		return name.Length == 4 &&
			(name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
			name[3] >= '1' && name[3] <= '9';
	}

	private static void RejectSymbolicLink(ZipArchiveEntry entry)
	{
		int unixMode = (entry.ExternalAttributes >> 16) & 61440;
		if (unixMode == 40960)
		{
			throw new InvalidDataException("对话包包含符号链接，已拒绝读取：" + entry.FullName);
		}
	}

	private static string DecodeUtf8(byte[] data)
	{
		string value = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true).GetString(data);
		return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
	}

	private static string GetString(IDictionary<string, object> dictionary, string key)
	{
		return dictionary != null && dictionary.TryGetValue(key, out object value) && value != null
			? Convert.ToString(value)
			: string.Empty;
	}

	private static bool HashEquals(string actual, string expected)
	{
		return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
	}

	private static void ReplaceOutput(string temporaryPath, string destinationPath)
	{
		if (!File.Exists(destinationPath))
		{
			File.Move(temporaryPath, destinationPath);
			return;
		}
		string previousPath = destinationPath + ".previous-" + Guid.NewGuid().ToString("N") + ".tmp";
		File.Move(destinationPath, previousPath);
		try
		{
			File.Move(temporaryPath, destinationPath);
			TryDelete(previousPath);
		}
		catch
		{
			if (!File.Exists(destinationPath) && File.Exists(previousPath))
			{
				File.Move(previousPath, destinationPath);
			}
			throw;
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static byte[] ReadEntry(ZipArchiveEntry entry)
	{
		return NativeBundleImporter.ReadBoundedEntry(
			entry,
			MaximumInMemoryRewriteEntryBytes,
			entry.FullName);
	}

	private static byte[] ReadFirstLine(ZipArchiveEntry entry, string threadId, bool compressed)
	{
		using Stream input = RolloutStorage.Decode(entry.Open(), compressed || RolloutStorage.IsCompressed(entry.FullName));
		using MemoryStream line = new MemoryStream();
		bool foundNewline = false;
		while (line.Length <= NativeBundleImporter.MaximumMetadataBytes)
		{
			int value = input.ReadByte();
			if (value < 0)
			{
				break;
			}
			if (value == '\n')
			{
				foundNewline = true;
				break;
			}
			line.WriteByte((byte)value);
		}
		if (!foundNewline)
		{
			throw new InvalidDataException(
				line.Length > NativeBundleImporter.MaximumMetadataBytes
					? "会话 session_meta 首行过大：" + threadId
					: "会话文件缺少首行换行符：" + threadId);
		}
		byte[] raw = line.ToArray();
		return raw.Length > 0 && raw[raw.Length - 1] == '\r'
			? raw.Take(raw.Length - 1).ToArray()
			: raw;
	}

	private static void EnsureMetadataSize(byte[] data, string name)
	{
		if (data == null || data.LongLength > NativeBundleImporter.MaximumMetadataBytes)
		{
			throw new InvalidDataException(name + " 超过 16 MB 安全上限。");
		}
	}

	private static void WriteEntry(ZipArchive archive, string path, byte[] data)
	{
		ZipArchiveEntry entry = archive.CreateEntry(path.Replace('\\', '/'), CompressionLevel.Optimal);
		using (Stream stream = entry.Open())
		{
			stream.Write(data, 0, data.Length);
		}
	}

	private static string Sha256(byte[] data)
	{
		using (SHA256 sha = SHA256.Create())
		{
			byte[] hash = sha.ComputeHash(data);
			StringBuilder value = new StringBuilder(hash.Length * 2);
			foreach (byte item in hash)
			{
				value.Append(item.ToString("x2"));
			}
			return value.ToString();
		}
	}

	private static string Sha256(Stream stream)
	{
		using (SHA256 sha = SHA256.Create())
		{
			byte[] hash = sha.ComputeHash(stream);
			StringBuilder value = new StringBuilder(hash.Length * 2);
			foreach (byte item in hash)
			{
				value.Append(item.ToString("x2"));
			}
			return value.ToString();
		}
	}
}
