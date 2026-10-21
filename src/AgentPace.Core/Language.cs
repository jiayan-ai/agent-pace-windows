using System.Globalization;
namespace AgentPace.Core;
public static class L
{
    public static string Language { get; set; } = "system";
    public static bool Chinese => Language == "zh-Hans" || (Language == "system" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh");
    public static string T(string zh, string en) => Chinese ? zh : en;
    public static string Number(double? value) => value is { } n && double.IsFinite(n) ? n.ToString("0.0", CultureInfo.InvariantCulture) : "—";
    public static string Date(DateTimeOffset? value) => value?.ToLocalTime().ToString(Chinese ? "M 月 d 日 HH:mm" : "MMM d, HH:mm", CultureInfo.GetCultureInfo(Chinese ? "zh-CN" : "en-US")) ?? T("未知", "Unknown");
}
public sealed class PaceException(string zh, string en, bool needsLogin = false, int retrySeconds = 0) : Exception()
{
    public override string Message => L.T(zh, en);
    public bool NeedsLogin { get; } = needsLogin;
    public int RetrySeconds { get; } = retrySeconds;
    public static PaceException Network() => new("额度同步失败，请检查网络后重试。", "Usage sync failed. Check your connection and retry.");
    public static PaceException Invalid() => new("暂时无法识别额度数据。", "Could not read quota data.");
    public static PaceException Login() => new("请先登录订阅账户；API Key 不提供订阅额度。", "Sign in to a subscription account first; API keys do not provide subscription quota.", true);
}
