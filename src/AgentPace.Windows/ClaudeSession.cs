using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace AgentPace.Windows;
public sealed class ClaudeSession(Store store):IDisposable
{
    Window? window;
    WebView2? browser;
    CoreWebView2Environment? environment;
    readonly List<(Window Window,WebView2 View)> popups=[];
    readonly TextBlock message=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};
    readonly TextBlock origin=new(){Margin=new Thickness(12,0,12,6),FontSize=11};
    readonly ComboBox organizations=new(){Width=220,Margin=new Thickness(8),DisplayMemberPath="Name",SelectedValuePath="Id"};
    readonly Button sync=new(){Margin=new Thickness(8),Padding=new Thickness(12,6,12,6)};
    Task? initializing;
    CancellationTokenSource? query;
    bool disposed,clearing,updatingOrganizations;
    List<Organization> known=[];
    record Organization(string Id,string Name);
    public void RefreshLanguage()
    {
        if(window is not null)window.Title=L.T("Agent Pace · 连接 Claude","Agent Pace · Connect Claude");
        sync.Content=L.T("同步额度","Sync usage");
        message.Text=L.T("在下方 claude.ai 登录，完成验证后点击同步额度。登录会话保存在本应用中。","Sign in at claude.ai below, complete verification, then click Sync usage. The session stays in this app.");
    }
    Task Ensure()=>initializing??=Initialize();
    async Task Initialize()
    {
        try
        {
            environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(store.Storage.Root,"WebView2"));
            browser=new WebView2();
            window=new Window{Width=960,Height=740,MinWidth=640,MinHeight=480,ShowActivated=false,ShowInTaskbar=false,Opacity=0,WindowStartupLocation=WindowStartupLocation.CenterScreen};
            var panel=new DockPanel();var header=new StackPanel();header.Children.Add(message);header.Children.Add(origin);var actions=new StackPanel{Orientation=Orientation.Horizontal};actions.Children.Add(organizations);actions.Children.Add(sync);header.Children.Add(actions);DockPanel.SetDock(header,Dock.Top);panel.Children.Add(header);panel.Children.Add(browser);window.Content=panel;
            window.Closing+=(_,e)=>{if(!disposed){e.Cancel=true;window.Hide();window.ShowInTaskbar=false;}};
            sync.Click+=async(_,_)=>await store.SyncClaude();
            organizations.SelectionChanged+=async(_,_)=>{if(updatingOrganizations)return;if(organizations.SelectedItem is Organization item){store.Settings.ClaudeOrganization=item.Id;store.Save();await store.SyncClaude();}};
            RefreshLanguage();
            window.Show();await browser.EnsureCoreWebView2Async(environment);window.Hide();window.Opacity=1;
            Configure(browser);
            browser.CoreWebView2.NavigationCompleted+=(_,_)=>origin.Text=Uri.TryCreate(browser.Source?.AbsoluteUri,UriKind.Absolute,out var uri)?uri.GetLeftPart(UriPartial.Authority):"";
            await NavigateUsage(browser.CoreWebView2,CancellationToken.None);
        }
        catch{initializing=null;browser?.Dispose();window?.Hide();throw;}
    }
    public async Task ShowLogin(){await Ensure();window!.ShowInTaskbar=true;window.Show();window.Activate();}
    void Configure(WebView2 view)
    {
        var core=view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;
        core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
        core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
        core.DownloadStarting+=(_,e)=>e.Cancel=true;
        core.NavigationStarting+=(_,e)=>{if(!Allowed(e.Uri))e.Cancel=true;};
        core.FrameNavigationStarting+=(_,e)=>{if(!Allowed(e.Uri))e.Cancel=true;};
        core.NewWindowRequested+=async(_,e)=>
        {
            e.Handled=true;
            if(window?.IsVisible!=true||!Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)||u.Scheme!="https")return;
            using var deferral=e.GetDeferral();
            var child=new WebView2();var host=new Window{Title=L.T("Claude 登录","Claude sign-in"),Width=760,Height=720,Content=child,Owner=window,WindowStartupLocation=WindowStartupLocation.CenterOwner};
            try{host.Show();await child.EnsureCoreWebView2Async(environment);Configure(child);e.NewWindow=child.CoreWebView2;popups.Add((host,child));host.Closed+=(_,_)=>{child.Dispose();popups.RemoveAll(p=>p.Window==host);};child.CoreWebView2.WindowCloseRequested+=(_,_)=>host.Close();}
            catch{child.Dispose();host.Close();message.Text=L.T("登录窗口无法打开，请重试。","Sign-in window could not open. Retry.");}
        };
    }
    static bool Allowed(string raw)=>raw is "about:blank" or "about:srcdoc"||Uri.TryCreate(raw,UriKind.Absolute,out var u)&&u.Scheme=="https";
    public async Task<Snapshot> Fetch(CancellationToken ct)
    {
        if(clearing||disposed)throw new OperationCanceledException();
        using var request=CancellationTokenSource.CreateLinkedTokenSource(ct);request.CancelAfter(TimeSpan.FromSeconds(45));query=request;
        try
        {
            await Ensure().WaitAsync(request.Token);
            var core=browser!.CoreWebView2;
            // Wait for initial navigation. Never extract cookies or access page messages.
            if(!ClaudeBridge.IsClaude(core.Source)&&window?.IsVisible!=true)await NavigateUsage(core,request.Token);
            if(!ClaudeBridge.IsClaude(core.Source))throw new PaceException("请先登录 Claude。","Sign in to Claude first.",true);
            using var tree=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree","{}").WaitAsync(request.Token));
            var frame=tree.RootElement.GetProperty("frameTree").GetProperty("frame");
            if(!ClaudeBridge.IsClaude(frame.Str("url")))throw PaceException.Login();
            var world=JsonSerializer.Serialize(new{frameId=frame.Str("id"),worldName="AgentPaceQuota",grantUniveralAccess=false});
            using var context=JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",world).WaitAsync(request.Token));
            var expression="(async () => { const selectedOrganization = "+JsonSerializer.Serialize(store.Settings.ClaudeOrganization)+";\n"+ClaudeBridge.Script+"\n})()";
            var parameters=JsonSerializer.Serialize(new{expression,contextId=context.RootElement.GetProperty("executionContextId").GetInt32(),awaitPromise=true,returnByValue=true});
            var raw=await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",parameters).WaitAsync(request.Token);
            if(raw.Length>2_000_000||!ClaudeBridge.IsClaude(core.Source))throw PaceException.Invalid();
            using var result=JsonDocument.Parse(raw);
            if(result.RootElement.Get("exceptionDetails")is not null)throw PaceException.Network();
            var value=result.RootElement.GetProperty("result").Str("value");
            if(value is null||value.Length>1_000_000)throw PaceException.Invalid();
            using var data=JsonDocument.Parse(value);var reply=data.RootElement;
            if(reply.Get("organizations") is {ValueKind:JsonValueKind.Array} entries)
            {
                known=entries.EnumerateArray().Where(e=>Guid.TryParse(e.Str("uuid"),out _)).Select(e=>new Organization(e.Str("uuid")!,e.Str("name")??"Claude")).ToList();
                updatingOrganizations=true;organizations.ItemsSource=known;organizations.SelectedValue=store.Settings.ClaudeOrganization;organizations.Visibility=known.Count>1||reply.Num("status")==409?Visibility.Visible:Visibility.Collapsed;updatingOrganizations=false;
            }
            var snapshot=UsageParser.Claude(reply,DateTimeOffset.Now);
            request.Token.ThrowIfCancellationRequested();
            store.Settings.ClaudeOrganization=reply.Str("organization")??"";
            message.Text=L.T("已连接，额度已同步。关闭窗口后仍会自动刷新。","Connected and synced. You can close this window; automatic refresh continues.");
            foreach(var popup in popups.ToArray())popup.Window.Close();
            return snapshot;
        }
        catch(PaceException ex){message.Text=ex.Message;throw;}
        finally{if(query==request)query=null;}
    }
    static async Task NavigateUsage(CoreWebView2 core,CancellationToken ct)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>completion.TrySetResult();
        core.NavigationCompleted+=Complete;
        try{core.Navigate("https://claude.ai/settings/usage");await completion.Task.WaitAsync(TimeSpan.FromSeconds(40),ct);}
        finally{core.NavigationCompleted-=Complete;}
    }
    public async Task Disconnect()
    {
        clearing=true;query?.Cancel();
        try{await Ensure();foreach(var p in popups.ToArray())p.Window.Close();browser!.CoreWebView2.Stop();browser.CoreWebView2.Navigate("about:blank");await browser.CoreWebView2.Profile.ClearBrowsingDataAsync();window?.Hide();known.Clear();organizations.ItemsSource=null;RefreshLanguage();}
        finally{clearing=false;}
    }
    public void Dispose(){disposed=true;query?.Cancel();foreach(var p in popups.ToArray())p.Window.Close();browser?.Dispose();window?.Close();}
}
