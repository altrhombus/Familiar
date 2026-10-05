using System.Text.Json;
using Familiar.Core.Languages;
using Familiar.Core.Recipes;

namespace Familiar.Core.Tests.Recipes;

public class RecipeTests
{
    private const string Minimal = """
        {
          // Comments and trailing commas are allowed so recipes can be annotated.
          "schemaVersion": 1,
          "source": { "path": "media\\sources\\install.wim", "index": 3 },
          "workingDirectory": "work",
          "languages": {
            "mediaPath": "lof",
            "languages": [ "de-DE", "ja-JP" ],
            "features": [ "OCR", "TextToSpeech" ],
            "defaultLanguage": "de-DE",
          },
          "output": { "path": "out\\install.wim" },
        }
        """;

    [Fact]
    public void Parse_ResolvesRelativePathsAgainstRecipeFolder()
    {
        var recipe = RecipeSerializer.Parse(Minimal, @"C:\Recipes");

        Assert.Equal(@"C:\Recipes\media\sources\install.wim", recipe.Source.Path);
        Assert.Equal(@"C:\Recipes\work", recipe.WorkingDirectory);
        Assert.Equal(@"C:\Recipes\lof", recipe.Languages!.MediaPath);
        Assert.Equal(@"C:\Recipes\out\install.wim", recipe.Output.Path);
    }

    [Fact]
    public void Parse_ReadsEnumsAsNames_AndAppliesDefaults()
    {
        var recipe = RecipeSerializer.Parse(Minimal, @"C:\Recipes");

        Assert.Equal([LanguageFeature.OCR, LanguageFeature.TextToSpeech], recipe.Languages!.Features);
        Assert.True(recipe.Languages.UpdateWinRE);
        Assert.True(recipe.Output.CleanupComponents);
    }

    [Fact]
    public void Parse_MissingOptionalValues_KeepTheirDefaults()
    {
        var recipe = RecipeSerializer.Parse(
            """{ "source": { "path": "a.wim" }, "languages": { "mediaPath": "x", "languages": ["de-DE"] }, "output": { "path": "o.wim" } }""",
            @"C:\Recipes");

        Assert.Equal(1, recipe.SchemaVersion);
        Assert.Equal(1, recipe.Source.Index);
        Assert.Equal(@"C:\FamiliarWork", recipe.WorkingDirectory);
        Assert.True(recipe.Languages!.IncludeFonts);
        Assert.True(recipe.Languages.UpdateWinRE);
        Assert.True(recipe.Output.CleanupComponents);
    }

    [Fact]
    public void Parse_RejectsUnknownProperties()
    {
        var typo = Minimal.Replace("\"defaultLanguage\"", "\"defaultLangauge\"", StringComparison.Ordinal);

        Assert.ThrowsAny<JsonException>(() => RecipeSerializer.Parse(typo, @"C:\Recipes"));
    }

    [Fact]
    public void Parse_RequiresSourceAndOutput()
    {
        Assert.ThrowsAny<JsonException>(() => RecipeSerializer.Parse("""{ "output": { "path": "x.wim" } }""", @"C:\"));
    }

    [Fact]
    public void Template_RoundTrips()
    {
        var json = RecipeSerializer.Serialize(RecipeSerializer.Template());

        var parsed = RecipeSerializer.Parse(json, @"C:\");

        Assert.Equal(RecipeSerializer.Template().Languages!.Languages, parsed.Languages!.Languages);
        Assert.Contains("\"defaultLanguage\": \"de-DE\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"features\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Template_PassesWithoutFileChecks()
    {
        Assert.Empty(RecipeValidator.Validate(RecipeSerializer.Template(), checkFiles: false));
    }

    [Fact]
    public void Validate_RejectsOverwritingTheSource()
    {
        var recipe = RecipeSerializer.Template() with { Output = new OutputSection { Path = @"E:\sources\install.wim" } };

        Assert.Contains(RecipeValidator.Validate(recipe, checkFiles: false), e => e.Contains("must differ", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOutputInsideWorkingDirectory()
    {
        var recipe = RecipeSerializer.Template() with { Output = new OutputSection { Path = @"C:\FamiliarWork\out.wim" } };

        Assert.Contains(RecipeValidator.Validate(recipe, checkFiles: false), e => e.Contains("workingDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DefaultLanguageMustBeRequested()
    {
        var template = RecipeSerializer.Template();
        var recipe = template with { Languages = template.Languages! with { DefaultLanguage = "fr-FR" } };

        Assert.Contains(RecipeValidator.Validate(recipe, checkFiles: false), e => e.Contains("fr-FR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\x\install.iso", "source.path")]
    [InlineData(@"C:\x\install.esd", null)]
    public void Validate_SourceMustBeWimOrEsd(string source, string? expected)
    {
        var recipe = RecipeSerializer.Template() with { Source = new SourceSection { Path = source } };

        var errors = RecipeValidator.Validate(recipe, checkFiles: false);

        if (expected is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Contains(errors, e => e.StartsWith(expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Validate_RejectsFutureSchema()
    {
        var recipe = RecipeSerializer.Template() with { SchemaVersion = 2 };

        Assert.Contains(RecipeValidator.Validate(recipe, checkFiles: false), e => e.Contains("schemaVersion", StringComparison.Ordinal));
    }
}
