using System.ComponentModel;
using Alterius.Core;

namespace Alterius.Upload;

/// <summary>
/// Manages authenticated SMB/UNC share connections via Win32 <c>WNetAddConnection2</c>.
/// </summary>
public sealed class SmbConnectionManager : IDisposable
{
    private const int NoError = 0;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorDeviceAlreadyRemembered = 1202;
    private const int ErrorSessionCredentialConflict = 1219;

    private readonly FileLogger _logger;
    private readonly object _sync = new();

    private string _connectedRoot = string.Empty;
    private string _connectedUsername = string.Empty;
    private string _connectedPassword = string.Empty;
    private bool _hasExplicitConnection;
    private bool _disposed;

    public SmbConnectionManager(FileLogger logger)
    {
        _logger = logger;
    }

    public static string ExtractShareRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim().Replace('/', '\\');
        if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return trimmed.TrimEnd('\\');
        }

        string withoutPrefix = trimmed[2..];
        string[] segments = withoutPrefix.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
        {
            return $@"\\{segments[0]}\{segments[1]}";
        }

        return trimmed.TrimEnd('\\');
    }

    public bool EnsureConnected(string sharePath, string username, string plainPassword, out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(sharePath))
        {
            errorMessage = "SMB share path is not configured.";
            return false;
        }

        string normalizedTarget = sharePath.Trim().TrimEnd('\\');
        string shareRoot = ExtractShareRoot(normalizedTarget);

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                DisconnectLocked();
                return VerifyAccessible(normalizedTarget, shareRoot, out errorMessage);
            }

            bool sameCredentials = _hasExplicitConnection
                && string.Equals(_connectedRoot, shareRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_connectedUsername, username, StringComparison.Ordinal)
                && string.Equals(_connectedPassword, plainPassword, StringComparison.Ordinal);

            if (sameCredentials && Directory.Exists(shareRoot))
            {
                errorMessage = null;
                return true;
            }

            DisconnectLocked();

            int result = ConnectWNet(shareRoot, username, plainPassword);
            if (result == ErrorSessionCredentialConflict)
            {
                // Replace conflicting existing SMB session to the same server/share.
                NativeMethods.WNetCancelConnection2(shareRoot, 0, true);
                result = ConnectWNet(shareRoot, username, plainPassword);
            }

            if (result != NoError && result != ErrorAlreadyAssigned && result != ErrorDeviceAlreadyRemembered)
            {
                string win32Msg = new Win32Exception(result).Message;
                errorMessage = $"SMB connection to '{shareRoot}' failed (Win32 {result}: {win32Msg}).";
                return false;
            }

            _connectedRoot = shareRoot;
            _connectedUsername = username;
            _connectedPassword = plainPassword;
            _hasExplicitConnection = true;

            if (!VerifyAccessible(normalizedTarget, shareRoot, out errorMessage))
            {
                DisconnectLocked();
                return false;
            }

            _logger.Info(nameof(SmbConnectionManager), $"Connected to SMB share '{shareRoot}' as '{username}'.");
            errorMessage = null;
            return true;
        }
    }

    public bool TestConnection(string sharePath, string username, string plainPassword, out string resultMessage)
    {
        if (string.IsNullOrWhiteSpace(sharePath))
        {
            resultMessage = "Укажите UNC-путь к SMB-шаре (например, \\\\SERVER\\Share).";
            return false;
        }

        string normalizedTarget = sharePath.Trim().TrimEnd('\\');
        string shareRoot = ExtractShareRoot(normalizedTarget);

        if (string.IsNullOrWhiteSpace(username))
        {
            if (VerifyAccessible(normalizedTarget, shareRoot, out string? error))
            {
                resultMessage = $"Подключение к '{shareRoot}' успешно (текущая учётная запись Windows).";
                return true;
            }

            resultMessage = error ?? $"Шара '{shareRoot}' недоступна.";
            return false;
        }

        lock (_sync)
        {
            int code = ConnectWNet(shareRoot, username, plainPassword);
            if (code == ErrorSessionCredentialConflict)
            {
                NativeMethods.WNetCancelConnection2(shareRoot, 0, true);
                code = ConnectWNet(shareRoot, username, plainPassword);
            }

            if (code != NoError && code != ErrorAlreadyAssigned && code != ErrorDeviceAlreadyRemembered)
            {
                string win32Msg = new Win32Exception(code).Message;
                resultMessage = $"Ошибка подключения (Win32 {code}): {win32Msg}";
                return false;
            }

            _connectedRoot = shareRoot;
            _connectedUsername = username;
            _connectedPassword = plainPassword;
            _hasExplicitConnection = true;

            if (VerifyAccessible(normalizedTarget, shareRoot, out string? verifyError))
            {
                resultMessage = $"Подключение к '{shareRoot}' успешно.";
                return true;
            }

            DisconnectLocked();
            resultMessage = verifyError ?? $"Не удалось открыть '{shareRoot}'.";
            return false;
        }
    }

    public void ResetConnection()
    {
        lock (_sync)
        {
            DisconnectLocked();
        }
    }

    private static int ConnectWNet(string remoteName, string username, string plainPassword)
    {
        var netResource = new NativeMethods.NETRESOURCE
        {
            dwType = NativeMethods.RESOURCETYPE_DISK,
            lpRemoteName = remoteName
        };

        return NativeMethods.WNetAddConnection2(netResource, plainPassword, username, 0);
    }

    private static bool VerifyAccessible(string fullPath, string shareRoot, out string? errorMessage)
    {
        try
        {
            string probePath = Directory.Exists(fullPath) ? fullPath : shareRoot;
            if (!Directory.Exists(probePath))
            {
                errorMessage = $"SMB path '{probePath}' is unreachable or does not exist.";
                return false;
            }

            // Enumerate first entry to force an actual SMB metadata roundtrip and validate read permission.
            _ = Directory.EnumerateFileSystemEntries(probePath).Take(1).Count();
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"SMB access check failed for '{shareRoot}': {ex.Message}";
            return false;
        }
    }

    private void DisconnectLocked()
    {
        if (!_hasExplicitConnection || string.IsNullOrEmpty(_connectedRoot))
        {
            return;
        }

        try
        {
            NativeMethods.WNetCancelConnection2(_connectedRoot, 0, false);
        }
        catch
        {
            // Best-effort disconnect.
        }

        _connectedRoot = string.Empty;
        _connectedUsername = string.Empty;
        _connectedPassword = string.Empty;
        _hasExplicitConnection = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            DisconnectLocked();
        }
    }
}
