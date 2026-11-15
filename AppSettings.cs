using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace NovaZip
{
    internal enum ThemeMode
    {
        Auto,
        Dark,
        Light
    }

    /// <summary>用户偏好：主题、右键菜单、智能解压等，保存在本地应用数据目录。</summary>
    internal static class AppSettings
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaZip");

        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.txt");

        public static ThemeMode Theme = ThemeMode.Auto;
        public static bool SmartExtract = true;
        public static bool ExcludeJunk = true;
        public static bool FixLegacyNames = true;
        public static double WindowOpacity = 1.0;
        public static bool AutoCheckUpdates = true;
        public static DateTime LastUpdateCheckUtc = DateTime.MinValue;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(SettingsFile)) return;

                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;

                    var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    var value = line.Substring(idx + 1).Trim();

                    switch (key)
                    {
                        case "theme":
                            Theme = value.ToLowerInvariant() switch
                            {
                                "dark" => ThemeMode.Dark,
                                "light" => ThemeMode.Light,
                                _ => ThemeMode.Auto
                            };
                            break;
                        case "smartextract":
                            SmartExtract = value != "0";
                            break;
                        case "excludejunk":
                            ExcludeJunk = value != "0";
                            break;
                        case "fixlegacynames":
                            FixLegacyNames = value != "0";
                            break;
                        case "windowopacity":
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
                                WindowOpacity = Math.Clamp(opacity, 0.0, 1.0);
                            break;
                        case "autocheckupdates":
                            AutoCheckUpdates = value != "0";
                            break;
                        case "lastupdatecheckutc":
                            if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind, out var checkedAt))
                                LastUpdateCheckUtc = checkedAt.ToUniversalTime();
                            break;
                    }
                }
            }
            catch
            {
                // 读取失败时使用默认值
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllLines(SettingsFile, new[]
                {
                    "theme=" + Theme.ToString().ToLowerInvariant(),
                    "smartextract=" + (SmartExtract ? "1" : "0"),
                    "excludejunk=" + (ExcludeJunk ? "1" : "0"),
                    "fixlegacynames=" + (FixLegacyNames ? "1" : "0"),
                    "windowopacity=" + WindowOpacity.ToString("0.00", CultureInfo.InvariantCulture),
                    "autocheckupdates=" + (AutoCheckUpdates ? "1" : "0"),
                    "lastupdatecheckutc=" + (LastUpdateCheckUtc == DateTime.MinValue
                        ? ""
                        : LastUpdateCheckUtc.ToString("o", CultureInfo.InvariantCulture))
                });
            }
            catch
            {
                // 沙箱或权限受限时忽略
            }
        }

        public static bool SystemUsesDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int light)
                    return light == 0;
            }
            catch
            {
                // 无法读取时回退深色
            }

            return true;
        }
    }
}
