using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FileLocker;

/// <summary>
/// Extracts an installed application's icon (from its registry DisplayIcon, or a
/// resolved executable) and returns it as a PNG data URI suitable for rendering
/// in the WebView App Manager. All work stays local; failures fall back to null
/// so the UI can show its initials placeholder instead.
/// </summary>
internal static class AppIconExtractor
{
    private const int IconPixelSize = 32;
    private const long MaxIconFileBytes = 4 * 1024 * 1024;

    public static string? TryGetIconDataUri(string? displayIcon, string installLocation, string uninstallCommand)
    {
        (string path, int index)? source = ResolveIconSource(displayIcon, installLocation, uninstallCommand);
        if (source is null)
        {
            return null;
        }

        try
        {
            using Icon? icon = LoadIcon(source.Value.path, source.Value.index);
            if (icon is null)
            {
                return null;
            }

            using Bitmap bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static (string path, int index)? ResolveIconSource(string? displayIcon, string installLocation, string uninstallCommand)
    {
        string? resolved = ParseDisplayIcon(displayIcon, out int index);
        if (resolved is not null && File.Exists(resolved))
        {
            return (resolved, index);
        }

        // Fall back to an executable inside the install location, if discoverable.
        string? exe = FindExecutable(installLocation) ?? ExtractExecutableFromCommand(uninstallCommand);
        if (exe is not null && File.Exists(exe))
        {
            return (exe, 0);
        }

        return null;
    }

    internal static string? ParseDisplayIcon(string? displayIcon, out int index)
    {
        index = 0;
        string? value = RegistryPathNormalizer.Normalize(displayIcon);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('@'))
        {
            value = value[1..].Trim();
            if (value.Length == 0)
            {
                return null;
            }
        }

        // DisplayIcon is often "C:\path\app.exe,0" or "...,-12" (resource index/id).
        int lastComma = value.LastIndexOf(',');
        if (lastComma >= 0 && int.TryParse(value[(lastComma + 1)..].Trim(), out int parsedIndex))
        {
            string path = value[..lastComma].Trim().Trim('"');
            if (path.Length == 0)
            {
                return null;
            }

            index = parsedIndex;
            value = path;
        }

        if (!IsSafeLocalPath(value, requireLeafName: true))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? FindExecutable(string installLocation)
    {
        string? normalized = RegistryPathNormalizer.Normalize(installLocation);
        if (string.IsNullOrWhiteSpace(normalized) ||
            !IsSafeLocalPath(normalized, requireLeafName: false) ||
            !Directory.Exists(normalized))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(normalized, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    internal static string? ExtractExecutableFromCommand(string? uninstallCommand)
    {
        string? command = StartupAppMaintenanceService.ParseStartupCommandTargetPath(uninstallCommand);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // Uninstaller executables rarely carry the app's real icon, so only use
        // them when they are not an obvious generic uninstaller stub.
        string fileName = Path.GetFileName(command);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return IsSafeLocalPath(command, requireLeafName: true) ? command : null;
    }

    private static Icon? LoadIcon(string path, int index)
    {
        if (!IsSafeLocalPath(path, requireLeafName: true))
        {
            return null;
        }

        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAcceptableIconFile(path))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(path);
            return new Icon(stream, new Size(IconPixelSize, IconPixelSize));
        }

        var largeIcons = new IntPtr[1];
        uint extracted = ExtractIconEx(path, index, largeIcons, null, 1);
        if (extracted == 0 || largeIcons[0] == IntPtr.Zero)
        {
            try
            {
                using Icon? associated = Icon.ExtractAssociatedIcon(path);
                return associated is null ? null : (Icon)associated.Clone();
            }
            catch
            {
                return null;
            }
        }

        try
        {
            using Icon fromHandle = Icon.FromHandle(largeIcons[0]);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(largeIcons[0]);
        }
    }

    private static bool IsAcceptableIconFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxIconFileBytes;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeLocalPath(string path, bool requireLeafName)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Any(character => char.IsControl(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format))
        {
            return false;
        }

        try
        {
            string trimmedPath = path.Trim();
            if (!Path.IsPathFullyQualified(trimmedPath))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(trimmedPath);
            if (requireLeafName && string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
            {
                return false;
            }

            string root = Path.GetPathRoot(fullPath) ?? string.Empty;
            string pathWithoutRoot = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
            return !pathWithoutRoot.Contains(':', StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
