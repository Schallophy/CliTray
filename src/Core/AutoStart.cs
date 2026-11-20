using System;
using Microsoft.Win32;

namespace CliTray.Core
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CliTray";

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                if (key == null)
                    return false;
                string value = key.GetValue(ValueName) as string;
                return !string.IsNullOrEmpty(value);
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                        throw new InvalidOperationException("无法打开启动项注册表");
                    key.SetValue(ValueName, Quote(AppPaths.ExePath) + " --autostart",
                                 RegistryValueKind.String);
                }
            }
            else
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null && key.GetValue(ValueName) != null)
                        key.DeleteValue(ValueName, false);
                }
            }
        }

        public static void RepairIfNeeded()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                        return;
                    string current = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(current))
                        return;
                    string expected = Quote(AppPaths.ExePath) + " --autostart";
                    if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                        key.SetValue(ValueName, expected, RegistryValueKind.String);
                }
            }
            catch
            {
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
