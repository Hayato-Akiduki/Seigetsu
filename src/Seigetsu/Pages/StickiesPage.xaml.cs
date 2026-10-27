using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Seigetsu.Data;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>付箋 1 枚分の表示用データ。</summary>
public sealed record StickyView(StickyNote Note, Brush Background, string DateText);

public partial class StickiesPage : UserControl, IRefreshable
{
    public StickiesPage()
    {
        InitializeComponent();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        var notes = App.Database.GetStickies();
        SubtitleText.Text = notes.Count > 0 ? $"{notes.Count}枚 ・ クリックで編集できます" : "";
        Board.ItemsSource = notes.Select(n => new StickyView(
            n,
            ThemeService.StickyBrush(n.Id),
            // 2026-10-27T09:47:31 → 2026-10-27 09:47（v6 と同じ）
            n.CreatedAt.Length >= 16 ? n.CreatedAt[..16].Replace('T', ' ') : n.CreatedAt)).ToList();
        EmptyText.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static StickyNote NoteOf(object sender) => ((StickyView)((FrameworkElement)sender).DataContext).Note;

    private void OnAddClick(object sender, RoutedEventArgs e) => Main.OpenStickyDialog();

    private void OnCardClick(object sender, RoutedEventArgs e) => Main.OpenStickyDialog(NoteOf(sender));

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        // 付箋を押したことにならないよう、ここで止める
        e.Handled = true;
        var note = NoteOf(sender);
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Main,
            Title = "削除の確認",
            Content = $"付箋「{note.Title}」を削除しますか？\nこの操作は元に戻せません。",
            PrimaryButtonText = "削除",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "キャンセル",
            FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        App.Database.DeleteSticky(note.Id);
        Main.Notify("付箋を削除しました。");
        Main.DataChanged();
    }
}
