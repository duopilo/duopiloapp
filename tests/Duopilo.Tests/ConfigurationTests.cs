using Duopilo.Hosting;
using Duopilo.Remote;

namespace Duopilo.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5155", true)]
    [InlineData("http://localhost:5155", true)]
    [InlineData("http://[::1]:5155", true)]
    [InlineData("http://0.0.0.0:5155", false)]
    [InlineData("http://192.168.1.10:5155", false)]
    [InlineData("http://*:5155", false)]
    [InlineData("kein-url", false)]
    public void Nur_Loopback_ist_als_HttpUrl_erlaubt(string url, bool expected)
    {
        Assert.Equal(expected, DuopiloConfiguration.IsLoopbackUrl(url, out _));
    }

    [Theory]
    [InlineData("--stdio", DuopiloTransport.Stdio)]
    [InlineData("--http", DuopiloTransport.Http)]
    public void Kommandozeile_bestimmt_den_Transport(string arg, DuopiloTransport expected)
    {
        Assert.Equal(expected, DuopiloConfiguration.ResolveTransport([arg], AppContext.BaseDirectory));
    }

    [Theory]
    [InlineData("https://duopilo.example.com", true)]
    [InlineData("https://duopilo.example.com/", true)]
    [InlineData("http://duopilo.example.com", false)]
    [InlineData("https://duopilo.example.com/unterpfad", false)]
    [InlineData("", false)]
    public void PublicUrl_muss_https_ohne_Pfad_sein(string publicUrl, bool valid)
    {
        var options = new RemoteOptions { PublicUrl = publicUrl, ConsentPassword = "ein-langes-passwort" };

        Assert.Equal(valid, options.OAuthEnabled);
        if (valid)
        {
            Assert.Equal("https://duopilo.example.com/mcp", options.ResourceUrl);
        }
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://localhost:3118/callback", true)]
    [InlineData("http://127.0.0.1/callback", true)]
    [InlineData("https://claude.ai/anderer/pfad", false)]
    [InlineData("https://evil.example/callback", false)]
    [InlineData("http://localhost.evil.example/callback", false)]
    public void Nur_Claude_Callback_und_Loopback_sind_als_Redirect_erlaubt(string uri, bool expected)
    {
        Assert.Equal(expected, OAuthEndpoints.IsAllowedRedirectUri(uri));
    }

    [Fact]
    public void Kurzer_Bearer_Token_wird_ignoriert()
    {
        Assert.False(new RemoteOptions { BearerToken = "zu-kurz" }.HasBearerToken);
        Assert.True(new RemoteOptions { BearerToken = new string('x', RemoteOptions.MinBearerTokenLength) }.HasBearerToken);
    }

    [Fact]
    public void Secrets_erzeugen_unterschiedliche_url_sichere_Werte()
    {
        var a = Secrets.NewToken();
        var b = Secrets.NewToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }
}
