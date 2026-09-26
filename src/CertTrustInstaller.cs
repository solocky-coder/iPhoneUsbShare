using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;

namespace iPhoneUsbShare.Driver;

public static class CertTrustInstaller
{
    private const string EmbeddedCertificateResource = "iPhoneUsbShare.Driver.WinUsbControlSigning.cer";

    public static void EnsureDriverSigningCertTrusted()
    {
        using var certificate = LoadEmbeddedCertificate();
        AddIfMissing(StoreLocation.LocalMachine, StoreName.Root, certificate);
        AddIfMissing(StoreLocation.LocalMachine, StoreName.TrustedPublisher, certificate);
    }

    public static void RemoveDriverSigningCertTrust()
    {
        using var certificate = LoadEmbeddedCertificate();
        RemoveMatching(StoreLocation.LocalMachine, StoreName.Root, certificate);
        RemoveMatching(StoreLocation.LocalMachine, StoreName.TrustedPublisher, certificate);
    }

    private static X509Certificate2 LoadEmbeddedCertificate()
    {
        var assembly = typeof(CertTrustInstaller).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedCertificateResource);
        if (stream is null) throw new InvalidOperationException($"Embedded driver-signing certificate resource was not found: {EmbeddedCertificateResource}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new X509Certificate2(memory.ToArray());
    }

    private static void AddIfMissing(StoreLocation location, StoreName name, X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            if (matches.Count == 0) store.Add(certificate);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Administrator elevation is required to trust the driver-signing certificate.", ex);
        }
    }

    private static void RemoveMatching(StoreLocation location, StoreName name, X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(name, location);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false);
            foreach (var match in matches) store.Remove(match);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Administrator elevation is required to remove the driver-signing certificate.", ex);
        }
    }
}