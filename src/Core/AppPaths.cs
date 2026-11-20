using System;
using System.Diagnostics;
using System.IO;

namespace CliTray.Core
{
    internal static class AppPaths
    {
        public static string ConfigDir { get; private set; }
        public static string ConfigFile { get; private set; }
        public static string StateDir { get; private set; }
        public static string CrashLog { get; private set; }
        public static string ExePath { get; private set; }

        static AppPaths()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home))
                home = ".";

            string configRoot = XdgRoot("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
            string stateRoot = XdgRoot("XDG_STATE_HOME", Path.Combine(home, ".local", "state"));

            ConfigDir = Path.Combine(configRoot, "clitray");
            ConfigFile = Path.Combine(ConfigDir, "config.json");
            StateDir = Path.Combine(stateRoot, "clitray");
            CrashLog = Path.Combine(StateDir, "crash.log");
            ExePath = Process.GetCurrentProcess().MainModule.FileName;

            Directory.CreateDirectory(ConfigDir);
            Directory.CreateDirectory(StateDir);
        }

        private static string XdgRoot(string variable, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                value = NormalizeMsys(Environment.ExpandEnvironmentVariables(value.Trim()));
                if (IsAbsoluteWindowsPath(value))
                    return value;
            }
            return fallback;
        }

        private static string NormalizeMsys(string path)
        {
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/')
                return char.ToUpperInvariant(path[1]) + ":\\" + path.Substring(3).Replace('/', '\\');
            return path;
        }

        private static bool IsAbsoluteWindowsPath(string path)
        {
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/'))
                return true;
            return path.Length >= 2 && path[0] == '\\' && path[1] == '\\';
        }
    }
}
