using System.Windows.Threading;
namespace AgentPace.Windows;
public sealed class Store : IDisposable
{
    public event Action? Changed;
    public Settings Settings {get;private set;}=new();
    public History History {get;private set;}=new();
    public RadarSnapshot? Radar {get;private set;}
    public bool RadarFailed {get;private set;}
    public bool RadarBusy {get;private set;}
    public string? PersistenceError {get;private set;}
    public readonly Storage Storage;
    public ClaudeSession Claude {get;}
    readonly Dictionary<string,PaceException> errors=[];
    readonly bool demo;
    readonly HashSet<string> busy=[],verified=[];
    readonly Dictionary<string,DateTimeOffset> next=[],attempted=[];
    readonly CancellationTokenSource lifetime=new();
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(15)};
    DateTimeOffset radarNext=DateTimeOffset.MinValue,radarAttempt=DateTimeOffset.MinValue;
    public DateTimeOffset Now {get;private set;}=DateTimeOffset.Now;
    public string Provider=>History.SelectedProvider;
    public ProviderHistory Current=>History.For(Provider);
    public bool Busy=>busy.Contains(Provider);
    public bool Stale=>!verified.Contains(Provider)||errors.ContainsKey(Provider)||Current.Snapshot is not {} s||(Now-s.FetchedAt).TotalSeconds is <0 or >600;
    public PaceException? Error=>errors.GetValueOrDefault(Provider);
    public MeteredWindow? Selected=>Current.Snapshot?.Windows.FirstOrDefault(w=>w.Id==Current.SelectedId)??Current.Snapshot?.Windows.FirstOrDefault();
    public SessionPace? Session=>Provider=="claude"&&Selected?.Window.WindowDurationMins==300?new(Selected.Window,Now):null;
    public DailyBudget? Budget=>Selected is {} w&&w.Window.ResetDate>Now&&Current.Snapshot is {} s&&Current.Budgets.TryGetValue(s.AccountKey+"/"+w.Id,out var b)&&b.Day==TimeZoneInfo.ConvertTime(Now,TimeZoneInfo.Local).ToString("yyyy-MM-dd")&&b.TimeZoneId==TimeZoneInfo.Local.Id?b:null;
    public string Summary
    {
        get
        {
            var name=Selected?.BucketId=="claude_fable"?"Fable":Provider=="claude"?"Claude":"Codex";
            var mark=Stale?"≈":"";
            if(Session is {} session)return name+" "+(session.Allowance is {} a?$"{mark}{L.Number(a)}%"+(session.FinalHour?" · <1h":"/h"):L.T("待同步","Pending"));
            return name+" "+(Budget is {} b?$"{mark}{L.Number(b.LeftToday)}%":L.T("待同步","Pending"));
        }
    }
    public Store(string? root=null,bool demo=false)
    {
        this.demo=demo;Storage=new(root??Storage.DefaultRoot);
        try{Settings=Storage.Read<Settings>("settings.json");History=Storage.Read<History>("state.json");if(History.SelectedProvider is not("codex" or "claude"))History.SelectedProvider="codex";}catch{try{Storage.PreserveUnreadable("settings.json");Storage.PreserveUnreadable("state.json");}catch{}Settings=new();History=new();PersistenceError=L.T("本地记录无法读取，已尝试保留原文件备份。","Saved records could not be read; preserving a backup was attempted.");}
        L.Language=Settings.Language is "en" or "zh-Hans"?Settings.Language:"system";
        try{var path=Path.Combine(Storage.Root,"reset-radar.json");if(File.Exists(path)&&new FileInfo(path).Length<1_048_576)Radar=System.Text.Json.JsonSerializer.Deserialize<RadarSnapshot>(File.ReadAllText(path),Json.Options);}catch{}
        Claude=new(this);
        timer.Tick+=async(_,_)=>{Now=DateTimeOffset.Now;Notify();if(!demo)await RefreshDue();};
        timer.Start();
        if(demo)LoadDemo();
    }
    public void Notify()=>Changed?.Invoke();
    public void Save()
    {
        try{Storage.Write("settings.json",Settings);Storage.Write("state.json",History);PersistenceError=null;}
        catch{PersistenceError=L.T("本地记录无法保存；重启后可能重新计算预算。","Local records could not be saved; a restart may recalculate budgets.");}
    }
    public void Language(string language){Settings.Language=language;L.Language=language;Save();Claude.RefreshLanguage();ForecastWindow.UpdateLanguage();Notify();}
    public void SelectProvider(string provider){History.SelectedProvider=provider;Save();Notify();}
    public void SelectWindow(string id){Current.SelectedId=id;Save();Notify();}
    public async Task RefreshDue(bool force=false)
    {
        foreach(var p in new[]{"codex","claude"})
        {
            var state=History.For(p);var expired=p=="claude"&&state.Snapshot?.Windows.Any(w=>w.Window.ResetDate<=Now)==true;
            if(force||!next.TryGetValue(p,out var due)||Now>=due||expired&&errors.GetValueOrDefault(p)?.RetrySeconds is not >0&&attempted.GetValueOrDefault(p)<=Now.AddMinutes(-1)) _=Refresh(p);
        }
        if(Settings.RadarEnabled&&(force||Now>=radarNext))await RefreshRadar();
    }
    public async Task Refresh(string? provider=null)
    {
        provider??=Provider;
        if(demo)return;
        if(busy.Contains(provider)||provider=="claude"&&(!Settings.ClaudeEnabled||errors.GetValueOrDefault(provider)?.NeedsLogin==true))return;
        if(errors.GetValueOrDefault(provider)?.RetrySeconds>0&&next.GetValueOrDefault(provider)>DateTimeOffset.Now)return;
        busy.Add(provider);attempted[provider]=DateTimeOffset.Now;Notify();
        try
        {
            var snapshot=provider=="codex"?await CodexClient.Fetch(new(Settings.CodexExecutable,Settings.WslDistribution),lifetime.Token):await Claude.Fetch(lifetime.Token);
            if(provider=="claude"&&!Settings.ClaudeEnabled)return;
            History.For(provider).Ingest(snapshot);verified.Add(provider);errors.Remove(provider);next[provider]=DateTimeOffset.Now.AddMinutes(5);Save();
        }
        catch(OperationCanceledException){}
        catch(Exception ex){var error=ex as PaceException??PaceException.Network();errors[provider]=error;next[provider]=DateTimeOffset.Now.AddSeconds(Math.Max(60,error.RetrySeconds));}
        finally{busy.Remove(provider);Now=DateTimeOffset.Now;Notify();}
    }
    public async Task ConnectClaude()
    {
        if(demo)return;
        Settings.ClaudeEnabled=true;if(errors.GetValueOrDefault("claude")?.NeedsLogin==true)errors.Remove("claude");Save();
        try{await Claude.ShowLogin();await Refresh("claude");}catch{errors["claude"]=new("无法打开登录窗口。请安装 Microsoft Edge WebView2 Runtime 后重试。","Could not open sign-in. Install Microsoft Edge WebView2 Runtime and retry.",true);Notify();}
    }
    public async Task SyncClaude(){if(errors.GetValueOrDefault("claude")?.NeedsLogin==true)errors.Remove("claude");await Refresh("claude");}
    public async Task DisconnectClaude()
    {
        Settings.ClaudeEnabled=false;Settings.ClaudeOrganization="";verified.Remove("claude");errors.Remove("claude");History.Providers.Remove("claude");Save();Notify();
        try{await Claude.Disconnect();}catch{errors["claude"]=new("同步已停止，但登录会话清理失败，请重试断开。","Sync stopped, but session cleanup failed. Retry disconnecting.");Notify();}
    }
    public async Task RefreshRadar()
    {
        if(demo||!Settings.RadarEnabled||RadarBusy||DateTimeOffset.Now<radarAttempt.AddMinutes(1))return;
        RadarBusy=true;radarAttempt=DateTimeOffset.Now;Notify();
        try{var value=await RadarClient.Fetch(lifetime.Token);if(Settings.RadarEnabled){Radar=value;RadarFailed=false;Storage.Write("reset-radar.json",value);}}
        catch{RadarFailed=true;}finally{RadarBusy=false;radarNext=DateTimeOffset.Now.AddMinutes(15);Notify();}
    }
    public void LoadDemo()
    {
        var now=Now;var end=now.AddDays(6).ToUnixTimeSeconds();
        History.For("codex").Ingest(new("demo-codex",now,[new("codex","Codex","primary",new(17,10080,end))],new(2,[])));
        History.For("claude").Ingest(new("demo-claude",now,[new("claude","Claude","primary",new(33,10080,end)),new("claude","Claude","secondary",new(40,300,now.AddHours(3).ToUnixTimeSeconds())),new("claude_fable","Claude Fable","primary",new(42,10080,end))]));
        verified.UnionWith(["codex","claude"]);
    }
    public void Dispose(){timer.Stop();lifetime.Cancel();Claude.Dispose();lifetime.Dispose();}
}
