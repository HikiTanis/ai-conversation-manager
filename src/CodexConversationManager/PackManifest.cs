using System.Collections.Generic;

namespace CodexConversationManager;

internal sealed class PackManifest
{
	public object native_sessions { get; set; }

	public int schema { get; set; }

	public string created_at { get; set; }

	public string mode { get; set; }

	public string source_project { get; set; }

	public string source_project_name { get; set; }

	public bool includes_subagents { get; set; }

	public string engine { get; set; }

	public string engine_version { get; set; }

	public List<string> bundles { get; set; }

	public List<PackSession> sessions { get; set; }

	public ProjectPayloadInfo project_payload { get; set; }

	public List<PackProject> projects { get; set; }
}
