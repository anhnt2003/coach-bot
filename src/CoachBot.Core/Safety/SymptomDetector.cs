using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CoachBot.Core.Domain;

namespace CoachBot.Core.Safety;

public sealed record SymptomAssessment(SymptomCategory Category, IReadOnlyList<string> Matched)
{
    public bool IsRedFlag => Category != SymptomCategory.None;
}

/// <summary>
/// Deterministic red-flag detection for English and Vietnamese free text. Deliberately over-inclusive:
/// a false alarm costs a rest day, a miss could cost far more. An LLM may add flags but never clears these.
/// </summary>
public static partial class SymptomDetector
{
    // Matched against lower-cased text with diacritics intact.
    private static readonly (SymptomCategory Category, string[] Phrases)[] Accented =
    [
        (SymptomCategory.Cardiac, ["đau ngực", "tức ngực", "ngất", "xỉu", "choáng", "chóng mặt", "tim đập nhanh", "tim đập loạn",
            "hồi hộp", "đánh trống ngực", "khó thở", "hụt hơi bất thường"]),
        (SymptomCategory.Injury, ["đau xương", "đau nhói", "sưng", "khập khiễng", "gãy", "rạn xương", "đau tăng khi chạy",
            "không thể chạy", "không đi được", "không chịu được lực"]),
        (SymptomCategory.Illness, ["sốt", "cảm cúm", "cúm", "ớn lạnh", "nôn", "tiêu chảy", "viêm họng", "covid"]),
    ];

    // Matched on word boundaries against lower-cased, diacritic-free text (also catches Vietnamese typed without accents).
    private static readonly (SymptomCategory Category, string[] Phrases)[] Plain =
    [
        (SymptomCategory.Cardiac, ["chest pain", "chest tightness", "chest pressure", "tight chest", "faint", "fainted", "fainting",
            "passed out", "blacked out", "blackout", "palpitation", "palpitations", "irregular heartbeat", "heart racing",
            "racing heart", "short of breath", "shortness of breath", "can't breathe", "cant breathe", "dizzy", "dizziness",
            "lightheaded", "light-headed", "dau nguc", "tuc nguc", "chong mat", "kho tho", "tim dap nhanh"]),
        (SymptomCategory.Injury, ["sharp pain", "stabbing pain", "bone pain", "stress fracture", "fracture", "limp", "limping",
            "swelling", "swollen", "can't put weight", "cant put weight", "pain getting worse", "pain gets worse",
            "worsening pain", "pain when walking", "dau xuong", "khap khieng"]),
        (SymptomCategory.Illness, ["fever", "feverish", "flu", "chills", "vomiting", "vomited", "diarrhea", "diarrhoea",
            "covid", "chest infection", "cam cum"]),
    ];

    public static SymptomAssessment Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(SymptomCategory.None, []);
        }

        var lower = text.ToLowerInvariant();
        var plain = WhitespaceRegex().Replace(RemoveDiacritics(lower), " ");
        var matched = new List<(SymptomCategory Category, string Phrase)>();

        foreach (var (category, phrases) in Accented)
        {
            matched.AddRange(phrases.Where(lower.Contains).Select(p => (category, p)));
        }

        foreach (var (category, phrases) in Plain)
        {
            matched.AddRange(phrases
                .Where(p => Regex.IsMatch(plain, $@"(?<![\p{{L}}]){Regex.Escape(p)}(?![\p{{L}}])"))
                .Select(p => (category, p)));
        }

        if (matched.Count == 0)
        {
            return new(SymptomCategory.None, []);
        }

        var top = matched.Max(m => m.Category);
        return new(top, [.. matched.Select(m => m.Phrase).Distinct()]);
    }

    internal static string RemoveDiacritics(string text)
    {
        var normalized = text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
