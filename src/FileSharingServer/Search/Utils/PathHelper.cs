using System.Text;
using TDSNET.Engine.Actions;
using TDSNET.Engine.Actions.USN;
using TDSNET.Engine.Utils;

namespace TDSNET.Utils
{
    public static class PathHelper
    {
        private static string FormatBytes(long bytes)
        {
            return bytes switch
            {
                < 1024L => $"{bytes:0.###} B",
                < 1048576L => $"{(bytes / 1024.0):0.###} KB",
                < 1073741824L => $"{(bytes / 1048576.0):0.###} MB",
                < 1099511627776L => $"{(bytes / 1073741824.0):0.###} GB",
                < 1125899906842624L => $"{(bytes / 1099511627776.0):0.###} TB",
                _ => $"{(bytes / 1125899906842624.0):0.###} PB"
            };
        }

        public static ReadOnlySpan<char> getfileNameNormalize(ReadOnlySpan<char> filename)
        {
            filename = filename.Trim('|');

            var index = filename.IndexOf('|');
            if (index < 0)
            {
                return ReadOnlySpan<char>.Empty;
            }
            else if (index + 1 < filename.Length)
            {
                return filename.Slice(index + 1, filename.Length - index - 1);
            }
            else
            {
                return ReadOnlySpan<char>.Empty;
            }
        }

        public static ReadOnlySpan<char> getfileName(ReadOnlySpan<char> filename)
        {
            filename = filename.Trim('|');

            var index = filename.IndexOf('|');
            if (index < 0)
            {
                return filename;
            }
            else
            {
                return filename.Slice(0, index);
            }
        }

        public static ReadOnlySpan<char> GetPath(FrnFileOrigin f)
        {
            var path = StringUtils.GetPathStr(f, ReadOnlySpan<char>.Empty);
            if (path.EndsWith(":".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var pathChar = new char[path.Length + 1];
                Array.Copy(path.ToArray(), pathChar, path.Length);
                pathChar[pathChar.Length - 1] = '\\';
                return pathChar.AsSpan();
            }
            else
            {
                return path;
            }
        }
    }
}