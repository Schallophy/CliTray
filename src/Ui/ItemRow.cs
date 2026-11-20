using System;
using System.ComponentModel;
using CliTray.Core;

namespace CliTray.Ui
{
    internal sealed class ItemRow : INotifyPropertyChanged
    {
        private bool _autoStart;
        private bool _running;
        private string _pidText = "";
        private DateTime _startedAt;

        public ItemRow(ManagedItem item)
        {
            Item = item;
            _autoStart = item.AutoStart;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ManagedItem Item { get; private set; }

        public string Id
        {
            get { return Item.Id; }
        }

        public string Name
        {
            get { return Item.Name ?? ""; }
        }

        public string Command
        {
            get
            {
                string args = string.IsNullOrEmpty(Item.Args) ? "" : " " + Item.Args;
                return (Item.File ?? "") + args;
            }
        }

        public string WorkDir
        {
            get { return Item.WorkDir ?? ""; }
        }

        public bool AutoStart
        {
            get { return _autoStart; }
            set
            {
                if (_autoStart == value)
                    return;
                _autoStart = value;
                Raise("AutoStart");
            }
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public string StatusText
        {
            get { return _running ? "运行中" : "已停止"; }
        }

        public string PidText
        {
            get { return _pidText; }
        }

        public DateTime StartedAt
        {
            get { return _startedAt; }
        }

        public void SetRuntime(bool running, int pid, DateTime startedAt)
        {
            bool changed = _running != running ||
                           _startedAt != startedAt ||
                           _pidText != (running ? pid.ToString() : "");

            _running = running;
            _startedAt = startedAt;
            _pidText = running ? pid.ToString() : "";

            if (!changed)
                return;

            Raise("IsRunning");
            Raise("StatusText");
            Raise("PidText");
            Raise("StartedAt");
        }

        public void SyncAutoStart(bool value)
        {
            if (_autoStart == value)
                return;
            _autoStart = value;
            Raise("AutoStart");
        }

        private void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
