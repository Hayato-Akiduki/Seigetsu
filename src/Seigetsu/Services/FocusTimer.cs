using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>Countdown は時間を決めて計る普通のタイマー（v7 で追加）。</summary>
public enum TimerMode { Stopwatch, Pomodoro, Countdown }

public enum TimerPhase { Work, Break }

/// <summary>時間が来て終わったもの。</summary>
public enum TimerFinish { Work, Break, Countdown }

/// <summary>
/// 集中タイマー（ストップウォッチ / ポモドーロ / タイマー）。ストップウォッチとポモドーロは v6 の FocusTimer と同じ動きで、
/// 計った時間は v6 と同じ mode 名で work_sessions に保存する（分析画面で v6 の記録と一緒に数えられる）。
/// タイマー（Countdown）は料理やカップ麺などにも使えるよう、作業の記録には入れない。
/// </summary>
public sealed class FocusTimer(SeigetsuDatabase db, TimeProvider? timeProvider = null)
{
    // work_sessions.mode に入れる名前（v6 と同じ）
    public const string StopwatchModeName = "通常";
    public const string PomodoroWorkModeName = "ポモドーロ-作業";

    // Tick の間がこれより空いたら、PC がスリープしていたとみなす（ふだんは 0.5 秒ごとに呼ばれる）
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SortedDictionary<DateTime, (DateTime Start, TimeSpan Duration)> _workDays = [];
    private long _lastTick;
    private TimeSpan _accumulated;

    private int? _startedPhaseSeconds;

    public TimerMode Mode { get; private set; } = TimerMode.Stopwatch;

    public TimerPhase Phase { get; private set; } = TimerPhase.Work;

    public bool Running { get; private set; }

    public bool IsPomodoro => Mode == TimerMode.Pomodoro;

    public bool IsCountdown => Mode == TimerMode.Countdown;

    /// <summary>残り時間を数えるモードか（ポモドーロとタイマー）。</summary>
    public bool CountsDown => Mode != TimerMode.Stopwatch;

    public string ModeName => Mode switch
    {
        TimerMode.Pomodoro => "ポモドーロ",
        TimerMode.Countdown => "タイマー",
        _ => "ストップウォッチ",
    };

    public bool IsIdle => !Running && _accumulated == TimeSpan.Zero;

    // ---- 設定値（設定画面で変えられる。v6 と同じキー） ------------------
    public int WorkSeconds => Math.Max(1, db.GetIntSetting(SettingKeys.WorkMinutes, 25)) * 60;

    public int BreakSeconds => Math.Max(1, db.GetIntSetting(SettingKeys.BreakMinutes, 5)) * 60;

    /// <summary>タイマーの長さ（秒）。v7 だけの設定。</summary>
    public int CountdownSeconds => Math.Clamp(db.GetIntSetting(SettingKeys.CountdownSeconds, 300), 1, MaxCountdownSeconds);

    public const int MaxCountdownSeconds = 24 * 3600 - 1;

    public int PhaseLength => Mode switch
    {
        TimerMode.Countdown => CountdownSeconds,
        _ => _startedPhaseSeconds ?? (Phase == TimerPhase.Work ? WorkSeconds : BreakSeconds),
    };

    // ---- 状態 ---------------------------------------------------------
    public double Elapsed => Running
        ? (_accumulated + _clock.GetElapsedTime(_lastTick)).TotalSeconds
        : _accumulated.TotalSeconds;

    public double Remaining => Math.Max(0, PhaseLength - Elapsed);

    /// <summary>0〜1。ポモドーロは今のフェーズ、タイマーは決めた時間の進み具合。ストップウォッチは 1 時間で一周。</summary>
    public double Progress => CountsDown
        ? Math.Min(1, Elapsed / PhaseLength)
        : Elapsed % 3600 / 3600;

    // ---- 操作 ---------------------------------------------------------
    public void Start()
    {
        if (Running)
            return;
        if (IsPomodoro)
            _startedPhaseSeconds ??= PhaseLength;
        Running = true;
        _lastTick = _clock.GetTimestamp();
    }

    public void Pause()
    {
        if (!Running)
            return;
        CaptureElapsed();
        Running = false;
    }

    public void Toggle()
    {
        if (Running)
            Pause();
        else
            Start();
    }

    /// <summary>それまでの時間を記録して、最初の状態に戻す。</summary>
    public void Reset()
    {
        RecordPartial();
        Running = false;
        _accumulated = TimeSpan.Zero;
        Phase = TimerPhase.Work;
        _startedPhaseSeconds = null;
    }

    public void SetMode(TimerMode mode)
    {
        if (mode == Mode)
            return;
        Reset();
        Mode = mode;
    }

    /// <summary>タイマーの長さを変える。動いている途中なら、最初からやり直す。</summary>
    public void SetCountdown(int seconds)
    {
        db.SetSetting(SettingKeys.CountdownSeconds, Math.Clamp(seconds, 1, MaxCountdownSeconds).ToString());
        if (!IsCountdown)
            return;
        Running = false;
        _accumulated = TimeSpan.Zero;
    }

    /// <summary>ポモドーロの今のフェーズを終えて、次へ進む。</summary>
    public void SkipPhase()
    {
        if (!IsPomodoro)
            return;
        RecordPartial();
        Running = false;
        _accumulated = TimeSpan.Zero;
        Phase = Next(Phase);
        _startedPhaseSeconds = null;
    }

    /// <summary>定期的に呼ぶ。ポモドーロのフェーズやタイマーが終わったら、終わったものを返す。</summary>
    public TimerFinish? Tick()
    {
        CaptureElapsed();
        if (!CountsDown || !Running || _accumulated.TotalSeconds < PhaseLength)
            return null;
        if (IsPomodoro && Phase == TimerPhase.Work)
            SaveWork();
        Running = false;
        _accumulated = TimeSpan.Zero;
        if (IsCountdown)
            return TimerFinish.Countdown; // 止めて、最初の長さに戻す
        var finished = Phase;
        Phase = Next(finished);
        _startedPhaseSeconds = null;
        return finished == TimerPhase.Work ? TimerFinish.Work : TimerFinish.Break;
    }

    // 前回から実際に計測した区間だけを足す。休憩と普通のタイマーはスリープ中も進める。
    private void CaptureElapsed()
    {
        if (!Running)
            return;
        var now = _clock.GetTimestamp();
        var interval = _clock.GetElapsedTime(_lastTick, now);
        _lastTick = now;
        if (Mode == TimerMode.Stopwatch || (IsPomodoro && Phase == TimerPhase.Work))
        {
            if (interval > SleepGap)
                return;
            var start = _clock.GetLocalNow().DateTime - interval;
            if (IsPomodoro)
            {
                var remaining = TimeSpan.FromSeconds(PhaseLength) - _accumulated;
                if (interval > remaining)
                    interval = remaining;
            }
            var end = start + interval;
            while (start < end)
            {
                var midnight = start.Date.AddDays(1);
                var until = end < midnight ? end : midnight;
                var day = _workDays.GetValueOrDefault(start.Date, (Start: start, Duration: TimeSpan.Zero));
                _workDays[start.Date] = (day.Start, day.Duration + (until - start));
                start = until;
            }
        }
        _accumulated += interval;
    }

    private static TimerPhase Next(TimerPhase phase) =>
        phase == TimerPhase.Work ? TimerPhase.Break : TimerPhase.Work;

    // 休憩時間とタイマーは記録しない（休憩は v6 と同じ）
    private void RecordPartial()
    {
        CaptureElapsed();
        SaveWork();
    }

    private void SaveWork()
    {
        if (_accumulated >= TimeSpan.FromSeconds(5) && _workDays.Count > 0)
            db.AddWorkDays(_workDays.Values, IsPomodoro ? PomodoroWorkModeName : StopwatchModeName);
        _workDays.Clear();
    }

    // ---- 表示用 -------------------------------------------------------
    public static string PhaseName(TimerPhase phase) => phase == TimerPhase.Work ? "作業" : "休憩";

    /// <summary>「5分」「1分30秒」「1時間30分」のような長さの書き方。</summary>
    public static string LengthLabel(int seconds)
    {
        var (h, m, sec) = (seconds / 3600, seconds % 3600 / 60, seconds % 60);
        var text = (h > 0 ? $"{h}時間" : "") + (m > 0 ? $"{m}分" : "") + (sec > 0 ? $"{sec}秒" : "");
        return text == "" ? "0秒" : text;
    }

    public string Display()
    {
        if (IsPomodoro)
        {
            var remaining = (int)Math.Round(Remaining);
            return $"{remaining / 60:00}:{remaining % 60:00}";
        }
        if (IsCountdown)
        {
            // 残りが 0.2 秒でも「00:01」と出す（0 になったらすぐ終わる）
            var remaining = (int)Math.Ceiling(Remaining);
            return remaining >= 3600
                ? $"{remaining / 3600}:{remaining % 3600 / 60:00}:{remaining % 60:00}"
                : $"{remaining / 60:00}:{remaining % 60:00}";
        }
        var elapsed = (int)Elapsed;
        return $"{elapsed / 3600:00}:{elapsed % 3600 / 60:00}:{elapsed % 60:00}";
    }

    public string Status()
    {
        if (IsCountdown)
        {
            if (Running)
                return "作動中";
            return _accumulated > TimeSpan.Zero ? "一時停止中" : $"待機中（{LengthLabel(CountdownSeconds)}）";
        }
        var phase = PhaseName(Phase);
        if (IsPomodoro)
        {
            if (Running)
                return $"{phase}中";
            if (_accumulated > TimeSpan.Zero)
                return $"{phase}（一時停止中）";
            return $"次は{phase}（{PhaseLength / 60}分）";
        }
        if (Running)
            return "計測中";
        return _accumulated > TimeSpan.Zero ? "一時停止中" : "待機中";
    }
}
