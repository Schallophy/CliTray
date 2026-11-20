using System;
using System.IO;

namespace CliTray.Core
{
    internal static class CommandResolver
    {
        public static string Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string value = input.Trim().Trim('"');
            if (value.Length == 0)
                return null;

            bool hasDirectory = value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0;
            if (hasDirectory || Path.IsPathRooted(value))
            {
                try
                {
                    value = Path.GetFullPath(value);
                }
                catch
                {
                    return null;
                }
                return File.Exists(value) ? value : null;
            }

            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] extensions = Extensions(value);
            foreach (string rawDir in pathVariable.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = rawDir.Trim().Trim('"');
                if (dir.Length == 0)
                    continue;
                foreach (string extension in extensions)
                {
                    string candidate;
                    try
                    {
                        candidate = Path.Combine(dir, value + extension);
                    }
                    catch
                    {
                        continue;
                    }
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            return null;
        }

        private static string[] Extensions(string value)
        {
            if (Path.HasExtension(value))
                return new[] { "" };

            string pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrWhiteSpace(pathExt))
                pathExt = ".COM;.EXE;.BAT;.CMD";
            return pathExt.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}