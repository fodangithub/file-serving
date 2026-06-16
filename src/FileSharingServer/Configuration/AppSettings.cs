namespace FileSharingServer.Configuration;

public class AppSettings
{
    public ServerConfig Server { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public HttpsConfig Https { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
}

public class ServerConfig
{
    public int Port { get; set; } = 8080;
    public string RootDirectory { get; set; } = "";
    public string Title { get; set; } = "File Serving";
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

public class SearchConfig
{
    /// <summary>Enable USN Journal-based file indexing for near-instant search.</summary>
    public bool EnableUsnIndex { get; set; } = true;

    /// <summary>Path to the LZ4-compressed cache file for persisting the index across restarts.</summary>
    public string CachePath { get; set; } = "usn-cache.data";

    /// <summary>How often (in seconds) to check the USN Journal for file changes.</summary>
    public int UpdateIntervalSeconds { get; set; } = 30;
}

public class RateLimitConfig
{
    /// <summary>Maximum failed login attempts before banning an IP.</summary>
    public int MaxFailedAttempts { get; set; } = 20;

    /// <summary>Time window (in minutes) for counting failed attempts.</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>Path to the YAML file storing the permanent ban list.</summary>
    public string BanlistPath { get; set; } = "banlist.yaml";
}

public class BanlistEntry
{
    public string Ip { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime BannedAt { get; set; } = DateTime.UtcNow;
}

public class BanlistConfig
{
    public List<BanlistEntry> BannedIps { get; set; } = new();
}
