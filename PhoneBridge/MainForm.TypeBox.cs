namespace PhoneBridge;

// Optional iPhone type box (Settings > iPhone type box). Keystrokes collect
// on the PC with normal Windows text editing, and go to the phone in one batch
// on Enter, through the same QueueIphoneText path live typing uses. Off by default.
public sealed partial class MainForm
{
    TypeBoxPopup _typeBox = null!;
    string _boxText = "";
    int _boxCaret, _boxAnchor;               // selection is [min, max) of caret and anchor
    readonly Stack<(string Text, int Caret)> _boxUndo = new();

    int SelStart => Math.Min(_boxCaret, _boxAnchor);
    int SelEnd => Math.Max(_boxCaret, _boxAnchor);
    bool HasSelection => _boxCaret != _boxAnchor;

    void ShowTypeBox()
    {
        int width = _stage.Width - S(16);
        _typeBox.Width_ = (int)Math.Round(width / ShellRenderer.Scale);
        _typeBox.Text_ = _boxText;
        _typeBox.Caret = _boxCaret;
        _typeBox.SelectionStart = SelStart;
        _typeBox.SelectionEnd = SelEnd;
        _typeBox.Open(new Point(Left + _stage.Left + S(8), Top + _stage.Bottom - S(50 + 10)));
        _typeBox.Redraw();
    }

    void CloseTypeBox()
    {
        _boxText = ""; _boxCaret = _boxAnchor = 0; _boxUndo.Clear();
        _typeBox.Dismiss();
    }

    void BoxEdit(Func<string> change, int caret)
    {
        _boxUndo.Push((_boxText, _boxCaret));
        _boxText = change();
        _boxCaret = _boxAnchor = Math.Clamp(caret, 0, _boxText.Length);
    }

    void BoxInsert(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int start = SelStart, end = SelEnd;
        BoxEdit(() => _boxText[..start] + text + _boxText[end..], start + text.Length);
    }

    void TypeBoxAppend(string text) { BoxInsert(text); ShowTypeBox(); }

    void BoxDeleteRange(int start, int end)
    {
        if (end <= start) return;
        BoxEdit(() => _boxText[..start] + _boxText[end..], start);
    }

    int WordLeft(int i)
    {
        while (i > 0 && char.IsWhiteSpace(_boxText[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_boxText[i - 1])) i--;
        return i;
    }

    int WordRight(int i)
    {
        while (i < _boxText.Length && !char.IsWhiteSpace(_boxText[i])) i++;
        while (i < _boxText.Length && char.IsWhiteSpace(_boxText[i])) i++;
        return i;
    }

    void BoxMove(int caret, bool extend)
    {
        _boxCaret = Math.Clamp(caret, 0, _boxText.Length);
        if (!extend) _boxAnchor = _boxCaret;
    }

    bool TypeBoxHandleKey(Keys keyData)
    {
        if (!_typeBox.IsOpen) return false;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0;
        var key = keyData & Keys.KeyCode;
        switch (key)
        {
            case Keys.Return when shift && !ctrl:
                BoxInsert("\n"); break;
            case Keys.Return:
                string text = _boxText;
                CloseTypeBox();
                foreach (char c in text) QueueIphoneText(c);
                return true;
            case Keys.Escape:
                CloseTypeBox();
                return true;
            case Keys.Back:
                if (HasSelection) BoxDeleteRange(SelStart, SelEnd);
                else if (ctrl) BoxDeleteRange(WordLeft(_boxCaret), _boxCaret);
                else if (_boxCaret > 0) BoxDeleteRange(_boxCaret - 1, _boxCaret);
                break;
            case Keys.Delete when shift && !ctrl:   // Shift+Delete = cut
                if (HasSelection) { TrySetClipboard(_boxText[SelStart..SelEnd]); BoxDeleteRange(SelStart, SelEnd); }
                break;
            case Keys.Delete:
                if (HasSelection) BoxDeleteRange(SelStart, SelEnd);
                else if (ctrl) BoxDeleteRange(_boxCaret, WordRight(_boxCaret));
                else if (_boxCaret < _boxText.Length) BoxDeleteRange(_boxCaret, _boxCaret + 1);
                break;
            case Keys.Left:
                if (!shift && !ctrl && HasSelection) BoxMove(SelStart, false);
                else BoxMove(ctrl ? WordLeft(_boxCaret) : _boxCaret - 1, shift);
                break;
            case Keys.Right:
                if (!shift && !ctrl && HasSelection) BoxMove(SelEnd, false);
                else BoxMove(ctrl ? WordRight(_boxCaret) : _boxCaret + 1, shift);
                break;
            case Keys.Home: case Keys.Up: BoxMove(0, shift); break;
            case Keys.End: case Keys.Down: BoxMove(_boxText.Length, shift); break;
            case Keys.A when ctrl: _boxAnchor = 0; _boxCaret = _boxText.Length; break;
            case Keys.C when ctrl:
            case Keys.Insert when ctrl:
                TrySetClipboard(HasSelection ? _boxText[SelStart..SelEnd] : _boxText); break;
            case Keys.X when ctrl:
                TrySetClipboard(HasSelection ? _boxText[SelStart..SelEnd] : _boxText);
                if (HasSelection) BoxDeleteRange(SelStart, SelEnd); else BoxDeleteRange(0, _boxText.Length);
                break;
            case Keys.V when ctrl:
            case Keys.Insert when shift:
                try { if (Clipboard.ContainsText()) BoxInsert(Clipboard.GetText()); } catch { }
                break;
            case Keys.Z when ctrl:
                if (_boxUndo.Count > 0) { var (t, c) = _boxUndo.Pop(); _boxText = t; _boxCaret = _boxAnchor = Math.Min(c, t.Length); }
                break;
            default:
                // Other Ctrl/Alt shortcuts stay in the box while it is open, so
                // they never act on the phone by surprise. Plain keys type via OnKeyPress.
                if (ctrl || (keyData & Keys.Alt) != 0) return true;
                return false;
        }
        if (_boxText.Length == 0 && key == Keys.Back && !HasSelection) { CloseTypeBox(); return true; }
        ShowTypeBox();
        return true;
    }

    static void TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }
}
