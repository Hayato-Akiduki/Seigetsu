using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Seigetsu.Data;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>プレイリスト 1 行分の表示用データ。</summary>
public sealed record TrackRow(
    Track Track, string Number, string Label, FontWeight Weight, Brush NameBrush, Brush NumberBrush, Brush RowBrush);

public partial class MusicPage : UserControl, IRefreshable
{
    // 音量のつまみを動かしている間は保存せず、止まってから保存する
    private readonly DispatcherTimer _volumeSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _loading;

    public MusicPage()
    {
        InitializeComponent();
        _volumeSave.Tick += (_, _) =>
        {
            _volumeSave.Stop();
            App.Database.SetSetting(SettingKeys.Volume, Player.Volume.ToString("0.00", CultureInfo.InvariantCulture));
        };
    }

    private static MusicPlayer Player => App.Player;

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Player.StateChanged += Refresh;
        _loading = true;
        VolumeSlider.Value = Math.Round(Player.Volume * 100);
        _loading = false;
        UpdateVolumeText();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Player.StateChanged -= Refresh;

    public void Refresh()
    {
        Player.SetTracks(App.Database.GetPlaylist());
        var current = Player.Current;
        var tracks = Player.Tracks;

        TrackText.Text = current?.Name ?? (Player.Error != "" ? "音声を再生できません" : "再生していません");
        StateText.Text = Player.Error != "" ? $"音声デバイスを確認してください（{Player.Error}）"
            : current is null ? (tracks.Count > 0 ? $"{tracks.Count}曲" : "曲を追加すると再生できます")
            : $"{(Player.Paused ? "一時停止中" : "再生中")} ・ {Player.Index + 1} / {tracks.Count}曲";
        PlayIcon.Symbol = Player.IsPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        RepeatButton.Appearance = Player.Repeat ? ControlAppearance.Primary : ControlAppearance.Secondary;
        ShuffleButton.Appearance = Player.Shuffle ? ControlAppearance.Primary : ControlAppearance.Secondary;

        var accent = ThemeService.Brush("AccentBrush");
        var muted = ThemeService.Brush("MutedBrush");
        TrackList.ItemsSource = tracks.Select((t, i) =>
        {
            var playing = i == Player.Index;
            var exists = File.Exists(t.Path);
            return new TrackRow(
                t,
                playing ? "♪" : $"{i + 1}",
                exists ? t.FileName : $"{t.FileName}（見つかりません）",
                playing ? FontWeights.Bold : FontWeights.Normal,
                exists ? ThemeService.Brush("TextBrush") : muted,
                playing ? accent : muted,
                playing ? ThemeService.Brush("SubtleBrush") : Brushes.Transparent);
        }).ToList();
        EmptyText.Visibility = tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>プレイヤーの操作。ファイルが無い・音が出せないときはお知らせを出す。</summary>
    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (FileNotFoundException error)
        {
            Main.Notify($"ファイルが見つかりません。\n{error.FileName}", ControlAppearance.Danger, 5);
        }
        catch (InvalidOperationException error)
        {
            Main.Notify(error.Message, ControlAppearance.Danger, 6);
        }
        catch (Exception error) when (error is IOException or FormatException or NAudio.MmException
                                       or System.Runtime.InteropServices.COMException)
        {
            // 壊れたファイルや、読めない形式のとき
            Main.Notify($"この曲を再生できませんでした。\n{error.Message}", ControlAppearance.Danger, 6);
        }
        Refresh();
        Main.TimerChanged();
    }

    private static Track TrackOf(object sender) => ((TrackRow)((FrameworkElement)sender).DataContext).Track;

    private int IndexOf(Track track) => Player.Tracks.ToList().FindIndex(t => t.Id == track.Id);

    private void OnToggleClick(object sender, RoutedEventArgs e) => Run(Player.Toggle);

    private void OnNextClick(object sender, RoutedEventArgs e) => Run(Player.Next);

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Run(Player.Previous);

    private void OnPlayRowClick(object sender, RoutedEventArgs e)
    {
        var index = IndexOf(TrackOf(sender));
        Run(() => Player.Play(index));
    }

    private void OnNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;
        var index = IndexOf(TrackOf(sender));
        Run(() => Player.Play(index));
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        var track = TrackOf(sender);
        if (Player.Current?.Id == track.Id)
            Player.Stop();
        App.Database.RemoveTrack(track.Id);
        Refresh();
        Main.TimerChanged();
    }

    private void OnRepeatClick(object sender, RoutedEventArgs e)
    {
        Player.Repeat = !Player.Repeat;
        Refresh();
    }

    private void OnShuffleClick(object sender, RoutedEventArgs e)
    {
        Player.Shuffle = !Player.Shuffle;
        Refresh();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded)
            return;
        Player.SetVolume(VolumeSlider.Value / 100);
        UpdateVolumeText();
        _volumeSave.Stop();
        _volumeSave.Start();
    }

    private void UpdateVolumeText() => VolumeText.Text = $"{(int)VolumeSlider.Value}%";

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "曲を追加",
            Multiselect = true,
            Filter = "音楽ファイル (*.mp3;*.wav;*.ogg)|*.mp3;*.wav;*.ogg|すべてのファイル (*.*)|*.*",
        };
        if (dialog.ShowDialog(Main) == true)
            Main.AddMusic(dialog.FileNames);
    }
}
