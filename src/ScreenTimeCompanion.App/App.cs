using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using ScreenTimeCompanion.App.Services;
using ScreenTimeCompanion.App.Views;
using Forms = System.Windows.Forms;

namespace ScreenTimeCompanion.App;

public sealed class App : System.Windows.Application
{
    private TrackerService? _tracker;
    private WidgetWindow? _widget;
    private DashboardWindow? _dashboard;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _pauseItem;
    private FileStream? _instanceLock;
    private string _pipeName = "";
    private bool _exiting;
    private readonly CancellationTokenSource _pipeCancellation = new();

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke-test")) return Diagnostics.RunAsync(args).GetAwaiter().GetResult();
        if (args.Contains("--controller-test")) return Diagnostics.ControllerTestAsync(args).GetAwaiter().GetResult();
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Run();
        return 0;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirectory = Argument(e.Args,"--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenTimeCompanion");
        try
        {
            dataDirectory = Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(dataDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..20];
            _pipeName = "ScreenTimeCompanion-"+key+"-"+Process.GetCurrentProcess().SessionId;
            try { _instanceLock = new FileStream(Path.Combine(dataDirectory,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch (IOException)
            {
                try {
                    using var client = new NamedPipeClientStream(".",_pipeName,PipeDirection.Out);
                    await client.ConnectAsync(1000);
                    await client.WriteAsync(Encoding.UTF8.GetBytes("show"));
                } catch { MessageBox.Show("屏幕时光已在此用户的另一个会话中运行。","屏幕时光"); }
                Shutdown(); return;
            }
            _tracker = await Task.Run(() => new TrackerService(dataDirectory,Dispatcher));
            _widget = new WidgetWindow(_tracker);
            _dashboard = new DashboardWindow(_tracker);
            if (e.Args.Contains("--ui-test")) _widget.ShowInTaskbar = true;
            MainWindow = _widget;
            _tracker.WidgetRequested += ShowWidget;
            _tracker.DashboardRequested += ShowDashboard;
            _tracker.QuitRequested += ExitApplication;
            CreateTray();
            _tracker.Changed += UpdateTray;
            _tracker.Start();
            if (!e.Args.Contains("--background")) ShowWidget();
            if (e.Args.Contains("--dashboard")) ShowDashboard();
            _ = ListenForActivationAsync();
            if (Argument(e.Args,"--test-exit-after") is string seconds && int.TryParse(seconds,out var duration))
                _ = AutoExitAsync(Math.Clamp(duration,5,3600));
        }
        catch (Exception error)
        {
            var message = "启动失败："+error.Message+"\n\n数据位置："+dataDirectory;
            try { File.WriteAllText(Path.Combine(dataDirectory,"startup-error.txt"),error.ToString()); } catch { }
            MessageBox.Show(message,"屏幕时光",MessageBoxButton.OK,MessageBoxImage.Error);
            _instanceLock?.Dispose(); Shutdown(1);
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示小窗",null,(_,_) => ShowWidget());
        menu.Items.Add("查看统计",null,(_,_) => ShowDashboard());
        _pauseItem = new Forms.ToolStripMenuItem("暂停统计",null,async (_,_) => {
            if (_tracker is null) return;
            try { await _tracker.SetPausedAsync(!_tracker.State.Settings.Paused); }
            catch (Exception error) { MessageBox.Show("无法更改记录状态："+error.Message,"屏幕时光"); }
        });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出屏幕时光",null,(_,_) => ExitApplication());
        _tray = new Forms.NotifyIcon { Icon = SystemIcons.Application, Visible = true, Text = "屏幕时光 · 正在启动", ContextMenuStrip = menu };
        _tray.MouseClick += (_,args) => { if (args.Button == Forms.MouseButtons.Left) ShowWidget(); };
    }

    private void UpdateTray()
    {
        if (_tray is null || _tracker is null) return;
        var state = _tracker.State;
        var today = state.Today.ToString("yyyy-MM-dd");
        var elapsed = TimeSpan.FromMilliseconds(state.Rows.Where(r=>r.LocalDate==today).Sum(r=>r.EffectiveMs));
        var text = $"屏幕时光 | 今日 {(int)elapsed.TotalHours}时{elapsed.Minutes}分 | {state.Status}";
        _tray.Text = text.Length>63 ? text[..63] : text;
        if (_pauseItem is not null) _pauseItem.Text = state.Settings.Paused ? "恢复统计" : "暂停统计";
    }

    private void ShowWidget()
    {
        if (_widget is null) return;
        _widget.ShowSafely(); // ShowActivated=false keeps the foreground application untouched.
    }

    private void ShowDashboard()
    {
        if (_dashboard is null) return;
        _dashboard.Show();
        if (_dashboard.WindowState == WindowState.Minimized) _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }

    private async Task ListenForActivationAsync()
    {
        while (!_pipeCancellation.IsCancellationRequested)
        {
            try {
                using var server = new NamedPipeServerStream(_pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_pipeCancellation.Token);
                var input = new byte[16];
                var count = await server.ReadAsync(input,_pipeCancellation.Token);
                if (Encoding.UTF8.GetString(input,0,count)=="show") _ = Dispatcher.BeginInvoke(ShowWidget);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { if (!_pipeCancellation.IsCancellationRequested) await Task.Delay(500); }
        }
    }

    private async Task AutoExitAsync(int seconds) { await Task.Delay(TimeSpan.FromSeconds(seconds)); ExitApplication(); }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _pipeCancellation.Cancel();
        try { if (_tracker is not null) await _tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) { MessageBox.Show("部分记录未能保存："+error.Message,"屏幕时光"); }
        finally {
            _tray?.Dispose(); _widget?.CloseForExit(); _dashboard?.CloseForExit();
            _instanceLock?.Dispose(); Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (!_exiting)
        {
            _exiting = true; _pipeCancellation.Cancel();
            try { _tracker?.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { /* OS shutdown has a finite budget. */ }
            _tray?.Dispose();
        }
        base.OnSessionEnding(e);
    }
    internal static string? Argument(string[] args,string name) { var index = Array.IndexOf(args,name); return index>=0 && index+1<args.Length ? args[index+1] : null; }
}
