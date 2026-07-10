using System.Windows.Controls;
using System.Windows.Input;
using ScreenClipTool.Config;

namespace ScreenClipTool.UI;

/// <summary>
/// Champ cliquable qui écoute le prochain KeyDown et enregistre
/// modificateurs (Ctrl/Alt/Shift/Win) + touche principale.
/// </summary>
public class HotkeyBox : TextBox
{
    private const string Hint = "Cliquer ici puis presser la combinaison…";

    public List<string> Modifiers { get; private set; } = new();
    public string? KeyName { get; private set; }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ClearCombo();
    }

    public void ClearCombo()
    {
        Modifiers = new List<string>();
        KeyName = null;
        Text = Hint;
    }

    public void SetCombo(IEnumerable<string> modifiers, string key)
    {
        Modifiers = modifiers.ToList();
        KeyName = key;
        Text = HotkeyBinding.Format(Modifiers, key);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (IsModifierKey(key) || key is Key.None or Key.DeadCharProcessed)
        {
            // Modificateur seul enfoncé : feedback en attendant la touche principale.
            Text = CurrentModifiers().Count > 0
                ? string.Join("+", CurrentModifiers()) + "+…"
                : Hint;
            return;
        }

        Modifiers = CurrentModifiers();
        KeyName = key.ToString();
        Text = HotkeyBinding.Format(Modifiers, KeyName);
    }

    private static List<string> CurrentModifiers()
    {
        var mods = new List<string>();
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
        if (m.HasFlag(ModifierKeys.Windows)) mods.Add("Win");
        return mods;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;
}
