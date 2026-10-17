using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace fuseraft.Cli.Tui;

// Approvals, questions and setup, asked in the panel that takes the prompt's place.
internal sealed partial class ReplTui
{
    // One of a few keys; anything else is ignored, so a stray key never answers. Esc answers the first.
    public Task<char> KeyAsync(string prompt, string keys, CancellationToken ct)
    {
        Ui(Ring);
        return PanelAsync<char>(ct, (panel, done) =>
        {
            panel.Show([(prompt, true)]);
            panel.OnKey = key =>
            {
                if (key == Key.Esc)
                    done(keys[0]);
                else if (!key.IsCtrl && !key.IsAlt && key.AsRune.Value is > 0 and < char.MaxValue and var rune && keys.Contains(char.ToLowerInvariant((char)rune)))
                    done(char.ToLowerInvariant((char)rune));
                return true;
            };
        });
    }

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, CancellationToken ct)
    {
        Ui(Ring);
        return ChooseAsync(title, choices, ct, quiet: true);
    }

    // The picker, drawn in the panel: ↑/↓, type to filter, Enter, Esc to dismiss (null).
    Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices, CancellationToken ct, bool quiet) =>
        PanelAsync<string?>(ct, quiet: quiet, setup: (panel, done) =>
        {
            var picker = new Picker(choices);
            var hint = choices.Count > Picker.DefaultHeight ? "↑/↓ to move, type to filter, Enter to choose, Esc to cancel" : "↑/↓ to move, Enter to choose, Esc to cancel";
            Draw();
            panel.OnKey = key =>
            {
                if (ConsoleKeyOf(key) is not { } info)
                    return false;
                switch (picker.Handle(info))
                {
                    case Picker.Outcome.Chosen:
                        done(picker.Result);
                        break;
                    case Picker.Outcome.Cancelled:
                        done(null);
                        break;
                    default:
                        Draw();
                        break;
                }
                return true;
            };

            void Draw() => panel.Show([($"{title} \e[2m({hint})\e[0m", false), .. picker.Lines()]);
        });

    // A line of text; secret masks it. Enter on an empty field answers "", Esc dismisses (null).
    public Task<string?> TextAsync(string prompt, bool secret, CancellationToken ct)
    {
        Ui(Ring);
        return PanelAsync<string?>(ct, (panel, done) =>
        {
            panel.Show([(prompt + " \e[2m(Enter to answer, Esc to cancel)\e[0m", true)]);
            var field = new TextField { X = 0, Y = 1, Width = Dim.Fill(), Secret = secret };
            panel.Add(field);
            field.SetFocus();
            field.Accepting += (_, e) =>
            {
                e.Handled = true;
                done(field.Text.Trim());
            };
            field.KeyDown += (_, key) =>
            {
                if (key == Key.Esc)
                {
                    key.Handled = true;
                    done(null);
                }
            };
            panel.OnKey = key =>
            {
                if (key != Key.Esc)
                    return false;
                done(null);
                return true;
            };
        }, rows: 2);
    }

    // Shows the panel in place of the prompt (whose draft is kept) until the answer comes, or until ct, which throws.
    // Keys arriving as it appears were meant for the prompt; they're thrown away until typing stops, unless the user
    // opened the panel (not quiet), when they're meant for it.
    async Task<T> PanelAsync<T>(CancellationToken ct, Action<PanelView, Action<T>> setup, int rows = 0, bool quiet = true)
    {
        await _panelTurn.WaitAsync(ct);
        try
        {
            var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var _ = ct.Register(() => answer.TrySetCanceled(ct));
            Ui(() =>
            {
                _panel.Reset(quiet ? DateTime.UtcNow : default, Quiet);
                setup(_panel, value => answer.TrySetResult(value));
                _panel.Rows = rows;
                _panel.Visible = true;
                _status.Asking = true;
                _prompt.Visible = _caret.Visible = false;
                Layout();
                if (_panel.SubViews.Count == 0)
                    _panel.SetFocus();
            });
            try
            {
                return await answer.Task;
            }
            finally
            {
                Ui(() =>
                {
                    _panel.Reset(default, Quiet);
                    _panel.Visible = false;
                    _status.Asking = false;
                    _prompt.Visible = _caret.Visible = true;
                    Layout();
                    _prompt.SetFocus();
                });
            }
        }
        finally
        {
            _panelTurn.Release();
        }
    }

    // The keys Picker understands, as the ConsoleKeyInfo it takes.
    static ConsoleKeyInfo? ConsoleKeyOf(Key key)
    {
        var plain = key.NoShift;
        ConsoleKey? special = plain == Key.Enter ? ConsoleKey.Enter
            : plain == Key.Esc ? ConsoleKey.Escape
            : plain == Key.CursorUp ? ConsoleKey.UpArrow
            : plain == Key.CursorDown ? ConsoleKey.DownArrow
            : plain == Key.PageUp ? ConsoleKey.PageUp
            : plain == Key.PageDown ? ConsoleKey.PageDown
            : plain == Key.Home ? ConsoleKey.Home
            : plain == Key.End ? ConsoleKey.End
            : plain == Key.Backspace ? ConsoleKey.Backspace
            : null;
        if (special is { } k)
            return new ConsoleKeyInfo('\0', k, false, false, false);
        if (key.IsCtrl || key.IsAlt || key.AsRune.Value is <= 0 or > char.MaxValue)
            return null;
        return new ConsoleKeyInfo((char)key.AsRune.Value, ConsoleKey.NoName, key.IsShift, false, false);
    }
}
