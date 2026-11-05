using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodexConversationManager;

internal static class WinSqliteMaintenance
{
	private const int SQLITE_OK = 0;

	private const int SQLITE_BUSY = 5;

	private const int SQLITE_LOCKED = 6;

	private const int SQLITE_ROW = 100;

	private const int SQLITE_DONE = 101;

	private const int SQLITE_OPEN_READONLY = 1;

	private const int SQLITE_OPEN_READWRITE = 2;

	private const int SQLITE_OPEN_CREATE = 4;

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_close_v2(IntPtr db);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_step(IntPtr statement);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_finalize(IntPtr statement);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_column_bytes(IntPtr statement, int index);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr callbackArgument, out IntPtr errorMessage);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern void sqlite3_free(IntPtr pointer);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr sqlite3_errmsg(IntPtr db);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr sqlite3_backup_init(IntPtr destination, byte[] destinationName, IntPtr source, byte[] sourceName);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_backup_step(IntPtr backup, int pageCount);

	[DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_backup_finish(IntPtr backup);

	public static TargetedIndexResult UpsertImportedThreads(string codexHome, IEnumerable<ThreadIndexMetadata> importedThreads)
	{
		List<ThreadIndexMetadata> list = (from @group in (importedThreads ?? Enumerable.Empty<ThreadIndexMetadata>()).Where((ThreadIndexMetadata item) => item != null && !string.IsNullOrWhiteSpace(item.Id)).GroupBy((ThreadIndexMetadata item) => item.Id, StringComparer.OrdinalIgnoreCase)
			select @group.Last()).ToList();
		if (list.Count == 0)
		{
			throw new InvalidOperationException("没有需要登记的导入会话。");
		}
		foreach (ThreadIndexMetadata item in list)
		{
			item.Cwd = TextHelpers.ToCodexIndexPath(item.Cwd);
			item.RolloutPath = TextHelpers.ToCodexIndexPath(item.RolloutPath);
			item.HistoryMode = CodexHistoryMode.Normalize(item.HistoryMode, item.Id);
		}

		string text = FindActiveDatabase(codexHome);
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new FileNotFoundException("没有找到 Codex 的 state_*.sqlite 索引文件。");
		}
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(text), out db, 2, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException("无法打开 Codex 索引：" + Error(db));
			}
			sqlite3_busy_timeout(db, 8000);
			if (!TableExists(db, "threads"))
			{
				throw new InvalidDataException("当前 Codex 索引没有 threads 表，未进行任何修改。");
			}
			string text2 = ReadBackfillSnapshot(db);
			string text3 = text2.Split(new char[1] { '\u001f' }, 2)[0];
			if (!string.Equals(text3, "complete", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Codex 的全局会话回填状态不是 complete（当前为 " + text3 + "），本工具已拒绝写入。请勿反复重启 Codex；先等待回填完成，若状态长期不变，请恢复索引备份或修复该状态后再试。");
			}
			string a = Scalar(db, "pragma data_version");
			string text4 = CreateConsistentBackup(db, text, codexHome);
			if (!string.Equals(IntegrityCheck(text4), "ok", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("写入前的 Codex 索引备份未通过完整性检查，未进行任何修改。");
			}
			HashSet<string> hashSet = ReadThreadColumns(db);
			string[] array = new string[10] { "id", "rollout_path", "created_at", "updated_at", "source", "model_provider", "cwd", "title", "sandbox_policy", "approval_mode" };
			string[] array2 = array;
			foreach (string text5 in array2)
			{
				if (!hashSet.Contains(text5))
				{
					throw new InvalidDataException("Codex threads 表缺少必需字段：" + text5);
				}
			}
			if (!hashSet.Contains("history_mode") && list.Any((ThreadIndexMetadata item) => string.Equals(item.HistoryMode, CodexHistoryMode.Paginated, StringComparison.Ordinal)))
			{
				throw new InvalidDataException("当前 Codex 索引不包含 history_mode，无法安全导入 paginated 会话。请先更新 Codex 客户端。");
			}
			int num2 = 0;
			int num3 = 0;
			bool flag = false;
			try
			{
				Execute(db, "begin immediate;");
				flag = true;
				if (!string.Equals(a, Scalar(db, "pragma data_version"), StringComparison.Ordinal))
				{
					throw new InvalidOperationException("Codex 索引在备份期间仍被其他进程更新，已取消写入。请完全退出 Codex 后重试。");
				}
				string text6 = ReadBackfillSnapshot(db);
				if (!string.Equals(text2, text6, StringComparison.Ordinal) || !string.Equals(text6.Split(new char[1] { '\u001f' }, 2)[0], "complete", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("备份后检测到 Codex 的全局回填状态发生变化，已取消写入；请稍后重试。");
				}
				foreach (ThreadIndexMetadata item in list)
				{
					bool flag2 = Scalar(db, "select count(*) from threads where id=" + SqlText(item.Id)) == "1";
					Execute(db, flag2 ? BuildUpdateSql(item, hashSet) : BuildInsertSql(item, hashSet));
					if (flag2)
					{
						num3++;
					}
					else
					{
						num2++;
					}
					if (!string.IsNullOrWhiteSpace(item.ParentThreadId) && TableExists(db, "thread_spawn_edges"))
					{
						Execute(db, "insert into thread_spawn_edges(parent_thread_id,child_thread_id,status) values(" + SqlText(item.ParentThreadId) + "," + SqlText(item.Id) + ",'open') on conflict(child_thread_id) do update set parent_thread_id=excluded.parent_thread_id,status=excluded.status;");
					}
				}
				string b = ReadBackfillSnapshot(db);
				if (!string.Equals(text6, b, StringComparison.Ordinal))
				{
					throw new InvalidDataException("定点登记意外改变了全局回填状态，事务已回滚。");
				}
				if (!string.Equals(Scalar(db, "pragma integrity_check"), "ok", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Codex 索引完整性检查未通过，事务已回滚。");
				}
				foreach (ThreadIndexMetadata item2 in list)
				{
					string path = Scalar(db, "select cwd from threads where id=" + SqlText(item2.Id));
					string path2 = Scalar(db, "select rollout_path from threads where id=" + SqlText(item2.Id));
					if (!TextHelpers.HasExtendedPrefix(path) || !TextHelpers.HasExtendedPrefix(path2))
					{
						throw new InvalidDataException("Codex 可见性验证失败：项目路径或会话文件路径未使用原生索引格式。任务：" + item2.Id);
					}
					if (!string.IsNullOrWhiteSpace(item2.ParentThreadId) && TableExists(db, "thread_spawn_edges") && Scalar(db, "select count(*) from thread_spawn_edges where parent_thread_id=" + SqlText(item2.ParentThreadId) + " and child_thread_id=" + SqlText(item2.Id)) != "1")
					{
						throw new InvalidDataException("Codex 可见性验证失败：子代理父子关系未写入索引。任务：" + item2.Id);
					}
					if (!string.Equals(TextHelpers.CanonicalPath(path), TextHelpers.CanonicalPath(item2.Cwd), StringComparison.OrdinalIgnoreCase) || !string.Equals(TextHelpers.CanonicalPath(path2), TextHelpers.CanonicalPath(item2.RolloutPath), StringComparison.OrdinalIgnoreCase))
					{
						throw new InvalidDataException("定点索引校验失败：" + item2.Id);
					}
					if (hashSet.Contains("history_mode"))
					{
						string historyMode = Scalar(db, "select history_mode from threads where id=" + SqlText(item2.Id));
						if (!string.Equals(historyMode, item2.HistoryMode, StringComparison.Ordinal))
						{
							throw new InvalidDataException("Codex 历史模式验证失败：" + item2.Id + "\n文件：" + item2.HistoryMode + "\n索引：" + historyMode);
						}
					}
				}
				Execute(db, "commit;");
				flag = false;
			}
			catch
			{
				if (flag)
				{
					try
					{
						Execute(db, "rollback;");
					}
					catch
					{
					}
				}
				throw;
			}
			TargetedIndexResult targetedIndexResult = new TargetedIndexResult();
			targetedIndexResult.DatabasePath = text;
			targetedIndexResult.BackupPath = text4;
			targetedIndexResult.BackfillState = text3;
			targetedIndexResult.InsertedCount = num2;
			targetedIndexResult.UpdatedCount = num3;
			targetedIndexResult.IndexedCount = list.Count;
			targetedIndexResult.VisibilityVerifiedCount = list.Count;
			return targetedIndexResult;
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static ThreadIndexRemovalResult RemoveThreads(string codexHome, IEnumerable<string> threadIds)
	{
		List<string> ids = (threadIds ?? Enumerable.Empty<string>())
			.Where((string id) => !string.IsNullOrWhiteSpace(id))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		ThreadIndexRemovalResult result = new ThreadIndexRemovalResult
		{
			RequestedCount = ids.Count
		};
		if (ids.Count == 0)
		{
			return result;
		}
		string databasePath = FindActiveDatabase(codexHome);
		result.DatabasePath = databasePath;
		if (string.IsNullOrWhiteSpace(databasePath))
		{
			return result;
		}
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
			{
				throw new InvalidDataException("无法打开 Codex 索引：" + Error(db));
			}
			sqlite3_busy_timeout(db, 8000);
			if (!TableExists(db, "threads"))
			{
				throw new InvalidDataException("当前 Codex 索引没有 threads 表，未进行任何修改。");
			}
			string idList = string.Join(",", ids.Select(SqlText).ToArray());
			string threadPredicate = "id in (" + idList + ")";
			string edgePredicate = "parent_thread_id in (" + idList + ") or child_thread_id in (" + idList + ")";
			int existingThreads = ScalarInt(db, "select count(*) from threads where " + threadPredicate);
			int existingEdges = TableExists(db, "thread_spawn_edges") ? ScalarInt(db, "select count(*) from thread_spawn_edges where " + edgePredicate) : 0;
			int existingTools = TableExists(db, "thread_dynamic_tools") ? ScalarInt(db, "select count(*) from thread_dynamic_tools where thread_id in (" + idList + ")") : 0;
			if (existingThreads == 0 && existingEdges == 0 && existingTools == 0)
			{
				return result;
			}
			string backfillBefore = ReadBackfillSnapshot(db);
			string backfillStatus = backfillBefore.Split(new char[1] { '\u001f' }, 2)[0];
			if (!string.Equals(backfillStatus, "complete", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Codex 的全局会话回填状态不是 complete（当前为 " + backfillStatus + "），本工具已拒绝清理侧边栏索引。请等待回填完成后重试。");
			}
			string dataVersion = Scalar(db, "pragma data_version");
			string backupPath = CreateConsistentBackup(db, databasePath, codexHome);
			if (!string.Equals(IntegrityCheck(backupPath), "ok", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("写入前的 Codex 索引备份未通过完整性检查，未进行任何修改。");
			}
			bool transactionOpen = false;
			try
			{
				Execute(db, "begin immediate;");
				transactionOpen = true;
				if (!string.Equals(dataVersion, Scalar(db, "pragma data_version"), StringComparison.Ordinal))
				{
					throw new InvalidOperationException("Codex 索引在备份期间仍被其他进程更新，已取消删除。请完全退出 Codex 后重试。");
				}
				string backfillLocked = ReadBackfillSnapshot(db);
				if (!string.Equals(backfillBefore, backfillLocked, StringComparison.Ordinal))
				{
					throw new InvalidOperationException("备份后检测到 Codex 的全局回填状态发生变化，已取消删除；请稍后重试。");
				}
				if (TableExists(db, "thread_spawn_edges"))
				{
					Execute(db, "delete from thread_spawn_edges where " + edgePredicate + ";");
				}
				if (TableExists(db, "thread_dynamic_tools"))
				{
					Execute(db, "delete from thread_dynamic_tools where thread_id in (" + idList + ");");
				}
				Execute(db, "delete from threads where " + threadPredicate + ";");
				if (ScalarInt(db, "select count(*) from threads where " + threadPredicate) != 0)
				{
					throw new InvalidDataException("Codex 侧边栏索引删除校验失败，事务已回滚。");
				}
				if (TableExists(db, "thread_spawn_edges") && ScalarInt(db, "select count(*) from thread_spawn_edges where " + edgePredicate) != 0)
				{
					throw new InvalidDataException("Codex 子代理关系索引删除校验失败，事务已回滚。");
				}
				if (!string.Equals(backfillLocked, ReadBackfillSnapshot(db), StringComparison.Ordinal))
				{
					throw new InvalidDataException("定点删除意外改变了全局回填状态，事务已回滚。");
				}
				if (!string.Equals(Scalar(db, "pragma integrity_check"), "ok", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Codex 索引完整性检查未通过，事务已回滚。");
				}
				Execute(db, "commit;");
				transactionOpen = false;
			}
			catch
			{
				if (transactionOpen)
				{
					try
					{
						Execute(db, "rollback;");
					}
					catch
					{
					}
				}
				throw;
			}
			result.BackupPath = backupPath;
			result.RemovedThreadCount = existingThreads;
			result.RemovedEdgeCount = existingEdges;
			return result;
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static DesktopCatalogRemovalResult RemoveDesktopCatalogThreads(string codexHome, IEnumerable<string> threadIds)
	{
		List<string> ids = (threadIds ?? Enumerable.Empty<string>())
			.Where((string id) => Guid.TryParse(id, out _))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		DesktopCatalogRemovalResult result = new DesktopCatalogRemovalResult
		{
			RequestedCount = ids.Count,
			DatabasePath = FindDesktopCatalogDatabase(codexHome)
		};
		if (ids.Count == 0 || string.IsNullOrWhiteSpace(result.DatabasePath))
		{
			return result;
		}
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(result.DatabasePath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
			{
				throw new InvalidDataException("无法打开 Codex 桌面任务目录：" + Error(db));
			}
			sqlite3_busy_timeout(db, 8000);
			if (!TableExists(db, "local_thread_catalog"))
			{
				return result;
			}
			string idList = string.Join(",", ids.Select(SqlText).ToArray());
			string predicate = "thread_id in (" + idList + ")";
			int existingCatalogEntries = ScalarInt(db, "select count(*) from local_thread_catalog where " + predicate);
			int existingTimelineEntries = TableExists(db, "thread_timeline_ledger") ? ScalarInt(db, "select count(*) from thread_timeline_ledger where " + predicate) : 0;
			if (existingCatalogEntries == 0 && existingTimelineEntries == 0)
			{
				return result;
			}
			CodexDesktopProjectRegistry.EnsureImportCanWrite(codexHome);
			string dataVersion = Scalar(db, "pragma data_version");
			bool hasCatalogMetadata = TableExists(db, "local_thread_catalog_metadata") && ScalarInt(db, "select count(*) from local_thread_catalog_metadata where id=1") == 1;
			string catalogRevision = hasCatalogMetadata ? Scalar(db, "select cast(catalog_revision as text) from local_thread_catalog_metadata where id=1") : "absent";
			string backupPath = CreateConsistentBackup(db, result.DatabasePath, codexHome);
			if (!string.Equals(IntegrityCheck(backupPath), "ok", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("写入前的 Codex 桌面任务目录备份未通过完整性检查，未进行任何修改。");
			}
			bool transactionOpen = false;
			try
			{
				Execute(db, "begin immediate;");
				transactionOpen = true;
				if (!string.Equals(dataVersion, Scalar(db, "pragma data_version"), StringComparison.Ordinal))
				{
					throw new InvalidOperationException("Codex 桌面任务目录在备份期间仍被其他进程更新，已取消删除。请完全退出 Codex 后重试。");
				}
				if (hasCatalogMetadata && !string.Equals(catalogRevision, Scalar(db, "select cast(catalog_revision as text) from local_thread_catalog_metadata where id=1"), StringComparison.Ordinal))
				{
					throw new InvalidOperationException("Codex 桌面任务目录版本在备份期间发生变化，已取消删除。");
				}
				if (TableExists(db, "thread_timeline_ledger"))
				{
					Execute(db, "delete from thread_timeline_ledger where " + predicate + ";");
				}
				Execute(db, "delete from local_thread_catalog where " + predicate + ";");
				if (hasCatalogMetadata)
				{
					Execute(db, "update local_thread_catalog_metadata set catalog_revision=catalog_revision+1 where id=1;");
				}
				if (ScalarInt(db, "select count(*) from local_thread_catalog where " + predicate) != 0)
				{
					throw new InvalidDataException("Codex 新版侧边栏目录删除校验失败，事务已回滚。");
				}
				if (TableExists(db, "thread_timeline_ledger") && ScalarInt(db, "select count(*) from thread_timeline_ledger where " + predicate) != 0)
				{
					throw new InvalidDataException("Codex 任务时间线目录删除校验失败，事务已回滚。");
				}
				if (!string.Equals(Scalar(db, "pragma integrity_check"), "ok", StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("Codex 桌面任务目录完整性检查未通过，事务已回滚。");
				}
				Execute(db, "commit;");
				transactionOpen = false;
			}
			catch
			{
				if (transactionOpen)
				{
					try
					{
						Execute(db, "rollback;");
					}
					catch
					{
					}
				}
				throw;
			}
			result.BackupPath = backupPath;
			result.RemovedCatalogEntryCount = existingCatalogEntries;
			result.RemovedTimelineEntryCount = existingTimelineEntries;
			return result;
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static string FindDesktopCatalogDatabase(string codexHome)
	{
		if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome))
		{
			return string.Empty;
		}
		// Current desktop releases use codex.db. A migrated profile can retain
		// codex-dev.db, so never let that stale catalog shadow the current one.
		foreach (string name in new[] { "codex.db", "codex-dev.db" })
		{
			string path = Path.GetFullPath(Path.Combine(codexHome, "sqlite", name));
			if (File.Exists(path)) return path;
		}
		return string.Empty;
	}

	public static int CountDesktopCatalogThreads(string codexHome, IEnumerable<string> threadIds)
	{
		string databasePath = FindDesktopCatalogDatabase(codexHome);
		string[] ids = (threadIds ?? Enumerable.Empty<string>()).Where((string id) => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (string.IsNullOrWhiteSpace(databasePath) || ids.Length == 0)
		{
			return 0;
		}
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
			{
				throw new InvalidDataException("无法读取 Codex 桌面任务目录：" + Error(db));
			}
			if (!TableExists(db, "local_thread_catalog"))
			{
				return 0;
			}
			return ScalarInt(db, "select count(*) from local_thread_catalog where thread_id in (" + string.Join(",", ids.Select(SqlText).ToArray()) + ")");
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static void CreateDesktopCatalogTestDatabase(string codexHome)
	{
		string directory = Path.Combine(codexHome, "sqlite");
		Directory.CreateDirectory(directory);
		string databasePath = Path.Combine(directory, "codex-dev.db");
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, IntPtr.Zero) != SQLITE_OK)
			{
				throw new InvalidDataException(Error(db));
			}
			Execute(db, "create table if not exists local_thread_catalog_hosts(host_id text primary key,host_kind text not null);" +
				"create table if not exists local_thread_catalog_metadata(id integer primary key,catalog_revision integer not null default 0);" +
				"create table if not exists local_thread_catalog(host_id text not null,thread_id text not null,display_title text not null,source_created_at real not null,source_updated_at real not null,cwd text not null,source_kind text not null,source_detail text,model_provider text not null,git_branch text,observation_sequence integer not null,missing_candidate integer not null default 0,thread_source text,source_recency_at real not null default 0,pending_observed_title integer not null default 0,primary key(host_id,thread_id));" +
				"create table if not exists thread_timeline_ledger(host_id text not null,thread_id text not null,sequence integer not null,record_id text not null,payload_json text not null,primary key(host_id,thread_id,sequence),unique(host_id,thread_id,record_id)) without rowid;" +
				"insert or ignore into local_thread_catalog_hosts(host_id,host_kind) values('local','local');" +
				"insert or ignore into local_thread_catalog_metadata(id,catalog_revision) values(1,0);");
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static void AddDesktopCatalogTestThread(string codexHome, string threadId, string title, string cwd)
	{
		CreateDesktopCatalogTestDatabase(codexHome);
		string databasePath = FindDesktopCatalogDatabase(codexHome);
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
			{
				throw new InvalidDataException(Error(db));
			}
			Execute(db, "insert or replace into local_thread_catalog(host_id,thread_id,display_title,source_created_at,source_updated_at,cwd,source_kind,source_detail,model_provider,git_branch,observation_sequence,missing_candidate,thread_source,source_recency_at,pending_observed_title) values('local'," + SqlText(threadId) + "," + SqlText(title) + ",1,2," + SqlText(cwd) + ",'cli',null,'openai',null,1,0,null,2,0);");
			Execute(db, "insert or replace into thread_timeline_ledger(host_id,thread_id,sequence,record_id,payload_json) values('local'," + SqlText(threadId) + ",1," + SqlText("test-" + threadId) + ",'{}');");
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	private static int ScalarInt(IntPtr database, string sql)
	{
		if (!int.TryParse(Scalar(database, sql), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
		{
			throw new InvalidDataException("Codex 索引返回了无效计数。");
		}
		return value;
	}

	public static string ReadBackfillState(string databasePath)
	{
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, 1, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException(Error(db));
			}
			return ReadBackfillSnapshot(db);
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static void CreateTargetedIndexTestDatabase(string databasePath, string status = "complete")
	{
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, 6, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException(Error(db));
			}
			Execute(db, "create table backfill_state(id integer primary key,status text not null,last_watermark text,last_success_at integer,updated_at integer not null);insert into backfill_state(id,status,last_watermark,last_success_at,updated_at) values(1," + SqlText(status) + ",'sessions/old.jsonl',123,123);create table threads(id text primary key,rollout_path text not null,created_at integer not null,updated_at integer not null,source text not null,model_provider text not null,cwd text not null,title text not null,sandbox_policy text not null,approval_mode text not null,tokens_used integer not null default 0,has_user_event integer not null default 0,archived integer not null default 0,archived_at integer,git_sha text,git_branch text,git_origin_url text,cli_version text not null default '',first_user_message text not null default '',agent_nickname text,agent_role text,memory_mode text not null default 'enabled',model text,reasoning_effort text,agent_path text,created_at_ms integer,updated_at_ms integer,thread_source text,preview text not null default '',recency_at integer not null default 0,recency_at_ms integer not null default 0,history_mode text not null default 'legacy',name text);create table thread_spawn_edges(parent_thread_id text not null,child_thread_id text primary key,status text not null);");
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	public static string IntegrityCheck(string databasePath)
	{
		IntPtr db = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(databasePath), out db, 1, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException(Error(db));
			}
			return Scalar(db, "pragma integrity_check");
		}
		finally
		{
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}

	private static string ReadBackfillSnapshot(IntPtr database)
	{
		if (!TableExists(database, "backfill_state"))
		{
			return "absent";
		}
		return Scalar(database, "select coalesce(status,'') || char(31) || coalesce(last_watermark,'NULL') || char(31) || coalesce(cast(last_success_at as text),'NULL') || char(31) || coalesce(cast(updated_at as text),'NULL') from backfill_state where id=1");
	}

	private static bool TableExists(IntPtr database, string name)
	{
		return Scalar(database, "select count(*) from sqlite_master where type='table' and name=" + SqlText(name)) == "1";
	}

	private static HashSet<string> ReadThreadColumns(IntPtr database)
	{
		IntPtr statement = IntPtr.Zero;
		try
		{
			if (sqlite3_prepare_v2(database, Utf8("pragma table_info(threads)"), -1, out statement, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException(Error(database));
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			while (true)
			{
				switch (sqlite3_step(statement))
				{
				default:
					throw new InvalidDataException(Error(database));
				case 100:
					break;
				case 101:
					return hashSet;
				}
				hashSet.Add(ColumnText(statement, 1));
			}
		}
		finally
		{
			if (statement != IntPtr.Zero)
			{
				sqlite3_finalize(statement);
			}
		}
	}

	private static string BuildInsertSql(ThreadIndexMetadata item, ISet<string> columns)
	{
		Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{
				"id",
				SqlText(item.Id)
			},
			{
				"rollout_path",
				SqlText(item.RolloutPath)
			},
			{
				"created_at",
				SqlNumber(item.CreatedAtSeconds)
			},
			{
				"updated_at",
				SqlNumber(item.UpdatedAtSeconds)
			},
			{
				"recency_at",
				SqlNumber(item.UpdatedAtSeconds)
			},
			{
				"created_at_ms",
				SqlNumber(item.CreatedAtMilliseconds)
			},
			{
				"updated_at_ms",
				SqlNumber(item.UpdatedAtMilliseconds)
			},
			{
				"recency_at_ms",
				SqlNumber(item.UpdatedAtMilliseconds)
			},
			{
				"source",
				SqlText(item.Source)
			},
			{
				"history_mode",
				SqlText(item.HistoryMode)
			},
			{
				"thread_source",
				SqlNullableText(item.ThreadSource)
			},
			{ "agent_nickname", "NULL" },
			{ "agent_role", "NULL" },
			{ "agent_path", "NULL" },
			{
				"model_provider",
				SqlText(item.ModelProvider)
			},
			{ "model", "NULL" },
			{ "reasoning_effort", "NULL" },
			{
				"cwd",
				SqlText(item.Cwd)
			},
			{
				"cli_version",
				SqlText(item.CliVersion)
			},
			{
				"title",
				SqlText(item.Title)
			},
			{ "name", "NULL" },
			{
				"preview",
				SqlText(item.Preview)
			},
			{
				"sandbox_policy",
				SqlText("{\"type\":\"read-only\"}")
			},
			{
				"approval_mode",
				SqlText("on-request")
			},
			{ "tokens_used", "0" },
			{
				"has_user_event",
				item.HasUserEvent ? "1" : "0"
			},
			{
				"first_user_message",
				SqlText(item.FirstUserMessage)
			},
			{
				"archived",
				item.Archived ? "1" : "0"
			},
			{
				"archived_at",
				item.Archived ? SqlNumber(item.UpdatedAtSeconds) : "NULL"
			},
			{ "is_pinned", "0" },
			{
				"git_sha",
				SqlNullableText(item.GitSha)
			},
			{
				"git_branch",
				SqlNullableText(item.GitBranch)
			},
			{
				"git_origin_url",
				SqlNullableText(item.GitOriginUrl)
			},
			{
				"memory_mode",
				SqlText("enabled")
			}
		};
		List<string> list = values.Keys.Where(columns.Contains).ToList();
		return "insert into threads(" + string.Join(",", list.ToArray()) + ") values(" + string.Join(",", list.Select((string name) => values[name]).ToArray()) + ");";
	}

	private static string BuildUpdateSql(ThreadIndexMetadata item, ISet<string> columns)
	{
		List<string> list = new List<string>();
		AddAssignment(list, columns, "rollout_path", SqlText(item.RolloutPath));
		AddAssignment(list, columns, "history_mode", SqlText(item.HistoryMode));
		AddAssignment(list, columns, "updated_at", SqlNumber(item.UpdatedAtSeconds));
		AddAssignment(list, columns, "updated_at_ms", SqlNumber(item.UpdatedAtMilliseconds));
		AddAssignment(list, columns, "source", SqlText(item.Source));
		AddAssignment(list, columns, "model_provider", SqlText(item.ModelProvider));
		AddAssignment(list, columns, "cwd", SqlText(item.Cwd));
		AddAssignment(list, columns, "cli_version", SqlText(item.CliVersion));
		AddAssignment(list, columns, "title", SqlText(item.Title));
		AddAssignment(list, columns, "preview", SqlText(item.Preview));
		AddAssignment(list, columns, "first_user_message", SqlText(item.FirstUserMessage));
		AddAssignment(list, columns, "has_user_event", item.HasUserEvent ? "1" : "has_user_event");
		AddAssignment(list, columns, "archived", item.Archived ? "1" : "0");
		AddAssignment(list, columns, "archived_at", item.Archived ? SqlNumber(item.UpdatedAtSeconds) : "NULL");
		if (columns.Contains("thread_source") && !string.IsNullOrWhiteSpace(item.ThreadSource))
		{
			list.Add("thread_source=" + SqlText(item.ThreadSource));
		}
		if (columns.Contains("git_sha") && !string.IsNullOrWhiteSpace(item.GitSha))
		{
			list.Add("git_sha=coalesce(git_sha," + SqlText(item.GitSha) + ")");
		}
		if (columns.Contains("git_branch") && !string.IsNullOrWhiteSpace(item.GitBranch))
		{
			list.Add("git_branch=coalesce(git_branch," + SqlText(item.GitBranch) + ")");
		}
		if (columns.Contains("git_origin_url") && !string.IsNullOrWhiteSpace(item.GitOriginUrl))
		{
			list.Add("git_origin_url=coalesce(git_origin_url," + SqlText(item.GitOriginUrl) + ")");
		}
		return "update threads set " + string.Join(",", list.ToArray()) + " where id=" + SqlText(item.Id) + ";";
	}

	private static void AddAssignment(ICollection<string> assignments, ISet<string> columns, string name, string value)
	{
		if (columns.Contains(name))
		{
			assignments.Add(name + "=" + value);
		}
	}

	private static string SqlText(string value)
	{
		string text = (value ?? string.Empty).Replace("\0", string.Empty).Replace("'", "''");
		return "'" + text + "'";
	}

	private static string SqlNullableText(string value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return SqlText(value);
		}
		return "NULL";
	}

	private static string SqlNumber(long value)
	{
		return value.ToString(CultureInfo.InvariantCulture);
	}

	public static string FindActiveDatabase(string codexHome)
	{
		if (string.IsNullOrWhiteSpace(codexHome) || !Directory.Exists(codexHome))
		{
			return string.Empty;
		}
		return (from path in Directory.GetFiles(codexHome, "state_*.sqlite", SearchOption.TopDirectoryOnly)
			where Regex.IsMatch(Path.GetFileName(path), "^state_\\d+\\.sqlite$", RegexOptions.IgnoreCase)
			select path).OrderByDescending(StateDatabaseVersion).ThenByDescending((string path) => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
	}

	private static int StateDatabaseVersion(string path)
	{
		Match match = Regex.Match(Path.GetFileName(path) ?? string.Empty, "^state_(\\d+)\\.sqlite$", RegexOptions.IgnoreCase);
		if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
		{
			return -1;
		}
		return result;
	}

	private static string CreateConsistentBackup(IntPtr source, string databasePath, string codexHome)
	{
		string text = Path.Combine(codexHome, "conversation-migrator-index-backups");
		Directory.CreateDirectory(text);
		string text2 = Path.Combine(text, Path.GetFileNameWithoutExtension(databasePath) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".sqlite");
		IntPtr db = IntPtr.Zero;
		IntPtr intPtr = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(text2), out db, 6, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException("无法创建 Codex 索引备份：" + Error(db));
			}
			sqlite3_busy_timeout(db, 8000);
			intPtr = sqlite3_backup_init(db, Utf8("main"), source, Utf8("main"));
			if (intPtr == IntPtr.Zero)
			{
				throw new InvalidDataException("无法开始 Codex 索引备份：" + Error(db));
			}
			int num = 5;
			for (int i = 0; i < 40; Thread.Sleep(100), i++)
			{
				num = sqlite3_backup_step(intPtr, -1);
				switch (num)
				{
				default:
					throw new InvalidDataException("备份 Codex 索引失败，SQLite 返回 " + num + "。");
				case 5:
				case 6:
					continue;
				case 101:
					break;
				}
				break;
			}
			if (num != 101)
			{
				throw new IOException("Codex 索引正忙，暂时无法安全备份。请完全退出 Codex 后重试导入。");
			}
			int num2 = sqlite3_backup_finish(intPtr);
			intPtr = IntPtr.Zero;
			if (num2 != 0)
			{
				throw new InvalidDataException("完成 Codex 索引备份失败，SQLite 返回 " + num2 + "。");
			}
			return text2;
		}
		catch
		{
			try
			{
				if (File.Exists(text2))
				{
					File.Delete(text2);
				}
			}
			catch
			{
			}
			throw;
		}
		finally
		{
			if (intPtr != IntPtr.Zero)
			{
				sqlite3_backup_finish(intPtr);
			}
			if (db != IntPtr.Zero)
			{
				sqlite3_close_v2(db);
			}
		}
	}
	public static void RestoreConsistentBackup(string databasePath, string backupPath)
	{
		if (string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
		{
			throw new FileNotFoundException("找不到可用于恢复 Codex 索引的导入前备份。", backupPath);
		}
		IntPtr source = IntPtr.Zero;
		IntPtr destination = IntPtr.Zero;
		IntPtr backup = IntPtr.Zero;
		try
		{
			if (sqlite3_open_v2(Utf8(backupPath), out source, 1, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException("无法打开 Codex 索引备份：" + Error(source));
			}
			if (sqlite3_open_v2(Utf8(databasePath), out destination, 2, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException("无法打开需要恢复的 Codex 索引：" + Error(destination));
			}
			sqlite3_busy_timeout(destination, 8000);
			backup = sqlite3_backup_init(destination, Utf8("main"), source, Utf8("main"));
			if (backup == IntPtr.Zero)
			{
				throw new InvalidDataException("无法开始恢复 Codex 索引：" + Error(destination));
			}
			int code = 5;
			for (int i = 0; i < 40; Thread.Sleep(100), i++)
			{
				code = sqlite3_backup_step(backup, -1);
				if (code == 101)
				{
					break;
				}
				if (code != 5 && code != 6)
				{
					throw new InvalidDataException("恢复 Codex 索引失败，SQLite 返回 " + code + "。");
				}
			}
			if (code != 101)
			{
				throw new IOException("Codex 索引正忙，无法从导入前备份恢复。");
			}
			int finishCode = sqlite3_backup_finish(backup);
			backup = IntPtr.Zero;
			if (finishCode != 0)
			{
				throw new InvalidDataException("完成 Codex 索引恢复失败，SQLite 返回 " + finishCode + "。");
			}
		}
		finally
		{
			if (backup != IntPtr.Zero)
			{
				sqlite3_backup_finish(backup);
			}
			if (destination != IntPtr.Zero)
			{
				sqlite3_close_v2(destination);
			}
			if (source != IntPtr.Zero)
			{
				sqlite3_close_v2(source);
			}
		}
		if (!string.Equals(IntegrityCheck(databasePath), "ok", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("恢复后的 Codex 索引未通过完整性检查。");
		}
	}

	private static void Execute(IntPtr database, string sql)
	{
		IntPtr errorMessage = IntPtr.Zero;
		if (sqlite3_exec(database, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out errorMessage) == 0)
		{
			return;
		}
		string text = ((errorMessage == IntPtr.Zero) ? Error(database) : PointerString(errorMessage));
		if (errorMessage != IntPtr.Zero)
		{
			sqlite3_free(errorMessage);
		}
		throw new InvalidDataException("更新 Codex 定点索引失败：" + text);
	}

	private static string Scalar(IntPtr database, string sql)
	{
		IntPtr statement = IntPtr.Zero;
		try
		{
			if (sqlite3_prepare_v2(database, Utf8(sql), -1, out statement, IntPtr.Zero) != 0)
			{
				throw new InvalidDataException(Error(database));
			}
			return sqlite3_step(statement) switch
			{
				101 => string.Empty, 
				100 => ColumnText(statement, 0), 
				_ => throw new InvalidDataException(Error(database)), 
			};
		}
		finally
		{
			if (statement != IntPtr.Zero)
			{
				sqlite3_finalize(statement);
			}
		}
	}

	private static string ColumnText(IntPtr statement, int index)
	{
		IntPtr intPtr = sqlite3_column_text(statement, index);
		int num = sqlite3_column_bytes(statement, index);
		if (intPtr == IntPtr.Zero || num <= 0)
		{
			return string.Empty;
		}
		byte[] array = new byte[num];
		Marshal.Copy(intPtr, array, 0, num);
		return Encoding.UTF8.GetString(array);
	}

	private static byte[] Utf8(string value)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
		byte[] array = new byte[bytes.Length + 1];
		Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);
		return array;
	}

	private static string PointerString(IntPtr pointer)
	{
		if (pointer == IntPtr.Zero)
		{
			return string.Empty;
		}
		int i;
		for (i = 0; Marshal.ReadByte(pointer, i) != 0 && i < 32768; i++)
		{
		}
		byte[] array = new byte[i];
		Marshal.Copy(pointer, array, 0, i);
		return Encoding.UTF8.GetString(array);
	}

	private static string Error(IntPtr database)
	{
		if (database == IntPtr.Zero)
		{
			return "SQLite 未返回详细信息";
		}
		return PointerString(sqlite3_errmsg(database));
	}
}
