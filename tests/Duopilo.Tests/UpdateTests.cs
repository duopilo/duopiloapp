using System.Net;
using System.Text;
using Duopilo.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Duopilo.Tests;

/// <summary>Update-Prüfung: Versionen, Auswahl aus den GitHub-Releases, Prüfsummen, Installer-Aufruf.</summary>
public sealed class UpdateTests
{
    // ------------------------------------------------------------------ Versionen

    [Theory]
    [InlineData("v0.10.0-beta.4", 0, 10, 0, "beta.4")]
    [InlineData("0.10.0", 0, 10, 0, "")]
    [InlineData("1.2.3-rc.1+abc123", 1, 2, 3, "rc.1")]
    [InlineData("V2.0.0", 2, 0, 0, "")]
    public void Versionen_werden_gelesen(string text, int major, int minor, int patch, string pre)
    {
        Assert.Equal(new SemVersion(major, minor, patch, pre), SemVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.10")]
    [InlineData("0.10.0-")]
    [InlineData("latest")]
    [InlineData("v1.2.3.4")]
    [InlineData("1.2.x")]
    public void Keine_Version(string text)
    {
        Assert.Null(SemVersion.Parse(text));
    }

    [Theory]
    [InlineData("0.10.0-beta.10", "0.10.0-beta.9")]
    [InlineData("0.10.0", "0.10.0-rc.1")]
    [InlineData("0.10.0-rc.1", "0.10.0-beta.5")]
    [InlineData("0.10.1-beta.1", "0.10.0")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.10.0-beta.4.1", "0.10.0-beta.4")]
    [InlineData("0.10.0-beta", "0.10.0-alpha.9")]
    public void Neuer_ist_neuer(string newer, string older)
    {
        Assert.True(SemVersion.Parse(newer)! > SemVersion.Parse(older)!);
        Assert.True(SemVersion.Parse(older)! < SemVersion.Parse(newer)!);
    }

    [Fact]
    public void Build_Metadaten_zaehlen_nicht()
    {
        Assert.Equal(0, SemVersion.Parse("0.10.0-beta.4+abc")!.CompareTo(SemVersion.Parse("0.10.0-beta.4+def")));
        Assert.Equal("0.10.0-beta.4", SemVersion.Parse("v0.10.0-beta.4+abc")!.ToString());
    }

    [Theory]
    [InlineData("auto", "0.10.0-beta.4", true)]
    [InlineData("auto", "0.10.0", false)]
    [InlineData("on", "0.10.0", true)]
    [InlineData("off", "0.10.0-beta.4", false)]
    [InlineData("whatever", "0.10.0-beta.4", true)]
    public void Vorabversionen_nach_Einstellung(string setting, string current, bool expected)
    {
        Assert.Equal(expected, UpdateLogic.WantsPrereleases(setting, SemVersion.Parse(current)!));
    }

    // ------------------------------------------------------------------ Releases von GitHub

    private const string ReleasesJson = """
        [
          {"tag_name":"v0.10.0-beta.6","name":"Duopilo 0.10.0-beta.6","draft":true,"prerelease":true,"html_url":"https://github.com/duopilo/duopiloapp/releases/tag/untagged-1","assets":[]},
          {"tag_name":"v0.10.0-beta.5","name":"Duopilo 0.10.0-beta.5","draft":false,"prerelease":true,"html_url":"https://github.com/duopilo/duopiloapp/releases/tag/v0.10.0-beta.5",
           "assets":[
             {"name":"Duopilo-0.10.0-beta.5-Setup.exe","size":147000000,"browser_download_url":"https://github.com/duopilo/duopiloapp/releases/download/v0.10.0-beta.5/Duopilo-0.10.0-beta.5-Setup.exe"},
             {"name":"Duopilo-0.10.0-beta.5-win-x64.zip","size":203000000,"browser_download_url":"https://github.com/duopilo/duopiloapp/releases/download/v0.10.0-beta.5/Duopilo-0.10.0-beta.5-win-x64.zip"},
             {"name":"SHA256SUMS.txt","size":220,"browser_download_url":"https://github.com/duopilo/duopiloapp/releases/download/v0.10.0-beta.5/SHA256SUMS.txt"}
           ]},
          {"tag_name":"v0.9.1","name":"","draft":false,"prerelease":false,"html_url":"https://github.com/duopilo/duopiloapp/releases/tag/v0.9.1","assets":[]},
          {"tag_name":"nightly","name":"Nightly","draft":false,"prerelease":true,"html_url":"x","assets":[]}
        ]
        """;

    [Fact]
    public void Releases_werden_gelesen()
    {
        var releases = UpdateLogic.ParseReleases(ReleasesJson);

        Assert.Equal(3, releases.Count); // "nightly" ist keine Version
        var beta5 = releases.Single(r => r.Tag == "v0.10.0-beta.5");
        Assert.Equal("Duopilo-0.10.0-beta.5-Setup.exe", beta5.Setup!.Name);
        Assert.Equal(147000000, beta5.Setup.Size);
        Assert.Equal("SHA256SUMS.txt", beta5.Checksums!.Name);
        Assert.Equal("v0.9.1", releases.Single(r => r.Version.Major == 0 && r.Version.Minor == 9).Name);
    }

    [Fact]
    public void Neueste_Version_ohne_Entwuerfe_und_nach_Einstellung()
    {
        var releases = UpdateLogic.ParseReleases(ReleasesJson);
        var beta4 = SemVersion.Parse("0.10.0-beta.4")!;

        Assert.Equal("v0.10.0-beta.5", UpdateLogic.Newest(releases, beta4, prereleases: true)!.Tag);
        Assert.Null(UpdateLogic.Newest(releases, beta4, prereleases: false));
        Assert.Null(UpdateLogic.Newest(releases, SemVersion.Parse("0.10.0-beta.5")!, prereleases: true));
        Assert.Equal("v0.9.1", UpdateLogic.Newest(releases, SemVersion.Parse("0.9.0")!, prereleases: false)!.Tag);
    }

    [Fact]
    public void Pruefsumme_aus_SHA256SUMS()
    {
        var hash = new string('a', 64);
        var other = new string('b', 64);
        var sums = $"{other}  Duopilo-0.10.0-beta.5-win-x64.zip\n{hash.ToUpperInvariant()}  Duopilo-0.10.0-beta.5-Setup.exe\n";

        Assert.Equal(hash, UpdateLogic.ChecksumFor(sums, "Duopilo-0.10.0-beta.5-Setup.exe"));
        Assert.Equal(other, UpdateLogic.ChecksumFor(sums.Replace("  Duopilo-0.10.0-beta.5-win", " *Duopilo-0.10.0-beta.5-win", StringComparison.Ordinal), "Duopilo-0.10.0-beta.5-win-x64.zip"));
        Assert.Null(UpdateLogic.ChecksumFor(sums, "Duopilo-0.10.0-beta.6-Setup.exe"));
        Assert.Null(UpdateLogic.ChecksumFor("not a checksum file", "x"));
    }

    // ------------------------------------------------------------------ Installer

    [Fact]
    public void Installer_fuer_alle_oder_nur_diesen_Benutzer()
    {
        Assert.EndsWith("/ALLUSERS", UpdateLogic.InstallerArguments(@"C:\Program Files\Duopilo\", @"C:\Program Files"));
        Assert.EndsWith("/CURRENTUSER", UpdateLogic.InstallerArguments(@"C:\Users\pilot\AppData\Local\Programs\Duopilo\", @"C:\Program Files"));
        Assert.EndsWith("/CURRENTUSER", UpdateLogic.InstallerArguments(@"C:\Program Files (x86)\Duopilo\", @"C:\Program Files"));
        Assert.StartsWith("/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /LAUNCH=1", UpdateLogic.InstallerArguments(@"C:\Tools\Duopilo", null));
    }

    [Fact]
    public void Helfer_wartet_beendet_und_startet_den_Installer()
    {
        var command = UpdateLogic.HelperCommand(4711, @"C:\Users\o'brien\AppData\Local\Duopilo\updates\Duopilo-0.10.0-beta.5-Setup.exe", "/SILENT /LAUNCH=1", "Duopilo");

        Assert.StartsWith("Wait-Process -Id 4711 -Timeout 60", command);
        Assert.Contains("Get-Process -Name 'Duopilo'", command);
        Assert.Contains(@"-FilePath 'C:\Users\o''brien\AppData\Local\Duopilo\updates\Duopilo-0.10.0-beta.5-Setup.exe'", command);
        Assert.EndsWith("-ArgumentList '/SILENT /LAUNCH=1'", command);
    }

    // ------------------------------------------------------------------ Dienst

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopRequests { get; private set; }

        public void StopApplication() => StopRequests++;
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static UpdateService Service(HttpMessageHandler handler, string current, bool releaseBuild = true, string prereleases = "auto") =>
        new(
            Options.Create(new DuopiloOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "duopilo-tests", Guid.NewGuid().ToString("N")), Updates = new UpdateOptions { Prereleases = prereleases } }),
            new Lifetime(),
            NullLogger<UpdateService>.Instance,
            new HttpClient(handler),
            null,
            SemVersion.Parse(current),
            releaseBuild);

    [Fact]
    public async Task Beta_bekommt_die_naechste_Beta_gemeldet()
    {
        var handler = new Answer(HttpStatusCode.OK, ReleasesJson);
        using var service = Service(handler, "0.10.0-beta.4");
        UpdateStatus? found = null;
        service.UpdateFound += s => found = s;

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal("available", status.State);
        Assert.Equal("0.10.0-beta.5", status.Latest);
        Assert.True(status.Available);
        Assert.True(status.LatestIsPrerelease);
        Assert.Equal("https://github.com/duopilo/duopiloapp/releases/tag/v0.10.0-beta.5", status.PageUrl);
        Assert.NotNull(found);
        Assert.NotNull(status.LastCheckUtc);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/duopilo/duopiloapp/releases?per_page=30", request.RequestUri!.ToString());
        Assert.Contains("Duopilo", request.Headers.UserAgent.ToString());

        // Zweite Prüfung, gleiche Version: keine neue Meldung
        found = null;
        await service.CheckAsync(CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task Stabile_Version_bekommt_keine_Beta()
    {
        using var service = Service(new Answer(HttpStatusCode.OK, ReleasesJson), "0.9.1");

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal("upToDate", status.State);
        Assert.Null(status.Latest);
        Assert.False(status.Available);
    }

    [Fact]
    public async Task Fehler_bei_GitHub_wird_gemeldet()
    {
        using var service = Service(new Answer(HttpStatusCode.NotFound, """{"message":"Not Found"}"""), "0.10.0-beta.4");

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal("failed", status.State);
        Assert.Contains("404", status.Error);
        Assert.False(status.Available);
    }

    [Fact]
    public async Task Selbst_gebaut_prueft_nicht()
    {
        var handler = new Answer(HttpStatusCode.OK, ReleasesJson);
        using var service = Service(handler, "0.9.1", releaseBuild: false);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal("selfBuilt", status.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ausschalten_vergisst_das_gemeldete_Update()
    {
        using var service = Service(new Answer(HttpStatusCode.OK, ReleasesJson), "0.10.0-beta.4");
        await service.CheckAsync(CancellationToken.None);

        service.Configure(false, "auto");
        Assert.Equal("off", service.Status.State);
        Assert.False(service.Status.Available);
        Assert.False(service.Status.CheckEnabled);

        service.Configure(true, "off");
        Assert.Equal("idle", service.Status.State);
        Assert.Equal("off", service.Status.Prereleases);
    }

    [Fact]
    public async Task Ohne_Installation_per_Installer_nur_der_Link()
    {
        using var service = Service(new Answer(HttpStatusCode.OK, ReleasesJson), "0.10.0-beta.4");
        await service.CheckAsync(CancellationToken.None);

        // Im Testordner liegt kein unins000.exe: nicht installierbar, Installieren lehnt verständlich ab
        Assert.False(service.Status.Installable);
        Assert.NotNull(service.Install());
    }

    [Fact]
    public void Diese_Version_kommt_aus_der_Assembly()
    {
        var current = UpdateLogic.CurrentVersion(typeof(UpdateService).Assembly);

        Assert.True(current.Major > 0 || current.Minor > 0);
    }
}
