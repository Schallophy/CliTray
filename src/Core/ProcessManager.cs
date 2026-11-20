using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CliTray.Core
{
    internal sealed class ProcessManager : IDisposable
    {
        private const int MaxOutputLines = 500;
        private const int MaxOutputLineChars = 2000;
        private const int MaxTailBytes = 256 * 1024;

        private sealed class Entry
        {
            public ManagedItem Item;
            public Process Process;
            public JobObject Job;
            public ManualResetEventSlim Exited;
            public int Pid;
            public DateTime StartedAt;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly HashSet<string> _starting = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _outputGate = new object();
        private readonly Dictionary<string, Queue<string>> _outputs =
            new Dictionary<string, Queue<string>>(StringComparer.Ordinal);

        private bool _shuttingDown;
        private bool _disposed;

        public event EventHandler StateChanged;

        public event Action<string, string> OutputReceived;

        public bool IsShuttingDown
        {
            get
            {
                lock (_gate)
                    return _shuttingDown;
            }
        }

        public void StartAutoStartItems(IEnumerable<ManagedItem> items)
        {
            List<ManagedItem> planned = items
                .Where(i => i.AutoStart)
                .Select(i => i.Clone())
                .ToList();
            if (planned.Count == 0)
                return;

            Task.Run(async () =>
            {
                await Task.Delay(2000).ConfigureAwait(false);
                foreach (ManagedItem item in planned)
                {
                    if (IsShuttingDown)
                        return;
                    if (item.DelaySeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(item.DelaySeconds)).ConfigureAwait(false);
                    if (IsShuttingDown)
                        return;

                    string error;
                    Start(item, out error);
                }
            });
        }

        public bool Start(ManagedItem item, out string error)
        {
            error = null;
            if (item == null)
            {
                error = "条目为空";
                return false;
            }

            lock (_gate)
            {
                if (_shuttingDown)
                {
                    error = "程序正在退出";
                    return false;
                }
                if (_starting.Contains(item.Id))
                {
                    error = "正在启动";
                    return false;
                }
                Entry current;
                if (_entries.TryGetValue(item.Id, out current) && IsRunning(current))
                {
                    error = "已经在运行";
                    return false;
                }
                _starting.Add(item.Id);
            }

            Process process = null;
            JobObject job = null;
            bool entryAdded = false;

            try
            {
                string file = CommandResolver.Resolve(item.File);
                if (file == null)
                {
                    if (string.IsNullOrWhiteSpace(item.File))
                        throw new InvalidOperationException("未配置程序路径");
                    throw new FileNotFoundException("未找到程序: " + item.File);
                }

                Process external = FindExternalInstance(file);
                if (external != null)
                {
                    if (AdoptProcess(item, external, file))
                        return true;
                    lock (_gate)
                        _starting.Remove(item.Id);
                    error = "已经在运行";
                    RaiseStateChanged();
                    return false;
                }

                ProcessStartInfo startInfo = BuildStartInfo(item, file);

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        AppendOutput(item.Id, e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                        AppendOutput(item.Id, e.Data);
                };

                if (!process.Start())
                    throw new InvalidOperationException("Process.Start 返回 false");
                if (startInfo.RedirectStandardOutput)
                    process.BeginOutputReadLine();
                if (startInfo.RedirectStandardError)
                    process.BeginErrorReadLine();

                if (process.HasExited)
                    throw new InvalidOperationException("程序启动后立即退出，退出码 " + SafeExitCode(process));

                try
                {
                    job = new JobObject();
                    job.Assign(process);
                }
                catch (Exception)
                {
                    if (job != null)
                    {
                        job.Dispose();
                        job = null;
                    }
                }

                int pid = SafePid(process);
                if (!item.ShowConsole)
                {
                    int childPid = ResolveChildPid(pid);
                    if (childPid != 0)
                        pid = childPid;
                }

                var entry = new Entry
                {
                    Item = item,
                    Process = process,
                    Job = job,
                    Exited = new ManualResetEventSlim(false),
                    Pid = pid,
                    StartedAt = DateTime.Now
                };

                lock (_gate)
                {
                    _starting.Remove(item.Id);
                    _entries[item.Id] = entry;
                }
                entryAdded = true;

                process.Exited += (s, e) => HandleExited(item.Id);

                if (process.HasExited)
                {
                    HandleExited(item.Id);
                    error = "程序启动后立即退出";
                    RaiseStateChanged();
                    return false;
                }

                RaiseStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                if (!entryAdded)
                {
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            if (job != null)
                                job.Terminate();
                            else
                                process.Kill();
                        }
                    }
                    catch
                    {
                    }
                    if (job != null)
                    {
                        try { job.Dispose(); } catch { }
                    }
                    if (process != null)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }

                lock (_gate)
                    _starting.Remove(item.Id);
                RaiseStateChanged();
                return false;
            }
        }

        public bool Stop(ManagedItem item, out string error)
        {
            error = null;
            if (item == null)
                return true;

            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(item.Id, out entry))
                    return true;
            }

            // 仅对自己启动并纳入 Job 的进程尝试优雅关闭；
            // 接管的外部进程（无 Job）可能挂着无响应窗口，CloseMainWindow
            // 是无超时的 SendMessage，会卡住调用方，直接整树终止。
            bool closeSent = false;
            if (entry.Job != null)
            {
                try
                {
                    closeSent = entry.Process.CloseMainWindow();
                }
                catch
                {
                }
            }

            if (closeSent && entry.Exited.Wait(TimeSpan.FromSeconds(Math.Max(1, item.StopTimeout))))
                return true;

            try
            {
                if (entry.Job != null)
                    entry.Job.Terminate();
                else
                    KillTree(SafePid(entry.Process));
            }
            catch (Exception ex)
            {
                if (!entry.Exited.IsSet)
                    error = ex.Message;
            }

            if (!entry.Exited.Wait(TimeSpan.FromSeconds(3)))
            {
                try { entry.Process.Kill(); } catch { }
                if (!entry.Exited.Wait(TimeSpan.FromSeconds(1)))
                {
                    if (string.IsNullOrEmpty(error))
                        error = "进程未能停止";
                    return false;
                }
            }

            RaiseStateChanged();
            if (entry.Exited.IsSet)
                return true;
            if (string.IsNullOrEmpty(error))
                error = "进程未能停止";
            return false;
        }

        public void StopAll()
        {
            List<ManagedItem> items;
            lock (_gate)
                items = _entries.Values.Select(e => e.Item).ToList();

            foreach (ManagedItem item in items)
            {
                string error;
                Stop(item, out error);
            }
        }

        public bool TryGetState(string id, out bool running, out int pid, out DateTime startedAt)
        {
            lock (_gate)
            {
                Entry entry;
                if (_entries.TryGetValue(id, out entry) && IsRunning(entry))
                {
                    running = true;
                    pid = entry.Pid;
                    startedAt = entry.StartedAt;
                    return true;
                }
            }

            running = false;
            pid = 0;
            startedAt = DateTime.MinValue;
            return false;
        }

        public void Shutdown()
        {
            lock (_gate)
                _shuttingDown = true;

            StopAll();

            List<Entry> leftover;
            lock (_gate)
            {
                leftover = _entries.Values.ToList();
                _entries.Clear();
            }

            foreach (Entry entry in leftover)
            {
                try
                {
                    if (entry.Job != null)
                        entry.Job.Terminate();
                }
                catch
                {
                }
                try { entry.Exited.Wait(1000); } catch { }
                try
                {
                    if (entry.Job != null)
                        entry.Job.Dispose();
                }
                catch
                {
                }
                try { entry.Process.Dispose(); } catch { }
            }

            RaiseStateChanged();
        }

        public void ReleaseAll()
        {
            List<Entry> entries;
            lock (_gate)
            {
                _shuttingDown = true;
                entries = _entries.Values.ToList();
                _entries.Clear();
            }

            foreach (Entry entry in entries)
            {
                try
                {
                    if (entry.Job != null)
                        entry.Job.Detach();
                }
                catch
                {
                }
                try { entry.Process.Dispose(); } catch { }
            }

            RaiseStateChanged();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Shutdown();
        }

        private void HandleExited(string id)
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(id, out entry))
                    return;
                _entries.Remove(id);
            }

            try { entry.Process.WaitForExit(2000); } catch { }

            try
            {
                if (entry.Job != null)
                    entry.Job.Dispose();
            }
            catch
            {
            }
            try { entry.Process.Dispose(); } catch { }
            try { entry.Exited.Set(); } catch { }

            RaiseStateChanged();
        }

        private static bool IsRunning(Entry entry)
        {
            try
            {
                return entry.Process != null && !entry.Process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static int SafePid(Process process)
        {
            try
            {
                return process.Id;
            }
            catch
            {
                return 0;
            }
        }

        private static int SafeExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private const uint Th32csSnapProcess = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private static int ResolveChildPid(int parentPid)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                int pid = FindChildPid(parentPid);
                if (pid != 0)
                    return pid;
                Thread.Sleep(25);
            }
            return 0;
        }

        private static int FindChildPid(int parentPid)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == new IntPtr(-1))
                return 0;
            try
            {
                var entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32First(snapshot, ref entry))
                    return 0;
                do
                {
                    if (entry.th32ParentProcessID == (uint)parentPid &&
                        !IsConsoleHelper(entry.szExeFile))
                        return (int)entry.th32ProcessID;
                } while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return 0;
        }

        private static bool IsConsoleHelper(string name)
        {
            return string.Equals(name, "conhost.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "openconsole.exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "chcp.com", StringComparison.OrdinalIgnoreCase);
        }

        public void BeginAdoptExternal(IEnumerable<ManagedItem> items)
        {
            if (items == null)
                return;

            List<ManagedItem> candidates = new List<ManagedItem>();
            lock (_gate)
            {
                if (_shuttingDown)
                    return;
                foreach (ManagedItem item in items)
                {
                    if (item == null || string.IsNullOrEmpty(item.Id))
                        continue;
                    if (_entries.ContainsKey(item.Id) || _starting.Contains(item.Id))
                        continue;
                    candidates.Add(item);
                }
            }
            if (candidates.Count == 0)
                return;

            Task.Run(() => AdoptExternalItems(candidates));
        }

        private void AdoptExternalItems(List<ManagedItem> candidates)
        {
            if (IsShuttingDown)
                return;

            HashSet<int> owned = CollectOwnedPids();
            List<ProcSnapshot> snapshot = SnapshotProcesses();

            foreach (ManagedItem item in candidates)
            {
                if (IsShuttingDown)
                    return;

                bool running;
                int currentPid;
                DateTime startedAt;
                if (TryGetState(item.Id, out running, out currentPid, out startedAt))
                    continue;

                string file = CommandResolver.Resolve(item.File);
                if (file == null)
                    continue;
                string targetName = Path.GetFileName(file);
                if (string.IsNullOrEmpty(targetName))
                    continue;
                string extension = Path.GetExtension(targetName);
                if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
                    continue;

                int externalPid = PickExternalPid(snapshot, targetName, owned);
                if (externalPid == 0)
                    continue;

                Process candidate;
                try
                {
                    candidate = Process.GetProcessById(externalPid);
                }
                catch
                {
                    continue;
                }

                bool alive;
                try { alive = !candidate.HasExited; }
                catch { alive = false; }
                if (!alive)
                {
                    try { candidate.Dispose(); } catch { }
                    continue;
                }

                if (AdoptProcess(item, candidate, file))
                    owned.Add(externalPid);
            }
        }

        private bool AdoptProcess(ManagedItem item, Process process, string file)
        {
            try
            {
                int pid = SafePid(process);
                DateTime startedAt = DateTime.Now;
                try { startedAt = process.StartTime; } catch { }

                process.Exited += (s, e) => HandleExited(item.Id);
                try { process.EnableRaisingEvents = true; } catch { }

                var entry = new Entry
                {
                    Item = item,
                    Process = process,
                    Exited = new ManualResetEventSlim(false),
                    Pid = pid,
                    StartedAt = startedAt
                };

                lock (_gate)
                {
                    if (_shuttingDown || _entries.ContainsKey(item.Id))
                    {
                        try { process.Dispose(); } catch { }
                        return false;
                    }
                    _entries[item.Id] = entry;
                    _starting.Remove(item.Id);
                }

                try
                {
                    if (process.HasExited)
                        HandleExited(item.Id);
                }
                catch
                {
                }

                AppendOutput(item.Id, "[CliTray] 检测到 " + Path.GetFileName(file) +
                    " 已在运行 (PID " + pid + ")，已关联到该实例。");

                string logPath = ResolveLogFile(item);
                if (logPath != null)
                {
                    string itemId = item.Id;
                    Task.Run(() => TailLogFile(itemId, logPath));
                }

                RaiseStateChanged();
                return true;
            }
            catch
            {
                try { process.Dispose(); } catch { }
                return false;
            }
        }

        private static string ResolveLogFile(ManagedItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.LogFile))
                return null;
            string path;
            try
            {
                path = Environment.ExpandEnvironmentVariables(item.LogFile.Trim().Trim('"'));
                if (!Path.IsPathRooted(path))
                {
                    string baseDir = !string.IsNullOrWhiteSpace(item.WorkDir)
                        ? item.WorkDir
                        : Path.GetDirectoryName(item.File);
                    if (!string.IsNullOrEmpty(baseDir))
                        path = Path.Combine(baseDir, path);
                }
            }
            catch
            {
                return null;
            }
            return path.Length > 0 ? path : null;
        }

        private void TailLogFile(string itemId, string path)
        {
            long offset = -1;
            byte[] pending = new byte[0];

            while (!IsShuttingDown)
            {
                bool running;
                int pid;
                DateTime startedAt;
                if (!TryGetState(itemId, out running, out pid, out startedAt))
                    break;

                try
                {
                    if (File.Exists(path))
                    {
                        using (FileStream stream = new FileStream(path, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (offset < 0)
                            {
                                // 接管时从头读取，恢复已有日志内容
                                offset = Math.Max(0, stream.Length - MaxTailBytes);
                                pending = new byte[0];
                            }
                            else if (stream.Length < offset)
                            {
                                // 日志被截断或轮转，从头重读
                                offset = 0;
                                pending = new byte[0];
                            }

                            long end = stream.Length;
                            if (end > offset)
                            {
                                int count = (int)Math.Min(end - offset, int.MaxValue);
                                var buffer = new byte[pending.Length + count];
                                Array.Copy(pending, buffer, pending.Length);
                                stream.Seek(offset, SeekOrigin.Begin);
                                int read = 0;
                                while (read < count)
                                {
                                    int n = stream.Read(buffer, pending.Length + read, count - read);
                                    if (n <= 0)
                                        break;
                                    read += n;
                                }
                                offset += read;

                                int total = pending.Length + read;
                                int lastNewline = -1;
                                for (int i = total - 1; i >= 0; i--)
                                {
                                    if (buffer[i] == (byte)'\n')
                                    {
                                        lastNewline = i;
                                        break;
                                    }
                                }

                                if (lastNewline >= 0)
                                {
                                    int keep = total - (lastNewline + 1);
                                    var remainder = new byte[keep];
                                    Array.Copy(buffer, lastNewline + 1, remainder, 0, keep);
                                    pending = remainder;

                                    string block = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
                                    string[] lines = block.Split('\n');
                                    for (int i = 0; i < lines.Length; i++)
                                    {
                                        if (i == lines.Length - 1 && lines[i].Length == 0)
                                            continue;
                                        AppendOutput(itemId, lines[i].TrimEnd('\r'));
                                    }
                                }
                                else if (total > MaxTailBytes)
                                {
                                    pending = new byte[0];
                                }
                                else
                                {
                                    if (total == buffer.Length)
                                        pending = buffer;
                                    else
                                    {
                                        pending = new byte[total];
                                        Array.Copy(buffer, pending, total);
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(800);
            }
        }

        private HashSet<int> CollectOwnedPids()
        {
            var owned = new HashSet<int>();
            lock (_gate)
            {
                foreach (Entry entry in _entries.Values)
                {
                    if (entry.Pid > 0)
                        owned.Add(entry.Pid);
                    if (entry.Process != null)
                    {
                        try { owned.Add(entry.Process.Id); }
                        catch { }
                    }
                }
            }
            try
            {
                using (Process self = Process.GetCurrentProcess())
                    owned.Add(self.Id);
            }
            catch
            {
            }
            return owned;
        }

        private struct ProcSnapshot
        {
            public int Pid;
            public int ParentPid;
            public string Name;
        }

        private static List<ProcSnapshot> SnapshotProcesses()
        {
            var list = new List<ProcSnapshot>();
            IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshot == new IntPtr(-1))
                return list;
            try
            {
                var entry = new ProcessEntry32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32First(snapshot, ref entry))
                    return list;
                do
                {
                    string name = Path.GetFileName(entry.szExeFile);
                    if (string.IsNullOrEmpty(name))
                        continue;
                    list.Add(new ProcSnapshot
                    {
                        Pid = (int)entry.th32ProcessID,
                        ParentPid = (int)entry.th32ParentProcessID,
                        Name = name
                    });
                } while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return list;
        }

        private static int PickExternalPid(List<ProcSnapshot> snapshot, string targetName,
            HashSet<int> owned)
        {
            var sameName = new HashSet<int>();
            int fallback = 0;
            foreach (ProcSnapshot entry in snapshot)
            {
                if (!string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                sameName.Add(entry.Pid);
                if (!owned.Contains(entry.Pid) && fallback == 0)
                    fallback = entry.Pid;
            }
            if (fallback == 0)
                return 0;

            // 优先选根进程（父进程不是同名进程），例如 syncthing 的 monitor，
            // 避免抓到子进程导致父进程不停拉起新实例。
            foreach (ProcSnapshot entry in snapshot)
            {
                if (!string.Equals(entry.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (owned.Contains(entry.Pid))
                    continue;
                if (!sameName.Contains(entry.ParentPid))
                    return entry.Pid;
            }
            return fallback;
        }

        private Process FindExternalInstance(string fileName)
        {
            string targetName = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(targetName))
                return null;
            string extension = Path.GetExtension(targetName);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
                return null;

            HashSet<int> owned = CollectOwnedPids();
            int pid = PickExternalPid(SnapshotProcesses(), targetName, owned);
            if (pid == 0)
                return null;
            try
            {
                Process candidate = Process.GetProcessById(pid);
                try
                {
                    if (candidate.HasExited)
                    {
                        candidate.Dispose();
                        return null;
                    }
                }
                catch
                {
                    candidate.Dispose();
                    return null;
                }
                return candidate;
            }
            catch
            {
                return null;
            }
        }

        private static void KillTree(int pid)
        {
            if (pid <= 0)
                return;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/PID " + pid + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process killer = Process.Start(startInfo))
                {
                    if (killer != null && !killer.WaitForExit(5000))
                    {
                        try { killer.Kill(); } catch { }
                    }
                }
            }
            catch
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                        process.Kill();
                }
                catch
                {
                }
            }
        }

        private static ProcessStartInfo BuildStartInfo(ManagedItem item, string file)
        {
            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                WorkingDirectory = ResolveWorkDir(item)
            };

            string comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(comspec))
                comspec = "cmd.exe";

            if (!item.ShowConsole)
            {
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                startInfo.StandardOutputEncoding = Encoding.UTF8;
                startInfo.StandardErrorEncoding = Encoding.UTF8;
                startInfo.FileName = comspec;
                startInfo.Arguments = "/d /s /c \"chcp 65001>nul & \"" + file + "\" " +
                                      (item.Args ?? "") + "\"";
                return startInfo;
            }

            startInfo.CreateNoWindow = false;
            string extension = Path.GetExtension(file) ?? "";
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".com", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = file;
                startInfo.Arguments = item.Args ?? "";
            }
            else
            {
                startInfo.FileName = comspec;
                startInfo.Arguments = "/d /s /c \"\"" + file + "\" " +
                                      (item.Args ?? "") + "\"";
            }

            return startInfo;
        }

        private static string ResolveWorkDir(ManagedItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.WorkDir))
                return item.WorkDir;
            try
            {
                string directory = Path.GetDirectoryName(item.File);
                if (!string.IsNullOrEmpty(directory))
                    return directory;
            }
            catch
            {
            }
            return Environment.CurrentDirectory;
        }

        public string[] GetOutput(string id)
        {
            lock (_outputGate)
            {
                Queue<string> queue;
                return _outputs.TryGetValue(id, out queue) ? queue.ToArray() : new string[0];
            }
        }

        public void ClearOutput(string id)
        {
            lock (_outputGate)
                _outputs.Remove(id);
        }

        private void AppendOutput(string id, string line)
        {
            if (string.IsNullOrEmpty(id))
                return;

            if (line == null)
                line = "";
            if (line.Length > MaxOutputLineChars)
                line = line.Substring(0, MaxOutputLineChars);

            Action<string, string> handler;
            lock (_outputGate)
            {
                Queue<string> queue;
                if (!_outputs.TryGetValue(id, out queue))
                {
                    queue = new Queue<string>();
                    _outputs[id] = queue;
                }
                queue.Enqueue(line);
                while (queue.Count > MaxOutputLines)
                    queue.Dequeue();
                handler = OutputReceived;
            }

            if (handler != null)
            {
                try
                {
                    handler(id, line);
                }
                catch
                {
                }
            }
        }

        private void RaiseStateChanged()
        {
            EventHandler handler = StateChanged;
            if (handler == null)
                return;
            foreach (Delegate target in handler.GetInvocationList())
            {
                try
                {
                    ((EventHandler)target)(this, EventArgs.Empty);
                }
                catch
                {
                }
            }
        }
    }
}
