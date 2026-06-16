using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FileSharingServer.Services;

public static class CertificateGenerator
{
    public static void GenerateIfNeeded(string certDirectory, string domain)
    {
        if (!Directory.Exists(certDirectory))
            Directory.CreateDirectory(certDirectory);

        var serverCertPath = Path.Combine(certDirectory, "server.crt");
        var serverKeyPath = Path.Combine(certDirectory, "server.key");

        if (File.Exists(serverCertPath) && File.Exists(serverKeyPath))
            return;

        Generate(certDirectory, domain);
    }

    public static void Generate(string certDirectory, string domain)
    {
        if (!Directory.Exists(certDirectory))
            Directory.CreateDirectory(certDirectory);

        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddYears(5);

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=FileServer Self-Signed CA",
            caKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var caSerial = GenerateSerial();
        var caCert = caRequest.CreateSelfSigned(now, expiry);

        ExportPem(Path.Combine(certDirectory, "ca.crt"), "CERTIFICATE", caCert.RawData);
        ExportPem(Path.Combine(certDirectory, "ca.key"), "RSA PRIVATE KEY", caKey.ExportRSAPrivateKey());
        ExportPem(Path.Combine(certDirectory, "ca.pub"), "PUBLIC KEY", caKey.ExportSubjectPublicKeyInfo());

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest(
            $"CN={domain}",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(domain);
        if (domain == "localhost")
        {
            sanBuilder.AddDnsName("*.localhost");
            sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
            sanBuilder.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        }
        serverRequest.CertificateExtensions.Add(sanBuilder.Build());

        serverRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));

        var serverCert = serverRequest.Create(caCert, now, expiry, caSerial);
        var serverCertWithKey = serverCert.CopyWithPrivateKey(serverKey);

        ExportPem(Path.Combine(certDirectory, "server.crt"), "CERTIFICATE", serverCertWithKey.RawData);
        ExportPem(Path.Combine(certDirectory, "server.key"), "RSA PRIVATE KEY", serverKey.ExportRSAPrivateKey());
        ExportPem(Path.Combine(certDirectory, "server.pub"), "PUBLIC KEY", serverKey.ExportSubjectPublicKeyInfo());
    }

    public static X509Certificate2 LoadServerCertificate(string certDirectory)
    {
        var certPath = Path.Combine(certDirectory, "server.crt");
        var keyPath = Path.Combine(certDirectory, "server.key");

        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null);
    }

    private static byte[] GenerateSerial()
    {
        var serial = new byte[20];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        return serial;
    }

    internal static void ExportPem(string path, string label, byte[] data)
    {
        var pem = PemEncoding.Write(label, data);
        File.WriteAllText(path, new string(pem));
    }
}
