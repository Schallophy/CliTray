using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace CliTray.Services
{
    internal static class EmbeddedAssemblies
    {
        private static readonly Dictionary<string, string> Resources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Wpf.Ui", "CliTray.Embedded.Wpf.Ui.dll" },
                { "Wpf.Ui.Abstractions", "CliTray.Embedded.Wpf.Ui.Abstractions.dll" },
                { "System.Memory", "CliTray.Embedded.System.Memory.dll" },
                { "System.Buffers", "CliTray.Embedded.System.Buffers.dll" },
                { "System.Numerics.Vectors", "CliTray.Embedded.System.Numerics.Vectors.dll" },
                { "System.Runtime.CompilerServices.Unsafe", "CliTray.Embedded.System.Runtime.CompilerServices.Unsafe.dll" },
                { "Newtonsoft.Json", "CliTray.Embedded.Newtonsoft.Json.dll" }
            };

        private static readonly Dictionary<string, Assembly> Cache =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;

            lock (Cache)
            {
                Assembly cached;
                if (Cache.TryGetValue(name, out cached))
                    return cached;

                string resourceName;
                if (!Resources.TryGetValue(name, out resourceName))
                    return null;

                using (Stream stream = typeof(EmbeddedAssemblies).Assembly
                           .GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                        return null;

                    var buffer = new byte[stream.Length];
                    int offset = 0;
                    while (offset < buffer.Length)
                    {
                        int read = stream.Read(buffer, offset, buffer.Length - offset);
                        if (read <= 0)
                            break;
                        offset += read;
                    }

                    Assembly assembly = Assembly.Load(buffer);
                    Cache[name] = assembly;
                    return assembly;
                }
            }
        }
    }
}
