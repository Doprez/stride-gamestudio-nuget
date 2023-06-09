﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

using Stride.Core.Assets;
using Stride.Core.Assets.Editor;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.ViewModels;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.Views;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.Settings;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.MostRecentlyUsedFiles;
using Stride.Core.Presentation.View;
using Stride.Core.Presentation.ViewModel;
using Stride.Core.Presentation.Windows;
using Stride.Core.Translation;
using Stride.Core.Translation.Providers;
using Stride.Assets.Presentation;
using Stride.Editor.Build;
using Stride.Editor.Engine;
using Stride.Editor.Preview;
using Stride.GameStudio.View;
using Stride.Graphics;
using Stride.Metrics;
using EditorSettings = Stride.Core.Assets.Editor.Settings.EditorSettings;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using Stride.GameStudio;

namespace StrideNuget;

internal class Program
{
	private static App app;
	private static Dispatcher mainDispatcher;
	private static RenderDocManager renderDocManager;
	private static readonly ConcurrentQueue<string> LogRingbuffer = new();

	[STAThread]
	public static void Main()
	{
		EditorPath.EditorTitle = StrideGameStudio.EditorName;

		if (IntPtr.Size == 4)
		{
			MessageBox.Show("Stride GameStudio requires a 64bit OS to run.", "Stride", MessageBoxButton.OK, MessageBoxImage.Error);
			Environment.Exit(1);
		}

		// We use MRU of the current version only when we're trying to reload last session.
		var mru = new MostRecentlyUsedFileCollection(InternalSettings.LoadProfileCopy, InternalSettings.MostRecentlyUsedSessions, InternalSettings.WriteFile);
		mru.LoadFromSettings();

		EditorSettings.Initialize();
		Thread.CurrentThread.Name = "Main thread";

		// Install Metrics for the editor
		using (StrideGameStudio.MetricsClient = new MetricsClient(CommonApps.StrideEditorAppId))
		{
			try
			{
				// Environment.GetCommandLineArgs correctly process arguments regarding the presence of '\' and '"'
				var args = Environment.GetCommandLineArgs().Skip(1).ToList();
				var startupSessionPath = StrideEditorSettings.StartupSession.GetValue();
				var lastSessionPath = EditorSettings.ReloadLastSession.GetValue() ? mru.MostRecentlyUsedFiles.FirstOrDefault() : null;
				var initialSessionPath = !UPath.IsNullOrEmpty(startupSessionPath) ? startupSessionPath : lastSessionPath?.FilePath;

				// Handle arguments
				for (var i = 0; i < args.Count; i++)
				{
					if (args[i] == "/NewProject")
					{
						initialSessionPath = null;
					}
					else if (args[i] == "/DebugEditorGraphics")
					{
						EmbeddedGame.DebugMode = true;
					}
					else if (args[i] == "/RenderDoc")
					{
						// TODO: RenderDoc is not working here (when not in debug)
						GameStudioPreviewService.DisablePreview = true;
						renderDocManager = new RenderDocManager();
						renderDocManager.Initialize();
					}
					else if (args[i] == "/RecordEffects")
					{
						GameStudioBuilderService.GlobalEffectLogPath = args[++i];
					}
					else
					{
						initialSessionPath = args[i];
					}
				}
				RuntimeHelpers.RunModuleConstructor(typeof(Asset).Module.ModuleHandle);

				//listen to logger for crash report
				GlobalLogger.GlobalMessageLogged += GlobalLoggerOnGlobalMessageLogged;

				mainDispatcher = Dispatcher.CurrentDispatcher;
				mainDispatcher.InvokeAsync(() => Startup(initialSessionPath));

				using (new WindowManager(mainDispatcher))
				{
					app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
					app.Activated += (sender, eventArgs) =>
					{
						StrideGameStudio.MetricsClient?.SetActiveState(true);
					};
					app.Deactivated += (sender, eventArgs) =>
					{
						StrideGameStudio.MetricsClient?.SetActiveState(false);
					};

					app.InitializeComponent();
					app.Run();
				}

				renderDocManager?.RemoveHooks();
			}
			catch (Exception e)
			{
				Console.WriteLine(e.Message);
			}
		}
	}

	private static void GlobalLoggerOnGlobalMessageLogged(ILogMessage logMessage)
	{
		if (logMessage.Type <= LogMessageType.Warning) return;

		LogRingbuffer.Enqueue(logMessage.ToString());
		while (LogRingbuffer.Count > 5)
		{
			string msg;
			LogRingbuffer.TryDequeue(out msg);
		}
	}

	private static async void Startup(UFile initialSessionPath)
	{
		try
		{
			InitializeLanguageSettings();
			var serviceProvider = InitializeServiceProvider();

			try
			{
				PackageSessionPublicHelper.FindAndSetMSBuildVersion();
			}
			catch (Exception e)
			{
				var message = "Could not find a compatible version of MSBuild.\r\n\r\n" +
							  "Check that you have a valid installation with the required workloads, or go to [www.visualstudio.com/downloads](https://www.visualstudio.com/downloads) to install a new one.\r\n" +
							  "Also make sure you have the latest [.NET 6 SDK](https://dotnet.microsoft.com/) \r\n\r\n" +
							  e;
				//await serviceProvider.Get<IEditorDialogService>().MessageBox(message, Stride.Core.Presentation.Services.MessageBoxButton.OK, Stride.Core.Presentation.Services.MessageBoxImage.Error);
				app.Shutdown();
				return;
			}

			// We use a MRU that contains the older version projects to display in the editor
			var mru = new MostRecentlyUsedFileCollection(InternalSettings.LoadProfileCopy, InternalSettings.MostRecentlyUsedSessions, InternalSettings.WriteFile);
			mru.LoadFromSettings();
			var editor = new GameStudioViewModel(serviceProvider, mru);
			AssetsPlugin.RegisterPlugin(typeof(StrideDefaultAssetsPlugin));
			AssetsPlugin.RegisterPlugin(typeof(StrideEditorPlugin));

			// Attempt to load the startup session, if available
			if (!UPath.IsNullOrEmpty(initialSessionPath))
			{
				var sessionLoaded = await editor.OpenInitialSession(initialSessionPath);
				if (sessionLoaded == true)
				{
					var mainWindow = new GameStudioWindow(editor);
					Application.Current.MainWindow = mainWindow;
					WindowManager.ShowMainWindow(mainWindow);
					return;
				}
			}

			// No session successfully loaded, open the new/open project window
			bool? completed;
			// The user might cancel after chosing a template to instantiate, in this case we'll reopen the window
			var startupWindow = new ProjectSelectionWindow
			{
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				ShowInTaskbar = true,
			};
			var viewModel = new NewOrOpenSessionTemplateCollectionViewModel(serviceProvider, startupWindow);
			startupWindow.Templates = viewModel;
			startupWindow.ShowDialog();

			// The user selected a template to instantiate
			if (startupWindow.NewSessionParameters != null)
			{
				// Clean existing entry in the MRU data
				var directory = startupWindow.NewSessionParameters.OutputDirectory;
				var name = startupWindow.NewSessionParameters.OutputName;
				//var mruData = new MRUAdditionalDataCollection(InternalSettings.LoadProfileCopy, GameStudioInternalSettings.MostRecentlyUsedSessionsData, InternalSettings.WriteFile);
				//mruData.RemoveFile(UFile.Combine(UDirectory.Combine(directory, name), new UFile(name + SessionViewModel.SolutionExtension)));

				completed = await editor.NewSession(startupWindow.NewSessionParameters);
			}
			// The user selected a path to open
			else if (startupWindow.ExistingSessionPath != null)
			{
				completed = await editor.OpenSession(startupWindow.ExistingSessionPath);
			}
			// The user cancelled from the new/open project window, so exit the application
			else
			{
				completed = true;
			}

			if (completed != true)
			{
				var windowsClosed = new List<Task>();
				foreach (var window in Application.Current.Windows.Cast<Window>().Where(x => x.IsLoaded))
				{
					var tcs = new TaskCompletionSource<int>();
					window.Unloaded += (s, e) => tcs.SetResult(0);
					windowsClosed.Add(tcs.Task);
				}

				await Task.WhenAll(windowsClosed);

				// When a project has been partially loaded, it might already have initialized some plugin that could conflict with
				// the next attempt to start something. Better start the application again.
				var commandLine = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(x => $"\"{x}\""));
				var process = new Process { StartInfo = new ProcessStartInfo(typeof(Program).Assembly.Location, commandLine) };
				process.Start();
				app.Shutdown();
				return;
			}

			if (editor.Session != null)
			{
				// If a session was correctly loaded, show the main window
				var mainWindow = new GameStudioWindow(editor);
				Application.Current.MainWindow = mainWindow;
				WindowManager.ShowMainWindow(mainWindow);
			}
			else
			{
				// Otherwise, exit.
				app.Shutdown();
			}
		}
		catch (Exception)
		{
			app.Shutdown();
		}
	}

	private static IViewModelServiceProvider InitializeServiceProvider()
	{
		// TODO: this should be done elsewhere
		var dispatcherService = new DispatcherService(Dispatcher.CurrentDispatcher);
		var dialogService = new StrideDialogService(dispatcherService, StrideGameStudio.EditorName);
		var pluginService = new PluginService();
		var services = new List<object> { new DispatcherService(Dispatcher.CurrentDispatcher), dialogService, pluginService };
		if (renderDocManager != null)
			services.Add(renderDocManager);
		var serviceProvider = new ViewModelServiceProvider(services);
		return serviceProvider;
	}

	private static void InitializeLanguageSettings()
	{
		TranslationManager.Instance.RegisterProvider(new GettextTranslationProvider());
		switch (EditorSettings.Language.GetValue())
		{
			case SupportedLanguage.MachineDefault:
				TranslationManager.Instance.CurrentLanguage = CultureInfo.InstalledUICulture;
				break;
			case SupportedLanguage.English:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("en-US");
				break;
			case SupportedLanguage.French:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("fr-FR");
				break;
			case SupportedLanguage.Japanese:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("ja-JP");
				break;
			case SupportedLanguage.Russian:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("ru-RU");
				break;
			case SupportedLanguage.German:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("de-DE");
				break;
			case SupportedLanguage.Spanish:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("es-ES");
				break;
			case SupportedLanguage.ChineseSimplified:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("zh-Hans");
				break;
			case SupportedLanguage.Italian:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("it-IT");
				break;
			case SupportedLanguage.Korean:
				TranslationManager.Instance.CurrentLanguage = new CultureInfo("ko-KR");
				break;
			default:
				throw new ArgumentOutOfRangeException();
		}
	}
}
