using System;
using System.Collections.Concurrent;
using System.Linq;
using TDSNET.Engine.Actions.USN;
using TDSNET.Engine.Utils;

namespace TDSNET.Engine;

/// <summary>
/// Parses a search query string into keywords and computes the bitmask
/// used for fast pre-filtering against the file index.
///
/// Extracted from TDS desktop app's MainWindow_TaskLoop.cs search logic.
/// </summary>
public class SearchQuery
{
    /// <summary>Individual keywords to match (all must be present — AND semantics).</summary>
    public string[] Keywords { get; }

    /// <summary>
    /// Bitmask over the alphabet for fast rejection.
    /// If any keyword's characters are not a subset of a file's keyindex, the file can be skipped.
    /// </summary>
    public ulong KeywordBitmask { get; }

    public SearchQuery(string[] keywords, ulong keywordBitmask)
    {
        Keywords = keywords;
        KeywordBitmask = keywordBitmask;
    }

    /// <summary>
    /// Parse a query string into a SearchQuery.
    /// The query is split by spaces into keywords (empty entries removed).
    /// The combined bitmask is computed from all keywords joined together.
    /// </summary>
    public static SearchQuery Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchQuery(Array.Empty<string>(), 0);

        // Normalize: uppercase, collapse double spaces
        query = query.ToUpperInvariant().Replace("  ", " ").Trim();

        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Compute combined bitmask from all keywords concatenated
        string combined = string.Concat(keywords);
        ulong bitmask = FileSys.TBS(SpellCN.GetSpellCode(combined));

        return new SearchQuery(keywords, bitmask);
    }

    /// <summary>
    /// Check if a file's keyindex passes the bitmask pre-filter.
    /// Returns false if the file definitely does NOT match (fast rejection).
    /// Returns true if the file might match (needs substring verification).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool PassesBitmaskFilter(ulong fileKeyindex)
    {
        return (KeywordBitmask | fileKeyindex) == fileKeyindex;
    }

    /// <summary>
    /// Verify that all keywords are present as substrings in the given text span.
    /// This is the second stage after bitmask pre-filtering.
    /// </summary>
    public bool MatchesAllKeywords(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < Keywords.Length; i++)
        {
            if (SpanCharUtils.NotContains(text, Keywords[i]))
                return false;
        }
        return true;
    }
}
