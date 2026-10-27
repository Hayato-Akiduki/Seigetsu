using System.Diagnostics;
using Seigetsu.Data;

namespace Seigetsu.Services;

public enum TimerMode { Stopwatch, Pomodoro }

public enum TimerPhase { Work, Break }

/// <summary>
/// 集中タイマー（ストップウォッチ / ポモドーロ）。v6 の FocusTimer と同じ動きで、
/// 計った時間は v6 と同じ mode 名で work_sessions に保存する（分析画面で v6 の記録と一緒に数えられる）。
/// </summary>
public sealed class FocusTimer(SeigetsuDatabase db)
{
    // work_sessions.mode に入れる名前（v6 と同じ）
    public const string StopwatchModeName = "通常";
    public const string PomodoroWorkModeName = "ポモドーロ-作業";

    private long _startedAt;     // 動き始めた時刻（Stopwatch のタイムスタンプ）
    private double _accumulated; // 一時停止までにたまった秒数

    public TimerMode Mode { get; private set; } = TimerMode.Stopwatch;

    public TimerPhase Phase { get; private set; } = TimerPhase.Work;

    public bool Running { get; private set; }

    public bool IsPomodoro => Mode == TimerMode.Pomodoro;

    public bool IsIdle => !Running && _accumulated == 0;

    // ---- 設定値（設定画面で変えられる。v6 と同じキー） ------------------
    public int WorkSeconds => Math.Max(1, db.GetIntSetting(SettingKeys.WorkMinutes, 25)) * 60;

    public int BreakSeconds => Math.Max(1, db.GetIntSetting(SettingKeys.BreakMinutes, 5)) * 60;

    public int PhaseLength => Phase == TimerPhase.Work ? WorkSeconds : BreakSeconds;

    // ---- 状態 ---------------------------------------------------------
    public double Elapsed => Running
        ? _accumulated + Stopwatch.GetElapsedTime(_startedAt).TotalSeconds
        : _accumulated;

    public double Remaining => Math.Max(0, PhaseLength - Elapsed);

    /// <summary>0〜1。ポモドーロは今のフェーズの進み具合、ストップウォッチは 1 時間で一周。</summary>
    public double Progress => IsPomodoro
        ? Math.Min(1, Elapsed / PhaseLength)
        : Elapsed % 3600 / 3600;

    // ---- 操作 ---------------------------------------------------------
    public void Start()
    {
        if (Running)
            return;
        Running = true;
        _startedAt = Stopwatch.GetTimestamp();
    }

    public void Pause()
    {
        if (!Running)
            return;
        _accumulated = Elapsed;
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
        _accumulated = 0;
        Phase = TimerPhase.Work;
    }

    public void SetMode(TimerMode mode)
    {
        if (mode == Mode)
            return;
        Reset();
        Mode = mode;
    }

    /// <summary>ポモドーロの今のフェーズを終えて、次へ進む。</summary>
    public void SkipPhase()
    {
        if (!IsPomodoro)
            return;
        RecordPartial();
        Running = false;
        _accumulated = 0;
        Phase = Next(Phase);
    }

    /// <summary>定期的に呼ぶ。ポモドーロのフェーズが終わったら、終わったフェーズを返す。</summary>
    public TimerPhase? Tick()
    {
        if (!IsPomodoro || !Running || Remaining > 0)
            return null;
        var finished = Phase;
        if (finished == TimerPhase.Work)
            db.AddSession(PhaseLength, PomodoroWorkModeName);
        Running = false;
        _accumulated = 0;
        Phase = Next(finished);
        return finished;
    }

    private static TimerPhase Next(TimerPhase phase) =>
        phase == TimerPhase.Work ? TimerPhase.Break : TimerPhase.Work;

    // 休憩時間は記録しない（v6 と同じ）
    private void RecordPartial()
    {
        var seconds = (int)Elapsed;
        if (Mode == TimerMode.Stopwatch)
            db.AddSession(seconds, StopwatchModeName);
        else if (Phase == TimerPhase.Work)
            db.AddSession(seconds, PomodoroWorkModeName);
        _accumulated = 0;
        if (Running)
            _startedAt = Stopwatch.GetTimestamp();
    }

    // ---- 表示用 -------------------------------------------------------
    public static string PhaseName(TimerPhase phase) => phase == TimerPhase.Work ? "作業" : "休憩";

    public string Display()
    {
        if (IsPomodoro)
        {
            var remaining = (int)Math.Round(Remaining);
            return $"{remaining / 60:00}:{remaining % 60:00}";
        }
        var elapsed = (int)Elapsed;
        return $"{elapsed / 3600:00}:{elapsed % 3600 / 60:00}:{elapsed % 60:00}";
    }

    public string Status()
    {
        var phase = PhaseName(Phase);
        if (IsPomodoro)
        {
            if (Running)
                return $"{phase}中";
            if (_accumulated > 0)
                return $"{phase}（一時停止中）";
            return $"次は{phase}（{PhaseLength / 60}分）";
        }
        if (Running)
            return "計測中";
        return _accumulated > 0 ? "一時停止中" : "待機中";
    }
}
