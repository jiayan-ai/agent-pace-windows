using System.Drawing;
using Color=System.Drawing.Color;
using Pen=System.Drawing.Pen;
using System.Drawing.Drawing2D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms=System.Windows.Forms;
namespace AgentPace.Windows;
public sealed class App:Application
{
    Store? store;MainWindow? details;Forms.NotifyIcon? tray;System.Drawing.Icon? trayIcon;
    EventWaitHandle? activate;CancellationTokenSource? activationStop;
    readonly bool demo,smoke;
    string trayState="";
    public App(bool demo,bool smoke){this.demo=demo;this.smoke=smoke;ShutdownMode=ShutdownMode.OnExplicitShutdown;}
    [STAThread]public static int Main(string[] args)
    {
        var smoke=args.Contains("--smoke-test");var demo=smoke||args.Contains("--demo");
        using var mutex=new Mutex(true,"Local\\AgentPace.Windows"+(demo?".Demo":""),out var created);
        if(!created){try{using var signal=EventWaitHandle.OpenExisting("Local\\AgentPace.Windows.Activate");signal.Set();}catch{}return 0;}
        try{return new App(demo,smoke).Run();}finally{mutex.ReleaseMutex();}
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root=demo?Path.Combine(Path.GetTempPath(),"AgentPace-Demo-"+Guid.NewGuid().ToString("N")):null;
        store=new(root,demo);details=new(store);MainWindow=details;
        tray=new Forms.NotifyIcon{Visible=true};tray.MouseClick+=(_,args)=>{if(args.Button==Forms.MouseButtons.Left)Dispatcher.Invoke(()=>details.ToggleFromTray());};store.Changed+=UpdateTray;UpdateTray();
        SystemEvents.PowerModeChanged+=PowerModeChanged;
        if(!demo){activate=new(false,EventResetMode.AutoReset,"Local\\AgentPace.Windows.Activate");activationStop=new();_ = Task.Run(()=>{while(WaitHandle.WaitAny([activate,activationStop.Token.WaitHandle])==0)Dispatcher.BeginInvoke(()=>details.ShowDetails());});}
        if(!e.Args.Contains("--background"))details.ShowDetails();
        if(smoke){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Smoke();return;}
        if(!demo)await store.RefreshDue(true);
    }
    void PowerModeChanged(object sender,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Resume&&!demo)Dispatcher.BeginInvoke(async()=>{UpdateTray();if(store is not null)await store.RefreshDue(true);});}
    void UpdateTray()
    {
        if(tray is null||store is null)return;
        var summary=store.Summary;var tooltip=summary+"\n"+(store.Session?.Countdown??L.Date(store.Selected?.Window.ResetDate));tray.Text=tooltip.Length>127?tooltip[..127]:tooltip;
        var remaining=store.Selected?.Window.Remaining;var state=$"{store.Provider}/{Math.Round(remaining??-1)}/{store.Budget?.OverBudget>0}/{store.Stale}/{L.Language}";
        if(state!=trayState)
        {
            var color=store.Budget?.OverBudget>0?Color.IndianRed:store.Provider=="claude"?Color.FromArgb(188,105,70):Color.FromArgb(89,88,217);
            var icon=CreateIcon(remaining,color,store.Stale);tray.Icon=icon;trayIcon?.Dispose();trayIcon=icon;trayState=state;
            var menu=new Forms.ContextMenuStrip();menu.Items.Add(L.T("打开 Agent Pace","Open Agent Pace"),null,(_,_)=>details?.ShowDetails());menu.Items.Add(L.T("刷新额度","Refresh usage"),null,async(_,_)=>{if(!demo&&store is not null)await store.Refresh();});menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add("Codex",null,(_,_)=>store.SelectProvider("codex"));menu.Items.Add("Claude Code",null,(_,_)=>store.SelectProvider("claude"));menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add(L.T("退出","Quit"),null,(_,_)=>ExitApp());var old=tray.ContextMenuStrip;tray.ContextMenuStrip=menu;old?.Dispose();
        }
    }
    static Icon CreateIcon(double? remaining,Color color,bool stale)
    {
        using var bitmap=new Bitmap(32,32);using(var g=Graphics.FromImage(bitmap)){g.SmoothingMode=SmoothingMode.AntiAlias;using var track=new Pen(Color.FromArgb(140,140,140),4);using var pen=new Pen(color,4);g.DrawEllipse(track,4,4,24,24);if(remaining is {} r)g.DrawArc(pen,4,4,24,24,-90,(float)Math.Max(2,r*3.6));using var fill=new SolidBrush(color);g.FillEllipse(fill,12,12,8,8);if(stale){using var dot=new SolidBrush(Color.Goldenrod);g.FillEllipse(dot,23,0,8,8);}}
        var handle=bitmap.GetHicon();try{using var owned=Icon.FromHandle(handle);return (Icon)owned.Clone();}finally{Native.DestroyIcon(handle);}
    }
    async Task Smoke()
    {
        try
        {
            var output=Environment.GetEnvironmentVariable("AGENT_PACE_SMOKE_OUTPUT")??Path.Combine(Path.GetTempPath(),"AgentPace-Smoke");Directory.CreateDirectory(output);
            foreach(var language in new[]{"en","zh-Hans"})foreach(var provider in new[]{"codex","claude"})
            {
                store!.Language(language);store.SelectProvider(provider);if(provider=="claude")store.SelectWindow("claude/secondary");details!.ShowDetails();details.Render();details.UpdateLayout();await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
                var expected=provider=="claude"?(language=="en"?"Suggested hourly allowance":"接下来每小时建议可用"):(language=="en"?"Available today":"今天还可以用");
                if(!TextContent(details).Contains(expected))throw new InvalidOperationException("Localized screen missing: "+expected);
                var bitmap=new RenderTargetBitmap((int)details.ActualWidth,(int)details.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(details);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,$"{provider}-{language}.png"));encoder.Save(file);
            }
            for(var i=0;i<3;i++){details!.Hide();details.ToggleFromTray();if(!details.IsVisible)throw new InvalidOperationException("Tray panel did not open");details.Hide();}
            File.WriteAllText(Path.Combine(output,"result.txt"),"PASS WPF localized Codex/Claude rendering and three tray open/hide cycles; synthetic data only. Real account sign-in and physical DPI/wake behavior require device testing.");ExitApp(0);
        }
        catch(Exception ex){var output=Environment.GetEnvironmentVariable("AGENT_PACE_SMOKE_OUTPUT")??Path.GetTempPath();Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());ExitApp(1);}
    }
    static string TextContent(DependencyObject node){var text=node is TextBlock t?t.Text:"";for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)text+="\n"+TextContent(VisualTreeHelper.GetChild(node,i));return text;}
    public void ExitApp(int code=0){SystemEvents.PowerModeChanged-=PowerModeChanged;activationStop?.Cancel();if(tray is not null){tray.Visible=false;tray.ContextMenuStrip?.Dispose();tray.Dispose();}trayIcon?.Dispose();store?.Dispose();details?.Quit();Shutdown(code);}
}
