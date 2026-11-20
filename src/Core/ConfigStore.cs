using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace CliTray.Core
{
    internal sealed class ConfigLoadResult
    {
        public AppConfig Config { get; set; }
        public string Warning { get; set; }
    }

    internal sealed class ConfigStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        private readonly object _saveLock = new object();

        public AppConfig Config { get; private set; }

        public ConfigLoadResult Load()
        {
            string warning = null;
            AppConfig config = null;
            bool needsSave = false;

            if (!File.Exists(AppPaths.ConfigFile))
            {
                config = new AppConfig();
                needsSave = true;
            }
            else
            {
                try
                {
                    config = Deserialize(File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    warning = "配置文件解析失败，已回退: " + ex.Message;
                    string backup = AppPaths.ConfigFile + ".bak";
                    if (File.Exists(backup))
                    {
                        try
                        {
                            config = Deserialize(File.ReadAllText(backup, Encoding.UTF8));
                            warning += "（已使用备份）";
                        }
                        catch
                        {
                        }
                    }
                    Quarantine();
                    needsSave = true;
                }
            }

            Config = config ?? new AppConfig();
            Normalize(Config);

            if (needsSave)
            {
                try
                {
                    Save();
                }
                catch (Exception ex)
                {
                    warning = Append(warning, "写入配置失败: " + ex.Message);
                }
            }

            return new ConfigLoadResult { Config = Config, Warning = warning };
        }

        public void Save()
        {
            lock (_saveLock)
            {
                string json = JsonConvert.SerializeObject(Config, JsonSettings);
                string tmp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(AppPaths.ConfigFile))
                {
                    try
                    {
                        File.Replace(tmp, AppPaths.ConfigFile, AppPaths.ConfigFile + ".bak", true);
                    }
                    catch
                    {
                        File.Copy(tmp, AppPaths.ConfigFile, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, AppPaths.ConfigFile);
                }
            }
        }

        private static AppConfig Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new AppConfig();
            return JsonConvert.DeserializeObject<AppConfig>(json, JsonSettings) ?? new AppConfig();
        }

        private static void Normalize(AppConfig config)
        {
            if (config.Settings == null)
                config.Settings = new ConfigSettings();
            if (config.Items == null)
                config.Items = new List<ManagedItem>();

            string theme = (config.Settings.Theme ?? "system").Trim().ToLowerInvariant();
            if (theme != "light" && theme != "dark")
                theme = "system";
            config.Settings.Theme = theme;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = config.Items.Count - 1; i >= 0; i--)
            {
                ManagedItem item = config.Items[i];
                if (item == null)
                {
                    config.Items.RemoveAt(i);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                {
                    item.Id = Guid.NewGuid().ToString("N");
                    ids.Add(item.Id);
                }

                item.Name = string.IsNullOrWhiteSpace(item.Name)
                    ? NameFromFile(item.File)
                    : item.Name.Trim();
                item.File = (item.File ?? "").Trim();
                item.Args = item.Args ?? "";
                item.WorkDir = (item.WorkDir ?? "").Trim();

                if (item.DelaySeconds < 0) item.DelaySeconds = 0;
                if (item.DelaySeconds > 3600) item.DelaySeconds = 3600;
                if (item.StopTimeout < 1) item.StopTimeout = 1;
                if (item.StopTimeout > 60) item.StopTimeout = 60;
            }
        }

        private static string NameFromFile(string file)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(file ?? "");
                return string.IsNullOrEmpty(name) ? "未命名" : name;
            }
            catch
            {
                return "未命名";
            }
        }

        private static void Quarantine()
        {
            try
            {
                string destination = AppPaths.ConfigFile + ".corrupt-" +
                                     DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(AppPaths.ConfigFile, destination);
            }
            catch
            {
            }
        }

        private static string Append(string first, string second)
        {
            return string.IsNullOrEmpty(first) ? second : first + "；" + second;
        }
    }
}
