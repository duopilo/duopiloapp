using System.Net.Http.Headers;
using System.Text;
using Duopilo.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Duopilo.Tests;

/// <summary>Startet Duopilo im HTTP-Modus auf einem In-Memory-TestServer.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string PublicUrl = "https://duopilo.test";
    public const string ResourceUrl = PublicUrl + "/mcp";
    public const string BearerToken = "test-bearer-0123456789abcdefghijklmnopqrstuvwxyz";
    public const string Password = "richtiges-passwort-42";
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    private TestApp(WebApplication app) => App = app;

    public WebApplication App { get; }

    /// <param name="services">Dienste ersetzen (z. B. die Einstellungsdatei auf einen Temp-Pfad legen).</param>
    public static async Task<TestApp> StartAsync(string stateFile, Dictionary<string, string?>? overrides = null, Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Duopilo:Remote:PublicUrl"] = PublicUrl,
            ["Duopilo:Remote:BearerToken"] = BearerToken,
            ["Duopilo:Remote:ConsentPassword"] = Password,
            ["Duopilo:Remote:StateFile"] = stateFile,
            ["Duopilo:ReconnectSeconds"] = "60",

            // Eigener Datenordner, damit Tests nie den echten %LOCALAPPDATA%\Duopilo (z. B. input.json) verändern
            ["Duopilo:DataDirectory"] = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N")),
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var builder = HttpHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseTestServer();
        HttpHost.AddServices(builder);
        services?.Invoke(builder.Services);

        var app = builder.Build();
        HttpHost.Configure(app);
        await app.StartAsync();
        return new TestApp(app);
    }

    /// <summary>Client, der wie ein direkter lokaler Aufruf aussieht (Host localhost).</summary>
    public HttpClient Local() => App.GetTestServer().CreateClient();

    /// <summary>Client, der wie ein Aufruf über den Cloudflare Tunnel aussieht (öffentlicher Host + CF-Header).</summary>
    public HttpClient Remote()
    {
        var client = App.GetTestServer().CreateClient();
        client.BaseAddress = new Uri("http://duopilo.test/");
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "160.79.104.10");
        return client;
    }

    /// <summary>Sendet ein MCP-initialize an /mcp und gibt nur den Statuscode zurück.</summary>
    public static async Task<HttpResponseMessage> McpInitializeAsync(HttpClient client, string? bearer)
    {
        const string body = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"duopilo-tests","version":"1.0"}}}
            """;
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
