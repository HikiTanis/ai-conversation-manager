using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

namespace CodexConversationManager;

internal static class StorageSafetySelfTest
{
    private static void Assert(bool ok, string message) { if (!ok) throw new InvalidOperationException("Storage safety: " + message); }
    private static void Refuses(Action action, string message)
    {
        try { action(); } catch (IOException) { return; } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Storage safety did not refuse: " + message);
    }
    public static void Run(string root)
    {
        string previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousDelete = CodexAppServerThreadDeletion.TestOverride;
        int calls = 0;
        try
        {
            Directory.CreateDirectory(root);
            string Setup(string name)
            {
                string home = Path.Combine(root, name); Directory.CreateDirectory(home);
                Environment.SetEnvironmentVariable("CODEX_HOME", home);
                WinSqliteMaintenance.CreateTargetedIndexTestDatabase(Path.Combine(home, "state_5.sqlite"), "complete");
                return home;
            }
            SessionInfo Session(string home, string id, string name = "rollout", string parent = null)
            {
                string path = Path.Combine(home, "sessions", name + "-" + id + ".jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var payload = new Dictionary<string, object> { { "id", id }, { "cwd", root }, { "timestamp", "2026-10-23T00:00:00Z" }, { "source", "cli" }, { "history_mode", "legacy" }, { "model_provider", "openai" } };
                if (parent != null) { payload["parent_thread_id"] = parent; payload["thread_source"] = "subagent"; }
                string text = JsonSerialization.NewSerializer().Serialize(new { type = "session_meta", timestamp = "2026-10-23T00:00:00Z", payload }) + "\n" +
                    "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"preserve this history\"}]}}\n";
                File.WriteAllText(path, text, new UTF8Encoding(false));
                TargetedThreadIndexer.IndexSessionFile(home, path, "safety", "safety");
                return new SessionInfo { ThreadId = id, SessionPath = path, Cwd = root, Title = "safety", Source = "cli", ParentThreadId = parent, IsSubagent = parent != null };
            }
            string id = "a1234567-1111-4111-8111-111111111111";
            string childId = "b1234567-2222-4222-8222-222222222222";
            var home = Setup("compressed-check"); var session = Session(home, id);
            RolloutStorage.EncodeFile(session.SessionPath, session.SessionPath + ".zst"); File.Delete(session.SessionPath);
            Assert(ConversationIndexMaintenance.FindOrphanedThreads(home).Count == 0, "plain index with compressed sibling is live");
            CodexAppServerThreadDeletion.TestOverride = (_, __) => { calls++; return new OfficialThreadDeletionResult { Succeeded = true }; };
            ConversationIndexMaintenance.RepairSelectedOrphans(home, new[] { id }); Assert(calls == 0, "diagnostics called deletion");
            File.Delete(session.SessionPath + ".zst");
            ConversationIndexMaintenance.RepairSelectedOrphans(home, new[] { id });
            Assert(calls == 0 && WinSqliteReader.ReadThreads(Path.Combine(home, "state_5.sqlite")).Any(t => t.Id == id), "missing-file check removed metadata");
            WinSqliteMaintenance.AddDesktopCatalogTestThread(home, id, "keep", root);
            string ledger = Path.Combine(home, "conversation-migrator-index-backups", "sidebar-remnant-repairs.json"); Directory.CreateDirectory(Path.GetDirectoryName(ledger));
            File.WriteAllText(ledger, JsonSerialization.NewSerializer().Serialize(new { thread_ids = new[] { id } }));
            ConversationIndexMaintenance.InvalidateCompletedRepairCaches(home);
            Assert(WinSqliteMaintenance.CountDesktopCatalogThreads(home, new[] { id }) == 1, "ledger replay deleted restored catalog");

            home = Setup("lost-response"); session = Session(home, id); string original = File.ReadAllText(session.SessionPath);
            CodexAppServerThreadDeletion.TestOverride = (_, __) => { File.Delete(session.SessionPath); throw new TimeoutException("deleted but response lost"); };
            try { ConversationStorage.MoveToTrash(session); throw new Exception("timeout expected"); } catch (InvalidOperationException e) { Assert(e.InnerException is TimeoutException, "preserved cause"); }
            var backup = ConversationStorage.ReadTrash().Single();
            Assert(File.ReadAllText(backup.BackupPath) == original, "timeout discarded the only copy");

            home = Setup("trash-purge-isolation"); session = Session(home, id);
            CodexAppServerThreadDeletion.TestOverride = (_, __) => new OfficialThreadDeletionResult { Succeeded = true };
            ConversationStorage.MoveToTrash(session);
            backup = ConversationStorage.ReadTrash().Single();
            var reimported = Session(home, id, "reimported");
            WinSqliteMaintenance.AddDesktopCatalogTestThread(home, id, "keep", root);
            string db = Path.Combine(home, "state_5.sqlite"); string dbHash = ProjectPayloadService.Sha256File(db);
            Refuses(() => ConversationStorage.Restore(backup), "same identity already indexed");
            ConversationStorage.DeleteFromTrash(backup);
            Assert(File.Exists(reimported.SessionPath) && ProjectPayloadService.Sha256File(db) == dbHash && WinSqliteMaintenance.CountDesktopCatalogThreads(home, new[] { id }) == 1, "purge changed live identity");

            home = Setup("restore-encoding-conflict"); session = Session(home, id); ConversationStorage.MoveToTrash(session); backup = ConversationStorage.ReadTrash().Single();
            RolloutStorage.EncodeFile(backup.BackupPath, session.SessionPath + ".zst");
            Refuses(() => ConversationStorage.Restore(backup), "compressed sibling"); Assert(File.Exists(backup.BackupPath), "conflict consumed backup");
            File.Delete(session.SessionPath + ".zst");
            var other = Session(home, id, "other"); WinSqliteMaintenance.RemoveThreads(home, new[] { id });
            Refuses(() => ConversationStorage.Restore(backup), "unindexed same-ID file at other path");
            File.Delete(other.SessionPath);
            ConversationStorage.Restore(backup);
            Assert(File.Exists(session.SessionPath), "clean restore failed");

            home = Setup("incomplete-cascade"); session = Session(home, id); var child = Session(home, childId, "child", id);
            RolloutStorage.EncodeFile(child.SessionPath, child.SessionPath + ".zst"); File.Delete(child.SessionPath);
            calls = 0; CodexAppServerThreadDeletion.TestOverride = (_, __) => { calls++; return new OfficialThreadDeletionResult { Succeeded = true }; };
            Refuses(() => ConversationStorage.MoveToTrash(session), "unselected compressed child"); Assert(calls == 0 && File.Exists(child.SessionPath + ".zst"), "cascade escaped preview");

            string replaceRoot = Path.Combine(root, "project-race"); Directory.CreateDirectory(replaceRoot);
            string replaceSource = Path.Combine(replaceRoot, "source"), replaceTarget = Path.Combine(replaceRoot, "target");
            File.WriteAllText(replaceSource, "incoming"); File.WriteAllText(replaceTarget, "backed up");
            string backedHash = ProjectPayloadService.Sha256File(replaceTarget);
            try
            {
                ProjectPayloadService.BeforeReplaceForTest = path => File.WriteAllText(path, "concurrent user edit");
                Refuses(() => ProjectPayloadService.CopyFileAtomically(replaceSource, replaceTarget, true, backedHash), "project overwrite race");
                Assert(Directory.GetFiles(replaceRoot, "*.codex-preserved").Any(path => File.ReadAllText(path) == "concurrent user edit"), "project overwrite lost latest bytes");
            }
            finally { ProjectPayloadService.BeforeReplaceForTest = null; }

            home = Setup("paginated-delete-scope"); session = Session(home, id);
            var fragment = Session(home, childId, "fragment");
            string fragmentText = "{\"ordinal\":1604,\"type\":\"session_meta\",\"payload\":{\"id\":\"" + childId + "\",\"history_mode\":\"paginated\",\"cwd\":\"C:/fixture\"}}\n";
            File.WriteAllText(fragment.SessionPath, fragmentText);
            Refuses(() => TargetedThreadIndexer.ReadMetadataForIndexing(fragment.SessionPath, "", ""), "fragment must still fail standalone import validation");
            RolloutStorage.EncodeFile(fragment.SessionPath, fragment.SessionPath + ".zst"); File.Delete(fragment.SessionPath);
            calls = 0;
            CodexAppServerThreadDeletion.TestOverride = (_, __) => { calls++; return new OfficialThreadDeletionResult { Succeeded = true }; };
            ConversationStorage.MoveToTrash(session);
            Assert(calls == 1 && File.Exists(fragment.SessionPath + ".zst"), "unrelated paginated fragment blocked deletion or was removed");
            fragment.SessionPath += ".zst"; fragment.Compressed = true;
            var fragmentDeleted = ConversationStorage.MoveToTrash(fragment);
            using (var reader = new StreamReader(RolloutStorage.OpenRead(fragmentDeleted.BackupPath)))
                Assert(reader.ReadToEnd() == fragmentText, "selected fragment backup changed bytes");
            Assert(calls == 2, "selected paginated fragment was not deletable");

            home = Setup("import-races"); session = Session(home, id);
            string bundle = Path.Combine(root, "race.codexbundle"); ExactBundleWriter.CreateSingleSessionBundle(session, bundle);
            string destinationHome = Path.Combine(root, "race-target"); Directory.CreateDirectory(destinationHome);
            string racedPath = null;
            try
            {
                NativeBundleImporter.BeforeInstallForTest = path => { racedPath = path; File.WriteAllText(path, "another writer owns this file"); };
                Refuses(() => NativeBundleImporter.Import(bundle, destinationHome, null, null, NativeBundleImportMode.Merge, false), "concurrent destination creation");
                Assert(File.ReadAllText(racedPath) == "another writer owns this file", "rollback deleted an unowned file");
            }
            finally { NativeBundleImporter.BeforeInstallForTest = null; }
            File.Delete(racedPath);
            var baselineImport = NativeBundleImporter.Import(bundle, destinationHome, null, null, NativeBundleImportMode.Merge, false);
            string replaceRollout = baselineImport.TouchedSessionFiles.Single();
            string extra = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"latest\"}}\n";
            File.AppendAllText(replaceRollout, extra);
            string latest = File.ReadAllText(replaceRollout) + extra;
            try
            {
                NativeBundleImporter.BeforeInstallForTest = path => File.AppendAllText(path, extra);
                Refuses(() => NativeBundleImporter.Import(bundle, destinationHome, null, null, NativeBundleImportMode.Replace, false), "concurrent replacement edit");
                Assert(File.ReadAllText(replaceRollout) == latest, "replacement lost the latest pre-install edit");
            }
            finally { NativeBundleImporter.BeforeInstallForTest = null; }
            File.Delete(replaceRollout);
            var transaction = NativeImportTransaction.Begin(destinationHome);
            var imported = NativeBundleImporter.Import(bundle, destinationHome, null, null, NativeBundleImportMode.Merge, false);
            transaction.TrackImportedSessionFiles(imported.TouchedSessionFiles, new[] { id });
            string installed = imported.TouchedSessionFiles.Single(); File.AppendAllText(installed, "newer content from user");
            Refuses(() => transaction.RollbackAndDeleteTemporaryBackups(), "newly imported conversation continued before rollback");
            Assert(File.ReadAllText(installed).Contains("newer content from user"), "rollback removed subsequent content");

            home = Setup("different-snapshots"); session = Session(home, id);
            string first = session.SessionPath + ".cct-bak-1", second = session.SessionPath + ".cct-bak-2";
            File.Copy(session.SessionPath, first); File.Copy(session.SessionPath, second);
            File.AppendAllText(second, "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"unique newer content\"}}\n");
            var hashes = new[] { ProjectPayloadService.Sha256File(first), ProjectPayloadService.Sha256File(second) }.OrderBy(x => x).ToArray();
            var moved = ImportSnapshotMaintenance.MoveOrphanedSnapshotsToTrash(home);
            Assert(moved.MovedToTrashCount == 2 && moved.RedundantDeletedCount == 0, "different snapshots discarded");
            Assert(ConversationStorage.ReadTrash().Select(t => ProjectPayloadService.Sha256File(t.BackupPath)).OrderBy(x => x).SequenceEqual(hashes), "snapshot bytes changed");
        }
        finally { CodexAppServerThreadDeletion.TestOverride = previousDelete; Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
    }
}
