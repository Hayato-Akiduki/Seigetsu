using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Seigetsu.Data;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>閉じる前などに、まだ保存していない内容を保存できる画面。</summary>
public interface ISavable
{
    void Flush();
}

/// <summary>一覧に出すメモ 1 枚。書いている途中でも、名前と 1 行目がすぐ一覧に出るようにする。</summary>
public sealed class MemoListItem(Memo memo) : INotifyPropertyChanged
{
    private string _title = memo.Title;
    private string _body = memo.Body;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; } = memo.Id;

    public string Title
    {
        get => _title;
        set { _title = value; Changed(); Changed(nameof(DisplayTitle)); }
    }

    public string Body
    {
        get => _body;
        set { _body = value; Changed(); Changed(nameof(Preview)); }
    }

    /// <summary>名前が空なら「無題のメモ」と出す。</summary>
    public string DisplayTitle => NotesPage.DisplayTitleOf(_title);

    /// <summary>本文の最初の 1 行。</summary>
    public string Preview =>
        _body.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line != "") ?? "まだ何も書いていません";

    private void Changed([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// メモの画面。メモは何枚でも作れて、左の一覧で切りかえる。
/// 1 枚目（id = 1）は v6 から使っているメモで、v6 で開いても同じ中身が見える。
/// </summary>
public partial class NotesPage : UserControl, ISavable
{
    // 最後に開いていたメモを覚えておく設定（掲示板から開くときにも使う）
    public const string SelectedSetting = "memo_selected";

    // 打つ手が止まってから保存する（v6 と同じ 0.8 秒）
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly ObservableCollection<MemoListItem> _items = [];

    private MemoListItem? _current;
    private bool _dirty;
    private bool _loading;

    public NotesPage()
    {
        InitializeComponent();
        MemoList.ItemsSource = _items;
        _saveDelay.Tick += (_, _) => Flush();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    public static string DisplayTitleOf(string title) => string.IsNullOrWhiteSpace(title) ? "無題のメモ" : title.Trim();

    // 開くたびにデータベースから読み直す（v6 で書きかえられていても最新を出す）
    private void OnLoaded(object sender, RoutedEventArgs e) =>
        Reload(App.Database.GetIntSetting(SelectedSetting, 1));

    // ほかの画面に移るときは保存しておく
    private void OnUnloaded(object sender, RoutedEventArgs e) => Flush();

    /// <summary>一覧を読み直して、selectId のメモを開く（無ければいちばん上のメモ）。</summary>
    private void Reload(long selectId)
    {
        Flush();
        var memos = App.Database.GetMemos();
        _loading = true;
        _items.Clear();
        foreach (var memo in memos)
            _items.Add(new MemoListItem(memo));
        var target = _items.FirstOrDefault(i => i.Id == selectId) ?? _items.FirstOrDefault();
        MemoList.SelectedItem = target;
        _loading = false;
        SubtitleText.Text = $"{_items.Count}枚 ・ 入力した内容は自動で保存されます";
        if (target is not null)
            Show(target);
    }

    private void OnMemoSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MemoList.SelectedItem is not MemoListItem item || item == _current)
            return;
        Flush();
        Show(item);
    }

    /// <summary>右側にメモを出す。</summary>
    private void Show(MemoListItem item)
    {
        var switching = item.Id != _current?.Id;
        _current = item;
        _loading = true;
        if (TitleBox.Text != item.Title)
            TitleBox.Text = item.Title;
        // 中身が同じなら入れ直さない（入れ直すとカーソルが先頭に戻るため）
        if (switching || NormalizedText != item.Body)
        {
            Body.Text = item.Body;
            // 読み込んだ内容までは Ctrl+Z で消えないようにする。ほかのメモの書きかえも元に戻らないようにする
            Body.IsUndoEnabled = false;
            Body.IsUndoEnabled = true;
        }
        _loading = false;
        _dirty = false;
        if (switching)
            StatusText.Text = "保存済み";
        App.Database.SetSetting(SelectedSetting, item.Id.ToString());
        UpdateCount();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCount();
        if (_loading || _current is null)
            return;
        _current.Body = NormalizedText;
        MarkDirty();
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null)
            return;
        _current.Title = TitleBox.Text;
        MarkDirty();
    }

    // 名前を書いて Enter を押したら、本文を書けるようにする
    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        Body.Focus();
        Body.CaretIndex = Body.Text.Length;
        e.Handled = true;
    }

    private void MarkDirty()
    {
        _dirty = true;
        StatusText.Text = "編集中…";
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    /// <summary>
    /// 保存する本文。WPF は改行を \r\n で入れるが、v6 は \n なので \n にそろえる。
    /// </summary>
    private string NormalizedText => Body.Text.Replace("\r\n", "\n");

    private void UpdateCount()
    {
        // Python の len() と同じく、絵文字なども 1 文字と数える
        CountText.Text = $"{NormalizedText.EnumerateRunes().Count()}文字";
    }

    /// <summary>未保存の内容があれば保存する（メモを切りかえるとき・画面を移るとき・アプリを閉じるときにも呼ばれる）。</summary>
    public void Flush()
    {
        _saveDelay.Stop();
        if (!_dirty || _current is null)
            return;
        App.Database.SaveMemo(_current.Id, TitleBox.Text.Trim(), NormalizedText);
        _dirty = false;
        StatusText.Text = $"保存しました（{DateTime.Now:HH:mm}）";
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        Flush();
        var id = App.Database.AddMemo("新しいメモ");
        Reload(id);
        // すぐに名前を付けられるようにする
        TitleBox.Focus();
        TitleBox.SelectAll();
        Main.Notify("新しいメモを作りました。名前を書きかえられます。");
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_current is not { } memo)
            return;
        Flush();
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Main,
            Title = "削除の確認",
            Content = $"メモ「{memo.DisplayTitle}」を削除しますか？\nこの操作は元に戻せません。",
            PrimaryButtonText = "削除",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "キャンセル",
            FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        // 消したあとは、となりのメモを開く（下に無ければ上）
        var index = _items.IndexOf(memo);
        var next = index + 1 < _items.Count ? _items[index + 1] : index > 0 ? _items[index - 1] : null;
        App.Database.DeleteMemo(memo.Id);
        _current = null;
        _dirty = false;
        Reload(next?.Id ?? 0);
        Main.Notify("メモを削除しました。");
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        Flush();
        // ファイル名に使えない文字は「_」にする
        var name = string.Concat(DisplayTitleOf(TitleBox.Text)
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog
        {
            Title = "メモをTXTとして保存",
            FileName = $"{name}_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            DefaultExt = ".txt",
            Filter = "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(Main) != true)
            return;
        try
        {
            // v6 と同じく UTF-8（BOM なし）で書く
            File.WriteAllText(dialog.FileName, NormalizedText);
            Main.Notify($"TXTファイルを保存しました。\n{dialog.FileName}", seconds: 5);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.Notify($"TXTファイルを保存できませんでした。\n{error.Message}", ControlAppearance.Danger, 6);
        }
    }
}
