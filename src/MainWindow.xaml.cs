using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CliTray.Core;
using CliTray.Ui;

namespace CliTray
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly ConfigStore _store;
        private readonly ProcessManager _manager;
        private readonly ObservableCollection<ItemRow> _rows =
            new ObservableCollection<ItemRow>();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _outputTimer;
        private readonly object _outputSync = new object();
        private readonly StringBuilder _pendingOutput = new StringBuilder();

        private bool _syncing;
        private bool _updatingSelf;
        private bool _allowClose;
        private bool _selfStart;
        private int _adoptTick;
        private string _notice = "";
        private string _selectedId;

        internal MainWindow(ConfigStore store, ProcessManager manager)
        {
            _store = store;
            _manager = manager;

            InitializeComponent();

            Icon = Imaging.CreateBitmapSourceFromHIcon(
                System.Drawing.SystemIcons.Application.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            ItemGrid.ItemsSource = _rows;
            _manager.StateChanged += ManagerStateChanged;
            _manager.OutputReceived += ManagerOutputReceived;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += delegate
            {
                RefreshRuntime();
                if (++_adoptTick >= 10)
                {
                    _adoptTick = 0;
                    _manager.BeginAdoptExternal(_store.Config.Items.ToList());
                }
            };

            _outputTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _outputTimer.Tick += delegate { FlushOutput(); };

            RefreshAll();
        }

        public void SetNotice(string notice)
        {
            _notice = notice ?? "";
            if (IsVisible)
                RefreshRuntime();
        }

        public void ShowManager()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();
            Topmost = true;
            Topmost = false;

            RefreshSelfAutoStart();
            RefreshAll();
            _timer.Start();
            _outputTimer.Start();
        }

        public void SyncSelfAutoStart()
        {
            RefreshSelfAutoStart();
            RefreshRuntime();
        }

        public void CloseForExit()
        {
            _allowClose = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
                _timer.Stop();
                _outputTimer.Stop();
                return;
            }

            _manager.StateChanged -= ManagerStateChanged;
            _manager.OutputReceived -= ManagerOutputReceived;
            _timer.Stop();
            _outputTimer.Stop();
            base.OnClosing(e);
        }

        private ItemRow SelectedRow
        {
            get { return ItemGrid.SelectedItem as ItemRow; }
        }

        private void RefreshAll()
        {
            ItemRow selected = SelectedRow;
            string selectedId = selected == null ? null : selected.Id;

            _syncing = true;
            try
            {
                foreach (ItemRow row in _rows)
                    row.PropertyChanged -= RowPropertyChanged;
                _rows.Clear();

                foreach (ManagedItem item in _store.Config.Items)
                {
                    var row = new ItemRow(item);
                    row.PropertyChanged += RowPropertyChanged;
                    _rows.Add(row);
                }

                if (!string.IsNullOrEmpty(selectedId))
                {
                    ItemRow restore = _rows.FirstOrDefault(r => r.Id == selectedId);
                    if (restore != null)
                    {
                        ItemGrid.SelectedItem = restore;
                        ItemGrid.ScrollIntoView(restore);
                    }
                }
            }
            finally
            {
                _syncing = false;
            }

            RefreshRuntime();
            ShowSelectedOutput();
            _manager.BeginAdoptExternal(_store.Config.Items.ToList());
        }

        private void RefreshRuntime()
        {
            int runningCount = 0;

            foreach (ItemRow row in _rows)
            {
                bool running;
                int pid;
                DateTime startedAt;
                _manager.TryGetState(row.Id, out running, out pid, out startedAt);
                row.SetRuntime(running, pid, startedAt);
                if (running)
                    runningCount++;
            }

            StatusText.Text = "运行 " + runningCount + "/" + _store.Config.Items.Count +
                              StatusSuffix();
            UpdateButtons();
        }

        private string StatusSuffix()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_notice))
                parts.Add(_notice);
            if (!_selfStart && _store.Config.Items.Any(i => i.AutoStart))
                parts.Add("本程序未自启，条目自启不会生效");
            return parts.Count == 0 ? "" : "    " + string.Join("；", parts);
        }

        private void UpdateButtons()
        {
            ItemRow row = SelectedRow;
            bool has = row != null;
            bool running = row != null && row.IsRunning;

            BtnEdit.IsEnabled = has;
            BtnDelete.IsEnabled = has;
            BtnStart.IsEnabled = has && !running;
            BtnStop.IsEnabled = has && running;
            BtnRestart.IsEnabled = has && running;
        }

        private void RefreshSelfAutoStart()
        {
            bool enabled;
            try
            {
                enabled = AutoStart.IsEnabled();
            }
            catch
            {
                enabled = _selfStart;
            }

            _selfStart = enabled;
            _updatingSelf = true;
            try
            {
                SelfAutoStart.IsChecked = enabled;
            }
            finally
            {
                _updatingSelf = false;
            }
        }

        private void SelfAutoStartClicked(object sender, RoutedEventArgs e)
        {
            if (_updatingSelf)
                return;

            bool desired = SelfAutoStart.IsChecked == true;
            try
            {
                AutoStart.SetEnabled(desired);
                _selfStart = desired;
                _notice = "";
            }
            catch (Exception ex)
            {
                _updatingSelf = true;
                try
                {
                    SelfAutoStart.IsChecked = !desired;
                }
                finally
                {
                    _updatingSelf = false;
                }
                Dialogs.Info(this, "CliTray", "设置开机自启失败: " + ex.Message);
            }

            RefreshRuntime();
        }

        private void RowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_syncing || _updatingSelf || e.PropertyName != "AutoStart")
                return;

            var row = sender as ItemRow;
            if (row == null || row.AutoStart == row.Item.AutoStart)
                return;

            bool value = row.AutoStart;
            row.Item.AutoStart = value;
            try
            {
                _store.Save();
            }
            catch (Exception ex)
            {
                row.Item.AutoStart = !value;
                _syncing = true;
                try
                {
                    row.SyncAutoStart(!value);
                }
                finally
                {
                    _syncing = false;
                }
                Dialogs.Info(this, "CliTray", "保存配置失败: " + ex.Message);
            }

            RefreshRuntime();
        }

        private void GridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateButtons();
            ShowSelectedOutput();
        }

        private void ShowSelectedOutput()
        {
            ItemRow row = SelectedRow;
            _selectedId = row == null ? null : row.Id;
            lock (_outputSync)
                _pendingOutput.Clear();

            OutputTitle.Text = row == null ? "输出" : "输出 — " + row.Name;
            if (row == null)
            {
                OutputBox.Text = "";
                return;
            }

            OutputBox.Text = string.Join(Environment.NewLine, _manager.GetOutput(row.Id));
            OutputBox.ScrollToEnd();
        }

        private void ManagerOutputReceived(string id, string line)
        {
            if (id != _selectedId)
                return;
            lock (_outputSync)
            {
                if (_pendingOutput.Length > 120000)
                    _pendingOutput.Clear();
                _pendingOutput.Append(line).Append('\n');
            }
        }

        private void FlushOutput()
        {
            string chunk;
            lock (_outputSync)
            {
                if (_pendingOutput.Length == 0)
                    return;
                chunk = _pendingOutput.ToString();
                _pendingOutput.Clear();
            }

            if (OutputBox.LineCount > 520)
            {
                ShowSelectedOutput();
                return;
            }

            if (OutputBox.Text.Length > 0)
                OutputBox.AppendText(Environment.NewLine);
            OutputBox.AppendText(chunk.TrimEnd('\n'));
            OutputBox.ScrollToEnd();
        }

        private void ClearOutput(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;
            _manager.ClearOutput(row.Id);
            ShowSelectedOutput();
        }

        private void GridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            DataGridRow row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
                return;
            row.IsSelected = true;
            ItemGrid.SelectedItem = row.Item;
        }

        private void GridContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;

            DataGridRow row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null || SelectedRow == null)
                return;

            row.IsSelected = true;
            ItemGrid.SelectedItem = row.Item;

            var menu = ItemGrid.Resources["RowMenu"] as ContextMenu;
            if (menu == null)
                return;

            menu.PlacementTarget = ItemGrid;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void GridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null)
                return;
            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) != null)
                return;
            if (SelectedRow == null)
                return;

            EditItem(sender, e);
        }

        private void AddItem(object sender, RoutedEventArgs e)
        {
            var dialog = new ItemEditWindow(null) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            _store.Config.Items.Add(dialog.Item);
            if (!SaveConfig())
            {
                _store.Config.Items.Remove(dialog.Item);
                return;
            }
            RefreshAll();
        }

        private void EditItem(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;

            var dialog = new ItemEditWindow(row.Item.Clone()) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            int index = _store.Config.Items.IndexOf(row.Item);
            if (index < 0)
                return;

            ManagedItem original = _store.Config.Items[index];
            _store.Config.Items[index] = dialog.Item;
            if (!SaveConfig())
            {
                _store.Config.Items[index] = original;
                return;
            }
            RefreshAll();
        }

        private void DeleteItem(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;

            ManagedItem item = row.Item;
            bool running;
            int pid;
            DateTime startedAt;
            if (_manager.TryGetState(item.Id, out running, out pid, out startedAt))
            {
                if (!Dialogs.Confirm(this, "CliTray", "该程序正在运行，删除前将先停止它。继续？"))
                    return;
                string stopError;
                _manager.Stop(item, out stopError);
            }
            else if (!Dialogs.Confirm(this, "CliTray", "删除 " + item.Name + "？"))
            {
                return;
            }

            int index = _store.Config.Items.IndexOf(item);
            if (index < 0)
                return;
            _store.Config.Items.RemoveAt(index);
            if (!SaveConfig())
            {
                _store.Config.Items.Insert(index, item);
                return;
            }
            _manager.ClearOutput(item.Id);
            RefreshAll();
        }

        private void StartSelected(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;

            string error;
            if (!_manager.Start(row.Item, out error) && error != "已经在运行")
                Dialogs.Info(this, "启动失败", error);
            RefreshRuntime();
        }

        private void StopSelected(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;

            string error;
            if (!_manager.Stop(row.Item, out error))
                Dialogs.Info(this, "停止失败", error);
            RefreshRuntime();
        }

        private async void RestartSelected(object sender, RoutedEventArgs e)
        {
            ItemRow row = SelectedRow;
            if (row == null)
                return;

            ManagedItem item = row.Item;
            string error;
            _manager.Stop(item, out error);
            await Task.Delay(600);
            if (_manager.IsShuttingDown)
                return;

            if (!_manager.Start(item, out error))
                Dialogs.Info(this, "重启失败", error);
            RefreshRuntime();
        }

        private void StartAll(object sender, RoutedEventArgs e)
        {
            var errors = new List<string>();
            foreach (ManagedItem item in _store.Config.Items.ToList())
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
                Dialogs.Info(this, "CliTray", "部分程序启动失败:\n\n" + string.Join("\n", errors));
            RefreshRuntime();
        }

        private void StopAll(object sender, RoutedEventArgs e)
        {
            _manager.StopAll();
            RefreshRuntime();
        }

        private void OpenConfigFolder(object sender, RoutedEventArgs e)
        {
            OpenFolder(AppPaths.ConfigDir);
        }

        private void OpenFolder(string path)
        {
            if (!Directory.Exists(path))
            {
                Dialogs.Info(this, "CliTray", "目录不存在: " + path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialogs.Info(this, "CliTray", "打开目录失败: " + ex.Message);
            }
        }

        private bool SaveConfig()
        {
            try
            {
                _store.Save();
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Info(this, "CliTray", "保存配置失败: " + ex.Message);
                return false;
            }
        }

        private void ManagerStateChanged(object sender, EventArgs e)
        {
            if (!IsLoaded)
                return;
            Dispatcher.BeginInvoke(new Action(RefreshRuntime));
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                var typed = current as T;
                if (typed != null)
                    return typed;

                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
