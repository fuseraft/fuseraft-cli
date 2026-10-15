using System.Text.Json;
using System.Text.Json.Serialization;

namespace fuseraft.Cli.Tui;

/// <summary>
/// The colors the full-screen REPL draws with, each an SGR code ("36", "1;94") for one role. Most built-in themes use the terminal's own
/// 16 colors, so they follow its palette and read on light and dark backgrounds alike; rust uses 256-colors (38;5;n) for
/// its oranges, which the 16 don't have, and theme files can use 24-bit ones (38;2;r;g;b) too.
/// </summary>
internal sealed record Theme
{
    public string Name { get; init; } = "default";

    /// <summary>
    /// Plain text and the screen behind it ("#e8e0d8 on #1c1714"); empty for the terminal's own. Only the full screen
    /// draws it: a plain terminal would show the background only behind the text, and keep it in the scrollback.
    /// </summary>
    public string Text { get; init; } = "";

    public string Description { get; init; } = "fuseraft's usual colors";

    /// <summary>fuseraft's own color: the caret, the spinner, the picker's choice.</summary>
    public string Accent { get; init; } = "36";

    /// <summary>A tool call's name. Bright blue: plain blue is too dark to read on most dark terminal themes.</summary>
    public string Tool { get; init; } = "94";

    /// <summary>A sub-agent's tag.</summary>
    public string Agent { get; init; } = "35";

    public string Success { get; init; } = "32";

    public string Warning { get; init; } = "33";

    public string Error { get; init; } = "31";

    /// <summary>What's there but not the point: tool arguments, token counts, hints.</summary>
    public string Muted { get; init; } = "2";

    /// <summary>The line above the prompt and the status line.</summary>
    public string Border { get; init; } = "90";

    public string Heading { get; init; } = "1";

    public string Link { get; init; } = "4";

    /// <summary>The chosen suggestion and search matches, on top of what they mark; empty for the accent and reversed colors.</summary>
    public string Selection { get; init; } = "";

    /// <summary>Inline code, and code blocks in a language the highlighter doesn't know.</summary>
    public string Code { get; init; } = "36";

    /// <summary>Under a whole code block, to the edge of the screen ("on #2a201b"); full screen only, like <see cref="Text"/>.</summary>
    public string CodeBlock { get; init; } = "";

    public string Comment { get; init; } = "2";

    public string String { get; init; } = "32";

    public string Constant { get; init; } = "33";

    public string Keyword { get; init; } = "35";

    public string Function { get; init; } = "94";

    public string Type { get; init; } = "36";

    static Theme _current = new();
    static Theme _chosen = new();

    /// <summary>
    /// The theme everything draws with; set once at startup, and by /theme. Where the terminal doesn't say it shows 24-bit
    /// color, a theme's hex colors are drawn as the nearest 256-colors.
    /// </summary>
    public static Theme Current
    {
        get => _current;
        set => (_chosen, _current) = (value, TrueColor ? value : value.To256());
    }

    /// <summary>The theme as it was set, before <see cref="Current"/> fit its colors to the terminal: what /theme save writes.</summary>
    public static Theme Chosen => _chosen;

    /// <summary>Whether the terminal shows 24-bit color.</summary>
    public static bool TrueColor { get; } = ShowsTrueColor(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Whether the environment says the terminal shows 24-bit color: COLORTERM, the standard way, or a terminal known to
    /// that doesn't always set it.
    /// </summary>
    public static bool ShowsTrueColor(Func<string, string?> env) =>
        env("COLORTERM") is "truecolor" or "24bit"
        || env("WT_SESSION") is not null
        || env("TERM_PROGRAM") is "iTerm.app" or "WezTerm" or "vscode" or "ghostty" or "mintty"
        || env("TERM") is { } term && (term.EndsWith("-direct") || term is "xterm-kitty" or "xterm-ghostty" or "alacritty");

    public static readonly IReadOnlyList<Theme> BuiltIn =
    [
        new(),
        // For terminals whose normal colors are too dark: the bright variants, and nothing dim.
        new()
        {
            Name = "bright", Description = "bright colors, for dark terminals whose normal colors are too dark",
            Accent = "96", Tool = "94", Agent = "95", Success = "92", Warning = "93", Error = "91",
            Muted = "37", Code = "96", Comment = "37", String = "92", Constant = "93", Keyword = "95", Function = "94", Type = "96",
        },
        // For light backgrounds: no yellow and no bright variants, which wash out on white. Warnings are magenta instead.
        new()
        {
            Name = "light", Description = "for light backgrounds: no yellow or bright colors",
            Accent = "34", Tool = "34", Agent = "36", Success = "32", Warning = "35", Error = "31",
            Code = "34", Comment = "2", String = "32", Constant = "31", Keyword = "35", Function = "34", Type = "36",
        },
        // No colors at all, only bold, dim and underline.
        new()
        {
            Name = "mono", Description = "no colors, only bold, dim and underline",
            Accent = "1", Tool = "1", Agent = "1", Success = "", Warning = "1", Error = "1", Border = "2",
            Code = "", Comment = "2", String = "", Constant = "", Keyword = "1", Function = "", Type = "",
        },
        // Warm, for dark backgrounds: oranges and rust reds from the 256-color palette around the terminal's own white text.
        new()
        {
            Name = "rust", Description = "warm oranges and reds, for dark terminals",
            Accent = "38;5;208", Tool = "38;5;215", Agent = "38;5;174", Success = "38;5;143", Warning = "38;5;220", Error = "38;5;196",
            Muted = "38;5;138", Border = "38;5;94", Heading = "1;38;5;208", Link = "4;38;5;216",
            Code = "38;5;216", Comment = "38;5;138", String = "38;5;180", Constant = "38;5;209", Keyword = "38;5;166", Function = "38;5;215", Type = "38;5;173",
        },
    ];

    /// <summary>Where theme files live: ~/.fuseraft/themes, one &lt;name&gt;.json each.</summary>
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fuseraft", "themes");

    /// <summary>The built-in themes and the ones in <paramref name="dir"/> (by default <see cref="Dir"/>), read again each time.</summary>
    public static List<Theme> All(string? dir = null) => [.. BuiltIn, .. Load(dir ?? Dir).Themes];

    /// <summary>The theme the REPL settings name (repl.theme), with their colors (repl.colors) on top.</summary>
    public static Theme Of(string? name, IReadOnlyDictionary<string, string>? colors) => From(name, colors, All()).Theme;

    public static Theme? Named(string name, IEnumerable<Theme>? themes = null) =>
        (themes ?? BuiltIn).FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every role a theme colors, by the name configs and theme files give it, in the order they're listed.</summary>
    public static readonly IReadOnlyList<(string Name, Func<Theme, string> Get, Func<Theme, string, Theme> Set)> Roles =
    [
        ("text", t => t.Text, (t, v) => t with { Text = v }),
        ("accent", t => t.Accent, (t, v) => t with { Accent = v }),
        ("tool", t => t.Tool, (t, v) => t with { Tool = v }),
        ("agent", t => t.Agent, (t, v) => t with { Agent = v }),
        ("success", t => t.Success, (t, v) => t with { Success = v }),
        ("warning", t => t.Warning, (t, v) => t with { Warning = v }),
        ("error", t => t.Error, (t, v) => t with { Error = v }),
        ("muted", t => t.Muted, (t, v) => t with { Muted = v }),
        ("border", t => t.Border, (t, v) => t with { Border = v }),
        ("heading", t => t.Heading, (t, v) => t with { Heading = v }),
        ("link", t => t.Link, (t, v) => t with { Link = v }),
        ("selection", t => t.Selection, (t, v) => t with { Selection = v }),
        ("code", t => t.Code, (t, v) => t with { Code = v }),
        ("codeblock", t => t.CodeBlock, (t, v) => t with { CodeBlock = v }),
        ("comment", t => t.Comment, (t, v) => t with { Comment = v }),
        ("string", t => t.String, (t, v) => t with { String = v }),
        ("constant", t => t.Constant, (t, v) => t with { Constant = v }),
        ("keyword", t => t.Keyword, (t, v) => t with { Keyword = v }),
        ("function", t => t.Function, (t, v) => t with { Function = v }),
        ("type", t => t.Type, (t, v) => t with { Type = v }),
    ];

    static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "30", ["red"] = "31", ["green"] = "32", ["yellow"] = "33", ["blue"] = "34", ["magenta"] = "35", ["cyan"] = "36", ["white"] = "37",
        ["gray"] = "90", ["grey"] = "90", ["bright-red"] = "91", ["bright-green"] = "92", ["bright-yellow"] = "93", ["bright-blue"] = "94",
        ["bright-magenta"] = "95", ["bright-cyan"] = "96", ["bright-white"] = "97",
        ["bold"] = "1", ["dim"] = "2", ["italic"] = "3", ["underline"] = "4",
    };

    const string ColorHelp = "Use words like red, bright-blue, gray, bold, dim, italic, underline, a 256-color number from 0 to 255, a hex color such as #ff8800, \"on\" and a color for the background, or none.";

    /// <summary>
    /// The theme a config asks for: one of <paramref name="themes"/> (by default the built-in ones) by <paramref name="name"/>,
    /// with <paramref name="colors"/> mapping roles to words such as "bold magenta" (or "none") on top. Anything not
    /// understood is reported and left as it was.
    /// </summary>
    public static (Theme Theme, List<string> Warnings) From(string? name, IReadOnlyDictionary<string, string>? colors, IReadOnlyList<Theme>? themes = null)
    {
        themes ??= BuiltIn;
        List<string> warnings = [];
        var theme = BuiltIn[0];
        if (name is not null)
        {
            if (Named(name, themes) is { } named)
                theme = named;
            else
                warnings.Add($"Unknown theme '{name}'; using the default. Themes: {string.Join(", ", themes.Select(t => t.Name))}.");
        }
        return (Paint(theme, colors, null, warnings, ""), warnings);
    }

    // Colors roles of a theme by words, which can name colors in the palette; what can't be read is reported, after where.
    static Theme Paint(Theme theme, IReadOnlyDictionary<string, string>? colors, IReadOnlyDictionary<string, string>? palette, List<string> warnings, string where)
    {
        foreach (var (role, value) in colors ?? new Dictionary<string, string>())
        {
            if (Roles.FirstOrDefault(r => r.Name.Equals(role, StringComparison.OrdinalIgnoreCase)) is not { Set: { } set })
                warnings.Add($"{where}Unknown color role '{role}'. Roles: {string.Join(", ", Roles.Select(r => r.Name))}.");
            else if (Sgr(value, palette) is { } sgr)
                theme = set(theme, sgr);
            else
                warnings.Add($"{where}Can't read the color '{value}' for '{role}'. {ColorHelp}" + (palette is { Count: > 0 } ? $" Or a palette color: {string.Join(", ", palette.Keys)}." : ""));
        }
        return theme;
    }

    /// <summary>
    /// Color words ("bold bright-cyan", a 256-color number such as "208", a hex color such as "#ff8800", or a name from
    /// <paramref name="palette"/>) as an SGR code, "" for "none", or null when a word isn't one. "on" makes the color
    /// after it the background: "#e8e0d8 on #1c1714".
    /// </summary>
    public static string? Sgr(string words, IReadOnlyDictionary<string, string>? palette = null)
    {
        List<string> codes = [];
        var on = false;
        foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("none", StringComparison.OrdinalIgnoreCase) && !on)
                continue;
            if (word.Equals("on", StringComparison.OrdinalIgnoreCase) && !on)
            {
                on = true;
                continue;
            }
            var code = Words.TryGetValue(word, out var known) ? known
                : word.All(char.IsAsciiDigit) && int.TryParse(word, out var n) && n <= 255 ? $"38;5;{n}"
                : Hex(word) is var (r, g, b) ? $"38;2;{r};{g};{b}"
                : palette is not null && palette.TryGetValue(word, out var named) && Sgr(named) is { Length: > 0 } color ? color
                : null;
            if (on)
            {
                // One color, as a background.
                if (code is null || Background(code) is not { } background)
                    return null;
                codes.Add(background);
                on = false;
            }
            else if (code is null)
                return null;
            else
                codes.Add(code);
        }
        return on ? null : string.Join(';', codes);
    }

    // A foreground color's code as the background's: 31 is 41, 91 is 101, 38;... is 48;.... Null for anything else.
    static string? Background(string code) =>
        code.StartsWith("38;") ? "48" + code[2..]
        : int.TryParse(code, out var n) && n is >= 30 and <= 37 or >= 90 and <= 97 ? (n + 10).ToString()
        : null;

    // #rrggbb or #rgb.
    static (int R, int G, int B)? Hex(string word)
    {
        if (word.Length is not (4 or 7) || word[0] != '#' || !word.Skip(1).All(char.IsAsciiHexDigit))
            return null;
        var digits = word.Length == 4 ? string.Concat(word.Skip(1).Select(c => $"{c}{c}")) : word[1..];
        var rgb = Convert.ToInt32(digits, 16);
        return (rgb >> 16, (rgb >> 8) & 255, rgb & 255);
    }

    /// <summary>An SGR code as the words that make it, for writing a theme file: "1;38;5;208" is "bold 208".</summary>
    public static string WordsOf(string sgr)
    {
        var codes = sgr.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        List<string> words = [];
        for (var i = 0; i < codes.Length; i++)
        {
            var on = codes[i] is 48 or >= 40 and <= 47 or >= 100 and <= 107 ? "on " : "";
            if (codes[i] is 38 or 48 && i + 2 < codes.Length && codes[i + 1] == 5)
            {
                words.Add(on + codes[i + 2]);
                i += 2;
            }
            else if (codes[i] is 38 or 48 && i + 4 < codes.Length && codes[i + 1] == 2)
            {
                words.Add($"{on}#{codes[i + 2]:x2}{codes[i + 3]:x2}{codes[i + 4]:x2}");
                i += 4;
            }
            else
            {
                var code = (on.Length > 0 ? codes[i] - 10 : codes[i]).ToString();
                words.Add(on + Words.First(w => w.Value == code).Key);
            }
        }
        return words.Count == 0 ? "none" : string.Join(' ', words);
    }

    /// <summary>The theme with its hex colors (foreground and background) as the nearest 256-colors, for terminals that don't show 24-bit color.</summary>
    public Theme To256() => Roles.Aggregate(this, (theme, role) => role.Set(theme, To256(role.Get(theme))));

    static string To256(string sgr)
    {
        var codes = sgr.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 4 < codes.Length; i++)
            if (codes[i] is "38" or "48" && codes[i + 1] == "2")
                codes = [.. codes[..(i + 1)], "5", Nearest256(int.Parse(codes[i + 2]), int.Parse(codes[i + 3]), int.Parse(codes[i + 4])).ToString(), .. codes[(i + 5)..]];
        return string.Join(';', codes);
    }

    /// <summary>The 256-color closest to an RGB color: the nearer of the 6x6x6 cube's color and the gray ramp's.</summary>
    public static int Nearest256(int r, int g, int b)
    {
        static int Step(int c) => c < 48 ? 0 : c < 115 ? 1 : (c - 35) / 40;
        static int Distance((int R, int G, int B) x, int r, int g, int b) => (x.R - r) * (x.R - r) + (x.G - g) * (x.G - g) + (x.B - b) * (x.B - b);
        var cube = 16 + 36 * Step(r) + 6 * Step(g) + Step(b);
        var average = (r + g + b) / 3;
        var gray = 232 + (average > 238 ? 23 : Math.Max(0, (average - 3) / 10));
        return Distance(Rgb(gray), r, g, b) < Distance(Rgb(cube), r, g, b) ? gray : cube;
    }

    /// <summary>256-color <paramref name="n"/> (16 and up) as the RGB xterm gives it: a 6x6x6 cube, then 24 grays.</summary>
    public static (int R, int G, int B) Rgb(int n)
    {
        if (n >= 232)
            return (8 + (n - 232) * 10, 8 + (n - 232) * 10, 8 + (n - 232) * 10);
        static int Level(int c) => c == 0 ? 0 : 55 + c * 40;
        n -= 16;
        return (Level(n / 36), Level(n / 6 % 6), Level(n % 6));
    }

    sealed class File
    {
        public string? Description { get; set; }
        public string? Base { get; set; }
        public Dictionary<string, string>? Palette { get; set; }
        public Dictionary<string, string>? Colors { get; set; }
    }

    static readonly JsonSerializerOptions FileOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The themes in <paramref name="dir"/>, one per &lt;name&gt;.json: a description, a built-in theme to start from
    /// ("base"), a palette naming colors, and colors for roles in words or palette names. What can't be read is reported.
    /// </summary>
    public static (List<Theme> Themes, List<string> Warnings) Load(string dir)
    {
        List<Theme> themes = [];
        List<string> warnings = [];
        if (!Directory.Exists(dir))
            return (themes, warnings);
        foreach (var path in Directory.GetFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var where = $"themes/{Path.GetFileName(path)}: ";
            if (NameProblem(name, themes) is { } problem)
            {
                warnings.Add(where + problem);
                continue;
            }
            File? file;
            try
            {
                file = JsonSerializer.Deserialize<File>(System.IO.File.ReadAllText(path), FileOptions);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{where}can't be read: {e.Message}");
                continue;
            }
            if (file is null)
                continue;

            var theme = BuiltIn[0];
            if (file.Base is not null)
            {
                if (Named(file.Base) is { } named)
                    theme = named;
                else
                    warnings.Add($"{where}Unknown base theme '{file.Base}'; starting from the default. Built-in themes: {string.Join(", ", BuiltIn.Select(t => t.Name))}.");
            }
            Dictionary<string, string> palette = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (color, value) in file.Palette ?? [])
            {
                if (Words.ContainsKey(color) || color.Equals("none", StringComparison.OrdinalIgnoreCase) || color.Any(char.IsWhiteSpace) || Sgr(color) is not null)
                    warnings.Add($"{where}The palette color '{color}' needs a name that isn't already a color word.");
                else if (Sgr(value) is not { Length: > 0 })
                    warnings.Add($"{where}Can't read the palette color '{value}' for '{color}'. {ColorHelp}");
                else
                    palette[color] = value;
            }
            themes.Add(Paint(theme, file.Colors, palette, warnings, where) with { Name = name, Description = file.Description ?? $"from {Path.GetFileName(path)}" });
        }
        return (themes, warnings);
    }

    /// <summary>Why <paramref name="name"/> can't name a theme file next to <paramref name="others"/>, or null if it can.</summary>
    public static string? NameProblem(string name, IEnumerable<Theme>? others = null) =>
        name.Length == 0 || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_')
            ? "A theme's name can only have lowercase letters, digits, - and _."
        : name == "save" ? "'save' is taken by /theme save."
        : Named(name) is not null ? $"'{name}' is a built-in theme's name."
        : others is not null && Named(name, others) is not null ? $"There's already a theme named '{name}'."
        : null;

    /// <summary>
    /// Writes <paramref name="theme"/> to &lt;name&gt;.json in <paramref name="dir"/>, every role in words, for someone to
    /// change or share; never over a file that's there. Returns the path, or why it couldn't.
    /// </summary>
    public static (string? Path, string? Problem) Save(Theme theme, string name, string dir)
    {
        if (NameProblem(name) is { } problem)
            return (null, problem);
        var path = Path.Combine(dir, name + ".json");
        if (System.IO.File.Exists(path))
            return (null, $"{path} is already there; pick another name, or delete it first.");
        var file = new File
        {
            Description = $"started from {theme.Name}",
            Colors = Roles.ToDictionary(r => r.Name, r => WordsOf(r.Get(theme))),
        };
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(file, WriteOptions) + "\n");
        return (path, null);
    }

    /// <summary>A role's code as a transcript style, for what the TUI draws itself.</summary>
    public static Style StyleOf(string sgr)
    {
        var transcript = new Transcript();
        transcript.Write($"\e[{sgr}mx");
        return transcript.Lines()[0][0].Style;
    }
}
