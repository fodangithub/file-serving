using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileSharingServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TDSNET.Engine;

namespace FileSharingServer.Services;

/// <summary>
/// Hosted service that manages the USN Journal file index.
///
/// On startup, detects the drive containing the configured root directory and
/// attempts to build an in-memory file index using the Windows NTFS USN Journal.
/// If USN indexing fails (no admin rights, non-NTFS drive), the service marks
/// itself as unavailable and FileService falls back to directory enumeration.
///
/// Runs a background loop to apply incremental USN updates at a configurable interval.
/// </summary>
public sealed class UsnSearchService : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly ILogger<UsnSearchService> _logger;
    private readonly string _rootDirectory;
    private readonly SearchConfig _config;
    private UsnSearchEngine? _engine;
    private CancellationTokenSource? _updateLoopCts;
    private Task? _updateLoopTask;

    public UsnSearchService(
        ILogger<UsnSearchService> logger,
        IOptions<AppSettings> settings)
    {
        _logger = logger;
        _rootDirectory = Path.GetFullPath(settings.Value.Server.RootDirectory);
        _config = settings.Value.Search;
    }

    /// <summary>Whether the USN index is available for search.</summary>
    public bool IsAvailable => _engine?.IsAvailable ?? false;

    /// <summary>Number of files in the index.</summary>
    public int IndexedFileCount => _engine?.IndexedFileCount ?? 0;

    /// <summary>
    /// Search the USN index. Returns null if the index is not available.
    /// </summary>
    public List<UsnSearchResult>? Search(string query, string? rootFilter = null, int maxResults = 200)
    {
        if (!IsAvailable) return null;

        try
        {
            return _engine!.Search(query, rootFilter, maxResults, excludeHidden: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN search failed for query '{Query}'", query);
            return null;
        }
    }

    /// <summary>
    /// Search the USN index using a regex predicate on filenames.
    /// Returns null if the index is not available.
    /// Used for glob/wildcard patterns (*.cs, test?.txt) — iterates the in-memory
    /// index so it's still far faster than walking the filesystem.
    /// </summary>
    public List<UsnSearchResult>? SearchWithRegex(
        System.Text.RegularExpressions.Regex regex,
        string? rootFilter = null,
        int maxResults = 200)
    {
        if (!IsAvailable) return null;

        try
        {
            return _engine!.SearchWithRegex(regex, rootFilter, maxResults, excludeHidden: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN regex search failed");
            return null;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.EnableUsnIndex)
        {
            _logger.LogInformation("USN index is disabled in configuration");
            return Task.CompletedTask;
        }

        // Determine which drive the root directory is on
        string driveName;
        try
        {
            var rootInfo = new DirectoryInfo(_rootDirectory);
            driveName = rootInfo.Root.FullName; // e.g. "D:\"
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine root drive for '{Root}'. USN index disabled", _rootDirectory);
            return Task.CompletedTask;
        }

        _engine = new UsnSearchEngine(driveName);

        // Build index in background so we don't block server startup
        _ = Task.Run(() => InitializeIndex(), CancellationToken.None);

        return Task.CompletedTask;
    }

    private void InitializeIndex()
    {
        try
        {
            _logger.LogInformation("Attempting to load USN cache from '{CachePath}'", _config.CachePath);

            // Try loading from cache first (much faster than full USN enumeration)
            if (File.Exists(_config.CachePath) && _engine!.TryLoadCache(_config.CachePath))
            {
                _logger.LogInformation(
                    "USN index loaded from cache: {Count:N0} files on {Drive}",
                    _engine.IndexedFileCount, _engine.DriveName);
            }
            else
            {
                _logger.LogInformation("Building USN index from journal for {Drive}...", _engine!.DriveName);
                _engine.Initialize();
                _logger.LogInformation(
                    "USN index built: {Count:N0} files on {Drive}",
                    _engine.IndexedFileCount, _engine.DriveName);

                // Save cache for faster next startup
                try
                {
                    _engine.SaveCache(_config.CachePath);
                    _logger.LogInformation("USN cache saved to '{CachePath}'", _config.CachePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save USN cache");
                }
            }

            // Start background update loop
            StartUpdateLoop();
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "USN index requires administrator privileges. " +
                "Search will fall back to directory enumeration. " +
                "Run the server as Administrator to enable USN indexing.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("USN index initialization failed: {Message}. Falling back to directory enumeration", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USN index initialization failed unexpectedly. Falling back to directory enumeration");
        }
    }

    private void StartUpdateLoop()
    {
        _updateLoopCts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.UpdateIntervalSeconds));

        _updateLoopTask = Task.Run(async () =>
        {
            while (!_updateLoopCts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, _updateLoopCts.Token).ConfigureAwait(false);
                    _engine?.UpdateIndex();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "USN update loop iteration failed, will retry");
                }
            }
        }, CancellationToken.None);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _updateLoopCts?.Cancel();

        // Save cache on shutdown for faster next startup
        if (_engine?.IsAvailable == true)
        {
            try
            {
                _engine.SaveCache(_config.CachePath);
                _logger.LogInformation("USN cache saved on shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save USN cache on shutdown");
            }
        }

        return _updateLoopTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _updateLoopCts?.Cancel();
        _updateLoopCts?.Dispose();
        _engine?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
