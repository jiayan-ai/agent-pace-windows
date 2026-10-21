using System.Globalization;
using System.Windows.Automation;
using System.Windows.Input;
using Microsoft.Win32;
namespace AgentPace.Windows;
public sealed class MainWindow:Window
{
    readonly Store store;
    readonly ScrollViewer scroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    readonly Dictionary<string,bool> expanded=[];
    bool rendering,quitting;
    Brush foreground=Brushes.Black,muted=Brushes.Gray,card=Brushes.White,accent=Brushes.SlateBlue;
    public MainWindow(Store store)
    {
        this.store=store;Title="Agent Pace";Width=460;Height=780;MinWidth=400;MinHeight=440;FontFamily=new FontFamily("Segoe UI");FontSize=14;WindowStartupLocation=WindowStartupLocation.CenterScreen;Content=scroll;
        Closing+=(_,e)=>{if(!quitting){e.Cancel=true;Hide();ShowInTaskbar=false;}};
        store.Changed+=Render;Render();
    }
    public void Quit(){quitting=true;Close();}
    public void ShowDetails(){ShowInTaskbar=true;if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;Show();Activate();}
    public void ToggleFromTray(){if(IsVisible&&IsActive)Hide();else{ShowInTaskbar=false;Show();Activate();PositionNearTray();}}
    void PositionNearTray()
    {
        var screen=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var helper=new System.Windows.Interop.WindowInteropHelper(this);var dpi=Native.GetDpiForWindow(helper.Handle)/96d;
        var width=(int)(ActualWidth*dpi);var height=(int)(Math.Min(ActualHeight,screen.Height/dpi-16)*dpi);
        Native.SetWindowPos(helper.Handle,IntPtr.Zero,screen.Right-width-8,screen.Bottom-height-8,width,height,0x0014);
    }
    public void Render()
    {
        if(rendering||HasOpenCombo(scroll))return;rendering=true;
        try
        {
            var offset=scroll.VerticalOffset;
            var dark=Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int n&&n==0;
            foreground=dark?Brushes.WhiteSmoke:Brush("#33333B");muted=dark?Brushes.LightGray:Brushes.DimGray;card=Brush(dark?"#27272D":"#F5F5F8");Background=Brush(dark?"#1C1C21":"#FFFFFF");Foreground=foreground;
            if(SystemParameters.HighContrast){foreground=SystemColors.WindowTextBrush;muted=foreground;card=SystemColors.WindowBrush;Background=SystemColors.WindowBrush;}
            accent=Brush(store.Provider=="claude"?"#BC6946":"#5958D9");
            var root=new StackPanel{Margin=new Thickness(22)};
            var header=new DockPanel{Margin=new Thickness(0,0,0,18)};
            var languages=new ComboBox{Width=108,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(8,4,0,0),ItemsSource=new[]{L.T("跟随系统","System"),"English","简体中文"},SelectedIndex=store.Settings.Language=="en"?1:store.Settings.Language=="zh-Hans"?2:0};
            languages.DropDownClosed+=(_,_)=>Render();AutomationProperties.SetName(languages,"Language / 语言");languages.SelectionChanged+=(_,_)=>{if(!rendering)store.Language(languages.SelectedIndex==1?"en":languages.SelectedIndex==2?"zh-Hans":"system");};DockPanel.SetDock(languages,Dock.Right);header.Children.Add(languages);
            var brand=new StackPanel();brand.Children.Add(Text("◕  Agent Pace",23,true));brand.Children.Add(Text(store.Session is null?L.T("每日额度节奏","Daily usage pace"):L.T("每小时额度节奏","Hourly usage pace"),12,false,muted));header.Children.Add(brand);root.Children.Add(header);
            var providers=new UniformGridShim(2);foreach(var (id,title) in new[]{("codex","Codex"),("claude","Claude Code")}){var b=Button(title,()=>store.SelectProvider(id));if(id==store.Provider){b.Background=accent;b.Foreground=Brushes.White;}providers.Children.Add(b);}root.Children.Add(providers);
            if(store.Current.Snapshot?.Windows is {Count:>1} windows)
            {
                var choices=new ComboBox{ItemsSource=windows.Select(w=>w.Label).ToList(),SelectedIndex=windows.FindIndex(w=>w.Id==store.Current.SelectedId),Margin=new Thickness(0,12,0,14),Padding=new Thickness(6)};choices.DropDownClosed+=(_,_)=>Render();AutomationProperties.SetName(choices,L.T("额度窗口","Quota window"));choices.SelectionChanged+=(_,_)=>{if(!rendering&&choices.SelectedIndex>=0)store.SelectWindow(windows[choices.SelectedIndex].Id);};root.Children.Add(choices);
            }
            if(store.Selected is {} selected)
            {
                if(store.Session is {} session)Session(root,session);else Daily(root,selected);
                if(store.Stale)root.Children.Add(Text(L.T("显示上次记录，尚未确认最新额度","Showing last-known usage; current quota is unconfirmed"),12,false,Brushes.DarkOrange));
                if(store.Provider=="codex"){Radar(root);Insights(root);}
            }
            else root.Children.Add(Card(Text(store.Busy?L.T("正在读取额度…","Reading usage…"):L.T("等待额度数据","Waiting for usage data"),22,true),Text(store.Provider=="codex"?L.T("安装并登录 Codex CLI；可在设置中指定 codex.exe 或 WSL 发行版。","Install and sign in to Codex CLI. Settings accepts codex.exe or a WSL distribution."):L.T("在工具内登录 Claude 后自动同步。","Connect Claude in this app to sync automatically."),13,false,muted)));
            if(store.Error is {} error)root.Children.Add(Text(error.Message,13,false,Brushes.DarkOrange));
            if(store.PersistenceError is {} persistence)root.Children.Add(Text(persistence,12,false,Brushes.DarkOrange));
            if(store.Provider=="claude"){
                root.Children.Add(Button(L.T("连接 / 验证 Claude","Connect / verify Claude"),async()=>await store.ConnectClaude()));
                if(store.Error?.Message.Contains("WebView2")==true)root.Children.Add(Button(L.T("安装 Microsoft WebView2 Runtime","Install Microsoft WebView2 Runtime"),()=>Native.OpenUrl("https://developer.microsoft.com/en-us/microsoft-edge/webview2/")));
            }
            Settings(root);
            var footer=new WrapPanel{Margin=new Thickness(0,15,0,0)};var refresh=Button(store.Busy?L.T("同步中…","Syncing…"):L.T("刷新","Refresh"),async()=>await store.Refresh());refresh.IsEnabled=!store.Busy;footer.Children.Add(refresh);footer.Children.Add(Button(L.T("置顶","Pin window"),()=>{Topmost=!Topmost;Render();}));footer.Children.Add(Button(L.T("退出","Quit"),()=>((App)Application.Current).ExitApp()));root.Children.Add(footer);
            root.Children.Add(Text(L.T($"{store.Current.Snapshot?.FetchedAt.ToLocalTime():HH:mm} 更新 · 每 5 分钟同步",$"Updated {store.Current.Snapshot?.FetchedAt.ToLocalTime():HH:mm} · Syncs every 5 min"),11,false,muted));
            root.Children.Add(Text("Agent Pace for Windows · 1.0.0-beta.1"+(Topmost?" · "+L.T("已置顶","Pinned"):""),10,false,muted));
            scroll.Content=root;scroll.ScrollToVerticalOffset(offset);
        }
        finally{rendering=false;}
    }
    void Daily(StackPanel root,MeteredWindow selected)
    {
        var b=store.Budget;var color=b?.OverBudget>0?Brushes.IndianRed:accent;
        root.Children.Add(Card(Text((b?.OverBudget>0?L.T("今日预算已超额","Over today's budget"):L.T("今天还可以用","Available today"))+(store.Stale?L.T(" · 上次记录"," · Last known"):""),16,true,muted),Text(L.Number(b?.LeftToday)+"%",56,true,color),Text(selected.BucketId=="claude_fable"?L.T("占 Fable 周额度的百分比","Percentage of Fable weekly quota"):L.T("占本周期总额度的百分比","Percentage of this window's quota"),12,false,muted)));
        if(selected.BucketId=="claude_fable")root.Children.Add(Text(L.T("Fable 使用也计入总周额度，仍受五小时额度限制。","Fable also counts toward the overall weekly quota and remains subject to the five-hour limit."),12,false,muted));
        var metrics=new UniformGridShim(2);var left=new StackPanel();left.Children.Add(Text(L.T("周期剩余","Remaining"),13,false,muted));left.Children.Add(Text(L.Number(selected.Window.Remaining)+"%",28,true));left.Children.Add(Text(L.T($"已用 {L.Number(selected.Window.UsedPercent)}%",$"Used {L.Number(selected.Window.UsedPercent)}%"),12,false,muted));metrics.Children.Add(left);
        var right=new StackPanel();right.Children.Add(Text(L.T("距离重置","Resets in"),13,false,muted));right.Children.Add(Text(L.Number(selected.Window.ResetDate is {} end?Math.Max(0,(end-store.Now).TotalDays):null)+L.T(" 天"," days"),25,true));right.Children.Add(Text(L.Date(selected.Window.ResetDate),12,false,muted));metrics.Children.Add(right);root.Children.Add(Card(metrics));
        if(b is not null)root.Children.Add(Card(Text(L.T($"今日预算  {L.Number(b.Allowance)}%",$"Today's budget  {L.Number(b.Allowance)}%"),14,true),Progress(b.Allowance>0?Math.Clamp(b.Consumed/b.Allowance*100,0,100):100,color),Text(L.T($"已用 {L.Number(b.Consumed)}% · 从 {b.StartedAt.ToLocalTime():HH:mm} 首次同步起",$"Used {L.Number(b.Consumed)}% · Since first sync at {b.StartedAt.ToLocalTime():HH:mm}"),12,false,muted)));
        else root.Children.Add(Text(L.T("等待有效额度与重置时间，确认今日预算。","Waiting for valid quota and reset time before calculating today's budget."),12,false,muted));
    }
    void Session(StackPanel root,SessionPace pace)
    {
        var reset=Card(Text(L.T("5 小时额度 · 距离重置","5-hour quota · Resets in"),16,true,Brushes.White),Text(pace.Countdown,30,true,Brushes.White),Text(L.Date(pace.Window.ResetDate),13,false,Brushes.White));reset.Background=accent;root.Children.Add(reset);
        root.Children.Add(Card(Text(pace.Heading+(store.Stale?L.T(" · 上次记录"," · Last known"):""),16,true,muted),Text(L.Number(pace.Allowance)+" "+pace.Unit,40,true,accent),Text(pace.FinalHour?L.T("不足一小时，显示重置前实际剩余额度。","Less than one hour remains; showing the actual quota available before reset."):L.T("按剩余额度与剩余小时数动态均摊。","Evenly spread across the remaining hours."),13,false,muted),Text(L.T("这是额度百分比，不是工作分钟数；仍受总周额度和模型限额约束。","These are quota percentages, not minutes of work. Overall weekly and model limits still apply."),12,false,muted)));
        root.Children.Add(Text(L.T($"本轮剩余 {L.Number(pace.Window.Remaining)}%",$"This window remaining {L.Number(pace.Window.Remaining)}%"),14,true));root.Children.Add(Progress(pace.Window.Remaining??0,accent));
    }
    void Insights(StackPanel root)
    {
        var h=store.Current;var pattern=new UsagePattern(h.Activity,store.Now);var fresh=h.ActivityFetchedAt is {} date&&(store.Now-date).TotalHours is >=0 and <6;
        var advice=ResetAdvice.Evaluate(h.Snapshot,pattern,fresh,store.Stale,store.Now);
        var stack=new StackPanel();stack.Children.Add(Text(L.T("Reset 助手","Reset advisor")+" · "+(h.Snapshot?.Credits?.AvailableCount is >=0?L.T($"{h.Snapshot.Credits.AvailableCount} 次可用",$"{h.Snapshot.Credits.AvailableCount} available"):L.T("数量未知","Count unknown")),16,true));stack.Children.Add(Text(advice.Title,15,true));stack.Children.Add(Text(advice.Reason,13,false,muted));
        var detail=new StackPanel();detail.Children.Add(Text(L.T($"近 30 天 · {pattern.Days.Count} 天记录 · {pattern.ActiveDays} 天活跃",$"Last 30 days · {pattern.Days.Count} recorded · {pattern.ActiveDays} active"),12));
        var chart=new UniformGridShim(30){Height=60,Margin=new Thickness(0,10,0,10)};var max=Math.Max(1,pattern.Days.Select(d=>d.Tokens).DefaultIfEmpty(0).Max());var today=DateOnly.FromDateTime(store.Now.UtcDateTime);
        for(var i=-30;i<0;i++){var key=today.AddDays(i).ToString("yyyy-MM-dd");var value=pattern.Days.FirstOrDefault(d=>d.StartDate==key);var bar=new Border{Background=value is null?muted:accent,Height=value is null?4:Math.Max(3,50*value.Tokens/max),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(1),ToolTip=key+" · "+(value is null?L.T("缺失记录","Missing record"):value.Tokens.ToString("N0")+" tokens")};chart.Children.Add(bar);}detail.Children.Add(chart);
        if(pattern.BusyWeekday is {} weekday)detail.Children.Add(Text(L.T("通常较忙：","Usually busier: ")+CultureInfo.GetCultureInfo(L.Chinese?"zh-CN":"en-US").DateTimeFormat.GetDayName(weekday),12));
        if(pattern.RecentRatio is {} ratio)detail.Children.Add(Text(L.T($"近 7 天日均 Token 活跃度为此前的 {ratio:0.0} 倍",$"Recent average daily token activity: {ratio:0.0}× the earlier period"),12));
        detail.Children.Add(Text(L.T("灰色表示缺失记录；Token 活跃度不等于额度百分比。只提供规则建议，不会自动使用 Reset。","Gray means missing data. Token activity is not quota percentage. Advice is rule-based; resets are never redeemed automatically."),12,false,muted));
        foreach(var c in h.Snapshot?.Credits?.Credits??[])if(c.Status=="available")detail.Children.Add(Text((c.Title??"Codex Reset")+" · "+L.Date(c.Expiry)+(c.Id==advice.PreferredCreditId?" ★":""),12));
        if(h.Snapshot?.Credits?.Credits is null)detail.Children.Add(Text(L.T("接口未提供券有效期明细。","Credit expiry details were not supplied."),12,false,muted));
        stack.Children.Add(Expand("history",L.T("近月模式与 Reset 明细","30-day patterns & reset details"),detail));root.Children.Add(Card(stack));
    }
    void Radar(StackPanel root)
    {
        var panel=new StackPanel();panel.Children.Add(Text(L.T("Tibo · Reset 动态","Tibo · Reset updates"),16,true));var a=store.Radar?.Assessment(store.Now,store.RadarFailed);
        panel.Children.Add(Text(!store.Settings.RadarEnabled?L.T("已关闭自动同步","Automatic sync is off"):a?.Title??L.T("等待公开消息","Waiting for public updates"),14,true));panel.Children.Add(Text(a?.Detail??L.T("公开消息不改变你的账户预算。","Public announcements do not change your account budget."),12,false,muted));
        panel.Children.Add(Button(L.T("查看 24 / 48 小时预测","View 24 / 48-hour forecast"),()=>ForecastWindow.Open()));
        var details=new StackPanel();foreach(var e in store.Radar?.Feed.Records.Take(8)??[]){details.Children.Add(Text(e.Label+" · "+e.Verification,12,true));details.Children.Add(Text(e.Precise?L.Date(e.Date):(e.AnnouncedAt??e.EffectiveAt??"")[..10]+L.T("（UTC 日期）"," (UTC date)"),12));if(e.Scope is {} scope)details.Children.Add(Text(scope[..Math.Min(scope.Length,160)],12,false,muted));if(ResetEvent.Evidence(e.SourceUrl) is {} url)details.Children.Add(Button(L.T("原始来源 ↗","Original source ↗"),()=>Native.OpenUrl(url.AbsoluteUri)));}
        details.Children.Add(Text(L.T($"本机读取：{L.Date(store.Radar?.FetchedAt)}",$"Fetched: {L.Date(store.Radar?.FetchedAt)}"),11,false,muted));details.Children.Add(Button(L.T("刷新动态","Refresh feed"),async()=>await store.RefreshRadar()));
        var enabled=new CheckBox{Content=L.T("自动同步公开 Reset 动态","Sync public reset updates automatically"),IsChecked=store.Settings.RadarEnabled,Margin=new Thickness(0,8,0,8),Foreground=foreground};enabled.Click+=async(_,_)=>{store.Settings.RadarEnabled=enabled.IsChecked==true;store.Save();store.Notify();if(store.Settings.RadarEnabled)await store.RefreshRadar();};details.Children.Add(enabled);
        panel.Children.Add(Expand("radar",L.T("记录与来源","Records & sources"),details));root.Children.Add(Card(panel));
    }
    void Settings(StackPanel root)
    {
        var panel=new StackPanel();panel.Children.Add(Text(L.T("每日首次同步固定预算，之后扣除当天已用额度；跨天或重置后重新分配。五小时窗口按剩余小时动态均摊。","Daily budgets are fixed at the first sync and reduced by observed usage. A new day or reset starts a new budget. Five-hour windows use a rolling hourly allowance."),12,false,muted));
        panel.Children.Add(Text(L.T("Codex 可执行文件（可留空自动查找）","Codex executable (empty = auto-detect)"),12));var exe=new TextBox{Text=store.Settings.CodexExecutable,Margin=new Thickness(0,4,0,6)};panel.Children.Add(exe);
        panel.Children.Add(Button(L.T("选择 codex.exe","Choose codex.exe"),()=>{var dialog=new OpenFileDialog{Filter="Executable (*.exe)|*.exe",CheckFileExists=true};if(dialog.ShowDialog()==true)exe.Text=dialog.FileName;}));
        panel.Children.Add(Text(L.T("WSL 发行版（可选；填写后使用该环境的 codex）","WSL distribution (optional; uses codex inside that environment)"),12));var wsl=new TextBox{Text=store.Settings.WslDistribution,Margin=new Thickness(0,4,0,6)};panel.Children.Add(wsl);
        panel.Children.Add(Button(L.T("保存连接设置","Save connection settings"),()=>{store.Settings.CodexExecutable=exe.Text.Trim();store.Settings.WslDistribution=wsl.Text.Trim();store.Save();store.Notify();}));
        var startup=new CheckBox{Content=L.T("登录 Windows 时启动","Launch at Windows sign-in"),IsChecked=Native.StartupEnabled,Foreground=foreground,Margin=new Thickness(0,10,0,10)};startup.Click+=(_,_)=>{try{Native.SetStartup(startup.IsChecked==true);}catch{startup.IsChecked=Native.StartupEnabled;MessageBox.Show(L.T("无法保存启动设置。","Could not save startup setting."));}};panel.Children.Add(startup);
        panel.Children.Add(Button(L.T("断开 Claude 并清除本工具登录会话","Disconnect Claude and clear this app's session"),async()=>await store.DisconnectClaude()));
        panel.Children.Add(Text(L.T("数据保存在本机。Claude 会话仅属于本工具；不会读取其他应用的凭据。","Data stays on this computer. The Claude session belongs to this app; other apps' credentials are not read."),12,false,muted));
        root.Children.Add(Expand("settings",L.T("设置与计算说明","Settings & calculation"),panel));
    }
    Expander Expand(string key,string title,UIElement content){var e=new Expander{Header=title,Content=content,IsExpanded=expanded.GetValueOrDefault(key),Foreground=foreground,Margin=new Thickness(0,12,0,0)};e.Expanded+=(_,_)=>expanded[key]=true;e.Collapsed+=(_,_)=>expanded[key]=false;return e;}
    TextBlock Text(string text,double size=14,bool bold=false,Brush? color=null)=>new(){Text=text,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=color??foreground,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,6)};
    Border Card(params UIElement[] content){var panel=new StackPanel();foreach(var child in content)panel.Children.Add(child);return new(){Background=card,CornerRadius=new CornerRadius(16),Padding=new Thickness(18),Margin=new Thickness(0,14,0,8),Child=panel};}
    Button Button(string title,Action action){var b=new Button{Content=title,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,4,7,4),Background=card,Foreground=foreground,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};b.Click+=(_,_)=>action();return b;}
    ProgressBar Progress(double value,Brush color)=>new(){Minimum=0,Maximum=100,Value=value,Height=7,Foreground=color,Margin=new Thickness(0,8,0,10)};
    static SolidColorBrush Brush(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    static bool HasOpenCombo(DependencyObject obj){if(obj is ComboBox c&&c.IsDropDownOpen||obj is TextBox t&&t.IsKeyboardFocusWithin)return true;for(var i=0;i<VisualTreeHelper.GetChildrenCount(obj);i++)if(HasOpenCombo(VisualTreeHelper.GetChild(obj,i)))return true;return false;}
}
public sealed class UniformGridShim : System.Windows.Controls.Primitives.UniformGrid { public UniformGridShim(int columns) { Columns=columns; } }
