using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CodexConversationManager;

// Immutable snapshots survive the short-lived bridge processes. The source is
// never changed. A changed/deleted/recompressed source cannot reuse its snapshot.
internal static class DesktopPreviewCache
{
    internal sealed class Message
    {
        public string role { get; set; }
        public string time { get; set; }
        public string text { get; set; }
        public bool truncated { get; set; }
    }
    internal sealed class Navigation
    {
        public int index { get; set; }
        public int number { get; set; }
        public string question { get; set; }
        public string answer { get; set; }
    }
    internal sealed class Index
    {
        public List<long> offsets { get; set; } = new List<long>();
        public List<Navigation> navigation { get; set; } = new List<Navigation>();
    }
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexConversationManager", "PreviewCache-v1");
    private static string Key(DesktopReadBridge.Request request, string home, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        string full = Path.GetFullPath(path);
        string prefix = Path.GetFullPath(home).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        FileInfo file = new FileInfo(full);
        if (!file.Exists) return null;
        // Disallow cache access through links, just like the catalog scanner.
        for (string current = full; current.Length >= prefix.Length; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        return DesktopOperations.Hash(string.Join("\n", "v1", request.provider, request.language, prefix.ToUpperInvariant(), request.projectId, request.id, full.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks));
    }
    public static object TryRead(DesktopReadBridge.Request request, string home)
    {
        string key = Key(request, home, request.sessionPath);
        return key == null ? null : Read(key, request.offset ?? 0);
    }
    private static object Read(string key, int offset, bool cacheHit = true)
    {
        if (offset < 0) throw new InvalidDataException("Invalid preview offset.");
        string indexPath = Path.Combine(Root, key + ".json"), bodyPath = Path.Combine(Root, key + ".bin");
        try
        {
            if (!File.Exists(indexPath) || !File.Exists(bodyPath)) return null;
            if (File.GetLastWriteTimeUtc(indexPath) < DateTime.UtcNow.AddHours(-24)) return null;
            var serializer = JsonSerialization.NewSerializer();
            Index index = serializer.Deserialize<Index>(File.ReadAllText(indexPath, Encoding.UTF8));
            var page = new List<Message>();
            using (FileStream stream = new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                for (int i = offset; i < index.offsets.Count && i < (long)offset + 20; i++)
                {
                    stream.Position = index.offsets[i];
                    page.Add(serializer.Deserialize<Message>(reader.ReadString()));
                }
            File.SetLastAccessTimeUtc(indexPath, DateTime.UtcNow);
            return new { messages = page, total = index.offsets.Count, nextOffset = offset + page.Count, hasMore = offset + page.Count < index.offsets.Count, revision = key, cacheHit, navigation = index.navigation };
        }
        catch (Exception e) when (e is IOException || e is ArgumentException || e is InvalidOperationException) { return null; }
    }
    public static object Create(DesktopReadBridge.Request request, string home, SessionInfo session)
    {
        string path = CodexCatalog.ResolveSessionPath(session);
        string key = Key(request, home, path);
        if (key == null) throw new IOException("Conversation changed. Refresh the list.");
        Directory.CreateDirectory(Root);
        using var mutex = new Mutex(false, "Local\\CcmPreview-" + key);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(85)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Conversation is still being read. Please retry.");
        string temporary = Path.Combine(Root, key + "." + Guid.NewGuid().ToString("N"));
        try
        {
            object cached = Read(key, request.offset ?? 0);
            if (cached != null) return cached;
            var messages = (BackendContext.IsDsh ? DshService.ReadConversation(session) : ConversationReader.Read(session)).Messages;
            Index index = new Index();
            var serializer = JsonSerialization.NewSerializer();
            Navigation current = null;
            using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(output, Encoding.UTF8))
                for (int i = 0; i < messages.Count; i++)
                {
                    ConversationMessage message = messages[i];
                    string text = message.Text;
                    if (message.IsUser)
                    {
                        current = new Navigation { index = i, number = index.navigation.Count + 1, question = Compact(text, 180), answer = "" };
                        index.navigation.Add(current);
                    }
                    else if (!message.IsNotice && current != null && string.IsNullOrEmpty(current.answer)) current.answer = Compact(text, 300);
                    index.offsets.Add(output.Position);
                    writer.Write(serializer.Serialize(new Message { role = message.IsNotice ? "notice" : message.IsUser ? "user" : "assistant", time = message.DisplayTime, text = text.Length > 100000 ? text.Substring(0, 100000) : text, truncated = text.Length > 100000 }));
                }
            if (key != Key(request, home, path)) throw new IOException("Conversation changed while reading. Please reopen it.");
            string body = Path.Combine(Root, key + ".bin"), metadata = Path.Combine(Root, key + ".json");
            File.Delete(metadata);
            File.Delete(body);
            File.Move(temporary, body);
            File.WriteAllText(temporary, serializer.Serialize(index), new UTF8Encoding(false));
            File.Move(temporary, metadata);
            object result = Read(key, request.offset ?? 0, false);
            Trim(key);
            return result ?? throw new IOException("Could not read the conversation cache.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); mutex.ReleaseMutex(); }
    }
    private static string Compact(string value, int length)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return text.Length > length ? text.Substring(0, length) + "…" : text;
    }
    private static void Trim(string keep)
    {
        try
        {
            long size = 0;
            foreach (var index in new DirectoryInfo(Root).GetFiles("*.json").OrderByDescending(f => f.LastAccessTimeUtc))
            {
                string key = Path.GetFileNameWithoutExtension(index.Name), body = Path.Combine(Root, key + ".bin");
                long bytes = index.Length + (File.Exists(body) ? new FileInfo(body).Length : 0);
                size += bytes;
                if (key != keep && (size > 512L * 1024 * 1024 || index.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-24)))
                { index.Delete(); File.Delete(body); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
