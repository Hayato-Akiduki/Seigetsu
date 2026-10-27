using System.Text;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>文字数カウンター（v6 と同じく、文字数・空白を除いた文字数・行数を出す）。</summary>
public partial class CharCounterWindow : FluentWindow
{
    public CharCounterWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateCount();
        Box.Focus();
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var (chars, noSpace, lines) = Count(Box.Text);
        ResultText.Text = $"文字数 {chars}　／　空白を除く {noSpace}　／　行数 {lines}";
    }

    /// <summary>
    /// v6（Python）と同じ数え方。改行は 1 文字、絵文字なども 1 文字と数える。
    /// 「空白」には全角の空白・タブ・改行も入る。
    /// </summary>
    public static (int Chars, int NoSpace, int Lines) Count(string text)
    {
        // WPF は改行を \r\n で入れるが、v6 は \n なので 1 文字にそろえる
        text = text.Replace("\r\n", "\n");
        var chars = 0;
        var noSpace = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            chars++;
            if (!Rune.IsWhiteSpace(rune))
                noSpace++;
        }
        var lines = text.Count(c => c == '\n') + 1;
        return (chars, noSpace, lines);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        Box.Clear();
        Box.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
