using CoachBot.Core.Domain;
using CoachBot.Core.Safety;

namespace CoachBot.Core.Tests;

public class SymptomDetectorTests
{
    [Theory]
    [InlineData("Had some chest pain at km 8", SymptomCategory.Cardiac)]
    [InlineData("felt dizzy and nearly fainted", SymptomCategory.Cardiac)]
    [InlineData("Hôm nay bị đau ngực khi chạy", SymptomCategory.Cardiac)]
    [InlineData("hom nay bi dau nguc", SymptomCategory.Cardiac)]
    [InlineData("tim đập nhanh bất thường", SymptomCategory.Cardiac)]
    [InlineData("sharp pain in my shin, limping now", SymptomCategory.Injury)]
    [InlineData("chân bị sưng và đi khập khiễng", SymptomCategory.Injury)]
    [InlineData("I have a fever", SymptomCategory.Illness)]
    [InlineData("bị sốt từ tối qua", SymptomCategory.Illness)]
    [InlineData("toi bi sot va buon non", SymptomCategory.Illness)]
    [InlineData("dau nhoi o dau goi", SymptomCategory.Injury)]
    [InlineData("hom qua bi ngat xiu khi chay", SymptomCategory.Cardiac)]
    public void Detects_red_flags_in_english_and_vietnamese(string text, SymptomCategory expected)
    {
        var result = SymptomDetector.Detect(text);
        Assert.Equal(expected, result.Category);
        Assert.True(result.IsRedFlag);
    }

    [Theory]
    [InlineData("Legs felt heavy but fine overall")]
    [InlineData("Chạy rất tốt, cảm thấy khỏe")]
    [InlineData("chay cham mot xiu, thay sung suc")]
    [InlineData("influence of the heat")]
    [InlineData("")]
    [InlineData(null)]
    public void Normal_feedback_is_not_flagged(string? text)
    {
        Assert.False(SymptomDetector.Detect(text).IsRedFlag);
    }

    [Fact]
    public void Cardiac_outranks_other_categories()
    {
        Assert.Equal(SymptomCategory.Cardiac, SymptomDetector.Detect("fever and chest pain").Category);
    }
}
