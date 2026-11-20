using System;
using System.Collections.Generic;

namespace CliTray.Core
{
    public sealed class AppConfig
    {
        public ConfigSettings Settings { get; set; }
        public List<ManagedItem> Items { get; set; }

        public AppConfig()
        {
            Settings = new ConfigSettings();
            Items = new List<ManagedItem>();
        }
    }

    public sealed class ConfigSettings
    {
        public bool StopAllOnExit { get; set; } = true;
        public string Theme { get; set; } = "system";
    }

    public sealed class ManagedItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string File { get; set; }
        public string Args { get; set; }
        public string WorkDir { get; set; }
        public string LogFile { get; set; }
        public bool AutoStart { get; set; }
        public int DelaySeconds { get; set; }
        public bool ShowConsole { get; set; }
        public int StopTimeout { get; set; } = 3;

        public ManagedItem Clone()
        {
            return new ManagedItem
            {
                Id = Id,
                Name = Name,
                File = File,
                Args = Args,
                WorkDir = WorkDir,
                LogFile = LogFile,
                AutoStart = AutoStart,
                DelaySeconds = DelaySeconds,
                ShowConsole = ShowConsole,
                StopTimeout = StopTimeout
            };
        }
    }
}
