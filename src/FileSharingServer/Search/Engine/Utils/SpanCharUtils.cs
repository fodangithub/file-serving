using System;
using System.Runtime.CompilerServices;

namespace TDSNET.Engine.Utils;

public static class SpanCharUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NotContains(ReadOnlySpan<char> source, ReadOnlySpan<char> pattern)
    {
        return source.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0;
    }
}
