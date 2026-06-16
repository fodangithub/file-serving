using System.Security.Cryptography.X509Certificates;
using FileSharingServer.Services;

namespace FileSharingServer.Tests.Services;

public class CertificateGeneratorTests : IDisposable
{
    private readonly string _testDir;

    public CertificateGeneratorTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"certgen_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Generate_CreatesAllFiles()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var expectedFiles = new[]
        {
            "ca.crt", "ca.key", "ca.pub",
            "server.crt", "server.key", "server.pub"
        };

        foreach (var file in expectedFiles)
        {
            var path = Path.Combine(_testDir, file);
            Assert.True(File.Exists(path), $"Missing file: {file}");
            Assert.True(new FileInfo(path).Length > 0, $"Empty file: {file}");
        }
    }

    [Fact]
    public void Generate_CreatesDirectoryIfNeeded()
    {
        var subDir = Path.Combine(_testDir, "new_subdir");
        CertificateGenerator.Generate(subDir, "localhost");

        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(Path.Combine(subDir, "server.crt")));
    }

    [Fact]
    public void Generate_ServerCertIsValid()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var cert = CertificateGenerator.LoadServerCertificate(_testDir);
        Assert.Contains("localhost", cert.Subject);
        Assert.True(cert.NotAfter > DateTime.UtcNow.AddYears(4));
    }

    [Fact]
    public void Generate_ServerCertIsSignedByCA()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var caCert = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(_testDir, "ca.crt"));
        var serverCert = CertificateGenerator.LoadServerCertificate(_testDir);

        var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(caCert);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        var valid = chain.Build(serverCert);
        Assert.True(valid, "Server cert should be signed by CA");
    }

    [Fact]
    public void Generate_ServerCertHasSanForLocalhost()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var cert = CertificateGenerator.LoadServerCertificate(_testDir);
        var sanExtension = cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();

        Assert.NotNull(sanExtension);
        var dnsNames = sanExtension!.EnumerateDnsNames().ToList();
        Assert.Contains("localhost", dnsNames);
    }

    [Fact]
    public void Generate_CustomDomain_WorksCorrectly()
    {
        CertificateGenerator.Generate(_testDir, "myserver.local");

        var cert = CertificateGenerator.LoadServerCertificate(_testDir);
        Assert.Contains("myserver.local", cert.Subject);

        var sanExtension = cert.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();
        Assert.NotNull(sanExtension);
        Assert.Contains("myserver.local", sanExtension!.EnumerateDnsNames());
    }

    [Fact]
    public void GenerateIfNeeded_DoesNotOverwriteExisting()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var certPath = Path.Combine(_testDir, "server.crt");
        var originalContent = File.ReadAllText(certPath);

        CertificateGenerator.GenerateIfNeeded(_testDir, "localhost");

        var afterContent = File.ReadAllText(certPath);
        Assert.Equal(originalContent, afterContent);
    }

    [Fact]
    public void GenerateIfNeeded_CreatesWhenMissing()
    {
        CertificateGenerator.GenerateIfNeeded(_testDir, "localhost");

        Assert.True(File.Exists(Path.Combine(_testDir, "server.crt")));
        Assert.True(File.Exists(Path.Combine(_testDir, "server.key")));
    }

    [Fact]
    public void ExportPem_WritesCorrectFormat()
    {
        var path = Path.Combine(_testDir, "test.pem");
        CertificateGenerator.ExportPem(path, "TEST DATA", new byte[] { 1, 2, 3 });

        var content = File.ReadAllText(path);
        Assert.StartsWith("-----BEGIN TEST DATA-----", content);
        Assert.Contains("-----END TEST DATA-----", content);
    }

    [Fact]
    public void LoadServerCertificate_ReturnsValidCert()
    {
        CertificateGenerator.Generate(_testDir, "localhost");

        var cert = CertificateGenerator.LoadServerCertificate(_testDir);
        Assert.NotNull(cert);
        Assert.True(cert.HasPrivateKey);
    }
}
