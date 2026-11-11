using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Osiris.Core;

namespace Osiris.Tests;

public sealed class McpTests : IAsyncLifetime
{
    readonly string root = Directory.CreateTempSubdirectory("osiris-mcp").FullName;
    WebApplication? server;
    string url = "";

    [McpServerToolType]
    public sealed class EchoTools
    {
        [McpServerTool(Name = "echo"), Description("Says the text back.")]
        public static string Echo(string text) => $"echo: {text}";
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<EchoTools>();
        server = builder.Build();
        server.MapMcp("/mcp");
        server.MapGet("/echo", (Microsoft.AspNetCore.Http.HttpRequest r) => $"auth={r.Headers.Authorization}");
        server.MapGet("/moved", () => Microsoft.AspNetCore.Http.Results.Redirect("https://elsewhere.example/"));
        await server.StartAsync();
        url = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/mcp";
    }

    public async Task DisposeAsync()
    {
        await server!.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    void Configure(string json) => File.WriteAllText(Directory.CreateDirectory(Path.Combine(root, ".osiris")).FullName + "/osiris.json", json.Replace("URL", url));

    [Fact]
    public async Task Tools_of_a_server_are_offered_under_its_name_and_a_broken_one_is_left_out()
    {
        Configure("""{"mcp": {"demo": {"url": "URL"}, "gone": {"url": "http://127.0.0.1:1/mcp"}, "off": {"url": "URL", "enabled": false}}}""");
        await using var mcp = new Mcp(new Workspace(root), allowPrivate: true, new Secrets(new Workspace(root), CoreTests.Key));

        var tools = await mcp.ToolsAsync();
        var echo = Assert.IsAssignableFrom<AIFunction>(Assert.Single(tools));
        Assert.Equal("demo_echo", echo.Name);
        Assert.Contains("echo: hi", (await echo.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }))?.ToString());
        Assert.Equal(["gone"], mcp.Unavailable.Keys);
        Assert.Contains("gone:", mcp.Prompt());
        Assert.Same(tools, await mcp.ToolsAsync()); // kept until the file changes
    }

    [Fact]
    public async Task A_server_agent_refuses_local_and_plain_http_servers()
    {
        Configure("""{"mcp": {"demo": {"url": "URL"}}}""");
        await using var mcp = new Mcp(new Workspace(root), allowPrivate: false, new Secrets(new Workspace(root), CoreTests.Key));
        Assert.Empty(await mcp.ToolsAsync());
        Assert.Contains("https", mcp.Unavailable["demo"]);
    }

    string Base => url[..^"/mcp".Length].Replace("127.0.0.1", "localhost");

    [Fact]
    public async Task Http_request_fills_a_secret_for_its_host_and_masks_it_in_the_answer()
    {
        var secrets = new Secrets(new Workspace(root), CoreTests.Key);
        secrets.Put("TOKEN", "tok-123456", ["localhost"]);
        var http = new HttpTool(secrets, allowPrivate: true);

        var answer = await http.RequestAsync($"{Base}/echo", headers: new() { ["Authorization"] = "Bearer {{secret:TOKEN}}" });
        Assert.StartsWith("HTTP 200", answer);
        Assert.Contains("auth=Bearer [hidden]", answer); // the server got it, the model doesn't see it

        var moved = await http.RequestAsync($"{Base}/moved");
        Assert.Contains("HTTP 302", moved);
        Assert.Contains("Location: https://elsewhere.example/", moved); // not followed

        secrets.Put("OTHER", "other-secret", ["api.example"]);
        Assert.StartsWith("Refused: The secret OTHER isn't allowed", await http.RequestAsync($"{Base}/echo?k={{{{secret:OTHER}}}}"));
        Assert.StartsWith("Refused: The URL must use https", await new HttpTool(secrets, allowPrivate: false).RequestAsync($"{Base}/echo"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2a00:1450:4001::1", false)]
    public void Private_addresses_are_blocked(string address, bool blocked) =>
        Assert.Equal(blocked, SafeUrl.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public async Task Urls_with_credentials_or_private_hosts_are_refused()
    {
        await Assert.ThrowsAsync<UnsafeUrlException>(() => SafeUrl.CheckAsync("https://user:pw@example.com", false));
        await Assert.ThrowsAsync<UnsafeUrlException>(() => SafeUrl.CheckAsync("https://127.0.0.1/mcp", false));
        await Assert.ThrowsAsync<UnsafeUrlException>(() => SafeUrl.CheckAsync("ftp://example.com", true));
        Assert.Equal("http://localhost:5000/mcp", (await SafeUrl.CheckAsync("http://localhost:5000/mcp", true)).ToString());
    }
}
