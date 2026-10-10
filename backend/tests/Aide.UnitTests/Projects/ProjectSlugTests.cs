using Aide.Domain.Projects;

namespace Aide.UnitTests.Projects;

/// <summary>FR-5: project slugs are kebab-case ASCII with an id suffix, independent of the globalization mode.</summary>
public class ProjectSlugTests
{
    private static readonly Guid Id = Guid.Parse("01a11a7e-8186-7dd2-8a8c-a32fd2c3fe94");

    [Theory]
    [InlineData("  Héllo, Wörld!  API v2 ", "hello-world-api-v2")]
    [InlineData("Crème Brûlée Øresund", "creme-brulee-oresund")]
    [InlineData("Straße Æsir Œuvre Þing", "strasse-aesir-oeuvre-thing")]
    [InlineData("Łódź Ćwierć Žluťoučký", "lodz-cwierc-zlutoucky")]
    [InlineData("Ångström ÿes Ÿ", "angstrom-yes-y")]
    [InlineData("e\u0301te\u0301", "ete")]
    [InlineData("a×b÷c", "a-b-c")]
    [InlineData("日本語", ProjectRules.SlugFallback)]
    [InlineData("---", ProjectRules.SlugFallback)]
    public void FR5_Slug_folds_accented_Latin_letters_to_ASCII(string name, string expectedBase) =>
        Assert.Equal($"{expectedBase}-c3fe94", ProjectRules.Slug(name, Id));

    [Fact]
    public void FR5_Slug_base_is_capped_and_has_no_trailing_dash()
    {
        var slug = ProjectRules.Slug(new string('a', 49) + " ß" + new string('b', 20), Id);

        Assert.Equal(new string('a', 49) + "-c3fe94", slug);
    }

    [Fact]
    public void FR5_Slug_long_suffix_on_collision() =>
        Assert.Equal("app-a32fd2c3fe94", ProjectRules.Slug("App", Id, ProjectRules.SlugLongSuffixLength));

    [Fact]
    public void FR5_Every_Latin_supplement_and_extended_A_letter_folds_to_lowercase_ASCII()
    {
        for (var c = '\u00C0'; c <= '\u017F'; c++)
        {
            if (c is '\u00D7' or '\u00F7')
            {
                continue;
            }

            var slug = ProjectRules.Slug(c.ToString(), Id);
            Assert.Matches("^[a-z]{1,2}-c3fe94$", slug);
        }
    }
}
