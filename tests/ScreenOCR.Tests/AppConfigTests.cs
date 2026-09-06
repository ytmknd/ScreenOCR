namespace ScreenOCR.Tests;

public sealed class AppConfigTests
{
    [Fact] public void DefaultsMatchSpecification()
    {
        var config = new AppConfig();
        Assert.Equal("Ctrl+Alt+6", config.Hotkey);
        Assert.Equal(OcrBackendNames.PpOcrV6Small, config.OcrEngine);
        Assert.Equal("python", config.PpOcrV6SmallPythonPath);
        Assert.Equal(120000, config.PpOcrV6SmallTimeoutMs);
        Assert.Equal("auto", config.OcrLanguage);
        Assert.True(config.RemoveCjkSpaces);
        Assert.True(config.AutoInvert);
        Assert.False(config.TryVertical);
        Assert.Empty(config.Replacements);
    }

    [Fact] public void ValidationNormalizesPpOcrV6SmallSettings()
    {
        var config = new AppConfig
        {
            OcrEngine = " PPOCRV6-SMALL ",
            PpOcrV6SmallPythonPath = "  py  ",
            PpOcrV6SmallTimeoutMs = 10
        };

        config.Validate();

        Assert.Equal(OcrBackendNames.PpOcrV6Small, config.OcrEngine);
        Assert.Equal("py", config.PpOcrV6SmallPythonPath);
        Assert.Equal(1000, config.PpOcrV6SmallTimeoutMs);
    }

    [Fact] public void ValidationFallsBackFromUnknownEngine()
    {
        var warnings = new List<string>();
        var config = new AppConfig { OcrEngine = "unknown" };

        config.Validate(warnings.Add);

        Assert.Equal(OcrBackendNames.PpOcrV6Small, config.OcrEngine);
        Assert.Single(warnings);
    }

    [Fact] public void ValidationDisablesOnlyBadRegex()
    {
        var config = new AppConfig { Replacements = [
            new ReplacementRule { Pattern = "[", Regex = true },
            new ReplacementRule { Pattern = "a", Replacement = "b", Regex = true }
        ]};
        config.Validate();
        Assert.False(config.Replacements[0].IsValid);
        Assert.True(config.Replacements[1].IsValid);
    }

    [Fact] public void UnknownPropertiesCanBeRetained()
    {
        var config = new AppConfig { ExtensionData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["futureOption"] = System.Text.Json.JsonDocument.Parse("42").RootElement.Clone()
        }};
        Assert.Equal(42, config.ExtensionData["futureOption"].GetInt32());
    }

    [Fact] public void MergeDefaultsMatchPlan()
    {
        var config = new AppConfig();
        Assert.True(config.MergeEngines);
        Assert.Equal(0.3, config.MergeMarginScore);
        Assert.Equal(0.30, config.MergeBaseCjkRatio);
        Assert.Equal(0.5, config.MergeCoverageAreaRatio);
        Assert.Equal(4, config.ScoreRareKanjiPenalty);
        Assert.Equal(6, config.ScoreMixedScriptPenalty);
        Assert.Equal(2, config.ScoreIsolatedLatinPenalty);
        Assert.Equal(2, config.ScoreEnglishLexiconBonus);
        Assert.Equal(1.0, config.ScorePlausibleCharWeight);
        Assert.Equal(1.0, config.ScoreLatinLexiconCharWeight);
        Assert.Equal(0.3, config.ScoreLatinNonLexiconCharWeight);
        Assert.Equal(0.5, config.ScoreDigitCharWeight);
        Assert.Null(config.OcrLanguages);
    }

    [Fact] public void EffectiveOcrLanguagesFallsBackToSingularWhenOcrLanguagesIsAbsent()
    {
        // ocrLanguages キーが無い（従来の）設定ファイルは、単数の ocrLanguage だけで単一エンジン動作する。
        var config = new AppConfig { OcrLanguage = "ja" };
        Assert.Equal(["ja"], config.EffectiveOcrLanguages);
    }

    [Fact] public void EffectiveOcrLanguagesUsesListWhenPresent()
    {
        var config = new AppConfig { OcrLanguages = ["ja", "en-US"] };
        Assert.Equal(["ja", "en-US"], config.EffectiveOcrLanguages);
    }

    [Fact] public void ScoreWeightsComputedPropertyReflectsConfiguredValues()
    {
        var config = new AppConfig { ScoreRareKanjiPenalty = 10, ScoreEnglishLexiconBonus = 0 };
        Assert.Equal(10, config.ScoreWeights.RareKanjiPenalty);
        Assert.Equal(0, config.ScoreWeights.EnglishLexiconBonus);
    }
}
