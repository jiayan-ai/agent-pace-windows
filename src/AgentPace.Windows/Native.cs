using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace AgentPace.Windows;
public static class Native
{
    [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]public static extern bool DestroyIcon(IntPtr handle);
    const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool StartupEnabled=>Registry.GetValue(@"HKEY_CURRENT_USER\"+RunKey,"AgentPace",null)is string;
    public static void SetStartup(bool enabled){using var key=Registry.CurrentUser.CreateSubKey(RunKey);if(enabled)key.SetValue("AgentPace",'"'+Environment.ProcessPath+'"'+" --background");else key.DeleteValue("AgentPace",false);}
    public static void OpenUrl(string url){try{if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https")Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch(System.ComponentModel.Win32Exception){MessageBox.Show(L.T("无法打开默认浏览器。","Could not open the default browser."));}}
}
