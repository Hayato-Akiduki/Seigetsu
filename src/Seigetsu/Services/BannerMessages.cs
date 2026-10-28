using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>ホームの上帯に出すことば 1 つ。Source は出典や書いた人（無ければ null）。</summary>
public sealed record BannerMessage(string Kind, string Text, string? Source = null);

/// <summary>
/// ホームの上帯に、時間がたつと切り替えて出すことば。ホームを開いたときだけあいさつを出し、そのあとは
/// 名言とことわざ・季節のことば・タスクのお知らせ・自分で書いたことばの中から、設定で選んだ種類を順番に出す。
/// </summary>
public static class BannerMessages
{
    /// <summary>あいさつのあとに切り替えて出す種類（設定に保存する名前, 画面に出す名前）。並びは出す順番。</summary>
    public static readonly (string Key, string Label)[] Kinds =
    [
        ("quote", "名言・ことわざ"),
        ("season", "季節や曜日のことば"),
        ("tasks", "タスクのお知らせ"),
        ("custom", "自分で書いたことば"),
    ];

    /// <summary>選べる切り替えの間隔（秒）。</summary>
    public static readonly int[] Intervals = [30, 60, 300, 600];

    private const string DefaultKinds = "quote,season,tasks";

    private static readonly Random Random = new();

    public static IReadOnlyList<string> EnabledKinds
    {
        get
        {
            var saved = App.Database.GetSetting(SettingKeys.BannerKinds, DefaultKinds);
            var keys = saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Kinds.Select(k => k.Key).Where(keys.Contains).ToList();
        }
    }

    public static void SetEnabled(string kind, bool enabled)
    {
        var kinds = EnabledKinds.ToHashSet();
        if (enabled)
            kinds.Add(kind);
        else
            kinds.Remove(kind);
        App.Database.SetSetting(SettingKeys.BannerKinds,
            string.Join(",", Kinds.Select(k => k.Key).Where(kinds.Contains)));
    }

    public static int IntervalSeconds => App.Database.GetIntSetting(SettingKeys.BannerInterval, 60);

    /// <summary>自分で書いたことば（空の行は飛ばす）。</summary>
    public static IReadOnlyList<string> CustomLines =>
        App.Database.GetSetting(SettingKeys.BannerCustom, "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// after の次の種類のことばを作る。出すものが無い種類（自分で書いたことばが空、など）は飛ばす。
    /// どの種類も出せないとき（どれも選んでいない、など）は、あいさつのままにする。
    /// </summary>
    public static BannerMessage Next(string? after, DateTime now)
    {
        var kinds = EnabledKinds;
        var start = after is null ? 0 : IndexOf(kinds, after) + 1;
        for (var i = 0; i < kinds.Count; i++)
        {
            var kind = kinds[(start + i) % kinds.Count];
            if (Make(kind, now) is { } message)
                return message;
        }
        return Greeting(now);
    }

    private static int IndexOf(IReadOnlyList<string> kinds, string kind)
    {
        for (var i = 0; i < kinds.Count; i++)
        {
            if (kinds[i] == kind)
                return i;
        }
        return -1;
    }

    private static BannerMessage? Make(string kind, DateTime now) => kind switch
    {
        "quote" => Pick(Quotes) is var (text, source) ? new BannerMessage(kind, text, source) : null,
        "season" => Season(now),
        "tasks" => Tasks(),
        "custom" => CustomLines is { Count: > 0 } lines ? new BannerMessage(kind, Pick(lines)) : null,
        _ => null,
    };

    public static BannerMessage Greeting(DateTime now) => new("greeting", GreetingText(now));

    public static string GreetingText(DateTime now) => now.Hour switch
    {
        >= 5 and < 11 => "おはようございます",
        >= 11 and < 18 => "こんにちは",
        _ => "こんばんは",
    };

    private static BannerMessage Season(DateTime now)
    {
        // 月のことばと曜日のことばを、半分ずつくらいの割合で出す
        if (Random.Next(2) == 0)
        {
            var (name, line) = Months[now.Month - 1];
            return new BannerMessage("season", line, name);
        }
        return new BannerMessage("season", Weekdays[(int)now.DayOfWeek]);
    }

    private static BannerMessage Tasks()
    {
        var db = App.Database;
        var (overdue, today) = db.DueCounts();
        if (overdue > 0)
            return new BannerMessage("tasks", $"期限を過ぎたタスクが {overdue} 件あります", "タスクのお知らせ");
        if (today > 0)
            return new BannerMessage("tasks", $"今日が締切のタスクが {today} 件あります", "タスクのお知らせ");
        var open = db.TaskCounts().Open;
        return open > 0
            ? new BannerMessage("tasks", $"まだ終わっていないタスクは {open} 件です", "タスクのお知らせ")
            : new BannerMessage("tasks", "終わっていないタスクはありません。おつかれさまです", "タスクのお知らせ");
    }

    private static T Pick<T>(IReadOnlyList<T> items) => items[Random.Next(items.Count)];

    // 名言・ことわざ（昔から伝わることばや、古典のことば）
    private static readonly (string Text, string Source)[] Quotes =
    [
        ("継続は力なり", "ことわざ"),
        ("千里の道も一歩から", "老子"),
        ("急がば回れ", "ことわざ"),
        ("塵も積もれば山となる", "ことわざ"),
        ("七転び八起き", "ことわざ"),
        ("石の上にも三年", "ことわざ"),
        ("好きこそものの上手なれ", "ことわざ"),
        ("案ずるより産むが易し", "ことわざ"),
        ("雨垂れ石を穿つ", "ことわざ"),
        ("早起きは三文の徳", "ことわざ"),
        ("笑う門には福来たる", "ことわざ"),
        ("備えあれば憂いなし", "ことわざ"),
        ("明日は明日の風が吹く", "ことわざ"),
        ("光陰矢の如し", "ことわざ"),
        ("人間万事塞翁が馬", "淮南子"),
        ("為せば成る　為さねば成らぬ何事も", "上杉鷹山"),
        ("初心忘るべからず", "世阿弥"),
        ("一期一会", "茶道のことば"),
        ("日々是好日", "禅のことば"),
        ("温故知新", "論語"),
        ("過ぎたるは猶及ばざるが如し", "論語"),
        ("学びて時に之を習う、亦説ばしからずや", "論語"),
        ("知らざるを知らずと為す、是れ知るなり", "論語"),
        ("ゆく河の流れは絶えずして、しかももとの水にあらず", "方丈記"),
        ("少年老い易く学成り難し", "ことわざ"),
        ("一寸の光陰軽んずべからず", "ことわざ"),
        ("天は自ら助くる者を助く", "ことわざ"),
        ("雲外蒼天", "四字熟語"),
        ("百聞は一見に如かず", "漢書"),
        ("三人寄れば文殊の知恵", "ことわざ"),
    ];

    // 月のことば（和風の月の名前と、ひとこと）
    private static readonly (string Name, string Line)[] Months =
    [
        ("睦月（むつき）", "新しい一年のはじまり。今年の目標はなんですか"),
        ("如月（きさらぎ）", "寒さもあと少し。あたたかくしてすごしましょう"),
        ("弥生（やよい）", "春のはじまり。新しいことを始めるのにいい季節です"),
        ("卯月（うづき）", "桜の季節。新しい毎日がはじまります"),
        ("皐月（さつき）", "若葉がまぶしい季節。少し休むのも大切です"),
        ("水無月（みなづき）", "雨の多い季節。家でじっくり取り組むのにいい時期です"),
        ("文月（ふみづき）", "夏のはじまり。水分をこまめにとりましょう"),
        ("葉月（はづき）", "暑さのいちばん厳しいころ。むりをしないように"),
        ("長月（ながつき）", "夜が長くなってきました。読書の季節です"),
        ("神無月（かんなづき）", "秋が深まる季節。実りの秋をたのしみましょう"),
        ("霜月（しもつき）", "朝晩が冷えてきました。かぜに気をつけて"),
        ("師走（しわす）", "一年のしめくくり。今年もよくがんばりました"),
    ];

    // 曜日のことば（日曜日から）
    private static readonly string[] Weekdays =
    [
        "日曜日。ゆっくり休んで、また明日から",
        "月曜日。今週もいいスタートを",
        "火曜日。調子が出てくるころです",
        "水曜日。週の折り返しです",
        "木曜日。あと少しで週末です",
        "金曜日。今週もおつかれさまでした",
        "土曜日。好きなことに時間を使いましょう",
    ];
}
