using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EnterpriseCaseIntelligence.Api.Security;

// Also source-linked into the standalone operator probe: no API/store dependency.
internal static class RetainedProtection
{
    public static IDataProtectionProvider Create(string directory, string applicationName, X509Certificate2 certificate, int? expectedKeyCount = null, bool readOnly = false)
        => DataProtectionProvider.Create(new DirectoryInfo(directory), protection =>
        {
            protection.SetApplicationName(applicationName).ProtectKeysWithCertificate(certificate);
            if (readOnly) protection.DisableAutomaticKeyGeneration();
            // A round trip alone can generate a replacement key when a retained
            // descriptor is unreadable. Validate all descriptors before generation.
            protection.Services.Replace(ServiceDescriptor.Singleton<IKeyManager>(services =>
            {
                var manager = ActivatorUtilities.CreateInstance<XmlKeyManager>(services);
                var keys = manager.GetAllKeys();
                if (expectedKeyCount is { } expected && keys.Count != expected) throw new System.Security.Cryptography.CryptographicException();
                foreach (var key in keys) _ = key.Descriptor;
                return manager;
            }));
        });
}
