using fuseraft.Cli.Tui;

namespace FuseraftCli.Tests;

public sealed class ThemeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-themes-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    void Write(string file, string json) => File.WriteAllText(Path.Combine(_dir, file), json);

    [Fact]
    public void NoConfig_IsTheDefaultTheme()
    {
        var (theme, warnings) = Theme.From(null, null);

        Assert.Equal(new Theme(), theme);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ABuiltInTheme_IsFoundByName()
    {
        var (theme, warnings) = Theme.From("Mono", null);

        Assert.Equal("mono", theme.Name);
        Assert.Equal("1", theme.Accent);
        Assert.Empty(warnings);
    }

    [Fact]
    public void TheLightTheme_UsesNoYellowOrBrightColors()
    {
        var light = Theme.Named("light")!;
        var codes = typeof(Theme).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name is not (nameof(Theme.Name) or nameof(Theme.Description) or nameof(Theme.Border)))
            .SelectMany(p => ((string)p.GetValue(light)!).Split(';'));

        Assert.DoesNotContain(codes, c => c == "33" || c.StartsWith('9'));
    }

    [Fact]
    public void TheRustTheme_DrawsIn256Colors()
    {
        var rust = Theme.Named("rust")!;

        Assert.Equal("38;5;208", rust.Accent);
        Assert.Equal(new Style(256 + 208, true, false), Theme.StyleOf(rust.Heading));
    }

    [Fact]
    public void Colors_OverrideSingleRoles()
    {
        var (theme, _) = Theme.From("bright", new Dictionary<string, string> { ["Accent"] = "bold magenta", ["code"] = "none" });

        Assert.Equal("1;35", theme.Accent);
        Assert.Equal("", theme.Code);
        Assert.Equal("92", theme.Success);
    }

    [Fact]
    public void WhatCantBeRead_IsReportedAndLeftAsItWas()
    {
        var (theme, warnings) = Theme.From("solarized", new Dictionary<string, string> { ["acent"] = "red", ["tool"] = "teal" });

        Assert.Equal(new Theme(), theme);
        Assert.Collection(warnings,
            w => Assert.Contains("Unknown theme 'solarized'", w),
            w => Assert.Contains("Unknown color role 'acent'", w),
            w => Assert.Contains("Can't read the color 'teal'", w));
    }

    [Theory]
    [InlineData("red", "31")]
    [InlineData("bright-blue", "94")]
    [InlineData("Gray", "90")]
    [InlineData("bold  underline green", "1;4;32")]
    [InlineData("none", "")]
    [InlineData("208", "38;5;208")]
    [InlineData("bold 0", "1;38;5;0")]
    [InlineData("256", null)]
    [InlineData("-1", null)]
    [InlineData("#ff8800", "38;2;255;136;0")]
    [InlineData("italic #F80", "3;38;2;255;136;0")]
    [InlineData("#ff88", null)]
    [InlineData("#gg0000", null)]
    [InlineData("orange", null)]
    [InlineData("on red", "41")]
    [InlineData("on bright-blue", "104")]
    [InlineData("bold on 238", "1;48;5;238")]
    [InlineData("#e8e0d8 on #1c1714", "38;2;232;224;216;48;2;28;23;20")]
    [InlineData("on", null)]
    [InlineData("on bold", null)]
    [InlineData("on on red", null)]
    public void Sgr_ReadsColorWords(string words, string? sgr) => Assert.Equal(sgr, Theme.Sgr(words));

    [Fact]
    public void StyleOf_ReadsACodeBackAsAStyle()
    {
        Assert.Equal(new Style(96, true, false), Theme.StyleOf("1;96"));
        Assert.Equal(default, Theme.StyleOf(""));
    }

    [Fact]
    public void AThemeFile_StartsFromItsBase_AndColorsRolesFromItsPalette()
    {
        Write("Ember.json", """
            // comments and trailing commas are fine
            {
              "description": "embers on charcoal",
              "base": "rust",
              "palette": { "ember": "#ff6a1f", "ash": "245", "night": "#1c1714" },
              "colors": { "accent": "bold ember", "muted": "ash", "error": "bright-red", "text": "ash on night" },
            }
            """);

        var (themes, warnings) = Theme.Load(_dir);

        Assert.Empty(warnings);
        var ember = Assert.Single(themes);
        Assert.Equal(("ember", "embers on charcoal"), (ember.Name, ember.Description));
        Assert.Equal("1;38;2;255;106;31", ember.Accent);
        Assert.Equal("38;5;245", ember.Muted);
        Assert.Equal("91", ember.Error);
        Assert.Equal("38;5;245;48;2;28;23;20", ember.Text);
        Assert.Equal(Theme.Named("rust")!.Tool, ember.Tool);
        Assert.Equal(ember, Theme.Named("EMBER", Theme.All(_dir)));
    }

    [Fact]
    public void ThemeFiles_ReportWhatCantBeRead_AndKeepTheRest()
    {
        Write("rust.json", "{}");
        Write("bad name.json", "{}");
        Write("broken.json", "{ nope");
        Write("odd.json", """{ "base": "solarized", "palette": { "red": "#f00", "fog": "teal" }, "colors": { "accent": "fog", "tool": "blue" } }""");

        var (themes, warnings) = Theme.Load(_dir);

        Assert.Equal("odd", Assert.Single(themes).Name);
        Assert.Equal("34", themes[0].Tool); // what could be read still is
        Assert.Collection(warnings,
            w => Assert.Contains("bad name.json: A theme's name can only have", w),
            w => Assert.Contains("broken.json: can't be read", w),
            w => Assert.Contains("odd.json: Unknown base theme 'solarized'", w),
            w => Assert.Contains("odd.json: The palette color 'red' needs a name", w),
            w => Assert.Contains("odd.json: Can't read the palette color 'teal' for 'fog'", w),
            w => Assert.Contains("odd.json: Can't read the color 'fog' for 'accent'", w),
            w => Assert.Contains("rust.json: 'rust' is a built-in theme's name", w));
    }

    [Fact]
    public void ASavedTheme_ReadsBackTheSame()
    {
        var mine = Theme.Named("rust")! with { Accent = "1;38;2;255;106;31", Muted = "2;3" };

        var (path, problem) = Theme.Save(mine, "mine", _dir);
        var loaded = Assert.Single(Theme.Load(_dir).Themes);

        Assert.Null(problem);
        Assert.Contains("\"accent\": \"bold #ff6a1f\"", File.ReadAllText(path!));
        Assert.Equal(mine with { Name = "mine", Description = "started from rust" }, loaded);
        Assert.Contains("already there", Theme.Save(mine, "mine", _dir).Problem);
        Assert.Contains("built-in", Theme.Save(mine, "light", _dir).Problem);
    }

    [Fact]
    public void EveryBuiltInColor_HasWordsThatReadBackTheSame()
    {
        foreach (var theme in Theme.BuiltIn)
            foreach (var role in Theme.Roles)
                Assert.Equal(role.Get(theme), Theme.Sgr(Theme.WordsOf(role.Get(theme))));
    }

    [Theory]
    [InlineData("38;2;232;224;216;48;2;28;23;20", "#e8e0d8 on #1c1714")]
    [InlineData("1;104", "bold on bright-blue")]
    [InlineData("48;5;238", "on 238")]
    [InlineData("", "none")]
    public void WordsOf_WritesBackgroundsWithOn(string sgr, string words)
    {
        Assert.Equal(words, Theme.WordsOf(sgr));
        Assert.Equal(sgr, Theme.Sgr(words));
    }

    [Theory]
    [InlineData(255, 135, 0, 208)]   // orange, in the cube
    [InlineData(128, 128, 128, 244)] // gray, on the ramp
    [InlineData(0, 0, 0, 16)]
    public void Nearest256_FindsTheClosestColor(int r, int g, int b, int n) => Assert.Equal(n, Theme.Nearest256(r, g, b));

    [Fact]
    public void To256_TurnsHexColorsInto256Colors_AndLeavesTheRest()
    {
        var theme = new Theme { Accent = "1;38;2;255;135;0", Tool = "38;5;33", Text = "38;2;255;135;0;48;2;28;23;20" }.To256();

        Assert.Equal(("1;38;5;208", "38;5;33", "32", "38;5;208;48;5;234"), (theme.Accent, theme.Tool, theme.Success, theme.Text));
    }

    [Theory]
    [InlineData("COLORTERM", "truecolor", true)]
    [InlineData("WT_SESSION", "x", true)]
    [InlineData("TERM_PROGRAM", "mintty", true)]
    [InlineData("TERM", "xterm-direct", true)]
    [InlineData("TERM", "xterm-256color", false)]
    public void ShowsTrueColor_ReadsTheEnvironment(string name, string value, bool shows) =>
        Assert.Equal(shows, Theme.ShowsTrueColor(n => n == name ? value : null));

    [Fact]
    public void TheSampleThemes_LoadWithoutWarnings_AndSetEveryRole()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "fuseraft.sln")))
            dir = dir.Parent!;
        var samples = Path.Combine(dir.FullName, "themes");

        var (themes, warnings) = Theme.Load(samples);

        Assert.Empty(warnings);
        Assert.Equal(Directory.GetFiles(samples, "*.json").Length, themes.Count);
        foreach (var file in Directory.GetFiles(samples, "*.json"))
            foreach (var role in Theme.Roles)
                Assert.Contains($"\"{role.Name}\":", File.ReadAllText(file));
    }
}
