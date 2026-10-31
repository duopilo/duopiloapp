using System.Security.Cryptography.X509Certificates;
using Duopilo.Sim;

namespace Duopilo.Tests;

/// <summary>
/// SimConnect aus dem installierten MSFS-SDK statt aus dem Paket: Suchreihenfolge, Installer im ZIP, Ergebnis von msiexec und die
/// Prüfung, dass nur ein von Microsoft signierter Installer startet.
/// </summary>
public sealed class SimConnectSdkTests
{
    private const string App = @"C:\Program Files\Duopilo";

    private static Func<string, bool> Existing(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static string[] Sdk(string root) => [SimConnectLocator.ManagedIn(root), SimConnectLocator.NativeIn(root)];

    [Fact]
    public void Neben_der_exe_geht_vor()
    {
        var exists = Existing([Path.Combine(App, SimConnectLocator.ManagedFile), Path.Combine(App, SimConnectLocator.NativeFile), .. Sdk(SimConnectLocator.DefaultSdkRoot)]);

        var found = SimConnectLocator.Find(App, null, _ => null, exists);

        Assert.NotNull(found);
        Assert.Equal("app", found.Source);
        Assert.Null(found.SdkRoot);
    }

    [Fact]
    public void Nur_eine_DLL_neben_der_exe_reicht_nicht()
    {
        var exists = Existing([Path.Combine(App, SimConnectLocator.ManagedFile), .. Sdk(SimConnectLocator.DefaultSdkRoot)]);

        var found = SimConnectLocator.Find(App, null, _ => null, exists);

        Assert.Equal("sdk", found?.Source);
        Assert.Equal(SimConnectLocator.DefaultSdkRoot, found?.SdkRoot);
    }

    [Fact]
    public void Reihenfolge_Einstellung_Variable_Standardordner()
    {
        const string configured = @"D:\SDK\MSFS 2024 SDK";
        const string variable = @"E:\MSFS2024SDK";
        Func<string, string?> environment = name => name == SimConnectLocator.SdkVariable ? variable : null;

        var all = Existing([.. Sdk(configured), .. Sdk(variable), .. Sdk(SimConnectLocator.DefaultSdkRoot)]);
        Assert.Equal(configured, SimConnectLocator.Find(App, configured, environment, all)?.SdkRoot);

        var noConfigured = Existing([.. Sdk(variable), .. Sdk(SimConnectLocator.DefaultSdkRoot)]);
        Assert.Equal(variable, SimConnectLocator.Find(App, configured, environment, noConfigured)?.SdkRoot);

        var onlyDefault = Existing(Sdk(SimConnectLocator.DefaultSdkRoot));
        var found = SimConnectLocator.Find(App, configured, environment, onlyDefault);
        Assert.Equal(SimConnectLocator.DefaultSdkRoot, found?.SdkRoot);
        Assert.Equal(Path.Combine(SimConnectLocator.DefaultSdkRoot, "SimConnect SDK", "lib", "managed", "Microsoft.FlightSimulator.SimConnect.dll"), found?.Managed);
        Assert.Equal(Path.Combine(SimConnectLocator.DefaultSdkRoot, "SimConnect SDK", "lib", "SimConnect.dll"), found?.Native);
    }

    [Fact]
    public void SDK_ohne_native_DLL_zaehlt_nicht()
    {
        var exists = Existing(SimConnectLocator.ManagedIn(SimConnectLocator.DefaultSdkRoot));

        Assert.Null(SimConnectLocator.Find(App, null, _ => null, exists));
    }

    [Fact]
    public void SDK_Ordner_ohne_Doppelte_und_Anfuehrungszeichen()
    {
        var roots = SimConnectLocator.SdkRoots("  \"c:\\msfs 2024 sdk\"  ", _ => "");

        Assert.Equal(new[] { "c:\\msfs 2024 sdk" }, roots);
        Assert.Equal(new[] { SimConnectLocator.DefaultSdkRoot }, SimConnectLocator.SdkRoots(null, _ => null));
    }

    [Fact]
    public void IsSdkRoot_braucht_beide_DLLs()
    {
        var root = Path.Combine(Path.GetTempPath(), "duopilo-sdk-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SimConnectLocator.ManagedIn(root))!);
            File.WriteAllText(SimConnectLocator.ManagedIn(root), "x");
            Assert.False(SimConnectLocator.IsSdkRoot(root));

            File.WriteAllText(SimConnectLocator.NativeIn(root), "x");
            Assert.True(SimConnectLocator.IsSdkRoot(root));
            Assert.True(SimConnectLocator.IsSdkRoot($"\"{root}\""));
            Assert.False(SimConnectLocator.IsSdkRoot(""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, true, "installed")]
    [InlineData(3010, true, "installed")]
    [InlineData(0, false, "failed")]
    [InlineData(1602, false, "cancelled")]
    [InlineData(1603, false, "failed")]
    [InlineData(1603, true, "failed")]
    public void Ergebnis_des_Installers(int exitCode, bool found, string outcome) =>
        Assert.Equal(outcome, SdkInstaller.MsiOutcome(exitCode, found));

    [Fact]
    public void Installer_im_ZIP_bevorzugt_Core()
    {
        var root = Path.Combine(Path.GetTempPath(), "duopilo-sdk-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "extras"));
            File.WriteAllText(Path.Combine(root, "extras", "Samples.msi"), "x");
            File.WriteAllText(Path.Combine(root, "MSFS2024_SDK_Core_Installer_1.8.1.msi"), "x");
            File.WriteAllText(Path.Combine(root, "readme.txt"), "x");

            Assert.Equal("MSFS2024_SDK_Core_Installer_1.8.1.msi", Path.GetFileName(SdkInstaller.FindInstaller(root)));

            File.Delete(Path.Combine(root, "MSFS2024_SDK_Core_Installer_1.8.1.msi"));
            Assert.Equal("Samples.msi", Path.GetFileName(SdkInstaller.FindInstaller(root)));

            File.Delete(Path.Combine(root, "extras", "Samples.msi"));
            Assert.Null(SdkInstaller.FindInstaller(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Adresse_des_SDK_kommt_von_Microsoft()
    {
        var uri = new Uri(SdkInstaller.DefaultUrl);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("sdk.flightsimulator.com", uri.Host);
        Assert.EndsWith($"MSFS2024_SDK_Core_Installer_{SdkInstaller.Version}.zip", uri.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>Name wie in einem Zertifikat: CN, O, L, S, C (null = weglassen).</summary>
    private static X500DistinguishedName Name(string? cn, string? o, string? l = null, string? st = null, string? c = null)
    {
        var builder = new X500DistinguishedNameBuilder();
        foreach (var (value, add) in new (string?, Action<string>)[]
                 {
                     (c, v => builder.AddCountryOrRegion(v)),
                     (st, v => builder.AddStateOrProvinceName(v)),
                     (l, v => builder.AddLocalityName(v)),
                     (o, v => builder.AddOrganizationName(v)),
                     (cn, v => builder.AddCommonName(v)),
                 })
        {
            if (value is not null)
            {
                add(value);
            }
        }

        return builder.Build();
    }

    [Fact]
    public void Erlaubte_Unterzeichner()
    {
        Assert.True(Authenticode.IsMicrosoftSubject(Name("Microsoft Corporation", "Microsoft Corporation", "Redmond", "Washington", "US")));
        Assert.True(Authenticode.IsMicrosoftSubject(Name("Microsoft Windows", "Microsoft Corporation")));

        // So ist der Installer des SDK 1.8.1 signiert (Microsofts Identitätsprüfung, "Microsoft ID Verified CS EOC CA 03")
        Assert.True(Authenticode.IsMicrosoftSubject(Name("Microsoft Flight Simulator", "Microsoft Flight Simulator", "Redmond", "Washington", "US")));
    }

    [Fact]
    public void Andere_Unterzeichner_nicht()
    {
        Assert.False(Authenticode.IsMicrosoftSubject(Name("Microsoft Corporation", "Evil Corp", c: "US")));
        Assert.False(Authenticode.IsMicrosoftSubject(Name("Someone", "Microsoft Corporation Fake", c: "US")));
        Assert.False(Authenticode.IsMicrosoftSubject(Name("Microsoft Flight Simulator", null)));

        // MSFS-Team nur mit Ort, Bundesstaat und Land wie im echten Zertifikat
        Assert.False(Authenticode.IsMicrosoftSubject(Name("Microsoft Flight Simulator", "Microsoft Flight Simulator")));
        Assert.False(Authenticode.IsMicrosoftSubject(Name("Microsoft Flight Simulator", "Microsoft Flight Simulator", "Moskau", "Washington", "US")));

        // Text im CN, der wie eine Organisation aussieht, zählt nicht
        Assert.False(Authenticode.IsMicrosoftSubject(Name("x, O=Microsoft Corporation, y", "Evil Corp", c: "US")));
    }

    [Fact]
    public void Download_von_Microsoft_mit_Pruefsumme()
    {
        Assert.Matches("^[0-9a-f]{64}$", SdkInstaller.DefaultSha256);
        Assert.Equal(SdkInstaller.DefaultSha256, SdkInstaller.ExpectedSha256(SdkInstaller.DefaultUrl));

        // Eigene Adresse: keine Prüfsumme bekannt, dann zählt die Signatur
        Assert.Null(SdkInstaller.ExpectedSha256("https://example.com/MSFS2024_SDK_Core_Installer_1.8.1.zip"));
    }

    /// <summary>Eine unsignierte Datei (z. B. eine Fehlerseite statt des Installers) wird nie gestartet.</summary>
    [Fact]
    public void Unsignierte_Datei_ist_nicht_vertrauenswuerdig()
    {
        var file = Path.Combine(Path.GetTempPath(), "duopilo-sdk-tests-" + Guid.NewGuid().ToString("N") + ".msi");
        try
        {
            File.WriteAllText(file, "<html>not an installer</html>");

            Assert.False(Authenticode.IsTrusted(file));
            Assert.Null(Authenticode.Signer(file));
            Assert.False(Authenticode.IsMicrosoftSigned(file));
            Assert.False(Authenticode.IsTrusted(file + ".missing"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Ohne_Tray_Instanz_nur_der_Zustand_vom_Start()
    {
        var view = System.Text.Json.JsonSerializer.SerializeToElement(SimConnectEndpoints.View(null));

        Assert.Equal(SimConnectLocator.Current is not null, view.GetProperty("found").GetBoolean());
        Assert.False(view.GetProperty("canInstall").GetBoolean());
        Assert.Equal(SdkInstaller.Version, view.GetProperty("sdkVersion").GetString());
        Assert.Equal(SimConnectLocator.DefaultSdkRoot, view.GetProperty("defaultSdkRoot").GetString());
    }
}
