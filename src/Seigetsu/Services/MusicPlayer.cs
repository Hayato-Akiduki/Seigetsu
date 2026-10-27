using System.Windows.Threading;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Seigetsu.Services;

/// <summary>プレイリストの 1 曲（playlist テーブルの id とファイルの場所）。</summary>
public sealed record Track(long Id, string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// BGM プレイヤー（v6 の MusicPlayer と同じ動き）。mp3 / wav は Windows の仕組みで、ogg は NAudio.Vorbis で読む。
/// 曲が終わったら、リピートなら同じ曲、そうでなければ次の曲（シャッフルなら別の曲をランダムに）へ進む。
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    public static readonly string[] Extensions = [".mp3", ".wav", ".ogg"];

    private const int FadeInMilliseconds = 600;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Random _random = new();
    private WasapiPlayer? _output;
    private IDisposable? _reader;
    private VolumeSampleProvider? _volumeProvider;

    public MusicPlayer(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
    }

    /// <summary>再生や曲の切り替えなどで状態が変わったとき（画面の表示を合わせるため）。</summary>
    public event Action? StateChanged;

    public IReadOnlyList<Track> Tracks { get; private set; } = [];

    public int Index { get; private set; } = -1;

    public bool Paused { get; private set; }

    public bool Repeat { get; set; }

    public bool Shuffle { get; set; }

    public double Volume { get; private set; }

    /// <summary>音声デバイスが使えないときの説明（使えるときは空）。</summary>
    public string Error { get; private set; } = "";

    public Track? Current => Index >= 0 && Index < Tracks.Count ? Tracks[Index] : null;

    public bool IsPlaying => Current is not null && !Paused;

    /// <summary>プレイリストを入れ替える。再生中の曲が残っていれば、その曲の位置を保つ。</summary>
    public void SetTracks(IReadOnlyList<Track> tracks)
    {
        var current = Current?.Path;
        Tracks = tracks;
        Index = current is null ? -1 : tracks.ToList().FindIndex(t => t.Path == current);
        if (current is not null && Index < 0)
            Stop(); // 再生中の曲がプレイリストから消えた
    }

    /// <summary>曲を最初から再生する。ファイルが無ければ FileNotFoundException。</summary>
    public void Play(int index)
    {
        if (Tracks.Count == 0)
            return;
        index = ((index % Tracks.Count) + Tracks.Count) % Tracks.Count;
        var path = Tracks[index].Path;
        if (!File.Exists(path))
            throw new FileNotFoundException("ファイルが見つかりません。", path);

        CloseOutput();
        ISampleProvider reader = System.IO.Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new AudioFileReader(path);
        _reader = (IDisposable)reader;

        // 曲の頭を 0.6 秒かけて大きくする（v6 と同じ）
        var fade = new FadeInOutSampleProvider(reader, initiallySilent: true);
        fade.BeginFadeIn(FadeInMilliseconds);
        _volumeProvider = new VolumeSampleProvider(fade) { Volume = (float)Volume };

        try
        {
            // 共有モード（ほかのアプリの音と一緒に鳴らす）
            var output = new WasapiPlayerBuilder()
                .WithSharedMode()
                .Build();
            output.Init(new SampleToWaveProvider(_volumeProvider));
            output.PlaybackStopped += OnPlaybackStopped;
            _output = output;
            output.Play();
            Error = "";
        }
        catch (Exception error) when (error is not FileNotFoundException)
        {
            // 音声デバイスが無い・使えないとき
            CloseOutput();
            Error = error.Message;
            Index = -1;
            Paused = false;
            StateChanged?.Invoke();
            throw new InvalidOperationException($"音声を再生できませんでした。\n{error.Message}", error);
        }

        Index = index;
        Paused = false;
        StateChanged?.Invoke();
    }

    public void Toggle()
    {
        if (Tracks.Count == 0)
            return;
        if (Current is null)
        {
            Play(0);
            return;
        }
        if (Paused)
            _output?.Play();
        else
            _output?.Pause();
        Paused = !Paused;
        StateChanged?.Invoke();
    }

    public void Next()
    {
        if (Tracks.Count == 0)
            return;
        if (Shuffle && Tracks.Count > 1)
        {
            // 今の曲以外からランダムに選ぶ
            var choice = _random.Next(Tracks.Count - 1);
            Play(choice >= Index ? choice + 1 : choice);
        }
        else
        {
            Play(Index + 1);
        }
    }

    public void Previous()
    {
        if (Tracks.Count > 0)
            Play(Math.Max(Index, 0) - 1);
    }

    public void Stop()
    {
        CloseOutput();
        Index = -1;
        Paused = false;
        StateChanged?.Invoke();
    }

    public void SetVolume(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        if (_volumeProvider is not null)
            _volumeProvider.Volume = (float)Volume;
    }

    // 曲が最後まで流れたとき（自分で止めたときは CloseOutput で先に外しているので来ない）
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _output))
                return;
            try
            {
                if (Repeat)
                    Play(Index);
                else
                    Next();
            }
            catch (Exception)
            {
                // 次の曲が無い・読めないときは止める
                Stop();
            }
        });
    }

    private void CloseOutput()
    {
        var output = _output;
        _output = null; // 先に外しておくと、止めたときの PlaybackStopped を「曲の終わり」と間違えない
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            output.Stop();
            output.Dispose();
        }
        _reader?.Dispose();
        _reader = null;
        _volumeProvider = null;
    }

    public void Dispose() => CloseOutput();
}
