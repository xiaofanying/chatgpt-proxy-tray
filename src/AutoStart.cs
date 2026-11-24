using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChatGptProxyTray
{
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ChatGptProxyTray";

        public static string CommandLine()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    return value != null && Convert.ToString(value).Trim().Length > 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取开机自启状态失败: " + ex.Message);
                return false;
            }
        }

        public static bool MatchesCurrentExe()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    if (value == null) return false;
                    return string.Equals(Convert.ToString(value).Trim(), CommandLine(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enabled, out string error)
        {
            error = "";
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null)
                    {
                        error = "无法打开注册表项 HKCU\\" + RunKey;
                        return false;
                    }
                    if (enabled)
                    {
                        key.SetValue(ValueName, CommandLine());
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                Log.Info(enabled ? "已开启开机自启: " + CommandLine() : "已关闭开机自启");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("设置开机自启失败", ex);
                return false;
            }
        }
    }
}
