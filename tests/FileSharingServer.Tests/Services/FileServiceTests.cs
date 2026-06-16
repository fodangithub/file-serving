using FileSharingServer.Services;

namespace FileSharingServer.Tests.Services;

public class FileServiceTests : IDisposable
{
    private readonly string _testRoot;
    private readonly FileService _service;

    public FileServiceTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"fileservice_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
        _service = new FileService(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
    }

    private void CreateDir(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(_testRoot, relativePath));
    }

    private void CreateFile(string relativePath, string content = "test")
    {
        var fullPath = Path.Combine(_testRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void SetHidden(string relativePath)
    {
        var fullPath = Path.Combine(_testRoot, relativePath);
        File.SetAttributes(fullPath, File.GetAttributes(fullPath) | FileAttributes.Hidden);
    }

    [Fact]
    public void GetDirectories_ReturnsNonHiddenDirs()
    {
        CreateDir("visible");
        CreateDir("another");

        var dirs = _service.GetDirectories("");

        Assert.Equal(2, dirs.Count);
        Assert.Contains(dirs, d => d.Name == "visible");
        Assert.Contains(dirs, d => d.Name == "another");
    }

    [Fact]
    public void GetDirectories_ExcludesDotPrefixed()
    {
        CreateDir("visible");
        CreateDir(".hidden");

        var dirs = _service.GetDirectories("");

        Assert.Single(dirs);
        Assert.Equal("visible", dirs[0].Name);
    }

    [Fact]
    public void GetDirectories_ExcludesWindowsHidden()
    {
        CreateDir("visible");
        CreateDir("secret");
        SetHidden("secret");

        var dirs = _service.GetDirectories("");

        Assert.Single(dirs);
        Assert.Equal("visible", dirs[0].Name);
    }

    [Fact]
    public void GetFiles_ReturnsNonHiddenFiles()
    {
        CreateFile("readme.txt");
        CreateFile("data.csv");

        var files = _service.GetFiles("");

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void GetFiles_ExcludesDotPrefixed()
    {
        CreateFile("visible.txt");
        CreateFile(".env");

        var files = _service.GetFiles("");

        Assert.Single(files);
        Assert.Equal("visible.txt", files[0].Name);
    }

    [Fact]
    public void GetFiles_ExcludesWindowsHidden()
    {
        CreateFile("visible.txt");
        CreateFile("secret.txt");
        SetHidden("secret.txt");

        var files = _service.GetFiles("");

        Assert.Single(files);
        Assert.Equal("visible.txt", files[0].Name);
    }

    [Fact]
    public void GetDirectories_SubDirectory()
    {
        CreateDir("parent/child1");
        CreateDir("parent/child2");

        var dirs = _service.GetDirectories("parent");

        Assert.Equal(2, dirs.Count);
    }

    [Fact]
    public void GetFileForDownload_ReturnsFile()
    {
        CreateFile("docs/test.txt", "hello");

        var result = _service.GetFileForDownload("docs/test.txt");

        Assert.NotNull(result);
        Assert.Equal("test.txt", result!.Value.fileName);
        Assert.True(File.Exists(result.Value.fullPath));
    }

    [Fact]
    public void GetFileForDownload_NonExistent_ReturnsNull()
    {
        var result = _service.GetFileForDownload("nonexistent.txt");
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_Blocked()
    {
        var result = _service.ResolveAndValidate("../../etc/passwd");
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_EncodedSlashes_Blocked()
    {
        var result = _service.ResolveAndValidate("..\\..\\windows\\system32");
        Assert.Null(result);
    }

    [Fact]
    public void ValidRelativePath_Resolves()
    {
        CreateDir("sub/dir");
        var result = _service.ResolveAndValidate("sub/dir");
        Assert.NotNull(result);
        Assert.StartsWith(_testRoot, result!);
    }

    [Fact]
    public void IsHidden_DotPrefix_ReturnsTrue()
    {
        CreateDir(".git");
        Assert.True(_service.IsHidden(Path.Combine(_testRoot, ".git")));
    }

    [Fact]
    public void IsHidden_NormalDir_ReturnsFalse()
    {
        CreateDir("normal");
        Assert.False(_service.IsHidden(Path.Combine(_testRoot, "normal")));
    }

    [Fact]
    public void GetRelativePath_ReturnsCorrect()
    {
        CreateDir("sub/deep");
        var relative = _service.GetRelativePath(Path.Combine(_testRoot, "sub", "deep"));
        Assert.NotNull(relative);
        Assert.Equal(Path.Combine("sub", "deep"), relative);
    }

    [Fact]
    public void GetDirectories_EmptyDir_ReturnsEmpty()
    {
        var dirs = _service.GetDirectories("");
        Assert.Empty(dirs);
    }

    [Fact]
    public void GetFiles_EmptyDir_ReturnsEmpty()
    {
        var files = _service.GetFiles("");
        Assert.Empty(files);
    }

    [Fact]
    public void GetDirectories_NonExistentPath_ReturnsEmpty()
    {
        var dirs = _service.GetDirectories("doesnotexist");
        Assert.Empty(dirs);
    }

    [Fact]
    public void SearchFiles_FindsMatchingFiles()
    {
        CreateFile("readme.txt");
        CreateFile("notes.txt");
        CreateFile("image.png");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.EndsWith(".txt", r.FileName));
    }

    [Fact]
    public void SearchFiles_SearchesRecursively()
    {
        CreateFile("top.txt");
        CreateFile("sub/deep.txt");
        CreateFile("sub/nested/bottom.txt");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void SearchFiles_ScopedToCurrentDirectory()
    {
        CreateFile("root.txt");
        CreateFile("sub/target.txt");

        var results = _service.SearchFiles("sub", "*.txt");

        Assert.Single(results);
        Assert.Equal("target.txt", results[0].FileName);
    }

    [Fact]
    public void SearchFiles_QuestionMarkWildcard()
    {
        CreateFile("ab.txt");
        CreateFile("abc.txt");

        var results = _service.SearchFiles("", "??.txt");

        Assert.Single(results);
        Assert.Equal("ab.txt", results[0].FileName);
    }

    [Fact]
    public void SearchFiles_ExcludesHiddenFiles()
    {
        CreateFile("visible.txt");
        CreateFile(".hidden.txt");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Single(results);
        Assert.Equal("visible.txt", results[0].FileName);
    }

    [Fact]
    public void SearchFiles_ExcludesFilesInHiddenDirs()
    {
        CreateFile("normal/file.txt");
        CreateFile(".hidden/file.txt");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Single(results);
        Assert.Contains("normal", results[0].RelativePath);
    }

    [Fact]
    public void ValidateSearchPattern_TooShort_ReturnsFalse()
    {
        Assert.False(FileService.ValidateSearchPattern("ab"));
        Assert.False(FileService.ValidateSearchPattern(""));
    }

    [Fact]
    public void ValidateSearchPattern_AllWildcards_ReturnsFalse()
    {
        Assert.False(FileService.ValidateSearchPattern("***"));
        Assert.False(FileService.ValidateSearchPattern("*?."));
        Assert.False(FileService.ValidateSearchPattern("..."));
    }

    [Fact]
    public void ValidateSearchPattern_HasLiteralChars_ReturnsTrue()
    {
        Assert.True(FileService.ValidateSearchPattern("*.txt"));
        Assert.True(FileService.ValidateSearchPattern("test*"));
        Assert.True(FileService.ValidateSearchPattern("a??"));
    }

    [Fact]
    public void SearchFiles_InvalidPattern_ReturnsEmpty()
    {
        CreateFile("test.txt");
        var results = _service.SearchFiles("", "**");
        Assert.Empty(results);
    }

    [Fact]
    public void SearchFiles_ResultContainsRelativePath()
    {
        CreateFile("docs/manual.pdf");

        var results = _service.SearchFiles("", "*.pdf");

        Assert.Single(results);
        Assert.Equal("docs/manual.pdf", results[0].RelativePath);
    }

    [Fact]
    public void GlobToRegex_MatchesCorrectly()
    {
        var regex = FileService.GlobToRegex("*.txt");
        Assert.Matches(regex, "readme.txt");
        Assert.DoesNotMatch(regex, "readme.pdf");
    }

    [Fact]
    public void SearchFiles_IsCaseInsensitive()
    {
        CreateFile("README.TXT");
        CreateFile("notes.txt");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Equal(2, results.Count);
    }

    // ── Partial matching (NormalizeSearchPattern) ─────────────────────────

    [Fact]
    public void NormalizeSearchPattern_NoWildcards_WrapsWithStars()
    {
        Assert.Equal("*read*", FileService.NormalizeSearchPattern("read"));
        Assert.Equal("*test.txt*", FileService.NormalizeSearchPattern("test.txt"));
    }

    [Fact]
    public void NormalizeSearchPattern_WithWildcards_ReturnsUnchanged()
    {
        Assert.Equal("*.txt", FileService.NormalizeSearchPattern("*.txt"));
        Assert.Equal("test*", FileService.NormalizeSearchPattern("test*"));
        Assert.Equal("??.txt", FileService.NormalizeSearchPattern("??.txt"));
    }

    [Fact]
    public void SearchFiles_PartialMatch_NoWildcards()
    {
        CreateFile("readme.txt");
        CreateFile("bread.txt");
        CreateFile("image.png");

        // "read" has no wildcards → auto-wrapped to "*read*"
        var results = _service.SearchFiles("", "read");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FileName == "readme.txt");
        Assert.Contains(results, r => r.FileName == "bread.txt");
    }

    [Fact]
    public void SearchFiles_ExactNameStillMatches()
    {
        CreateFile("readme.txt");
        CreateFile("readme.md");

        // "readme.txt" → "*readme.txt*" matches both because readme.txt is a substring of readme.txt.bak etc.
        // But here "readme.txt" is a substring of "readme.txt" exactly.
        var results = _service.SearchFiles("", "readme.txt");

        Assert.Single(results);
        Assert.Equal("readme.txt", results[0].FileName);
    }

    // ── Async search ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchFilesAsync_FindsMatchingFiles()
    {
        CreateFile("readme.txt");
        CreateFile("notes.txt");
        CreateFile("image.png");

        var results = new List<SearchResult>();
        await foreach (var r in _service.SearchFilesAsync("", "*.txt"))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.EndsWith(".txt", r.FileName));
    }

    [Fact]
    public async Task SearchFilesAsync_PartialMatch()
    {
        CreateFile("readme.txt");
        CreateFile("bread.txt");
        CreateFile("image.png");

        var results = new List<SearchResult>();
        await foreach (var r in _service.SearchFilesAsync("", "read"))
            results.Add(r);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FileName == "readme.txt");
        Assert.Contains(results, r => r.FileName == "bread.txt");
    }

    [Fact]
    public async Task SearchFilesAsync_Cancellation_StopsEnumeration()
    {
        // Create enough files that the search won't finish instantly.
        for (var i = 0; i < 50; i++)
            CreateFile($"file{i:D3}.txt");

        using var cts = new CancellationTokenSource();
        var results = new List<SearchResult>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var r in _service.SearchFilesAsync("", "*.txt", cts.Token))
            {
                results.Add(r);
                if (results.Count >= 3)
                    cts.Cancel();
            }
        });

        // We should have received a few results but not all 50.
        Assert.InRange(results.Count, 3, 49);
    }

    [Fact]
    public async Task SearchFilesAsync_InvalidPattern_YieldsNothing()
    {
        CreateFile("test.txt");

        var results = new List<SearchResult>();
        await foreach (var r in _service.SearchFilesAsync("", "**"))
            results.Add(r);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchFilesAsync_NonExistentPath_YieldsNothing()
    {
        var results = new List<SearchResult>();
        await foreach (var r in _service.SearchFilesAsync("doesnotexist", "*.txt"))
            results.Add(r);

        Assert.Empty(results);
    }

    // ── Security tests ────────────────────────────────────────────────────

    [Fact]
    public void PathTraversal_TooLongPath_Blocked()
    {
        var longPath = new string('a', FileService.MaxRelativePathLength + 1);
        var result = _service.ResolveAndValidate(longPath);
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_NullByte_Blocked()
    {
        var result = _service.ResolveAndValidate("test\0.txt");
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_ControlChars_Blocked()
    {
        var result = _service.ResolveAndValidate("test\x01.txt");
        Assert.Null(result);
    }

    [Fact]
    public void PathTraversal_NullInput_Blocked()
    {
        var result = _service.ResolveAndValidate(null!);
        Assert.Null(result);
    }

    [Fact]
    public void GetFileForDownload_HiddenFile_Blocked()
    {
        CreateFile(".env", "secret=value");

        var result = _service.GetFileForDownload(".env");
        Assert.Null(result);
    }

    [Fact]
    public void GetFileForDownload_FileInHiddenDir_Blocked()
    {
        CreateFile(".hidden/secret.txt", "secret");

        var result = _service.GetFileForDownload(".hidden/secret.txt");
        Assert.Null(result);
    }

    [Fact]
    public void GetFileForDownload_NormalFile_Allowed()
    {
        CreateFile("docs/readme.txt", "hello");

        var result = _service.GetFileForDownload("docs/readme.txt");
        Assert.NotNull(result);
        Assert.Equal("readme.txt", result!.Value.fileName);
    }

    [Fact]
    public void PathTraversal_PrefixBoundary_DifferentDir()
    {
        // Create a directory whose name starts with the test root's last segment
        // but is actually a different directory. The prefix-boundary check must
        // reject paths that resolve into it.
        var parentDir = Path.GetDirectoryName(_testRoot)!;
        var rootName = Path.GetFileName(_testRoot);
        var similarDir = Path.Combine(parentDir, rootName + "_evil");
        Directory.CreateDirectory(similarDir);
        try
        {
            // Build a path that textually starts with _testRoot but actually
            // points to the sibling directory — this can't happen through
            // Path.Combine+GetFullPath with "..", so test IsUnderRoot directly.
            var evilPath = similarDir + Path.DirectorySeparatorChar + "file.txt";
            // IsUnderRoot is private; test via GetRelativePath which calls it.
            var relative = _service.GetRelativePath(evilPath);
            Assert.Null(relative);
        }
        finally
        {
            Directory.Delete(similarDir, true);
        }
    }

    [Fact]
    public void ValidateSearchPattern_TooLong_ReturnsFalse()
    {
        var longPattern = "aaa" + new string('b', FileService.MaxSearchPatternLength);
        Assert.False(FileService.ValidateSearchPattern(longPattern));
    }

    [Fact]
    public void ValidateSearchPattern_ControlChars_ReturnsFalse()
    {
        Assert.False(FileService.ValidateSearchPattern("test\x01"));
        Assert.False(FileService.ValidateSearchPattern("\x00abc"));
    }

    [Fact]
    public void SearchFiles_DoesNotReturnHiddenFilesInSubdirectories()
    {
        CreateFile("visible/normal.txt");
        CreateFile("visible/.secret.txt");

        var results = _service.SearchFiles("", "*.txt");

        Assert.Single(results);
        Assert.Equal("normal.txt", results[0].FileName);
    }

    [Fact]
    public async Task SearchFilesAsync_DoesNotReturnHiddenFilesInSubdirectories()
    {
        CreateFile("visible/normal.txt");
        CreateFile("visible/.secret.txt");

        var results = new List<SearchResult>();
        await foreach (var r in _service.SearchFilesAsync("", "*.txt"))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal("normal.txt", results[0].FileName);
    }
}
