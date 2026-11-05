namespace Seigetsu.Services;

/// <summary>
/// 通知の本文に添えることば。同じ通知でも毎回少し違う言い方になるよう、種類ごとに何通りか用意して、
/// 前回と同じものが続かないように選ぶ。時間帯（朝・夜・深夜）や今日の集中時間に合わせたことばもまぜる。
/// </summary>
public static class NotifyPhrases
{
    private static readonly Random Random = new();
    private static readonly Dictionary<string[], int> Last = [];

    // ---- 集中タイマー：作業が終わった（{0} は休憩の長さ） -------------------
    private static readonly string[] WorkDone =
    [
        "{0}ほど休憩しましょう。",
        "おつかれさまでした。{0}ひと息つきましょう。",
        "よく集中できました。{0}、目と肩を休めましょう。",
        "ここで{0}の休憩です。水分もとっておきましょう。",
        "いったん手を止めて、{0}のんびりしましょう。",
        "立ち上がって伸びをするのにちょうどいい時間です。休憩は{0}。",
        "がんばりました。{0}だけ、画面から目を離しましょう。",
        "深呼吸をひとつ。{0}の休憩に入りましょう。",
    ];

    // ---- 集中タイマー：休憩が終わった（{0} は作業の長さ） -------------------
    private static readonly string[] BreakDone =
    [
        "次の作業（{0}）を始めましょう。",
        "リフレッシュできましたか？ 次の{0}もいきましょう。",
        "休憩はおしまいです。もうひと踏ん張り、{0}集中しましょう。",
        "さあ、続きを始めましょう。次は{0}です。",
        "気持ちを切り替えて、{0}の作業に戻りましょう。",
        "準備ができたら、次の{0}を始めましょう。",
    ];

    // ---- 普通のタイマー -------------------------------------------------
    private static readonly string[] CountdownDone =
    [
        "時間になりました。",
        "設定した時間が来ました。",
        "ピピッ、時間です。",
        "お待たせしました。時間になりました。",
        "タイマーが鳴りました。次の予定はだいじょうぶですか？",
    ];

    // ---- 締切 ----------------------------------------------------------
    private static readonly string[] DueToday =
    [
        "忘れないうちに片づけましょう。",
        "今日のうちに終わらせてしまいましょう。",
        "まずは 1 つ、手をつけてみませんか？",
        "早めに済ませると、あとが楽になります。",
        "今日がんばれば、明日はすっきりです。",
    ];

    private static readonly string[] DueOverdue =
    [
        "まだ間に合うものから、1 つずつ片づけましょう。",
        "期限が過ぎています。できるところから進めましょう。",
        "見直して、終わったものは完了にしておきましょう。",
        "あせらず、いちばん大事なものから始めましょう。",
    ];

    // ---- 授業の変更 -----------------------------------------------------
    private static readonly string[] Classes =
    [
        "休講・補講・教室変更をお知らせします。",
        "時間割に変更があります。確かめておきましょう。",
        "授業の予定が変わりました。教室や時間を見ておきましょう。",
        "大学からのお知らせです。出かける前に確かめましょう。",
    ];

    // ---- 10・14・18 時の通知：課題があるとき ------------------------------
    private static readonly string[] TasksMorning =
    [
        "午前のうちに 1 つ進めておくと、一日が楽になります。",
        "朝の頭が冴えているうちに、手をつけてみませんか？",
        "今日の予定に、課題の時間を入れておきましょう。",
    ];

    private static readonly string[] TasksAfternoon =
    [
        "午後のすきま時間に、少しだけ進めてみましょう。",
        "15 分だけでも手をつけると、ぐっと進みます。",
        "締切が近いものから片づけていきましょう。",
    ];

    private static readonly string[] TasksEvening =
    [
        "夜になる前に、今日の分を進めておきましょう。",
        "あと少しで今日も終わり。1 つ片づけてすっきりしませんか？",
        "明日の自分のために、今のうちに進めておきましょう。",
    ];

    // ---- 10・14・18 時の通知：課題がないとき（勉強をうながす） ---------------
    private static readonly string[] StudyTitleMorning = ["朝の勉強を始めませんか？", "午前の勉強の時間です", "今日の勉強、何から始めますか？"];
    private static readonly string[] StudyTitleAfternoon = ["午後の勉強の時間です", "ひと勉強いかがですか？", "午後も少し進めましょう"];
    private static readonly string[] StudyTitleEvening = ["夕方のひと勉強の時間です", "今日の仕上げに勉強しませんか？", "夜の勉強を始めましょう"];

    private static readonly string[] StudyMorning =
    [
        "急ぎの課題はありません。予習や苦手なところの見直しにぴったりです。",
        "締切に追われていない今こそ、少し先の勉強を進めるチャンスです。",
        "まずは 25 分、集中タイマーで始めてみましょう。",
        "朝の 1 時間は、夜の 2 時間ぶんの価値があるとも言われます。",
    ];

    private static readonly string[] StudyAfternoon =
    [
        "近い締切の課題はありません。今日の授業の復習をしてみませんか？",
        "眠くなりやすい時間です。短い集中を 1 回だけやってみましょう。",
        "ノートを見返すだけでも、記憶はぐっと残りやすくなります。",
        "読みかけの資料や本を、少し進めてみませんか？",
    ];

    private static readonly string[] StudyEvening =
    [
        "急ぎの課題はありません。今日習ったことを 10 分だけ振り返りましょう。",
        "寝る前に覚えたことは、記憶に残りやすいそうです。",
        "明日の授業の準備を、少しだけしておきませんか？",
        "今日のうちに 1 回、集中タイマーを回してみましょう。",
    ];

    /// <summary>締切の近い課題があるときの本文（hour は 10・14・18 のどれか）。</summary>
    public static string TasksBody(int hour) => Pick(hour < 12 ? TasksMorning : hour < 17 ? TasksAfternoon : TasksEvening);

    /// <summary>課題がないときの見出しと本文。</summary>
    public static (string Title, string Body) Study(int hour) => hour < 12
        ? (Pick(StudyTitleMorning), Pick(StudyMorning))
        : hour < 17
            ? (Pick(StudyTitleAfternoon), Pick(StudyAfternoon))
            : (Pick(StudyTitleEvening), Pick(StudyEvening));

    /// <summary>勉強の通知の補足（今日ここまでの集中時間）。</summary>
    public static string StudyNote(int secondsToday) => secondsToday < 60
        ? "今日はまだ集中の記録がありません"
        : secondsToday >= 3600
            ? $"今日はもう {Format.Duration(secondsToday)} 集中しています。この調子で！"
            : $"今日の集中 {Format.Duration(secondsToday)}";

    // ---- 時間帯に合わせて添えることば ------------------------------------
    private static readonly string[] Morning = ["朝から集中できていますね。", "いい一日のスタートです。"];
    private static readonly string[] Night = ["夜までおつかれさまです。", "夜は無理をしすぎないようにしましょう。"];
    private static readonly string[] LateNight = ["もう遅い時間です。今日はそろそろ休みませんか？", "夜ふかしはほどほどに。睡眠も大事です。"];

    public static string WorkFinished(string breakLength) => WithTime(string.Format(Pick(WorkDone), breakLength));
    public static string BreakFinished(string workLength) => string.Format(Pick(BreakDone), workLength);
    public static string CountdownFinished() => Pick(CountdownDone);
    public static string DueBody(bool overdueOnly) => Pick(overdueOnly ? DueOverdue : DueToday);
    public static string ClassesBody() => Pick(Classes);

    /// <summary>今日の集中時間の補足。たくさん集中した日は、ひとことほめる。</summary>
    public static string FocusNote(int secondsToday)
    {
        var total = $"今日の集中 {Format.Duration(secondsToday)}";
        var hours = secondsToday / 3600;
        return hours switch
        {
            >= 6 => $"{total} ・ すばらしい一日です！",
            >= 4 => $"{total} ・ たくさんがんばりました",
            >= 2 => $"{total} ・ いい調子です",
            _ => total,
        };
    }

    // 作業の終わりには、朝・夜・深夜だけ、時間帯のことばを添える
    private static string WithTime(string text)
    {
        var hour = DateTime.Now.Hour;
        string[]? extra = hour switch
        {
            >= 5 and < 9 => Morning,
            >= 21 => Night,
            < 5 => LateNight,
            _ => null,
        };
        // 毎回だと くどいので、時間帯のことばは 2 回に 1 回くらい
        return extra is null || Random.Next(2) == 0 ? text : $"{text}{Pick(extra)}";
    }

    // 前回と同じものは続けて選ばない
    private static string Pick(string[] pool)
    {
        if (pool.Length == 1)
            return pool[0];
        var last = Last.GetValueOrDefault(pool, -1);
        int index;
        do
            index = Random.Next(pool.Length);
        while (index == last);
        Last[pool] = index;
        return pool[index];
    }
}
