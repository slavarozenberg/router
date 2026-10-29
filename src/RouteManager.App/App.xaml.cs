using System.Windows;
using RouteManager.App.Infrastructure;
using RouteManager.App.ViewModels;
using RouteManager.App.Views;
using RouteManager.Core.Net;
using RouteManager.Core.Steering;
using RouteManager.Core.Storage;
using Forms = System.Windows.Forms;

namespace RouteManager.App;

public partial class App : Application
{
    // Two instances would fight over the same routes and ledger.
    private Mutex? _singleInstance;
    private SteeringEngine? _engine;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Local\ (per session): a Global\ mutex created elevated could not be opened by an unelevated instance.
        _singleInstance = new Mutex(false, @"Local\RouteManager.SingleInstance");
        var wait = e.Args.Contains(Elevation.RestartArgument) ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;
        bool acquired;
        try
        {
            acquired = _singleInstance.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // previous instance crashed; we own it now
        }
        if (!acquired)
        {
            MessageBox.Show("Route Manager is already running. Look for it in the notification area.",
                "Route Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstance = null;
            Shutdown();
            return;
        }

        var paths = AppPaths.Default;
        _engine = new SteeringEngine(new WindowsRouteTable(), new WindowsInterfaceCatalog(), new LedgerStore(paths));
        _window = new MainWindow();
        _viewModel = new MainViewModel(new RuleStore(paths), _engine, new WindowDialogs(_window));
        _window.DataContext = _viewModel;
        _window.Closing += OnWindowClosing;

        CreateTrayIcon();
        _window.Show();
        _viewModel.Start();
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Route Manager", null, (_, _) => ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit (removes applied routes)", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            Text = "Route Manager",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing the window keeps the app (and route maintenance) running in the tray, like Rouler.
        e.Cancel = true;
        _window!.Hide();
        if (!_trayHintShown && _tray is not null)
        {
            _tray.ShowBalloonTip(3000, "Route Manager is still running",
                "Rules stay applied and follow network changes. Right-click the tray icon to exit.", Forms.ToolTipIcon.Info);
            _trayHintShown = true;
        }
    }

    private void ShowWindow()
    {
        _window!.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        try
        {
            _viewModel?.RemoveRoutesForExit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Some routes could not be removed: {ex.Message}", "Route Manager",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
