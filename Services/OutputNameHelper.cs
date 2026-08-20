using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MovieConcatenater.Services;

public static class OutputNameHelper
{
    // ファイルボディ末尾の連番: foo_01 / bar-2 / baz 003 / movie.01
    private static readonly Regex TrailingSequenceRegex = new(
        @"^(?<base>.*?)(?<sep>[_\-\.\s]?)(?<num>\d+)$",
        RegexOptions.Compiled);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    public static IOrderedEnumerable<string> OrderPathsNaturally(IEnumerable<string> paths) =>
        paths.OrderBy(p => p, Comparer<string>.Create(CompareNatural));

    public static int CompareNatural(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        try
        {
            return StrCmpLogicalW(a, b);
        }
        catch
        {
            return StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }
    }

    /// <summary>
    /// 複数ファイルのボディ末尾が連番と見なせる場合、連番を除いた共通ベース名を返す。
    /// 該当しない場合は null。
    /// </summary>
    public static string? SuggestBaseNameFromSequence(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count < 2)
        {
            return null;
        }

        var bodies = filePaths
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (bodies.Count < 2)
        {
            return null;
        }

        var parsed = new List<(string Base, string Sep, int Number, int Digits)>();
        foreach (string body in bodies)
        {
            var m = TrailingSequenceRegex.Match(body);
            if (!m.Success)
            {
                return null;
            }

            string baseName = m.Groups["base"].Value;
            string sep = m.Groups["sep"].Value;
            string numText = m.Groups["num"].Value;
            if (!int.TryParse(numText, out int number))
            {
                return null;
            }

            if (string.IsNullOrEmpty(baseName) && string.IsNullOrEmpty(sep))
            {
                return null;
            }

            parsed.Add((baseName, sep, number, numText.Length));
        }

        string commonBase = parsed[0].Base;
        string commonSep = parsed[0].Sep;
        if (parsed.Any(p => p.Base != commonBase || p.Sep != commonSep))
        {
            return null;
        }

        var numbers = parsed.Select(p => p.Number).OrderBy(n => n).ToList();
        if (numbers.Distinct().Count() != numbers.Count)
        {
            return null;
        }

        string suggested = commonBase.TrimEnd(' ', '.', '-', '_');
        if (string.IsNullOrWhiteSpace(suggested))
        {
            return null;
        }

        return suggested;
    }

    public static string SuggestDefaultFileName(IReadOnlyList<string> filePaths)
    {
        string? fromSeq = SuggestBaseNameFromSequence(filePaths);
        if (!string.IsNullOrWhiteSpace(fromSeq))
        {
            return fromSeq + GetPreferredExtension(filePaths);
        }

        if (filePaths.Count == 1)
        {
            return Path.GetFileName(filePaths[0]);
        }

        if (filePaths.Count > 0)
        {
            return Path.GetFileNameWithoutExtension(filePaths[0]) + "_concat" + GetPreferredExtension(filePaths);
        }

        return "output.mp4";
    }

    public static string GetPreferredExtension(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
        {
            return ".mp4";
        }

        string ext = Path.GetExtension(filePaths[0]);
        return string.IsNullOrWhiteSpace(ext) ? ".mp4" : ext;
    }
}
