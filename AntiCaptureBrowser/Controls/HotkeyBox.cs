using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AntiCaptureBrowser.Controls;

/// <summary>
/// 只读输入框：按下组合键即可录入（如 Ctrl+Alt+B），用于设置页中的全局热键。
/// </summary>
public class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyValueProperty = DependencyProperty.Register(
        nameof(HotkeyValue), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly RoutedEvent HotkeyChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(HotkeyChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(HotkeyBox));

    public string HotkeyValue
    {
        get => (string)GetValue(HotkeyValueProperty);
        set => SetValue(HotkeyValueProperty, value);
    }

    public event RoutedEventHandler HotkeyChanged
    {
        add => AddHandler(HotkeyChangedEvent, value);
        remove => RemoveHandler(HotkeyChangedEvent, value);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "点击后按下组合键即可录入，例如 Ctrl+Alt+B";
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 纯修饰键按下时先不录入
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        bool isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            // 要求 Ctrl/Alt/Win 或 F 键，避免录入普通字符键干扰打字
            Text = "（请按 Ctrl/Alt 组合键或 F 功能键）";
            return;
        }

        HotkeyValue = Describe(modifiers, key);
        RaiseEvent(new RoutedEventArgs(HotkeyChangedEvent));
    }

    private static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var keyName = key.ToString();
        // WPF 数字键名为 D0..D9，解析器接受 "1".."9" 或 "D1"
        if (keyName.Length == 2 && keyName[0] == 'D' && char.IsAsciiDigit(keyName[1])) keyName = keyName[1..].ToString();

        parts.Add(keyName);
        return string.Join("+", parts);
    }
}
