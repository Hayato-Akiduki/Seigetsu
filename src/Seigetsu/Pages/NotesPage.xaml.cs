using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Seigetsu.Pages;

/// <summary>閉じる前などに、まだ保存していない内容を保存できる画面。</summary>
public interface ISavable
{
    void Flush();
}

public partial class NotesPage : UserControl, ISavable
{
    // 打つ手が止まってから保存する（v6 と同じ 0.8 秒）
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private bool _dirty;
    private bool _loading;

    public NotesPage()
    {
        InitializeComponent();
        _saveDelay.Tick += (_, _) => Flush();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 開くたびにデータベースから読み直す（v6 で書き換えられていても最新を出す）
        // 中身が同じなら入れ直さない（入れ直すとカーソルが先頭に戻るため）
        var saved = App.Database.GetNote();
        if (!_dirty && saved != NormalizedText)
        {
            _loading = true;
            Body.Text = saved;
            // 読み込んだ内容までは Ctrl+Z で消えないようにする
            Body.IsUndoEnabled = false;
            Body.IsUndoEnabled = true;
            _loading = false;
        }
        UpdateCount();
    }

    // ほかの画面に移るときは保存しておく
    private void OnUnloaded(object sender, RoutedEventArgs e) => Flush();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCount();
        if (_loading)
            return;
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

    /// <summary>未保存の内容があれば保存する（画面を移るとき・アプリを閉じるときにも呼ばれる）。</summary>
    public void Flush()
    {
        _saveDelay.Stop();
        if (!_dirty)
            return;
        App.Database.SaveNote(NormalizedText);
        _dirty = false;
        StatusText.Text = $"保存しました（{DateTime.Now:HH:mm}）";
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        Flush();
        var dialog = new SaveFileDialog
        {
            Title = "メモをTXTとして保存",
            FileName = $"seigetsu_note_{DateTime.Now:yyyyMMdd_HHmm}.txt",
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
            Main.Notify($"TXTファイルを保存できませんでした。\n{error.Message}", Wpf.Ui.Controls.ControlAppearance.Danger, 6);
        }
    }
}
