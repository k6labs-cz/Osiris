using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Osiris.Core;
using Osiris.Web;

namespace Osiris.Tests;

/// <summary>The workspace's <c>.osiris/</c>: its marker, configuration, model and packages; and who may use it on a server.</summary>
public sealed class WorkspaceTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("osiris-test").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    void Configure(string json) => File.WriteAllText(new Workspace(root).Initialize().ConfigFile, json);

    AgentOptions Options(bool local, params string[] allowed) => new()
    {
        SecretKey = CoreTests.Key,
        Local = local,
        AllowedPackages = allowed,
        PackagesDirectory = Path.Combine(root, ".packages"),
        Model = ModelConfig.Default with { ApiKey = "host-key" },
    };

    /// <summary>The feed the build made of the database tools' package, and the version in it.</summary>
    static (string Feed, string Version) Feed()
    {
        var feed = Path.Combine(AppContext.BaseDirectory, "feed");
        var package = Path.GetFileNameWithoutExtension(Directory.GetFiles(feed, "k6labs.Osiris.Tools.Database.*.nupkg").Single());
        return (feed, package["k6labs.Osiris.Tools.Database.".Length..]);
    }

    [Fact]
    public void A_workspace_is_marked_by_its_osiris_folder_and_found_from_inside_like_a_git_repository()
    {
        Assert.Null(Workspace.Find(root));
        var workspace = new Workspace(root);
        Assert.False(workspace.Exists);

        workspace.Initialize();
        File.WriteAllText(workspace.ConfigFile, """{ "model": { "name": "kept" } }""");
        workspace.Initialize(); // again: nothing is overwritten

        Assert.True(workspace.Exists);
        Assert.Contains("kept", File.ReadAllText(workspace.ConfigFile));
        var ignored = File.ReadAllLines(workspace.DataOf(".gitignore"));
        Assert.Contains("threads/", ignored);
        Assert.Contains("run/", ignored);
        Assert.DoesNotContain("osiris.json", ignored);

        var deep = Directory.CreateDirectory(Path.Combine(root, "docs", "2026")).FullName;
        Assert.Equal(workspace.Root, Workspace.Find(deep)!.Root);
    }

    [Fact]
    public void Configuration_reads_model_packages_sources_and_servers_and_changes_one_section_only()
    {
        Configure("""
            {
              // comments and trailing commas are fine
              "model": { "endpoint": "https://api.openai.com/v1", "name": "gpt-5" },
              "packages": {
                "Acme.Tools": "1.2.3",
                "Acme.Crm": { "version": "2.0.0", "settings": { "region": "eu" } },
              },
              "sources": [ "https://nuget.example/index.json" ],
              "mcp": { "crm": { "url": "https://crm.example/mcp" } }
            }
            """);
        var file = new Workspace(root).ConfigFile;

        var config = OsirisConfig.Load(file);
        Assert.Equal("gpt-5", config.Model.Name);
        Assert.Equal("mistral-embed", config.Model.Over(ModelConfig.Default).Embedding); // the rest comes from the defaults
        Assert.Equal(["Acme.Tools", "Acme.Crm"], config.Packages.Select(p => p.Id));
        Assert.Equal("eu", config.Packages[1].Settings.GetProperty("region").GetString());
        Assert.Equal("https://nuget.example/index.json", Assert.Single(config.Sources));
        Assert.True(config.Mcp["crm"].Enabled);

        OsirisConfig.Update(file, "mcp", null);
        config = OsirisConfig.Load(file);
        Assert.Empty(config.Mcp);
        Assert.Equal(2, config.Packages.Count);

        File.WriteAllText(file, """{ "packages": { "Acme.Tools": {} } }""");
        Assert.Contains("needs a version", Assert.Throws<ConfigurationException>(() => OsirisConfig.Load(file)).Message);
    }

    [Theory]
    [InlineData("""{ "model": { "endpoint": "https://llm.example/v1" } }""", false, "its own key")]            // the host's key stays with the host's endpoint
    [InlineData("""{ "model": { "apiKey": "{{env:PATH}}" } }""", false, "only on your own machine")]            // a server's environment isn't a workspace's
    [InlineData("""{ "model": { "apiKey": "sk-plain-text" } }""", true, "Don't write")]                         // a key in the file would end in git
    [InlineData("""{ "model": { "apiKey": "{{secret:MISSING}}" } }""", true, "no secret MISSING")]
    public async Task The_models_key_never_leaks_between_workspace_and_host(string json, bool local, string expected)
    {
        Configure(json);
        var models = new Models(new Workspace(root), Options(local), new Secrets(new Workspace(root), CoreTests.Key));
        var e = await Assert.ThrowsAsync<ConfigurationException>(() => models.Chat.GetResponseAsync([new(ChatRole.User, "hi")]));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public async Task A_package_from_a_feed_brings_its_tools_and_prompt_to_the_agent()
    {
        var (feed, version) = Feed();
        Configure(JsonSerializer.Serialize(new { packages = new Dictionary<string, string> { ["k6labs.Osiris.Tools.Database"] = version }, sources = new[] { feed } }));
        var model = new CoreTests.ScriptedModel(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "db_create_table", new Dictionary<string, object?>
            {
                ["name"] = "contacts",
                ["columns"] = JsonSerializer.SerializeToElement(new[] { new { name = "name", type = "text" } }),
            })])),
            CoreTests.Says("Created."));
        await using var agent = new Agent(new Workspace(root), Options(local: true, "*"), model, new CoreTests.LetterEmbeddings());

        var events = new List<RunEvent>();
        await foreach (var e in agent.RunAsync(Guid.NewGuid(), "Make a contacts table")) events.Add(e);

        Assert.Empty(agent.Packages.Unavailable);
        Assert.Contains("\"success\":true", Assert.Single(events.OfType<ToolResult>()).Result);
        Assert.Contains(model.LastOptions!.Tools!, t => t.Name == "db_query");
        Assert.Contains("# DATABASE", model.LastOptions.Instructions);
        Assert.True(File.Exists(agent.Workspace.DataOf("database.db")));
    }

    [Fact]
    public async Task A_server_loads_only_the_packages_its_operator_allows_and_no_workspace_sources()
    {
        var (feed, version) = Feed();
        Configure(JsonSerializer.Serialize(new { packages = new Dictionary<string, string> { ["Evil.Tools"] = "1.0.0", ["k6labs.Osiris.Tools.Database"] = version }, sources = new[] { feed } }));
        var packages = new Packages(new Workspace(root), Options(local: false, "k6labs.Osiris.*"));

        var extensions = await packages.LoadAsync();

        Assert.Empty(extensions.Tools);
        Assert.Contains("doesn't allow", packages.Unavailable["Evil.Tools"]);
        Assert.Contains("only on the user's machine", packages.Unavailable["sources"]);
        Assert.Contains("Evil.Tools", packages.Prompt()); // the model can say why a tool is missing
    }

    [Fact]
    public void On_the_server_a_workspace_opens_to_the_roles_a_user_has_in_its_tenant()
    {
        var access = new AnubisAccess(Microsoft.Extensions.Options.Options.Create(new AuthOptions()));
        static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(roles.Select(r => new Claim(AnubisAccess.RolesClaim, r)), "test"));

        var user = User("acme:User");
        Assert.True(access.CanUse(user, "acme"));
        Assert.False(access.CanAdminister(user, "acme"));
        Assert.False(access.CanUse(user, "other"));                         // a role in another tenant

        var admin = User("acme:Administrator", "beta:User");
        Assert.True(access.CanUse(admin, "acme"));
        Assert.True(access.CanAdminister(admin, "acme"));
        Assert.True(access.CanUse(admin, "beta"));
        Assert.False(access.CanAdminister(admin, "beta"));                  // an administrator of one tenant only

        var everywhere = User("Administrator");                             // a role without a tenant holds in all
        Assert.True(access.CanAdminister(everywhere, "acme"));
        Assert.True(access.CanAdminister(everywhere, "other"));

        Assert.False(access.CanUse(User("acme:Auditor"), "acme"));          // another role of the application
        Assert.False(access.CanUse(User("acme-eu:User"), "acme"));          // a tenant that only starts the same
        Assert.False(access.CanUse(User(), "acme"));                        // signed in, no roles
    }
}
