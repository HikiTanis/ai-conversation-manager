using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexConversationManager;

internal enum NativeBundleImportMode
{
	Merge,
	Replace
}

internal enum NativeBundleImportStatus
{
	Created,
	FastForwarded,
	Replaced,
	SkippedIdentical,
	SkippedLocalAhead
}

internal sealed class NativeBundleImportOptions
{
	public string BundlePath { get; set; }

	public string CodexHome { get; set; }

	public string SourceCwd { get; set; }

	public string TargetCwd { get; set; }

	public NativeBundleImportMode Mode { get; set; } = NativeBundleImportMode.Merge;

	public bool DryRun { get; set; }
}

internal sealed class NativeBundleImportSessionResult
{
	public string ThreadId { get; set; }

	public string SourceEntryPath { get; set; }

	public string DestinationPath { get; set; }

	public string BackupPath { get; set; }

	public NativeBundleImportStatus Status { get; set; }

	public bool CwdChanged { get; set; }

	public long SourceSizeBytes { get; set; }

	public long DestinationSizeBytes { get; set; }

	public bool WouldWrite => Status == NativeBundleImportStatus.Created ||
		Status == NativeBundleImportStatus.FastForwarded ||
		Status == NativeBundleImportStatus.Replaced;
}

internal sealed class NativeBundleImportResult
{
	public string BundlePath { get; set; }

	public string CodexHome { get; set; }

	public bool DryRun { get; set; }

	public List<NativeBundleImportSessionResult> Sessions { get; set; } = new List<NativeBundleImportSessionResult>();

	public int CreatedCount => Sessions.Count(item => item.Status == NativeBundleImportStatus.Created);

	public int MergedCount => Sessions.Count(item => item.Status == NativeBundleImportStatus.FastForwarded);

	public int ReplacedCount => Sessions.Count(item => item.Status == NativeBundleImportStatus.Replaced);

	public int SkippedCount => Sessions.Count(item =>
		item.Status == NativeBundleImportStatus.SkippedIdentical ||
		item.Status == NativeBundleImportStatus.SkippedLocalAhead);

	public IReadOnlyList<string> PlannedWriteSessionFiles => Sessions
		.Where(item => item.WouldWrite)
		.Select(item => item.DestinationPath)
		.ToList();

	public IReadOnlyList<string> TouchedSessionFiles => DryRun
		? new List<string>()
		: PlannedWriteSessionFiles;

	// Existing targets receive adjacent .ccm-txn-bak-* snapshots before mutation.
	// The outer NativeImportTransaction must commit-clean them after indexing succeeds,
	// or restore them when a later import/indexing step fails.
	public IReadOnlyList<string> TransactionBackupFiles => Sessions
		.Where(item => !string.IsNullOrWhiteSpace(item.BackupPath))
		.Select(item => item.BackupPath)
		.ToList();
}

internal static class NativeBundleImporter
{
	internal const string FormatVersion = "codex-sync-bundle-v1";

	internal const int MaximumMetadataBytes = 16 * 1024 * 1024;

	internal const int MaximumArchiveEntries = 50000;

	internal const long MaximumArchiveEntryBytes = 32L * 1024L * 1024L * 1024L;

	internal const long MaximumExpandedBytes = 128L * 1024L * 1024L * 1024L;

	internal const long MaximumCompressionRatio = 10000L;

	internal const int MaximumJsonLineBytes = 64 * 1024 * 1024;

	internal const long CompressionRatioCheckThresholdBytes = 100L * 1024L * 1024L;

	private static readonly Regex Sha256Pattern = new Regex(
		"^[0-9a-f]{64}$",
		RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ThreadIdPattern = new Regex(
		"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
		RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex CwdPropertyPattern = new Regex(
		"(?<prefix>\\\"cwd\\\"\\s*:\\s*)(?<value>\\\"(?:\\\\.|[^\\\"\\\\])*\\\")",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private sealed class ValidatedBundle : IDisposable
	{
		public string StageRoot { get; set; }

		public List<StagedSession> Sessions { get; } = new List<StagedSession>();

		public void Dispose()
		{
			TryDeleteDirectory(StageRoot);
		}
	}

	private sealed class StagedSession
	{
		public BundleSession ManifestSession { get; set; }

		public string EntryPath { get; set; }

		public string StagedPath { get; set; }

		public bool CwdChanged { get; set; }

		public long SourceSizeBytes { get; set; }

		public DateTime? CreatedAtUtc { get; set; }

		public DateTime? UpdatedAtUtc { get; set; }
	}

	private sealed class PlannedImport
	{
		public StagedSession Source { get; set; }

		public string StorageRoot { get; set; }

		public string DestinationPath { get; set; }

		public NativeBundleImportStatus Status { get; set; }

		public string BackupPath { get; set; }

		public long ExistingLength { get; set; }

		public long ExistingLastWriteTicks { get; set; }
	}

	private sealed class AppliedChange
	{
		public string DestinationPath { get; set; }

		public string BackupPath { get; set; }

		public bool Created { get; set; }
	}

	private enum PrefixComparison
	{
		Identical,
		IncomingExtendsLocal,
		LocalExtendsIncoming,
		Diverged
	}

	public static NativeBundleImportResult Import(
		string bundlePath,
		string codexHome,
		string sourceCwd,
		string targetCwd,
		NativeBundleImportMode mode,
		bool dryRun)
	{
		return Import(new NativeBundleImportOptions
		{
			BundlePath = bundlePath,
			CodexHome = codexHome,
			SourceCwd = sourceCwd,
			TargetCwd = targetCwd,
			Mode = mode,
			DryRun = dryRun
		});
	}

	public static NativeBundleImportResult Import(NativeBundleImportOptions options)
	{
		if (options == null)
		{
			throw new ArgumentNullException(nameof(options));
		}
		string bundlePath = RequiredFile(options.BundlePath, "找不到需要导入的对话包。");
		string codexHome = RequiredDirectoryPath(options.CodexHome);
		using ValidatedBundle bundle = ReadAndStageBundle(bundlePath, options);
		List<PlannedImport> plans = BuildPlan(bundle, codexHome, options.Mode);
		NativeBundleImportResult result = new NativeBundleImportResult
		{
			BundlePath = bundlePath,
			CodexHome = codexHome,
			DryRun = options.DryRun
		};
		if (!options.DryRun)
		{
			ApplyPlan(plans);
		}
		foreach (PlannedImport plan in plans)
		{
			long destinationSize = !options.DryRun || plan.Status == NativeBundleImportStatus.SkippedIdentical ||
				plan.Status == NativeBundleImportStatus.SkippedLocalAhead
				? SafeFileLength(plan.DestinationPath)
				: new FileInfo(plan.Source.StagedPath).Length;
			result.Sessions.Add(new NativeBundleImportSessionResult
			{
				ThreadId = plan.Source.ManifestSession.thread_id,
				SourceEntryPath = plan.Source.EntryPath,
				DestinationPath = plan.DestinationPath,
				BackupPath = options.DryRun ? string.Empty : plan.BackupPath,
				Status = plan.Status,
				CwdChanged = plan.Source.CwdChanged,
				SourceSizeBytes = plan.Source.SourceSizeBytes,
				DestinationSizeBytes = destinationSize
			});
		}
		return result;
	}

	/// <summary>
	/// Performs the same archive, manifest, checksum, session identity and cwd
	/// validation as a native import, but deliberately does not inspect or mutate
	/// the destination Codex home. This is the required trust boundary before
	/// lineage planning or Thread ID rewriting.
	/// </summary>
	public static int PreflightBundle(
		string bundlePath,
		string sourceCwd,
		string targetCwd)
	{
		NativeBundleImportOptions options = new NativeBundleImportOptions
		{
			BundlePath = RequiredFile(bundlePath, "找不到需要预检的对话包。"),
			SourceCwd = sourceCwd,
			TargetCwd = targetCwd,
			DryRun = true
		};
		using ValidatedBundle bundle = ReadAndStageBundle(options.BundlePath, options);
		return bundle.Sessions.Count;
	}

	private static ValidatedBundle ReadAndStageBundle(string bundlePath, NativeBundleImportOptions options)
	{
		ValidatedBundle result = new ValidatedBundle
		{
			StageRoot = Path.Combine(Path.GetTempPath(), "codex-native-import-" + Guid.NewGuid().ToString("N"))
		};
		Directory.CreateDirectory(result.StageRoot);
		try
		{
			using ZipArchive archive = ZipFile.OpenRead(bundlePath);
			Dictionary<string, ZipArchiveEntry> entries = IndexEntries(archive);
			EnsureTemporaryDiskSpace(result.StageRoot, entries.Values);
			byte[] manifestBytes = ReadSmallEntry(RequiredEntry(entries, "manifest.json"), "manifest.json");
			byte[] checksumBytes = ReadSmallEntry(RequiredEntry(entries, "checksums.json"), "checksums.json");
			JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
			BundleManifest manifest;
			Dictionary<string, string> rawChecksums;
			try
			{
				manifest = serializer.Deserialize<BundleManifest>(DecodeUtf8(manifestBytes));
				rawChecksums = serializer.Deserialize<Dictionary<string, string>>(DecodeUtf8(checksumBytes));
			}
			catch (Exception ex)
			{
				throw new InvalidDataException("对话包的 manifest.json 或 checksums.json 不是有效 JSON。", ex);
			}
			ValidateManifest(manifest);
			Dictionary<string, string> checksums = NormalizeChecksums(rawChecksums);
			ValidateChecksumCoverage(entries, checksums);
			ValidateHash("manifest.json", manifestBytes, checksums["manifest.json"]);
			HashSet<string> sessionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> threadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int index = 0;
			foreach (BundleSession session in manifest.sessions)
			{
				ValidateSessionDescriptor(session, entries, checksums, sessionPaths, threadIds);
				string entryPath = NormalizeEntryPath(session.bundle_path, directory: false);
				ZipArchiveEntry entry = entries[entryPath];
				string rawPath = Path.Combine(result.StageRoot, index.ToString("D5", CultureInfo.InvariantCulture) + ".raw.jsonl");
				ExtractEntry(entry, rawPath);
				string actualHash = ProjectPayloadService.Sha256File(rawPath);
				ValidateHashValue(entryPath, actualHash, checksums[entryPath]);
				ValidateHashValue(entryPath, actualHash, session.sha256);
				if (session.size_bytes != entry.Length)
				{
					throw new InvalidDataException("对话包清单中的文件大小与实际内容不一致：" + entryPath);
				}
				if (session.compressed || RolloutStorage.IsCompressed(entryPath))
				{
					string decodedPath = rawPath + ".decoded";
					using (Stream decoded = RolloutStorage.Decode(File.OpenRead(rawPath), true))
					using (Stream output = File.Create(decodedPath)) decoded.CopyTo(output);
					File.Delete(rawPath);
					File.Move(decodedPath, rawPath);
					session.bundle_path = RolloutStorage.PlainPath(session.bundle_path);
					session.compressed = false;
				}
				string stagedPath = Path.Combine(result.StageRoot, index.ToString("D5", CultureInfo.InvariantCulture) + ".jsonl");
				bool cwdChanged = ValidateAndRewriteSessionMeta(
					rawPath,
					stagedPath,
					session,
					options.SourceCwd,
					options.TargetCwd);
				File.Delete(rawPath);
				result.Sessions.Add(new StagedSession
				{
					ManifestSession = session,
					EntryPath = entryPath,
					StagedPath = stagedPath,
					CwdChanged = cwdChanged,
					SourceSizeBytes = entry.Length,
					CreatedAtUtc = ParseUtc(session.created_at),
					UpdatedAtUtc = ParseUtc(session.updated_at)
				});
				index++;
			}
			ValidateOtherEntries(entries, checksums, sessionPaths);
			return result;
		}
		catch
		{
			result.Dispose();
			throw;
		}
	}

	private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive archive)
	{
		ValidateArchiveResourceLimits(archive);
		Dictionary<string, ZipArchiveEntry> result =
			new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			RejectSymbolicLink(entry);
			bool directory = string.IsNullOrEmpty(entry.Name);
			string path = NormalizeEntryPath(entry.FullName, directory);
			if (result.ContainsKey(path))
			{
				throw new InvalidDataException("对话包包含重复路径：" + path);
			}
			result.Add(path, entry);
		}
		return result;
	}

	internal static void ValidateArchiveResourceLimits(ZipArchive archive)
	{
		if (archive == null)
		{
			throw new ArgumentNullException(nameof(archive));
		}
		if (archive.Entries.Count > MaximumArchiveEntries)
		{
			throw new InvalidDataException("对话包文件数量过多，已停止解压。");
		}
		long expandedBytes = 0L;
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (entry.Length < 0 || entry.CompressedLength < 0 ||
				entry.Length > MaximumArchiveEntryBytes)
			{
				throw new InvalidDataException("对话包条目大小无效或超过 32 GB，已停止解压：" + entry.FullName);
			}
			if (expandedBytes > MaximumExpandedBytes - entry.Length)
			{
				throw new InvalidDataException("对话包展开大小无效或超过 128 GB，已停止解压。");
			}
			expandedBytes += entry.Length;
			if (entry.Length > CompressionRatioCheckThresholdBytes &&
				(entry.CompressedLength == 0L ||
					entry.Length / Math.Max(1L, entry.CompressedLength) > MaximumCompressionRatio))
			{
				throw new InvalidDataException("对话包包含异常压缩比文件，已停止解压：" + entry.FullName);
			}
		}
	}

	internal static byte[] ReadBoundedEntry(
		ZipArchiveEntry entry,
		long maximumBytes,
		string displayName)
	{
		if (entry == null)
		{
			throw new ArgumentNullException(nameof(entry));
		}
		if (maximumBytes < 0 || maximumBytes > int.MaxValue)
		{
			throw new ArgumentOutOfRangeException(nameof(maximumBytes));
		}
		if (entry.Length < 0 || entry.Length > maximumBytes)
		{
			throw new InvalidDataException((displayName ?? entry.FullName) + " 过大或长度无效。");
		}
		using Stream input = entry.Open();
		using MemoryStream output = new MemoryStream((int)entry.Length);
		byte[] buffer = new byte[81920];
		long written = 0L;
		while (true)
		{
			int read = input.Read(buffer, 0, buffer.Length);
			if (read == 0)
			{
				break;
			}
			written += read;
			if (written > entry.Length || written > maximumBytes)
			{
				throw new InvalidDataException((displayName ?? entry.FullName) + " 展开长度超过限制。");
			}
			output.Write(buffer, 0, read);
		}
		if (written != entry.Length)
		{
			throw new InvalidDataException((displayName ?? entry.FullName) + " 读取不完整。");
		}
		return output.ToArray();
	}

	private static void EnsureTemporaryDiskSpace(string stageRoot, IEnumerable<ZipArchiveEntry> entries)
	{
		long expanded = entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Sum(entry => entry.Length);
		long required = expanded > (long.MaxValue - 256L * 1024L * 1024L) / 2L
			? long.MaxValue
			: expanded * 2L + 256L * 1024L * 1024L;
		try
		{
			string root = Path.GetPathRoot(Path.GetFullPath(stageRoot));
			DriveInfo drive = new DriveInfo(root);
			if (drive.IsReady && drive.AvailableFreeSpace < required)
			{
				throw new IOException("系统临时盘空间不足，无法安全验证并导入此迁移包。需要约 " +
					TextHelpers.FormatBytes(required) + "，当前可用 " + TextHelpers.FormatBytes(drive.AvailableFreeSpace) + "。");
			}
		}
		catch (IOException)
		{
			throw;
		}
		catch
		{
			// Some removable or virtual volumes do not expose capacity; extraction still
			// remains bounded by MaximumExpandedBytes.
		}
	}

	private static void ValidateManifest(BundleManifest manifest)
	{
		if (manifest == null)
		{
			throw new InvalidDataException("对话包缺少有效清单。");
		}
		if (!string.Equals(manifest.format_version, FormatVersion, StringComparison.Ordinal))
		{
			throw new InvalidDataException(
				"不支持的对话包格式：" + (manifest.format_version ?? "（空）"));
		}
		if (manifest.sessions == null || manifest.sessions.Count == 0)
		{
			throw new InvalidDataException("对话包清单中没有 sessions。");
		}
	}

	private static Dictionary<string, string> NormalizeChecksums(
		Dictionary<string, string> rawChecksums)
	{
		if (rawChecksums == null)
		{
			throw new InvalidDataException("checksums.json 不是有效的哈希表。");
		}
		Dictionary<string, string> result =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (KeyValuePair<string, string> pair in rawChecksums)
		{
			string path = NormalizeEntryPath(pair.Key, directory: false);
			if (!Sha256Pattern.IsMatch(pair.Value ?? string.Empty))
			{
				throw new InvalidDataException("checksums.json 包含无效 SHA-256：" + path);
			}
			if (result.ContainsKey(path))
			{
				throw new InvalidDataException("checksums.json 包含重复路径：" + path);
			}
			result.Add(path, pair.Value.ToLowerInvariant());
		}
		return result;
	}

	private static void ValidateChecksumCoverage(
		Dictionary<string, ZipArchiveEntry> entries,
		Dictionary<string, string> checksums)
	{
		foreach (KeyValuePair<string, ZipArchiveEntry> pair in entries)
		{
			if (string.IsNullOrEmpty(pair.Value.Name) ||
				string.Equals(pair.Key, "checksums.json", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (!checksums.ContainsKey(pair.Key))
			{
				throw new InvalidDataException(
					"对话包文件没有 SHA-256 记录：" + pair.Key);
			}
		}
		foreach (string path in checksums.Keys)
		{
			if (string.Equals(path, "checksums.json", StringComparison.OrdinalIgnoreCase) ||
				!entries.TryGetValue(path, out ZipArchiveEntry entry) ||
				string.IsNullOrEmpty(entry.Name))
			{
				throw new InvalidDataException(
					"checksums.json 引用了不存在或无效的文件：" + path);
			}
		}
		if (!checksums.ContainsKey("manifest.json"))
		{
			throw new InvalidDataException(
				"checksums.json 缺少 manifest.json 的 SHA-256。");
		}
	}

	private static void ValidateSessionDescriptor(
		BundleSession session,
		Dictionary<string, ZipArchiveEntry> entries,
		Dictionary<string, string> checksums,
		HashSet<string> sessionPaths,
		HashSet<string> threadIds)
	{
		if (session == null || !Guid.TryParse(session.thread_id, out Guid parsedThreadId))
		{
			throw new InvalidDataException("对话包清单包含无效 Thread ID。");
		}
		session.thread_id = parsedThreadId.ToString("D");
		if (!threadIds.Add(session.thread_id))
		{
			throw new InvalidDataException(
				"对话包清单包含重复 Thread ID：" + session.thread_id);
		}
		string path = NormalizeEntryPath(session.bundle_path, directory: false);
		if (!RolloutStorage.PlainPath(path).EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("对话文件不是 JSONL：" + path);
		}
		if (!sessionPaths.Add(path))
		{
			throw new InvalidDataException(
				"多个会话引用了同一个文件：" + path);
		}
		if (!entries.TryGetValue(path, out ZipArchiveEntry entry) ||
			string.IsNullOrEmpty(entry.Name))
		{
			throw new InvalidDataException(
				"对话包清单引用的会话文件不存在：" + path);
		}
		if (!checksums.ContainsKey(path))
		{
			throw new InvalidDataException("对话文件缺少 SHA-256：" + path);
		}
		if (!Sha256Pattern.IsMatch(session.sha256 ?? string.Empty))
		{
			throw new InvalidDataException(
				"对话清单包含无效 SHA-256：" + path);
		}
		if (session.size_bytes <= 0 || entry.Length <= 0)
		{
			throw new InvalidDataException(
				"对话清单包含空文件或无效文件大小：" + path);
		}
	}

	private static void ValidateOtherEntries(
		Dictionary<string, ZipArchiveEntry> entries,
		Dictionary<string, string> checksums,
		HashSet<string> sessionPaths)
	{
		foreach (KeyValuePair<string, ZipArchiveEntry> pair in entries)
		{
			if (string.IsNullOrEmpty(pair.Value.Name) ||
				string.Equals(pair.Key, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(pair.Key, "checksums.json", StringComparison.OrdinalIgnoreCase) ||
				sessionPaths.Contains(pair.Key))
			{
				continue;
			}
			using Stream stream = pair.Value.Open();
			ValidateHashValue(pair.Key, Sha256(stream), checksums[pair.Key]);
		}
	}

	private static bool ValidateAndRewriteSessionMeta(
		string rawPath,
		string destinationPath,
		BundleSession session,
		string sourceCwd,
		string targetCwd)
	{
		byte[] lineBytes;
		byte[] newlineBytes;
		long restOffset;
		using (FileStream input = new FileStream(
			rawPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read))
		{
			ReadFirstLine(input, out lineBytes, out newlineBytes, out restOffset);
		}
		JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
		Dictionary<string, object> root;
		try
		{
			root = serializer.DeserializeObject(DecodeUtf8(lineBytes))
				as Dictionary<string, object>;
		}
		catch (Exception ex)
		{
			throw new InvalidDataException(
				"会话首行不是有效 JSON：" + session.thread_id,
				ex);
		}
		if (root == null ||
			!root.TryGetValue("payload", out object payloadValue) ||
			!(payloadValue is Dictionary<string, object> payload))
		{
			throw new InvalidDataException(
				"会话首行缺少 session_meta payload：" + session.thread_id);
		}
		if (!root.TryGetValue("type", out object typeValue) ||
			typeValue == null ||
			!string.Equals(
				Convert.ToString(typeValue, CultureInfo.InvariantCulture),
				"session_meta",
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"会话首行不是 session_meta：" + session.thread_id);
		}
		string payloadId = Value(payload, "id");
		string payloadThreadId = Value(payload, "thread_id");
		string payloadSessionId = Value(payload, "session_id");
		string embeddedId = !string.IsNullOrWhiteSpace(payloadId)
			? payloadId
			: (!string.IsNullOrWhiteSpace(payloadThreadId) ? payloadThreadId : payloadSessionId);
		bool aliasMismatch = !string.IsNullOrWhiteSpace(payloadId) &&
			!string.Equals(payloadId, session.thread_id, StringComparison.OrdinalIgnoreCase);
		aliasMismatch |= !string.IsNullOrWhiteSpace(payloadThreadId) &&
			!string.Equals(payloadThreadId, session.thread_id, StringComparison.OrdinalIgnoreCase);
		// For subagents, session_id may intentionally be the parent Thread ID. It is
		// treated as the current ID only when id/thread_id are both absent.
		aliasMismatch |= string.IsNullOrWhiteSpace(payloadId) &&
			string.IsNullOrWhiteSpace(payloadThreadId) &&
			!string.IsNullOrWhiteSpace(payloadSessionId) &&
			!string.Equals(payloadSessionId, session.thread_id, StringComparison.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(embeddedId) || aliasMismatch)
		{
			throw new InvalidDataException(
				"会话文件中的 Thread ID 与清单不一致：" + session.thread_id);
		}
		bool changed = false;
		string originalCwd = Value(payload, "cwd");
		if (!string.IsNullOrWhiteSpace(targetCwd))
		{
			bool alreadyTarget = PathsEqual(originalCwd, targetCwd);
			bool matchesSource =
				string.IsNullOrWhiteSpace(sourceCwd) ||
				PathsEqual(originalCwd, sourceCwd);
			if (!alreadyTarget && !matchesSource)
			{
				throw new InvalidDataException(
					"会话 cwd 与指定源项目不一致，已拒绝改写：" +
					session.thread_id +
					"\n会话：" +
					originalCwd +
					"\n源项目：" +
					sourceCwd);
			}
			if (!alreadyTarget)
			{
				payload["cwd"] = targetCwd;
				changed = true;
			}
		}
		using (FileStream output = new FileStream(
			destinationPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			if (!string.IsNullOrWhiteSpace(targetCwd))
			{
				byte[] firstLineOutput = changed
					? RewriteCwdProperty(lineBytes, originalCwd, targetCwd)
					: lineBytes;
				output.Write(firstLineOutput, 0, firstLineOutput.Length);
				output.Write(newlineBytes, 0, newlineBytes.Length);
				using FileStream input = new FileStream(
					rawPath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read);
				input.Position = restOffset;
				changed |= CopyRemainingWithTurnContextCwdRewrite(
					input,
					output,
					serializer,
					originalCwd,
					sourceCwd,
					targetCwd);
			}
			else
			{
				using FileStream input = new FileStream(
					rawPath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.Read);
				input.CopyTo(output);
			}
		}
		return changed;
	}

	private static List<PlannedImport> BuildPlan(
		ValidatedBundle bundle,
		string codexHome,
		NativeBundleImportMode mode)
	{
		Dictionary<string, List<string>> existingById = FindExistingSessions(
			codexHome,
			bundle.Sessions.Select(item => item.ManifestSession.thread_id));
		List<PlannedImport> result = new List<PlannedImport>();
		HashSet<string> destinations =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (StagedSession source in bundle.Sessions)
		{
			string storageRoot;
			string expected = ResolveDestination(
				codexHome,
				source.ManifestSession,
				out storageRoot);
			List<string> matches = existingById.TryGetValue(
				source.ManifestSession.thread_id,
				out List<string> found)
				? found
				: new List<string>();
			if (File.Exists(expected) &&
				!matches.Contains(expected, StringComparer.OrdinalIgnoreCase))
			{
				string collisionId = ReadThreadId(expected);
				if (!string.Equals(
					collisionId,
					source.ManifestSession.thread_id,
					StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException(
						"导入目标已被另一条会话占用：" + expected);
				}
				matches.Add(expected);
			}
			matches = matches
				.Select(Path.GetFullPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (matches.Count > 1)
			{
				throw new InvalidDataException(
					"本机存在多个相同 Thread ID 的会话文件，无法确定合并目标：" +
					source.ManifestSession.thread_id +
					"\n" +
					string.Join("\n", matches));
			}
			string destination = matches.Count == 1 ? matches[0] : expected;
			storageRoot = ResolveStorageRootForPath(codexHome, destination);
			if (!destinations.Add(destination))
			{
				throw new InvalidDataException(
					"多个导入会话解析到了同一个目标文件：" + destination);
			}
			NativeBundleImportStatus status;
			if (!File.Exists(destination))
			{
				status = NativeBundleImportStatus.Created;
			}
			else if (mode == NativeBundleImportMode.Replace)
			{
				status = FilesHaveSameContent(destination, source.StagedPath)
					? NativeBundleImportStatus.SkippedIdentical
					: NativeBundleImportStatus.Replaced;
			}
			else
			{
				PrefixComparison comparison = CompareJsonlPrefix(
					destination,
					source.StagedPath);
				status = comparison switch
				{
					PrefixComparison.Identical =>
						NativeBundleImportStatus.SkippedIdentical,
					PrefixComparison.IncomingExtendsLocal =>
						NativeBundleImportStatus.FastForwarded,
					PrefixComparison.LocalExtendsIncoming =>
						NativeBundleImportStatus.SkippedLocalAhead,
					_ => throw new InvalidDataException(
						"会话内容已经分叉，无法安全合并：" +
						source.ManifestSession.thread_id)
				};
			}
			FileInfo existing = File.Exists(destination)
				? new FileInfo(destination)
				: null;
			result.Add(new PlannedImport
			{
				Source = source,
				StorageRoot = storageRoot,
				DestinationPath = destination,
				Status = status,
				ExistingLength = existing?.Length ?? -1L,
				ExistingLastWriteTicks = existing?.LastWriteTimeUtc.Ticks ?? 0L
			});
		}
		return result;
	}

	private static Dictionary<string, List<string>> FindExistingSessions(
		string codexHome,
		IEnumerable<string> threadIds)
	{
		HashSet<string> wanted =
			new HashSet<string>(threadIds, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, List<string>> result = wanted.ToDictionary(
			id => id,
			_ => new List<string>(),
			StringComparer.OrdinalIgnoreCase);
		foreach (string folder in new[] { "sessions", "archived_sessions" })
		{
			string root = Path.Combine(codexHome, folder);
			if (!Directory.Exists(root))
			{
				continue;
			}
			foreach (string path in EnumerateSessionFiles(root))
			{
				string embedded = ReadThreadId(path);
				if (wanted.Contains(embedded))
				{
					result[embedded].Add(Path.GetFullPath(path));
				}
			}
		}
		try
		{
			string database = WinSqliteMaintenance.FindActiveDatabase(codexHome);
			if (!string.IsNullOrWhiteSpace(database) && File.Exists(database))
			{
				foreach (DbThread thread in WinSqliteReader.ReadThreads(database))
				{
					if (thread != null && wanted.Contains(thread.Id) && !string.IsNullOrWhiteSpace(thread.RolloutPath))
					{
						string indexedPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(thread.RolloutPath));
						if (File.Exists(indexedPath))
						{
							result[thread.Id].Add(indexedPath);
						}
					}
				}
			}
		}
		catch
		{
			// Files remain authoritative when the optional desktop index is busy or old.
		}
		foreach (string id in wanted)
		{
			result[id] = result[id].Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		}
		return result;
	}

	private static IEnumerable<string> EnumerateSessionFiles(string root)
	{
		Stack<string> pending = new Stack<string>();
		pending.Push(Path.GetFullPath(root));
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			string[] files;
			try
			{
				files = Directory.GetFiles(directory);
			}
			catch
			{
				files = Array.Empty<string>();
			}
			foreach (string file in files)
			{
				bool include = false;
				try
				{
					include = (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 &&
						(file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ||
						 file.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase));
				}
				catch
				{
				}
				if (include)
				{
					yield return file;
				}
			}
			string[] children;
			try
			{
				children = Directory.GetDirectories(directory);
			}
			catch
			{
				children = Array.Empty<string>();
			}
			foreach (string child in children)
			{
				try
				{
					if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
					{
						pending.Push(child);
					}
				}
				catch
				{
				}
			}
		}
	}

	private static string ResolveDestination(
		string codexHome,
		BundleSession session,
		out string storageRoot)
	{
		string path = NormalizeEntryPath(
			session.bundle_path,
			directory: false);
		bool archivedPrefix = path.StartsWith(
			"archived_sessions/",
			StringComparison.OrdinalIgnoreCase);
		if (path.StartsWith("sessions/", StringComparison.OrdinalIgnoreCase))
		{
			path = path.Substring("sessions/".Length);
		}
		else if (archivedPrefix)
		{
			path = path.Substring("archived_sessions/".Length);
		}
		storageRoot = Path.GetFullPath(Path.Combine(
			codexHome,
			session.archived || archivedPrefix
				? "archived_sessions"
				: "sessions"));
		path = EnsureRolloutPathThreadId(path, session.thread_id);
		string relative = NormalizeWindowsRelativePath(path);
		string normalizedRoot = storageRoot.TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		string destination = Path.GetFullPath(Path.Combine(
			normalizedRoot,
			relative));
		if (!destination.StartsWith(
			normalizedRoot + Path.DirectorySeparatorChar,
			StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"对话包路径越界：" + session.bundle_path);
		}
		return destination;
	}

	private static string ResolveStorageRootForPath(
		string codexHome,
		string destination)
	{
		string fullDestination = Path.GetFullPath(destination);
		foreach (string folder in new[] { "sessions", "archived_sessions" })
		{
			string root = Path.GetFullPath(Path.Combine(codexHome, folder))
				.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (fullDestination.StartsWith(
				root + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase))
			{
				return root;
			}
		}
		throw new InvalidDataException(
			"现有会话文件不在 Codex 会话目录内：" + destination);
	}

	private static PrefixComparison CompareJsonlPrefix(
		string localPath,
		string incomingPath)
	{
		using StreamReader local = OpenJsonl(localPath);
		using StreamReader incoming = OpenJsonl(incomingPath);
		while (true)
		{
			string localLine = local.ReadLine();
			string incomingLine = incoming.ReadLine();
			if (localLine == null && incomingLine == null)
			{
				return PrefixComparison.Identical;
			}
			if (localLine == null)
			{
				return PrefixComparison.IncomingExtendsLocal;
			}
			if (incomingLine == null)
			{
				return PrefixComparison.LocalExtendsIncoming;
			}
			if (!string.Equals(localLine, incomingLine, StringComparison.Ordinal))
			{
				if (!JsonMetadataLinesEquivalent(localLine, incomingLine))
				{
					return PrefixComparison.Diverged;
				}
			}
		}
	}

	private static bool JsonMetadataLinesEquivalent(string left, string right)
	{
		if (left == null || right == null || left.Length > MaximumMetadataBytes || right.Length > MaximumMetadataBytes)
		{
			return false;
		}
		bool metadata = left.IndexOf("\"session_meta\"", StringComparison.Ordinal) >= 0 ||
			left.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0;
		if (!metadata)
		{
			return false;
		}
		try
		{
			JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
			return JsonValuesEquivalent(serializer.DeserializeObject(left), serializer.DeserializeObject(right));
		}
		catch
		{
			return false;
		}
	}

	private static bool JsonValuesEquivalent(object left, object right)
	{
		if (ReferenceEquals(left, right))
		{
			return true;
		}
		if (left == null || right == null)
		{
			return false;
		}
		if (left is Dictionary<string, object> leftDictionary &&
			right is Dictionary<string, object> rightDictionary)
		{
			if (leftDictionary.Count != rightDictionary.Count)
			{
				return false;
			}
			foreach (KeyValuePair<string, object> pair in leftDictionary)
			{
				if (!rightDictionary.TryGetValue(pair.Key, out object rightValue) ||
					!JsonValuesEquivalent(pair.Value, rightValue))
				{
					return false;
				}
			}
			return true;
		}
		if (left is object[] leftArray && right is object[] rightArray)
		{
			if (leftArray.Length != rightArray.Length)
			{
				return false;
			}
			for (int index = 0; index < leftArray.Length; index++)
			{
				if (!JsonValuesEquivalent(leftArray[index], rightArray[index]))
				{
					return false;
				}
			}
			return true;
		}
		if (IsJsonNumber(left) && IsJsonNumber(right))
		{
			try
			{
				return Convert.ToDecimal(left, CultureInfo.InvariantCulture) ==
					Convert.ToDecimal(right, CultureInfo.InvariantCulture);
			}
			catch
			{
				return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture),
					Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
			}
		}
		return left.Equals(right);
	}

	private static bool IsJsonNumber(object value)
	{
		return value is byte || value is sbyte || value is short || value is ushort ||
			value is int || value is uint || value is long || value is ulong ||
			value is float || value is double || value is decimal;
	}

	private static StreamReader OpenJsonl(string path)
	{
		return new StreamReader(
			RolloutStorage.OpenRead(path),
			new UTF8Encoding(
				encoderShouldEmitUTF8Identifier: false,
				throwOnInvalidBytes: true),
			detectEncodingFromByteOrderMarks: true,
			65536);
	}

	private static bool FilesHaveSameContent(string leftPath, string rightPath)
	{
		if (!RolloutStorage.IsCompressed(leftPath) && !RolloutStorage.IsCompressed(rightPath) &&
			new FileInfo(leftPath).Length != new FileInfo(rightPath).Length) return false;
		using Stream left = RolloutStorage.OpenRead(leftPath);
		using Stream right = RolloutStorage.OpenRead(rightPath);
		using SHA256 sha = SHA256.Create();
		return sha.ComputeHash(left).SequenceEqual(sha.ComputeHash(right));
	}

	private static void ApplyPlan(List<PlannedImport> plans)
	{
		List<AppliedChange> changes = new List<AppliedChange>();
		try
		{
			foreach (PlannedImport plan in plans.Where(item => item.Status ==
				NativeBundleImportStatus.Created ||
				item.Status == NativeBundleImportStatus.FastForwarded ||
				item.Status == NativeBundleImportStatus.Replaced))
			{
				bool existsNow = File.Exists(plan.DestinationPath);
				if (plan.Status == NativeBundleImportStatus.Created && existsNow)
				{
					throw new IOException(
						"安全检查后目标位置出现了新文件，已停止导入：" +
						plan.DestinationPath);
				}
				if (plan.Status != NativeBundleImportStatus.Created && !existsNow)
				{
					throw new IOException(
						"安全检查后原会话文件已消失，已停止导入：" +
						plan.DestinationPath);
				}
				if (existsNow)
				{
					FileInfo current = new FileInfo(plan.DestinationPath);
					if (current.Length != plan.ExistingLength ||
						current.LastWriteTimeUtc.Ticks != plan.ExistingLastWriteTicks)
					{
						throw new IOException(
							"安全检查后原会话文件发生变化，已停止导入：" +
							plan.DestinationPath);
					}
				}
				string parent = Path.GetDirectoryName(plan.DestinationPath);
				EnsureNoNestedReparsePoint(plan.StorageRoot, parent);
				Directory.CreateDirectory(parent);
				AppliedChange change = new AppliedChange
				{
					DestinationPath = plan.DestinationPath,
					Created = !existsNow
				};
				if (existsNow)
				{
					change.BackupPath = CreateTransactionBackup(
						plan.DestinationPath);
					plan.BackupPath = change.BackupPath;
				}
				changes.Add(change);
				CopyAtomically(
					plan.Source.StagedPath,
					plan.DestinationPath,
					existsNow);
				ApplyTimestamps(
					plan.DestinationPath,
					plan.Source.CreatedAtUtc,
					plan.Source.UpdatedAtUtc);
			}
		}
		catch (Exception importError)
		{
			try
			{
				RollbackAppliedChanges(changes);
			}
			catch (Exception rollbackError)
			{
				throw new AggregateException(
					"原生导入失败，而且部分会话文件无法自动恢复。",
					importError,
					rollbackError);
			}
			throw;
		}
	}

	private static string CreateTransactionBackup(string path)
	{
		long ordinal = DateTime.UtcNow.Ticks;
		string backup;
		do
		{
			backup = path +
				".ccm-txn-bak-" +
				ordinal.ToString(CultureInfo.InvariantCulture);
			ordinal++;
		}
		while (File.Exists(backup));
		File.Copy(path, backup, overwrite: false);
		File.SetCreationTimeUtc(backup, File.GetCreationTimeUtc(path));
		File.SetLastWriteTimeUtc(backup, File.GetLastWriteTimeUtc(path));
		return backup;
	}

	private static void CopyAtomically(
		string source,
		string destination,
		bool overwrite)
	{
		string temporary = destination +
			".native-import-" +
			Guid.NewGuid().ToString("N") +
			".tmp";
		try
		{
			if (RolloutStorage.IsCompressed(destination)) RolloutStorage.EncodeFile(source, temporary);
			else File.Copy(source, temporary, overwrite: false);
			if (overwrite)
			{
				File.Replace(
					temporary,
					destination,
					null,
					ignoreMetadataErrors: true);
			}
			else
			{
				File.Move(temporary, destination);
			}
		}
		finally
		{
			TryDeleteFile(temporary);
		}
	}

	private static void RollbackAppliedChanges(
		IEnumerable<AppliedChange> changes)
	{
		List<Exception> failures = new List<Exception>();
		foreach (AppliedChange change in changes.Reverse())
		{
			try
			{
				if (change.Created)
				{
					if (File.Exists(change.DestinationPath))
					{
						File.Delete(change.DestinationPath);
					}
				}
				else if (!string.IsNullOrWhiteSpace(change.BackupPath) &&
					File.Exists(change.BackupPath))
				{
					File.Copy(
						change.BackupPath,
						change.DestinationPath,
						overwrite: true);
				}
				if (!string.IsNullOrWhiteSpace(change.BackupPath) &&
					File.Exists(change.BackupPath))
				{
					File.Delete(change.BackupPath);
				}
			}
			catch (Exception ex)
			{
				failures.Add(ex);
			}
		}
		if (failures.Count > 0)
		{
			throw new AggregateException(failures);
		}
	}

	private static void ApplyTimestamps(
		string path,
		DateTime? createdAtUtc,
		DateTime? updatedAtUtc)
	{
		try
		{
			if (createdAtUtc.HasValue)
			{
				File.SetCreationTimeUtc(path, createdAtUtc.Value);
			}
			if (updatedAtUtc.HasValue)
			{
				File.SetLastWriteTimeUtc(path, updatedAtUtc.Value);
			}
		}
		catch
		{
			// Timestamps improve ordering but are not required for a valid rollout.
		}
	}

	private static void EnsureNoNestedReparsePoint(
		string storageRoot,
		string parent)
	{
		string root = Path.GetFullPath(storageRoot).TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		string current = Path.GetFullPath(parent).TrimEnd(
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar);
		if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase) &&
			!current.StartsWith(
				root + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"会话目标目录越界：" + parent);
		}
		while (current.Length >= root.Length)
		{
			if (Directory.Exists(current) &&
				(File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException(
					"会话目标目录包含重解析点，已拒绝写入：" + current);
			}
			if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
			{
				break;
			}
			current = Path.GetDirectoryName(current);
			if (string.IsNullOrWhiteSpace(current))
			{
				break;
			}
		}
	}

	private static void ExtractEntry(
		ZipArchiveEntry entry,
		string destination)
	{
		using Stream input = entry.Open();
		using FileStream output = new FileStream(
			destination,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None);
		byte[] buffer = new byte[81920];
		long written = 0L;
		while (true)
		{
			int read = input.Read(buffer, 0, buffer.Length);
			if (read == 0)
			{
				break;
			}
			written += read;
			if (written > entry.Length || written > MaximumExpandedBytes)
			{
				throw new InvalidDataException("对话包条目展开长度超过清单限制：" + entry.FullName);
			}
			output.Write(buffer, 0, read);
		}
		if (written != entry.Length)
		{
			throw new InvalidDataException("对话包条目读取不完整：" + entry.FullName);
		}
	}

	private static bool CopyRemainingWithTurnContextCwdRewrite(
		FileStream input,
		FileStream output,
		JavaScriptSerializer serializer,
		string sessionCwd,
		string sourceCwd,
		string targetCwd)
	{
		bool changed = false;
		while (ReadRawLine(
			input,
			out byte[] line,
			out byte[] newline))
		{
			byte[] outputLine = line;
			if (ContainsAscii(line, "turn_context"))
			{
				Dictionary<string, object> record;
				try
				{
					record = serializer.DeserializeObject(DecodeUtf8(line))
						as Dictionary<string, object>;
				}
				catch (Exception ex)
				{
					throw new InvalidDataException(
						"包含 turn_context 的会话记录不是有效 JSON。",
						ex);
				}
				string type = record != null &&
					record.TryGetValue("type", out object typeValue) &&
					typeValue != null
						? Convert.ToString(
							typeValue,
							CultureInfo.InvariantCulture)
						: string.Empty;
				if (string.Equals(
					type,
					"turn_context",
					StringComparison.OrdinalIgnoreCase) &&
					record.TryGetValue("payload", out object payloadValue) &&
					payloadValue is Dictionary<string, object> payload)
				{
					string cwd = Value(payload, "cwd");
					bool matchesSession = PathsEqual(cwd, sessionCwd);
					bool matchesSource =
						!string.IsNullOrWhiteSpace(sourceCwd) &&
						PathsEqual(cwd, sourceCwd);
					if (!PathsEqual(cwd, targetCwd) &&
						(matchesSession || matchesSource))
					{
						outputLine = RewriteCwdProperty(line, cwd, targetCwd);
						changed = true;
					}
				}
			}
			output.Write(outputLine, 0, outputLine.Length);
			output.Write(newline, 0, newline.Length);
		}
		return changed;
	}

	private static byte[] RewriteCwdProperty(byte[] lineBytes, string expectedCwd, string targetCwd)
	{
		string json = DecodeUtf8(lineBytes);
		JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
		string targetLiteral = serializer.Serialize(targetCwd ?? string.Empty);
		bool replaced = false;
		string rewritten = CwdPropertyPattern.Replace(json, match =>
		{
			if (replaced)
			{
				return match.Value;
			}
			string decoded;
			try
			{
				decoded = serializer.Deserialize<string>(match.Groups["value"].Value);
			}
			catch
			{
				return match.Value;
			}
			if (!PathsEqual(decoded, expectedCwd))
			{
				return match.Value;
			}
			replaced = true;
			return match.Groups["prefix"].Value + targetLiteral;
		});
		if (!replaced)
		{
			throw new InvalidDataException("会话 cwd 无法在原始 JSON 中安全定位，已停止导入。");
		}
		return new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true).GetBytes(rewritten);
	}

	private static bool ReadRawLine(
		Stream input,
		out byte[] lineBytes,
		out byte[] newlineBytes)
	{
		using MemoryStream line = new MemoryStream();
		bool readAny = false;
		bool foundNewline = false;
		while (true)
		{
			int value = input.ReadByte();
			if (value < 0)
			{
				break;
			}
			readAny = true;
			if (value == '\n')
			{
				foundNewline = true;
				break;
			}
			if (line.Length >= MaximumJsonLineBytes)
			{
				throw new InvalidDataException("会话单条 JSONL 记录超过 64 MB，已停止导入。");
			}
			line.WriteByte((byte)value);
		}
		if (!readAny)
		{
			lineBytes = Array.Empty<byte>();
			newlineBytes = Array.Empty<byte>();
			return false;
		}
		byte[] raw = line.ToArray();
		if (foundNewline &&
			raw.Length > 0 &&
			raw[raw.Length - 1] == '\r')
		{
			lineBytes = raw.Take(raw.Length - 1).ToArray();
			newlineBytes = new byte[] { 13, 10 };
		}
		else
		{
			lineBytes = raw;
			newlineBytes = foundNewline
				? new byte[] { 10 }
				: Array.Empty<byte>();
		}
		return true;
	}

	private static bool ContainsAscii(byte[] data, string value)
	{
		if (data == null ||
			string.IsNullOrEmpty(value) ||
			data.Length < value.Length)
		{
			return false;
		}
		for (int start = 0; start <= data.Length - value.Length; start++)
		{
			int index = 0;
			while (index < value.Length &&
				data[start + index] == (byte)value[index])
			{
				index++;
			}
			if (index == value.Length)
			{
				return true;
			}
		}
		return false;
	}

	private static void ReadFirstLine(
		Stream input,
		out byte[] lineBytes,
		out byte[] newlineBytes,
		out long restOffset)
	{
		using MemoryStream line = new MemoryStream();
		bool foundNewline = false;
		while (line.Length <= MaximumMetadataBytes)
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
				line.Length > MaximumMetadataBytes
					? "会话 session_meta 首行过大。"
					: "会话文件缺少首行换行符。");
		}
		byte[] raw = line.ToArray();
		if (raw.Length > 0 && raw[raw.Length - 1] == '\r')
		{
			lineBytes = raw.Take(raw.Length - 1).ToArray();
			newlineBytes = new byte[] { 13, 10 };
		}
		else
		{
			lineBytes = raw;
			newlineBytes = new byte[] { 10 };
		}
		restOffset = input.Position;
	}

	private static string ReadThreadId(string path)
	{
		try
		{
			using Stream input = RolloutStorage.OpenRead(path);
			ReadFirstLine(input, out byte[] line, out _, out _);
			Dictionary<string, object> root =
				JsonSerialization.NewSerializer().DeserializeObject(DecodeUtf8(line))
				as Dictionary<string, object>;
			if (root != null &&
				root.TryGetValue("payload", out object value) &&
				value is Dictionary<string, object> payload)
			{
				string id = Value(payload, "id");
				if (string.IsNullOrWhiteSpace(id))
				{
					id = Value(payload, "thread_id");
				}
				return string.IsNullOrWhiteSpace(id) ? Value(payload, "session_id") : id;
			}
		}
		catch
		{
		}
		return string.Empty;
	}

	private static string IdFromFileName(string path)
	{
		MatchCollection matches = ThreadIdPattern.Matches(Path.GetFileName(path) ?? string.Empty);
		return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Value;
	}

	private static string EnsureRolloutPathThreadId(string path, string threadId)
	{
		string normalized = NormalizeEntryPath(path, directory: false);
		int separator = normalized.LastIndexOf('/');
		string directory = separator < 0 ? string.Empty : normalized.Substring(0, separator + 1);
		string fileName = separator < 0 ? normalized : normalized.Substring(separator + 1);
		if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("对话文件不是 JSONL：" + path);
		}
		string stem = fileName.Substring(0, fileName.Length - ".jsonl".Length);
		stem = ThreadIdPattern.Replace(stem, string.Empty).TrimEnd('-', '_', '.', ' ');
		if (string.IsNullOrWhiteSpace(stem))
		{
			stem = "rollout";
		}
		return directory + stem + "-" + threadId + ".jsonl";
	}

	private static byte[] ReadSmallEntry(
		ZipArchiveEntry entry,
		string name)
	{
		return ReadBoundedEntry(entry, MaximumMetadataBytes, name);
	}

	private static ZipArchiveEntry RequiredEntry(
		Dictionary<string, ZipArchiveEntry> entries,
		string path)
	{
		if (!entries.TryGetValue(path, out ZipArchiveEntry entry) ||
			string.IsNullOrEmpty(entry.Name))
		{
			throw new InvalidDataException(
				"对话包缺少 " + path + "。");
		}
		return entry;
	}

	private static void ValidateHash(
		string path,
		byte[] data,
		string expected)
	{
		using SHA256 sha = SHA256.Create();
		ValidateHashValue(path, Hex(sha.ComputeHash(data)), expected);
	}

	private static void ValidateHashValue(
		string path,
		string actual,
		string expected)
	{
		if (!Sha256Pattern.IsMatch(expected ?? string.Empty) ||
			!string.Equals(
				actual,
				expected,
				StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException(
				"SHA-256 校验失败：" + path);
		}
	}

	private static string Sha256(Stream input)
	{
		using SHA256 sha = SHA256.Create();
		return Hex(sha.ComputeHash(input));
	}

	private static string Hex(byte[] bytes)
	{
		StringBuilder value = new StringBuilder(bytes.Length * 2);
		foreach (byte item in bytes)
		{
			value.Append(item.ToString(
				"x2",
				CultureInfo.InvariantCulture));
		}
		return value.ToString();
	}

	private static string NormalizeEntryPath(
		string value,
		bool directory)
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
			throw new InvalidDataException(
				"对话包包含无效路径：" + value);
		}
		string[] segments = normalized.Split('/');
		foreach (string segment in segments)
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
				throw new InvalidDataException(
					"对话包包含 Windows 不允许的路径：" + value);
			}
		}
		return normalized;
	}

	private static string NormalizeWindowsRelativePath(string value)
	{
		string normalized = NormalizeEntryPath(
			value,
			directory: false).Replace('/', Path.DirectorySeparatorChar);
		if (Path.IsPathRooted(normalized))
		{
			throw new InvalidDataException(
				"对话包包含绝对路径：" + value);
		}
		return normalized;
	}

	private static bool IsReservedDeviceName(string segment)
	{
		string name = Path.GetFileNameWithoutExtension(
			segment).ToUpperInvariant();
		if (name == "CON" ||
			name == "PRN" ||
			name == "AUX" ||
			name == "NUL")
		{
			return true;
		}
		return name.Length == 4 &&
			(name.StartsWith("COM", StringComparison.Ordinal) ||
				name.StartsWith("LPT", StringComparison.Ordinal)) &&
			name[3] >= '1' &&
			name[3] <= '9';
	}

	private static void RejectSymbolicLink(ZipArchiveEntry entry)
	{
		int unixMode = (entry.ExternalAttributes >> 16) & 61440;
		if (unixMode == 40960)
		{
			throw new InvalidDataException(
				"对话包包含符号链接，已拒绝读取：" + entry.FullName);
		}
	}

	private static string DecodeUtf8(byte[] data)
	{
		string value = new UTF8Encoding(
			encoderShouldEmitUTF8Identifier: false,
			throwOnInvalidBytes: true).GetString(data);
		return value.Length > 0 && value[0] == '\uFEFF'
			? value.Substring(1)
			: value;
	}

	private static string Value(
		IDictionary<string, object> dictionary,
		string key)
	{
		return dictionary != null &&
			dictionary.TryGetValue(key, out object value) &&
			value != null
				? Convert.ToString(value, CultureInfo.InvariantCulture)
				: string.Empty;
	}

	private static bool PathsEqual(string left, string right)
	{
		if (string.IsNullOrWhiteSpace(left) ||
			string.IsNullOrWhiteSpace(right))
		{
			return false;
		}
		try
		{
			return string.Equals(
				TextHelpers.CanonicalPath(left),
				TextHelpers.CanonicalPath(right),
				StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(
				left.Trim().TrimEnd('\\', '/').Replace('/', '\\'),
				right.Trim().TrimEnd('\\', '/').Replace('/', '\\'),
				StringComparison.OrdinalIgnoreCase);
		}
	}

	private static DateTime? ParseUtc(string value)
	{
		return DateTime.TryParse(
			value,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal |
				DateTimeStyles.AdjustToUniversal,
			out DateTime parsed)
				? parsed.ToUniversalTime()
				: null;
	}

	private static string RequiredFile(string path, string message)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new FileNotFoundException(message, path);
		}
		string fullPath = Path.GetFullPath(path);
		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException(message, fullPath);
		}
		return fullPath;
	}

	private static string RequiredDirectoryPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException(
				"Codex Home 不能为空。");
		}
		string fullPath = Path.GetFullPath(path);
		if (File.Exists(fullPath))
		{
			throw new IOException(
				"Codex Home 指向了文件：" + fullPath);
		}
		return fullPath;
	}

	private static long SafeFileLength(string path)
	{
		try
		{
			return File.Exists(path)
				? new FileInfo(path).Length
				: 0L;
		}
		catch
		{
			return 0L;
		}
	}

	private static void TryDeleteFile(string path)
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

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}
}
