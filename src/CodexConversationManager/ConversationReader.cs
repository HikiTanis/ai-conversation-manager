using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexConversationManager;

internal static class ConversationReader
{
	public static ConversationReadResult Read(SessionInfo session)
	{
		if (session == null)
		{
			throw new ArgumentNullException("session");
		}
		string text = CodexCatalog.ResolveSessionPath(session);
		if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
		{
			throw new FileNotFoundException("找不到该对话对应的本地会话文件。", text);
		}
		ConversationReadResult conversationReadResult = new ConversationReadResult();
		conversationReadResult.Messages = new List<ConversationMessage>();
		conversationReadResult.SessionPath = text;
		ConversationReadResult conversationReadResult2 = conversationReadResult;
		JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
		using (Stream stream = RolloutStorage.OpenRead(text))
		{
			foreach (Utf8LineRecord line in ReadUtf8Lines(stream))
			{
				if (string.IsNullOrWhiteSpace(line.Text))
				{
					continue;
				}
				try
				{
					bool isUser;
					string displayTime;
					string messageText;
					if (!TryReadDisplayMessage(javaScriptSerializer, line.Text, out isUser, out displayTime, out messageText))
					{
						continue;
					}
					ConversationMessage conversationMessage = new ConversationMessage();
					conversationMessage.RoleLabel = (isUser ? UiLanguage.T("你") : "Codex");
					conversationMessage.DisplayTime = displayTime;
					conversationMessage.IsUser = isUser;
					if (RolloutStorage.IsCompressed(text)) conversationMessage.Text = messageText;
					else conversationMessage.SetDeferredText(text, line.Offset, line.ByteLength);
					conversationReadResult2.Messages.Add(conversationMessage);
				}
				catch
				{
				}
			}
		}
		if (conversationReadResult2.Messages.Count == 0)
		{
			conversationReadResult2.Messages.Add(Notice(UiLanguage.T("没有在该文件中找到可显示的用户或 Codex 文本消息。")));
		}
		return conversationReadResult2;
	}

	internal static string ReadDeferredText(ConversationMessage message)
	{
		if (message == null || string.IsNullOrWhiteSpace(message.DeferredPath) || message.DeferredOffset < 0L || message.DeferredLength <= 0)
		{
			return string.Empty;
		}
		try
		{
			byte[] buffer = new byte[message.DeferredLength];
			using (FileStream stream = new FileStream(message.DeferredPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				stream.Seek(message.DeferredOffset, SeekOrigin.Begin);
				int totalRead = 0;
				while (totalRead < buffer.Length)
				{
					int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
					if (read <= 0)
					{
						throw new EndOfStreamException();
					}
					totalRead += read;
				}
			}
			JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
			bool isUser;
			string displayTime;
			string messageText;
			if (TryReadDisplayMessage(serializer, DecodeUtf8Line(buffer, buffer.Length), out isUser, out displayTime, out messageText))
			{
				return messageText;
			}
		}
		catch
		{
		}
		return UiLanguage.IsEnglish ? "Message content is no longer available." : "消息正文已不可用。";
	}

	private static bool TryReadDisplayMessage(JavaScriptSerializer serializer, string line, out bool isUser, out string displayTime, out string messageText)
	{
		isUser = false;
		displayTime = string.Empty;
		messageText = string.Empty;
		if (!(serializer.DeserializeObject(line) is Dictionary<string, object> item) || GetString(item, "type") != "response_item")
		{
			return false;
		}
		object value;
		Dictionary<string, object> payload = item.TryGetValue("payload", out value) ? value as Dictionary<string, object> : null;
		if (payload == null || GetString(payload, "type") != "message")
		{
			return false;
		}
		string role = GetString(payload, "role");
		isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
		if (!isUser && !string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		messageText = ExtractText(payload);
		if (isUser)
		{
			messageText = StripAmbientContext(messageText);
		}
		messageText = NormalizeMessage(messageText);
		if (string.IsNullOrWhiteSpace(messageText))
		{
			return false;
		}
		displayTime = FormatTime(GetString(item, "timestamp"));
		return true;
	}

	private static IEnumerable<Utf8LineRecord> ReadUtf8Lines(Stream stream)
	{
		byte[] readBuffer = new byte[65536];
		using (MemoryStream lineBuffer = new MemoryStream())
		{
			long lineOffset = 0L;
			int bytesRead;
			while ((bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
			{
				long blockOffset = stream.Position - bytesRead;
				int segmentStart = 0;
				for (int index = 0; index < bytesRead; index++)
				{
					if (readBuffer[index] != (byte)'\n')
					{
						continue;
					}
					int segmentLength = index - segmentStart;
					if (segmentLength > 0)
					{
						lineBuffer.Write(readBuffer, segmentStart, segmentLength);
					}
					if (lineBuffer.Length > int.MaxValue)
					{
						throw new InvalidDataException("Conversation line is too large to preview.");
					}
					int lineLength = (int)lineBuffer.Length;
					yield return new Utf8LineRecord(lineOffset, lineLength, DecodeUtf8Line(lineBuffer.GetBuffer(), lineLength));
					lineBuffer.SetLength(0L);
					lineBuffer.Position = 0L;
					lineOffset = blockOffset + index + 1L;
					segmentStart = index + 1;
				}
				if (segmentStart < bytesRead)
				{
					lineBuffer.Write(readBuffer, segmentStart, bytesRead - segmentStart);
				}
			}
			if (lineBuffer.Length > 0L)
			{
				if (lineBuffer.Length > int.MaxValue)
				{
					throw new InvalidDataException("Conversation line is too large to preview.");
				}
				int lineLength = (int)lineBuffer.Length;
				yield return new Utf8LineRecord(lineOffset, lineLength, DecodeUtf8Line(lineBuffer.GetBuffer(), lineLength));
			}
		}
	}

	private static string DecodeUtf8Line(byte[] buffer, int length)
	{
		string value = Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r');
		if (value.Length > 0 && value[0] == '\uFEFF')
		{
			value = value.Substring(1);
		}
		return value;
	}

	private sealed class Utf8LineRecord
	{
		public Utf8LineRecord(long offset, int byteLength, string text)
		{
			Offset = offset;
			ByteLength = byteLength;
			Text = text;
		}

		public long Offset { get; }

		public int ByteLength { get; }

		public string Text { get; }
	}

	public static string ReadTitleCandidate(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return string.Empty;
		}
		JavaScriptSerializer serializer = JsonSerialization.NewSerializer();
		int lines = 0;
		int characters = 0;
		try
		{
			using Stream stream = RolloutStorage.OpenRead(path);
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 8192);
			string line;
			while (lines < 240 && characters < 2000000 && (line = reader.ReadLine()) != null)
			{
				lines++;
				characters += line.Length;
				if (!(serializer.DeserializeObject(line) is Dictionary<string, object> item) || GetString(item, "type") != "response_item")
				{
					continue;
				}
				object value;
				Dictionary<string, object> payload = item.TryGetValue("payload", out value) ? value as Dictionary<string, object> : null;
				if (payload == null || GetString(payload, "type") != "message" || !string.Equals(GetString(payload, "role"), "user", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string candidate = NormalizeMessage(StripAmbientContext(ExtractText(payload)));
				if (IsTitleNoise(candidate))
				{
					continue;
				}
				return TextHelpers.CleanLine(candidate, 76, string.Empty);
			}
		}
		catch
		{
		}
		return string.Empty;
	}

	private static bool IsTitleNoise(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return true;
		}
		string text = value.Trim();
		return text.StartsWith("<turn_aborted", StringComparison.OrdinalIgnoreCase) ||
			text.StartsWith("The following is the Codex agent history", StringComparison.OrdinalIgnoreCase) ||
			text.IndexOf("whose request action you are assessing", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string ExtractText(Dictionary<string, object> payload)
	{
		if (!payload.TryGetValue("content", out var value) || value == null)
		{
			return string.Empty;
		}
		List<string> list = new List<string>();
		if (!(value is object[] array))
		{
			return string.Empty;
		}
		object[] array2 = array;
		foreach (object obj in array2)
		{
			if (!(obj is Dictionary<string, object> item))
			{
				continue;
			}
			string text = GetString(item, "type");
			string text2 = GetString(item, "text");
			switch (text)
			{
			case "input_text":
			case "output_text":
			case "text":
				if (!string.IsNullOrWhiteSpace(text2))
				{
					list.Add(text2);
					continue;
				}
				break;
			}
			if (text == "input_image")
			{
				list.Add("［图片］");
			}
		}
		return string.Join("\n\n", list.ToArray());
	}

	private static string StripAmbientContext(string value)
	{
		string input = value ?? string.Empty;
		input = Regex.Replace(input, "<(recommended_plugins|environment_context|in-app-browser-context)\\b[^>]*>.*?</\\1>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
		return Regex.Replace(input, "<image\\b[^>]*>", "［图片］", RegexOptions.IgnoreCase);
	}

	private static string NormalizeMessage(string value)
	{
		string input = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
		return Regex.Replace(input, "\\n{3,}", "\n\n");
	}

	private static string FormatTime(string value)
	{
		if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var result))
		{
			return string.Empty;
		}
		return result.ToLocalTime().ToString("MM-dd HH:mm");
	}

	private static ConversationMessage Notice(string text)
	{
		ConversationMessage conversationMessage = new ConversationMessage();
		conversationMessage.RoleLabel = UiLanguage.T("提示");
		conversationMessage.Text = text;
		conversationMessage.DisplayTime = string.Empty;
		conversationMessage.IsNotice = true;
		return conversationMessage;
	}

	private static string GetString(Dictionary<string, object> item, string key)
	{
		if (!item.TryGetValue(key, out var value) || value == null)
		{
			return string.Empty;
		}
		return Convert.ToString(value);
	}
}
