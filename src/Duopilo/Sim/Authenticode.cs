using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Duopilo.Sim;

/// <summary>
/// Prüft die digitale Signatur (Authenticode) einer Datei mit Windows (WinVerifyTrust) und liest den Unterzeichner – damit
/// Duopilo nur einen Installer startet, den wirklich Microsoft signiert hat.
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

    /// <summary>Unterzeichner (Subject des Zertifikats), null ohne Signatur.</summary>
    public static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // Signierte Dateien liest nur diese API
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Gültig von Microsoft signiert (Organisation "Microsoft Corporation" im Zertifikat)?</summary>
    public static bool IsMicrosoftSigned(string path) =>
        IsTrusted(path) && Signer(path) is { } subject && IsMicrosoftSubject(subject);

    /// <summary>"CN=Microsoft Corporation, O=Microsoft Corporation, …" → true.</summary>
    public static bool IsMicrosoftSubject(string subject) =>
        subject.Split(',').Select(p => p.Trim()).Any(p => p.Equals("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase));

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
