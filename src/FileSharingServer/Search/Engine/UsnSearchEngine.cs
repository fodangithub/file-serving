using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TDSNET.Engine.Actions.USN;
using TDSNET.Engine.Utils;
using TDSNET.Utils;

namespace TDSNET.Engine;

/// <summary>
/// Result from a USN index search.
/// </summary>
public class UsnSearchResult
{
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
}

/// <summary>
/// High-level facade over the TDS USN Journal engine.
///
/// Builds an in-memory index of all files on an NTFS volume using the Windows
/// USN Journal API, then provides near-instant search via bitmask pre-filtering
/// and Span-based substring matching.
///
/// Usage:
///   var engine = new UsnSearchEngine("C:\\");
///   engine.Initialize();           // or: engine.TryLoadCache("cache.data")
///   var results = engine.Search("readme", rootFilter: "C:\\Users\\me\\docs");
///   engine.UpdateIndex();          // incremental USN update
///   engine.SaveCache("cache.data");
/// </summary>
public class UsnSearchEngine : IDisposable
{
    private FileSys? _fileSys;
    private readonly string _driveName;
    private bool _disposed;

    static UsnSearchEngine()
    {
        // Register code page encodings (GB2312 etc.) needed by the pinyin conversion
        // in CNchar.cs. Without this, .NET only supports UTF-8/UTF-16/ASCII by default.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public UsnSearchEngine(string driveName)
    {
        _driveName = driveName.EndsWith('\\') ? driveName : driveName + "\\";
    }

    /// <summary>Whether the index has been successfully built.</summary>
    public bool IsAvailable => _fileSys != null && _fileSys.files.Count > 0;

    /// <summary>The drive this engine indexes (e.g. "C:\").</summary>
    public string DriveName => _driveName;

    /// <summary>Number of files currently in the index.</summary>
    public int IndexedFileCount => _fileSys?.files.Count ?? 0;

    /// <summary>
    /// Build the file index from the NTFS USN Journal.
    /// Requires administrator privileges and an NTFS volume.
    /// Throws on failure (catch and fall back to directory enumeration).
    /// </summary>
    public void Initialize()
    {
        var driveInfo = new DriveInfoData { Name = _driveName, DriveFormat = "ntfs" };
        var fs = new FileSys(driveInfo);
        fs.ntfsUsnJournal = new NtfsUsnJournal(driveInfo);

        fs.usnStates = new Win32Api.USN_JOURNAL_DATA();
        if (!fs.SaveJournalState())
        {
            fs.ntfsUsnJournal.CreateUsnJournal(1000 * 1024, 16 * 1024);
            if (!fs.SaveJournalState())
                throw new InvalidOperationException("Failed to read USN journal state. Ensure the process has administrator privileges.");
        }

        fs.CreateFiles();
        LinkParents(fs);
        ProcessNames(fs);
        fs.Compress();

        _fileSys = fs;
    }

    /// <summary>
    /// Try to load a previously saved index from disk (LZ4-compressed).
    /// Returns true if the cache was loaded successfully.
    /// After loading, the USN journal handle is re-created for incremental updates.
    /// </summary>
    public bool TryLoadCache(string cachePath)
    {
        try
        {
            var cache = new DiskDataCache(cachePath);
            var result = cache.TryLoadFromDisk();
            if (result == null || result.Count == 0)
                return false;

            // We only care about the drive we were configured for
            FileSys? fs = null;
            foreach (var f in result)
            {
                if (string.Equals(f.driveInfoData.Name, _driveName, StringComparison.OrdinalIgnoreCase))
                {
                    fs = f;
                    break;
                }
            }

            if (fs == null)
                return false;

            // Re-create the USN journal handle for incremental updates
            fs.ntfsUsnJournal = new NtfsUsnJournal(fs.driveInfoData);
            LinkParents(fs);
            fs.Compress();

            _fileSys = fs;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Save the current index to disk as an LZ4-compressed cache file.
    /// </summary>
    public void SaveCache(string cachePath)
    {
        if (_fileSys == null) return;

        var cache = new DiskDataCache(cachePath);
        cache.DumpToDisk(new List<FileSys> { _fileSys });
    }

    /// <summary>
    /// Apply incremental updates from the USN Journal since the last check.
    /// Call this periodically to keep the index fresh.
    /// </summary>
    public void UpdateIndex()
    {
        if (_fileSys == null) return;
        try
        {
            _fileSys.DoWhileFileChanges();
        }
        catch
        {
            // USN journal may have been deleted or rolled over — silently ignore
        }
    }

    /// <summary>
    /// Search the index for files matching the query.
    /// </summary>
    /// <param name="query">Space-separated keywords (AND semantics). Case-insensitive.</param>
    /// <param name="rootFilter">
    /// Optional: only return files whose full path starts with this prefix.
    /// Should be a full path like "C:\Users\me\Documents".
    /// </param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="excludeHidden">If true, skip files starting with '.' or in hidden directories.</param>
    public List<UsnSearchResult> Search(
        string query,
        string? rootFilter = null,
        int maxResults = 200,
        bool excludeHidden = true)
    {
        if (_fileSys == null || !IsAvailable)
            return new List<UsnSearchResult>();

        var searchQuery = SearchQuery.Parse(query);
        if (searchQuery.Keywords.Length == 0)
            return new List<UsnSearchResult>();

        var results = new List<UsnSearchResult>();
        var files = _fileSys.files;

        // Normalize root filter for prefix comparison
        string? rootPrefix = null;
        if (!string.IsNullOrEmpty(rootFilter))
        {
            rootPrefix = rootFilter.EndsWith('\\') ? rootFilter : rootFilter + "\\";
        }

        foreach (var f in files.Values)
        {
            // Bitmask pre-filter (very fast rejection)
            if (!searchQuery.PassesBitmaskFilter(f.keyindex))
                continue;

            // Substring match
            var nameSpan = f.innerFileName.AsSpan();
            if (!searchQuery.MatchesAllKeywords(nameSpan))
                continue;

            // Build full path
            string fullPath = PathHelper.GetPath(f).ToString();

            // Root filter
            if (rootPrefix != null && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Hidden file filter
            if (excludeHidden)
            {
                string fileName = PathHelper.getfileName(f.innerFileName).ToString();
                if (fileName.StartsWith('.'))
                    continue;
            }

            results.Add(new UsnSearchResult
            {
                FileName = PathHelper.getfileName(f.innerFileName).ToString(),
                FullPath = fullPath,
                IsDirectory = IsDirectoryPath(fullPath),
            });

            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fileSys?.ntfsUsnJournal?.Dispose();
        _fileSys = null;
    }

    /// <summary>
    /// Search the index for files whose names match a regex predicate.
    ///
    /// Iterates the in-memory index (no disk I/O), so this is still far faster
    /// than walking the filesystem — even with regex evaluation on every filename.
    /// Use this for glob/wildcard patterns that can't be expressed as keyword substring search.
    /// </summary>
    /// <param name="regex">Compiled regex to match against the clean filename.</param>
    /// <param name="rootFilter">Only return files whose full path starts with this prefix.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="excludeHidden">If true, skip files starting with '.'.</param>
    public List<UsnSearchResult> SearchWithRegex(
        System.Text.RegularExpressions.Regex regex,
        string? rootFilter = null,
        int maxResults = 200,
        bool excludeHidden = true)
    {
        if (_fileSys == null || !IsAvailable)
            return new List<UsnSearchResult>();

        var results = new List<UsnSearchResult>();
        var files = _fileSys.files;

        string? rootPrefix = null;
        if (!string.IsNullOrEmpty(rootFilter))
        {
            rootPrefix = rootFilter.EndsWith('\\') ? rootFilter : rootFilter + "\\";
        }

        foreach (var f in files.Values)
        {
            // Extract clean filename from packed format (|name|pinyin|)
            string fileName = PathHelper.getfileName(f.innerFileName).ToString();

            // Hidden file filter (check name early to skip before path construction)
            if (excludeHidden && fileName.StartsWith('.'))
                continue;

            // Regex match on the clean filename
            if (!regex.IsMatch(fileName))
                continue;

            // Build full path (only for matches — avoids path construction for non-matches)
            string fullPath = PathHelper.GetPath(f).ToString();

            // Root filter
            if (rootPrefix != null && !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new UsnSearchResult
            {
                FileName = fileName,
                FullPath = fullPath,
                IsDirectory = IsDirectoryPath(fullPath),
            });

            if (results.Count >= maxResults)
                break;
        }

        return results;
    }

    #region Private helpers

    private static bool IsDirectoryPath(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Link each file entry to its parent via the parentFileReferenceNumber.
    /// Required after fresh indexing or cache loading.
    /// </summary>
    private static void LinkParents(FileSys fs)
    {
        foreach (var f in fs.files.Values)
        {
            if (f.parentFileReferenceNumber != ulong.MaxValue
                && fs.files.ContainsKey(f.parentFileReferenceNumber))
            {
                if (f.parentFrn == null)
                    f.parentFrn = fs.files[f.parentFileReferenceNumber];
            }
        }
    }

    /// <summary>
    /// Compute the NACN name and bitmask index for every file in the index.
    /// Required after fresh indexing (not needed after cache load — names are already processed).
    /// </summary>
    private static void ProcessNames(FileSys fs)
    {
        var spellDict = new ConcurrentDictionary<char, char>();

        Parallel.ForEach(fs.files.Values, f =>
        {
            FileSys.GetNACNNameAndIndex(f.innerFileName, out var nacnName, out var index, spellDict);
            f.keyindex = index;
            f.SetInnerFileName(nacnName);
        });
    }

    #endregion
}
