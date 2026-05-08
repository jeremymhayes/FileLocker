using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace FileLocker
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            InitializeComponent();
            UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                string[] commandLineArgs = Environment.GetCommandLineArgs();
                string? launchAction = null;
                var launchPaths = new List<string>();

                foreach (string argument in commandLineArgs.Skip(1))
                {
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        launchAction = argument.ToLowerInvariant();
                    }
                    else if (!string.IsNullOrWhiteSpace(argument))
                    {
                        launchPaths.Add(argument);
                    }
                }

                string appDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileLocker");
                Directory.CreateDirectory(appDataDirectory);
                AppPreferences preferences = await AppPreferencesStore.LoadAsync(appDataDirectory);

                _window = new MainWindow(preferences, launchPaths, launchAction);
                _window.Activate();
            }
            catch (Exception ex) when (IsCancellationException(ex))
            {
                Debug.WriteLine($"FileLocker launch cancelled during shutdown: {ex.Message}");
            }
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            if (!IsBenignShutdownException(e.Exception))
            {
                return;
            }

            if (_window == null && e.Exception is COMException)
            {
                return;
            }

            Debug.WriteLine($"Handled benign WinUI exception: {e.Exception.Message}");
            e.Handled = true;
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (!IsBenignShutdownException(e.Exception))
            {
                return;
            }

            Debug.WriteLine($"Observed benign background task exception: {e.Exception.Message}");
            e.SetObserved();
        }

        private static bool IsBenignShutdownException(Exception exception)
        {
            if (exception is OperationCanceledException or TaskCanceledException or COMException)
            {
                return true;
            }

            if (exception is AggregateException aggregateException)
            {
                return aggregateException.Flatten().InnerExceptions.All(IsBenignShutdownException);
            }

            return false;
        }

        private static bool IsCancellationException(Exception exception)
        {
            if (exception is OperationCanceledException or TaskCanceledException)
            {
                return true;
            }

            if (exception is AggregateException aggregateException)
            {
                return aggregateException.Flatten().InnerExceptions.All(IsCancellationException);
            }

            return false;
        }
    }
}
