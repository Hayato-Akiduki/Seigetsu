using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>LACS の課題 1 件（締切は自分の PC の時刻にしたもの）。</summary>
public sealed record LacsAssignment(string Id, string Title, string Course, DateTime Due);

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
    public const string TimetableUrl = "https://nuweb.nagasaki-u.ac.jp/campusweb/campussquare.do?_flowId=RSW0001000-flow";

    /// <summary>LACS に長大ID で入る入口（ログイン画面の「長大ID でログイン」と同じ）。ログイン済みなら LACS に戻ってくる。</summary>
    public const string LacsSignIn =
        "https://lacs.nagasaki-u.ac.jp/auth-saml/saml/login?apId=_7_1&redirectUrl=https%3A%2F%2Flacs.nagasaki-u.ac.jp%2Fultra%2Finstitution-page";

    public const string LacsHost = "lacs.nagasaki-u.ac.jp";
    public const string NuWebHost = "nuweb.nagasaki-u.ac.jp";

    public static bool Enabled => App.Database.GetSetting(SettingKeys.NagasakiMode, "0") == "1";

    public static readonly string[] WeekdayNames = ["", "月", "火", "水", "木", "金", "土"];

    // ---- LACS の課題 ----------------------------------------------------
    /// <summary>
    /// LACS のページの中で動かすスクリプト。ログイン中のセッションのまま Blackboard の REST API で
    /// カレンダーの項目（課題の締切）を読み、結果を chrome.webview.postMessage で返す。
    /// 一度に読める期間は 16 週までなので、1 週前から 14 週先までにする。
    /// </summary>
    public const string LacsScript = """
        (async () => {
          const post = (o) => window.chrome.webview.postMessage(JSON.stringify(Object.assign({ kind: 'lacs' }, o)));
          try {
            const me = await fetch('/learn/api/public/v1/users/me', { credentials: 'include' });
            if (!me.ok) { post({ error: 'login', status: me.status }); return; }
            const day = 24 * 60 * 60 * 1000;
            const since = new Date(Date.now() - 7 * day).toISOString();
            const until = new Date(Date.now() + 98 * day).toISOString();
            let url = '/learn/api/public/v1/calendars/items?since=' + encodeURIComponent(since)
                    + '&until=' + encodeURIComponent(until) + '&limit=200';
            const items = [];
            for (let page = 0; url && page < 20; page++) {
              const r = await fetch(url, { credentials: 'include' });
              if (!r.ok) { post({ error: 'api', status: r.status }); return; }
              const j = await r.json();
              items.push(...(j.results || []));
              url = j.paging && j.paging.nextPage;
            }
            post({ items });
          } catch (e) {
            post({ error: 'script', message: String(e) });
          }
        })();
        """;

    /// <summary>LACS から返ってきた項目のうち、課題（成績表の列＝締切のあるもの）だけを取り出す。</summary>
    public static IReadOnlyList<LacsAssignment> ParseLacsItems(JsonElement items)
    {
        var list = new List<LacsAssignment>();
        foreach (var item in items.EnumerateArray())
        {
            if (Str(item, "type") != "GradebookColumn")
                continue;
            var id = Str(item, "id");
            var title = Str(item, "title").Trim();
            // 締切は end（なければ start）。UTC で来るので PC の時刻に直す
            var when = Str(item, "end");
            if (when == "")
                when = Str(item, "start");
            if (id == "" || title == "" ||
                !DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                continue;
            list.Add(new LacsAssignment(id, title, Str(item, "calendarName").Trim(), due.ToLocalTime().DateTime));
        }
        return list;
    }

    /// <summary>タスクの題名。例:「レポート1（線形代数学・23:59 締切）」</summary>
    public static string TaskTitle(LacsAssignment a)
    {
        var course = a.Course == "" ? "" : a.Course + "・";
        return $"{a.Title}（{course}{a.Due:H:mm} 締切）";
    }

    /// <summary>課題をタスクに取り込む。(新しく足した数, 取り込み済みだった数)</summary>
    public static (int Added, int Updated) ImportAssignments(IEnumerable<LacsAssignment> assignments)
    {
        var db = App.Database;
        int added = 0, updated = 0;
        foreach (var a in assignments)
        {
            if (db.UpsertLacsTask(a.Id, TaskTitle(a), a.Due.ToString("yyyy-MM-dd")))
                added++;
            else
                updated++;
        }
        return (added, updated);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // ---- NU-Web の時間割 ------------------------------------------------
    /// <summary>
    /// NU-Web のページの中で動かすスクリプト。表示中のページ（同じサイトの枠の中も含む）にある表を、
    /// 結合されたマス（rowspan / colspan）を広げた「文字の 2 次元の表」にして返す。
    /// どの表が時間割かは C# の側（ParseTimetable）で決める。
    /// </summary>
    public const string TimetableScript = """
        (() => {
          const tables = [];
          const visit = (doc, depth) => {
            if (!doc || depth > 4) return;
            for (const t of doc.querySelectorAll('table')) {
              if (t.rows.length < 3 || tables.length >= 200) continue;
              const grid = [];
              Array.from(t.rows).forEach((tr, ri) => {
                grid[ri] = grid[ri] || [];
                let c = 0;
                for (const td of tr.cells) {
                  while (grid[ri][c] !== undefined) c++;
                  const text = (td.innerText || '').replace(/ /g, ' ').trim();
                  const rs = Math.min(td.rowSpan || 1, 20), cs = Math.min(td.colSpan || 1, 20);
                  for (let dr = 0; dr < rs; dr++) {
                    grid[ri + dr] = grid[ri + dr] || [];
                    for (let dc = 0; dc < cs; dc++) grid[ri + dr][c + dc] = text;
                  }
                  c += cs;
                }
              });
              tables.push(grid.map(row => Array.from(row, x => x === undefined ? '' : x)));
            }
            for (const f of doc.querySelectorAll('iframe, frame')) {
              try { visit(f.contentDocument, depth + 1); } catch (e) { }
            }
          };
          visit(document, 0);
          window.chrome.webview.postMessage(JSON.stringify({ kind: 'timetable', tables }));
        })();
        """;

    /// <summary>
    /// 表の中から時間割を探す。曜日（月〜金）が並んだ行を見出しとし、その下の行の左端から「何限か」を読む。
    /// 曜日が縦に並んだ表にも対応する。いちばん多くのコマが読めた表を使う。
    /// </summary>
    public static IReadOnlyList<ClassSlot> ParseTimetable(JsonElement tables)
    {
        var best = new List<ClassSlot>();
        foreach (var table in tables.EnumerateArray())
        {
            var grid = table.EnumerateArray()
                .Select(row => row.EnumerateArray().Select(c => (c.GetString() ?? "").Replace("\r", "")).ToArray())
                .ToArray();
            foreach (var candidate in new[] { grid, Transpose(grid) })
            {
                var slots = ParseGrid(candidate);
                if (slots.Count > best.Count)
                    best = slots;
            }
        }
        return best;
    }

    private static List<ClassSlot> ParseGrid(string[][] grid)
    {
        var slots = new Dictionary<(int, int), ClassSlot>();
        for (var r = 0; r < grid.Length; r++)
        {
            // 見出しの行: 曜日の列を探す（5 つ以上の曜日が並んでいる行）
            var dayColumns = new Dictionary<int, int>();
            for (var c = 0; c < grid[r].Length; c++)
            {
                if (WeekdayOf(Normalize(grid[r][c])) is { } day && !dayColumns.ContainsValue(day))
                    dayColumns[c] = day;
            }
            if (dayColumns.Count < 5)
                continue;

            var firstDayColumn = dayColumns.Keys.Min();
            for (var rr = r + 1; rr < grid.Length; rr++)
            {
                var row = grid[rr];
                if (firstDayColumn == 0 || row.Length <= firstDayColumn)
                    continue;
                // 左端の列（曜日より左）から「1限」「1」「第1時限 8:50～10:20」などを読む
                var head = Normalize(string.Join(" ", row.Take(firstDayColumn)));
                if (PeriodOf(head) is not { } period)
                    continue;
                var time = TimeRegex().Match(head) is { Success: true } m
                    ? $"{m.Groups[1].Value}～{m.Groups[2].Value}" : "";
                foreach (var (column, day) in dayColumns)
                {
                    if (column >= row.Length)
                        continue;
                    if (ReadCell(row[column]) is { } cell)
                        slots[(day, period)] = new ClassSlot(day, period, cell.Title, cell.Detail, time);
                }
            }
            break;
        }
        return slots.Values.OrderBy(s => s.Weekday).ThenBy(s => s.Period).ToList();
    }

    /// <summary>
    /// 1 コマの中身を (授業名, 先生など) にする。空や「未登録」なら null。
    /// NU-Web の「履修登録・登録状況照会」では、授業ごとに「時間割コード（14 桁）・授業名・(単位数)・先生」の順に並ぶ。
    /// 1 コマに 2 つ以上の授業があれば（前半と後半のクォーターなど）、「 / 」でつなぐ。
    /// </summary>
    private static (string Title, string Detail)? ReadCell(string text)
    {
        var lines = text.Split('\n', '\t').Select(l => l.Trim())
            .Where(l => l != "" && !IsBlankMark(l) && l != "未登録").ToList();
        if (lines.Count == 0)
            return null;

        if (!lines.Any(l => CodeRegex().IsMatch(l)))
            return (lines[0], string.Join(" / ", lines.Skip(1)));

        var names = new List<string>();
        var details = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!CodeRegex().IsMatch(lines[i]) || i + 1 >= lines.Count)
                continue;
            var rest = lines.Skip(i + 1).TakeWhile(l => !CodeRegex().IsMatch(l)).ToList();
            names.Add(rest[0]);
            // 単位数「(2.0)」は出さず、先生の名前だけ残す
            var teacher = rest.Skip(1).FirstOrDefault(l => !CreditRegex().IsMatch(l));
            if (teacher is not null)
                details.Add(Regex.Replace(teacher, @"\s+", " "));
        }
        return names.Count == 0 ? null : (string.Join(" / ", names), string.Join(" / ", details));
    }

    [GeneratedRegex(@"^\d{14}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^\(\s*[\d.]+\s*\)$")]
    private static partial Regex CreditRegex();

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

    private static string[][] Transpose(string[][] grid)
    {
        var width = grid.Length == 0 ? 0 : grid.Max(r => r.Length);
        return Enumerable.Range(0, width)
            .Select(c => grid.Select(r => c < r.Length ? r[c] : "").ToArray())
            .ToArray();
    }

    // 曜日や「何限か」を読むときだけ、全角の数字やコロンを半角にする（授業名は元の文字のまま残す）
    private static string Normalize(string text) => text.Normalize(NormalizationForm.FormKC);

    /// <summary>「月」「月曜」「月曜日」「Mon」などを 1〜6 にする。</summary>
    private static int? WeekdayOf(string cell)
    {
        var text = cell.Trim();
        if (text.Length == 0 || text.Length > 4)
            return null;
        var index = Array.IndexOf(WeekdayNames, text[..1]);
        if (index > 0 && (text.Length == 1 || text[1..] is "曜" or "曜日"))
            return index;
        return text.ToLowerInvariant() switch
        {
            "mon" => 1, "tue" => 2, "wed" => 3, "thu" => 4, "fri" => 5, "sat" => 6,
            _ => null,
        };
    }

    private static int? PeriodOf(string head)
    {
        var m = PeriodRegex().Match(head);
        return m.Success && int.TryParse(m.Groups[1].Value, out var p) && p is >= 1 and <= 10 ? p : null;
    }

    private static bool IsBlankMark(string line) => line is "-" or "－" or "ー" or "―" or "*" or "・";

    // 「1限」「第1時限」「1講時」「1校時」、または数字だけ（時刻の数字は拾わない）
    [GeneratedRegex(@"^\D{0,2}?(\d{1,2})(?:\s*(?:限|時限|講時|校時|コマ)|\s*$|\s+\d{1,2}:)")]
    private static partial Regex PeriodRegex();

    [GeneratedRegex(@"(\d{1,2}:\d{2})\s*[~～\-ー〜]\s*(\d{1,2}:\d{2})")]
    private static partial Regex TimeRegex();
}
