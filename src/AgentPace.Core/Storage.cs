using System.Text.Json;
namespace AgentPace.Core;

public sealed class Settings
{
    public string Language { get; set; } = "system";
    public bool ClaudeEnabled
    {
        get; set;
    }
    public string ClaudeOrganization { get; set; } = "";
    public bool RadarEnabled { get; set; } = true;
    public bool AutoResetEnabled { get; set; }
    public string AutoResetAccountKey { get; set; } = "";
    public string CodexExecutable { get; set; } = "";
    public string WslDistribution { get; set; } = "";
}
public sealed class Storage(string root)
{
    public string Root { get; } = root;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentPace");
    public T Read<T>(string name) where T : new()
    {
        var path = Path.Combine(Root, name);
        if (!File.Exists(path))
            return new();
        if (new FileInfo(path).Length > 8_000_000)
            throw new IOException("State file too large");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options) ?? throw new IOException("Invalid state");
    }
    public bool AutoResetRevoked => File.Exists(Path.Combine(Root, "auto-reset-disabled"));
    public void RevokeAutoReset()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "auto-reset-disabled"), "disabled");
    }
    public void ClearAutoResetRevocation() => File.Delete(Path.Combine(Root, "auto-reset-disabled"));
    public FileStream AcquireAutoResetAuthorizationLock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "auto-reset-authorization.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public FileStream AcquireAutoResetLock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, "auto-reset.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void PreserveUnreadable(string name)
    {
        var path = Path.Combine(Root, name);
        if (File.Exists(path))
            File.Copy(path, path + ".unreadable-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
    }
    public void Write<T>(string name, T data)
    {
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json.Options));
        File.Move(temporary, path, true);
    }
}
