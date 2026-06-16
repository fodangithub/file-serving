using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FileSharingServer.Configuration;
using Microsoft.Extensions.Options;

namespace FileSharingServer.Services;

public partial class FileService
{
    private readonly string _rootDirectory;

    public FileService(IOptions<AppSettings> settings)
    {
        _rootDirectory = Path.GetFullPath(settings.Value.Server.RootDirectory);
    }

    public FileService(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
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

        return (fullPath, Path.GetFileName(fullPath));
    }

    public string? GetRelativePath(string fullPath)
    {
        if (!fullPath.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetRelativePath(_rootDirectory, fullPath);
    }

    public static bool ValidateSearchPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length < 3)
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

        var regex = GlobToRegex(NormalizeSearchPattern(pattern));
        var results = new List<SearchResult>();

        foreach (var file in EnumerateFilesSafe(fullPath))
        {
            if (IsHidden(file) || IsInHiddenDirectory(file, fullPath))
                continue;

            var fileName = Path.GetFileName(file);
            if (!regex.IsMatch(fileName))
                continue;

            var relPath = Path.GetRelativePath(_rootDirectory, file);
            results.Add(new SearchResult
            {
                FileName = fileName,
                RelativePath = relPath.Replace('\\', '/'),
                Size = new FileInfo(file).Length
            });

            if (results.Count >= 200)
                break;
        }

        return results;
    }

    /// <summary>
    /// Async version of <see cref="SearchFiles"/> that yields results one at a time
    /// and honours <paramref name="ct"/> so the caller can cancel mid-enumeration.
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

        var regex = GlobToRegex(NormalizeSearchPattern(pattern));
        var count = 0;

        foreach (var file in EnumerateFilesSafe(fullPath))
        {
            ct.ThrowIfCancellationRequested();

            if (IsHidden(file) || IsInHiddenDirectory(file, fullPath))
                continue;

            var fileName = Path.GetFileName(file);
            if (!regex.IsMatch(fileName))
                continue;

            var relPath = Path.GetRelativePath(_rootDirectory, file);
            yield return new SearchResult
            {
                FileName = fileName,
                RelativePath = relPath.Replace('\\', '/'),
                Size = new FileInfo(file).Length
            };

            if (++count >= 200)
                yield break;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                yield return file;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subDirs)
                stack.Push(sub);
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
        return new Regex(escaped.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    internal string? ResolveAndValidate(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));
        if (!fullPath.StartsWith(_rootDirectory, StringComparison.OrdinalIgnoreCase))
            return null;
        return fullPath;
    }
}

public class SearchResult
{
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
}
