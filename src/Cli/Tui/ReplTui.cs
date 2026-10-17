using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using fuseraft.Cli.Commands.Repl;
using fuseraft.Cli.Display;
using fuseraft.Core.Models;
using Spectre.Console;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace fuseraft.Cli.Tui;

/// <summary>
/// The full-screen REPL: the transcript, the prompt below it (or above it, after /dock top), and a status line. The
/// REPL runs on a background task and reaches the screen through <see cref="IReplScreen"/>; everything that touches a
/// view goes through Ui().
/// </summary>
/// <remarks>
/// While it runs, <see cref="AnsiConsole.Console"/> writes into the transcript instead of the terminal, so every
/// command and message keeps the look Spectre gives it on a plain terminal. <c>Console.Out</c> is left alone: it's
/// how Terminal.Gui reaches the terminal.
/// </remarks>
internal sealed partial class ReplTui : IReplScreen
{
    const int MaxPromptRows = 10;

    // How long typing has to stop before a panel takes keys, so a "y" typed into the draft never answers an approval.
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    // How long without a key before the user has probably looked away, and the bell rings when they're needed.
    internal static readonly TimeSpan Away = TimeSpan.FromSeconds(10);

    readonly IApplication _app;
    readonly Transcript _transcript = new();
    readonly TuiRenderer _renderer;
    readonly IAnsiConsole _console;
    readonly Channel<(string? Text, bool Echoed)> _sent = Channel.CreateUnbounded<(string?, bool)>(); // null ends the session
    readonly SemaphoreSlim _panelTurn = new(1, 1);
    readonly Runnable _top;
    readonly TranscriptView _view;
    readonly Transcript _outputText = new();
    readonly TranscriptView _outputView;
    int? _output; // which kept output the viewer shows, while it's open
    string? _find; // what Ctrl+F is looking for, while it's open
    int? _found; // the transcript line of the match it's on
    readonly RuleView _rule;
    readonly Label _caret;
    readonly PromptView _prompt;
    readonly PromptCompletion _completion = new();
    readonly SuggestView _suggest;
    readonly PanelView _panel;
    readonly StatusView _status;
    DateTime _lastInterrupt;
    DateTime _lastKey = DateTime.UtcNow;
    readonly bool _bell;
    Dock _dock;
    ReplStatus? _shown;
    int _redrawQueued;

    static bool Colored => Environment.GetEnvironmentVariable("NO_COLOR") is null;

    /// <summary>A real terminal on both ends; piped input or output gets the line-based REPL.</summary>
    public static bool Supported => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public Func<bool>? Interrupt { private get; set; }

    public Func<string, bool>? Interject { private get; set; }

    public int Width => Math.Max(20, _app.Screen.Width - 1);

    /// <summary>The session's skills, for $name completion.</summary>
    public Func<IReadOnlyList<string>>? Skills { set => _completion.Skills = value; }

    /// <summary>The workspace's files, for @ completion.</summary>
    public FileIndex? Files { set => _completion.Files = value; }

    ReplTui(IApplication app, ColorSystem colors, ReplDefaultsConfig settings)
    {
        _app = app;
        _bell = settings.Bell ?? true;
        _renderer = new TuiRenderer(_transcript, () => Width);
        _console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi        = AnsiSupport.Yes,
            ColorSystem = Colored ? (ColorSystemSupport)colors : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out         = new AnsiConsoleOutput(_transcript),
        });
        _console.Profile.Capabilities.Links   = false;
        _console.Profile.Capabilities.Unicode = true;

        _top = new Runnable { Width = Dim.Fill(), Height = Dim.Fill() };
        _view = new TranscriptView(_transcript) { X = 0, Y = 0, Width = Dim.Fill() };
        _outputView = new TranscriptView(_outputText) { X = 0, Y = 0, Width = Dim.Fill(), Visible = false };
        _rule = new RuleView { X = 0, Width = Dim.Fill(), Height = 1 };
        _caret = new Label { X = 0, Text = "›", Width = 2, Height = 1 };
        Paint();
        _prompt = new PromptView { X = 2, Width = Dim.Fill() };
        _suggest = new SuggestView { X = 0, Width = Dim.Fill(), Visible = false };
        _panel = new PanelView { X = 0, Width = Dim.Fill(), Visible = false };
        _status = new StatusView { X = 0, Width = Dim.Fill(), Height = 1 };
        _top.Add(_view, _outputView, _suggest, _rule, _caret, _prompt, _panel, _status);

        _view.Scrolled = () => _rule.Below = _view.Below;
        _view.Wheeled = _outputView.Wheeled = () => _lastKey = DateTime.UtcNow;
        _prompt.Sent += text =>
        {
            _prompt.Remember(text);
            Send(text);
            _view.Follow();
        };
        // After the edit has settled; the editor reports a change before its text reflects all of it.
        _prompt.ContentChanged += (_, _) => _app.AddTimeout(TimeSpan.Zero, () =>
        {
            _prompt.Suggest();
            Layout();
            return false;
        });
        _prompt.Completion = _completion;
        _prompt.SuggestionsChanged += () =>
        {
            _suggest.Show(_prompt.Suggestions, _prompt.Selected);
            Layout();
        };
        _transcript.Changed += Redraw;
        _outputText.Changed += () => Ui(_outputView.SetNeedsDraw);
        _app.Keyboard.KeyDown += OnKey;
        _app.Paste += (_, e) =>
        {
            // The editor doesn't take pastes itself; a field in the panel does.
            if (!_prompt.HasFocus)
                return;
            _prompt.Insert(e.Text.ReplaceLineEndings("\n"));
            e.Handled = true;
        };
        _app.ScreenChanged += (_, _) => Layout();
        if (settings.Dock is { } dock && Docks.Named(dock) is { } docked)
            _dock = docked;
        Layout();
    }

    /// <summary>
    /// Runs <paramref name="repl"/> inside the TUI, then prints the transcript to the terminal once it closes so the
    /// session stays in the scrollback.
    /// </summary>
    public static async Task<int> RunAsync(Func<ReplTui, Task<int>> repl)
    {
        // Asked of the terminal before Terminal.Gui owns it: the answer is read from stdin.
        _ = ThemeDetector.IsLightBackground;
        var settings = UserConfigStore.Load().Config?.Repl ?? new ReplDefaultsConfig();
        var (themeFiles, fileWarnings) = Theme.Load(Theme.Dir);
        var (theme, themeWarnings) = Theme.From(settings.Theme, settings.Colors, [.. Theme.BuiltIn, .. themeFiles]);
        Theme.Current = theme;

        var exit = 0;
        ExceptionDispatchInfo? failure = null;
        var colors = AnsiConsole.Profile.Capabilities.ColorSystem;
        var (savedConsole, savedError) = (AnsiConsole.Console, Console.Error);
        ReplTui tui;
        using (var app = Application.Create())
        {
            app.Init();
            tui = new ReplTui(app, colors, settings);
            AnsiConsole.Console = tui._console;
            Console.SetError(tui._transcript);
            ReplInput.Screen = tui;
            foreach (var warning in fileWarnings.Concat(themeWarnings))
                AnsiConsole.MarkupLine($"[yellow]⚠ {Markup.Escape(warning)}[/]");
            if (settings.Dock is { } dock && Docks.Named(dock) is null)
                AnsiConsole.MarkupLine($"[yellow]⚠ Unknown dock '{Markup.Escape(dock)}'; using the bottom. Positions: {Docks.Names}.[/]");
            Task work = Task.CompletedTask;
            app.AddTimeout(TimeSpan.Zero, () =>
            {
                work = Task.Run(async () =>
                {
                    try
                    {
                        exit = await repl(tui);
                    }
                    catch (Exception e)
                    {
                        failure = ExceptionDispatchInfo.Capture(e);
                    }
                    finally
                    {
                        app.Invoke(() => app.RequestStop());
                    }
                });
                return false;
            });
            try
            {
                app.Run(tui._top);
                await work;
            }
            finally
            {
                ReplInput.Screen = null;
                AnsiConsole.Console = savedConsole;
                Console.SetError(savedError);
            }
        }
        Console.Out.Write(tui._transcript.Ansi(color: Colored));
        Console.Out.Flush();
        failure?.Throw();
        return exit;
    }

    // While a turn runs, a message joins it; a command waits for it to end, and so does a message when nothing can
    // take it (a command is running). Otherwise the REPL is waiting for it.
    void Send(string text)
    {
        // Nothing reads it once the session is ending; it's in the history, for ↑ in the next one.
        if (_status.Ending)
        {
            _status.Flash("The session is ending, so that wasn't sent");
            return;
        }
        if (!_status.Working)
        {
            _sent.Writer.TryWrite((text, false));
            return;
        }
        var command = text.StartsWith('!') || (text.StartsWith('/') && !text.Contains('\n'));
        if (!command && Interject?.Invoke(text) == true)
        {
            _renderer.Line(Accent("› ") + text);
            return;
        }
        _sent.Writer.TryWrite((text, true));
        _renderer.Line(Accent("› ") + text + Muted(command ? "  (runs when this turn ends)" : "  (sent when this ends)"));
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        _renderer.End(turn: true);
        Working(false);
        var (text, echoed) = await _sent.Reader.ReadAsync(ct);
        if (text is not null)
        {
            if (!echoed)
                _renderer.Line(Accent("› ") + text);
            Working(true);
        }
        return text;
    }

    public void Ending() => Ui(() =>
    {
        _status.Ending = true;
        _status.Set("ending the session (saving memories)", working: true);
    });

    public void Status(ReplStatus status) => Ui(() =>
    {
        _shown = status;
        _status.Set(StatusText(status), _status.Working);
    });

    void Working(bool working) => Ui(() =>
    {
        if (_status.Working && !working)
            Ring();
        _status.Set(_shown is null ? "" : StatusText(_shown), working);
    });

    // The model in the accent, then how full the context is, green while compaction is out of reach, yellow when a turn
    // like the last one would get there, and red once it's past; then the modes.
    static string StatusText(ReplStatus s)
    {
        var used = $"{s.Context * 100:0}% context";
        var t = Theme.Current;
        used = s.Level switch
        {
            ContextLevel.Compacting => Paint(t.Error, used),
            ContextLevel.Near => Paint(t.Warning, used),
            _ => Paint(t.Success, used),
        };
        return string.Join(" · ", [Paint(t.Accent, s.Model), $"turn {s.Turn}", used, .. s.Modes]);
    }

    public void Text(string delta) => _renderer.Text(delta);

    public void Thinking(string delta) => _renderer.Thinking(delta);

    public void EndMessage(bool turn) => _renderer.End(turn);

    public void ToolStarted(string callId, string name, string summary, string? agent = null, string? arguments = null) =>
        _renderer.ToolStarted(callId, name, summary, agent, arguments);

    public void ToolFinished(string callId, string name, bool ok, string result, string? agent = null) =>
        _renderer.ToolFinished(callId, name, ok, result, agent);

    public void FileChanged(string path, int? added, int? removed) => _renderer.FileChanged(path, added, removed);

    public void Keep(string title, string text, bool diff) => _renderer.Keep(new ToolOutput(title, text, diff));

    /// <summary>Whether to ring: the user wants the bell, and hasn't pressed a key for <see cref="Away"/>.</summary>
    internal static bool ShouldRing(bool enabled, DateTime lastKey, DateTime now) => enabled && now - lastKey >= Away;

    // Called on the UI thread, so the bell goes out between frames.
    void Ring()
    {
        if (!ShouldRing(_bell, _lastKey, DateTime.UtcNow))
            return;
        Console.Out.Write('\a');
        Console.Out.Flush();
    }

    // The sequence goes out between frames, so it never lands inside one the driver is writing.
    public string? Copy(string text)
    {
        Ui(() =>
        {
            Console.Out.Write(Clipboard.Osc52(text));
            Console.Out.Flush();
        });
        return Clipboard.CopyNatively(text);
    }

    public string Docked => Docks.Name(_dock);

    public bool Dock(string position)
    {
        if (Docks.Named(position) is not { } dock)
            return false;
        Ui(() =>
        {
            _dock = dock;
            Layout();
        });
        return true;
    }

    public void Repaint() => Ui(() =>
    {
        Paint();
        if (_shown is not null)
            _status.Set(StatusText(_shown), _status.Working);
        _top.SetNeedsDraw();
    });

    // Keys that mean the same everywhere: Ctrl+C, Ctrl+D, Esc (never quits), scrolling the transcript, and Ctrl+O.
    void OnKey(object? sender, Key key)
    {
        _lastKey = DateTime.UtcNow;
        var ctrlC = key.KeyCode == (KeyCode.C | KeyCode.CtrlMask);
        if (!ctrlC)
        {
            // Any other key disarms a pending Ctrl+C exit, and a hint shown for the last key no longer applies.
            _lastInterrupt = default;
            _status.ClearFlash();
        }
        key.Handled = ctrlC ? Run(CtrlC)
            : key.KeyCode == (KeyCode.O | KeyCode.CtrlMask) ? Run(ToggleOutput)
            : _find is { } find ? FindKey(key, find)
            : _output is { } shown ? OutputKey(key, shown)
            : key.KeyCode == (KeyCode.F | KeyCode.CtrlMask) ? Run(OpenFind)
            : key.KeyCode == (KeyCode.Home | KeyCode.CtrlMask) ? Run(_view.ScrollToTop)
            : key.KeyCode == (KeyCode.R | KeyCode.CtrlMask) && !_panel.Visible ? Run(() => _ = SearchHistoryAsync())
            : key.KeyCode == (KeyCode.D | KeyCode.CtrlMask) && !_panel.Visible && _prompt.Text.Length == 0 ? Run(EndSession)
            : key == Key.Esc && !_panel.Visible ? Run(_prompt.Dismiss)
            : key == Key.PageUp || key == Key.PageDown ? Run(() => _view.Page(key == Key.PageUp ? -1 : 1))
            : key.KeyCode == (KeyCode.End | KeyCode.CtrlMask) ? Run(_view.Follow)
            : key.Handled;

        static bool Run(Action action)
        {
            action();
            return true;
        }
    }

    // Ending the session while a turn runs cancels the turn first.
    void EndSession()
    {
        if (_status.Working)
            Interrupt?.Invoke();
        _sent.Writer.TryWrite((null, false));
    }

    // Ctrl+C cancels the running turn, or else closes or clears what's open; twice in a row on an empty prompt exits.
    // An open question is answered as Esc would answer it first, so the turn behind it can stop.
    void CtrlC()
    {
        if (_panel.Visible)
            _panel.Dismiss();
        if (Interrupt?.Invoke() == true)
        {
            DropQueued();
            return;
        }
        if (_panel.Visible)
            return;
        if (_find is not null)
            CloseFind();
        else if (_output is not null)
            CloseOutput();
        else if (_prompt.Text.Length > 0)
            _prompt.Text = "";
        else if (DateTime.UtcNow - _lastInterrupt < TimeSpan.FromSeconds(2))
            _sent.Writer.TryWrite((null, false));
        else
        {
            _lastInterrupt = DateTime.UtcNow;
            _status.Flash("Press Ctrl+C again to exit", TimeSpan.FromSeconds(2));
        }
    }

    // Cancelling a turn drops what was sent to run after it.
    void DropQueued()
    {
        var dropped = 0;
        while (_sent.Reader.TryRead(out _))
            dropped++;
        if (dropped > 0)
            _renderer.Line(Muted("  (what you sent during the turn was dropped)"));
    }

    // The prompt's area grows with the draft (or the panel), and the transcript gets what's left.
    void Layout()
    {
        if (_prompt.Viewport.Y > 0 && _prompt.Rows(_app.Screen.Width - 2) <= MaxPromptRows)
            _prompt.Viewport = _prompt.Viewport with { Y = 0 };
        var rows = _panel.Visible ? _panel.Needed() : Math.Clamp(_prompt.Rows(_app.Screen.Width - 2), 1, MaxPromptRows);
        var suggested = _panel.Visible ? 0 : Math.Min(_prompt.Suggestions.Count, PromptCompletion.MaxShown);
        var frame = Arrange(_dock, _app.Screen.Height, rows, suggested);
        _suggest.Visible = suggested > 0;
        _suggest.Y = frame.Suggest;
        _suggest.Height = suggested;
        _view.Y = _outputView.Y = frame.Transcript;
        _view.Height = _outputView.Height = frame.TranscriptHeight;
        _rule.Y = frame.Rule;
        _caret.Y = _prompt.Y = _panel.Y = frame.Prompt;
        _prompt.Height = _panel.Height = rows;
        _status.Y = frame.Status;
        _console.Profile.Width = Width;
        _top.SetNeedsLayout();
    }

    /// <summary>The row each part starts on, and how many rows the transcript gets.</summary>
    internal readonly record struct Frame(int Transcript, int TranscriptHeight, int Suggest, int Rule, int Prompt, int Status);

    /// <summary>
    /// Lays out a screen <paramref name="height"/> rows tall, with <paramref name="rows"/> for the prompt (or panel) and
    /// <paramref name="suggested"/> suggestions. Docked at the bottom: transcript, suggestions, rule, prompt, status line.
    /// At the top it's the mirror image: status line, prompt, rule, suggestions, transcript.
    /// </summary>
    internal static Frame Arrange(Dock dock, int height, int rows, int suggested)
    {
        var transcript = Math.Max(0, height - rows - 2 - suggested);
        return dock == Tui.Dock.Top
            ? new(rows + 2 + suggested, transcript, rows + 2, rows + 1, 1, 0)
            : new(0, transcript, transcript, transcript + suggested, transcript + suggested + 1, height - 1);
    }

    void Redraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
            _app.Invoke(() =>
            {
                _redrawQueued = 0;
                _view.SetNeedsDraw();
            });
    }

    void Ui(Action action) => _app.Invoke(action);

    // The screen in the theme's text colors, which the views inherit, and the caret in its accent.
    void Paint()
    {
        var plain = Styled.Plain;
        _top.SetScheme(new Scheme(plain) { Focus = plain, Editable = plain, Active = plain });
        _caret.SetScheme(new Scheme(Styled.Accent));
    }

    static string Accent(string s) => Paint(Theme.Current.Accent, s);

    static string Muted(string s) => Paint(Theme.Current.Muted, s);

    static string Paint(string code, string s) => code.Length == 0 || s.Length == 0 ? s : $"\e[{code}m{s}\e[0m";
}
