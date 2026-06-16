using System.Globalization;

namespace ComicViewer;

internal sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var ix = 0;
        var iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var numberComparison = CompareNumberChunks(x, ref ix, y, ref iy);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }
            }
            else
            {
                var charComparison = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (charComparison != 0)
                {
                    return charComparison;
                }

                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int CompareNumberChunks(string x, ref int ix, string y, ref int iy)
    {
        var startX = ix;
        var startY = iy;

        while (ix < x.Length && char.IsDigit(x[ix]))
        {
            ix++;
        }

        while (iy < y.Length && char.IsDigit(y[iy]))
        {
            iy++;
        }

        var chunkX = x[startX..ix].TrimStart('0');
        var chunkY = y[startY..iy].TrimStart('0');
        if (chunkX.Length == 0)
        {
            chunkX = "0";
        }

        if (chunkY.Length == 0)
        {
            chunkY = "0";
        }

        var lengthComparison = chunkX.Length.CompareTo(chunkY.Length);
        if (lengthComparison != 0)
        {
            return lengthComparison;
        }

        var valueComparison = string.Compare(chunkX, chunkY, CultureInfo.InvariantCulture, CompareOptions.Ordinal);
        return valueComparison != 0 ? valueComparison : (ix - startX).CompareTo(iy - startY);
    }
}
