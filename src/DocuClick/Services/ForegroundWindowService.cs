using System.Runtime.InteropServices;

namespace DocuClick.Services;

/// <summary>Fallback window-title lookup for when UI Automation yields nothing.</summary>
internal static partial class ForegroundWindowService
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(nint hWnd, [Out] char[] lpString, int nMaxCount);

    internal static nint GetHandle() => GetForegroundWindow();

    public static string? GetTitle()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == 0)
        {
            return null;
        }

        var buffer = new char[256];
        var length = GetWindowTextW(hWnd, buffer, buffer.Length);
        if (length <= 0)
        {
            return null;
        }

        var title = new string(buffer, 0, length);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
