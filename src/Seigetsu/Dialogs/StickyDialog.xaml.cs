using System.Windows;
using System.Windows.Input;
using Seigetsu.Data;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>付箋の追加と編集を兼ねる入力画面。保存したら ShowDialog() が true を返す。</summary>
public partial class StickyDialog : FluentWindow
{
    private readonly bool _editing;

    public StickyDialog(StickyNote? note)
    {
        InitializeComponent();
        _editing = note is not null;
        Title = _editing ? "付箋を編集" : "新しい付箋";
        HeadingText.Text = Title;
        SaveButton.Content = _editing ? "保存" : "追加";
        TitleBox.Text = note?.Title ?? "";
        BodyBox.Text = note?.Body ?? "";
    }

    public string ResultTitle { get; private set; } = "";
    public string ResultBody { get; private set; } = "";

    // 編集のときはすぐ内容を書けるように、内容の欄から始める（v6 と同じ）
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_editing)
        {
            BodyBox.Focus();
            BodyBox.CaretIndex = BodyBox.Text.Length;
        }
        else
        {
            TitleBox.Focus();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Save();
            e.Handled = true;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        // v6 は \n で保存しているので、改行をそろえる
        var body = BodyBox.Text.Replace("\r\n", "\n").Trim();
        if (body == "")
        {
            ErrorText.Text = "内容を入力してください。";
            BodyBox.Focus();
            return;
        }
        ResultTitle = string.IsNullOrWhiteSpace(TitleBox.Text) ? "付箋" : TitleBox.Text.Trim();
        ResultBody = body;
        DialogResult = true;
    }
}
