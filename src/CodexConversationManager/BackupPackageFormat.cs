using System;
using System.IO;

namespace CodexConversationManager;

internal static class BackupPackageFormat
{
	public static string ConversationExtension => BackendContext.IsDsh ? ".dshchat" : ".codexchat";

	public static string ProjectExtension => BackendContext.IsDsh ? ".dshproject" : ".codexproject";

	public const string LegacyPackExtension = ".codexpack";

	public const string RawBundleExtension = ".codexbundle";

	public static string ExtensionFor(bool includesProjectFiles)
	{
		return includesProjectFiles ? ProjectExtension : ConversationExtension;
	}

	public static bool IsFormalPackage(string path)
	{
		string extension = Path.GetExtension(path) ?? string.Empty;
		return string.Equals(extension, ConversationExtension, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(extension, ProjectExtension, StringComparison.OrdinalIgnoreCase)
			|| (!BackendContext.IsDsh && string.Equals(extension, LegacyPackExtension, StringComparison.OrdinalIgnoreCase));
	}

	public static bool IsSupportedImport(string path)
	{
		return IsFormalPackage(path) || (!BackendContext.IsDsh && string.Equals(Path.GetExtension(path), RawBundleExtension, StringComparison.OrdinalIgnoreCase));
	}

	public static string OpenDialogFilter => BackendContext.IsDsh ? "DSH (*.dshchat;*.dshproject)|*.dshchat;*.dshproject|All files (*.*)|*.*" : UiLanguage.T("Codex 正式备份 (*.codexchat;*.codexproject)|*.codexchat;*.codexproject|旧版备份 (*.codexpack;*.codexbundle)|*.codexpack;*.codexbundle|所有文件 (*.*)|*.*");
}
