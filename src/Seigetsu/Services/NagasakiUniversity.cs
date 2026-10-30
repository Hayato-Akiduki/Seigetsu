using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>LACS の課題 1 件（締切は自分の PC の時刻にしたもの）。Submitted は提出済みかどうか。</summary>
public sealed record LacsAssignment(string Id, string Title, string Course, DateTime Due, bool Submitted);

/// <summary>
/// 長崎大学モード。LACS（Blackboard Learn）と NU-Web（CampusSquare）から情報を読み取る。
/// どちらもログインが要るので、アプリの中のブラウザ（WebView2）で本人にログインしてもらい、
/// そのページの中でスクリプトを動かして読み取る。パスワードはアプリでは扱わない。
/// </summary>
public static partial class NagasakiUniversity
{
    public const string LacsUrl = "https://lacs.nagasaki-u.ac.jp/ultra/institution-page";
    public const string NuWebUrl = "https://nuweb.nagasaki-u.ac.jp/campusweb/campusportal.do";
    /// <summary>NU-Web に長大ID（学内の一括ログイン）で入る入口。ログイン済みなら、そのまま NU-Web のホームに着く。</summary>
    public const string NuWebSignIn = "https://nuweb.nagasaki-u.ac.jp/campusweb/shiblogin.do";

    /// <summary>NU-Web の「履修登録・登録状況照会」（自分の時間割が表で出る）。</summary>
    public const string TimetableUrl =
        "https://nuweb.nagasaki-u.ac.jp/campusweb/campussquare.do?_flowId=RSW0001000-flow";

    /// <summary>LACS の最初のページ。ここにある「長大ID でログイン」のリンクをたどって入る。</summary>
    public const string LacsHome = "https://lacs.nagasaki-u.ac.jp/";

    /// <summary>
    /// LACS に長大ID で入る入口。ふだんは LacsHome のページからリンクを読むので、これはリンクが見つからないときの予備。
    /// </summary>
    public const string LacsSignIn =
        "https://lacs.nagasaki-u.ac.jp/auth-saml/saml/login?apId=_7_1&redirectUrl=https%3A%2F%2Flacs.nagasaki-u.ac.jp%2Fultra%2Finstitution-page";

    public const string LacsHost = "lacs.nagasaki-u.ac.jp";
    public const string NuWebHost = "nuweb.nagasaki-u.ac.jp";

    public static bool Enabled => App.Database.GetSetting(SettingKeys.NagasakiMode, "0") == "1";

    public static readonly string[] WeekdayNames = ["", "月", "火", "水", "木", "金", "土"];

    // ---- LACS の課題 ----------------------------------------------------
    // 読み方は LacsClient（霽月から LACS の公開 REST API を直接読む）。ここはタスクへの取り込みだけ

    /// <summary>タスクの題名。例:「レポート1（線形代数学・23:59 締切）」</summary>
    public static string TaskTitle(LacsAssignment a)
    {
        var course = a.Course == "" ? "" : a.Course + "・";
        return $"{a.Title}（{course}{a.Due:H:mm} 締切）";
    }

    /// <summary>課題をタスクに取り込む。提出済みのものは完了にする。(新しく足した数, 取り込み済みだった数)</summary>
    public static (int Added, int Updated) ImportAssignments(IEnumerable<LacsAssignment> assignments)
    {
        var db = App.Database;
        int added = 0, updated = 0;
        foreach (var a in assignments)
        {
            if (db.UpsertLacsTask(a.Id, TaskTitle(a), a.Due.ToString("yyyy-MM-dd"), a.Submitted))
                added++;
            else
                updated++;
        }
        return (added, updated);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- 時間割と LACS の授業を結びつける -------------------------------
    /// <summary>
    /// 時間割の授業名（例:「●A01線形代数学」）に合う LACS の授業を探す。
    /// 記号・空白・英数字の番号を除いた名前で、どちらかがもう一方を含んでいれば同じ授業とみなす。
    /// 1 コマに 2 つの授業（「 / 」区切り）があれば、前のものから順に探す。
    /// </summary>
    public static LacsCourse? FindCourse(IEnumerable<LacsCourse> courses, string title)
    {
        var list = courses.Select(c => (Course: c, Key: CourseKey(c.Name))).Where(c => c.Key != "").ToList();
        foreach (var part in title.Split(" / "))
        {
            var key = CourseKey(part);
            if (key == "")
                continue;
            var found = list.Where(c => c.Key.Contains(key) || key.Contains(c.Key))
                .OrderBy(c => Math.Abs(c.Key.Length - key.Length)).Select(c => c.Course).FirstOrDefault();
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string CourseKey(string name)
    {
        var text = name.Normalize(System.Text.NormalizationForm.FormKC);
        // 先頭の「●B11」「[2026前期]」のような印や番号と、かっこ書きを除く
        text = Regex.Replace(text, @"[\[(（【].*?[\])）】]", "");
        text = Regex.Replace(text, @"^[^\p{L}]*[A-Za-z]{0,3}\d+", "");
        return Regex.Replace(text, @"[\s\p{P}\p{S}]", "");
    }

    // ---- NU-Web の時間割と教室 -------------------------------------------
    // 読み方は NuWebClient（霽月から NU-Web のページを直接読む）。ここは教室を各コマに入れるのと、授業の時刻だけ

    /// <summary>時間割の各コマに教室を入れる（コードごとの教室を「、」でつなぐ）。</summary>
    public static IReadOnlyList<ClassSlot> WithRooms(IEnumerable<ClassSlot> slots, IReadOnlyDictionary<string, string> rooms) =>
        slots.Select(s => s with
        {
            Room = string.Join(" / ", s.Codes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(c => rooms.GetValueOrDefault(c, "")).Where(r => r != "").Distinct()),
        }).ToList();


    /// <summary>
    /// 授業の時刻。時間割の表に書いていなければこれを使う。
    /// LACS-MCP（長崎大学の学生向けの非公式アプリ）が使っている長崎大学の時刻と同じ。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> DefaultPeriodTimes = new Dictionary<int, string>
    {
        [1] = "8:50～10:20",
        [2] = "10:30～12:00",
        [3] = "12:50～14:20",
        [4] = "14:30～16:00",
        [5] = "16:10～17:40",
        [6] = "17:50～19:20",
    };

    public static string TimeOf(ClassSlot slot) =>
        slot.TimeText != "" ? slot.TimeText : DefaultPeriodTimes.GetValueOrDefault(slot.Period, "");
}
