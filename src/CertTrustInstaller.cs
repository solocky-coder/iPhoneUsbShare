using System;
using System.Security.Cryptography.X509Certificates;

namespace iPhoneUsbShare.Driver
{
    /// <summary>
    /// Establishes local trust for the self-signed cert that signs
    /// WinUsbControl.cat, so that when the app later calls
    /// pnputil /add-driver (or SetupCopyOEMInf / UpdateDriverForPlugAndPlayDevicesW),
    /// _VERIFY_FILE_SIGNATURE resolves the chain to a trusted root instead of
    /// failing with TRUST_E_NOSIGNATURE / SPAPI_E_DRIVER_STORE_ADD_FAILED.
    ///
    /// This must run BEFORE the driver-store import step, and requires the
    /// same elevated/admin context the app already needs for that step.
    ///
    /// The embedded resource "WinUsbControlSigning.cer" is the PUBLIC key
    /// only (produced by tools/New-SigningCert.ps1). Never ship the .pfx.
    /// </summary>
    public static class CertTrustInstaller
    {
        private const string EmbeddedCerResourceName =
            "iPhoneUsbShare.Driver.WinUsbControlSigning.cer";

        /// <summary>
        /// Imports the embedded driver-signing certificate into both
        /// Cert:\LocalMachine\Root and Cert:\LocalMachine\TrustedPublisher.
        /// Both stores are required: Root establishes the chain of trust,
        /// TrustedPublisher is what Windows actually checks for
        /// driver/catalog signature trust decisions (SmartScreen-adjacent
        /// "Publisher" trust, separate from CA trust).
        /// </summary>
        /// <returns>true if the cert ended up present (and trusted) in both stores.</returns>
        public static bool EnsureDriverSigningCertTrusted()
        {
            using var cert = LoadEmbeddedCertificate();

            bool rootOk = EnsureInStore(cert, StoreName.Root, StoreLocation.LocalMachine);
            bool pubOk = EnsureInStore(cert, StoreName.TrustedPublisher, StoreLocation.LocalMachine);

            return rootOk && pubOk;
        }

        /// <summary>
        /// Removes the cert from both stores. Call this from your uninstaller
        /// so you don't leave a trusted root behind on the user's machine
        /// after the app (and its driver) are gone.
        /// </summary>
        public static void RemoveDriverSigningCertTrust()
        {
            using var cert = LoadEmbeddedCertificate();

            RemoveFromStore(cert, StoreName.Root, StoreLocation.LocalMachine);
            RemoveFromStore(cert, StoreName.TrustedPublisher, StoreLocation.LocalMachine);
        }

        private static X509Certificate2 LoadEmbeddedCertificate()
        {
            var asm = typeof(CertTrustInstaller).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedCerResourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"Embedded resource '{EmbeddedCerResourceName}' not found. " +
                    "Make sure WinUsbControlSigning.cer is added as an EmbeddedResource " +
                    "with that logical name, and that it matches the cert used to sign " +
                    "WinUsbControl.cat.");
            }

            var bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = stream.Read(bytes, read, bytes.Length - read);
                if (n == 0) break;
                read += n;
            }

            return new X509Certificate2(bytes);
        }

        private static bool EnsureInStore(X509Certificate2 cert, StoreName storeName, StoreLocation location)
        {
            using var store = new X509Store(storeName, location);
            try
            {
                store.Open(OpenFlags.ReadWrite);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Almost always means "not running elevated". Surface this
                // clearly instead of letting the pnputil step fail later
                // with an opaque driver-store error.
                throw new InvalidOperationException(
                    $"Could not open certificate store {storeName}\\{location} for write. " +
                    "This step must run elevated (admin), same as the driver-store import step.",
                    ex);
            }

            using (store)
            {
                bool alreadyPresent = store.Certificates.Find(
                    X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;

                if (!alreadyPresent)
                {
                    store.Add(cert);
                }

                return store.Certificates.Find(
                    X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count > 0;
            }
        }

        private static void RemoveFromStore(X509Certificate2 cert, StoreName storeName, StoreLocation location)
        {
            using var store = new X509Store(storeName, location);
            try
            {
                store.Open(OpenFlags.ReadWrite);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Best-effort on uninstall; don't block the rest of cleanup.
                return;
            }

            using (store)
            {
                var matches = store.Certificates.Find(
                    X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false);
                foreach (var match in matches)
                {
                    store.Remove(match);
                }
            }
        }
    }
}

/*
 * Integration point in your existing installer flow:
 *
 *   if (!CertTrustInstaller.EnsureDriverSigningCertTrusted())
 *       throw new InvalidOperationException("Failed to establish trust for driver signing cert.");
 *
 *   // ...then, unchanged:
 *   RunPnputilAddDriver(pathToWinUsbControlInf);
 *
 * If you'd rather not embed a P/Invoke-free X509Store approach (e.g. you're
 * calling from a non-.NET installer, or want to avoid CAPI2 store-open
 * quirks in some sandboxed contexts), the equivalent shell-out is:
 *
 *   certutil -addstore Root "WinUsbControlSigning.cer"
 *   certutil -addstore TrustedPublisher "WinUsbControlSigning.cer"
 *
 * both of which also require an elevated process. Functionally identical;
 * X509Store above just avoids spawning certutil.exe.
 */
