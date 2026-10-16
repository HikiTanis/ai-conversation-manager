using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexConversationManager;

internal static class ConversationLineage
{
	public const string OriginThreadIdKey = "codex_migrator_origin_thread_id";

	public static bool TryReadPayload(string sessionPath, out Dictionary<string, object> payload)
	{
		payload = null;
		if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
		{
			return false;
		}
		try
		{
			string firstLine;
			using (Stream stream = RolloutStorage.OpenRead(sessionPath))
			using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true, 4096))
			{
				firstLine = reader.ReadLine();
			}
			if (string.IsNullOrWhiteSpace(firstLine) || !(JsonSerialization.NewSerializer().DeserializeObject(firstLine) is Dictionary<string, object> root))
			{
				return false;
			}
			if (!root.TryGetValue("payload", out object value) || !(value is Dictionary<string, object> parsedPayload))
			{
				return false;
			}
			payload = parsedPayload;
			return true;
		}
		catch
		{
			return false;
		}
	}

	public static string ResolveCurrentThreadId(IDictionary<string, object> payload, string fallback)
	{
		string value = GetString(payload, "id");
		if (string.IsNullOrWhiteSpace(value))
		{
			value = GetString(payload, "thread_id");
		}
		if (string.IsNullOrWhiteSpace(value))
		{
			value = GetString(payload, "session_id");
		}
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}

	public static string ResolveOriginThreadId(IDictionary<string, object> payload, string currentThreadId)
	{
		string value = GetString(payload, OriginThreadIdKey);
		return string.IsNullOrWhiteSpace(value) ? currentThreadId : value;
	}

	public static string GetString(IDictionary<string, object> dictionary, string key)
	{
		if (dictionary != null && dictionary.TryGetValue(key, out object value) && value != null)
		{
			return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
		}
		return string.Empty;
	}
}
