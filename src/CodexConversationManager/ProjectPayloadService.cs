using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexConversationManager;

internal static class ProjectPayloadService
{
	private const int MaximumArchiveEntries = 50000;

	private const long MaximumSingleEntryBytes = 32L * 1024L * 1024L * 1024L;

	private const long MaximumExpandedBytes = 128L * 1024L * 1024L * 1024L;

	private const long CompressionRatioCheckThresholdBytes = 16L * 1024L * 1024L;

	private const long MaximumCompressionRatio = 10000L;

	private sealed class SourceFile
	{
		public string FullPath { get; set; }

		public string RelativePath { get; set; }

		public long Length { get; set; }

		public DateTime LastWriteTimeUtc { get; set; }
	}

	private sealed class SourceSnapshot
	{
		public string RootPath { get; set; }

		public List<string> Directories { get; } = new List<string>();

		public List<SourceFile> Files { get; } = new List<SourceFile>();

		public int SkippedReparsePoints { get; set; }
	}

	private sealed class ArchiveItem
	{
		public string RelativePath { get; set; }

		public bool IsDirectory { get; set; }

		public long Length { get; set; }

		public DateTimeOffset LastWriteTime { get; set; }
	}

	public static ProjectPayloadInfo CreateArchive(string sourceDirectory, string archivePath, string excludedOutputPath, Action<int, int, string> progress)
	{
		string sourceRoot = ValidateSourcePath(sourceDirectory);
		SourceSnapshot snapshot = ScanSource(sourceRoot, excludedOutputPath);
		if (File.Exists(archivePath))
		{
			File.Delete(archivePath);
		}
		string archiveParent = Path.GetDirectoryName(Path.GetFullPath(archivePath));
		if (string.IsNullOrWhiteSpace(archiveParent))
		{
			throw new InvalidOperationException("项目载荷输出路径无效。");
		}
		Directory.CreateDirectory(archiveParent);
		using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
		{
			foreach (string relativeDirectory in snapshot.Directories)
			{
				ZipArchiveEntry directoryEntry = archive.CreateEntry(ToEntryName(relativeDirectory) + "/", CompressionLevel.NoCompression);
				directoryEntry.LastWriteTime = ClampZipTime(Directory.GetLastWriteTimeUtc(Path.Combine(sourceRoot, relativeDirectory)));
			}
			int index = 0;
			foreach (SourceFile file in snapshot.Files)
			{
				index++;
				progress?.Invoke(index, snapshot.Files.Count, file.RelativePath);
				ZipArchiveEntry entry = archive.CreateEntry(ToEntryName(file.RelativePath), CompressionLevel.Optimal);
				entry.LastWriteTime = ClampZipTime(file.LastWriteTimeUtc);
				using Stream destination = entry.Open();
				using FileStream source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				source.CopyTo(destination);
			}
		}
		List<ArchiveItem> archivedItems = ReadAndValidateArchive(archivePath, sourceRoot, verifyContent: false);
		List<ArchiveItem> archivedFiles = archivedItems.Where((ArchiveItem item) => !item.IsDirectory).ToList();
		int archivedFileCount = archivedFiles.Count;
		long archivedBytes = archivedFiles.Sum((ArchiveItem item) => item.Length);
		return new ProjectPayloadInfo
		{
			archive_file = Path.GetFileName(archivePath),
			source_path = sourceRoot,
			root_name = new DirectoryInfo(sourceRoot).Name,
			file_count = archivedFileCount,
			directory_count = snapshot.Directories.Count,
			skipped_reparse_points = snapshot.SkippedReparsePoints,
			uncompressed_bytes = archivedBytes,
			sha256 = Sha256File(archivePath)
		};
	}

	public static ProjectRestorePlan InspectArchive(string archivePath, ProjectPayloadInfo payload, string targetDirectory, ProjectFileConflictMode conflictMode)
	{
		ValidatePayload(payload);
		string projectArchive = Path.GetFullPath(archivePath);
		if (!File.Exists(projectArchive))
		{
			throw new FileNotFoundException("迁移包缺少项目文件载荷。", projectArchive);
		}
		string actualHash = Sha256File(projectArchive);
		if (!string.Equals(actualHash, payload.sha256, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("项目文件载荷的 SHA-256 校验失败，迁移包可能已损坏或被修改。");
		}
		string targetRoot = ValidateTargetPath(targetDirectory, allowMissing: true);
		List<ArchiveItem> items = ReadAndValidateArchive(projectArchive, targetRoot, verifyContent: true);
		List<ArchiveItem> files = items.Where((ArchiveItem item) => !item.IsDirectory).ToList();
		List<ArchiveItem> directories = items.Where((ArchiveItem item) => item.IsDirectory).ToList();
		long totalBytes = files.Sum((ArchiveItem item) => item.Length);
		if (files.Count != payload.file_count || directories.Count != payload.directory_count || totalBytes != payload.uncompressed_bytes)
		{
			throw new InvalidDataException("项目载荷清单与压缩包内的文件、目录数量或大小不一致。");
		}
		if (Directory.Exists(targetRoot) && conflictMode == ProjectFileConflictMode.RequireEmpty && Directory.EnumerateFileSystemEntries(targetRoot).Any())
		{
			throw new IOException("目标项目目录不是空目录。请选择一个空目录，或改用“保留现有文件/覆盖并备份”策略。");
		}
		int existingFiles = 0;
		foreach (ArchiveItem directory in directories)
		{
			string destination = ResolveDestination(targetRoot, directory.RelativePath);
			if (File.Exists(destination))
			{
				throw new IOException("项目目录结构冲突：迁移包需要目录，但目标位置存在同名文件：\n" + destination);
			}
			EnsureNoNestedReparsePoint(targetRoot, destination);
		}
		foreach (ArchiveItem file in files)
		{
			string destination = ResolveDestination(targetRoot, file.RelativePath);
			if (Directory.Exists(destination))
			{
				throw new IOException("项目目录结构冲突：迁移包需要文件，但目标位置存在同名目录：\n" + destination);
			}
			EnsureNoNestedReparsePoint(targetRoot, Path.GetDirectoryName(destination));
			if (File.Exists(destination))
			{
				existingFiles++;
			}
		}
		CheckTargetDiskSpace(targetRoot, totalBytes);
		return new ProjectRestorePlan
		{
			TargetPath = targetRoot,
			FileCount = files.Count,
			DirectoryCount = directories.Count,
			ExistingFileCount = existingFiles,
			NewFileCount = files.Count - existingFiles,
			UncompressedBytes = totalBytes,
			ConflictMode = conflictMode
		};
	}

	public static ProjectRestoreResult RestoreArchive(string archivePath, ProjectPayloadInfo payload, string targetDirectory, ProjectFileConflictMode conflictMode)
	{
		ProjectRestorePlan plan = InspectArchive(archivePath, payload, targetDirectory, conflictMode);
		string temporaryRoot = Path.GetTempPath();
		CheckTemporaryDiskSpace(temporaryRoot, plan.UncompressedBytes);
		string stageRoot = Path.Combine(temporaryRoot, "codex-project-stage-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(stageRoot);
		string backupPath = string.Empty;
		Dictionary<string, string> backedExistingFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		int created = 0;
		int overwritten = 0;
		int skipped = 0;
		try
		{
			ExtractToStage(archivePath, stageRoot);
			if (conflictMode == ProjectFileConflictMode.RequireEmpty && Directory.Exists(plan.TargetPath) && Directory.EnumerateFileSystemEntries(plan.TargetPath).Any())
			{
				throw new IOException("目标项目目录在检查后出现了文件，已停止还原。");
			}
			if (conflictMode == ProjectFileConflictMode.OverwriteWithBackup && Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories).Any((string stagedFile) => File.Exists(ResolveDestination(plan.TargetPath, RelativeFromRoot(stageRoot, stagedFile)))))
			{
				backupPath = CreateConflictBackup(stageRoot, plan.TargetPath, payload, out backedExistingFiles);
			}
			Directory.CreateDirectory(plan.TargetPath);
			foreach (string stagedDirectory in Directory.GetDirectories(stageRoot, "*", SearchOption.AllDirectories).OrderBy((string path) => path.Length))
			{
				string relative = RelativeFromRoot(stageRoot, stagedDirectory);
				string destinationDirectory = ResolveDestination(plan.TargetPath, relative);
				if (File.Exists(destinationDirectory))
				{
					throw new IOException("项目目录结构发生冲突：\n" + destinationDirectory);
				}
				EnsureNoNestedReparsePoint(plan.TargetPath, Path.GetDirectoryName(destinationDirectory));
				Directory.CreateDirectory(destinationDirectory);
			}
			foreach (string stagedFile in Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase))
			{
				string relative = RelativeFromRoot(stageRoot, stagedFile);
				string destination = ResolveDestination(plan.TargetPath, relative);
				string parent = Path.GetDirectoryName(destination);
				EnsureNoNestedReparsePoint(plan.TargetPath, parent);
				Directory.CreateDirectory(parent);
				bool exists = File.Exists(destination);
				if (exists && conflictMode == ProjectFileConflictMode.SkipExisting)
				{
					skipped++;
					continue;
				}
				if (exists && conflictMode == ProjectFileConflictMode.RequireEmpty)
				{
					throw new IOException("目标项目目录不再为空，已停止还原：\n" + destination);
				}
				if (exists && conflictMode == ProjectFileConflictMode.OverwriteWithBackup && (!backedExistingFiles.TryGetValue(relative, out string backedHash) || backedHash != Sha256File(destination)))
				{
					throw new IOException("安全检查后出现了新的同名文件，为避免无备份覆盖，已停止还原：\n" + destination);
				}
				CopyFileAtomically(stagedFile, destination, exists, exists ? backedExistingFiles[relative] : null);
				if (exists)
				{
					overwritten++;
				}
				else
				{
					created++;
				}
			}
			return new ProjectRestoreResult
			{
				TargetPath = plan.TargetPath,
				CreatedFileCount = created,
				OverwrittenFileCount = overwritten,
				SkippedFileCount = skipped,
				BackupPath = backupPath
			};
		}
		catch (Exception ex)
		{
			string backupNote = string.IsNullOrWhiteSpace(backupPath) ? string.Empty : "\n\n已覆盖文件的备份位于：\n" + backupPath;
			throw new InvalidOperationException("项目文件还原未完成：" + ex.Message + backupNote, ex);
		}
		finally
		{
			TryDeleteTempDirectory(stageRoot);
		}
	}

	public static string ResolvePayloadArchivePath(string extractedPackRoot, ProjectPayloadInfo payload)
	{
		ValidatePayload(payload);
		string root = Path.GetFullPath(extractedPackRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(extractedPackRoot, payload.archive_file));
		if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("项目载荷路径越界，已拒绝读取。");
		}
		return candidate;
	}

	public static string FormatBytes(long bytes)
	{
		if (bytes >= 1073741824L)
		{
			return (bytes / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
		}
		if (bytes >= 1048576L)
		{
			return (bytes / 1048576.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
		}
		if (bytes >= 1024L)
		{
			return (bytes / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " KB";
		}
		return bytes.ToString(CultureInfo.InvariantCulture) + " B";
	}

	public static string Sha256File(string path)
	{
		using SHA256 sha = SHA256.Create();
		using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		byte[] hash = sha.ComputeHash(input);
		StringBuilder text = new StringBuilder(hash.Length * 2);
		foreach (byte value in hash)
		{
			text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
		}
		return text.ToString();
	}

	private static SourceSnapshot ScanSource(string sourceRoot, string excludedOutputPath)
	{
		SourceSnapshot snapshot = new SourceSnapshot { RootPath = sourceRoot };
		long totalBytes = 0L;
		string excluded = NormalizeOptionalPath(excludedOutputPath);
		Stack<string> pending = new Stack<string>();
		pending.Push(sourceRoot);
		while (pending.Count > 0)
		{
			string current = pending.Pop();
			foreach (string directory in Directory.GetDirectories(current).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase))
			{
				EnsureWithin(directory, sourceRoot, "项目目录枚举越界。");
				FileAttributes attributes = File.GetAttributes(directory);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					snapshot.SkippedReparsePoints++;
					continue;
				}
				string relative = RelativeFromRoot(sourceRoot, directory);
				EnsureSourceEntryCapacity(snapshot);
				snapshot.Directories.Add(relative);
				pending.Push(directory);
			}
			foreach (string filePath in Directory.GetFiles(current).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase))
			{
				EnsureWithin(filePath, sourceRoot, "项目文件枚举越界。");
				if (!string.IsNullOrWhiteSpace(excluded) && string.Equals(TextHelpers.CanonicalPath(filePath), TextHelpers.CanonicalPath(excluded), StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				FileAttributes attributes = File.GetAttributes(filePath);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					snapshot.SkippedReparsePoints++;
					continue;
				}
				FileInfo info = new FileInfo(filePath);
				if (info.Length < 0L || info.Length > MaximumSingleEntryBytes)
				{
					throw new InvalidOperationException("项目中单个文件超过 32 GB，无法加入备份：\n" + filePath);
				}
				if (totalBytes > MaximumExpandedBytes - info.Length)
				{
					throw new InvalidOperationException("项目文件总大小超过 128 GB，无法创建备份。");
				}
				EnsureSourceEntryCapacity(snapshot);
				totalBytes += info.Length;
				snapshot.Files.Add(new SourceFile
				{
					FullPath = filePath,
					RelativePath = RelativeFromRoot(sourceRoot, filePath),
					Length = info.Length,
					LastWriteTimeUtc = info.LastWriteTimeUtc
				});
			}
		}
		snapshot.Directories.Sort(StringComparer.OrdinalIgnoreCase);
		snapshot.Files.Sort((SourceFile left, SourceFile right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
		return snapshot;
	}

	private static void EnsureSourceEntryCapacity(SourceSnapshot snapshot)
	{
		if ((long)snapshot.Directories.Count + snapshot.Files.Count >= MaximumArchiveEntries)
		{
			throw new InvalidOperationException("项目文件和目录总数超过 50000 个，无法创建备份。");
		}
	}

	private static List<ArchiveItem> ReadAndValidateArchive(string archivePath, string targetRoot, bool verifyContent)
	{
		using ZipArchive archive = ZipFile.OpenRead(archivePath);
		return ReadAndValidateArchive(archive, targetRoot, verifyContent);
	}

	private static List<ArchiveItem> ReadAndValidateArchive(ZipArchive archive, string targetRoot, bool verifyContent)
	{
		if (archive.Entries.Count > MaximumArchiveEntries)
		{
			throw new InvalidDataException("项目载荷文件和目录数量超过 50000 个，已停止读取。");
		}
		List<ArchiveItem> items = new List<ArchiveItem>();
		HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		long declaredBytes = 0L;
		long actualBytes = 0L;
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			RejectSymbolicLink(entry);
			bool isDirectory = string.IsNullOrEmpty(entry.Name);
			string relative = NormalizeRelativeArchivePath(entry.FullName, isDirectory);
			if (isDirectory && entry.Length != 0L)
			{
				throw new InvalidDataException("项目载荷目录条目包含数据，已拒绝读取：" + entry.FullName);
			}
			if (entry.Length < 0L || entry.CompressedLength < 0L || (!isDirectory && entry.Length > MaximumSingleEntryBytes))
			{
				throw new InvalidDataException("项目载荷单个文件展开大小超过 32 GB，已停止读取：" + entry.FullName);
			}
			if (declaredBytes > MaximumExpandedBytes - entry.Length)
			{
				throw new InvalidDataException("项目载荷展开大小无效或超过 128 GB，已停止读取。");
			}
			declaredBytes += entry.Length;
			if (!isDirectory && HasAbnormalCompressionRatio(entry))
			{
				throw new InvalidDataException("项目载荷包含异常压缩比文件，已停止读取：" + entry.FullName);
			}
			if (!names.Add(relative))
			{
				throw new InvalidDataException("项目载荷包含重复路径：" + relative);
			}
			ResolveDestination(targetRoot, relative);
			if (verifyContent && !isDirectory)
			{
				VerifyEntryContent(entry, ref actualBytes);
			}
			items.Add(new ArchiveItem
			{
				RelativePath = relative,
				IsDirectory = isDirectory,
				Length = isDirectory ? 0L : entry.Length,
				LastWriteTime = entry.LastWriteTime
			});
		}
		if (verifyContent && actualBytes != declaredBytes)
		{
			throw new InvalidDataException("项目载荷实际展开大小与压缩包记录不一致，已停止读取。");
		}
		ValidateArchiveStructure(items);
		return items;
	}

	private static bool HasAbnormalCompressionRatio(ZipArchiveEntry entry)
	{
		if (entry.Length < CompressionRatioCheckThresholdBytes)
		{
			return false;
		}
		if (entry.CompressedLength == 0L)
		{
			return true;
		}
		if (entry.CompressedLength > entry.Length || entry.CompressedLength > long.MaxValue / MaximumCompressionRatio)
		{
			return false;
		}
		return entry.Length > entry.CompressedLength * MaximumCompressionRatio;
	}

	private static void VerifyEntryContent(ZipArchiveEntry entry, ref long totalRead)
	{
		long entryRead = 0L;
		using Stream stream = entry.Open();
		byte[] buffer = new byte[81920];
		while (true)
		{
			int read = stream.Read(buffer, 0, buffer.Length);
			if (read == 0)
			{
				break;
			}
			if (entryRead > entry.Length - read || entryRead > MaximumSingleEntryBytes - read || totalRead > MaximumExpandedBytes - read)
			{
				throw new InvalidDataException("项目载荷条目实际展开长度超过限制，已停止读取：" + entry.FullName);
			}
			entryRead += read;
			totalRead += read;
		}
		if (entryRead != entry.Length)
		{
			throw new InvalidDataException("项目载荷条目读取不完整：" + entry.FullName);
		}
	}

	private static void ValidateArchiveStructure(IList<ArchiveItem> items)
	{
		HashSet<string> filePaths = new HashSet<string>(items.Where((ArchiveItem item) => !item.IsDirectory).Select((ArchiveItem item) => item.RelativePath), StringComparer.OrdinalIgnoreCase);
		foreach (ArchiveItem item in items)
		{
			string parent = Path.GetDirectoryName(item.RelativePath);
			while (!string.IsNullOrWhiteSpace(parent))
			{
				if (filePaths.Contains(parent))
				{
					throw new InvalidDataException("项目载荷同时把路径作为文件和上级目录，已拒绝还原：" + parent);
				}
				parent = Path.GetDirectoryName(parent);
			}
		}
	}

	private static void ExtractToStage(string archivePath, string stageRoot)
	{
		using ZipArchive archive = ZipFile.OpenRead(archivePath);
		List<ArchiveItem> items = ReadAndValidateArchive(archive, stageRoot, verifyContent: false);
		long totalExpected = items.Where((ArchiveItem item) => !item.IsDirectory).Sum((ArchiveItem item) => item.Length);
		long totalWritten = 0L;
		for (int index = 0; index < archive.Entries.Count; index++)
		{
			ZipArchiveEntry entry = archive.Entries[index];
			ArchiveItem item = items[index];
			string destination = ResolveDestination(stageRoot, item.RelativePath);
			if (item.IsDirectory)
			{
				Directory.CreateDirectory(destination);
				EnsureNoNestedReparsePoint(stageRoot, destination);
				continue;
			}
			string parent = Path.GetDirectoryName(destination);
			EnsureNoNestedReparsePoint(stageRoot, parent);
			Directory.CreateDirectory(parent);
			EnsureNoNestedReparsePoint(stageRoot, parent);
			ExtractEntryCounted(entry, destination, item.Length, item.LastWriteTime, ref totalWritten);
		}
		if (totalWritten != totalExpected)
		{
			throw new InvalidDataException("项目载荷实际展开大小与压缩包记录不一致，已停止读取。");
		}
	}

	private static void ExtractEntryCounted(ZipArchiveEntry entry, string destination, long expectedLength, DateTimeOffset lastWriteTime, ref long totalWritten)
	{
		long entryWritten = 0L;
		try
		{
			using (Stream source = entry.Open())
			using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				byte[] buffer = new byte[81920];
				while (true)
				{
					int read = source.Read(buffer, 0, buffer.Length);
					if (read == 0)
					{
						break;
					}
					if (entryWritten > expectedLength - read || entryWritten > MaximumSingleEntryBytes - read || totalWritten > MaximumExpandedBytes - read)
					{
						throw new InvalidDataException("项目载荷条目实际展开长度超过限制，已停止解压：" + entry.FullName);
					}
					entryWritten += read;
					totalWritten += read;
					output.Write(buffer, 0, read);
				}
			}
			if (entryWritten != expectedLength)
			{
				throw new InvalidDataException("项目载荷条目读取不完整：" + entry.FullName);
			}
			File.SetLastWriteTimeUtc(destination, lastWriteTime.UtcDateTime);
		}
		catch
		{
			TryDeleteFile(destination);
			throw;
		}
	}

	private static string CreateConflictBackup(string stageRoot, string targetRoot, ProjectPayloadInfo payload, out Dictionary<string, string> backedFiles)
	{
		backedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string backupRoot = Path.Combine(BackendContext.Home, "conversation-migrator-project-backups", DateTime.Now.ToString("yyyy-MM-dd"));
		Directory.CreateDirectory(backupRoot);
		string baseName = TextHelpers.SafeFileName(string.IsNullOrWhiteSpace(payload.root_name) ? "project" : payload.root_name);
		string backupPath = Path.Combine(backupRoot, DateTime.Now.ToString("HHmmssfff") + "-" + baseName + ".zip");
		string temporaryBackup = backupPath + ".tmp";
		try
		{
			using (ZipArchive backup = ZipFile.Open(temporaryBackup, ZipArchiveMode.Create))
			{
				foreach (string stagedFile in Directory.GetFiles(stageRoot, "*", SearchOption.AllDirectories))
				{
					string relative = RelativeFromRoot(stageRoot, stagedFile);
					string existing = ResolveDestination(targetRoot, relative);
					if (File.Exists(existing))
					{
						ZipArchiveEntry entry = backup.CreateEntry(ToEntryName(relative), CompressionLevel.Optimal);
						entry.LastWriteTime = ClampZipTime(File.GetLastWriteTimeUtc(existing));
						using Stream destination = entry.Open();
						using FileStream source = new FileStream(existing, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
						source.CopyTo(destination);
						backedFiles.Add(relative, string.Empty);
					}
				}
				Dictionary<string, object> restoreInfo = new Dictionary<string, object>
				{
					{ "target_project", targetRoot },
					{ "created_at", DateTimeOffset.Now.ToString("o") },
					{ "source_project", payload.source_path ?? string.Empty },
					{ "payload_sha256", payload.sha256 ?? string.Empty }
				};
				ZipArchiveEntry infoEntry = backup.CreateEntry("__codex_migrator_restore_info__.json", CompressionLevel.Optimal);
				using StreamWriter writer = new StreamWriter(infoEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				writer.Write(JsonSerialization.NewSerializer().Serialize(restoreInfo));
			}
			using (ZipArchive verified = ZipFile.OpenRead(temporaryBackup))
				foreach (string relative in backedFiles.Keys.ToArray())
				{
					using Stream input = verified.GetEntry(ToEntryName(relative)).Open();
					using SHA256 sha = SHA256.Create();
					backedFiles[relative] = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
				}
			File.Move(temporaryBackup, backupPath);
			return backupPath;
		}
		catch
		{
			try
			{
				if (File.Exists(temporaryBackup))
				{
					File.Delete(temporaryBackup);
				}
			}
			catch
			{
			}
			throw;
		}
	}

	internal static Action<string> BeforeReplaceForTest { get; set; }

	internal static void CopyFileAtomically(string source, string destination, bool overwrite, string backedHash)
	{
		string temporary = destination + ".codex-import-" + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.Copy(source, temporary, overwrite: false);
			if (overwrite)
			{
				if (!File.Exists(destination))
				{
					throw new IOException("准备覆盖的目标文件在还原期间消失，已停止写入：\n" + destination);
				}
				// Capture the actual replaced bytes, including edits made after the ZIP backup.
                string captured = Path.Combine(Path.GetDirectoryName(destination), Guid.NewGuid().ToString("N") + ".codex-preserved");
                BeforeReplaceForTest?.Invoke(destination);
                File.Replace(temporary, destination, captured, ignoreMetadataErrors: true);
                if (Sha256File(captured) != backedHash)
                    throw new IOException("目标文件在备份后发生变化。被替换的最新内容已保留，请手动恢复：\n" + captured);
                File.Delete(captured);
			}
			else
			{
				File.Move(temporary, destination);
			}
			File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
		}
		finally
		{
			try
			{
				if (File.Exists(temporary))
				{
					File.Delete(temporary);
				}
			}
			catch
			{
			}
		}
	}

	private static string ValidateSourcePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("项目目录为空。");
		}
		string fullPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(path));
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("项目目录不存在：\n" + fullPath);
		}
		RejectDangerousRoot(fullPath, "备份");
		return fullPath;
	}

	private static string ValidateTargetPath(string path, bool allowMissing)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("请选择新电脑上的项目目标目录。");
		}
		string fullPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(path));
		if (File.Exists(fullPath))
		{
			throw new IOException("项目目标路径是一个文件，不是目录：\n" + fullPath);
		}
		if (!allowMissing && !Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("项目目标目录不存在：\n" + fullPath);
		}
		RejectDangerousRoot(fullPath, "还原");
		string codexHome = Path.GetFullPath(BackendContext.Home);
		if (TextHelpers.IsWithin(fullPath, codexHome) || TextHelpers.IsWithin(codexHome, fullPath))
		{
			throw new InvalidOperationException("项目目标目录不能与 Codex 配置目录重叠。");
		}
		string applicationDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
		string otherHome = Path.GetFullPath(BackendContext.IsDsh ? CodexCatalog.ResolveCodexHome() : DshService.Home);
		if (TextHelpers.IsWithin(fullPath, otherHome) || TextHelpers.IsWithin(otherHome, fullPath))
			throw new InvalidOperationException("项目目标目录不能与另一数据源的配置目录重叠。");
		if (TextHelpers.IsWithin(applicationDirectory, fullPath) || TextHelpers.IsWithin(fullPath, applicationDirectory))
		{
			throw new InvalidOperationException("项目目标目录不能与当前迁移工具目录重叠。");
		}
		return fullPath;
	}

	private static void RejectDangerousRoot(string fullPath, string action)
	{
		string driveRoot = Path.GetPathRoot(fullPath);
		if (string.Equals(TextHelpers.CanonicalPath(fullPath), TextHelpers.CanonicalPath(driveRoot), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("不能把磁盘根目录作为项目" + action + "目录。");
		}
		Environment.SpecialFolder[] protectedFolders = new Environment.SpecialFolder[7]
		{
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolder.Desktop,
			Environment.SpecialFolder.MyDocuments,
			Environment.SpecialFolder.Windows,
			Environment.SpecialFolder.System,
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86
		};
		foreach (Environment.SpecialFolder folder in protectedFolders)
		{
			string protectedPath = Environment.GetFolderPath(folder);
			if (!string.IsNullOrWhiteSpace(protectedPath) && string.Equals(TextHelpers.CanonicalPath(fullPath), TextHelpers.CanonicalPath(protectedPath), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("不能把系统或用户根目录作为项目" + action + "目录：\n" + fullPath);
			}
		}
	}

	private static void ValidatePayload(ProjectPayloadInfo payload)
	{
		if (payload == null || string.IsNullOrWhiteSpace(payload.archive_file) || string.IsNullOrWhiteSpace(payload.sha256))
		{
			throw new InvalidDataException("迁移包的项目载荷清单不完整。");
		}
		if (payload.file_count < 0 || payload.directory_count < 0 || payload.uncompressed_bytes < 0L ||
			payload.file_count > MaximumArchiveEntries || payload.directory_count > MaximumArchiveEntries ||
			(long)payload.file_count + payload.directory_count > MaximumArchiveEntries ||
			payload.uncompressed_bytes > MaximumExpandedBytes)
		{
			throw new InvalidDataException("迁移包的项目载荷统计无效。");
		}
	}

	private static string NormalizeRelativeArchivePath(string value, bool isDirectory)
	{
		if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
		{
			throw new InvalidDataException("项目载荷包含无效路径：" + value);
		}
		string normalized = (value ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
		if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized))
		{
			throw new InvalidDataException("项目载荷包含无效路径：" + value);
		}
		string[] segments = normalized.Split(new char[1] { '\\' }, StringSplitOptions.None);
		foreach (string segment in segments)
		{
			if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsReservedDeviceName(segment))
			{
				throw new InvalidDataException("项目载荷包含 Windows 不允许的路径：" + value);
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
		if (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)))
		{
			return name[3] >= '1' && name[3] <= '9';
		}
		return false;
	}

	private static string ResolveDestination(string root, string relative)
	{
		string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string destination = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
		string prefix = normalizedRoot + Path.DirectorySeparatorChar;
		if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("项目载荷路径越界：" + relative);
		}
		return destination;
	}

	private static void EnsureNoNestedReparsePoint(string targetRoot, string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(targetRoot))
		{
			return;
		}
		string root = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar);
		string current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
		while (current.Length > root.Length && current.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
		{
			if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
			{
				throw new IOException("目标项目目录内包含重解析点，已拒绝向其中写入：\n" + current);
			}
			current = Path.GetDirectoryName(current);
		}
	}

	private static void RejectSymbolicLink(ZipArchiveEntry entry)
	{
		int unixMode = (entry.ExternalAttributes >> 16) & 61440;
		bool windowsReparsePoint = (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
		if (unixMode == 40960 || windowsReparsePoint)
		{
			throw new InvalidDataException("项目载荷包含符号链接，已拒绝还原：" + entry.FullName);
		}
	}

	private static void CheckTargetDiskSpace(string targetRoot, long requiredBytes)
	{
		try
		{
			string root = Path.GetPathRoot(targetRoot);
			DriveInfo drive = new DriveInfo(root);
			long conservativeNeed = requiredBytes + Math.Min(requiredBytes / 10L, 1073741824L);
			if (drive.IsReady && drive.AvailableFreeSpace < conservativeNeed)
			{
				throw new IOException("目标磁盘可用空间不足。至少需要约 " + FormatBytes(conservativeNeed) + "，当前可用 " + FormatBytes(drive.AvailableFreeSpace) + "。");
			}
		}
		catch (IOException)
		{
			throw;
		}
		catch
		{
		}
	}

	private static void CheckTemporaryDiskSpace(string temporaryRoot, long expandedBytes)
	{
		long overhead = Math.Min(268435456L, Math.Max(16777216L, expandedBytes / 20L));
		long required = expandedBytes > long.MaxValue - overhead ? long.MaxValue : expandedBytes + overhead;
		try
		{
			string root = Path.GetPathRoot(Path.GetFullPath(temporaryRoot));
			DriveInfo drive = new DriveInfo(root);
			if (drive.IsReady && drive.AvailableFreeSpace < required)
			{
				throw new IOException("系统临时盘空间不足，无法安全解压项目载荷。至少需要约 " + FormatBytes(required) + "，当前可用 " + FormatBytes(drive.AvailableFreeSpace) + "。");
			}
		}
		catch (IOException)
		{
			throw;
		}
		catch
		{
			// Some virtual or removable volumes do not report capacity. Counted
			// extraction and the hard expanded-size limits still apply.
		}
	}

	private static void EnsureWithin(string candidate, string root, string message)
	{
		if (!TextHelpers.IsWithin(candidate, root))
		{
			throw new InvalidOperationException(message);
		}
	}

	private static string RelativeFromRoot(string root, string path)
	{
		string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(path);
		if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("路径不在项目目录内：" + path);
		}
		return fullPath.Substring(prefix.Length);
	}

	private static string ToEntryName(string relativePath)
	{
		return relativePath.Replace('\\', '/');
	}

	private static DateTimeOffset ClampZipTime(DateTime dateTimeUtc)
	{
		DateTime utc = dateTimeUtc.Kind == DateTimeKind.Utc ? dateTimeUtc : dateTimeUtc.ToUniversalTime();
		if (utc.Year < 1980)
		{
			utc = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		}
		if (utc.Year > 2107)
		{
			utc = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
		}
		return new DateTimeOffset(utc);
	}

	private static string NormalizeOptionalPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return string.Empty;
		}
		try
		{
			return Path.GetFullPath(TextHelpers.StripExtendedPrefix(path));
		}
		catch
		{
			return string.Empty;
		}
	}

	private static void TryDeleteTempDirectory(string path)
	{
		try
		{
			string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string fullPath = Path.GetFullPath(path);
			if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
			{
				Directory.Delete(fullPath, recursive: true);
			}
		}
		catch
		{
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
}
