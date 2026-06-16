using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FileSharingServer.Configuration;
using Microsoft.Extensions.Options;

namespace FileSharingServer.Services;

public partial class FileService
{
    public const int MaxRelativePathLength = 260;
    public const int MaxSearchPatternLength = 200;
    public const int MaxSearchResults = 200;
    public const int MaxDirectoryDepth = 30;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private readonly string _rootDirectory;
    private readonly UsnSearchService? _usnSearch;

    public FileService(IOptions<AppSettings> settings, UsnSearchService? usnSearch = null)
    {
        _rootDirectory = Path.GetFullPath(settings.Value.Server.RootDirectory);
        _usnSearch = usnSearch;
    }

    public FileService(string rootDirectory, UsnSearchService? usnSearch = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _usnSearch = usnSearch;
    }

    public string RootDirectory => _rootDirectory;

    public bool IsHidden(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith('.'))
            return true;

        var info = new FileInfo(fullPath);
        return (info.Attributes & FileAttributes.Hidden) != 0;
    }

    public List<DirectoryInfo> GetDirectories(string relativePath = "")
    {
        var fullPath = ResolveAndValidate(relativePath);
        if (fullPath == null || !Directory.Exists(fullPath))
            return new List<DirectoryInfo>();

        return Directory.GetDirectories(fullPath)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !IsHidden(d.FullName))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<FileInfo> GetFiles(string relativePath = "")
    {
        var fullPath = ResolveAndValidate(relativePath);
        if (fullPath == null || !Directory.Exists(fullPath))
            return new List<FileInfo>();

        return Directory.GetFiles(fullPath)
            .Select(f => new FileInfo(f))
            .Where(f => !IsHidden(f.FullName))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public (string fullPath, string fileName)? GetFileForDownload(string relativePath)
    {
        var fullPath = ResolveAndValidate(relativePath);
        if (fullPath == null || !File.Exists(fullPath))
            return null;

        if (IsHidden(fullPath) || IsInHiddenDirectory(fullPath, _rootDirectory))
            return null;

        return (fullPath, Path.GetFileName(fullPath));
    }

    public string? GetRelativePath(string fullPath)
    {
        if (!IsUnderRoot(fullPath))
            return null;
        return Path.GetRelativePath(_rootDirectory, fullPath);
    }

    public static bool ValidateSearchPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length < 3)
            return false;

        if (pattern.Length > MaxSearchPatternLength)
            return false;

        if (pattern.Any(c => char.IsControl(c)))
            return false;

        foreach (var ch in pattern)
        {
            if (ch != '*' && ch != '?' && ch != '.')
                return true;
        }
        return false;
    }

    /// <summary>
    /// When the pattern has no wildcards at all, treat it as a substring / partial match
    /// by wrapping it with *.  e.g. "read" becomes "*read*" so it matches "readme.txt".
    /// Patterns that already contain * or ? are returned unchanged.
    /// </summary>
    internal static string NormalizeSearchPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return pattern;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return "*" + pattern + "*";

        return pattern;
    }

    public List<SearchResult> SearchFiles(string relativePath, string pattern)
    {
        if (!ValidateSearchPattern(pattern))
            return new List<SearchResult>();

        var fullPath = ResolveAndValidate(relativePath);
        if (fullPath == null || !Directory.Exists(fullPath))
            return new List<SearchResult>();

        // Fast path: use USN in-memory index (no disk I/O)
        if (_usnSearch != null && _usnSearch.IsAvailable)
        {
            bool hasWildcards = pattern.Contains('*') || pattern.Contains('?');

            if (!hasWildcards)
            {
                // Simple text → keyword substring search
                var usnResults = _usnSearch.Search(pattern, rootFilter: fullPath, maxResults: MaxSearchResults);
                if (usnResults != null)
                    return MapUsnResults(usnResults, fullPath);
            }
            else
            {
                // Glob/wildcard → regex over the in-memory index (still much faster than disk walk)
                var regex = GlobToRegex(NormalizeSearchPattern(pattern));
                try
                {
                    var usnResults = _usnSearch.SearchWithRegex(regex, rootFilter: fullPath, maxResults: MaxSearchResults);
                    if (usnResults != null)
                        return MapUsnResults(usnResults, fullPath);
                }
                catch (RegexMatchTimeoutException) { }
            }
        }

        // Fallback: disk-walk with regex (only when USN is unavailable)
        var fallbackRegex = GlobToRegex(NormalizeSearchPattern(pattern));
        var results = new List<SearchResult>();

        try
        {
            foreach (var (entry, isDir) in EnumerateEntriesSafe(fullPath, MaxDirectoryDepth))
            {
                if (IsHidden(entry) || IsInHiddenDirectory(entry, fullPath))
                    continue;

                var fileName = Path.GetFileName(entry);
                if (!fallbackRegex.IsMatch(fileName))
                    continue;

                var relPath = Path.GetRelativePath(_rootDirectory, entry);
                results.Add(new SearchResult
                {
                    FileName = fileName,
                    RelativePath = relPath.Replace('\\', '/'),
                    Size = isDir ? 0 : new FileInfo(entry).Length,
                    IsDirectory = isDir,
                });

                if (results.Count >= MaxSearchResults)
                    break;
            }
        }
        catch (RegexMatchTimeoutException) { }

        return results;
    }

    /// <summary>
    /// Async streaming version of <see cref="SearchFiles"/>.
    /// Runs the synchronous file-system enumeration on a background thread and
    /// delivers results through a <see cref="Channel{SearchResult}"/> so the
    /// Blazor Server sync context stays responsive and can render incremental
    /// updates as results arrive.
    /// </summary>
    public async IAsyncEnumerable<SearchResult> SearchFilesAsync(
        string relativePath,
        string pattern,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ValidateSearchPattern(pattern))
            yield break;

        var fullPath = ResolveAndValidate(relativePath);
        if (fullPath == null || !Directory.Exists(fullPath))
            yield break;

        // Fast path: use USN in-memory index (no disk I/O)
        if (_usnSearch != null && _usnSearch.IsAvailable)
        {
            bool hasWildcards = pattern.Contains('*') || pattern.Contains('?');

            List<TDSNET.Engine.UsnSearchResult>? usnResults = null;
            if (!hasWildcards)
            {
                // Simple text → keyword substring search
                usnResults = _usnSearch.Search(pattern, rootFilter: fullPath, maxResults: MaxSearchResults);
            }
            else
            {
                // Glob/wildcard → regex over the in-memory index
                try
                {
                    var regex = GlobToRegex(NormalizeSearchPattern(pattern));
                    usnResults = _usnSearch.SearchWithRegex(regex, rootFilter: fullPath, maxResults: MaxSearchResults);
                }
                catch (RegexMatchTimeoutException) { }
            }

            if (usnResults != null)
            {
                var mapped = MapUsnResults(usnResults, fullPath);
                foreach (var result in mapped)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return result;
                }
                yield break;
            }
        }

        // Fallback: disk-walk with regex via background thread + channel (only when USN unavailable)
        var fallbackRegex = GlobToRegex(NormalizeSearchPattern(pattern));
        var channel = Channel.CreateUnbounded<SearchResult>();

        _ = Task.Run(async () =>
        {
            try
            {
                var count = 0;
                foreach (var (entry, isDir) in EnumerateEntriesSafe(fullPath, MaxDirectoryDepth))
                {
                    ct.ThrowIfCancellationRequested();

                    if (IsHidden(entry) || IsInHiddenDirectory(entry, fullPath))
                        continue;

                    var fileName = Path.GetFileName(entry);
                    if (!fallbackRegex.IsMatch(fileName))
                        continue;

                    var relPath = Path.GetRelativePath(_rootDirectory, entry);
                    await channel.Writer.WriteAsync(new SearchResult
                    {
                        FileName = fileName,
                        RelativePath = relPath.Replace('\\', '/'),
                        Size = isDir ? 0 : new FileInfo(entry).Length,
                        IsDirectory = isDir,
                    }, ct).ConfigureAwait(false);

                    if (++count >= MaxSearchResults)
                        break;
                }
            }
            catch (OperationCanceledException) { }
            catch (RegexMatchTimeoutException) { }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <summary>
    /// Map USN search results to the SearchResult model used by the UI.
    /// Applies hidden-directory filtering and computes relative paths and file sizes.
    /// </summary>
    private List<SearchResult> MapUsnResults(List<TDSNET.Engine.UsnSearchResult> usnResults, string searchRoot)
    {
        var results = new List<SearchResult>();
        foreach (var r in usnResults)
        {
            if (IsHidden(r.FullPath) || IsInHiddenDirectory(r.FullPath, searchRoot))
                continue;

            var relPath = Path.GetRelativePath(_rootDirectory, r.FullPath);
            long size = 0;
            if (!r.IsDirectory)
            {
                try { size = new FileInfo(r.FullPath).Length; } catch { /* file may have been deleted */ }
            }

            results.Add(new SearchResult
            {
                FileName = r.FileName,
                RelativePath = relPath.Replace('\\', '/'),
                Size = size,
                IsDirectory = r.IsDirectory,
            });

            if (results.Count >= MaxSearchResults)
                break;
        }
        return results;
    }

    private static IEnumerable<(string path, bool isDirectory)> EnumerateEntriesSafe(string root, int maxDepth)
    {
        var stack = new Stack<(string path, int depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            if (depth > maxDepth)
                continue;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                yield return (file, false);

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subDirs)
            {
                yield return (sub, true);
                stack.Push((sub, depth + 1));
            }
        }
    }

    private bool IsInHiddenDirectory(string filePath, string searchRoot)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir != null && dir.Length > searchRoot.Length)
        {
            if (IsHidden(dir))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    internal static Regex GlobToRegex(string pattern)
    {
        var escaped = new System.Text.StringBuilder("^");
        foreach (var ch in pattern)
        {
            switch (ch)
            {
                case '*':
                    escaped.Append(".*");
                    break;
                case '?':
                    escaped.Append('.');
                    break;
                default:
                    escaped.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }
        escaped.Append('$');
        return new Regex(escaped.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    }

    /// <summary>
    /// Validates a relative path against traversal, length, symlink-escape,
    /// and hidden-directory attacks.  Returns the resolved full path or null.
    /// </summary>
    internal string? ResolveAndValidate(string relativePath)
    {
        if (relativePath == null)
            return null;

        if (relativePath.Length > MaxRelativePathLength)
            return null;

        if (relativePath.IndexOf('\0') >= 0)
            return null;

        if (relativePath.Any(c => char.IsControl(c)))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!IsUnderRoot(fullPath))
            return null;

        if (!IsPathSecure(fullPath))
            return null;

        return fullPath;
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _rootDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walks every path segment from <paramref name="fullPath"/> up to the root,
    /// verifying that no directory is a symlink escaping the root and that no
    /// directory is hidden (dot-prefixed or Windows-hidden attribute).
    /// </summary>
    private bool IsPathSecure(string fullPath)
    {
        var current = fullPath;
        for (int i = 0; i < 40; i++)
        {
            if (string.Equals(current, _rootDirectory, StringComparison.OrdinalIgnoreCase))
                break;

            if (current.Length <= _rootDirectory.Length)
                break;

            if (!CheckSymlinkAndHidden(current))
                return false;

            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current)
                break;
            current = parent;
        }
        return true;
    }

    private bool CheckSymlinkAndHidden(string path)
    {
        try
        {
            var info = new FileInfo(path);

            var linkTarget = info.LinkTarget;
            if (linkTarget != null)
            {
                var resolvedTarget = Path.GetFullPath(linkTarget, Path.GetDirectoryName(path)!);
                if (!IsUnderRoot(resolvedTarget))
                    return false;
            }

            var name = Path.GetFileName(path);
            if (name.StartsWith('.'))
                return false;

            if ((info.Attributes & FileAttributes.Hidden) != 0)
                return false;
        }
        catch
        {
            return false;
        }
        return true;
    }
}

public class SearchResult
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
}
