using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace CodexConversationManager;

internal static class ZstdRolloutSelfTest
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        string home = Path.Combine(root, "source");
        string folder = Path.Combine(home, "sessions", "2026", "10", "09");
        Directory.CreateDirectory(folder);
        string project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        string id = "31000000-0000-4000-8000-000000000001";
        string childId = "31000000-0000-4000-8000-000000000002";
        string plain = Path.Combine(folder, "rollout-" + id + ".jsonl");
        string content = Metadata(id, project, null) + "\r\n" + Message("user", "旧会话 🐈 café") + "\n" + Message("assistant", new string('答', 70000));
        File.WriteAllText(plain, content, new UTF8Encoding(true));
        byte[] expected = File.ReadAllBytes(plain);
        string compressed = plain + ".zst";
        RolloutStorage.EncodeFile(plain, compressed);
        byte[] originalCompressed = File.ReadAllBytes(compressed);
        var uncompressedPreview = ConversationReader.Read(new SessionInfo { SessionPath = plain }).Messages.Select(m => m.Text).ToArray();
        File.Delete(plain);
        SessionInfo session = NativeSessionCatalog.Scan(home).Single();
        Assert(session.Compressed && session.MetadataVerified && session.Cwd == project, "catalog metadata");
        var preview = ConversationReader.Read(session);
        Assert(preview.Messages.Count == 2 && preview.Messages.Select(m => m.Text).SequenceEqual(uncompressedPreview), "UTF8 preview and long lines");
        Assert(ConversationReader.ReadTitleCandidate(compressed).Contains("旧会话"), "title");
        Assert(TargetedThreadIndexer.SnapshotSessionFiles(home).Contains(compressed), "compressed enumeration");
        Assert(TargetedThreadIndexer.ReadMetadataForIndexing(compressed, "title", "preview").Id == id, "index metadata");

        string bundle = Path.Combine(root, "backup.codexbundle");
        ExactBundleWriter.CreateSingleSessionBundle(session, bundle);
        using (ZipArchive archive = ZipFile.OpenRead(bundle))
        {
            BundleSession item = Manifest(archive).sessions.Single();
            Assert(!item.compressed && item.bundle_path.EndsWith(".jsonl"), "normalized bundle descriptor");
            using Stream input = archive.GetEntry(item.bundle_path).Open();
            using MemoryStream bytes = new MemoryStream();
            input.CopyTo(bytes);
            Assert(bytes.ToArray().SequenceEqual(expected), "backup preserves decoded bytes including BOM/newlines");
        }
        string importedHome = Path.Combine(root, "imported");
        var imported = NativeBundleImporter.Import(bundle, importedHome, null, null, NativeBundleImportMode.Merge, false);
        Assert(imported.CreatedCount == 1 && File.ReadAllBytes(imported.Sessions[0].DestinationPath).SequenceEqual(expected), "backup/import roundtrip");
        Assert(NativeBundleImporter.Import(bundle, home, null, null, NativeBundleImportMode.Merge, false).SkippedCount == 1, "identical compressed destination");
        Assert(NativeBundleImporter.Import(bundle, home, null, null, NativeBundleImportMode.Replace, false).SkippedCount == 1, "identical replace");

        // Incoming history extends the compressed destination; write back a valid zstd
        // frame at the same path so the existing Codex index remains valid.
        string extendedPath = Path.Combine(root, "rollout-" + id + ".jsonl");
        File.WriteAllBytes(extendedPath, expected.Concat(Encoding.UTF8.GetBytes("\n" + Message("assistant", "追加内容") + "\n")).ToArray());
        SessionInfo extendedSession = new SessionInfo { ThreadId = id, SessionPath = extendedPath, Cwd = project, RelativePath = session.RelativePath };
        string extendedBundle = Path.Combine(root, "extended.codexbundle");
        ExactBundleWriter.CreateSingleSessionBundle(extendedSession, extendedBundle);
        var transaction = NativeImportTransaction.Begin(home);
        var merge = NativeBundleImporter.Import(extendedBundle, home, null, null, NativeBundleImportMode.Merge, false);
        transaction.TrackImportedSessionFiles(merge.TouchedSessionFiles, new[] { id });
        Assert(merge.MergedCount == 1 && merge.Sessions[0].DestinationPath == compressed, "compressed fast forward");
        Assert(ConversationReader.Read(session).Messages.Count == 3, "fast forward preview");
        Assert(!File.Exists(plain), "no duplicate plain sibling");
        Assert(NativeBundleImporter.Import(bundle, home, null, null, NativeBundleImportMode.Merge, false).SkippedCount == 1, "local ahead");
        transaction.RollbackAndDeleteTemporaryBackups();
        Assert(File.ReadAllBytes(compressed).SequenceEqual(originalCompressed), "rollback restores original compressed bytes");

        // Also accept externally produced packages that carry .jsonl.zst entries.
        string compressedBundle = Path.Combine(root, "compressed.codexbundle");
        MakeCompressedBundle(bundle, compressedBundle, compressed);
        string rawHome = Path.Combine(root, "raw-import");
        Assert(NativeBundleImporter.Import(compressedBundle, rawHome, null, null, NativeBundleImportMode.Merge, false).CreatedCount == 1, "compressed archive import");
        string fresh = Path.Combine(root, "fresh.codexbundle");
        var rewrite = BundleFreshIdRewriter.RewriteAsFresh(compressedBundle, fresh);
        Assert(rewrite.IdMap[id] != id && NativeBundleImporter.PreflightBundle(fresh, null, null) == 1, "compressed ID rewrite");
        var planner = new ConversationImportPlanner(home);
        var plan = planner.CreatePlan(compressedBundle, Path.Combine(root, "planned.codexbundle"), project, false);
        Assert(plan.MatchedCount == 1 && plan.IdMap[id] == id, "compressed lineage matching");

        string archivedFolder = Path.Combine(home, "archived_sessions");
        Directory.CreateDirectory(archivedFolder);
        string childPlain = Path.Combine(archivedFolder, "rollout-" + childId + ".jsonl");
        File.WriteAllText(childPlain, Metadata(childId, project, id) + "\n" + Message("user", "子会话") + "\n", new UTF8Encoding(false));
        RolloutStorage.EncodeFile(childPlain, childPlain + ".zst");
        File.Delete(childPlain);
        var sessions = NativeSessionCatalog.Scan(home);
        Assert(sessions.Single(s => s.ThreadId == childId).ParentThreadId == id && sessions.Single(s => s.ThreadId == childId).Archived, "child metadata");
        string pair = Path.Combine(root, "pair.codexbundle"), pairFresh = Path.Combine(root, "pair-fresh.codexbundle");
        ExactBundleWriter.CreateBundle(sessions, pair, null);
        var pairMap = BundleFreshIdRewriter.RewriteAsFresh(pair, pairFresh);
        var pairImport = NativeBundleImporter.Import(pairFresh, Path.Combine(root, "pair-home"), null, project + "-moved", NativeBundleImportMode.Merge, false);
        string childImported = pairImport.Sessions.Single(s => s.ThreadId == pairMap.IdMap[childId]).DestinationPath;
        Assert(childImported.Contains("archived_sessions"), "archived import");
        Assert(ConversationLineage.TryReadPayload(childImported, out var childPayload) && ConversationLineage.GetString(childPayload, "parent_thread_id") == pairMap.IdMap[id], "parent-child rewrite");

        // Two independent frames, including a frame with no declared content size.
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
        File.WriteAllText(first, Metadata(id, project, null) + "\n", new UTF8Encoding(false));
        File.WriteAllText(second, Message("user", "多帧") + "\n", new UTF8Encoding(false));
        RolloutStorage.EncodeFile(first, first + ".zst");
        RolloutStorage.EncodeFile(second, second + ".zst");
        string frames = Path.Combine(root, "frames.jsonl.zst");
        File.WriteAllBytes(frames, File.ReadAllBytes(first + ".zst").Concat(File.ReadAllBytes(second + ".zst")).ToArray());
        Assert(ConversationReader.Read(new SessionInfo { SessionPath = frames }).Messages.Single().Text == "多帧", "concatenated frames");

        string corruptFolder = Path.Combine(root, "corrupt"); Directory.CreateDirectory(corruptFolder);
        string corrupt = Path.Combine(corruptFolder, Path.GetFileName(compressed));
        foreach (byte[] bad in new[] { originalCompressed.Take(originalCompressed.Length - 3).ToArray(), Encoding.UTF8.GetBytes("not zstd") })
        {
            File.WriteAllBytes(corrupt, bad);
            AssertFails(() => ConversationReader.Read(new SessionInfo { SessionPath = corrupt }), "invalid preview");
            byte[] originalBundle = File.ReadAllBytes(bundle);
            AssertFails(() => ExactBundleWriter.CreateSingleSessionBundle(new SessionInfo { ThreadId = id, SessionPath = corrupt }, bundle), "invalid backup");
            Assert(File.ReadAllBytes(bundle).SequenceEqual(originalBundle), "failed backup preserves previous output");
            string badBundle = Path.Combine(root, Guid.NewGuid() + ".codexbundle");
            MakeCompressedBundle(bundle, badBundle, corrupt);
            AssertFails(() => NativeBundleImporter.Import(badBundle, rawHome, null, null, NativeBundleImportMode.Replace, false), "invalid import");
        }
        Assert(File.ReadAllBytes(compressed).SequenceEqual(originalCompressed), "source unchanged");
    }

    private static string Metadata(string id, string cwd, string parent)
    {
        return JsonSerialization.NewSerializer().Serialize(new { type = "session_meta", timestamp = "2026-10-09T00:00:00Z", payload = new { id, cwd, timestamp = "2026-10-09T00:00:00Z", source = parent == null ? "cli" : "subagent", parent_thread_id = parent, history_mode = "legacy" } });
    }
    private static string Message(string role, string text) => JsonSerialization.NewSerializer().Serialize(new { type = "response_item", timestamp = "2026-10-09T00:00:01Z", payload = new { type = "message", role, content = new[] { new { type = role == "user" ? "input_text" : "output_text", text } } } });
    private static BundleManifest Manifest(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry("manifest.json").Open());
        return JsonSerialization.NewSerializer().Deserialize<BundleManifest>(reader.ReadToEnd());
    }
    private static void MakeCompressedBundle(string source, string output, string zstd)
    {
        using ZipArchive original = ZipFile.OpenRead(source);
        BundleManifest manifest = Manifest(original);
        BundleSession item = manifest.sessions.Single();
        item.bundle_path += ".zst"; item.compressed = true;
        byte[] data = File.ReadAllBytes(zstd);
        item.size_bytes = data.Length; item.sha256 = ProjectPayloadService.Sha256File(zstd);
        using ZipArchive archive = ZipFile.Open(output, ZipArchiveMode.Create);
        using (Stream stream = archive.CreateEntry(item.bundle_path).Open()) stream.Write(data, 0, data.Length);
        byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerialization.NewSerializer().Serialize(manifest));
        using (Stream stream = archive.CreateEntry("manifest.json").Open()) stream.Write(manifestBytes, 0, manifestBytes.Length);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var checksums = new Dictionary<string, string> { [item.bundle_path] = item.sha256, ["manifest.json"] = BitConverter.ToString(sha.ComputeHash(manifestBytes)).Replace("-", "").ToLowerInvariant() };
        using var writer = new StreamWriter(archive.CreateEntry("checksums.json").Open(), new UTF8Encoding(false));
        writer.Write(JsonSerialization.NewSerializer().Serialize(checksums));
    }
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("Zstd test failed: " + name); }
    private static void AssertFails(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Zstd test should reject: " + name);
    }
}
