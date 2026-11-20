using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CliTray.Core;
using Microsoft.Win32;
using FolderBrowser = System.Windows.Forms.FolderBrowserDialog;

namespace CliTray
{
    public partial class ItemEditWindow : Wpf.Ui.Controls.FluentWindow
    {
        private readonly bool _isNew;

        public ItemEditWindow(ManagedItem item)
        {
            _isNew = item == null;
            Item = item ?? new ManagedItem();
            if (string.IsNullOrEmpty(Item.Id))
                Item.Id = Guid.NewGuid().ToString("N");
            if (Item.StopTimeout < 1)
                Item.StopTimeout = 3;

            InitializeComponent();

            string title = _isNew ? "添加托管程序" : "编辑托管程序";
            Title = title;
            WindowTitleBar.Title = title;

            Populate();
        }

        public ManagedItem Item { get; private set; }

        private void Populate()
        {
            NameBox.Text = Item.Name ?? "";
            FileBox.Text = Item.File ?? "";
            ArgsBox.Text = Item.Args ?? "";
            WorkDirBox.Text = Item.WorkDir ?? "";
            LogFileBox.Text = Item.LogFile ?? "";
            AutoStartBox.IsChecked = Item.AutoStart;
            ShowConsoleBox.IsChecked = Item.ShowConsole;
            DelayBox.Value = Clamp(Item.DelaySeconds, 0, 3600);
            StopTimeoutBox.Value = Clamp(Item.StopTimeout, 1, 60);
        }

        private static double Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private void BrowseFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择程序",
                CheckFileExists = true,
                Filter = "可执行文件|*.exe;*.bat;*.cmd;*.com|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
                return;

            FileBox.Text = dialog.FileName;
            if (NameBox.Text.Trim().Length == 0)
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            if (WorkDirBox.Text.Trim().Length == 0)
                WorkDirBox.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
        }

        private void BrowseWorkDir(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowser
            {
                Description = "选择工作目录",
                ShowNewFolderButton = false
            })
            {
                string current = WorkDirBox.Text.Trim();
                if (current.Length > 0 && Directory.Exists(current))
                    dialog.SelectedPath = current;

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                WorkDirBox.Text = dialog.SelectedPath;
            }
        }

        private void BrowseLogFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择日志文件",
                CheckFileExists = false,
                Filter = "日志文件|*.log;*.txt;*.out|所有文件|*.*"
            };
            string current = LogFileBox.Text.Trim();
            if (current.Length > 0)
            {
                try
                {
                    dialog.FileName = Path.GetFullPath(
                        Environment.ExpandEnvironmentVariables(current));
                }
                catch
                {
                }
            }
            if (dialog.ShowDialog(this) == true)
                LogFileBox.Text = dialog.FileName;
        }

        private void OkClicked(object sender, RoutedEventArgs e)
        {
            if (FileBox.Text.Trim().Length == 0)
            {
                Fail("请选择要托管的程序。", FileBox);
                return;
            }

            string file = CommandResolver.Resolve(FileBox.Text.Trim());
            if (file == null)
            {
                Fail("未找到程序:\n" + FileBox.Text.Trim(), FileBox);
                return;
            }

            string workDir = WorkDirBox.Text.Trim();
            if (workDir.Length > 0)
            {
                try
                {
                    workDir = Path.GetFullPath(workDir);
                }
                catch (Exception ex)
                {
                    Fail("工作目录无效: " + ex.Message, WorkDirBox);
                    return;
                }
                if (!Directory.Exists(workDir))
                {
                    Fail("工作目录不存在:\n" + workDir, WorkDirBox);
                    return;
                }
            }

            string name = NameBox.Text.Trim();
            if (name.Length == 0)
                name = Path.GetFileNameWithoutExtension(file);

            Item.Name = name;
            Item.File = file;
            Item.Args = ArgsBox.Text.Trim();
            Item.WorkDir = workDir;
            string logFile = LogFileBox.Text.Trim();
            Item.LogFile = logFile.Length > 0 ? logFile : null;
            Item.AutoStart = AutoStartBox.IsChecked == true;
            Item.ShowConsole = ShowConsoleBox.IsChecked == true;
            Item.DelaySeconds = (int)(DelayBox.Value ?? 0);
            Item.StopTimeout = (int)(StopTimeoutBox.Value ?? 3);

            DialogResult = true;
        }

        private void Fail(string message, Control focus)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            focus.Focus();
        }
    }
}
