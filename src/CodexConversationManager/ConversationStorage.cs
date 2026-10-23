using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

internal static class ConversationStorage
{
	private const string TrashDirectoryName = "conversation-migrator-trash";

	public static string TrashRoot => Path.GetFullPath(Path.Combine(CodexCatalog.ResolveCodexHome(), TrashDirectoryName));

	public static DeletedSessionResult MoveToTrash(SessionInfo session)
	{
		return MoveToTrash(session, ResolveProjectPath(session, null), new SessionInfo[1] { session });
	}

	public static DeletedSessionResult MoveToTrash(SessionInfo session, string projectPath)
	{
		return MoveToTrash(session, projectPath, new SessionInfo[1] { session });
	}

	public static DeletedSessionResult MoveToTrash(SessionInfo session, string projectPath, IEnumerable<SessionInfo> affectedSessions)
	{
		string codexHome = CodexCatalog.ResolveCodexHome();
		List<DeletionTarget> targets = PrepareDeletionTargets(session, projectPath, affectedSessions);
		CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
		string trashRoot = TrashRoot;
		string datedDirectory = Path.Combine(trashRoot, DateTime.Now.ToString("yyyy-MM-dd"));
		Directory.CreateDirectory(datedDirectory);
		List<StagedTrashCopy> stagedCopies = new List<StagedTrashCopy>();
		try
		{
			foreach (DeletionTarget target in targets)
			{
				string fileName = Guid.NewGuid().ToString("N") + (RolloutStorage.IsCompressed(target.SourcePath) ? ".jsonl.zst" : ".jsonl");
				string backupPath = UniqueFilePath(datedDirectory, fileName);
				if (!TextHelpers.IsWithin(backupPath, trashRoot))
				{
					throw new InvalidOperationException("安全校验失败：备份目录越界。");
				}
				StagedTrashCopy staged = new StagedTrashCopy
				{
					Target = target,
					BackupPath = backupPath,
					SidecarPath = backupPath + ".delete-info.json"
				};
				staged.Metadata = CreateTrashMetadata(target, staged.BackupPath);
				File.Copy(target.SourcePath, staged.BackupPath);
				if (ProjectPayloadService.Sha256File(target.SourcePath) != ProjectPayloadService.Sha256File(staged.BackupPath))
					throw new IOException("会话在备份过程中发生变化，未执行删除。");
				stagedCopies.Add(staged);
				WriteMetadata(staged.SidecarPath, staged.Metadata);
			}
			OfficialThreadDeletionResult officialDeletion = CodexAppServerThreadDeletion.DeleteThread(codexHome, session.ThreadId);
			foreach (StagedTrashCopy staged in stagedCopies)
			{
				staged.Metadata["official_delete"] = true;
				staged.Metadata["official_delete_protocol"] = "thread/delete";
				WriteMetadata(staged.SidecarPath, staged.Metadata);
			}
			ThreadIndexRemovalResult indexRemoval;
			DesktopCatalogRemovalResult catalogRemoval;
			DesktopThreadRemovalResult desktopRemoval;
			RemoveThreadVisibility(codexHome, targets.Select((DeletionTarget target) => target.Session.ThreadId), out indexRemoval, out catalogRemoval, out desktopRemoval);
			foreach (DeletionTarget target in targets)
			{
				if (File.Exists(target.SourcePath))
				{
					File.Delete(target.SourcePath);
				}
				if (File.Exists(target.SourcePath))
				{
					throw new IOException("Codex 官方删除已返回成功，但原会话文件仍存在：\n" + target.SourcePath);
				}
			}
			foreach (StagedTrashCopy staged in stagedCopies)
			{
				staged.Metadata["index_backup_path"] = indexRemoval.BackupPath ?? string.Empty;
				staged.Metadata["desktop_catalog_backup_path"] = catalogRemoval.BackupPath ?? string.Empty;
				staged.Metadata["desktop_state_backup_path"] = desktopRemoval.BackupPath ?? string.Empty;
				WriteMetadata(staged.SidecarPath, staged.Metadata);
			}
		}
		catch (Exception operationError)
		{
			// Failure/timeout does not prove thread/delete had no side effects.
			// Never discard recovery copies, including when staging itself failed.
			throw new InvalidOperationException("删除未完成或结果不明确。所有已创建的回收站副本均已保留，请刷新并核对后恢复。备份目录：\n" + datedDirectory, operationError);
		}

		StagedTrashCopy rootCopy = stagedCopies.First((StagedTrashCopy staged) => string.Equals(staged.Target.Session.ThreadId, session.ThreadId, StringComparison.OrdinalIgnoreCase));
		return new DeletedSessionResult
		{
			OriginalPath = rootCopy.Target.SourcePath,
			BackupPath = rootCopy.BackupPath,
			BackupPaths = stagedCopies.Select((StagedTrashCopy staged) => staged.BackupPath).ToList(),
			PermanentlyDeleted = false,
			AffectedConversationCount = targets.Count
		};
	}

	public static DeletedSessionResult DeletePermanently(SessionInfo session)
	{
		return DeletePermanently(session, new SessionInfo[1] { session });
	}

	public static DeletedSessionResult DeletePermanently(SessionInfo session, IEnumerable<SessionInfo> affectedSessions)
	{
		string codexHome = CodexCatalog.ResolveCodexHome();
		List<DeletionTarget> targets = PrepareDeletionTargets(session, ResolveProjectPath(session, null), affectedSessions);
		CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
		CodexAppServerThreadDeletion.DeleteThread(codexHome, session.ThreadId);
		RemoveThreadVisibility(codexHome, targets.Select((DeletionTarget target) => target.Session.ThreadId), out _, out _, out _);
		foreach (DeletionTarget target in targets)
		{
			if (File.Exists(target.SourcePath))
			{
				File.Delete(target.SourcePath);
			}
			if (File.Exists(target.SourcePath))
			{
				throw new IOException("Codex 官方删除已返回成功，但原会话文件仍存在：\n" + target.SourcePath);
			}
		}
		return new DeletedSessionResult
		{
			OriginalPath = targets[0].SourcePath,
			BackupPath = string.Empty,
			PermanentlyDeleted = true,
			AffectedConversationCount = targets.Count
		};
	}

	public static List<TrashSessionInfo> ReadTrash()
	{
		string trashRoot = TrashRoot;
		List<TrashSessionInfo> items = new List<TrashSessionInfo>();
		if (!Directory.Exists(trashRoot))
		{
			return items;
		}
		foreach (string sidecarPath in Directory.GetFiles(trashRoot, "*.delete-info.json", SearchOption.AllDirectories))
		{
			try
			{
				if (!TextHelpers.IsWithin(sidecarPath, trashRoot))
				{
					continue;
				}
				Dictionary<string, object> metadata = ReadMetadata(sidecarPath);
				string backupPath = Value(metadata, "backup_path");
				if (string.IsNullOrWhiteSpace(backupPath))
				{
					backupPath = sidecarPath.Substring(0, sidecarPath.Length - ".delete-info.json".Length);
				}
				backupPath = ValidateTrashPath(backupPath);
				if (!File.Exists(backupPath))
				{
					continue;
				}
				string projectPath = NormalizeOptionalPath(Value(metadata, "project_path"));
				if (string.IsNullOrWhiteSpace(projectPath))
				{
					projectPath = ReadProjectPathFromSession(backupPath);
				}
				DateTimeOffset deletedAt;
				if (!DateTimeOffset.TryParse(Value(metadata, "deleted_at"), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out deletedAt))
				{
					deletedAt = new DateTimeOffset(File.GetLastWriteTime(backupPath));
				}
				long sizeBytes;
				if (!long.TryParse(Value(metadata, "size_bytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out sizeBytes) || sizeBytes < 0L)
				{
					sizeBytes = new FileInfo(backupPath).Length;
				}
				items.Add(new TrashSessionInfo
				{
					ThreadId = Value(metadata, "thread_id"),
					Title = Value(metadata, "title"),
					OriginalPath = Value(metadata, "original_path"),
					BackupPath = backupPath,
					SidecarPath = sidecarPath,
					ProjectPath = projectPath,
					ProjectDeleteMode = Value(metadata, "project_delete_mode"),
					DeletedAt = deletedAt,
					SizeBytes = sizeBytes,
					Preview = Value(metadata, "preview"),
					Source = Value(metadata, "source"),
					ModelProvider = Value(metadata, "model_provider"),
					CliVersion = Value(metadata, "cli_version"),
					CreatedAt = Value(metadata, "created_at"),
					UpdatedAt = Value(metadata, "updated_at"),
					Archived = BoolValue(metadata, "archived"),
					Compressed = BoolValue(metadata, "compressed"),
					IsSubagent = BoolValue(metadata, "is_subagent"),
					ParentThreadId = Value(metadata, "parent_thread_id")
				});
			}
			catch
			{
			}
		}
		return items.OrderByDescending((TrashSessionInfo item) => item.DeletedAt).ToList();
	}

	public static void Restore(TrashSessionInfo item)
	{
		if (item == null)
		{
			throw new ArgumentNullException("item");
		}
		string backupPath = ValidateTrashPath(item.BackupPath);
		string sidecarPath = ValidateTrashPath(item.SidecarPath);
		if (!File.Exists(backupPath))
		{
			throw new FileNotFoundException("回收站中的会话备份已不存在。", backupPath);
		}
		string codexHome = CodexCatalog.ResolveCodexHome();
		CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
		string originalPath = ValidateSessionTargetPath(item.OriginalPath);
		if (RolloutStorage.ExistsInEitherEncoding(originalPath))
		{
			throw new IOException("原位置已存在同名会话，已拒绝覆盖：\n" + originalPath);
		}
		EnsureRestoreIdentityAvailable(codexHome, item.ThreadId);
		string parent = Path.GetDirectoryName(originalPath);
		if (string.IsNullOrWhiteSpace(parent))
		{
			throw new InvalidOperationException("原会话路径无效。");
		}
		var restoredMetadata = TargetedThreadIndexer.ReadMetadataForIndexing(backupPath, item.Title, item.Preview);
		if (!string.Equals(restoredMetadata.Id, item.ThreadId, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("回收站备份的会话 ID 与记录不一致，未恢复。");
		Directory.CreateDirectory(parent);
		File.Move(backupPath, originalPath);
		try
		{
			SessionInfo session = SessionFromTrash(item, originalPath);
			ThreadIndexMetadata metadata = restoredMetadata;
			RestoreThreadVisibility(codexHome, metadata, originalPath);
			if (File.Exists(sidecarPath))
			{
				File.Delete(sidecarPath);
			}
		}
		catch (Exception restoreError)
		{
			try
			{
				RemoveThreadVisibility(codexHome, item.ThreadId, out _, out _, out _);
				if (File.Exists(originalPath) && !File.Exists(backupPath))
				{
					File.Move(originalPath, backupPath);
				}
			}
			catch (Exception rollbackError)
			{
				throw new AggregateException("会话索引恢复失败，且文件自动回滚失败。", restoreError, rollbackError);
			}
			throw;
		}
		TryDeleteEmptyDirectory(Path.GetDirectoryName(backupPath));
	}

	public static void DeleteFromTrash(TrashSessionInfo item)
	{
		if (item == null)
		{
			throw new ArgumentNullException("item");
		}
		string codexHome = CodexCatalog.ResolveCodexHome();
		CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
		string backupPath = ValidateTrashPath(item.BackupPath);
		string sidecarPath = ValidateTrashPath(item.SidecarPath);
		if (File.Exists(backupPath))
		{
			File.Delete(backupPath);
		}
		if (File.Exists(sidecarPath))
		{
			File.Delete(sidecarPath);
		}
		TryDeleteEmptyDirectory(Path.GetDirectoryName(backupPath));
	}

	public static string ResolveProjectPath(SessionInfo session, ProjectGroup project)
	{
		string cwd = session == null ? string.Empty : session.Cwd;
		string projectPath = project == null ? string.Empty : project.ProjectPath;
		try
		{
			if (!string.IsNullOrWhiteSpace(projectPath))
			{
				string fullProjectPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(projectPath));
				if (string.IsNullOrWhiteSpace(cwd) || TextHelpers.IsWithin(cwd, fullProjectPath))
				{
					return fullProjectPath;
				}
			}
			if (!string.IsNullOrWhiteSpace(cwd))
			{
				return Path.GetFullPath(TextHelpers.StripExtendedPrefix(cwd));
			}
		}
		catch
		{
		}
		return string.Empty;
	}

	public static string ValidateProjectPath(string projectPath)
	{
		if (string.IsNullOrWhiteSpace(projectPath))
		{
			throw new InvalidOperationException("该会话没有可用的项目路径。");
		}
		string fullPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(projectPath));
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("项目目录不存在：\n" + fullPath);
		}
		string root = Path.GetPathRoot(fullPath);
		if (string.Equals(TextHelpers.CanonicalPath(fullPath), TextHelpers.CanonicalPath(root), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("安全校验失败：不能删除磁盘根目录。");
		}
		foreach (Environment.SpecialFolder folder in ProtectedSpecialFolders())
		{
			string protectedPath = Environment.GetFolderPath(folder);
			if (!string.IsNullOrWhiteSpace(protectedPath) && TextHelpers.IsWithin(protectedPath, fullPath))
			{
				throw new InvalidOperationException("安全校验失败：不能删除系统、用户目录或包含这些目录的上级目录：\n" + fullPath);
			}
		}
		string codexHome = Path.GetFullPath(BackendContext.Home);
		if (TextHelpers.IsWithin(fullPath, codexHome) || TextHelpers.IsWithin(codexHome, fullPath))
		{
			throw new InvalidOperationException("安全校验失败：项目路径与 Codex 配置目录重叠。");
		}
		string applicationDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
		string otherHome = Path.GetFullPath(BackendContext.IsDsh ? CodexCatalog.ResolveCodexHome() : DshService.Home);
		if (TextHelpers.IsWithin(fullPath, otherHome) || TextHelpers.IsWithin(otherHome, fullPath))
			throw new InvalidOperationException("安全校验失败：项目路径与另一数据源的配置目录重叠。");
		if (TextHelpers.IsWithin(applicationDirectory, fullPath) || TextHelpers.IsWithin(fullPath, applicationDirectory))
		{
			throw new InvalidOperationException("安全校验失败：项目路径与当前工具目录重叠。请把工具移到其他位置后再处理该项目。");
		}
		return fullPath;
	}

	public static void DeleteProject(string projectPath, ProjectDeleteMode mode)
	{
		if (mode == ProjectDeleteMode.None)
		{
			return;
		}
		string fullPath = ValidateProjectPath(projectPath);
		if (mode == ProjectDeleteMode.RecycleBin)
		{
			Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(fullPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
			return;
		}
		Directory.Delete(fullPath, recursive: true);
	}

	public static void MarkProjectHandled(TrashSessionInfo item, ProjectDeleteMode mode)
	{
		if (item == null || string.IsNullOrWhiteSpace(item.SidecarPath))
		{
			return;
		}
		string sidecarPath = ValidateTrashPath(item.SidecarPath);
		Dictionary<string, object> metadata = ReadMetadata(sidecarPath);
		metadata["project_path"] = NormalizeOptionalPath(item.ProjectPath);
		metadata["project_delete_mode"] = mode == ProjectDeleteMode.RecycleBin ? "windows_recycle_bin" : "permanent";
		metadata["project_deleted_at"] = DateTimeOffset.Now.ToString("o");
		WriteMetadata(sidecarPath, metadata);
	}

	public static void MarkProjectHandled(string backupPath, string projectPath, ProjectDeleteMode mode)
	{
		if (string.IsNullOrWhiteSpace(backupPath))
		{
			return;
		}
		string validatedBackup = ValidateTrashPath(backupPath);
		string sidecarPath = validatedBackup + ".delete-info.json";
		if (!File.Exists(sidecarPath))
		{
			return;
		}
		Dictionary<string, object> metadata = ReadMetadata(sidecarPath);
		metadata["project_path"] = NormalizeOptionalPath(projectPath);
		metadata["project_delete_mode"] = mode == ProjectDeleteMode.RecycleBin ? "windows_recycle_bin" : "permanent";
		metadata["project_deleted_at"] = DateTimeOffset.Now.ToString("o");
		WriteMetadata(sidecarPath, metadata);
	}

	private static string ResolveValidatedSessionPath(SessionInfo session)
	{
		if (session == null)
		{
			throw new ArgumentNullException("session");
		}
		string path = CodexCatalog.ResolveSessionPath(session);
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			throw new FileNotFoundException("找不到该对话对应的本地会话文件。", path);
		}
		path = ValidateSessionTargetPath(path);
		return path;
	}

	private static List<DeletionTarget> PrepareDeletionTargets(SessionInfo root, string rootProjectPath, IEnumerable<SessionInfo> affectedSessions)
	{
		if (root == null)
		{
			throw new ArgumentNullException("root");
		}
		if (string.IsNullOrWhiteSpace(root.ThreadId))
		{
			throw new InvalidOperationException("该会话缺少 Thread ID，无法通过 Codex 官方接口删除。");
		}
		Dictionary<string, SessionInfo> sessions = new Dictionary<string, SessionInfo>(StringComparer.OrdinalIgnoreCase)
		{
			[root.ThreadId] = root
		};
		foreach (SessionInfo item in affectedSessions ?? Enumerable.Empty<SessionInfo>())
		{
			if (item != null && !string.IsNullOrWhiteSpace(item.ThreadId))
			{
				sessions[item.ThreadId] = item;
			}
		}
		foreach (SessionInfo item in sessions.Values)
		{
			if (string.Equals(item.ThreadId, root.ThreadId, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string parentId = item.ParentThreadId;
			bool reachesRoot = false;
			while (!string.IsNullOrWhiteSpace(parentId) && visited.Add(parentId))
			{
				if (string.Equals(parentId, root.ThreadId, StringComparison.OrdinalIgnoreCase))
				{
					reachesRoot = true;
					break;
				}
				if (!sessions.TryGetValue(parentId, out SessionInfo parent))
				{
					break;
				}
				parentId = parent.ParentThreadId;
			}
			if (!reachesRoot)
			{
				throw new InvalidOperationException("安全校验失败：受影响会话不是所选主会话的子代理：\n" + item.ThreadId);
			}
		}
		// thread/delete cascades through the native graph. Verify the complete graph
		// against both disk and the index before allowing that call.
		string home = CodexCatalog.ResolveCodexHome();
		var parents = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
		var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		Action<string, string> addParent = (id, parentId) => {
			if (string.IsNullOrWhiteSpace(parentId)) return;
			if (!parents.TryGetValue(id, out var set)) parents[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			set.Add(parentId);
		};
		foreach (string file in RolloutStorage.EnumerateStrict(home))
		{
			var metadata = TargetedThreadIndexer.ReadMetadataForIndexing(file, "", "");
			if (paths.ContainsKey(metadata.Id)) throw new IOException("发现重复会话 ID，未执行删除：" + metadata.Id);
			paths.Add(metadata.Id, file);
			addParent(metadata.Id, metadata.ParentThreadId);
		}
		string database = WinSqliteMaintenance.FindActiveDatabase(home);
		if (!string.IsNullOrWhiteSpace(database))
			foreach (var thread in WinSqliteReader.ReadThreads(database)) addParent(thread.Id, thread.ParentThreadId);
		var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.ThreadId };
		bool changed;
		do { changed = false; foreach (var pair in parents) if (pair.Value.Any(closure.Contains)) changed |= closure.Add(pair.Key); } while (changed);
		if (!closure.SetEquals(sessions.Keys)) throw new IOException("原生会话关联范围与预览不一致，未执行删除；请刷新并保留未识别记录。");
		foreach (var item in sessions.Values)
		{
			if (!paths.TryGetValue(item.ThreadId, out string actual) ||
				!string.Equals(TextHelpers.CanonicalPath(actual), TextHelpers.CanonicalPath(ResolveValidatedSessionPath(item)), StringComparison.OrdinalIgnoreCase))
				throw new IOException("会话身份或路径发生变化，未执行删除：" + item.ThreadId);
		}
		List<SessionInfo> ordered = new List<SessionInfo> { root };
		ordered.AddRange(sessions.Values.Where((SessionInfo item) => !string.Equals(item.ThreadId, root.ThreadId, StringComparison.OrdinalIgnoreCase)).OrderBy((SessionInfo item) => item.UpdatedDate));
		return ordered.Select((SessionInfo item) => new DeletionTarget
		{
			Session = item,
			SourcePath = ResolveValidatedSessionPath(item),
			ProjectPath = string.Equals(item.ThreadId, root.ThreadId, StringComparison.OrdinalIgnoreCase) ? NormalizeOptionalPath(rootProjectPath) : NormalizeOptionalPath(ResolveProjectPath(item, null))
		}).ToList();
	}

	private static Dictionary<string, object> CreateTrashMetadata(DeletionTarget target, string backupPath)
	{
		SessionInfo session = target.Session;
		return new Dictionary<string, object>
		{
			{ "schema", 3 },
			{ "thread_id", session.ThreadId ?? string.Empty },
			{ "title", session.DisplayTitle },
			{ "original_path", target.SourcePath },
			{ "backup_path", backupPath },
			{ "project_path", target.ProjectPath },
			{ "size_bytes", new FileInfo(target.SourcePath).Length },
			{ "deleted_at", DateTimeOffset.Now.ToString("o") },
			{ "preview", session.Preview ?? string.Empty },
			{ "source", session.Source ?? string.Empty },
			{ "model_provider", session.ModelProvider ?? string.Empty },
			{ "cli_version", session.CliVersion ?? string.Empty },
			{ "created_at", session.CreatedAt ?? string.Empty },
			{ "updated_at", session.UpdatedAt ?? string.Empty },
			{ "archived", session.Archived },
			{ "compressed", session.Compressed },
			{ "is_subagent", session.IsSubagent },
			{ "parent_thread_id", session.ParentThreadId ?? string.Empty },
			{ "official_delete", false }
		};
	}

	private static void EnsureRestoreIdentityAvailable(string home, string id)
	{
		if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("恢复记录缺少会话 ID。");
		string database = WinSqliteMaintenance.FindActiveDatabase(home);
		if (string.IsNullOrWhiteSpace(database)) throw new InvalidDataException("缺少 Codex 索引，请先初始化目标目录。");
		if (WinSqliteReader.ReadThreads(database).Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
			throw new IOException("同 ID 会话已存在于索引中，拒绝覆盖：" + id);
		// Strict enumeration and parsing: an unreadable directory or rollout cannot
		// establish that an identity is absent. Do not use the best-effort UI scan.
		foreach (string file in RolloutStorage.EnumerateStrict(home))
		{
			var metadata = TargetedThreadIndexer.ReadMetadataForIndexing(file, "", "");
			if (string.Equals(metadata.Id, id, StringComparison.OrdinalIgnoreCase))
				throw new IOException("同 ID 会话文件已存在，拒绝覆盖：" + file);
		}
	}

	private static string ValidateSessionTargetPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("会话路径为空。");
		}
		string fullPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(path));
		string codexHome = Path.GetFullPath(CodexCatalog.ResolveCodexHome());
		string sessionsRoot = Path.Combine(codexHome, "sessions");
		string archivedRoot = Path.Combine(codexHome, "archived_sessions");
		if (!TextHelpers.IsWithin(fullPath, sessionsRoot) && !TextHelpers.IsWithin(fullPath, archivedRoot))
		{
			throw new InvalidOperationException("安全校验失败：会话文件不在 Codex 会话目录内，已拒绝删除或恢复。");
		}
		return fullPath;
	}

	private static string ValidateTrashPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new InvalidOperationException("回收站路径为空。");
		}
		string fullPath = Path.GetFullPath(TextHelpers.StripExtendedPrefix(path));
		if (!TextHelpers.IsWithin(fullPath, TrashRoot) || string.Equals(TextHelpers.CanonicalPath(fullPath), TextHelpers.CanonicalPath(TrashRoot), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("安全校验失败：目标不在软件回收站内。");
		}
		return fullPath;
	}

	private static string UniqueFilePath(string directory, string fileName)
	{
		string path = Path.GetFullPath(Path.Combine(directory, fileName));
		int suffix = 1;
		while (File.Exists(path) || File.Exists(path + ".delete-info.json"))
		{
			path = Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + "-" + suffix + Path.GetExtension(fileName));
			suffix++;
		}
		return path;
	}

	private static string ReadProjectPathFromSession(string sessionPath)
	{
		if (!sessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}
		try
		{
			using (StreamReader reader = new StreamReader(sessionPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
			{
				string firstLine = reader.ReadLine();
				if (string.IsNullOrWhiteSpace(firstLine))
				{
					return string.Empty;
				}
				Dictionary<string, object> root = JsonSerialization.NewSerializer().DeserializeObject(firstLine) as Dictionary<string, object>;
				Dictionary<string, object> payload = root != null && root.ContainsKey("payload") ? root["payload"] as Dictionary<string, object> : null;
				return NormalizeOptionalPath(Value(payload, "cwd"));
			}
		}
		catch
		{
			return string.Empty;
		}
	}

	private static Dictionary<string, object> ReadMetadata(string path)
	{
		Dictionary<string, object> metadata = JsonSerialization.NewSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
		if (metadata == null)
		{
			throw new InvalidDataException("删除信息文件格式无效。");
		}
		return metadata;
	}

	private static void WriteMetadata(string path, Dictionary<string, object> metadata)
	{
		File.WriteAllText(path, JsonSerialization.NewSerializer().Serialize(metadata), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private static string Value(Dictionary<string, object> source, string key)
	{
		if (source == null || !source.TryGetValue(key, out var value) || value == null)
		{
			return string.Empty;
		}
		return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
	}

	private static bool BoolValue(Dictionary<string, object> source, string key)
	{
		if (source == null || !source.TryGetValue(key, out object value) || value == null)
		{
			return false;
		}
		if (value is bool boolean)
		{
			return boolean;
		}
		return bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed) && parsed;
	}

	private static void RemoveThreadVisibility(string codexHome, string threadId, out ThreadIndexRemovalResult indexRemoval, out DesktopCatalogRemovalResult catalogRemoval, out DesktopThreadRemovalResult desktopRemoval)
	{
		RemoveThreadVisibility(codexHome, new string[1] { threadId }, out indexRemoval, out catalogRemoval, out desktopRemoval);
	}

	private static void RemoveThreadVisibility(string codexHome, IEnumerable<string> threadIds, out ThreadIndexRemovalResult indexRemoval, out DesktopCatalogRemovalResult catalogRemoval, out DesktopThreadRemovalResult desktopRemoval)
	{
		indexRemoval = new ThreadIndexRemovalResult();
		catalogRemoval = new DesktopCatalogRemovalResult();
		desktopRemoval = new DesktopThreadRemovalResult();
		string[] ids = (threadIds ?? Enumerable.Empty<string>()).Where((string id) => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (ids.Length == 0)
		{
			return;
		}
		CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
		indexRemoval = WinSqliteMaintenance.RemoveThreads(codexHome, ids);
		catalogRemoval = WinSqliteMaintenance.RemoveDesktopCatalogThreads(codexHome, ids);
		desktopRemoval = CodexDesktopProjectRegistry.RemoveThreads(codexHome, ids);
		CodexDesktopTaskCache.InvalidateThreads(codexHome, ids);
	}

	private sealed class DeletionTarget
	{
		public SessionInfo Session { get; set; }

		public string SourcePath { get; set; }

		public string ProjectPath { get; set; }
	}

	private sealed class StagedTrashCopy
	{
		public DeletionTarget Target { get; set; }

		public string BackupPath { get; set; }

		public string SidecarPath { get; set; }

		public Dictionary<string, object> Metadata { get; set; }
	}

	private static void RestoreThreadVisibility(string codexHome, ThreadIndexMetadata metadata, string path)
	{
		if (metadata == null || string.IsNullOrWhiteSpace(metadata.Id) || string.IsNullOrWhiteSpace(WinSqliteMaintenance.FindActiveDatabase(codexHome)))
		{
			return;
		}
		metadata.RolloutPath = TextHelpers.ToCodexIndexPath(Path.GetFullPath(path));
		metadata.Archived = TextHelpers.IsWithin(path, Path.Combine(codexHome, "archived_sessions"));
		TargetedThreadIndexer.IndexMetadata(codexHome, metadata);
	}

	private static ThreadIndexMetadata CreateIndexMetadata(SessionInfo session, string path)
	{
		try
		{
			if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
			{
				return TargetedThreadIndexer.ReadMetadataForIndexing(path, session.DisplayTitle, session.Preview);
			}
		}
		catch
		{
		}
		DateTime created = ParseUtc(session.CreatedAt, File.GetCreationTimeUtc(path));
		DateTime updated = session.UpdatedDate != DateTime.MinValue ? session.UpdatedDate.ToUniversalTime() : ParseUtc(session.UpdatedAt, File.GetLastWriteTimeUtc(path));
		if (updated < created)
		{
			updated = created;
		}
		long createdMilliseconds = new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
		long updatedMilliseconds = new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
		return new ThreadIndexMetadata
		{
			Id = session.ThreadId,
			RolloutPath = TextHelpers.ToCodexIndexPath(Path.GetFullPath(path)),
			CreatedAtSeconds = createdMilliseconds / 1000,
			UpdatedAtSeconds = updatedMilliseconds / 1000,
			CreatedAtMilliseconds = createdMilliseconds,
			UpdatedAtMilliseconds = updatedMilliseconds,
			Source = string.IsNullOrWhiteSpace(session.Source) ? "unknown" : session.Source,
			ThreadSource = session.IsSubagent ? "subagent" : null,
			ParentThreadId = session.ParentThreadId,
			ModelProvider = string.IsNullOrWhiteSpace(session.ModelProvider) ? "openai" : session.ModelProvider,
			Cwd = TextHelpers.ToCodexIndexPath(session.Cwd ?? string.Empty),
			CliVersion = session.CliVersion ?? string.Empty,
			Title = session.DisplayTitle,
			Preview = string.IsNullOrWhiteSpace(session.Preview) ? session.DisplayTitle : session.Preview,
			FirstUserMessage = string.IsNullOrWhiteSpace(session.Preview) ? session.DisplayTitle : session.Preview,
			HasUserEvent = !string.IsNullOrWhiteSpace(session.Preview) || !string.IsNullOrWhiteSpace(session.Title),
			Archived = session.Archived,
			GitSha = null,
			GitBranch = null,
			GitOriginUrl = null
		};
	}

	private static SessionInfo SessionFromTrash(TrashSessionInfo item, string originalPath)
	{
		return new SessionInfo
		{
			ThreadId = item.ThreadId,
			SessionPath = originalPath,
			Cwd = item.ProjectPath,
			Title = item.Title,
			Preview = item.Preview,
			Source = item.Source,
			ModelProvider = item.ModelProvider,
			CliVersion = item.CliVersion,
			CreatedAt = item.CreatedAt,
			UpdatedAt = item.UpdatedAt,
			Archived = item.Archived,
			Compressed = item.Compressed,
			IsSubagent = item.IsSubagent,
			ParentThreadId = item.ParentThreadId
		};
	}

	private static DateTime ParseUtc(string value, DateTime fallback)
	{
		if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
		{
			return parsed.UtcDateTime;
		}
		DateTime result = fallback;
		if (result.Kind == DateTimeKind.Local)
		{
			result = result.ToUniversalTime();
		}
		else if (result.Kind == DateTimeKind.Unspecified)
		{
			result = DateTime.SpecifyKind(result, DateTimeKind.Utc);
		}
		return result.Year < 2000 ? DateTime.UtcNow : result;
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
			return path.Trim();
		}
	}

	private static Environment.SpecialFolder[] ProtectedSpecialFolders()
	{
		return new Environment.SpecialFolder[12]
		{
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolder.Desktop,
			Environment.SpecialFolder.MyDocuments,
			Environment.SpecialFolder.ApplicationData,
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolder.CommonApplicationData,
			Environment.SpecialFolder.Windows,
			Environment.SpecialFolder.System,
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86,
			Environment.SpecialFolder.CommonProgramFiles,
			Environment.SpecialFolder.CommonProgramFilesX86
		};
	}

	private static void TryDeleteEmptyDirectory(string directory)
	{
		try
		{
			if (!string.IsNullOrWhiteSpace(directory) && TextHelpers.IsWithin(directory, TrashRoot) && !Directory.EnumerateFileSystemEntries(directory).Any())
			{
				Directory.Delete(directory);
			}
		}
		catch
		{
		}
	}
}
