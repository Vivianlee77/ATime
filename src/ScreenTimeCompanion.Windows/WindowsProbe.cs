using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ScreenTimeCompanion.Core;

namespace ScreenTimeCompanion.Windows;

/// <summary>Read-only diagnostics. A one-shot probe cannot establish display power state.</summary>
public static class WindowsProbe
{
    public static TrackingObservation ReadOnce()
    {
        try
        {
            var facts = SessionFacts.Read();
            var app = new ForegroundIdentityResolver().Read();
            var input = new InputClock().Read(out var now);
            // Explicitly conservative: only the notification source can confirm display state.
            var reason = facts.InactiveReason ?? "WaitingForDisplay";
            return new(now, DateTimeOffset.UtcNow, input, app, false, reason, ReadUnbiased());
        }
        catch (Exception)
        {
            return Failure("CollectorError");
        }
    }

    internal static long? ReadUnbiased() => NativeMethods.QueryUnbiasedInterruptTime(out var value)
        ? checked((long)(value / 10_000)) : null;

    internal static TrackingObservation Failure(string reason)
    {
        long monotonic;
        try { monotonic = checked((long)NativeMethods.GetTickCount64()); }
        catch (Exception) { monotonic = Math.Max(0, Environment.TickCount64); }
        return new(monotonic, DateTimeOffset.UtcNow, null, AppIdentity.Unknown, false, reason);
    }
}

internal readonly record struct SessionFacts(int SessionId, bool? Connected, bool? Locked, bool? Remote)
{
    internal string? InactiveReason => Remote == true ? "UnsupportedSession"
        : Connected == false ? "Disconnected"
        : Locked == true ? "Locked"
        : Connected is null || Locked is null || Remote is null ? "SessionUnknown" : null;

    internal static SessionFacts Read()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var sessionId = process.SessionId;
            bool? connected = null, locked = null, remote = null;
            if (ReadBuffer(sessionId, 8, out var state)) connected = state == 0;
            nint buffer = 0;
            try
            {
                if (NativeMethods.WTSQuerySessionInformationW(0, sessionId, 16, out buffer, out var bytes)
                    && bytes >= sizeof(ushort))
                {
                    var protocol = unchecked((ushort)Marshal.ReadInt16(buffer));
                    remote = protocol == 0 ? false : protocol is 1 or 2 ? true : null;
                }
            }
            finally { if (buffer != 0) NativeMethods.WTSFreeMemory(buffer); }

            buffer = 0;
            try
            {
                if (NativeMethods.WTSQuerySessionInformationW(0, sessionId, 25, out buffer, out var bytes)
                    && bytes >= Marshal.SizeOf<NativeMethods.WtsInfoExPrefix>())
                {
                    var info = Marshal.PtrToStructure<NativeMethods.WtsInfoExPrefix>(buffer);
                    if (info.Level == 1 && info.SessionId == sessionId)
                    {
                        connected = info.SessionState == 0;
                        // Windows 11 baseline. Windows 7 had reversed flag semantics.
                        locked = info.SessionFlags == 0 ? true : info.SessionFlags == 1 ? false : null;
                    }
                }
            }
            finally { if (buffer != 0) NativeMethods.WTSFreeMemory(buffer); }
            return new(sessionId, connected, locked, remote);
        }
        catch (Exception) { return new(-1, null, null, null); }
    }

    private static bool ReadBuffer(int sessionId, int infoClass, out int value)
    {
        nint buffer = 0;
        value = 0;
        try
        {
            if (!NativeMethods.WTSQuerySessionInformationW(0, sessionId, infoClass, out buffer, out var bytes)
                || bytes < sizeof(int)) return false;
            value = Marshal.ReadInt32(buffer);
            return true;
        }
        finally { if (buffer != 0) NativeMethods.WTSFreeMemory(buffer); }
    }
}

internal sealed class InputClock
{
    private uint? _lastRaw;
    private long? _lastMapped;

    internal long? Read(out long now)
    {
        var input = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        var succeeded = NativeMethods.GetLastInputInfo(ref input);
        // Capture the observation time AFTER input retrieval. Capturing it before
        // WTS/process queries lets genuine intervening input look one wrap old.
        now = checked((long)NativeMethods.GetTickCount64());
        if (!succeeded) return null;
        if (_lastRaw == input.Time && _lastMapped.HasValue) return _lastMapped;
        if (!InputTickConverter.TryMap(input.Time, now, out var mapped)) return null;
        // A small backwards tick is allowed by the API; a substantial reversal is not
        // promoted to a new input event. Recover when a subsequent valid input arrives.
        if (_lastMapped is long previous && mapped < previous - 1_000) return null;
        _lastRaw = input.Time;
        _lastMapped = mapped;
        return mapped;
    }
}

internal sealed class ForegroundIdentityResolver
{
    private const int CacheLimit = 256;
    private readonly Dictionary<(uint Pid, ulong Created), AppIdentity> _cache = [];

    internal AppIdentity Read()
    {
        try
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == 0) return AppIdentity.Unknown;
            if (window == NativeMethods.GetShellWindow() || window == NativeMethods.GetDesktopWindow())
                return AppIdentity.Desktop;
            if (NativeMethods.GetWindowThreadProcessId(window, out var pid) == 0 || pid == 0)
                return AppIdentity.Unknown;
            if (pid == Environment.ProcessId) return AppIdentity.Self;
            using var process = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
            if (process.IsInvalid) return AppIdentity.Unknown;
            if (!NativeMethods.GetProcessTimes(process, out var created, out _, out _, out _))
                return AppIdentity.Unknown;
            var key = (pid, created.Value);

            // Window class is read per window, since explorer.exe also owns real file windows.
            var className = new StringBuilder(256);
            NativeMethods.GetClassNameW(window, className, className.Capacity);
            if (!_cache.TryGetValue(key, out var identity))
            {
                var path = new StringBuilder(32_768);
                var length = (uint)path.Capacity;
                if (!NativeMethods.QueryFullProcessImageNameW(process, 0, path, ref length))
                    return AppIdentity.Unknown;
                var executable = Path.GetFileName(path.ToString());
                // A frame-host PID does not establish the actual packaged application identity.
                if (executable.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                    return AppIdentity.Unknown;
                var identityText = "path:" + path.ToString().Replace('/', '\\').ToUpperInvariant();
                uint appIdLength = 0;
                if (NativeMethods.GetApplicationUserModelId(process, ref appIdLength, null) == 122
                    && appIdLength is > 0 and < 4096)
                {
                    var appId = new StringBuilder((int)appIdLength);
                    if (NativeMethods.GetApplicationUserModelId(process, ref appIdLength, appId) == 0)
                        identityText = "aumid:" + appId;
                }
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityText))).ToLowerInvariant();
                identity = new("app-" + hash, FriendlyName(executable), executable);
                if (_cache.Count >= CacheLimit) _cache.Clear();
                _cache[key] = identity;
            }
            if (identity.ExecutableName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                && className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return AppIdentity.Desktop;
            return identity;
        }
        catch (Exception) { return AppIdentity.Unknown; }
    }

    private static string FriendlyName(string executable) => executable.ToLowerInvariant() switch
    {
        "code.exe" => "Visual Studio Code",
        "msedge.exe" => "Microsoft Edge",
        "chrome.exe" => "Google Chrome",
        "firefox.exe" => "Firefox",
        "explorer.exe" => "文件资源管理器",
        "windowsterminal.exe" => "Windows 终端",
        "powershell.exe" or "pwsh.exe" => "PowerShell",
        "cmd.exe" => "命令提示符",
        "notepad.exe" => "记事本",
        "systemsettings.exe" => "Windows 设置",
        "wechat.exe" or "weixin.exe" => "微信",
        "feishu.exe" => "飞书",
        "qq.exe" => "QQ",
        "winword.exe" => "Microsoft Word",
        "excel.exe" => "Microsoft Excel",
        "powerpnt.exe" => "Microsoft PowerPoint",
        "devenv.exe" => "Visual Studio",
        "codex.exe" => "Codex",
        _ => Path.GetFileNameWithoutExtension(executable) is { Length: > 0 } name ? name : "未命名应用"
    };
}
