using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexConversationManager;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		if (args.Length == 1 && args[0] == "--desktop-read") return DesktopReadBridge.Run();
		if (args.Length == 1 && args[0] == "--desktop-operation") return DesktopReadBridge.Run(true);
		BackendContext.Initialize(args);
		if (args.Contains("--dsh-ui-test")) return DshUiTests.Run(args.Select(a => a == "--dsh-ui-test" ? "--ui-test" : a).ToArray());
		if (args.Contains("--provider-test")) return ProviderUiTests.Run(args);
		string report = string.Empty;
		string text = string.Empty;
		string language = string.Empty;
		string output = string.Empty;
		string text2 = string.Empty;
		string text3 = string.Empty;
		bool flag = false;
		for (int i = 0; i < args.Length; i++)
		{
			if (string.Equals(args[i], "--self-test", StringComparison.OrdinalIgnoreCase))
			{
				flag = true;
			}
			else if (string.Equals(args[i], "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				report = args[++i];
			}
			else if (string.Equals(args[i], "--bundle-test", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
			{
				text = args[++i];
				output = args[++i];
			}
			else if (string.Equals(args[i], "--render-test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				text2 = args[++i];
			}
			else if (string.Equals(args[i], "--chrome-test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
			{
				text3 = args[++i];
			}
			else if ((string.Equals(args[i], "--language", StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
			{
				language = args[++i];
			}
		}
		UiLanguage.Initialize(language);
		if (flag)
		{
			return RunSelfTest(report);
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			return RunBundleTest(text, output);
		}
		if (!string.IsNullOrWhiteSpace(text2))
		{
			if (Path.GetFileNameWithoutExtension(text2).IndexOf("paginated-completion", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return RunPaginatedCompletionRenderTest(text2);
			}
			if (Path.GetFileNameWithoutExtension(text2).IndexOf("dialog-theme", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return RunDialogThemeRenderTest(text2);
			}
			return RunRenderTest(text2);
		}
		if (!string.IsNullOrWhiteSpace(text3))
		{
			return RunChromeTest(text3);
		}
		try
		{
			Application application = new Application();
			application.ShutdownMode = ShutdownMode.OnMainWindowClose;
			Application application2 = application;
			MainWindowController mainWindowController = new MainWindowController();
			application2.MainWindow = mainWindowController.Window;
			return application2.Run(mainWindowController.Window);
		}
		catch (Exception ex)
		{
			string text5 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexConversationManager-startup-error.txt");
			try
			{
				File.WriteAllText(text5, ex.ToString(), Encoding.UTF8);
			}
			catch
			{
			}
			MessageBox.Show(UiLanguage.T("窗口启动失败：\n\n" + ex.Message + "\n\n诊断日志：" + text5), UiLanguage.T("Codex 对话与项目管理器"), MessageBoxButton.OK, MessageBoxImage.Hand);
			return 2;
		}
	}

	private static int RunBundleTest(string threadId, string output)
	{
		try
		{
			List<SessionInfo> nativeSessions = NativeSessionCatalog.ScanDefault();
			CatalogResult catalogResult = CodexCatalog.Build(nativeSessions);
			SessionInfo sessionInfo = catalogResult.Projects.SelectMany((ProjectGroup x) => x.Sessions).FirstOrDefault((SessionInfo x) => string.Equals(x.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));
			if (sessionInfo == null)
			{
				throw new InvalidOperationException("thread not found: " + threadId);
			}
			ExactBundleWriter.CreateSingleSessionBundle(sessionInfo, output);
			return 0;
		}
		catch
		{
			return 1;
		}
	}

	private static int RunChromeTest(string output)
	{
		try
		{
			Application application = new Application();
			application.ShutdownMode = ShutdownMode.OnMainWindowClose;
			Application application2 = application;
			MainWindowController controller = new MainWindowController();
			application2.MainWindow = controller.Window;
			bool tested = false;
			controller.Window.ContentRendered += async delegate
			{
				if (!tested)
				{
					tested = true;
					await controller.InitialLoadTask;
					IntPtr handle = new WindowInteropHelper(controller.Window).Handle;
					string report = ChromeVerifier.Verify(handle);
					controller.Window.WindowState = WindowState.Maximized;
					await Task.Delay(180);
					controller.Window.UpdateLayout();
					if (!ChromeVerifier.VerifyMaximizedWorkArea(handle, out string maximizedReport))
					{
						throw new InvalidOperationException("maximized window extended outside the monitor work area\r\n" + maximizedReport);
					}
					File.WriteAllText(output, report + "\r\n" + maximizedReport, Encoding.UTF8);
					controller.EndBusyForTest();
					controller.Window.Close();
				}
			};
			return application2.Run(controller.Window);
		}
		catch (Exception ex)
		{
			try
			{
				File.WriteAllText(output + ".error.txt", ex.ToString(), Encoding.UTF8);
			}
			catch
			{
			}
			return 1;
		}
	}

	private static RenderTargetBitmap RenderWithDialogBackground(FrameworkElement visual, int width, int height)
	{
		RenderTargetBitmap raw = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		raw.Render(visual);
		DrawingVisual composed = new DrawingVisual();
		using (DrawingContext drawing = composed.RenderOpen())
		{
			drawing.DrawRectangle(DialogUi.Brush("#F7F7F4"), null, new Rect(0.0, 0.0, width, height));
			drawing.DrawImage(raw, new Rect(0.0, 0.0, width, height));
		}
		RenderTargetBitmap result = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
		result.Render(composed);
		return result;
	}

	private static int RunPaginatedCompletionRenderTest(string output)
	{
		try
		{
			Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
			Window preview = AppDialog.CreatePaginatedCompletionPreviewForTest();
			application.MainWindow = preview;
			bool captured = false;
			preview.ContentRendered += async delegate
			{
				if (captured)
				{
					return;
				}
				captured = true;
				await Task.Delay(250);
				preview.UpdateLayout();
				FrameworkElement visual = preview;
				int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
				int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
				RenderTargetBitmap bitmap = RenderWithDialogBackground(visual, width, height);
				PngBitmapEncoder encoder = new PngBitmapEncoder
				{
					Frames = { BitmapFrame.Create(bitmap) }
				};
				using (FileStream stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					encoder.Save(stream);
				}
				preview.Close();
			};
			return application.Run(preview);
		}
		catch (Exception ex)
		{
			try
			{
				File.WriteAllText(output + ".error.txt", ex.ToString(), Encoding.UTF8);
			}
			catch
			{
			}
			return 1;
		}
	}

	private static int RunDialogThemeRenderTest(string output)
	{
		try
		{
			Application application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
			Window preview = DialogUi.CreateThemePreviewForTest();
			application.MainWindow = preview;
			bool captured = false;
			preview.ContentRendered += async delegate
			{
				if (captured) return;
				captured = true;
				System.Windows.Controls.ComboBox previewCombo = preview.Tag as System.Windows.Controls.ComboBox;
				if (previewCombo != null)
				{
					previewCombo.IsDropDownOpen = true;
				}
				await Task.Delay(350);
				preview.UpdateLayout();
				FrameworkElement visual = preview;
				int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
				int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
				RenderTargetBitmap bitmap = RenderWithDialogBackground(visual, width, height);
				BitmapSource finalBitmap = bitmap;
				System.Windows.Controls.Primitives.Popup popup = previewCombo?.Template.FindName("PART_Popup", previewCombo) as System.Windows.Controls.Primitives.Popup;
				if (popup?.Child is FrameworkElement popupVisual && popupVisual.ActualWidth > 0.0 && popupVisual.ActualHeight > 0.0)
				{
					int popupWidth = Math.Max(1, (int)Math.Ceiling(popupVisual.ActualWidth));
					int popupHeight = Math.Max(1, (int)Math.Ceiling(popupVisual.ActualHeight));
					RenderTargetBitmap popupBitmap = new RenderTargetBitmap(popupWidth, popupHeight, 96.0, 96.0, PixelFormats.Pbgra32);
					popupBitmap.Render(popupVisual);
					Point popupOffset = previewCombo.TranslatePoint(new Point(0.0, previewCombo.ActualHeight + 5.0), visual);
					int combinedWidth = Math.Max(width, (int)Math.Ceiling(popupOffset.X + popupVisual.ActualWidth));
					int combinedHeight = Math.Max(height, (int)Math.Ceiling(popupOffset.Y + popupVisual.ActualHeight));
					DrawingVisual composed = new DrawingVisual();
					using (DrawingContext drawing = composed.RenderOpen())
					{
						drawing.DrawImage(bitmap, new Rect(0.0, 0.0, width, height));
						drawing.DrawImage(popupBitmap, new Rect(popupOffset.X, popupOffset.Y, popupVisual.ActualWidth, popupVisual.ActualHeight));
					}
					RenderTargetBitmap combined = new RenderTargetBitmap(combinedWidth, combinedHeight, 96.0, 96.0, PixelFormats.Pbgra32);
					combined.Render(composed);
					finalBitmap = combined;
				}
				PngBitmapEncoder encoder = new PngBitmapEncoder
				{
					Frames = { BitmapFrame.Create(finalBitmap) }
				};
				using (FileStream stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					encoder.Save(stream);
				}
				preview.Close();
			};
			return application.Run(preview);
		}
		catch (Exception ex)
		{
			try
			{
				File.WriteAllText(output + ".error.txt", ex.ToString(), Encoding.UTF8);
			}
			catch
			{
			}
			return 1;
		}
	}

	private static int RunRenderTest(string output)
	{
		try
		{
			Application application = new Application();
			application.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs eventArgs)
			{
				try
				{
					File.WriteAllText(output + ".error.txt", eventArgs.Exception.ToString(), Encoding.UTF8);
				}
				catch
				{
				}
				eventArgs.Handled = true;
				application.Shutdown(1);
			};
			application.ShutdownMode = ShutdownMode.OnMainWindowClose;
			Application application2 = application;
			MainWindowController controller = new MainWindowController();
			application2.MainWindow = controller.Window;
			bool captured = false;
			controller.Window.ContentRendered += async delegate
			{
				if (!captured)
				{
					captured = true;
					await controller.InitialLoadTask;
					string renderName = Path.GetFileNameWithoutExtension(output).ToLowerInvariant();
					if (renderName.Contains("user-size"))
					{
						controller.Window.WindowState = WindowState.Normal;
						controller.Window.Width = 931.0;
						controller.Window.Height = 639.0;
					}
					else if (renderName.Contains("compact"))
					{
						controller.Window.WindowState = WindowState.Normal;
						controller.Window.Width = 900.0;
						controller.Window.Height = 620.0;
					}
					else if (renderName.Contains("fullscreen") || renderName.Contains("main-window-max"))
					{
						controller.Window.WindowState = WindowState.Maximized;
						await Task.Delay(180);
						controller.Window.UpdateLayout();
					}
					if (renderName.Contains("import"))
					{
						if (renderName.Contains("progress"))
						{
							controller.ShowImportProgressForTest();
						}
						else
						{
							controller.ShowImportForTest();
						}
						if (renderName.Contains("project"))
						{
							controller.ShowProjectRestoreForTest();
						}
						if (!controller.TestImportModeHelpForTest())
						{
							throw new InvalidOperationException("import mode help did not follow the selected conflict mode");
						}
					}
					else if (renderName.Contains("preview"))
					{
						bool previewOpened = renderName.Contains("subagent") ? await controller.ShowFirstSubagentConversationForTest(string.Empty) : await controller.ShowFirstConversationForTest(string.Empty);
						if (!previewOpened)
						{
							throw new InvalidOperationException("conversation preview did not open");
						}
						if (!controller.TestConversationAutomaticLayoutForTest())
						{
							throw new InvalidOperationException("conversation preview did not auto-fit with fixed insets");
						}
						if (renderName.Contains("responsive") && !controller.TestConversationFollowsWindowResizeForTest())
						{
							throw new InvalidOperationException("conversation preview did not follow main-window size changes");
						}
						if (renderName.Contains("long") && !controller.TestLongConversationPreviewForTest())
						{
							throw new InvalidOperationException("long conversation preview was truncated, did not open at the latest message, or its navigation rail failed");
						}
						if (renderName.Contains("rail") && !controller.ShowConversationNavigationPreviewForTest())
						{
							throw new InvalidOperationException("user-message navigation hover preview did not open");
						}
					}
					else if (renderName.Contains("subagent"))
					{
						if (!controller.ShowSubagentViewForTest(string.Empty))
						{
							throw new InvalidOperationException("subagent view did not separate linked child conversations");
						}
						if (renderName.Contains("selection") && !controller.TestSubagentSelectionToggleForTest())
						{
							throw new InvalidOperationException("subagent all/select-none/selected-delete interaction failed");
						}
						if (!(await controller.WaitForSelectedProjectStorageForTest()))
						{
							throw new InvalidOperationException("project storage metrics did not complete");
						}
					}
					else if (renderName.Contains("main-selection"))
					{
						if (!controller.ShowMainSessionViewForTest(string.Empty))
						{
							throw new InvalidOperationException("main session view did not show project conversations");
						}
						if (!controller.TestMainSelectionToggleForTest())
						{
							throw new InvalidOperationException("main all/select-none/selected-delete interaction failed");
						}
						if (!controller.TestFilteredSelectionStateForTest())
						{
							throw new InvalidOperationException("search-hidden selection controls did not follow the visible result set");
						}
						if (!controller.TestSessionTypeSwitchSelectionStateForTest())
						{
							throw new InvalidOperationException("main/subagent switch retained stale selection controls");
						}
						if (!(await controller.WaitForSelectedProjectStorageForTest()))
						{
							throw new InvalidOperationException("project storage metrics did not complete");
						}
					}
					else if (renderName.Contains("default-backup"))
					{
						if (!controller.IsConversationBackupDefaultForTest())
						{
							throw new InvalidOperationException("conversation-only backup was not the default mode");
						}
					}
					else if (renderName.Contains("conversation-backup"))
					{
						if (!controller.SelectConversationBackupForTest(string.Empty))
						{
							throw new InvalidOperationException("conversation backup mode did not select a conversation");
						}
					}
					else if (!controller.SelectProjectBackupForTest(string.Empty))
					{
						throw new InvalidOperationException("project backup mode did not select a project");
					}
					else if (!(await controller.WaitForSelectedProjectStorageForTest()))
					{
						throw new InvalidOperationException("project storage metrics did not complete");
					}
					await Task.Delay(350);
					controller.Window.UpdateLayout();
					if (renderName.Contains("import") && !controller.TestImportLayoutForTest())
					{
						throw new InvalidOperationException("import action buttons are clipped or the project conflict field is using the native template");
					}
					if (!(controller.Window.Content is FrameworkElement visual))
					{
						throw new InvalidOperationException("window content unavailable");
					}
					int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
					int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
					RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);
					bitmap.Render(visual);
					PngBitmapEncoder encoder = new PngBitmapEncoder
					{
						Frames = { BitmapFrame.Create(bitmap) }
					};
					using (FileStream stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						encoder.Save(stream);
					}
					controller.EndBusyForTest();
					controller.Window.Close();
				}
			};
			return application2.Run(controller.Window);
		}
		catch (Exception ex)
		{
			try
			{
				File.WriteAllText(output + ".error.txt", ex.ToString(), Encoding.UTF8);
			}
			catch
			{
			}
			return 1;
		}
	}

	private static void RunRealOfficialThreadDeleteIntegrationTest()
	{
		string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "official-thread-delete-integration-" + Guid.NewGuid().ToString("N"));
		string threadId = "44444444-4444-4444-8444-444444444444";
		string sessionDirectory = Path.Combine(root, "sessions", "2026", "01", "02");
		string sessionPath = Path.Combine(sessionDirectory, "rollout-2026-01-02T03-04-05-" + threadId + ".jsonl");
		try
		{
			Directory.CreateDirectory(sessionDirectory);
			Dictionary<string, object> sessionMeta = new Dictionary<string, object>
			{
				{ "timestamp", "2026-01-02T03:04:05Z" },
				{ "type", "session_meta" },
				{ "payload", new Dictionary<string, object>
					{
						{ "id", threadId },
						{ "timestamp", "2026-01-02T03:04:05Z" },
						{ "cwd", Path.Combine(root, "project") },
						{ "originator", "codex-desktop" },
						{ "cli_version", "integration-test" },
						{ "source", "vscode" },
						{ "model_provider", "openai" }
					}
				}
			};
			File.WriteAllText(sessionPath, JsonSerialization.NewSerializer().Serialize(sessionMeta) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			CodexAppServerThreadDeletion.TestOverride = null;
			OfficialThreadDeletionResult result = CodexAppServerThreadDeletion.DeleteThread(root, threadId);
			if (!result.Succeeded || File.Exists(sessionPath))
			{
				throw new InvalidOperationException("real Codex thread/delete integration test did not remove the disposable rollout");
			}
		}
		finally
		{
			CodexAppServerThreadDeletion.TestOverride = null;
			try
			{
				if (Directory.Exists(root))
				{
					Directory.Delete(root, recursive: true);
				}
			}
			catch
			{
			}
		}
	}

	private static string RunFeatureSafetyTest()
	{
		string environmentVariable = Environment.GetEnvironmentVariable("CODEX_HOME");
		bool runRealOfficialDelete = Environment.GetEnvironmentVariable("CODEX_MIGRATOR_REAL_OFFICIAL_DELETE_TEST") == "1";
		List<string> officialDeletes = new List<string>();
		string text = Path.Combine(Path.GetTempPath(), "codex-migrator-feature-test-" + Guid.NewGuid().ToString("N"));
		string projectDeleteTest = Path.Combine(Path.GetTempPath(), "codex-migrator-project-delete-test-" + Guid.NewGuid().ToString("N"));
		string projectPayloadTest = Path.Combine(Path.GetTempPath(), "codex-migrator-project-payload-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(text);
			Environment.SetEnvironmentVariable("CODEX_HOME", text);
			ConversationIndexMaintenance.LogRootOverride = Path.Combine(text, "codex-desktop-logs");
			CodexDesktopTaskCache.UserDataRootOverride = Path.Combine(text, "desktop-web-cache");
			string olderRuntime = Path.Combine(text, "codex-runtime-older.exe");
			string newerRuntime = Path.Combine(text, "codex-runtime-newer.exe");
			File.WriteAllText(olderRuntime, "synthetic executable without PE version metadata", Encoding.UTF8);
			File.WriteAllText(newerRuntime, "synthetic executable without PE version metadata", Encoding.UTF8);
			CodexAppServerThreadDeletion.VersionOutputOverrideForTest = path => path.IndexOf("newer", StringComparison.OrdinalIgnoreCase) >= 0 ? "codex-cli 0.150.0-alpha.12.2" : "codex-cli 0.148.0";
			try
			{
				string selectedRuntime = CodexAppServerThreadDeletion.SelectNewestCodexCandidateForTest(new string[2] { olderRuntime, newerRuntime });
				if (!string.Equals(selectedRuntime, newerRuntime, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("Codex runtime CLI-version probe did not select the newer executable");
				}
			}
			finally
			{
				CodexAppServerThreadDeletion.VersionOutputOverrideForTest = null;
			}
			string desktopRuntimeRoot = Path.Combine(text, "desktop-runtime");
			string nestedDesktopRuntime = Path.Combine(desktopRuntimeRoot, "bin", "a5c9108151f176e9", "codex.exe");
			string versionedDesktopRuntime = Path.Combine(desktopRuntimeRoot, "app-0.150.0", "resources", "bin", "codex.exe");
			Directory.CreateDirectory(Path.GetDirectoryName(nestedDesktopRuntime));
			Directory.CreateDirectory(Path.GetDirectoryName(versionedDesktopRuntime));
			IReadOnlyList<string> desktopCandidates = CodexAppServerThreadDeletion.CodexDesktopCandidatesForTest(desktopRuntimeRoot);
			if (!desktopCandidates.Contains(Path.Combine(desktopRuntimeRoot, "bin", "codex.exe"), StringComparer.OrdinalIgnoreCase) ||
				!desktopCandidates.Contains(nestedDesktopRuntime, StringComparer.OrdinalIgnoreCase) ||
				!desktopCandidates.Contains(versionedDesktopRuntime, StringComparer.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Codex Desktop runtime discovery did not include direct and versioned install paths");
			}
			if (runRealOfficialDelete)
			{
				RunRealOfficialThreadDeleteIntegrationTest();
			}
			CodexAppServerThreadDeletion.TestOverride = delegate(string _, string threadId)
			{
				officialDeletes.Add(threadId);
				return new OfficialThreadDeletionResult
				{
					Succeeded = true,
					CodexPath = "self-test-codex.exe",
					Error = string.Empty
				};
			};
			string text2 = Path.Combine(text, "sessions", "2026", "01", "02");
			Directory.CreateDirectory(text2);
			string testThreadId = "11111111-1111-4111-8111-111111111111";
			string text3 = Path.Combine(text2, "rollout-2026-01-02T03-04-05-" + testThreadId + ".jsonl");
			JavaScriptSerializer javaScriptSerializer = JsonSerialization.NewSerializer();
			Dictionary<string, object> dictionary = new Dictionary<string, object>();
			dictionary.Add("ordinal", 0);
			dictionary.Add("timestamp", "2026-01-02T03:04:05Z");
			dictionary.Add("type", "session_meta");
			dictionary.Add("payload", new Dictionary<string, object>
			{
				{ "id", testThreadId },
				{ "timestamp", "2026-01-02T03:04:05Z" },
				{ "cwd", "C:\\fake-project" },
				{ "originator", "codex_cli_rs" },
				{ "cli_version", "test" },
				{ "source", "cli" },
				{ "model_provider", "openai" },
				{ "history_mode", "paginated" }
			});
			Dictionary<string, object> obj = dictionary;
			Dictionary<string, object> turnContext = new Dictionary<string, object>();
			turnContext.Add("ordinal", 1);
			turnContext.Add("timestamp", "2026-01-02T03:04:05Z");
			turnContext.Add("type", "turn_context");
			turnContext.Add("payload", new Dictionary<string, object>
			{
				{ "turn_id", "22222222-2222-4222-8222-222222222222" },
				{ "cwd", "C:\\fake-project" },
				{ "approval_policy", "never" },
				{ "model", "test-model" }
			});
			Dictionary<string, object> objContext = turnContext;
			Dictionary<string, object> dictionary2 = new Dictionary<string, object>();
			dictionary2.Add("timestamp", "2026-01-02T03:04:05Z");
			dictionary2.Add("type", "response_item");
			dictionary2.Add("ordinal", 2);
			dictionary2.Add("payload", new Dictionary<string, object>
			{
				{ "type", "message" },
				{ "role", "user" },
				{
					"content",
					new object[1]
					{
						new Dictionary<string, object>
						{
							{ "type", "input_text" },
							{ "text", "<environment_context><cwd>C:\\\\old</cwd></environment_context>\n真正的问题" }
						}
					}
				}
			});
			Dictionary<string, object> obj2 = dictionary2;
			Dictionary<string, object> dictionary3 = new Dictionary<string, object>();
			dictionary3.Add("timestamp", "2026-01-02T03:05:06Z");
			dictionary3.Add("type", "response_item");
			dictionary3.Add("ordinal", 3);
			dictionary3.Add("payload", new Dictionary<string, object>
			{
				{ "type", "message" },
				{ "role", "assistant" },
				{
					"content",
					new object[1]
					{
						new Dictionary<string, object>
						{
							{ "type", "output_text" },
							{ "text", "这是回答。" }
						}
					}
				}
			});
			Dictionary<string, object> obj3 = dictionary3;
			Dictionary<string, object> duplicateUserMessage = new Dictionary<string, object>
			{
				{ "timestamp", "2026-01-02T03:06:07Z" },
				{ "type", "response_item" },
				{ "ordinal", 4 },
				{
					"payload",
					new Dictionary<string, object>
					{
						{ "type", "message" },
						{ "role", "user" },
						{
							"content",
							new object[1]
							{
								new Dictionary<string, object>
								{
									{ "type", "input_text" },
									{ "text", "继续" }
								}
							}
						}
					}
				}
			};
			Dictionary<string, object> duplicateUserMessage2 = new Dictionary<string, object>(duplicateUserMessage);
			duplicateUserMessage2["ordinal"] = 5;
			duplicateUserMessage2["timestamp"] = "2026-01-02T03:06:08Z";
			string fixtureContents = javaScriptSerializer.Serialize(obj) + Environment.NewLine + javaScriptSerializer.Serialize(objContext) + Environment.NewLine + javaScriptSerializer.Serialize(obj2) + Environment.NewLine + javaScriptSerializer.Serialize(obj3) + Environment.NewLine + javaScriptSerializer.Serialize(duplicateUserMessage) + Environment.NewLine + javaScriptSerializer.Serialize(duplicateUserMessage2) + Environment.NewLine;
			File.WriteAllText(text3, fixtureContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			ThreadIndexMetadata paginatedMetadata = TargetedThreadIndexer.ReadMetadataForTest(text3, "功能测试", "真正的问题");
			if (!string.Equals(paginatedMetadata.HistoryMode, CodexHistoryMode.Paginated, StringComparison.Ordinal) ||
				!string.Equals(CodexHistoryMode.Normalize(null, "legacy-default-test"), CodexHistoryMode.Legacy, StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Codex history mode metadata test failed");
			}
			SessionInfo sessionInfo = new SessionInfo();
			sessionInfo.ThreadId = testThreadId;
			sessionInfo.SessionPath = text3;
			string missingOrdinalPath = Path.Combine(text2, "rollout-missing-ordinal-" + testThreadId + ".jsonl");
			Dictionary<string, object> missingOrdinalMeta = new Dictionary<string, object>(obj);
			missingOrdinalMeta.Remove("ordinal");
			File.WriteAllText(missingOrdinalPath, javaScriptSerializer.Serialize(missingOrdinalMeta) + Environment.NewLine + javaScriptSerializer.Serialize(objContext) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			bool missingOrdinalRejected = false;
			try
			{
				TargetedThreadIndexer.ReadMetadataForTest(missingOrdinalPath, "无效序号", "无效序号");
			}
			catch (InvalidDataException ex)
			{
				missingOrdinalRejected = ex.Message.IndexOf("ordinal", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			string gapOrdinalPath = Path.Combine(text2, "rollout-gap-ordinal-" + testThreadId + ".jsonl");
			Dictionary<string, object> gapRecord = new Dictionary<string, object>(obj3);
			gapRecord["ordinal"] = 4;
			File.WriteAllText(gapOrdinalPath, javaScriptSerializer.Serialize(obj) + Environment.NewLine + javaScriptSerializer.Serialize(objContext) + Environment.NewLine + javaScriptSerializer.Serialize(obj2) + Environment.NewLine + javaScriptSerializer.Serialize(gapRecord) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			bool gapOrdinalRejected = false;
			try
			{
				TargetedThreadIndexer.ReadMetadataForTest(gapOrdinalPath, "断号序号", "断号序号");
			}
			catch (InvalidDataException ex)
			{
				gapOrdinalRejected = ex.Message.IndexOf("ordinal", StringComparison.OrdinalIgnoreCase) >= 0;
			}
			if (!missingOrdinalRejected || !gapOrdinalRejected)
			{
				throw new InvalidOperationException("paginated ordinal validation test failed");
			}
			File.Delete(missingOrdinalPath);
			File.Delete(gapOrdinalPath);
			sessionInfo.RelativePath = "2026/01/02/" + Path.GetFileName(text3);
			sessionInfo.Title = "功能测试";
			sessionInfo.Cwd = "C:\\fake-project";
			sessionInfo.Preview = "真正的问题";
			sessionInfo.Source = "cli";
			sessionInfo.CreatedAt = "2026-01-02T03:04:05Z";
			sessionInfo.UpdatedAt = "2026-01-02T03:05:06Z";
			sessionInfo.UpdatedDate = new DateTime(2026, 1, 2, 3, 5, 6, DateTimeKind.Utc);
			SessionInfo session = sessionInfo;
			ConversationReadResult conversationReadResult = ConversationReader.Read(session);
			if (conversationReadResult.Messages.Count != 4 ||
				conversationReadResult.Messages.Any(message => message.DeferredLength <= 0 || !string.Equals(message.DeferredPath, text3, StringComparison.OrdinalIgnoreCase)) ||
				conversationReadResult.Messages[0].Text != "真正的问题" || conversationReadResult.Messages[1].Text != "这是回答。" ||
				conversationReadResult.Messages[2].Text != "继续" || conversationReadResult.Messages[3].Text != "继续")
			{
				throw new InvalidOperationException("conversation preview lazy parser or duplicate preservation test failed");
			}
			if (BackupPackageFormat.ExtensionFor(includesProjectFiles: false) != ".codexchat" || BackupPackageFormat.ExtensionFor(includesProjectFiles: true) != ".codexproject" ||
				!BackupPackageFormat.IsFormalPackage("legacy.codexpack") || !BackupPackageFormat.IsFormalPackage("chat.codexchat") || !BackupPackageFormat.IsFormalPackage("project.codexproject") ||
				BackupPackageFormat.IsFormalPackage("raw.codexbundle") || !BackupPackageFormat.IsSupportedImport("raw.codexbundle"))
			{
				throw new InvalidOperationException("formal backup extension test failed");
			}
			string transactionFolder = Path.Combine(text2, "native-import-transaction");
			Directory.CreateDirectory(transactionFolder);
			string rollbackActive = Path.Combine(transactionFolder, "rollout-99999999-9999-4999-8999-999999999999.jsonl");
			string rollbackOriginal = "rollback-original";
			File.WriteAllText(rollbackActive, rollbackOriginal, Encoding.UTF8);
			NativeImportTransaction rollbackTransaction = NativeImportTransaction.Begin(text);
			string rollbackBackup = rollbackActive + ".ccm-txn-bak-100";
			File.Copy(rollbackActive, rollbackBackup);
			File.WriteAllText(rollbackActive, "rollback-mutated", Encoding.UTF8);
			string rollbackImportedId = "77777777-7777-4777-8777-777777777777";
			string rollbackImported = Path.Combine(transactionFolder, "rollout-imported-" + rollbackImportedId + ".jsonl");
			string rollbackUnrelated = Path.Combine(transactionFolder, "rollout-unrelated-88888888-8888-4888-8888-888888888888.jsonl");
			File.WriteAllText(rollbackImported, "planned-import", Encoding.UTF8);
			File.WriteAllText(rollbackUnrelated, "unrelated-new-file", Encoding.UTF8);
			rollbackTransaction.TrackImportedSessionFiles(new string[3] { rollbackImported, rollbackUnrelated, rollbackActive }, new string[2] { rollbackImportedId, "99999999-9999-4999-8999-999999999999" });
			ImportTransactionRollbackResult rollbackResult = rollbackTransaction.RollbackAndDeleteTemporaryBackups();
			if (rollbackResult.RestoredCount != 1 || rollbackResult.DeletedCount != 1 || rollbackResult.RemovedImportedCount != 1 || File.Exists(rollbackBackup) || File.Exists(rollbackImported) || !File.Exists(rollbackUnrelated) || File.ReadAllText(rollbackActive, Encoding.UTF8) != rollbackOriginal)
			{
				throw new InvalidOperationException("native import transaction rollback-and-clean test failed");
			}

			string commitActive = Path.Combine(transactionFolder, "rollout-aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa.jsonl");
			File.WriteAllText(commitActive, "commit-original", Encoding.UTF8);
			NativeImportTransaction commitTransaction = NativeImportTransaction.Begin(text);
			string commitBackup = commitActive + ".ccm-txn-bak-101";
			File.Copy(commitActive, commitBackup);
			File.WriteAllText(commitActive, "commit-current", Encoding.UTF8);
			commitTransaction.TrackImportedSessionFiles(new[] { commitActive }, new[] { "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" });
			int committedBackupCount = commitTransaction.CommitAndDeleteTemporaryBackups();
			if (committedBackupCount != 1 || File.Exists(commitBackup) || File.ReadAllText(commitActive, Encoding.UTF8) != "commit-current")
			{
				throw new InvalidOperationException("native import transaction commit-and-clean test failed");
			}

			string cleanupFailureActive = Path.Combine(transactionFolder, "rollout-bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb.jsonl");
			File.WriteAllText(cleanupFailureActive, "cleanup-original", Encoding.UTF8);
			NativeImportTransaction cleanupFailureTransaction = NativeImportTransaction.Begin(text);
			string cleanupFailureBackup = cleanupFailureActive + ".ccm-txn-bak-102";
			File.Copy(cleanupFailureActive, cleanupFailureBackup);
			File.WriteAllText(cleanupFailureActive, "cleanup-current", Encoding.UTF8);
			string stagedFailureSnapshot = string.Empty;
			NativeImportTransaction.CommitCleanupFailureForTest = _ => true;
			try
			{
				cleanupFailureTransaction.TrackImportedSessionFiles(new[] { cleanupFailureActive }, new[] { "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" });
				int cleanupFailureCount = cleanupFailureTransaction.CommitAndDeleteTemporaryBackups();
				ImportTransactionRollbackResult postCommitRollback = cleanupFailureTransaction.RollbackAndDeleteTemporaryBackups();
				string cleanupRoot = ImportSnapshotMaintenance.TransactionCleanupRoot(text);
				string[] stagedSnapshots = Directory.Exists(cleanupRoot)
					? Directory.GetFiles(cleanupRoot, "snapshot-*.jsonl", SearchOption.AllDirectories)
					: Array.Empty<string>();
				if (cleanupFailureCount != 1 || File.Exists(cleanupFailureBackup) ||
					File.ReadAllText(cleanupFailureActive, Encoding.UTF8) != "cleanup-current" ||
					postCommitRollback.RestoredCount != 0 || postCommitRollback.DeletedCount != 0 || postCommitRollback.RemovedImportedCount != 0 ||
					stagedSnapshots.Length != 1)
				{
					throw new InvalidOperationException("post-commit cleanup failure changed a completed import");
				}
				stagedFailureSnapshot = stagedSnapshots[0];
			}
			finally
			{
				NativeImportTransaction.CommitCleanupFailureForTest = null;
				if (!string.IsNullOrWhiteSpace(stagedFailureSnapshot) && File.Exists(stagedFailureSnapshot))
				{
					string stagingDirectory = Path.GetDirectoryName(stagedFailureSnapshot);
					File.Delete(stagedFailureSnapshot);
					ImportSnapshotMaintenance.DeleteEmptyTransactionCleanupDirectories(stagingDirectory);
				}
			}
			File.Delete(rollbackActive);
			File.Delete(commitActive);
			File.Delete(cleanupFailureActive);
			File.Delete(rollbackUnrelated);
			Directory.Delete(transactionFolder);
			string text7 = Path.Combine(text, "original.codexbundle");
			string text8 = Path.Combine(text, "fresh.codexbundle");
			ExactBundleWriter.CreateSingleSessionBundle(session, text7);
			ZstdRolloutSelfTest.Run(Path.Combine(text, "zstd-selftest"));
			StorageSafetySelfTest.Run(Path.Combine(text, "storage-safety-selftest"));
			string newProject = Path.Combine(text, "new-project");
			Directory.CreateDirectory(newProject);
			Dictionary<string, string> dictionary4 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			dictionary4.Add(testThreadId, "C:\\fake-project");
			Dictionary<string, string> indexedCwds = dictionary4;
			HashSet<string> hashSet = BundleFreshIdRewriter.FindIndexedPathMismatches(text7, indexedCwds, newProject);
			if (!hashSet.Contains(testThreadId))
			{
				throw new InvalidOperationException("indexed path mismatch was not detected");
			}
			FreshIdRewriteResult freshIdRewriteResult = BundleFreshIdRewriter.Rewrite(text7, text8, hashSet);
			if (freshIdRewriteResult.RewrittenCount != 1 || !freshIdRewriteResult.IdMap.ContainsKey(testThreadId))
			{
				throw new InvalidOperationException("fresh ID bundle rewrite failed");
			}
			string text10 = freshIdRewriteResult.IdMap[testThreadId];
			using (ZipArchive zipArchive2 = ZipFile.OpenRead(text8))
			{
				ZipArchiveEntry entry = zipArchive2.GetEntry("manifest.json");
				if (entry == null)
				{
					throw new InvalidOperationException("rewritten manifest missing");
				}
				BundleManifest bundleManifest2;
				using (StreamReader streamReader = new StreamReader(entry.Open(), Encoding.UTF8))
				{
					bundleManifest2 = javaScriptSerializer.Deserialize<BundleManifest>(streamReader.ReadToEnd());
				}
				BundleSession bundleSession = bundleManifest2.sessions.Single();
				if (bundleSession.thread_id != text10 || bundleSession.origin_thread_id != testThreadId || bundleSession.bundle_path.IndexOf(text10, StringComparison.OrdinalIgnoreCase) < 0)
				{
					throw new InvalidOperationException("rewritten manifest still uses old thread ID");
				}
				ZipArchiveEntry entry2 = zipArchive2.GetEntry(bundleSession.bundle_path);
				if (entry2 == null)
				{
					throw new InvalidOperationException("rewritten session entry missing");
				}
				using StreamReader streamReader2 = new StreamReader(entry2.Open(), Encoding.UTF8);
				string text11 = streamReader2.ReadToEnd();
				if (text11.IndexOf("\"id\":\"" + text10 + "\"", StringComparison.Ordinal) < 0 || text11.IndexOf("真正的问题", StringComparison.Ordinal) < 0)
				{
					throw new InvalidOperationException("rewritten session metadata or content is invalid");
				}
				if (text11.IndexOf("\"" + ConversationLineage.OriginThreadIdKey + "\":\"" + testThreadId + "\"", StringComparison.Ordinal) < 0)
				{
					throw new InvalidOperationException("rewritten session did not retain the immutable origin Thread ID");
				}
			}
			ConversationLineageSelfTest.Run(Path.Combine(text, "lineage-selftest"));
			NativeBundleImportResult freshImport = NativeBundleImporter.Import(text8, text, "C:\\fake-project", newProject, NativeBundleImportMode.Merge, dryRun: false);
			if (freshImport.CreatedCount != 1)
			{
				throw new InvalidOperationException("native fresh-ID import did not create one conversation");
			}
			if (NativeSessionCatalog.Scan(text).Count != 2)
			{
				throw new InvalidOperationException("independent copy import setup failed");
			}
			bool importedLineagePersisted = TargetedThreadIndexer.SnapshotSessionFiles(text).Any(path =>
				ConversationLineage.TryReadPayload(path, out Dictionary<string, object> payload) &&
				string.Equals(ConversationLineage.ResolveCurrentThreadId(payload, string.Empty), text10, StringComparison.OrdinalIgnoreCase) &&
				string.Equals(ConversationLineage.ResolveOriginThreadId(payload, text10), testThreadId, StringComparison.OrdinalIgnoreCase));
			if (!importedLineagePersisted)
				throw new InvalidOperationException("native import did not preserve the immutable origin Thread ID");
			if (NativeSessionCatalog.Scan(text).Count != 2)
			{
				throw new InvalidOperationException("independent conversation copy was unexpectedly removed");
			}
			string databasePath = Path.Combine(text, "state_5.sqlite");
			WinSqliteMaintenance.CreateTargetedIndexTestDatabase(databasePath);
			string oldDesktopProjectId = "old-project";
			string targetDesktopProjectId = "target-project";
			string originalDesktopState = WriteDesktopStateFixtureForTest(text, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ oldDesktopProjectId, "C:\\fake-project" },
				{ targetDesktopProjectId, newProject }
			}, testThreadId, oldDesktopProjectId, "C:\\fake-project");
			string text12 = WinSqliteMaintenance.ReadBackfillState(databasePath);
			HashSet<string> filesBeforeImport = TargetedThreadIndexer.SnapshotSessionFiles(text);
			NativeImportTransaction targetedImportTransaction = NativeImportTransaction.Begin(text);
			var targetedImport = NativeBundleImporter.Import(text7, text, "C:\\fake-project", newProject, NativeBundleImportMode.Replace, dryRun: false);
            targetedImportTransaction.TrackImportedSessionFiles(targetedImport.TouchedSessionFiles, new[] { testThreadId });
			Dictionary<string, string> dictionary5 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			dictionary5.Add(testThreadId, "功能测试");
			Dictionary<string, string> dictionary6 = dictionary5;
			TargetedIndexResult targetedIndexResult = TargetedThreadIndexer.IndexImportedSessions(text, new string[1] { text7 }, filesBeforeImport, copiesOnly: false, newProject, dictionary6);
			dictionary6[testThreadId] = "功能测试（已更新）";
			TargetedIndexResult targetedIndexResult2 = TargetedThreadIndexer.IndexImportedSessions(text, new string[1] { text7 }, filesBeforeImport, copiesOnly: false, newProject, dictionary6);
			targetedImportTransaction.CommitAndDeleteTemporaryBackups();
			string text13 = WinSqliteMaintenance.ReadBackfillState(databasePath);
			DbThread dbThread = WinSqliteReader.ReadThreads(databasePath).Single();
			if (targetedIndexResult.InsertedCount != 1 || targetedIndexResult.UpdatedCount != 0 || targetedIndexResult2.InsertedCount != 0 || targetedIndexResult2.UpdatedCount != 1 || text13 != text12 || !text13.StartsWith("complete\u001f", StringComparison.Ordinal) || !File.Exists(targetedIndexResult.BackupPath) || WinSqliteMaintenance.IntegrityCheck(targetedIndexResult.BackupPath) != "ok" || WinSqliteMaintenance.ReadBackfillState(targetedIndexResult.BackupPath) != text12 || WinSqliteReader.ReadThreads(targetedIndexResult.BackupPath).Count != 0 || !string.Equals(dbThread.Id, testThreadId, StringComparison.OrdinalIgnoreCase) || dbThread.Title != "功能测试（已更新）" || !string.Equals(TextHelpers.CanonicalPath(dbThread.Cwd), TextHelpers.CanonicalPath(newProject), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("targeted thread index test failed or backfill_state changed");
			}
			if (targetedIndexResult.VisibilityVerifiedCount != 1 || targetedIndexResult2.VisibilityVerifiedCount != 1 || !TextHelpers.HasExtendedPrefix(dbThread.RawCwd) || !TextHelpers.HasExtendedPrefix(dbThread.RolloutPath))
			{
				throw new InvalidOperationException("targeted thread Codex visibility path test failed");
			}
			if (!targetedIndexResult.DesktopStateFound || targetedIndexResult.DesktopAssignmentExpectedCount != 1 || targetedIndexResult.DesktopAssignmentVerifiedCount != 1 || targetedIndexResult.DesktopProjectCount != 1 ||
				!targetedIndexResult2.DesktopStateFound || targetedIndexResult2.DesktopAssignmentExpectedCount != 1 || targetedIndexResult2.DesktopAssignmentVerifiedCount != 1 ||
				!File.Exists(targetedIndexResult.DesktopStateBackupPath) || File.ReadAllText(targetedIndexResult.DesktopStateBackupPath, Encoding.UTF8) != originalDesktopState)
			{
				throw new InvalidOperationException("desktop project assignment backup or verification test failed");
			}
			AssertDesktopAssignmentForTest(Path.Combine(text, ".codex-global-state.json"), testThreadId, newProject, targetDesktopProjectId, "C:\\fake-project");
			ThreadIndexMetadata historyToggle = TargetedThreadIndexer.ReadMetadataForTest(text3, "历史模式切换测试", "历史模式切换测试");
			historyToggle.Cwd = TextHelpers.ToCodexIndexPath(newProject);
			historyToggle.HistoryMode = CodexHistoryMode.Legacy;
			WinSqliteMaintenance.UpsertImportedThreads(text, new ThreadIndexMetadata[1] { historyToggle });
			if (!string.Equals(WinSqliteReader.ReadThreads(databasePath).Single(item => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)).HistoryMode, CodexHistoryMode.Legacy, StringComparison.Ordinal))
			{
				throw new InvalidOperationException("history_mode paginated-to-legacy update test failed");
			}
			historyToggle.HistoryMode = CodexHistoryMode.Paginated;
			WinSqliteMaintenance.UpsertImportedThreads(text, new ThreadIndexMetadata[1] { historyToggle });
			if (!string.Equals(WinSqliteReader.ReadThreads(databasePath).Single(item => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)).HistoryMode, CodexHistoryMode.Paginated, StringComparison.Ordinal))
			{
				throw new InvalidOperationException("history_mode legacy-to-paginated update test failed");
			}

			string compensationHome = Path.Combine(text, "desktop-registration-compensation");
			Directory.CreateDirectory(compensationHome);
			string compensationDatabase = Path.Combine(compensationHome, "state_5.sqlite");
			WinSqliteMaintenance.CreateTargetedIndexTestDatabase(compensationDatabase);
			ThreadIndexMetadata compensationMetadata = TargetedThreadIndexer.ReadMetadataForTest(text3, "补偿事务测试", "补偿事务测试");
			compensationMetadata.Cwd = TextHelpers.ToCodexIndexPath(Path.Combine(compensationHome, "project"));
			bool desktopFailureCompensated = false;
			CodexDesktopProjectRegistry.TestOverride = delegate
			{
				throw new InvalidOperationException("forced desktop registration failure");
			};
			try
			{
				TargetedThreadIndexer.IndexMetadata(compensationHome, compensationMetadata);
			}
			catch (InvalidOperationException ex)
			{
				desktopFailureCompensated = ex.Message.IndexOf("恢复到导入前状态", StringComparison.Ordinal) >= 0;
			}
			finally
			{
				CodexDesktopProjectRegistry.TestOverride = null;
			}
			if (!desktopFailureCompensated || WinSqliteReader.ReadThreads(compensationDatabase).Count != 0 || !string.Equals(WinSqliteMaintenance.IntegrityCheck(compensationDatabase), "ok", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("desktop registration failure did not restore the pre-import SQLite index");
			}
			string mappedFixtures = Path.Combine(text, "mapped-fixtures");
			Directory.CreateDirectory(mappedFixtures);
			string secondThreadId = "22222222-2222-4222-8222-222222222222";
			string secondSessionPath = Path.Combine(mappedFixtures, "rollout-2026-01-03T03-04-05-" + secondThreadId + ".jsonl");
			string secondContents = fixtureContents.Replace(testThreadId, secondThreadId).Replace("C:\\\\fake-project", "C:\\\\fake-project-two").Replace("真正的问题", "第二个项目的问题").Replace("这是回答。", "第二个项目的回答。");
			File.WriteAllText(secondSessionPath, secondContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			SessionInfo secondSession = new SessionInfo
			{
				ThreadId = secondThreadId,
				SessionPath = secondSessionPath,
				RelativePath = "2026/01/03/" + Path.GetFileName(secondSessionPath),
				Title = "第二项目功能测试",
				Cwd = "C:\\fake-project-two",
				Preview = "第二个项目的问题",
				Source = "cli",
				CreatedAt = "2026-01-03T03:04:05Z",
				UpdatedAt = "2026-01-03T03:05:06Z",
				UpdatedDate = new DateTime(2026, 1, 3, 3, 5, 6, DateTimeKind.Utc)
			};
			string secondBundle = Path.Combine(mappedFixtures, "second-project.codexbundle");
			ExactBundleWriter.CreateSingleSessionBundle(secondSession, secondBundle);
			string mappedHome = Path.Combine(text, "mapped-index");
			Directory.CreateDirectory(mappedHome);
			try
			{
				Environment.SetEnvironmentVariable("CODEX_HOME", mappedHome);
				string mappedDatabase = Path.Combine(mappedHome, "state_5.sqlite");
				WinSqliteMaintenance.CreateTargetedIndexTestDatabase(mappedDatabase);
				string mappedTargetOne = Path.Combine(mappedHome, "projects", "one");
				string mappedTargetTwo = Path.Combine(mappedHome, "projects", "two");
				Directory.CreateDirectory(mappedTargetOne);
				Directory.CreateDirectory(mappedTargetTwo);
				WriteDesktopStateFixtureForTest(mappedHome, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, null, null);
				HashSet<string> mappedBefore = TargetedThreadIndexer.SnapshotSessionFiles(mappedHome);
				NativeBundleImporter.Import(text7, mappedHome, "C:\\fake-project", mappedTargetOne, NativeBundleImportMode.Merge, dryRun: false);
				NativeBundleImporter.Import(secondBundle, mappedHome, "C:\\fake-project-two", mappedTargetTwo, NativeBundleImportMode.Merge, dryRun: false);
				Dictionary<string, string> targetsByBundle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ Path.GetFullPath(text7), mappedTargetOne },
					{ Path.GetFullPath(secondBundle), mappedTargetTwo }
				};
				Dictionary<string, string> mappedTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
				{
					{ testThreadId, "项目一" },
					{ secondThreadId, "项目二" }
				};
				TargetedIndexResult mappedResult = TargetedThreadIndexer.IndexImportedSessionsMapped(mappedHome, new string[2] { text7, secondBundle }, mappedBefore, copiesOnly: false, targetsByBundle, mappedTitles);
				Dictionary<string, DbThread> mappedThreads = WinSqliteReader.ReadThreads(mappedDatabase).ToDictionary((DbThread item) => item.Id, StringComparer.OrdinalIgnoreCase);
				if (mappedResult.InsertedCount != 2 || mappedThreads.Count != 2 || !string.Equals(TextHelpers.CanonicalPath(mappedThreads[testThreadId].Cwd), TextHelpers.CanonicalPath(mappedTargetOne), StringComparison.OrdinalIgnoreCase) || !string.Equals(TextHelpers.CanonicalPath(mappedThreads[secondThreadId].Cwd), TextHelpers.CanonicalPath(mappedTargetTwo), StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidOperationException("multi-project mapped targeted index test failed");
				}
				if (mappedResult.VisibilityVerifiedCount != 2 || mappedThreads.Values.Any((DbThread item) => !TextHelpers.HasExtendedPrefix(item.RawCwd) || !TextHelpers.HasExtendedPrefix(item.RolloutPath)))
				{
					throw new InvalidOperationException("multi-project Codex visibility path test failed");
				}
				if (!mappedResult.DesktopStateFound || mappedResult.DesktopAssignmentExpectedCount != 2 || mappedResult.DesktopAssignmentVerifiedCount != 2 || mappedResult.DesktopProjectCount != 2 || !File.Exists(mappedResult.DesktopStateBackupPath))
				{
					throw new InvalidOperationException("multi-project desktop assignment registration test failed");
				}
				AssertDesktopAssignmentForTest(Path.Combine(mappedHome, ".codex-global-state.json"), testThreadId, mappedTargetOne, null, "C:\\fake-project");
				AssertDesktopAssignmentForTest(Path.Combine(mappedHome, ".codex-global-state.json"), secondThreadId, mappedTargetTwo, null, "C:\\fake-project-two");
			}
			finally
			{
				Environment.SetEnvironmentVariable("CODEX_HOME", text);
			}
			string[] array = new string[2] { "pending", "running" };
			foreach (string text14 in array)
			{
				string text15 = Path.Combine(text, text14 + "-guard");
				Directory.CreateDirectory(text15);
				string databasePath2 = Path.Combine(text15, "state_5.sqlite");
				WinSqliteMaintenance.CreateTargetedIndexTestDatabase(databasePath2, text14);
				string text16 = WinSqliteMaintenance.ReadBackfillState(databasePath2);
				bool flag2 = false;
				try
				{
					WinSqliteMaintenance.UpsertImportedThreads(text15, new ThreadIndexMetadata[1] { TargetedThreadIndexer.ReadMetadataForTest(text3, "功能测试", "真正的问题") });
				}
				catch (InvalidOperationException ex2)
				{
					flag2 = ex2.Message.IndexOf(text14, StringComparison.OrdinalIgnoreCase) >= 0;
				}
				if (!flag2 || WinSqliteMaintenance.ReadBackfillState(databasePath2) != text16 || WinSqliteReader.ReadThreads(databasePath2).Count != 0 || Directory.Exists(Path.Combine(text15, "conversation-migrator-index-backups")))
				{
					throw new InvalidOperationException(text14 + " backfill guard test failed");
				}
			}
			string text17 = Path.Combine(text, "copy-mode");
			Directory.CreateDirectory(text17);
			try
			{
				Environment.SetEnvironmentVariable("CODEX_HOME", text17);
				string databasePath3 = Path.Combine(text17, "state_5.sqlite");
				WinSqliteMaintenance.CreateTargetedIndexTestDatabase(databasePath3);
				HashSet<string> filesBeforeCopyImports = TargetedThreadIndexer.SnapshotSessionFiles(text17);
				NativeBundleImporter.Import(text7, text17, "C:\\fake-project", newProject, NativeBundleImportMode.Merge, dryRun: false);
				string path = TargetedThreadIndexer.SnapshotSessionFiles(text17).Single();
				string contents = File.ReadAllText(path, Encoding.UTF8).Replace("这是回答。", "这是本机分支回答。");
				File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				string text18 = WinSqliteMaintenance.ReadBackfillState(databasePath3);
				string freshCopyBundle = Path.Combine(text17, "copy-fresh.codexbundle");
				FreshIdRewriteResult copyRewrite = BundleFreshIdRewriter.RewriteAsFresh(text7, freshCopyBundle);
				string copiedThreadId = copyRewrite.IdMap[testThreadId];
				NativeBundleImporter.Import(freshCopyBundle, text17, "C:\\fake-project", newProject, NativeBundleImportMode.Merge, dryRun: false);
				Dictionary<string, string> copyTitles = new Dictionary<string, string>(dictionary6, StringComparer.OrdinalIgnoreCase)
				{
					[copiedThreadId] = dictionary6[testThreadId]
				};
				TargetedIndexResult targetedIndexResult3 = TargetedThreadIndexer.IndexImportedSessions(text17, new string[2] { text7, freshCopyBundle }, filesBeforeCopyImports, copiesOnly: false, newProject, copyTitles);
				List<DbThread> list = WinSqliteReader.ReadThreads(databasePath3);
				if (targetedIndexResult3.InsertedCount != 2 || targetedIndexResult3.UpdatedCount != 0 || targetedIndexResult3.IndexedCount != 2 || list.Count != 2 || WinSqliteMaintenance.ReadBackfillState(databasePath3) != text18 || !list.Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)) || !list.Any((DbThread item) => string.Equals(item.Id, copiedThreadId, StringComparison.OrdinalIgnoreCase)) || list.Any((DbThread item) => !string.Equals(TextHelpers.CanonicalPath(item.Cwd), TextHelpers.CanonicalPath(newProject), StringComparison.OrdinalIgnoreCase)))
				{
					throw new InvalidOperationException("copy-mode targeted index test failed");
				}
				if (targetedIndexResult3.VisibilityVerifiedCount != 2 || list.Any((DbThread item) => !TextHelpers.HasExtendedPrefix(item.RawCwd) || !TextHelpers.HasExtendedPrefix(item.RolloutPath)))
				{
					throw new InvalidOperationException("copy-mode Codex visibility path test failed");
				}
			}
			finally
			{
				Environment.SetEnvironmentVariable("CODEX_HOME", text);
			}
			string originalSessionContents = File.ReadAllText(text3, Encoding.UTF8);
			string trashDeleteLegacySnapshot = text3 + ".cct-bak-1001";
			File.Copy(text3, trashDeleteLegacySnapshot);
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, testThreadId, "功能测试（已更新）", newProject);
			DeletedSessionResult deletedSessionResult = ConversationStorage.MoveToTrash(session, newProject);
			if (File.Exists(text3) || !File.Exists(trashDeleteLegacySnapshot) || !File.Exists(deletedSessionResult.BackupPath) || !File.Exists(deletedSessionResult.BackupPath + ".delete-info.json"))
			{
				throw new InvalidOperationException("safe delete test failed");
			}
			File.Delete(trashDeleteLegacySnapshot);
			if (WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)) || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new string[1] { testThreadId }) != 0 || !File.Exists(deletedSessionResult.BackupPath + ".delete-info.json"))
			{
				throw new InvalidOperationException("safe delete left the conversation visible in a current sidebar index");
			}
			AssertDesktopThreadAbsentForTest(Path.Combine(text, ".codex-global-state.json"), testThreadId);
			TrashSessionInfo trashSessionInfo = ConversationStorage.ReadTrash().Single((TrashSessionInfo item) => string.Equals(item.ThreadId, testThreadId, StringComparison.OrdinalIgnoreCase));
			if (!string.Equals(TextHelpers.CanonicalPath(trashSessionInfo.ProjectPath), TextHelpers.CanonicalPath(newProject), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("trash project metadata test failed");
			}
			if (NativeSessionCatalog.Scan(text).Count != 1)
			{
				throw new InvalidOperationException("deleting the original conversation affected its independent copy");
			}
			ConversationStorage.Restore(trashSessionInfo);
			if (!File.Exists(text3) || File.Exists(deletedSessionResult.BackupPath) || ConversationStorage.ReadTrash().Any((TrashSessionInfo item) => string.Equals(item.ThreadId, testThreadId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("trash restore test failed");
			}
			if (!WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("trash restore did not rebuild the conversation thread index");
			}
			AssertDesktopAssignmentForTest(Path.Combine(text, ".codex-global-state.json"), testThreadId, newProject, null, null);
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, testThreadId, "功能测试", newProject);
			DeletedSessionResult deletedSessionResult2 = ConversationStorage.MoveToTrash(session, newProject);
			TrashSessionInfo trashSessionInfo2 = ConversationStorage.ReadTrash().Single((TrashSessionInfo item) => string.Equals(item.ThreadId, testThreadId, StringComparison.OrdinalIgnoreCase));
			if (WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)) || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new string[1] { testThreadId }) != 0)
			{
				throw new InvalidOperationException("second trash delete left the thread index visible");
			}
			AssertDesktopThreadAbsentForTest(Path.Combine(text, ".codex-global-state.json"), testThreadId);
			string trashPurgeLegacySnapshot = text3 + ".cct-bak-1002";
			File.Copy(deletedSessionResult2.BackupPath, trashPurgeLegacySnapshot);
			ConversationStorage.DeleteFromTrash(trashSessionInfo2);
			if (!File.Exists(trashPurgeLegacySnapshot) || File.Exists(deletedSessionResult2.BackupPath) || File.Exists(deletedSessionResult2.BackupPath + ".delete-info.json"))
			{
				throw new InvalidOperationException("trash permanent purge test failed");
			}
			File.Delete(trashPurgeLegacySnapshot);
			File.WriteAllText(text3, originalSessionContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TargetedThreadIndexer.IndexSessionFile(text, text3, "功能测试", "真正的问题");
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, testThreadId, "功能测试", newProject);
			string permanentDeleteLegacySnapshot = text3 + ".cct-bak-1003";
			File.Copy(text3, permanentDeleteLegacySnapshot);
			DeletedSessionResult deletedSessionResult3 = ConversationStorage.DeletePermanently(session);
			if (File.Exists(text3) || !File.Exists(permanentDeleteLegacySnapshot) || !deletedSessionResult3.PermanentlyDeleted || !string.IsNullOrEmpty(deletedSessionResult3.BackupPath) || WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)) || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new string[1] { testThreadId }) != 0)
			{
				throw new InvalidOperationException("direct permanent delete test failed");
			}
			File.Delete(permanentDeleteLegacySnapshot);
			AssertDesktopThreadAbsentForTest(Path.Combine(text, ".codex-global-state.json"), testThreadId);
			if (officialDeletes.Count((string id) => string.Equals(id, testThreadId, StringComparison.OrdinalIgnoreCase)) != 3)
			{
				throw new InvalidOperationException("conversation deletion did not call the official thread/delete protocol exactly once per deletion");
			}

			string cascadeParentId = "66666666-6666-4666-8666-666666666666";
			string cascadeChildId = "77777777-7777-4777-8777-777777777777";
			string cascadeParentPath = Path.Combine(text2, "rollout-2026-01-06T03-04-05-" + cascadeParentId + ".jsonl");
			string cascadeChildPath = Path.Combine(text2, "rollout-2026-01-06T03-05-05-" + cascadeChildId + ".jsonl");
			string cascadeParentContents = fixtureContents.Replace(testThreadId, cascadeParentId).Replace("真正的问题", "级联主对话测试");
			Dictionary<string, object> cascadeChildMeta = javaScriptSerializer.DeserializeObject(javaScriptSerializer.Serialize(obj)) as Dictionary<string, object>;
			Dictionary<string, object> cascadeChildPayload = cascadeChildMeta["payload"] as Dictionary<string, object>;
			cascadeChildPayload["id"] = cascadeChildId;
			cascadeChildPayload["session_id"] = cascadeParentId;
			cascadeChildPayload["parent_thread_id"] = cascadeParentId;
			cascadeChildPayload["thread_source"] = "subagent";
			cascadeChildPayload["source"] = new Dictionary<string, object>
			{
				{
					"subagent",
					new Dictionary<string, object>
					{
						{
							"thread_spawn",
							new Dictionary<string, object> { { "parent_thread_id", cascadeParentId } }
						}
					}
				}
			};
			string cascadeChildContents = javaScriptSerializer.Serialize(cascadeChildMeta) + Environment.NewLine + javaScriptSerializer.Serialize(objContext) + Environment.NewLine + javaScriptSerializer.Serialize(obj2).Replace("真正的问题", "级联子代理测试") + Environment.NewLine + javaScriptSerializer.Serialize(obj3) + Environment.NewLine;
			File.WriteAllText(cascadeParentPath, cascadeParentContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.WriteAllText(cascadeChildPath, cascadeChildContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			SessionInfo cascadeParent = new SessionInfo
			{
				ThreadId = cascadeParentId,
				SessionPath = cascadeParentPath,
				Title = "级联主对话测试",
				Cwd = newProject,
				Preview = "级联主对话测试",
				Source = "cli",
				CreatedAt = "2026-01-06T03:04:05Z",
				UpdatedAt = "2026-01-06T03:05:06Z",
				UpdatedDate = new DateTime(2026, 1, 6, 3, 5, 6, DateTimeKind.Utc)
			};
			SessionInfo cascadeChild = new SessionInfo
			{
				ThreadId = cascadeChildId,
				SessionPath = cascadeChildPath,
				Title = "级联子代理测试",
				Cwd = newProject,
				Preview = "级联子代理测试",
				Source = "subagent",
				CreatedAt = "2026-01-06T03:05:05Z",
				UpdatedAt = "2026-01-06T03:05:06Z",
				UpdatedDate = new DateTime(2026, 1, 6, 3, 5, 6, DateTimeKind.Utc),
				IsSubagent = true,
				ParentThreadId = cascadeParentId
			};
			TargetedThreadIndexer.IndexSessionFile(text, cascadeParentPath, cascadeParent.Title, cascadeParent.Preview);
			TargetedThreadIndexer.IndexSessionFile(text, cascadeChildPath, cascadeChild.Title, cascadeChild.Preview);
			if (!string.Equals(WinSqliteReader.ReadThreads(databasePath).Single((DbThread item) => string.Equals(item.Id, cascadeChildId, StringComparison.OrdinalIgnoreCase)).ParentThreadId, cascadeParentId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("thread index parent-child relation was not readable for cascade protection");
			}
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, cascadeParentId, cascadeParent.Title, newProject);
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, cascadeChildId, cascadeChild.Title, newProject);
			DeletedSessionResult cascadeTrashResult = ConversationStorage.MoveToTrash(cascadeParent, newProject, new SessionInfo[2] { cascadeParent, cascadeChild });
			List<TrashSessionInfo> cascadeTrashItems = ConversationStorage.ReadTrash().Where((TrashSessionInfo item) => string.Equals(item.ThreadId, cascadeParentId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.ThreadId, cascadeChildId, StringComparison.OrdinalIgnoreCase)).ToList();
			if (cascadeTrashResult.AffectedConversationCount != 2 || cascadeTrashResult.BackupPaths.Count != 2 || cascadeTrashItems.Count != 2 || File.Exists(cascadeParentPath) || File.Exists(cascadeChildPath) || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new string[2] { cascadeParentId, cascadeChildId }) != 0 || officialDeletes.Count((string id) => string.Equals(id, cascadeParentId, StringComparison.OrdinalIgnoreCase)) != 1 || officialDeletes.Any((string id) => string.Equals(id, cascadeChildId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("cascade trash staging did not preserve every spawned descendant before official deletion");
			}
			ConversationStorage.Restore(cascadeTrashItems.Single((TrashSessionInfo item) => string.Equals(item.ThreadId, cascadeParentId, StringComparison.OrdinalIgnoreCase)));
			ConversationStorage.Restore(cascadeTrashItems.Single((TrashSessionInfo item) => string.Equals(item.ThreadId, cascadeChildId, StringComparison.OrdinalIgnoreCase)));
			if (!File.Exists(cascadeParentPath) || !File.Exists(cascadeChildPath) || !WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, cascadeParentId, StringComparison.OrdinalIgnoreCase)) || !WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, cascadeChildId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("cascade trash restore did not rebuild both conversations");
			}
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, cascadeParentId, cascadeParent.Title, newProject);
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, cascadeChildId, cascadeChild.Title, newProject);
			DeletedSessionResult cascadePermanentResult = ConversationStorage.DeletePermanently(cascadeParent, new SessionInfo[2] { cascadeParent, cascadeChild });
			if (cascadePermanentResult.AffectedConversationCount != 2 || File.Exists(cascadeParentPath) || File.Exists(cascadeChildPath) || WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, cascadeParentId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Id, cascadeChildId, StringComparison.OrdinalIgnoreCase)) || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new string[2] { cascadeParentId, cascadeChildId }) != 0 || officialDeletes.Count((string id) => string.Equals(id, cascadeParentId, StringComparison.OrdinalIgnoreCase)) != 2 || officialDeletes.Any((string id) => string.Equals(id, cascadeChildId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("cascade permanent deletion was not handled by one root thread/delete call");
			}

			File.WriteAllText(text3, originalSessionContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TargetedThreadIndexer.IndexSessionFile(text, text3, "官方删除失败保护测试", "官方删除失败保护测试");
			CodexAppServerThreadDeletion.TestOverride = delegate
			{
				return new OfficialThreadDeletionResult { Succeeded = false, Error = "simulated refusal" };
			};
			bool officialRefusalBlockedLocalDelete = false;
			try
			{
				ConversationStorage.DeletePermanently(session);
			}
			catch (InvalidOperationException)
			{
				officialRefusalBlockedLocalDelete = true;
			}
			if (!officialRefusalBlockedLocalDelete || !File.Exists(text3) || !WinSqliteReader.ReadThreads(databasePath).Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("official deletion refusal did not preserve the original conversation and index");
			}
			CodexAppServerThreadDeletion.TestOverride = delegate(string _, string threadId)
			{
				officialDeletes.Add(threadId);
				return new OfficialThreadDeletionResult { Succeeded = true, CodexPath = "self-test-codex.exe", Error = string.Empty };
			};

			File.WriteAllText(text3, originalSessionContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TargetedThreadIndexer.IndexSessionFile(text, text3, "旧失效项测试", "旧失效项测试");
			File.Delete(text3);
			string staleTaskCacheDirectory = Path.Combine(CodexDesktopTaskCache.UserDataRootOverride, "Default", "Cache", "Cache_Data");
			Directory.CreateDirectory(staleTaskCacheDirectory);
			byte[] staleTaskCacheBytes = new byte[65530 + testThreadId.Length + 16];
			Encoding.ASCII.GetBytes(testThreadId).CopyTo(staleTaskCacheBytes, 65530);
			File.WriteAllBytes(Path.Combine(staleTaskCacheDirectory, "data_1"), staleTaskCacheBytes);
			List<DbThread> orphanedThreads = ConversationIndexMaintenance.FindOrphanedThreads(text);
			if (!orphanedThreads.Any((DbThread item) => string.Equals(item.Id, testThreadId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("missing rollout file was not detected as a stale sidebar item");
			}
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, testThreadId, "旧失效项测试", newProject);
			OrphanIndexRepairResult orphanRepair = ConversationIndexMaintenance.RepairSelectedOrphans(text, new string[1] { testThreadId });
			if (orphanRepair.RepairedCount != 0 || orphanRepair.DetectedCount != 1 ||
				!Directory.Exists(staleTaskCacheDirectory) || !WinSqliteReader.ReadThreads(databasePath).Any(t => t.Id == testThreadId) ||
				WinSqliteMaintenance.CountDesktopCatalogThreads(text, new[] { testThreadId }) != 1)
				throw new InvalidOperationException("missing-path diagnostics must preserve indexes and cache");
			var completedRepairCacheCleanup = ConversationIndexMaintenance.InvalidateCompletedRepairCaches(text);
			if (completedRepairCacheCleanup.RemovedCatalogEntryCount != 0 || !Directory.Exists(staleTaskCacheDirectory))
				throw new InvalidOperationException("old repair ledger must not replay deletions");

			string guardedParentId = "88888888-8888-4888-8888-888888888888";
			string guardedChildId = "99999999-9999-4999-8999-999999999999";
			string guardedParentPath = Path.Combine(text2, "rollout-2026-01-07T03-04-05-" + guardedParentId + ".jsonl");
			string guardedChildPath = Path.Combine(text2, "rollout-2026-01-07T03-05-05-" + guardedChildId + ".jsonl");
			Dictionary<string, object> guardedChildMeta = javaScriptSerializer.DeserializeObject(javaScriptSerializer.Serialize(obj)) as Dictionary<string, object>;
			Dictionary<string, object> guardedChildPayload = guardedChildMeta["payload"] as Dictionary<string, object>;
			guardedChildPayload["id"] = guardedChildId;
			guardedChildPayload["session_id"] = guardedParentId;
			guardedChildPayload["parent_thread_id"] = guardedParentId;
			guardedChildPayload["thread_source"] = "subagent";
			guardedChildPayload["source"] = new Dictionary<string, object>
			{
				{
					"subagent",
					new Dictionary<string, object> { { "other", "guardian" } }
				}
			};
			File.WriteAllText(guardedParentPath, fixtureContents.Replace(testThreadId, guardedParentId).Replace("真正的问题", "失效主对话子代理保护测试"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.WriteAllText(guardedChildPath, javaScriptSerializer.Serialize(guardedChildMeta) + Environment.NewLine + javaScriptSerializer.Serialize(objContext) + Environment.NewLine + javaScriptSerializer.Serialize(obj2).Replace("真正的问题", "仍存子代理") + Environment.NewLine + javaScriptSerializer.Serialize(obj3) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TargetedThreadIndexer.IndexSessionFile(text, guardedParentPath, "失效主对话子代理保护测试", "失效主对话子代理保护测试");
			TargetedThreadIndexer.IndexSessionFile(text, guardedChildPath, "仍存子代理", "仍存子代理");
			File.Delete(guardedParentPath);
			List<LiveDescendantInfo> locatedDescendants = ConversationIndexMaintenance.FindLiveDescendants(text, new string[1] { guardedParentId });
			if (locatedDescendants.Count != 1 || !string.Equals(locatedDescendants[0].ThreadId, guardedChildId, StringComparison.OrdinalIgnoreCase) || !string.Equals(locatedDescendants[0].RootThreadId, guardedParentId, StringComparison.OrdinalIgnoreCase) || !string.Equals(TextHelpers.CanonicalPath(locatedDescendants[0].RolloutPath), TextHelpers.CanonicalPath(guardedChildPath), StringComparison.OrdinalIgnoreCase) || locatedDescendants[0].Title.IndexOf(UiLanguage.IsEnglish ? "guardian" : "审批守卫", StringComparison.OrdinalIgnoreCase) < 0)
			{
				throw new InvalidOperationException("live descendant location did not return the exact parent, title, Thread ID, and path");
			}
			CatalogResult orphanSubagentCatalog = CodexCatalog.Build(new List<SessionInfo>
			{
				new SessionInfo
				{
					ThreadId = guardedChildId,
					SessionPath = guardedChildPath,
					Cwd = newProject,
					Title = "仍存子代理",
					Preview = "仍存子代理",
					IsSubagent = true,
					ParentThreadId = guardedParentId,
					UpdatedDate = new DateTime(2026, 1, 7, 3, 5, 6, DateTimeKind.Utc)
				}
			});
			ProjectGroup orphanSubagentProject = orphanSubagentCatalog.Projects.Single();
			if (!orphanSubagentProject.IsSubagentOnly || orphanSubagentProject.MainCount != 0 || orphanSubagentProject.InternalCount != 1 || orphanSubagentProject.CanBackupFiles || orphanSubagentProject.DisplayName.IndexOf(UiLanguage.IsEnglish ? "Orphaned subagents" : "孤立子代理", StringComparison.OrdinalIgnoreCase) < 0 || orphanSubagentProject.Sessions[0].DisplayTitle.IndexOf(UiLanguage.IsEnglish ? "Approval guardian" : "内部审批守卫", StringComparison.OrdinalIgnoreCase) < 0)
			{
				throw new InvalidOperationException("orphaned subagent was not exposed as a manageable, non-project-backup group");
			}
			bool liveDescendantGuarded = false;
			try
			{
				ConversationIndexMaintenance.RepairSelectedOrphans(text, new string[1] { guardedParentId });
			}
			catch (LiveDescendantRepairException ex)
			{
				liveDescendantGuarded = ex.Descendants.Count == 1 && string.Equals(ex.Descendants[0].ThreadId, guardedChildId, StringComparison.OrdinalIgnoreCase);
			}
			if (!liveDescendantGuarded || !File.Exists(guardedChildPath) || WinSqliteReader.ReadThreads(databasePath).Count((DbThread item) => string.Equals(item.Id, guardedParentId, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Id, guardedChildId, StringComparison.OrdinalIgnoreCase)) != 2 || officialDeletes.Any((string id) => string.Equals(id, guardedParentId, StringComparison.OrdinalIgnoreCase)))
			{
				throw new InvalidOperationException("stale-sidebar repair did not stop before cascading into a live descendant");
			}
			File.Delete(guardedChildPath);
			WinSqliteMaintenance.RemoveThreads(text, new string[2] { guardedParentId, guardedChildId });
			CodexDesktopProjectRegistry.RemoveThreads(text, new string[2] { guardedParentId, guardedChildId });

			string legacySidebarThreadId = "55555555-5555-4555-8555-555555555555";
			string legacySidebarPath = Path.Combine(text2, "rollout-2026-01-05T03-04-05-" + legacySidebarThreadId + ".jsonl");
			string legacySidebarContents = fixtureContents.Replace(testThreadId, legacySidebarThreadId).Replace("真正的问题", "旧版半删除侧边栏测试");
			File.WriteAllText(legacySidebarPath, legacySidebarContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			TargetedThreadIndexer.IndexSessionFile(text, legacySidebarPath, "旧版半删除侧边栏测试", "旧版半删除侧边栏测试");
			ThreadIndexRemovalResult legacyPreDelete = WinSqliteMaintenance.RemoveThreads(text, new string[1] { legacySidebarThreadId });
			if (string.IsNullOrWhiteSpace(legacyPreDelete.BackupPath) || !File.Exists(legacyPreDelete.BackupPath))
			{
				throw new InvalidOperationException("legacy stale sidebar setup did not create a pre-delete index backup");
			}
			File.Delete(legacySidebarPath);
			Directory.CreateDirectory(ConversationIndexMaintenance.LogRootOverride);
			File.WriteAllText(Path.Combine(ConversationIndexMaintenance.LogRootOverride, "codex-desktop-test.log"), "error [electron-message-handler] Request failed conversationId=" + legacySidebarThreadId + " error={\"message\":\"no rollout found for thread id " + legacySidebarThreadId + "\"} failureReason=rollout_not_found" + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			List<DbThread> deletedSidebarRemnants = ConversationIndexMaintenance.FindDeletedSidebarRemnants(text);
			if (deletedSidebarRemnants.Count != 1 || !string.Equals(deletedSidebarRemnants[0].Id, legacySidebarThreadId, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("log-confirmed legacy stale sidebar item was not detected precisely");
			}
			WinSqliteMaintenance.AddDesktopCatalogTestThread(text, legacySidebarThreadId, "旧版半删除侧边栏测试", newProject);
			OrphanIndexRepairResult legacySidebarRepair = ConversationIndexMaintenance.RepairDeletedSidebarRemnants(text, new string[1] { legacySidebarThreadId });
			if (legacySidebarRepair.RepairedCount != 0 || WinSqliteMaintenance.CountDesktopCatalogThreads(text, new[] { legacySidebarThreadId }) != 1 || officialDeletes.Any(id => id == legacySidebarThreadId))
				throw new InvalidOperationException("legacy sidebar diagnostics deleted recovery evidence");

			string legacyThreadId = "33333333-3333-4333-8333-333333333333";
			string legacyActive = Path.Combine(text2, "rollout-2026-01-04T03-04-05-" + legacyThreadId + ".jsonl");
			string legacyContents = fixtureContents.Replace(testThreadId, legacyThreadId).Replace("真正的问题", "旧版安全快照测试");
			string legacyBackupOne = legacyActive + ".cct-bak-2001";
			string legacyBackupTwo = legacyActive + ".cct-bak-2002";
			File.WriteAllText(legacyBackupOne, legacyContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.WriteAllText(legacyBackupTwo, legacyContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			OrphanedSnapshotCleanupResult legacyMigration = ImportSnapshotMaintenance.MoveOrphanedSnapshotsToTrash(text);
			if (legacyMigration.MovedToTrashCount != 2 || legacyMigration.RedundantDeletedCount != 0 || File.Exists(legacyBackupOne) || File.Exists(legacyBackupTwo))
			{
				throw new InvalidOperationException("legacy transaction snapshot migration test failed");
			}
			TrashSessionInfo legacyTrash = ConversationStorage.ReadTrash().First((TrashSessionInfo item) => string.Equals(item.ThreadId, legacyThreadId, StringComparison.OrdinalIgnoreCase));
			if (legacyTrash.DisplayTitle.IndexOf("旧版安全快照", StringComparison.Ordinal) < 0 || !File.Exists(legacyTrash.BackupPath))
			{
				throw new InvalidOperationException("legacy transaction snapshot was not visible in trash");
			}
			ConversationStorage.DeleteFromTrash(legacyTrash);
			if (File.Exists(legacyTrash.BackupPath) || File.Exists(legacyTrash.SidecarPath))
			{
				throw new InvalidOperationException("legacy transaction snapshot trash purge test failed");
			}

			bool projectGuardWorked = false;
			try
			{
				ConversationStorage.ValidateProjectPath(text);
			}
			catch (InvalidOperationException)
			{
				projectGuardWorked = true;
			}
			if (!projectGuardWorked)
			{
				throw new InvalidOperationException("Codex home project deletion guard test failed");
			}
			string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string userProfileParent = string.IsNullOrWhiteSpace(userProfilePath) ? string.Empty : Directory.GetParent(userProfilePath)?.FullName;
			if (!string.IsNullOrWhiteSpace(userProfileParent) && Directory.Exists(userProfileParent))
			{
				bool protectedParentGuardWorked = false;
				try
				{
					ConversationStorage.ValidateProjectPath(userProfileParent);
				}
				catch (InvalidOperationException)
				{
					protectedParentGuardWorked = true;
				}
				if (!protectedParentGuardWorked)
				{
					throw new InvalidOperationException("protected user-directory parent deletion guard test failed");
				}
			}
			Directory.CreateDirectory(projectDeleteTest);
			File.WriteAllText(Path.Combine(projectDeleteTest, "keep-until-confirmed.txt"), "test", Encoding.UTF8);
			ConversationStorage.DeleteProject(projectDeleteTest, ProjectDeleteMode.Permanent);
			if (Directory.Exists(projectDeleteTest))
			{
				throw new InvalidOperationException("project permanent delete test failed");
			}
			string payloadSource = Path.Combine(projectPayloadTest, "source-project");
			string payloadTarget = Path.Combine(projectPayloadTest, "restored-project");
			string payloadArchive = Path.Combine(projectPayloadTest, "project-files.zip");
			Directory.CreateDirectory(Path.Combine(payloadSource, "src"));
			Directory.CreateDirectory(Path.Combine(payloadSource, "empty-directory"));
			File.WriteAllText(Path.Combine(payloadSource, "src", "hello.txt"), "payload-content", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.WriteAllText(Path.Combine(payloadSource, "中文文件.txt"), "中文内容", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			string excludedPack = Path.Combine(payloadSource, "do-not-include.codexpack");
			File.WriteAllText(excludedPack, "exclude-me", Encoding.UTF8);
			ProjectPayloadInfo payloadInfo = ProjectPayloadService.CreateArchive(payloadSource, payloadArchive, excludedPack, null);
			if (payloadInfo.file_count != 2 || payloadInfo.directory_count < 2 || payloadInfo.uncompressed_bytes <= 0L || string.IsNullOrWhiteSpace(payloadInfo.sha256))
			{
				throw new InvalidOperationException("project payload creation test failed");
			}
			PackManifest payloadManifest = new PackManifest
			{
				schema = 3,
				mode = "project_with_files",
				source_project = payloadSource,
				project_payload = payloadInfo,
				bundles = new List<string>(),
				sessions = new List<PackSession>()
			};
			PackManifest payloadManifestRoundTrip = javaScriptSerializer.Deserialize<PackManifest>(javaScriptSerializer.Serialize(payloadManifest));
			if (payloadManifestRoundTrip?.project_payload?.file_count != 2)
			{
				throw new InvalidOperationException("schema 3 project payload manifest test failed");
			}
			string combinedStaging = Path.Combine(projectPayloadTest, "combined-pack-staging");
			string combinedPack = Path.Combine(projectPayloadTest, "project-and-conversations.codexproject");
			string combinedExtract = Path.Combine(projectPayloadTest, "combined-pack-extract");
			Directory.CreateDirectory(combinedStaging);
			File.Copy(payloadArchive, Path.Combine(combinedStaging, "project-files.zip"));
			File.Copy(text7, Path.Combine(combinedStaging, "project.codexbundle"));
			payloadManifest.bundles = new List<string> { "project.codexbundle" };
			payloadManifest.sessions = new List<PackSession>
			{
				new PackSession
				{
					thread_id = testThreadId,
					title = "功能测试",
					bundle_file = "project.codexbundle"
				}
			};
			File.WriteAllText(Path.Combine(combinedStaging, "manifest.json"), javaScriptSerializer.Serialize(payloadManifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			ZipFile.CreateFromDirectory(combinedStaging, combinedPack, CompressionLevel.Optimal, includeBaseDirectory: false);
			ZipFile.ExtractToDirectory(combinedPack, combinedExtract);
			PackManifest combinedManifest = javaScriptSerializer.Deserialize<PackManifest>(File.ReadAllText(Path.Combine(combinedExtract, "manifest.json"), Encoding.UTF8));
			if (combinedManifest?.schema != 3 || combinedManifest.bundles?.SingleOrDefault() != "project.codexbundle")
			{
				throw new InvalidOperationException("combined project and conversation package manifest test failed");
			}
			PackManifest batchManifest = new PackManifest
			{
				schema = 5,
				mode = "batch_projects_with_files",
				bundles = new List<string>
				{
					"projects/project-001/project.codexbundle",
					"projects/project-002/project.codexbundle"
				},
				projects = new List<PackProject>
				{
					new PackProject
					{
						project_key = "project-001",
						source_project = "D:\\OldComputer\\Projects\\project-one",
						source_project_name = "project-one",
						target_folder = "project-one",
						bundles = new List<string> { "projects/project-001/project.codexbundle" }
					},
					new PackProject
					{
						project_key = "project-002",
						source_project = "D:\\OldComputer\\Projects\\project-two",
						source_project_name = "project-two",
						target_folder = "project-two",
						bundles = new List<string> { "projects/project-002/project.codexbundle" }
					}
				},
				sessions = new List<PackSession>
				{
					new PackSession
					{
						origin_thread_id = testThreadId,
						thread_id = testThreadId,
						project_key = "project-001",
						bundle_file = "projects/project-001/project.codexbundle"
					},
					new PackSession
					{
						thread_id = secondThreadId,
						origin_thread_id = secondThreadId,
						project_key = "project-002",
						bundle_file = "projects/project-002/project.codexbundle"
					}
				}
			};
			PackManifest batchRoundTrip = javaScriptSerializer.Deserialize<PackManifest>(javaScriptSerializer.Serialize(batchManifest));
			string batchTargetRoot = Path.Combine(projectPayloadTest, "batch-target");
			List<string> batchTargets = MainWindowController.BuildProjectTargetPaths(batchRoundTrip.projects, batchTargetRoot);
			if (batchRoundTrip?.schema != 5 || batchRoundTrip.projects?.Count != 2 || batchRoundTrip.sessions?.Count != 2 || batchRoundTrip.sessions.Any(item => string.IsNullOrWhiteSpace(item.origin_thread_id)) || batchTargets.Count != 2 || !string.Equals(batchTargets[0], Path.Combine(batchTargetRoot, "project-one"), StringComparison.OrdinalIgnoreCase) || !string.Equals(batchTargets[1], Path.Combine(batchTargetRoot, "project-two"), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("schema 5 multi-project lineage manifest and target mapping test failed");
			}
			bool batchTargetTraversalBlocked = false;
			batchRoundTrip.projects[1].target_folder = "..\\escape";
			try
			{
				MainWindowController.BuildProjectTargetPaths(batchRoundTrip.projects, batchTargetRoot);
			}
			catch (InvalidDataException)
			{
				batchTargetTraversalBlocked = true;
			}
			if (!batchTargetTraversalBlocked)
			{
				throw new InvalidOperationException("schema 4 project target traversal guard test failed");
			}
			TargetedThreadIndexer.ValidateBundles(new string[1] { Path.Combine(combinedExtract, combinedManifest.bundles[0]) });
			ProjectRestorePlan combinedPlan = ProjectPayloadService.InspectArchive(ProjectPayloadService.ResolvePayloadArchivePath(combinedExtract, combinedManifest.project_payload), combinedManifest.project_payload, Path.Combine(projectPayloadTest, "combined-inspect-target"), ProjectFileConflictMode.RequireEmpty);
			if (combinedPlan.FileCount != 2)
			{
				throw new InvalidOperationException("combined project payload inspection test failed");
			}
			ProjectRestorePlan payloadPlan = ProjectPayloadService.InspectArchive(payloadArchive, payloadInfo, payloadTarget, ProjectFileConflictMode.RequireEmpty);
			if (payloadPlan.FileCount != 2 || payloadPlan.NewFileCount != 2 || payloadPlan.ExistingFileCount != 0)
			{
				throw new InvalidOperationException("project payload dry-run inspection test failed");
			}
			ProjectRestoreResult payloadRestore = ProjectPayloadService.RestoreArchive(payloadArchive, payloadInfo, payloadTarget, ProjectFileConflictMode.RequireEmpty);
			if (payloadRestore.CreatedFileCount != 2 || File.ReadAllText(Path.Combine(payloadTarget, "src", "hello.txt"), Encoding.UTF8) != "payload-content" || !Directory.Exists(Path.Combine(payloadTarget, "empty-directory")) || File.Exists(Path.Combine(payloadTarget, "do-not-include.codexpack")))
			{
				throw new InvalidOperationException("project payload restore test failed");
			}
			File.WriteAllText(Path.Combine(payloadTarget, "src", "hello.txt"), "local-version", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			ProjectRestoreResult payloadSkip = ProjectPayloadService.RestoreArchive(payloadArchive, payloadInfo, payloadTarget, ProjectFileConflictMode.SkipExisting);
			if (payloadSkip.SkippedFileCount != 2 || File.ReadAllText(Path.Combine(payloadTarget, "src", "hello.txt"), Encoding.UTF8) != "local-version")
			{
				throw new InvalidOperationException("project payload skip-existing test failed");
			}
			ProjectRestoreResult payloadOverwrite = ProjectPayloadService.RestoreArchive(payloadArchive, payloadInfo, payloadTarget, ProjectFileConflictMode.OverwriteWithBackup);
			if (payloadOverwrite.OverwrittenFileCount != 2 || File.ReadAllText(Path.Combine(payloadTarget, "src", "hello.txt"), Encoding.UTF8) != "payload-content" || !File.Exists(payloadOverwrite.BackupPath))
			{
				throw new InvalidOperationException("project payload overwrite-with-backup test failed");
			}
			using (ZipArchive overwriteBackup = ZipFile.OpenRead(payloadOverwrite.BackupPath))
			{
				ZipArchiveEntry backedHello = overwriteBackup.GetEntry("src/hello.txt");
				if (backedHello == null)
				{
					throw new InvalidOperationException("project overwrite backup entry missing");
				}
				using StreamReader backedReader = new StreamReader(backedHello.Open(), Encoding.UTF8);
				if (backedReader.ReadToEnd() != "local-version")
				{
					throw new InvalidOperationException("project overwrite backup content test failed");
				}
			}
			string maliciousArchive = Path.Combine(projectPayloadTest, "malicious.zip");
			using (ZipArchive malicious = ZipFile.Open(maliciousArchive, ZipArchiveMode.Create))
			{
				ZipArchiveEntry escape = malicious.CreateEntry("../escape.txt");
				using StreamWriter escapeWriter = new StreamWriter(escape.Open(), Encoding.UTF8);
				escapeWriter.Write("escape");
			}
			ProjectPayloadInfo maliciousInfo = new ProjectPayloadInfo
			{
				archive_file = "malicious.zip",
				file_count = 1,
				directory_count = 0,
				uncompressed_bytes = 6,
				sha256 = ProjectPayloadService.Sha256File(maliciousArchive)
			};
			bool traversalBlocked = false;
			try
			{
				ProjectPayloadService.InspectArchive(maliciousArchive, maliciousInfo, Path.Combine(projectPayloadTest, "malicious-target"), ProjectFileConflictMode.RequireEmpty);
			}
			catch (InvalidDataException)
			{
				traversalBlocked = true;
			}
			if (!traversalBlocked)
			{
				throw new InvalidOperationException("project payload traversal guard test failed");
			}
			string originalLanguageCode = UiLanguage.Code;
			try
			{
				UiLanguage.Initialize("en-US");
				string[] criticalEnglishTexts =
				{
					UiLanguage.T("当前操作尚未完成，请等待完成后再关闭。"),
					UiLanguage.T("操作进行中"),
					UiLanguage.T("暂时不能关闭窗口"),
					UiLanguage.T("当前正在写入或校验本地数据。完成后即可安全关闭。"),
					UiLanguage.T("继续等待"),
					UiLanguage.T("导入验证完成，已清理 2 个事务安全快照。"),
					UiLanguage.T("现在重新打开 Codex，再打开迁入后的项目目录并实际打开对应对话。"),
					UiLanguage.T("请完全退出并重新打开 Codex，让侧栏重新读取索引并实际打开对应对话。"),
					UiLanguage.T("项目文件若已还原，右侧操作记录会明确列出；修复问题后可重新导入同一迁移包；也可取消“还原项目文件”或选择“跳过同名文件”，避免再次改动已还原的项目文件。"),
					MainWindowController.BuildPaginatedImportWarning(2)
				};
				if (criticalEnglishTexts.Any(value => value.Any(character => character >= 0x3400 && character <= 0x9fff)))
				{
					throw new InvalidOperationException("critical English workflow text contains CJK characters");
				}
			}
			finally
			{
				UiLanguage.Initialize(originalLanguageCode);
			}
			return "ImportModes=origin-merge+independent-copy · SamePathCwd=no-op · FormalBackup=.codexchat+.codexproject+legacy · NativeTxn=rollback+commit+delete · LegacyTxnSnapshot=trash-compatibility · Lineage=origin-persist+project-scope+parent-child+fresh-every-time+ambiguity-guard+checksum-guard+outer-archive-guards · IndependentCopy=retained+delete-isolated · TargetedIndex=insert+update+two-project-cwd+native-path+visibility · HistoryMode=legacy+paginated+bidirectional-update+ordinal-gap-guard · ImportRollback=planned-new-files-only+unrelated-preserved+sqlite-compensation+commit-cleanup-failure-safe · RuntimeSelection=cli-version-probe+desktop-install-discovery · DesktopProjectState=existing-remap+create+multi-project+backup+verify · BackupPrewrite=OK · BackfillUnchanged=complete · PendingRunningGuard=OK · Zstd=preview+backup+compressed-bundle+merge+rewrite+rollback+corruption · Preview=2 messages · Trash=copy+official-delete+index-remove+desktop-catalog-remove+list+index-restore+purge+descendant-staging · PermanentDelete=official-delete+index-remove+desktop-catalog-remove+descendant-cascade · OfficialDeleteRefusal=preserves-local-data · StaleSidebar=read-only-diagnosis+compressed-rollout+ledger-no-op+live-descendant-guard+orphan-subagent-visible+exact-location+catalog-preserved+cache-preserved · ProjectGuard+Permanent=OK · ProjectPayload=schema5+two-targets+target-guard+combined-pack+create+inspect+restore+skip+backup+traversal-guard · EnglishCriticalFlows=no-CJK · PreviewLayout=auto-fit+fixed-insets+full-width+complete-scroll-end";
		}
		finally
		{
			CodexAppServerThreadDeletion.TestOverride = null;
			CodexAppServerThreadDeletion.VersionOutputOverrideForTest = null;
			NativeImportTransaction.CommitCleanupFailureForTest = null;
			CodexDesktopProjectRegistry.TestOverride = null;
			ConversationIndexMaintenance.LogRootOverride = null;
			CodexDesktopTaskCache.UserDataRootOverride = null;
			Environment.SetEnvironmentVariable("CODEX_HOME", environmentVariable);
			try
			{
				if (Environment.GetEnvironmentVariable("CODEX_MIGRATOR_KEEP_TEST") != "1" && Directory.Exists(text))
				{
					Directory.Delete(text, recursive: true);
				}
				if (Directory.Exists(projectDeleteTest))
				{
					Directory.Delete(projectDeleteTest, recursive: true);
				}
				if (Directory.Exists(projectPayloadTest))
				{
					Directory.Delete(projectPayloadTest, recursive: true);
				}
			}
			catch
			{
			}
		}
	}
	private static string WriteDesktopStateFixtureForTest(string codexHome, IDictionary<string, string> projectPathsById, string assignedThreadId, string assignedProjectId, string assignedCwd)
	{
		Dictionary<string, object> projects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		foreach (KeyValuePair<string, string> pair in projectPathsById ?? new Dictionary<string, string>())
		{
			projects[pair.Key] = new Dictionary<string, object>
			{
				{ "id", pair.Key },
				{ "name", Path.GetFileName(pair.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) },
				{ "rootPaths", new object[1] { pair.Value } },
				{ "createdAt", now },
				{ "updatedAt", now }
			};
		}
		Dictionary<string, object> assignments = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, object> hints = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, object> writableRoots = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, object> atoms = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
		object[] projectless = new object[0];
		if (!string.IsNullOrWhiteSpace(assignedThreadId))
		{
			assignments[assignedThreadId] = new Dictionary<string, object>
			{
				{ "projectKind", "local" },
				{ "projectId", assignedProjectId },
				{ "cwd", assignedCwd },
				{ "pendingCoreUpdate", false }
			};
			hints[assignedThreadId] = assignedCwd;
			writableRoots[assignedThreadId] = new object[2] { assignedCwd, "C:\\keep-root" };
			projectless = new object[1] { assignedThreadId };
			atoms["heartbeat-thread-permissions-by-id"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
			{
				{ assignedThreadId, true }
			};
			atoms["thread-tab-routes-v1:" + Uri.EscapeDataString("local:" + assignedThreadId)] = new object[1] { "conversation" };
		}
		Dictionary<string, object> state = new Dictionary<string, object>
		{
			{ "local-projects", projects },
			{ "project-order", projectPathsById.Keys.Cast<object>().ToArray() },
			{ "electron-saved-workspace-roots", projectPathsById.Values.Cast<object>().ToArray() },
			{ "thread-project-assignments", assignments },
			{ "projectless-thread-ids", projectless },
			{ "thread-workspace-root-hints", hints },
			{ "thread-writable-roots", writableRoots },
			{ "electron-persisted-atom-state", atoms }
		};
		string json = JsonSerialization.NewSerializer().Serialize(state);
		File.WriteAllText(Path.Combine(codexHome, ".codex-global-state.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return json;
	}

	private static void AssertDesktopAssignmentForTest(string statePath, string threadId, string expectedCwd, string expectedProjectId, string forbiddenCwd)
	{
		Dictionary<string, object> state = JsonSerialization.NewSerializer().DeserializeObject(File.ReadAllText(statePath, Encoding.UTF8)) as Dictionary<string, object>;
		Dictionary<string, object> projects = state?["local-projects"] as Dictionary<string, object>;
		Dictionary<string, object> assignments = state?["thread-project-assignments"] as Dictionary<string, object>;
		Dictionary<string, object> writableRoots = state?["thread-writable-roots"] as Dictionary<string, object>;
		Dictionary<string, object> hints = state?["thread-workspace-root-hints"] as Dictionary<string, object>;
		object[] projectless = state?["projectless-thread-ids"] as object[];
		if (projects == null || assignments == null || writableRoots == null || projectless == null ||
			!assignments.TryGetValue(threadId, out object assignmentValue) || !(assignmentValue is Dictionary<string, object> assignment))
		{
			throw new InvalidOperationException("desktop project state fixture is missing the imported assignment");
		}
		string projectId = Convert.ToString(assignment["projectId"]);
		string cwd = Convert.ToString(assignment["cwd"]);
		if (!string.Equals(Convert.ToString(assignment["projectKind"]), "local", StringComparison.OrdinalIgnoreCase) ||
			(!string.IsNullOrWhiteSpace(expectedProjectId) && !string.Equals(projectId, expectedProjectId, StringComparison.OrdinalIgnoreCase)) ||
			!string.Equals(TextHelpers.CanonicalPath(cwd), TextHelpers.CanonicalPath(expectedCwd), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("desktop project assignment does not point at the imported project");
		}
		if (!projects.TryGetValue(projectId, out object projectValue) || !(projectValue is Dictionary<string, object> project) ||
			!(project["rootPaths"] is object[] projectRoots) ||
			!projectRoots.Any((object root) => string.Equals(TextHelpers.CanonicalPath(Convert.ToString(root)), TextHelpers.CanonicalPath(expectedCwd), StringComparison.OrdinalIgnoreCase)))
		{
			throw new InvalidOperationException("desktop project root was not registered");
		}
		if (!writableRoots.TryGetValue(threadId, out object rootsValue) || !(rootsValue is object[] roots) ||
			!roots.Any((object root) => string.Equals(TextHelpers.CanonicalPath(Convert.ToString(root)), TextHelpers.CanonicalPath(expectedCwd), StringComparison.OrdinalIgnoreCase)) ||
			(!string.IsNullOrWhiteSpace(forbiddenCwd) && roots.Any((object root) => string.Equals(TextHelpers.CanonicalPath(Convert.ToString(root)), TextHelpers.CanonicalPath(forbiddenCwd), StringComparison.OrdinalIgnoreCase))) ||
			projectless.Any((object value) => string.Equals(Convert.ToString(value), threadId, StringComparison.OrdinalIgnoreCase)) ||
			(hints != null && hints.ContainsKey(threadId)))
		{
			throw new InvalidOperationException("desktop workspace roots or projectless state were not remapped");
		}
	}

	private static void AssertDesktopThreadAbsentForTest(string statePath, string threadId)
	{
		string json = File.ReadAllText(statePath, Encoding.UTF8);
		if (json.IndexOf(threadId, StringComparison.OrdinalIgnoreCase) >= 0)
		{
			throw new InvalidOperationException("desktop state still contains a deleted thread reference");
		}
	}


	private static int RunSelfTest(string report)
	{
		try
		{
			string text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CodexConversationManager.xaml");
			if (!File.Exists(text))
			{
				throw new FileNotFoundException("UI xaml not found", text);
			}
			{
				string localizedXaml = UiLanguage.LoadXaml(text);
				object obj = XamlReader.Parse(localizedXaml.Replace("Icon=\"assets/app-icon.ico\"", ""));
				if (!(obj is Window window))
				{
					throw new InvalidDataException("XAML root is not Window");
				}
				string[] array = new string[]
				{
					"MergeModeRadio", "CopyModeRadio", "ImportModeHelpText", "ConversationOverlay", "ConversationList", "ConversationCloseButton", "ConversationCanvas", "ConversationDialogHost", "TrashButton", "BackupProjectFilesButton", "ProjectRestorePanel",
					"RestoreProjectFilesCheck", "ProjectConflictCombo", "BackupFolderBox", "BrowseBackupFolderButton", "SelectAllProjectsButton", "ClearProjectsButton", "TargetPathLabel", "TargetPathHelpText", "MaximizeGlyph", "RestoreGlyph",
					"ProjectBackupModeRadio", "ConversationBackupModeRadio", "BackupModeHelpText", "ProjectSelectionTools", "SessionSelectionTools", "ToggleSessionSelectionButton", "DeleteSelectedSessionsButton", "ProjectPaneTitle", "ProjectPaneSubtitle", "SessionModeHint", "SelectionHelpText",
					"MainSessionsTabRadio", "SubagentSessionsTabRadio", "CopyProjectPathButton", "ProjectSizeText",
					"BrowsePackageButton", "BrowseTargetButton", "ImportProgressPanel", "ImportStageText", "ImportStageDetailText", "ImportElapsedText", "ImportStageProgress", "ImportWorkflowGrid", "ImportActionBar", "LanguageButton",
					"ConversationContentLayout", "ConversationNavigationHost", "ConversationNavigationScroller", "ConversationNavigationRail", "ConversationNavigationPreviewPopup", "ConversationNavigationPreviewTitle", "ConversationNavigationPreviewResponse"
				};
				string[] array2 = array;
				foreach (string text2 in array2)
				{
					if (window.FindName(text2) == null)
					{
						throw new InvalidDataException("UI control missing: " + text2);
					}
				}
				window.Icon = BitmapFrame.Create(new Uri(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets/app-icon.ico")));
				if (window.Icon == null)
				{
					throw new InvalidDataException("application icon was not loaded");
				}
				window.Close();
			}
			if (!DialogUi.VerifyThemeForTest())
			{
				throw new InvalidDataException("dialog theme did not apply to generated controls");
			}
			string text3 = RunFeatureSafetyTest();
			string presentationDataTests = RunPresentationDataTest();
			List<SessionInfo> list = NativeSessionCatalog.ScanDefault();
			CatalogResult catalogResult = CodexCatalog.Build(list);
			int linkedSubagents = list.Count((SessionInfo session) => session.IsSubagent && !string.IsNullOrWhiteSpace(session.ParentThreadId));
			int sizedSessions = list.Count((SessionInfo session) => session.SizeBytes > 0L && !string.IsNullOrWhiteSpace(session.DisplayPath));
			if (catalogResult.InternalCount > 0 && linkedSubagents == 0)
			{
				throw new InvalidDataException("subagent parent links were not detected");
			}
			if (list.Count > 0 && sizedSessions == 0)
			{
				throw new InvalidDataException("session file sizes and paths were not detected");
			}
			ProjectGroup sampleProject = catalogResult.Projects.FirstOrDefault((ProjectGroup x) => x.Sessions.Count > 0);
			SessionInfo sampleParent = list.FirstOrDefault((SessionInfo x) => !x.IsSubagent);
			bool sampleParentGrouped = sampleParent != null && catalogResult.Projects.Any((ProjectGroup x) => x.Sessions.Contains(sampleParent));
			string contents = "OK\r\nEngine=Native\r\nRawSessions=" + list.Count + "\r\nProjects=" + catalogResult.Projects.Count + "\r\nMain=" + catalogResult.MainCount + "\r\nSubagents=" + catalogResult.InternalCount + "\r\nLinkedSubagents=" + linkedSubagents + "\r\nSizedSessions=" + sizedSessions + "\r\nUsedCodexIndex=" + catalogResult.UsedCodexIndex + "\r\nFeatureTests=" + text3 + "\r\nPresentationDataTests=" + presentationDataTests + "\r\nSampleParentFound=" + (sampleParent != null) + "\r\nSampleParentGrouped=" + sampleParentGrouped + "\r\nSampleProjectMain=" + (sampleProject?.MainCount ?? 0) + "\r\nSampleProjectSubagents=" + (sampleProject?.InternalCount ?? 0);
			if (!string.IsNullOrWhiteSpace(report))
			{
				File.WriteAllText(report, contents, Encoding.UTF8);
			}
			return 0;
		}
		catch (Exception ex)
		{
			if (!string.IsNullOrWhiteSpace(report))
			{
				File.WriteAllText(report, "FAIL\r\n" + ex, Encoding.UTF8);
			}
			return 1;
		}
	}

	private static string RunPresentationDataTest()
	{
		string root = Path.Combine(Path.GetTempPath(), "codex-migrator-presentation-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			string project = Path.Combine(root, "project");
			Directory.CreateDirectory(Path.Combine(project, "nested"));
			File.WriteAllBytes(Path.Combine(project, "one.bin"), new byte[1024]);
			File.WriteAllBytes(Path.Combine(project, "nested", "two.bin"), new byte[2048]);
			ProjectStorageSummary metrics = ProjectStorageMetrics.Measure(project);
			if (metrics.TotalBytes != 3072L || metrics.FileCount != 2 || metrics.DirectoryCount != 1)
			{
				throw new InvalidDataException("project storage metric test failed");
			}

			string sessionPath = Path.Combine(root, "subagent.jsonl");
			string ambient = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"<environment_context><cwd>C:\\\\work</cwd></environment_context>\"}]}}";
			string prompt = "{\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"检查导入文件并生成迁移方案\"}]}}";
			File.WriteAllText(sessionPath, ambient + Environment.NewLine + prompt + Environment.NewLine, Encoding.UTF8);
			string title = ConversationReader.ReadTitleCandidate(sessionPath);
			if (!string.Equals(title, "检查导入文件并生成迁移方案", StringComparison.Ordinal))
			{
				throw new InvalidDataException("subagent title extraction test failed");
			}
			string driveIndexPath = TextHelpers.ToCodexIndexPath(@"D:\test");
			if (!string.Equals(driveIndexPath, @"\\?\D:\test", StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(TextHelpers.ToCodexIndexPath(driveIndexPath), driveIndexPath, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Codex drive index path normalization test failed");
			}
			string uncIndexPath = TextHelpers.ToCodexIndexPath(@"\\server\share\project");
			if (!string.Equals(uncIndexPath, @"\\?\UNC\server\share\project", StringComparison.OrdinalIgnoreCase) ||
				!string.Equals(TextHelpers.CanonicalPath(uncIndexPath), TextHelpers.CanonicalPath(@"\\server\share\project"), StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Codex UNC index path normalization test failed");
			}

			return "ProjectSize=3072B+2files · SubagentTitle=meaningful-user-prompt · SessionPath+Size=visible · CodexIndexPath=drive+unc+idempotent";
		}
		finally
		{
			try
			{
				if (Directory.Exists(root))
				{
					Directory.Delete(root, recursive: true);
				}
			}
			catch
			{
			}
		}
	}

}
