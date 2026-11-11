using System.Security.Cryptography;

namespace Osiris.Core;

/// <summary>
/// What the app hosting agents (CLI, desktop, server) decides for all its workspaces; everything else comes from
/// each workspace's <c>.osiris/osiris.json</c>.
/// </summary>
public sealed record AgentOptions
{
    /// <summary>The model used where a workspace doesn't configure its own, with the host's API key in plain text.</summary>
    public ModelConfig Model { get; init; } = ModelConfig.Default;

    /// <summary>The 32-byte key sealing the workspaces' secrets, kept outside them.</summary>
    public required byte[] SecretKey { get; init; }

    /// <summary>
    /// True on the user's own machine. Lets MCP servers and http_request reach localhost and private networks,
    /// <c>{{env:NAME}}</c> read the environment, and packages come from local folders and any id.
    /// </summary>
    public bool Local { get; init; }

    /// <summary>
    /// The package ids a workspace may load (<c>*</c> at the end matches a prefix). A package is code running inside
    /// the host, so a shared server takes only what its operator trusts.
    /// </summary>
    public IReadOnlyList<string> AllowedPackages { get; init; } = ["k6labs.Osiris.*"];

    /// <summary>Where downloaded packages are unpacked, shared by the host's workspaces.</summary>
    public string PackagesDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osiris", "packages");

    /// <summary>The user's configuration folder: <c>~/.config/osiris</c> (<c>%AppData%\osiris</c>).</summary>
    public static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osiris");

    /// <summary>
    /// The options of a single-user app: the model from the user's <c>osiris.json</c>, the API key from it or from
    /// <c>OSIRIS_API_KEY</c> / <c>MISTRAL_API_KEY</c> / <c>OPENAI_API_KEY</c>, any package allowed.
    /// </summary>
    public static AgentOptions ForUser()
    {
        var model = OsirisConfig.Load(Path.Combine(UserDirectory, "osiris.json")).Model.Over(ModelConfig.Default);
        return new AgentOptions
        {
            Model = model with { ApiKey = model.ApiKey ?? EnvironmentKey() },
            SecretKey = LoadSecretKey(null),
            Local = true,
            AllowedPackages = ["*"],
        };
    }

    public static string? EnvironmentKey() =>
        new[] { "OSIRIS_API_KEY", "MISTRAL_API_KEY", "OPENAI_API_KEY" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>
    /// The key sealing secrets: <paramref name="configured"/> (base64, 32 bytes), else one made once in the user's
    /// configuration folder, readable by them only. Outside every workspace, so a workspace (and its git) never opens its secrets.
    /// </summary>
    public static byte[] LoadSecretKey(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return Convert.FromBase64String(configured);
        var file = Path.Combine(UserDirectory, "secret.key");
        if (File.Exists(file)) return Convert.FromBase64String(File.ReadAllText(file).Trim());
        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(UserDirectory);
        File.WriteAllText(file, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    public bool Allows(string packageId) => AllowedPackages.Any(p =>
        p.EndsWith('*') ? packageId.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : packageId.Equals(p, StringComparison.OrdinalIgnoreCase));
}
