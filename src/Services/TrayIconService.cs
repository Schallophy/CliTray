using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CliTray.Core;
using CliTray.Ui;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ToolTipIcon = System.Windows.Forms.ToolTipIcon;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = Wpf.Ui.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace CliTray.Services
{
    internal sealed class TrayIconService : IDisposable
    {
        private readonly Window _owner;
        private readonly ConfigStore _store;
        private readonly ProcessManager _manager;
        private readonly Action _showManager;
        private readonly Action<string> _setTheme;
        private readonly Action _exit;
        private readonly Func<string> _getTheme;
        private readonly Action _syncSelfStart;

        private readonly NotifyIcon _icon;
        private readonly WpfContextMenu _menu;

        public TrayIconService(
            Window owner,
            ConfigStore store,
            ProcessManager manager,
            Action showManager,
            Action<string> setTheme,
            Action exit,
            Func<string> getTheme)
        {
            _owner = owner;
            _store = store;
            _manager = manager;
            _showManager = showManager;
            _setTheme = setTheme;
            _exit = exit;
            _getTheme = getTheme;
            _syncSelfStart = delegate
            {
                var main = _owner as MainWindow;
                if (main != null)
                    main.SyncSelfAutoStart();
            };

            _menu = new WpfContextMenu
            {
                Placement = PlacementMode.MousePoint,
                StaysOpen = false
            };

            _icon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "CliTray",
                Visible = true,
                ContextMenuStrip = null
            };
            _icon.DoubleClick += delegate { _showManager(); };
            _icon.MouseUp += OnIconMouseUp;
        }

        public void Balloon(string text, ToolTipIcon icon)
        {
            _icon.ShowBalloonTip(4000, "CliTray", text, icon);
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.MouseUp -= OnIconMouseUp;
            _icon.Dispose();
            _menu.IsOpen = false;
        }

        private void OnIconMouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Right)
                return;

            RebuildMenu();
            _menu.PlacementTarget = _owner;
            _menu.IsOpen = true;
        }

        private void RebuildMenu()
        {
            _menu.Items.Clear();
            _menu.Items.Add(Item("管理...", delegate { _showManager(); }));
            _menu.Items.Add(BuildItemsMenu());
            _menu.Items.Add(new WpfSeparator());
            _menu.Items.Add(Item("全部启动", StartAll));
            _menu.Items.Add(Item("全部停止", delegate { _manager.StopAll(); }));
            _menu.Items.Add(new WpfSeparator());
            _menu.Items.Add(BuildSelfStartItem());
            _menu.Items.Add(BuildStopOnExitItem());
            _menu.Items.Add(BuildThemeMenu());
            _menu.Items.Add(Item("关于", ShowAbout));
            _menu.Items.Add(new WpfSeparator());
            _menu.Items.Add(Item("退出", delegate { _exit(); }));
        }

        private WpfMenuItem BuildItemsMenu()
        {
            var root = new WpfMenuItem { Header = "托管程序" };

            if (_store.Config.Items.Count == 0)
            {
                root.Items.Add(new WpfMenuItem
                {
                    Header = "（暂无托管程序）",
                    IsEnabled = false
                });
                return root;
            }

            foreach (ManagedItem item in _store.Config.Items)
            {
                ManagedItem captured = item;
                bool running;
                int pid;
                DateTime startedAt;
                _manager.TryGetState(item.Id, out running, out pid, out startedAt);

                root.Items.Add(Item(
                    (running ? "[运行] " : "[停止] ") + item.Name,
                    delegate { ToggleItem(captured); }));
            }

            return root;
        }

        private WpfMenuItem BuildSelfStartItem()
        {
            bool enabled;
            try
            {
                enabled = AutoStart.IsEnabled();
            }
            catch
            {
                enabled = false;
            }

            var item = new WpfMenuItem
            {
                Header = "本程序开机自启",
                IsCheckable = true,
                IsChecked = enabled
            };
            item.Click += delegate
            {
                try
                {
                    AutoStart.SetEnabled(item.IsChecked);
                    _syncSelfStart();
                    Balloon(item.IsChecked ? "已设置开机自启" : "已取消开机自启",
                            ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Balloon("设置开机自启失败: " + ex.Message, ToolTipIcon.Error);
                    try
                    {
                        item.IsChecked = AutoStart.IsEnabled();
                    }
                    catch
                    {
                    }
                }
            };
            return item;
        }

        private WpfMenuItem BuildStopOnExitItem()
        {
            var item = new WpfMenuItem
            {
                Header = "退出时停止托管程序",
                IsCheckable = true,
                IsChecked = _store.Config.Settings.StopAllOnExit
            };
            item.Click += delegate
            {
                bool previous = _store.Config.Settings.StopAllOnExit;
                _store.Config.Settings.StopAllOnExit = item.IsChecked;
                try
                {
                    _store.Save();
                }
                catch (Exception ex)
                {
                    _store.Config.Settings.StopAllOnExit = previous;
                    item.IsChecked = previous;
                    Balloon("保存设置失败: " + ex.Message, ToolTipIcon.Error);
                }
            };
            return item;
        }

        private WpfMenuItem BuildThemeMenu()
        {
            var root = new WpfMenuItem { Header = "主题" };
            string current = _getTheme() ?? "system";

            root.Items.Add(ThemeItem("跟随系统", "system", current));
            root.Items.Add(ThemeItem("浅色", "light", current));
            root.Items.Add(ThemeItem("深色", "dark", current));
            return root;
        }

        private WpfMenuItem ThemeItem(string text, string mode, string current)
        {
            var item = new WpfMenuItem
            {
                Header = text,
                IsCheckable = true,
                IsChecked = string.Equals(mode, current, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += delegate { _setTheme(mode); };
            return item;
        }

        private void ToggleItem(ManagedItem item)
        {
            bool running;
            int pid;
            DateTime startedAt;
            _manager.TryGetState(item.Id, out running, out pid, out startedAt);

            string error;
            if (running)
                _manager.Stop(item, out error);
            else
                _manager.Start(item, out error);

            if (!string.IsNullOrEmpty(error) && error != "已经在运行")
                Balloon((running ? "停止失败: " : "启动失败: ") + error, ToolTipIcon.Error);
        }

        private void StartAll()
        {
            var errors = new System.Collections.Generic.List<string>();
            foreach (ManagedItem item in _store.Config.Items)
            {
                bool running;
                int pid;
                DateTime startedAt;
                if (_manager.TryGetState(item.Id, out running, out pid, out startedAt))
                    continue;

                string error;
                if (!_manager.Start(item, out error) && error != "正在启动")
                    errors.Add(item.Name + ": " + error);
            }

            if (errors.Count > 0)
                Balloon("部分程序启动失败: " + string.Join("；", errors), ToolTipIcon.Error);
        }

        private void ShowAbout()
        {
            string exitText = _store.Config.Settings.StopAllOnExit
                ? "退出托盘时会停止所有托管程序。"
                : "退出托盘时会保留托管程序运行，但不再受 CliTray 管理。";

            Dialogs.Info(
                _owner,
                "关于 CliTray",
                "CliTray 0.1\n\n" +
                "配置文件:\n" + AppPaths.ConfigFile + "\n\n" +
                exitText);
        }

        private static WpfMenuItem Item(string text, Action click)
        {
            var item = new WpfMenuItem { Header = text };
            item.Click += delegate { click(); };
            return item;
        }
    }
}
