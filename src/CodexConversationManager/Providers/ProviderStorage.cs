using System;
using System.Collections.Generic;

namespace CodexConversationManager;

// UI commands route through the selected provider. The original Codex backend
// remains unchanged; DSH never invokes Codex indexing or thread deletion.
internal static class ProviderStorage
{
    public static DeletedSessionResult MoveToTrash(SessionInfo s, string p, IEnumerable<SessionInfo> related) => BackendContext.IsDsh ? DshStorage.MoveToTrash(s,p,related) : ConversationStorage.MoveToTrash(s,p,related);
    public static DeletedSessionResult DeletePermanently(SessionInfo s, IEnumerable<SessionInfo> related) => BackendContext.IsDsh ? DshStorage.DeletePermanently(s,related) : ConversationStorage.DeletePermanently(s,related);
    public static List<TrashSessionInfo> ReadTrash() => BackendContext.IsDsh ? DshStorage.ReadTrash() : ConversationStorage.ReadTrash();
    public static void Restore(TrashSessionInfo s) { if (BackendContext.IsDsh) DshStorage.Restore(s); else ConversationStorage.Restore(s); }
    public static void DeleteFromTrash(TrashSessionInfo s) { if (BackendContext.IsDsh) DshStorage.DeleteFromTrash(s); else ConversationStorage.DeleteFromTrash(s); }
    public static string ResolveProjectPath(SessionInfo s, ProjectGroup p) => ConversationStorage.ResolveProjectPath(s,p);
    public static string ValidateProjectPath(string p) => ConversationStorage.ValidateProjectPath(p);
    public static void DeleteProject(string p, ProjectDeleteMode m) => ConversationStorage.DeleteProject(p,m);
    public static void MarkProjectHandled(string b, string p, ProjectDeleteMode m) { if (BackendContext.IsDsh) DshStorage.MarkProjectHandled(b,p,m); else ConversationStorage.MarkProjectHandled(b,p,m); }
    public static void MarkProjectHandled(TrashSessionInfo s, ProjectDeleteMode m) { if (BackendContext.IsDsh) DshStorage.MarkProjectHandled(s,m); else ConversationStorage.MarkProjectHandled(s,m); }
}
