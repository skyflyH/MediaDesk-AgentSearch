using System.Globalization;
using System.Text;

namespace MediaLibrary.Service;

internal sealed class ChineseClipTokenizer
{
    private readonly Dictionary<string, long> vocabulary;

    public ChineseClipTokenizer(string vocabularyPath)
    {
        vocabulary = File.ReadLines(vocabularyPath).Select((token, index) => (token, index))
            .ToDictionary(item => item.token, item => (long)item.index, StringComparer.Ordinal);
        foreach (var token in new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" })
            if (!vocabulary.ContainsKey(token)) throw new InvalidDataException("Required ChineseCLIP token is missing.");
    }

    public (long[] Ids, long[] Mask) Encode(string text, int maxLength)
    {
        var ids = new long[maxLength];
        Array.Fill(ids, vocabulary["[PAD]"]);
        var mask = new long[maxLength];
        ids[0] = vocabulary["[CLS]"];
        var length = 1;
        foreach (var token in BasicTokens(text))
        {
            foreach (var part in WordPieces(token))
            {
                if (length == maxLength - 1) break;
                ids[length++] = part;
            }
            if (length == maxLength - 1) break;
        }
        ids[length++] = vocabulary["[SEP]"];
        Array.Fill(mask, 1, 0, length);
        return (ids, mask);
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        var buffer = new StringBuilder();
        foreach (var rune in text.Normalize(NormalizationForm.FormC).ToLowerInvariant()
            .Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (rune.Value is 0 or 0xFFFD || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format ||
                category == UnicodeCategory.Control && !Rune.IsWhiteSpace(rune))
                continue;
            var whitespace = Rune.IsWhiteSpace(rune);
            var separate = whitespace || IsCjk(rune.Value) || IsPunctuation(rune, category);
            if (separate && buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
            if (whitespace) continue;
            if (separate) yield return rune.ToString();
            else buffer.Append(rune);
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private IEnumerable<long> WordPieces(string word)
    {
        var characters = word.EnumerateRunes().ToArray();
        if (characters.Length > 100) return [vocabulary["[UNK]"]];
        var output = new List<long>();
        for (var start = 0; start < characters.Length;)
        {
            var end = characters.Length;
            while (end > start)
            {
                var prefix = start == 0 ? "" : "##";
                for (var offset = start; offset < end; offset++) prefix += characters[offset].ToString();
                if (vocabulary.TryGetValue(prefix, out var id))
                {
                    output.Add(id);
                    break;
                }
                end--;
            }
            if (end == start) return [vocabulary["[UNK]"]];
            start = end;
        }
        return output;
    }

    private static bool IsCjk(int code) => code is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF
        or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F or >= 0x2B740 and <= 0x2B81F
        or >= 0x2B820 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;

    private static bool IsPunctuation(Rune rune, UnicodeCategory category) => rune.Value is >= 33 and <= 47
        or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126 ||
        category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
}
