using Aide.Domain.Stack;

namespace Aide.UnitTests.Stack;

/// <summary>§4.16, §4.23: pack and frozen v2 globs (owned paths, plan rules, protected paths).</summary>
public class PathGlobTests
{
    [Theory]
    [InlineData("app/**", "app/page.tsx", true)]
    [InlineData("app/**", "app/(auth)/login/page.tsx", true)]
    [InlineData("app/**", "apps/page.tsx", false)]
    [InlineData("app/**", "src/app/page.tsx", false)]
    [InlineData("supabase/migrations/**", "supabase/migrations/0002_tasks.sql", true)]
    [InlineData("lib/*/**", "lib/tasks/rules.ts", true)]
    [InlineData("lib/*/**", "lib/types.ts", false)]
    [InlineData("**/*.test.ts", "lib/tasks/rules.test.ts", true)]
    [InlineData("**/*.test.ts", "rules.test.ts", true)]
    [InlineData("**/*.test.{ts,tsx}", "components/Button.test.tsx", true)]
    [InlineData("**/*.test.{ts,tsx}", "components/Button.test.js", false)]
    [InlineData("app/**/page.tsx", "app/page.tsx", true)]
    [InlineData("app/**/page.tsx", "app/a/b/page.tsx", true)]
    [InlineData("app/*.tsx", "app/a/page.tsx", false)]
    [InlineData("middleware.?s", "middleware.ts", true)]
    [InlineData("lib/database.types.ts", "lib/database.types.ts", true)]
    [InlineData("lib/database.types.ts", "lib/databaseXtypes.ts", false)]
    [InlineData("App/**", "app/page.tsx", true)]
    [InlineData("[x].ts", "[x].ts", true)]
    public void S4_11_Globs_with_a_slash_are_anchored_at_the_root(string pattern, string path, bool matches) =>
        Assert.Equal(matches, PathGlob.Create(pattern).IsMatch(path));

    [Theory]
    [InlineData(".env*", ".env", true)]
    [InlineData(".env*", ".env.local", true)]
    [InlineData(".env*", "apps/web/.env.production", true)]
    [InlineData(".env*", "src/env.ts", false)]
    [InlineData("package.json", "package.json", true)]
    [InlineData("package.json", "packages/ui/package.json", true)]
    public void S4_11_Globs_without_a_slash_match_the_name_in_any_folder(string pattern, string path, bool matches) =>
        Assert.Equal(matches, PathGlob.Create(pattern).IsMatch(path));

    [Theory]
    [InlineData("")]
    [InlineData(" app/**")]
    [InlineData("/app/**")]
    [InlineData("../secrets")]
    [InlineData("app//x")]
    [InlineData("./app")]
    [InlineData("app\\**")]
    [InlineData("{a,{b,c}}")]
    [InlineData("{a,b")]
    [InlineData("a}")]
    public void S4_11_Invalid_globs_are_rejected_with_a_reason(string pattern)
    {
        Assert.Null(PathGlob.TryCreate(pattern, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void S4_11_A_glob_set_matches_positives_minus_exclusions()
    {
        var ui = GlobSet.Create("app/**", "!app/api/**", "components/**");

        Assert.True(ui.IsMatch("app/tasks/page.tsx"));
        Assert.True(ui.IsMatch("components/TaskList.tsx"));
        Assert.False(ui.IsMatch("app/api/tasks/route.ts"));
        Assert.False(ui.IsMatch("lib/tasks/rules.ts"));
        Assert.False(GlobSet.Empty.IsMatch("app/page.tsx"));

        var reasons = new List<string>();
        GlobSet.Compile(["!app/**"], "owns", reasons);
        Assert.Equal("owns: only exclusions ('!…'); add the globs they exclude from.", Assert.Single(reasons));
    }
}
