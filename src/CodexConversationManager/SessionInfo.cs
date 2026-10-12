using System;

namespace CodexConversationManager;

internal sealed class SessionInfo : NotifyObject
{
	private bool isSelected;

	public string ThreadId { get; set; }

	public string OriginThreadId { get; set; }

	public string SessionPath { get; set; }

	public string RelativePath { get; set; }

	public string Cwd { get; set; }

	public string Title { get; set; }

	public string Preview { get; set; }

	public string Source { get; set; }

	public string ModelProvider { get; set; }

	public string CliVersion { get; set; }

	public string CreatedAt { get; set; }

	public string UpdatedAt { get; set; }

	public DateTime UpdatedDate { get; set; }

	public bool Archived { get; set; }

	public bool Compressed { get; set; }

	public bool IsSubagent { get; set; }

	public string ParentThreadId { get; set; }

	public string ParentDisplayTitle { get; set; }

	public string SubagentDisplayTitle { get; set; }

	public string SubagentPurpose { get; set; }

	public long SizeBytes { get; set; }

	public bool MetadataVerified { get; set; }

	public bool IsSelected
	{
		get
		{
			return isSelected;
		}
		set
		{
			if (isSelected != value)
			{
				isSelected = value;
				RaisePropertyChanged("IsSelected");
			}
		}
	}

	public bool CanSelect => true;

	public bool CanDelete => true;

	public string DisplayTitle
	{
		get
		{
			if (IsSubagent && !string.IsNullOrWhiteSpace(SubagentDisplayTitle))
			{
				return TextHelpers.CleanLine(SubagentDisplayTitle, 160, UiLanguage.T("子代理对话"));
			}
			string value = ((!string.IsNullOrWhiteSpace(Title)) ? Title : Preview);
			return TextHelpers.CleanLine(value, 160, UiLanguage.T("（无标题对话）"));
		}
	}

	public string DisplayTime
	{
		get
		{
			if (!(UpdatedDate == DateTime.MinValue))
			{
				return UpdatedDate.ToString("yyyy-MM-dd  HH:mm");
			}
			return TextHelpers.CleanLine(UpdatedAt, 28, UiLanguage.T("时间未知"));
		}
	}

	public string DisplaySource
	{
		get
		{
			if (IsSubagent)
			{
				return UiLanguage.T("子代理");
			}
			string text = (Source ?? string.Empty).ToLowerInvariant();
			if (text.Contains("dsh")) return "DeepSeek";
			if (text.Contains("vscode"))
			{
				return "VS Code";
			}
			if (text.Contains("app") || text.Contains("desktop"))
			{
				return "Codex";
			}
			if (text.Contains("cli") || text.Contains("terminal"))
			{
				return "CLI";
			}
			if (!string.IsNullOrWhiteSpace(Source))
			{
				return TextHelpers.CleanLine(Source, 18, "Codex");
			}
			return "Codex";
		}
	}

	public string DisplayRelation => IsSubagent ? UiLanguage.T("所属主对话 · ") + TextHelpers.CleanLine(ParentDisplayTitle, 120, UiLanguage.T("未找到所属主对话")) : string.Empty;

	public string DisplaySize => TextHelpers.FormatBytes(SizeBytes);

	public string DisplayPath
	{
		get
		{
			string value = TextHelpers.StripExtendedPrefix(SessionPath);
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
			return RelativePath ?? string.Empty;
		}
	}

	public string DisplayMetadata => UiLanguage.T(Archived ? "已归档" : "未归档") + " · " + DisplayTime + " · " + DisplaySource + " · " + DisplaySize + " · " + ShortId;

	public string ShortId
	{
		get
		{
			if (string.IsNullOrWhiteSpace(ThreadId))
			{
				return string.Empty;
			}
			if (ThreadId.Length > 13)
			{
				return ThreadId.Substring(0, 13) + "…";
			}
			return ThreadId;
		}
	}
}
