using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Osiris.Core;

/// <summary>What the workspace's packages bring to the agent.</summary>
public sealed record Extensions(IReadOnlyList<IToolProvider> Tools, IReadOnlyList<IGuard> Guards, IReadOnlyList<McpServerDefinition> McpServers)
{
    public static Extensions None { get; } = new([], [], []);
}

/// <summary>
/// The workspace's NuGet packages (<c>"packages"</c> in <c>.osiris/osiris.json</c>): each is downloaded once with its
/// dependencies, loaded next to the agent, and every public <see cref="IToolProvider"/>, <see cref="IGuard"/> and
/// <see cref="IMcpServerProvider"/> in it is created for the workspace. Loaded on first use and again when the file
/// changes; a package that fails is left out and named in the prompt.
/// <para>
/// Packages come from nuget.org and from k6labs' GitHub Packages, plus the workspace's own <c>"sources"</c> on the
/// user's machine. GitHub Packages wants a token even for public packages: <c>OSIRIS_PACKAGES_TOKEN</c> (or
/// <c>GITHUB_TOKEN</c>), or the credentials of the same source in the user's NuGet.Config.
/// </para>
/// </summary>
public sealed class Packages(Workspace workspace, AgentOptions host)
{
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    public const string K6Labs = "https://nuget.pkg.github.com/k6labs-cz/index.json";

    static readonly NuGetFramework Framework = NuGetFramework.Parse($"net{Environment.Version.Major}.0");
    // A package is loaded once per process: its types can't be unloaded while their tools may still run.
    static readonly ConcurrentDictionary<string, Lazy<Task<Assembly[]>>> Loaded = new(StringComparer.OrdinalIgnoreCase);

    readonly SemaphoreSlim gate = new(1, 1);
    Extensions? extensions;
    DateTime stamp;

    /// <summary>Packages that couldn't be used at the last load, with why.</summary>
    public IReadOnlyDictionary<string, string> Unavailable { get; private set; } = new Dictionary<string, string>();

    public string Prompt() => Unavailable.Count == 0 ? "" :
        $"Packages unavailable now (their tools are missing): {string.Join("; ", Unavailable.Select(u => $"{u.Key}: {u.Value}"))}";

    public async Task<Extensions> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = File.Exists(workspace.ConfigFile) ? File.GetLastWriteTimeUtc(workspace.ConfigFile) : default;
            if (extensions is not null && now == stamp) return extensions;

            var unavailable = new Dictionary<string, string>();
            List<IToolProvider> tools = [];
            List<IGuard> guards = [];
            List<McpServerDefinition> servers = [];
            try
            {
                var config = OsirisConfig.Load(workspace.ConfigFile);
                if (!host.Local && config.Sources.Count > 0) unavailable["sources"] = "A workspace's own package sources work only on the user's machine; the server's operator adds sources.";
                var sources = Sources(host.Local ? config.Sources : []);
                foreach (var package in config.Packages)
                {
                    try
                    {
                        if (!host.Allows(package.Id)) throw new InvalidOperationException("This server doesn't allow the package.");
                        var assemblies = await Loaded.GetOrAdd($"{package.Id}/{package.Version}", _ => new(() => InstallAsync(package, sources, host.PackagesDirectory))).Value.WaitAsync(ct);
                        var context = new OsirisContext(workspace, package.Settings, host.Local);
                        var found = assemblies.SelectMany(a => a.GetExportedTypes()).Where(t => t is { IsClass: true, IsAbstract: false }).ToList();
                        tools.AddRange(found.Where(typeof(IToolProvider).IsAssignableFrom).Select(t => (IToolProvider)Create(t, context)));
                        guards.AddRange(found.Where(typeof(IGuard).IsAssignableFrom).Select(t => (IGuard)Create(t, context)));
                        servers.AddRange(found.Where(typeof(IMcpServerProvider).IsAssignableFrom).SelectMany(t => ((IMcpServerProvider)Create(t, context)).GetServers()));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // A failed download may work next time: forget it, so the next load tries again.
                        Loaded.TryRemove($"{package.Id}/{package.Version}", out _);
                        unavailable[package.Id] = (e as TargetInvocationException)?.InnerException?.Message ?? e.Message;
                    }
                }
            }
            catch (ConfigurationException e)
            {
                unavailable["osiris.json"] = e.Message;
            }
            Unavailable = unavailable;
            stamp = now;
            return extensions = new Extensions(tools, guards, servers);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>An extension takes the <see cref="OsirisContext"/> in its constructor, or nothing.</summary>
    static object Create(Type type, OsirisContext context) =>
        type.GetConstructor([typeof(OsirisContext)]) is { } withContext ? withContext.Invoke([context])
        : Activator.CreateInstance(type) ?? throw new InvalidOperationException($"{type.Name} can't be created.");

    // ---- NuGet ----

    sealed record Source(string Url, SourceRepository Repository);

    /// <summary>The workspace's own sources first (a local folder with a package under development), then nuget.org and k6labs.</summary>
    static List<Source> Sources(IEnumerable<string> own)
    {
        var known = new PackageSourceProvider(Settings.LoadDefaultSettings(root: null)).LoadPackageSources().ToList();
        var token = new[] { "OSIRIS_PACKAGES_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" }.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        return [.. own.Append(NuGetOrg).Append(K6Labs).Distinct(StringComparer.OrdinalIgnoreCase).Select(url =>
        {
            var source = new PackageSource(url);
            // GitHub takes any user name with a token.
            if (token is not null && url.Contains("nuget.pkg.github.com", StringComparison.OrdinalIgnoreCase))
                source.Credentials = new PackageSourceCredential(url, "osiris", token, isPasswordClearText: true, validAuthenticationTypesText: null);
            else if (known.FirstOrDefault(k => string.Equals(k.Source.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Credentials is { } credentials)
                source.Credentials = credentials;
            return new Source(url, Repository.Factory.GetCoreV3(source));
        })];
    }

    /// <summary>k6labs' packages come from k6labs' feed (or the workspace's own sources), never from a namesake on nuget.org.</summary>
    static IEnumerable<Source> SourcesFor(string id, List<Source> sources) =>
        id.StartsWith("k6labs.", StringComparison.OrdinalIgnoreCase) ? sources.Where(s => s.Url != NuGetOrg) : sources;

    /// <summary>What the host has already (the Osiris API, Microsoft.Extensions.AI, …) is shared with the package, not loaded twice.</summary>
    static bool HostHas(string assemblyName)
    {
        try { return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(assemblyName)) is not null; }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { return false; }
    }

    static async Task<Assembly[]> InstallAsync(PackageReference package, List<Source> sources, string cacheDir)
    {
        using var cache = new SourceCacheContext();
        // The package and its dependencies, each at the lowest version that satisfies everyone (as NuGet restores).
        var closure = new Dictionary<string, (NuGetVersion Version, Source Source)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, VersionRange Range)>();
        queue.Enqueue((package.Id, NuGetVersion.TryParse(package.Version, out var exact) ? new VersionRange(exact, true, exact, true) : VersionRange.Parse(package.Version)));
        while (queue.TryDequeue(out var wanted))
        {
            if (closure.TryGetValue(wanted.Id, out var have) && wanted.Range.Satisfies(have.Version)) continue;
            if (!wanted.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase) && HostHas(wanted.Id)) continue;
            var (version, source, dependencies) = await FindAsync(wanted.Id, wanted.Range, sources, cache);
            closure[wanted.Id] = (version, source);
            foreach (var d in dependencies) queue.Enqueue((d.Id, d.VersionRange));
        }

        var dirs = new List<string>();
        foreach (var (id, (version, source)) in closure) dirs.Add(await ExtractAsync(id, version, source, cache, cacheDir));

        var context = new PackageLoadContext(package.Id, dirs);
        var rootDir = dirs[closure.Keys.ToList().FindIndex(k => k.Equals(package.Id, StringComparison.OrdinalIgnoreCase))];
        // An assembly the host has already is the host's: the same types on both sides.
        var assemblies = Directory.EnumerateFiles(rootDir, "*.dll")
            .Select(f => HostHas(Path.GetFileNameWithoutExtension(f)) ? AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(f))) : context.LoadFromAssemblyPath(f))
            .ToArray();
        return assemblies.Length > 0 ? assemblies : throw new InvalidOperationException($"The package has no assembly for {Framework.GetShortFolderName()}.");
    }

    static async Task<(NuGetVersion Version, Source Source, IEnumerable<NuGet.Packaging.Core.PackageDependency> Dependencies)> FindAsync(string id, VersionRange range, List<Source> sources, SourceCacheContext cache)
    {
        var problems = new List<string>();
        foreach (var source in SourcesFor(id, sources))
        {
            try
            {
                var find = await source.Repository.GetResourceAsync<FindPackageByIdResource>() ?? throw new FatalProtocolException("not a package source");
                var version = range.FindBestMatch(await find.GetAllVersionsAsync(id, cache, NullLogger.Instance, default));
                if (version is null) continue;
                var info = await find.GetDependencyInfoAsync(id, version, cache, NullLogger.Instance, default);
                var group = info is null ? null : NuGetFrameworkUtility.GetNearest(info.DependencyGroups, Framework, g => g.TargetFramework);
                return (version, source, group?.Packages ?? []);
            }
            catch (Exception e) when (e is NuGetProtocolException or HttpRequestException or IOException)
            {
                problems.Add($"{new Uri(source.Url, UriKind.RelativeOrAbsolute) switch { { IsAbsoluteUri: true, IsFile: false } u => u.Host, _ => source.Url }}: {e.Message.Split('\n')[0]}");
            }
        }
        throw new InvalidOperationException($"{id} {range.ToNormalizedString()} wasn't found." + (problems.Count > 0 ? $" ({string.Join("; ", problems)})" : ""));
    }

    /// <summary>Unpacks what runs here (the nearest framework's assemblies, this platform's native libraries) into the cache; returns the folder.</summary>
    static async Task<string> ExtractAsync(string id, NuGetVersion version, Source source, SourceCacheContext cache, string cacheDir)
    {
        var dir = Path.Combine(cacheDir, id.ToLowerInvariant(), version.ToNormalizedString());
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var done = Path.Combine(dir, ".extracted");
        if (File.Exists(done)) return dir;

        var temp = Directory.CreateDirectory($"{dir}.{Guid.NewGuid():N}.tmp").FullName;
        try
        {
            using var nupkg = new MemoryStream();
            var find = await source.Repository.GetResourceAsync<FindPackageByIdResource>() ?? throw new FatalProtocolException("not a package source");
            if (!await find.CopyNupkgToStreamAsync(id, version, nupkg, cache, NullLogger.Instance, default)) throw new InvalidOperationException($"{id} {version} couldn't be downloaded.");
            nupkg.Position = 0;
            using var reader = new PackageArchiveReader(nupkg);

            var files = new List<string>();
            if (NuGetFrameworkUtility.GetNearest(await reader.GetLibItemsAsync(default), Framework, g => g.TargetFramework) is { } lib) files.AddRange(lib.Items);
            var all = (await reader.GetFilesAsync(default)).ToList();
            foreach (var rid in RuntimeIds())
            {
                // The first runtime that has the file wins (linux-x64 before linux before unix).
                var managed = all.Where(f => f.StartsWith($"runtimes/{rid}/lib/", StringComparison.OrdinalIgnoreCase)).GroupBy(f => f.Split('/')[3])
                    .Select(g => new FrameworkSpecificGroup(NuGetFramework.ParseFolder(g.Key), g)).ToList();
                if (NuGetFrameworkUtility.GetNearest(managed, Framework, g => g.TargetFramework) is { } runtimeLib) files.InsertRange(0, runtimeLib.Items);
                files.AddRange(all.Where(f => f.StartsWith($"runtimes/{rid}/native/", StringComparison.OrdinalIgnoreCase)));
            }
            foreach (var file in files.Where(f => !f.EndsWith("/_._")).DistinctBy(Path.GetFileName))
                reader.ExtractFile(file, Path.Combine(temp, Path.GetFileName(file)), NullLogger.Instance);
            File.WriteAllText(Path.Combine(temp, ".extracted"), "");

            // Another process may have unpacked it meanwhile: theirs is as good.
            try { Directory.Move(temp, dir); }
            catch (IOException) when (File.Exists(done)) { }
            return dir;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    static IEnumerable<string> RuntimeIds()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return new[] { RuntimeInformation.RuntimeIdentifier, $"{os}-{arch}", os, OperatingSystem.IsWindows() ? null : "unix", "any" }.OfType<string>().Distinct();
    }

    /// <summary>A package's own assemblies and dependencies; whatever the host has comes from the host, so the Osiris API is one type for both.</summary>
    sealed class PackageLoadContext(string name, List<string> dirs) : AssemblyLoadContext(name)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (HostHas(assemblyName.Name!)) return null;
            return dirs.Select(d => Path.Combine(d, $"{assemblyName.Name}.dll")).FirstOrDefault(File.Exists) is { } file ? LoadFromAssemblyPath(file) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var names = new[] { unmanagedDllName, $"{unmanagedDllName}.dll", $"lib{unmanagedDllName}.so", $"lib{unmanagedDllName}.dylib" };
            return dirs.SelectMany(d => names.Select(n => Path.Combine(d, n))).FirstOrDefault(File.Exists) is { } file ? LoadUnmanagedDllFromPath(file) : IntPtr.Zero;
        }
    }
}
