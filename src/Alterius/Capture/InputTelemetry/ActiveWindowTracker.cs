using System.Diagnostics;
using Alterius.Core;

namespace Alterius.Capture.InputTelemetry;

/// <summary>
/// Resolves the current Windows foreground window handle, title, and process name using Win32 APIs.
/// </summary>
public sealed class ActiveWindowTracker
{
    private readonly Dictionary<uint, string> _processNameCache = new();

    public ActiveWindowInfo GetForegroundWindowInfo()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return new ActiveWindowInfo(IntPtr.Zero, "0x0", string.Empty, "idle");
        }

        string title = ReadWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string processName = pid != 0 ? ResolveProcessName(pid) : "unknown";
        string hwndHex = $"0x{hwnd.ToInt64():X}";

        return new ActiveWindowInfo(hwnd, hwndHex, title, processName);
    }

    private static string ReadWindowTitle(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        int cappedLength = Math.Min(length, 512);
        char[] buffer = new char[cappedLength + 1];
        int copied = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied).Trim() : string.Empty;
    }

    private string ResolveProcessName(uint pid)
    {
        if (_processNameCache.TryGetValue(pid, out string? cached))
        {
            return cached;
        }

        string resolved = "unknown";
        IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                char[] pathBuffer = new char[1024];
                int size = pathBuffer.Length;
                if (NativeMethods.QueryFullProcessImageName(hProcess, 0, pathBuffer, ref size) && size > 0)
                {
                    string fullPath = new(pathBuffer, 0, size);
                    resolved = Path.GetFileNameWithoutExtension(fullPath);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }
        else
        {
            try
            {
                using Process proc = Process.GetProcessById((int)pid);
                if (!string.IsNullOrWhiteSpace(proc.ProcessName))
                {
                    resolved = proc.ProcessName;
                }
            }
            catch
            {
                // Elevated or already-exited process; keep "unknown".
            }
        }

        if (_processNameCache.Count > 512)
        {
            _processNameCache.Clear();
        }

        _processNameCache[pid] = resolved;
        return resolved;
    }
}

public readonly record struct ActiveWindowInfo(
    IntPtr Handle,
    string HwndHex,
    string WindowTitle,
    string ProcessName);
