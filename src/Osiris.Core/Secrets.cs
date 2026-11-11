using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace Osiris.Core;

public sealed record SecretInfo(string Name, string[] AllowedHosts, string Description, DateTimeOffset Updated);

/// <summary>A secret that can't be used: unknown, not allowed for the host, or in the URL's host.</summary>
public sealed class SecretException(string message) : Exception(message);

/// <summary>
/// The workspace's secrets, one JSON file each in <c>.osiris/secrets/</c>, the value sealed (AES-GCM) with a key kept
/// outside the workspace, so the folder (and its git) never holds a value. A secret is used as <c>{{secret:NAME}}</c>
/// in a request (http_request, an MCP server's URL and headers) and filled only for its allowed hosts: the model never
/// sees the value, and answers have it masked.
/// </summary>
public sealed partial class Secrets(Workspace workspace, byte[] key)
{
    sealed record Stored(string ValueEnc, string[] AllowedHosts, string Description, DateTimeOffset Updated);

    readonly string dir = workspace.DataOf("secrets");

    public IReadOnlyList<SecretInfo> List() => Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.json")
            .Select(f => (Name: Path.GetFileNameWithoutExtension(f), Stored: Read(Path.GetFileNameWithoutExtension(f))))
            .Where(s => s.Stored is not null)
            .Select(s => new SecretInfo(s.Name, s.Stored!.AllowedHosts, s.Stored.Description, s.Stored.Updated))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList()
        : [];

    /// <summary>Creates or changes a secret; a null <paramref name="value"/> keeps the stored one (only hosts change).</summary>
    public void Put(string name, string? value, string[] allowedHosts, string description = "")
    {
        if (!Name().IsMatch(name)) throw new SecretException($"Invalid secret name \"{name}\": UPPER_CASE letters, digits and _.");
        var hosts = allowedHosts.Select(h => h.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (hosts.Length == 0) throw new SecretException("A secret needs at least one allowed host.");
        if (hosts.FirstOrDefault(h => !Host().IsMatch(h)) is { } bad) throw new SecretException($"Invalid host \"{bad}\".");
        var sealedValue = value is not null ? Seal(value) : Read(name)?.ValueEnc ?? throw new SecretException($"The secret {name} needs a value.");
        Directory.CreateDirectory(dir);
        File.WriteAllText(FileOf(name), JsonSerializer.Serialize(new Stored(sealedValue, hosts, description.Trim(), DateTimeOffset.UtcNow), AIJsonUtilities.DefaultOptions));
    }

    public bool Delete(string name)
    {
        if (!Name().IsMatch(name) || !File.Exists(FileOf(name))) return false;
        File.Delete(FileOf(name));
        return true;
    }

    /// <summary>
    /// Fills <c>{{secret:NAME}}</c> in the texts of one request to <paramref name="host"/>; returns them with the values
    /// used, for masking. Throws <see cref="SecretException"/> for an unknown secret or a host it isn't allowed for.
    /// </summary>
    public (Dictionary<string, string> Filled, List<string> Used) Fill(IReadOnlyDictionary<string, string> texts, string host)
    {
        var values = new Dictionary<string, string>();
        foreach (var name in texts.Values.SelectMany(t => Ref().Matches(t)).Select(m => m.Groups[1].Value).Distinct())
        {
            var stored = Read(name) ?? throw new SecretException($"There is no secret {name}.");
            if (!HostAllowed(host, stored.AllowedHosts)) throw new SecretException($"The secret {name} isn't allowed for {host}.");
            values[name] = Open(stored.ValueEnc, name);
        }
        var filled = texts.ToDictionary(t => t.Key, t => Ref().Replace(t.Value, m => values[m.Groups[1].Value]));
        return (filled, [.. values.Values]);
    }

    /// <summary>The host a URL with placeholders goes to; a placeholder may not decide the scheme or host, or a secret could pick where it's sent.</summary>
    public static string HostOf(string url)
    {
        if (!Uri.TryCreate(Ref().Replace(url, "SECRETREF"), UriKind.Absolute, out var parsed)) throw new SecretException($"Invalid URL: {url}");
        if ((parsed.Scheme + parsed.Authority).Contains("secretref", StringComparison.OrdinalIgnoreCase)) throw new SecretException("A secret can't be in the URL's scheme or host.");
        return parsed.Host.ToLowerInvariant();
    }

    /// <summary><c>example.com</c> matches exactly; <c>*.example.com</c> any subdomain (not the apex).</summary>
    public static bool HostAllowed(string host, IEnumerable<string> allowed) =>
        allowed.Any(a => a.StartsWith("*.") ? host.EndsWith(a[1..], StringComparison.OrdinalIgnoreCase) : string.Equals(host, a, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every used value masked, so no tool output or error shows it.</summary>
    public static string Redact(string text, IEnumerable<string> used) =>
        used.Where(v => v.Length >= 4).Aggregate(text, (t, v) => t.Replace(v, "[hidden]"));

    public string Prompt()
    {
        var list = List();
        if (list.Count == 0) return "";
        return $$"""
            # SECRETS
            Access keys for http_request and MCP servers. Use them only as the placeholder; you don't know the value and never write it.
            Each works only for its hosts.
            {{string.Join('\n', list.Select(s => $"- {{{{secret:{s.Name}}}}} for {string.Join(", ", s.AllowedHosts)}{(s.Description.Length > 0 ? $": {s.Description}" : "")}"))}}
            """;
    }

    Stored? Read(string name) => Name().IsMatch(name) && File.Exists(FileOf(name))
        ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FileOf(name)), AIJsonUtilities.DefaultOptions)
        : null;

    string FileOf(string name) => Path.Combine(dir, $"{name}.json");

    /// <summary>nonce | tag | ciphertext, in base64.</summary>
    string Seal(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var output = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16));
        return Convert.ToBase64String(output);
    }

    string Open(string sealedValue, string name)
    {
        try
        {
            var data = Convert.FromBase64String(sealedValue);
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or OverflowException)
        {
            throw new SecretException($"The secret {name} can't be opened (was the key changed?). Set it again.");
        }
    }

    [GeneratedRegex(@"\{\{\s*secret:([A-Z][A-Z0-9_]{0,63})\s*\}\}")]
    private static partial Regex Ref();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex Name();

    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}$|^localhost$")]
    private static partial Regex Host();
}
