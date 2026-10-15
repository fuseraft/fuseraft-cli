using Terminal.Gui.Input;

namespace fuseraft.Cli.Tui;

// Ctrl+O: a tool's whole output, an approval's whole diff, or the model's whole thinking, over the transcript.
internal sealed partial class ReplTui
{
    // While the viewer is open it has the keys, so they scroll it rather than edit the draft or answer an approval
    // behind it. Esc closes it; anything else is ignored.
    bool OutputKey(Key key, int shown)
    {
        if (key == Key.Esc || key == Key.Q)
            CloseOutput();
        else if (key == Key.CursorLeft)
            ShowOutput(shown - 1);
        else if (key == Key.CursorRight)
            ShowOutput(shown + 1);
        else if (key == Key.CursorUp || key == Key.CursorDown)
            _outputView.ScrollBy(key == Key.CursorUp ? -1 : 1);
        else if (key == Key.PageUp || key == Key.PageDown || key == Key.Space)
            _outputView.Page(key == Key.PageUp ? -1 : 1);
        else if (key == Key.Home)
            _outputView.ScrollToTop();
        else if (key == Key.End)
            _outputView.Follow();
        return true;
    }

    // Opens the viewer on output <paramref name="index"/> (clamped, so int.MaxValue is the newest), over the transcript.
    void ShowOutput(int index)
    {
        var outputs = _renderer.Outputs;
        if (outputs.Count == 0)
        {
            _status.Flash("No tool output yet");
            return;
        }
        index = Math.Clamp(index, 0, outputs.Count - 1);
        if (index == _output)
            return;
        _output = index;
        var output = outputs[index];
        _outputText.Clear();
        if (output.Diff)
            TuiRenderer.Diff(_outputText, output.Text);
        else
            foreach (var line in output.Text.TrimEnd('\n').Split('\n'))
                _outputText.Write("  " + line.Replace("\t", "    ").Replace("\e", "") + "\n");
        _outputView.ScrollToTop();
        _outputView.Visible = true;
        _view.Visible = false;
        _rule.Note = $" {output.Title} · {index + 1} of {outputs.Count} · ←/→ older/newer · Esc to close ";
    }

    void ToggleOutput()
    {
        if (_output is null)
            ShowOutput(int.MaxValue);
        else
            CloseOutput();
    }

    void CloseOutput()
    {
        _output = null;
        _outputView.Visible = false;
        _view.Visible = true;
        _rule.Note = null;
        _outputText.Clear();
    }
}
