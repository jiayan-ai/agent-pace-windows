using Microsoft.Web.WebView2.Wpf;
namespace AgentPace.Windows;
public sealed class ForecastWindow:Window
{
    static ForecastWindow? instance;
    readonly WebView2 view=new();
    readonly TextBlock status=new(){Margin=new Thickness(10),TextWrapping=TextWrapping.Wrap};
    bool loaded;
    Button refresh=null!,external=null!;
    ForecastWindow()
    {
        Title="Will Codex Reset";Width=900;Height=760;MinWidth=600;MinHeight=400;
        var root=new DockPanel();var top=new StackPanel();top.Children.Add(status);var actions=new StackPanel{Orientation=Orientation.Horizontal};refresh=new Button{Content=L.T("刷新","Refresh"),Margin=new Thickness(8)};refresh.Click+=(_,_)=>view.CoreWebView2?.Reload();external=new Button{Content=L.T("在浏览器打开","Open in browser"),Margin=new Thickness(8)};external.Click+=(_,_)=>Native.OpenUrl("https://willcodexreset.com/");actions.Children.Add(refresh);actions.Children.Add(external);top.Children.Add(actions);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);root.Children.Add(view);Content=root;
        Loaded+=async(_,_)=>{if(loaded)return;loaded=true;try{view.CreationProperties=new(){UserDataFolder=Path.Combine(Path.GetTempPath(),"AgentPace-forecast-"+Guid.NewGuid().ToString("N")),IsInPrivateModeEnabled=true};await view.EnsureCoreWebView2Async();var core=view.CoreWebView2;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.PermissionRequested+=(_,e)=>e.State=Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)||u.Scheme!="https"||u.Host is not("willcodexreset.com" or "www.willcodexreset.com")){e.Cancel=true;if(e.IsUserInitiated)Native.OpenUrl(e.Uri);}};core.NewWindowRequested+=(_,e)=>{e.Handled=true;if(e.IsUserInitiated)Native.OpenUrl(e.Uri);};core.NavigationCompleted+=(_,e)=>status.Text=e.IsSuccess?L.T("社区预测 · 公告不等于账户到账","Community forecast · Announcements are not account delivery"):L.T("网页加载失败，可以刷新或在浏览器打开。","Page failed to load. Retry or open it in your browser.");core.Navigate("https://willcodexreset.com/");}catch{status.Text=L.T("网页无法打开，请检查 WebView2 Runtime。","Could not open the page. Check WebView2 Runtime.");}};
        Closed+=(_,_)=>{view.Dispose();instance=null;};
    }
    public static void UpdateLanguage(){if(instance is {} w){w.refresh.Content=L.T("刷新","Refresh");w.external.Content=L.T("在浏览器打开","Open in browser");}}
    public static void Open(){instance??=new();instance.Show();instance.Activate();}
}
