using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CliTray.Core;
using CliTray.Services;

namespace CliTray
{
    internal static class Program
    {
        internal const string ShowEventName = @"Local\CliTray.Show";
        private const string MutexName = @"Local\CliTray.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            EmbeddedAssemblies.Install();

            bool autoStart = false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase))
                    autoStart = true;
            }

            for (; ; )
            {
                bool createdNew;
                Mutex mutex = new Mutex(true, MutexName, out createdNew);
                if (createdNew)
                {
                    RunApp(mutex, autoStart);
                    return;
                }

                if (!autoStart && SignalExisting())
                {
                    mutex.Dispose();
                    return;
                }

                if (PeerIsAlive())
                {
                    mutex.Dispose();
                    return;
                }

                // 互斥体被占但事件不存在：上一个实例退出时卡成了僵尸进程
                if (KillZombieInstances())
                {
                    mutex.Dispose();
                    continue;
                }

                // 互斥体已被弃置（原持有者已死）：接管运行
                RunApp(mutex, autoStart);
                return;
            }
        }

        private static void RunApp(Mutex mutex, bool autoStart)
        {
            using (mutex)
            {
                var app = new App(autoStart);
                app.InitializeComponent();
                app.DispatcherUnhandledException += OnDispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                Environment.ExitCode = app.Run();
                GC.KeepAlive(mutex);
            }
        }

        private static bool SignalExisting()
        {
            try
            {
                using (EventWaitHandle wait = EventWaitHandle.OpenExisting(ShowEventName))
                    wait.Set();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool PeerIsAlive()
        {
            try
            {
                using (EventWaitHandle.OpenExisting(ShowEventName))
                    return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool KillZombieInstances()
        {
            bool killed = false;
            int self = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName("CliTray"))
            {
                using (process)
                {
                    if (process.Id == self)
                        continue;
                    // 刚启动的健康实例可能还没创建事件，不要误杀
                    try
                    {
                        if ((DateTime.Now - process.StartTime).TotalSeconds < 5)
                            continue;
                    }
                    catch
                    {
                        continue;
                    }
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                        killed = true;
                    }
                    catch
                    {
                    }
                }
            }
            return killed;
        }

        private static void OnDispatcherUnhandledException(
            object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Report(e.Exception, false);
            e.Handled = true;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Report(e.ExceptionObject as Exception, true);
        }

        private static void Report(Exception ex, bool fatal)
        {
            if (ex == null)
                return;
            try
            {
                Directory.CreateDirectory(AppPaths.StateDir);
                File.AppendAllText(
                    AppPaths.CrashLog,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                    (fatal ? " [fatal] " : " [ui] ") +
                    ex + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
