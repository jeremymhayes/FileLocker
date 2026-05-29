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
                LaunchArguments launchArguments = ParseLaunchArguments(Environment.GetCommandLineArgs());

                string appDataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FileLocker");
                Directory.CreateDirectory(appDataDirectory);
                AppPreferences preferences = await AppPreferencesStore.LoadAsync(appDataDirectory);

                _window = new MainWindow(preferences, launchArguments.Paths, launchArguments.Action);
                _window.Activate();
            }
            catch (Exception ex) when (IsCancellationException(ex))
            {
                Debug.WriteLine($"FileLocker launch cancelled during shutdown: {ex.Message}");
            }
        }

        internal static LaunchArguments ParseLaunchArguments(IEnumerable<string> commandLineArgs)
        {
            string? launchAction = null;
            var launchPaths = new List<string>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string argument in commandLineArgs.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    continue;
                }

                string trimmed = argument.Trim();
                if (trimmed.StartsWith("--", StringComparison.Ordinal))
                {
                    string? normalizedAction = NormalizeLaunchAction(trimmed);
                    if (!string.IsNullOrWhiteSpace(normalizedAction))
                    {
                        launchAction = normalizedAction;
                    }

                    continue;
                }

                if (trimmed.Length > MainWindow.MaxBridgeStringValueChars || !seenPaths.Add(trimmed))
                {
                    continue;
                }

                launchPaths.Add(trimmed);
                if (launchPaths.Count >= MainWindow.MaxBridgeStringListItems)
                {
                    break;
                }
            }

            return new LaunchArguments(launchPaths.ToArray(), launchAction);
        }

        private static string? NormalizeLaunchAction(string action)
        {
            const int maxLaunchActionChars = 128;
            string normalized = action.Trim().ToLowerInvariant();
            if (normalized.Length is 0 or > maxLaunchActionChars)
            {
                return null;
            }

            if (normalized is "--decrypt" or "--verify" or "--rotate")
            {
                return normalized;
            }

            const string pagePrefix = "--page=";
            if (!normalized.StartsWith(pagePrefix, StringComparison.Ordinal))
            {
                return null;
            }

            ReadOnlySpan<char> pageKey = normalized.AsSpan(pagePrefix.Length);
            if (pageKey.Length is 0 or > 64)
            {
                return null;
            }

            foreach (char character in pageKey)
            {
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return null;
                }
            }

            return normalized;
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

    internal sealed record LaunchArguments(IReadOnlyList<string> Paths, string? Action);
}
