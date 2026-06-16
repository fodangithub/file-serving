namespace FileSharingServer.Configuration;

public class AppSettings
{
    public ServerConfig Server { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public HttpsConfig Https { get; set; } = new();
}

public class ServerConfig
{
    public int Port { get; set; } = 8080;
    public string RootDirectory { get; set; } = "";
}

public class AuthConfig
{
    public List<UserConfig> Users { get; set; } = new();
}

public class UserConfig
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class HttpsConfig
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8443;
    public string Domain { get; set; } = "localhost";
    public string CertDirectory { get; set; } = "Certificates-SelfSigned";
}
