using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Duopilo.Sim;

/// <summary>
/// Prüft die digitale Signatur (Authenticode) einer Datei mit Windows (WinVerifyTrust) und liest den Unterzeichner – damit
/// Duopilo einen Installer von einer eigenen Adresse nur startet, wenn Microsoft oder das MSFS-Team ihn signiert hat.
/// </summary>
public static class Authenticode
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Gültig signiert (Kette bis zu einer vertrauenswürdigen Wurzel)? Nur Windows; sonst false.</summary>
    public static bool IsTrusted(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo
        {
            CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            PcwszFilePath = pathPtr,
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                DwUiChoice = WtdUiNone,
                FdwRevocationChecks = WtdRevokeNone,
                DwUnionChoice = WtdChoiceFile,
                PFile = filePtr,
                DwStateAction = WtdStateActionIgnore,
            };
            Marshal.StructureToPtr(data, dataPtr, false);
            return WinVerifyTrust(-1, GenericVerifyV2, dataPtr) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(dataPtr);
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    /// <summary>
    /// Erlaubte Unterzeichner: Microsoft selbst oder das MSFS-Team. Dessen Installer (SDK 1.8.1) trägt
    /// "CN=Microsoft Flight Simulator, O=Microsoft Flight Simulator, L=Redmond, S=Washington, C=US", ausgestellt von Microsofts
    /// Identitätsprüfung ("Microsoft ID Verified CS EOC CA 03"). Diese CA stellt auch anderen geprüften Firmen Zertifikate aus –
    /// deshalb zählt die Organisation samt Ort, nicht der Aussteller.
    /// </summary>
    private static readonly (string Organization, string? Locality, string? State, string? Country)[] AllowedSigners =
    [
        ("Microsoft Corporation", null, null, null),
        ("Microsoft Flight Simulator", "Redmond", "Washington", "US"),
    ];

    private const string OidOrganization = "2.5.4.10";
    private const string OidLocality = "2.5.4.7";
    private const string OidState = "2.5.4.8";
    private const string OidCountry = "2.5.4.6";

    /// <summary>Unterzeichner (Name aus dem Zertifikat), null ohne Signatur.</summary>
    public static X500DistinguishedName? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Signierte Dateien liest nur diese API
            using var cert = X509Certificate.CreateFromSignedFile(path);
            using var cert2 = new X509Certificate2(cert);
#pragma warning restore SYSLIB0057
            return new X500DistinguishedName(cert2.SubjectName);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Unterzeichner als Text ("CN=…, O=…"), null ohne Signatur.</summary>
    public static string? Signer(string path) => SignerName(path)?.Name;

    /// <summary>Gültig signiert von einem erlaubten Unterzeichner (<see cref="AllowedSigners"/>)?</summary>
    public static bool IsMicrosoftSigned(string path) =>
        IsTrusted(path) && SignerName(path) is { } name && IsMicrosoftSubject(name);

    /// <summary>
    /// Ist der Name einer der erlaubten Unterzeichner? Liest die Bestandteile einzeln aus dem Zertifikat statt den Text zu zerlegen –
    /// sonst könnte ein CN wie "x, O=Microsoft Corporation" täuschen.
    /// </summary>
    public static bool IsMicrosoftSubject(X500DistinguishedName name)
    {
        var parts = new Dictionary<string, List<string>>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements)
            {
                continue;
            }

            var oid = rdn.GetSingleElementType().Value ?? "";
            if (!parts.TryGetValue(oid, out var values))
            {
                parts[oid] = values = [];
            }

            values.Add(rdn.GetSingleElementValue() ?? "");
        }

        // Genau ein Wert je Bestandteil, sonst nicht eindeutig
        string? One(string oid) => parts.TryGetValue(oid, out var v) && v.Count == 1 ? v[0] : null;
        var organization = One(OidOrganization);
        return organization is not null && AllowedSigners.Any(a =>
            organization == a.Organization
            && (a.Locality is null || One(OidLocality) == a.Locality)
            && (a.State is null || One(OidState) == a.State)
            && (a.Country is null || One(OidCountry) == a.Country));
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionId, nint pWvtData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public nint PcwszFilePath;
        public nint HFile;
        public nint PgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public nint PPolicyCallbackData;
        public nint PSipClientData;
        public uint DwUiChoice;
        public uint FdwRevocationChecks;
        public uint DwUnionChoice;
        public nint PFile;
        public uint DwStateAction;
        public nint HWvtStateData;
        public nint PwszUrlReference;
        public uint DwProvFlags;
        public uint DwUiContext;
        public nint PSignatureSettings;
    }
}
