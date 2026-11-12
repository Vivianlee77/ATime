using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ATime.Windows;

internal static class NativeMethods
{
    internal const uint EventSystemForeground = 0x0003;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const int WmPowerBroadcast = 0x0218;
    internal const int WmWtsSessionChange = 0x02B1;
    internal const int WmSample = 0x8001;
    internal const int WmStop = 0x8002;
    internal const int WmTimeChange = 0x001E;
    internal const int PbtPowerSettingChange = 0x8013;
    internal const int PbtApmSuspend = 0x0004;
    internal const int PbtApmResumeCritical = 0x0006;
    internal const int PbtApmResumeSuspend = 0x0007;
    internal const int PbtApmResumeAutomatic = 0x0012;
    internal static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    internal delegate void WinEventProc(nint hook, uint eventType, nint window,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        internal uint Size;
        internal uint Time;
    }

    // WTSINFOEXW's union is 8-byte aligned (its level 1 contains LARGE_INTEGER).
    // Only the non-identifying prefix is read; user/domain/station names are not marshalled.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct WtsInfoExPrefix
    {
        [FieldOffset(0)] internal uint Level;
        [FieldOffset(8)] internal uint SessionId;
        [FieldOffset(12)] internal int SessionState;
        [FieldOffset(16)] internal uint SessionFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime
    {
        internal uint Low;
        internal uint High;
        internal readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetDesktopWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,
        uint flags, StringBuilder fileName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process,
        out NativeFileTime creationTime, out NativeFileTime exitTime,
        out NativeFileTime kernelTime, out NativeFileTime userTime);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetApplicationUserModelId(SafeProcessHandle process,
        ref uint length, StringBuilder? applicationUserModelId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax,
        nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, int message, nint wParam, nint lParam);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(nint window);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSQuerySessionInformationW(nint server, int sessionId,
        int infoClass, out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    internal static extern void WTSFreeMemory(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint RegisterPowerSettingNotification(nint recipient,
        ref Guid settingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(nint handle);
}
