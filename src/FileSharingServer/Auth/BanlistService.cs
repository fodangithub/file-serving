using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FileSharingServer.Configuration;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FileSharingServer.Services;

/// <summary>
/// Tracks failed login attempts per IP and maintains a permanent ban list.
///
/// - IPs exceeding <see cref="RateLimitConfig.MaxFailedAttempts"/> failures within
///   <see cref="RateLimitConfig.WindowMinutes"/> are permanently banned.
/// - The ban list is persisted to a YAML file and reloaded automatically on file change.
/// </summary>
public sealed class BanlistService : IDisposable
{
    private readonly RateLimitConfig _config;
    private readonly string _banlistPath;
    private readonly ILogger<BanlistService> _logger;
    private readonly FileSystemWatcher? _watcher;

    // Banned IPs — loaded from YAML, augmented at runtime
    private readonly ConcurrentDictionary<string, BanlistEntry> _bannedIps = new(StringComparer.OrdinalIgnoreCase);

    // Failed login tracking: IP → list of failure timestamps
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new(StringComparer.OrdinalIgnoreCase);

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public BanlistService(IOptions<AppSettings> settings, ILogger<BanlistService> logger)
    {
        _config = settings.Value.RateLimit;
        _logger = logger;
        _banlistPath = Path.GetFullPath(_config.BanlistPath);

        // Load initial ban list
        LoadBanlist();

        // Watch for file changes to reload
        var dir = Path.GetDirectoryName(_banlistPath);
        var file = Path.GetFileName(_banlistPath);
        if (dir != null && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnBanlistFileChanged;
        }
    }

    /// <summary>Check if an IP address is currently banned.</summary>
    public bool IsBanned(IPAddress? ip)
    {
        if (ip == null) return false;
        var key = ip.ToString();
        return _bannedIps.ContainsKey(key);
    }

    /// <summary>
    /// Record a failed login attempt. If the IP exceeds the threshold,
    /// it is permanently banned and the ban is written to disk.
    /// </summary>
    public void RecordFailure(IPAddress? ip)
    {
        if (ip == null) return;
        var key = ip.ToString();

        var now = DateTime.UtcNow;
        var timestamps = _failures.GetOrAdd(key, _ => new List<DateTime>());

        lock (timestamps)
        {
            timestamps.Add(now);

            // Prune old entries outside the window
            var cutoff = now.AddMinutes(-_config.WindowMinutes);
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= _config.MaxFailedAttempts)
            {
                BanIp(key, $"Exceeded {_config.MaxFailedAttempts} failed attempts in {_config.WindowMinutes} minutes");
                timestamps.Clear();
            }
        }
    }

    /// <summary>Record a successful login — resets the failure counter for this IP.</summary>
    public void RecordSuccess(IPAddress? ip)
    {
        if (ip == null) return;
        _failures.TryRemove(ip.ToString(), out _);
    }

    private void BanIp(string ip, string reason)
    {
        var entry = new BanlistEntry
        {
            Ip = ip,
            Reason = reason,
            BannedAt = DateTime.UtcNow,
        };

        if (_bannedIps.TryAdd(ip, entry))
        {
            _logger.LogWarning("Banned IP {Ip}: {Reason}", ip, reason);
            SaveBanlist();
        }
    }

    #region Banlist persistence

    private void LoadBanlist()
    {
        try
        {
            if (!File.Exists(_banlistPath))
                return;

            var yaml = File.ReadAllText(_banlistPath);
            var config = _deserializer.Deserialize<BanlistConfig>(yaml);
            if (config?.BannedIps == null) return;

            _bannedIps.Clear();
            foreach (var entry in config.BannedIps)
            {
                if (!string.IsNullOrWhiteSpace(entry.Ip))
                    _bannedIps[entry.Ip] = entry;
            }

            _logger.LogInformation("Loaded {Count} banned IP(s) from {Path}", _bannedIps.Count, _banlistPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load banlist from {Path}", _banlistPath);
        }
    }

    private void SaveBanlist()
    {
        try
        {
            var config = new BanlistConfig
            {
                BannedIps = _bannedIps.Values.OrderBy(e => e.BannedAt).ToList(),
            };

            var sb = new StringBuilder();
            sb.AppendLine("# IP Ban List");
            sb.AppendLine("# ==========  ");
            sb.AppendLine("# Auto-managed by the server. You can also add entries manually.");
            sb.AppendLine("# Changes to this file are detected and reloaded automatically.");
            sb.AppendLine();

            sb.Append(_serializer.Serialize(config));
            File.WriteAllText(_banlistPath, sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save banlist to {Path}", _banlistPath);
        }
    }

    private DateTime _lastReload = DateTime.MinValue;

    private void OnBanlistFileChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: FileSystemWatcher can fire multiple times for a single save
        if ((DateTime.UtcNow - _lastReload).TotalSeconds < 2)
            return;
        _lastReload = DateTime.UtcNow;

        _logger.LogInformation("Banlist file changed, reloading...");
        LoadBanlist();
    }

    #endregion

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
