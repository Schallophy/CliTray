using System;
using System.Threading;
using System.Windows;
using CliTray.Core;
using CliTray.Services;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;

namespace CliTray
{
    public partial class App : Application
    {
        private readonly bool _autoStart;

        private ConfigStore _store;
        private ProcessManager _manager;
        private MainWindow _main;
        private TrayIconService _tray;
        private EventWaitHandle _showEvent;
        private RegisteredWaitHandle _showWait;
        private bool _themeWatched;

        public App(bool autoStart)
        {
            _autoStart = autoStart;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _store = new ConfigStore();
            ConfigLoadResult load = _store.Load();
            _manager = new ProcessManager();
            AutoStart.RepairIfNeeded();

            _main = new MainWindow(_store, _manager);
            MainWindow = _main;
            _main.SetNotice(load.Warning);

            ApplyTheme(_store.Config.Settings.Theme);
            _tray = new TrayIconService(
                _main, _store, _manager,
                ShowManager,
                SetTheme,
                ExitFromTray,
                delegate { return _store.Config.Settings.Theme; });

            try
            {
                _showEvent = new EventWaitHandle(
                    false, EventResetMode.AutoReset, Program.ShowEventName);
            }
            catch
            {
                _showEvent = null;
            }

            if (_showEvent != null)
            {
                _showWait = ThreadPool.RegisterWaitForSingleObject(
                    _showEvent, OnShowSignal, null, Timeout.Infinite, false);
            }

            SystemEvents.SessionEnding += OnSessionEnding;

            if (_autoStart)
                _manager.StartAutoStartItems(_store.Config.Items);
            else
                _main.ShowManager();

            if (!string.IsNullOrEmpty(load.Warning))
                _tray.Balloon(load.Warning, System.Windows.Forms.ToolTipIcon.Warning);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 看门狗：退出流程若被卡住（如停止托管进程时阻塞），
            // 强制结束进程，避免留下无托盘无窗口的僵尸实例。
            System.Threading.Tasks.Task.Run(delegate
            {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                Environment.Exit(0);
            });

            if (_showWait != null)
            {
                try
                {
                    _showWait.Unregister(null);
                }
                catch
                {
                }
            }
            if (_showEvent != null)
            {
                try
                {
                    _showEvent.Close();
                }
                catch
                {
                }
            }

            SystemEvents.SessionEnding -= OnSessionEnding;

            try
            {
                if (_tray != null)
                    _tray.Dispose();
            }
            catch
            {
            }

            try
            {
                if (_manager != null)
                {
                    if (_store != null && _store.Config.Settings.StopAllOnExit)
                        _manager.Shutdown();
                    else
                        _manager.ReleaseAll();
                    _manager.Dispose();
                }
            }
            catch
            {
            }

            base.OnExit(e);
        }

        private void ApplyTheme(string mode)
        {
            bool system = string.IsNullOrEmpty(mode) ||
                          string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase);

            if (system)
            {
                ApplicationThemeManager.ApplySystemTheme(false);
                if (!_themeWatched)
                {
                    SystemThemeWatcher.Watch(_main, WindowBackdropType.Mica, false);
                    _themeWatched = true;
                }
                return;
            }

            if (_themeWatched)
            {
                SystemThemeWatcher.UnWatch(_main);
                _themeWatched = false;
            }

            ApplicationTheme theme = string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
        }

        private void SetTheme(string mode)
        {
            _store.Config.Settings.Theme = mode;
            try
            {
                _store.Save();
            }
            catch (Exception ex)
            {
                _tray.Balloon("保存主题设置失败: " + ex.Message, System.Windows.Forms.ToolTipIcon.Error);
            }

            ApplyTheme(mode);
        }

        private void ShowManager()
        {
            if (_main != null)
                _main.ShowManager();
        }

        private void OnShowSignal(object state, bool timedOut)
        {
            if (timedOut)
                return;
            Application app = Current;
            if (app == null)
                return;
            app.Dispatcher.BeginInvoke(new Action(ShowManager));
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            try
            {
                _manager.Shutdown();
            }
            catch
            {
            }
        }

        private void ExitFromTray()
        {
            _main.CloseForExit();
            Shutdown();
        }
    }
}
