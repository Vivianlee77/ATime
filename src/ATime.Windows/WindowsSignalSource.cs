using System.Runtime.InteropServices;
using ATime.Core;
using Forms = System.Windows.Forms;

namespace ATime.Windows;

/// <summary>
/// Observes only the current Windows session. Events arrive on a dedicated background
/// STA thread: handlers should enqueue observations and must not directly access UI controls.
/// </summary>
public sealed class WindowsSignalSource : IDisposable
{
    private readonly object _lifecycleLock = new();
    private readonly ForegroundIdentityResolver _identities = new();
    private readonly InputClock _inputClock = new();
    private Thread? _thread;
    private nint _windowHandle;
    private int _disposed;
    private TrackingObservation? _latest;
    private string? _lastError;

    // The following fields are owned by the collector's STA thread only.
    private SignalWindow? _window;
    private Forms.ApplicationContext? _context;
    private Forms.Timer? _timer;
    private NativeMethods.WinEventProc? _foregroundCallback;
    private nint _foregroundHook;
    private nint _powerRegistration;
    private bool _sessionRegistered;
    private SessionFacts _session;
    private int? _displayState;
    private bool _suspended;
    private long? _lastSampleMono;
    private long? _lastUnbiased;
    private long _nextRegistrationRetry;

    public event Action<TrackingObservation>? Observation;
    public TrackingObservation? Latest => Volatile.Read(ref _latest);
    /// <summary>A short diagnostic code, never a path, title, or input content.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_thread is not null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "ATime.WindowsSignals" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            thread = _thread;
            var handle = Volatile.Read(ref _windowHandle);
            if (handle != 0)
            {
                try { NativeMethods.PostMessageW(handle, NativeMethods.WmStop, 0, 0); }
                catch (Exception) { Volatile.Write(ref _lastError, "CollectorStopFailed"); }
            }
        }
        if (thread is not null && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
            thread.Join(TimeSpan.FromSeconds(2));
        GC.SuppressFinalize(this);
    }

    private void Run()
    {
        try
        {
            _context = new Forms.ApplicationContext();
            _window = new SignalWindow(this);
            _window.Create();
            Volatile.Write(ref _windowHandle, _window.Handle);
            if (Volatile.Read(ref _disposed) != 0) return;

            RegisterNotifications();
            _session = SessionFacts.Read();
            _timer = new Forms.Timer { Interval = 1_000 };
            _timer.Tick += OnTick;
            _timer.Start();
            Sample(refreshSession: false);
            Forms.Application.Run(_context);
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastError, "CollectorInitializationFailed");
            Publish(WindowsProbe.Failure("CollectorError"));
        }
        finally
        {
            _timer?.Stop();
            _timer?.Dispose();
            if (_foregroundHook != 0) NativeMethods.UnhookWinEvent(_foregroundHook);
            if (_powerRegistration != 0) NativeMethods.UnregisterPowerSettingNotification(_powerRegistration);
            if (_sessionRegistered && _window is not null)
                NativeMethods.WTSUnRegisterSessionNotification(_window.Handle);
            _window?.DestroyHandle();
            Volatile.Write(ref _windowHandle, 0);
            _context?.Dispose();
            // Root the managed callback for the full lifetime of the native registration.
            GC.KeepAlive(_foregroundCallback);
        }
    }

    private void RegisterNotifications()
    {
        if (_window is null) return;
        if (!_sessionRegistered)
            _sessionRegistered = NativeMethods.WTSRegisterSessionNotification(_window.Handle, 0);
        if (_powerRegistration == 0) RegisterDisplayNotification();
        if (_foregroundHook == 0)
        {
            _foregroundCallback ??= (_, _, _, _, _, _, _) =>
            {
                // Hooks do no identity work or database work. Do not skip this process:
                // foreground time spent in our own windows belongs to the self bucket.
                try
                {
                    var handle = Volatile.Read(ref _windowHandle);
                    if (handle != 0 && Volatile.Read(ref _disposed) == 0)
                        NativeMethods.PostMessageW(handle, NativeMethods.WmSample, 0, 0);
                }
                catch (Exception) { Volatile.Write(ref _lastError, "ForegroundNotificationFailed"); }
            };
            _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemForeground,
                NativeMethods.EventSystemForeground, 0, _foregroundCallback, 0, 0, 0);
        }
        Volatile.Write(ref _lastError, !_sessionRegistered ? "SessionNotificationRegistrationFailed"
            : _powerRegistration == 0 ? "DisplayNotificationRegistrationFailed"
            : _foregroundHook == 0 ? "ForegroundHookUnavailablePollingActive" : null);
    }

    private void RegisterDisplayNotification()
    {
        _displayState = null;
        if (_powerRegistration != 0) NativeMethods.UnregisterPowerSettingNotification(_powerRegistration);
        _powerRegistration = 0;
        if (_window is null) return;
        var guid = NativeMethods.SessionDisplayStatus;
        _powerRegistration = NativeMethods.RegisterPowerSettingNotification(_window.Handle, ref guid, 0);
    }

    private void OnTick(object? sender, EventArgs args)
    {
        try
        {
            var now = checked((long)NativeMethods.GetTickCount64());
            if (now >= _nextRegistrationRetry)
            {
                RegisterNotifications();
                _nextRegistrationRetry = now + 5_000;
            }
            Sample(refreshSession: true);
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastError, "CollectorTimerFailed");
            Publish(WindowsProbe.Failure("CollectorError"));
        }
    }

    private void Sample(bool refreshSession)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (refreshSession) _session = SessionFacts.Read();
            var app = _identities.Read();
            var input = _inputClock.Read(out var now);
            var utc = DateTimeOffset.UtcNow;
            var unbiased = WindowsProbe.ReadUnbiased();
            if (_lastSampleMono is long oldMono && _lastUnbiased is long oldUnbiased
                && unbiased is long currentUnbiased
                && now - oldMono - (currentUnbiased - oldUnbiased) > 1_000)
            {
                // Critical sleep may omit its pre-suspend event. The engine also receives
                // both clocks and treats the unobserved interval as a gap.
                _suspended = false;
                RegisterDisplayNotification();
                _session = SessionFacts.Read();
            }
            _lastSampleMono = now;
            _lastUnbiased = unbiased;
            var reason = _session.InactiveReason
                ?? (_suspended ? "Suspended" : null)
                ?? (_displayState is null ? "WaitingForDisplay" : null)
                ?? (_displayState == 0 ? "DisplayOff" : null)
                ?? (input is null ? "InputUnknown" : null);
            Publish(new(now, utc, input, app, reason is null, reason, unbiased));
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastError, "SampleFailed");
            Publish(WindowsProbe.Failure("CollectorError"));
        }
    }

    private void Publish(TrackingObservation observation)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Volatile.Write(ref _latest, observation);
        var handlers = Observation;
        if (handlers is null) return;
        foreach (Action<TrackingObservation> handler in handlers.GetInvocationList())
        {
            try { handler(observation); }
            catch (Exception) { Volatile.Write(ref _lastError, "ObservationSubscriberFailed"); }
        }
    }

    private void HandleMessage(ref Forms.Message message)
    {
        try
        {
            switch (message.Msg)
            {
                case NativeMethods.WmStop:
                    _timer?.Stop();
                    _context?.ExitThread();
                    return;
                case NativeMethods.WmSample:
                case NativeMethods.WmTimeChange:
                    Sample(refreshSession: false);
                    return;
                case NativeMethods.WmWtsSessionChange:
                    HandleSessionChange((int)message.WParam, (int)message.LParam);
                    return;
                case NativeMethods.WmPowerBroadcast:
                    HandlePowerChange((int)message.WParam, message.LParam);
                    return;
            }
        }
        catch (Exception)
        {
            Volatile.Write(ref _lastError, "NativeMessageFailed");
            Publish(WindowsProbe.Failure("CollectorError"));
        }
    }

    private void HandleSessionChange(int kind, int sessionId)
    {
        if (_session.SessionId >= 0 && sessionId != _session.SessionId) return;
        var facts = SessionFacts.Read();
        _session = kind switch
        {
            1 => facts with { Connected = true, Remote = false },
            2 or 4 or 6 => facts with { Connected = false },
            3 => facts with { Connected = true, Remote = true },
            7 => facts with { Locked = true },
            8 => facts with { Locked = false },
            _ => facts
        };
        if (kind is 1 or 3 or 8) RegisterDisplayNotification();
        Sample(refreshSession: false);
    }

    private void HandlePowerChange(int kind, nint data)
    {
        switch (kind)
        {
            case NativeMethods.PbtApmSuspend:
                _suspended = true;
                Sample(refreshSession: false);
                break;
            case NativeMethods.PbtApmResumeAutomatic:
            case NativeMethods.PbtApmResumeCritical:
            case NativeMethods.PbtApmResumeSuspend:
                _suspended = false;
                RegisterDisplayNotification();
                Sample(refreshSession: true);
                break;
            case NativeMethods.PbtPowerSettingChange when data != 0:
                var setting = Marshal.PtrToStructure<Guid>(data);
                var dataLength = Marshal.ReadInt32(data, 16);
                if (setting == NativeMethods.SessionDisplayStatus && dataLength >= sizeof(int))
                {
                    var state = Marshal.ReadInt32(data, 20);
                    _displayState = state is 0 or 1 or 2 ? state : null;
                    Sample(refreshSession: false);
                }
                break;
        }
    }

    private sealed class SignalWindow(WindowsSignalSource owner) : Forms.NativeWindow
    {
        internal void Create() => CreateHandle(new Forms.CreateParams
        {
            Caption = "ATime signal sink",
            // A hidden top-level window receives power broadcasts; HWND_MESSAGE would not.
            Style = unchecked((int)0x80000000),
            ExStyle = 0x08000080,
            Width = 0,
            Height = 0
        });

        protected override void WndProc(ref Forms.Message message)
        {
            owner.HandleMessage(ref message);
            base.WndProc(ref message);
        }
    }
}
