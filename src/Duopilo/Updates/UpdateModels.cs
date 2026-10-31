using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Duopilo.Updates;

/// <summary>
/// Version nach SemVer ("0.10.0-beta.4"): drei Zahlen, optional ein Vorabzusatz. Vergleich nach SemVer 2.0: ohne Zusatz ist
/// neuer als mit ("0.10.0" &gt; "0.10.0-rc.1"), Zusätze teilweise numerisch ("beta.10" &gt; "beta.9"). Build-Metadaten ("+abc")
/// zählen nicht.
/// </summary>
public sealed record SemVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>"v0.10.0-beta.4", "0.10.0", "0.10.0-beta.4+abc" → Version; sonst null.</summary>
    public static SemVersion? Parse(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : "";
        var parts = core.Split('.');
        if (parts.Length != 3 || (dash >= 0 && pre.Length == 0))
        {
            return null;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        if (pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return null;
        }

        return new SemVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var c = (aNum, bNum) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>Eine Datei eines Releases.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>Ein Release auf GitHub (nur, was die Prüfung braucht).</summary>
public sealed record ReleaseInfo(string Tag, SemVersion Version, string Name, string PageUrl, bool Prerelease, bool Draft, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>Installer ("Duopilo-0.10.0-beta.4-Setup.exe").</summary>
    public ReleaseAsset? Setup => Assets.FirstOrDefault(a => a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Prüfsummen ("SHA256SUMS.txt" – Zeilen "hash  Dateiname").</summary>
    public ReleaseAsset? Checksums => Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reine Logik der Update-Prüfung (testbar ohne Netz).</summary>
public static class UpdateLogic
{
    public const string PrereleasesAuto = "auto";
    public const string PrereleasesOn = "on";
    public const string PrereleasesOff = "off";

    public static readonly IReadOnlyList<string> PrereleaseOptions = [PrereleasesAuto, PrereleasesOn, PrereleasesOff];

    /// <summary>Unbekannt oder leer = "auto".</summary>
    public static string NormalizePrereleases(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "an" or "ja" => PrereleasesOn,
        "off" or "false" or "no" or "aus" or "nein" => PrereleasesOff,
        _ => PrereleasesAuto,
    };

    /// <summary>Vorabversionen anbieten? "auto": wenn diese selbst eine ist (wer eine Beta fährt, bekommt die nächste).</summary>
    public static bool WantsPrereleases(string setting, SemVersion current) => NormalizePrereleases(setting) switch
    {
        PrereleasesOn => true,
        PrereleasesOff => false,
        _ => current.IsPrerelease,
    };

    /// <summary>Releases aus der Antwort von GET /repos/{owner}/{repo}/releases. Tags, die keine Version sind, fallen weg.</summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ReleaseInfo>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (SemVersion.Parse(tag) is not { } version)
            {
                continue;
            }

            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in a.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name is not null && url is not null)
                    {
                        assets.Add(new ReleaseAsset(name, url, asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0));
                    }
                }
            }

            list.Add(new ReleaseInfo(
                tag!,
                version,
                r.TryGetProperty("name", out var nm) && nm.GetString() is { Length: > 0 } title ? title : tag!,
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                r.TryGetProperty("prerelease", out var p) && p.GetBoolean(),
                r.TryGetProperty("draft", out var d) && d.GetBoolean(),
                assets));
        }

        return list;
    }

    /// <summary>Neueste veröffentlichte Version über <paramref name="current"/> (Entwürfe nie, Vorabversionen nach Einstellung) – sonst null.</summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, SemVersion current, bool prereleases) =>
        releases
            .Where(r => !r.Draft && (prereleases || (!r.Prerelease && !r.Version.IsPrerelease)) && r.Version > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

    /// <summary>SHA-256 einer Datei aus SHA256SUMS.txt ("hash  Dateiname", auch "hash *Dateiname") – null, wenn sie fehlt.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64)
            {
                continue;
            }

            var name = line[space..].Trim().TrimStart('*');
            var hash = line[..space];
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && hash.All(Uri.IsHexDigit))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// Schalter für den Installer: still mit Fortschrittsfenster, danach Duopilo starten (/LAUNCH=1, setup.iss). Installiert für
    /// alle Benutzer, wenn Duopilo unter „Programme“ liegt (fragt dann nach Adminrechten), sonst nur für diesen Benutzer.
    /// </summary>
    public static string InstallerArguments(string appDirectory, string? programFiles)
    {
        var allUsers = !string.IsNullOrEmpty(programFiles)
            && Path.GetFullPath(appDirectory).StartsWith(Path.GetFullPath(programFiles).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /LAUNCH=1 " + (allUsers ? "/ALLUSERS" : "/CURRENTUSER");
    }

    /// <summary>
    /// PowerShell-Befehl, der wartet, bis diese Instanz beendet ist (höchstens 60 s), übrige Duopilo-Prozesse beendet (die
    /// stdio-Instanz von Claude Desktop hält sonst die Dateien) und dann den Installer startet.
    /// </summary>
    public static string HelperCommand(int processId, string setupPath, string arguments, string processName)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        return $"Wait-Process -Id {processId} -Timeout 60 -ErrorAction SilentlyContinue; "
            + $"Get-Process -Name {Quote(processName)} -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue; "
            + "Start-Sleep -Milliseconds 500; "
            + $"Start-Process -FilePath {Quote(setupPath)} -ArgumentList {Quote(arguments)}";
    }

    /// <summary>Version dieser Duopilo.exe: die volle aus dem Tag ("0.10.0-beta.4"), ohne Build-Metadaten.</summary>
    public static SemVersion CurrentVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemVersion.Parse(informational)
            ?? SemVersion.Parse(assembly.GetName().Version?.ToString(3))
            ?? new SemVersion(0, 0, 0, "");
    }

    /// <summary>
    /// Von der Release-Pipeline gebaut (Installer/ZIP)? Selbst gebaute Stände (update.cmd) tragen die Version aus dem Projekt und
    /// werden mit update.cmd aktualisiert – dort keine Update-Prüfung.
    /// </summary>
    public static bool IsReleaseBuild(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "DuopiloReleaseBuild" && a.Value == "true");
}
