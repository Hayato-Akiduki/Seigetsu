using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// NU-Web（CampusSquare）を読む。ログインの Cookie を付けて、霽月から直接ページを読む（読むだけで、GET しか使わない）。
/// 読み方は LACS-MCP（作った人の許可を得て使っている）の nuweb.py と同じ。
/// - ログインできているか：ホーム（campusportal.do?page=main）に id="logout" があるか
/// - 時間割：「履修登録・登録状況照会」（RSW0001000-flow）の表。見出しの「N校時」「月曜日」と、
///   各コマの「時間割コード（14 桁）・授業名・(単位)・先生」
/// - 教室：「シラバス参照」（SYW0001000-flow）で時間割コードを検索（searchCode）し、見つかったものを開く（input）。
///   その「教室」の欄
/// </summary>
public sealed partial class NuWebClient : IDisposable
{
    private const string Flows = "/campusweb/campussquare.do";
    private const string Portal = "/campusweb/campusportal.do?page=main";

    private readonly HttpClient _http;

    public NuWebClient(CookieContainer jar, string userAgent, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? NagasakiSession.CreateHandler(jar, allowRedirect: true))
        {
            BaseAddress = new Uri($"https://{NagasakiUniversity.NuWebHost}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (userAgent != "")
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>ログインできているか（ホームに「ログアウト」がある）。ついでに NU-Web のログインの残り時間も延びる。</summary>
    public async Task<bool> IsSignedInAsync()
    {
        try
        {
            var (page, at) = await GetAsync(Portal);
            var ok = at.Host == NagasakiUniversity.NuWebHost && page.Contains("id=\"logout\"", StringComparison.Ordinal);
            NagasakiLog.Write($"NU-Web: ホーム → {(ok ? "ログイン済み" : "ログインが必要")}（{NagasakiLog.Where(at)}）");
            return ok;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            NagasakiLog.Write($"NU-Web: ホームを読めませんでした（{error.Message}）");
            return false;
        }
    }

    /// <summary>自分の時間割。表が見つからなければ空（ログインが切れていたら NuWebLoginException）。</summary>
    public async Task<IReadOnlyList<ClassSlot>> ReadTimetableAsync()
    {
        var (page, at) = await GetAsync($"{Flows}?_flowId=RSW0001000-flow");
        if (!page.Contains("rishu-koma", StringComparison.Ordinal))
        {
            NagasakiLog.Write($"NU-Web: 履修登録の画面に時間割の表がありません（{NagasakiLog.Where(at)}）");
            if (at.Host != NagasakiUniversity.NuWebHost || page.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase))
                throw new NuWebLoginException();
            return [];
        }
        var slots = ParseRegistration(page);
        NagasakiLog.Write($"NU-Web: 時間割 {slots.Count} コマ");
        return slots;
    }

    /// <summary>時間割コードごとの教室。読めなかったコードは入れない（次の更新でもう一度読む）。</summary>
    public async Task<Dictionary<string, string>> ReadRoomsAsync(IEnumerable<string> codes)
    {
        var rooms = new Dictionary<string, string>();
        int failed = 0;
        foreach (var code in codes)
        {
            try
            {
                var (start, _) = await GetAsync($"{Flows}?_flowId=SYW0001000-flow");
                var (found, _) = await GetAsync(
                    $"{Flows}?_flowExecutionKey={FlowKey(start)}&_eventId=searchCode&jikanwaricd={code}");
                var refer = ReferRegex().Matches(found).FirstOrDefault(m => m.Groups[3].Value == code);
                if (refer is null)
                {
                    rooms[code] = ""; // シラバスが無い授業
                    continue;
                }
                var (syllabus, _) = await GetAsync(
                    $"{Flows}?_flowExecutionKey={FlowKey(found)}&_eventId=input&nendo={refer.Groups[1].Value}"
                    + $"&jikanwariShozokuCode={refer.Groups[2].Value}&jikanwaricd={code}&locale=ja_JP");
                rooms[code] = ParseRoom(syllabus);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or NuWebException)
            {
                failed++;
            }
        }
        NagasakiLog.Write($"NU-Web: 教室 {rooms.Count} 件読み込み、{failed} 件読めず");
        return rooms;
    }

    // ---- 掲示板 ---------------------------------------------------------
    /// <summary>掲示板の画面（自分宛ての掲示の一覧）。ふだんのブラウザで開くときにも使う。</summary>
    public const string NoticeBoardPath = Flows + "?_flowId=KJW0001100-flow";

    /// <summary>
    /// 掲示板（KJW0001100-flow）の掲示のうち、since より後に掲載されたものを読む。
    /// 一覧の表（掲載日時・表題・返信未読・ジャンル・所属・氏名・掲示期間）を読み、本文（div.keiji-naiyo）は
    /// needsBody が true を返した掲示（まだ霽月に残していないもの）だけ、1 件ずつ間をあけて開く（多くても maxBodies 件）。
    /// 本文を開かなかった掲示は Body が null。ログインが切れていたら NuWebLoginException。
    /// 本文のリンクは「未読の表示」（displayMidoku）なので、開くと NU-Web の側でもその掲示は既読になる
    /// （掲示板の一覧から外れる）。使う人と相談のうえ、本文も霽月で読めるほうを選んでいる。
    /// </summary>
    public async Task<IReadOnlyList<NuWebNotice>> ReadNoticesAsync(DateTime since, Func<string, bool> needsBody, int maxBodies)
    {
        var (page, at) = await GetAsync(NoticeBoardPath);
        if (!page.Contains("keiji-input", StringComparison.Ordinal))
        {
            NagasakiLog.Write($"NU-Web: 掲示板の一覧がありません（{NagasakiLog.Where(at)}）");
            if (at.Host != NagasakiUniversity.NuWebHost || page.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase))
                throw new NuWebLoginException();
            throw new NuWebException("掲示板の一覧が見つかりませんでした");
        }

        var notices = new List<NuWebNotice>();
        var opened = 0;
        foreach (Match row in NoticeRowRegex().Matches(page))
        {
            if (!DateTime.TryParse(row.Groups["date"].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var posted) || posted < since)
                continue;
            var href = WebUtility.HtmlDecode(row.Groups["href"].Value);
            if (NoticeIdRegex().Match(href) is not { Success: true } key)
                continue;
            var id = $"{key.Groups[1].Value}-{key.Groups[2].Value}-{key.Groups[3].Value}";
            var dept = Line(row.Groups["dept"].Value);
            var name = Line(row.Groups["name"].Value);
            var sender = name == "" || name == dept ? dept : $"{dept}／{name}";

            string? body = null;
            if (opened < maxBodies && needsBody(id))
            {
                opened++;
                await Task.Delay(NoticeGap); // 続けて何件も開くときは、少し間をあける
                var (detail, _) = await GetAsync("/campusweb/" + href);
                body = NoticeBodyRegex().Match(detail) is { Success: true } naiyo ? LacsClient.HtmlToText(naiyo.Groups[1].Value) : "";
            }
            notices.Add(new NuWebNotice(id, Line(row.Groups["genre"].Value), sender, Line(row.Groups["title"].Value), body, posted));
        }
        NagasakiLog.Write($"NU-Web: 掲示 {notices.Count} 件（本文を開いたもの {opened} 件）");
        return notices;
    }

    private static readonly TimeSpan NoticeGap = TimeSpan.FromMilliseconds(300);

    /// <summary>表の 1 マスを 1 行の文字にする（前後の改行や続いた空白を詰める）。</summary>
    private static string Line(string markup) => Regex.Replace(Text(markup), @"\s+", " ").Trim();

    private async Task<(string Page, Uri At)> GetAsync(string path)
    {
        using var response = await _http.GetAsync(path);
        var at = response.RequestMessage?.RequestUri ?? new Uri(_http.BaseAddress!, path);
        if (!response.IsSuccessStatusCode)
            throw new NuWebException($"NU-Web の応答 {(int)response.StatusCode}（{NagasakiLog.Where(at)}）");
        return (await response.Content.ReadAsStringAsync(), at);
    }

    private static string FlowKey(string page) =>
        FlowKeyRegex().Match(page) is { Success: true } m ? m.Groups[1].Value
            : throw new NuWebException("NU-Web の画面の鍵（_flowExecutionKey）が見つかりません");

    // ---- ページの読み取り（試験できるように public） ---------------------------
    /// <summary>「履修登録・登録状況照会」の表を、1 コマずつにする。</summary>
    public static IReadOnlyList<ClassSlot> ParseRegistration(string page)
    {
        var days = new List<int>();
        var slots = new List<ClassSlot>();
        int? period = null;
        var column = 0;
        foreach (Match m in GridCellRegex().Matches(page))
        {
            var head = m.Groups[1].Success ? m.Groups[1].Value.Trim() : null;
            if (head is not null && !m.Groups[2].Success)
            {
                if (PeriodHeadRegex().Match(head) is { Success: true } p)
                    (period, column) = (int.Parse(p.Groups[1].Value), 0);
                else if (DayHeadRegex().IsMatch(head))
                    days.Add(Array.IndexOf(NagasakiUniversity.WeekdayNames, head[..1]));
                continue;
            }
            if (period is not { } current)
                continue;
            var day = column < days.Count ? days[column] : 0;
            column++;
            if (day is < 1 or > 6)
                continue;

            var lines = Text(m.Groups[2].Value).Split('\n').Select(l => l.Trim()).Where(l => l != "").ToList();
            var names = new List<string>();
            var teachers = new List<string>();
            var codes = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (!CodeRegex().IsMatch(lines[i]) || i + 1 >= lines.Count)
                    continue;
                var rest = lines.Skip(i + 1).TakeWhile(l => !CodeRegex().IsMatch(l)).ToList();
                if (rest.Count == 0)
                    continue;
                codes.Add(lines[i]);
                names.Add(rest[0]);
                if (rest.Skip(1).FirstOrDefault(l => !CreditRegex().IsMatch(l)) is { } teacher)
                    teachers.Add(Regex.Replace(teacher, @"\s+", " "));
            }
            if (names.Count > 0)
                slots.Add(new ClassSlot(day, current, string.Join(" / ", names), string.Join(" / ", teachers), "",
                    Codes: string.Join(",", codes)));
        }
        // 同じコマが 2 回出てきたら、あとの方を使う
        return slots.GroupBy(s => (s.Weekday, s.Period)).Select(g => g.Last())
            .OrderBy(s => s.Weekday).ThenBy(s => s.Period).ToList();
    }

    /// <summary>シラバスの「教室」の欄。「教養教育A棟24／RoomA-24」は日本語の名前だけ残す。</summary>
    public static string ParseRoom(string page)
    {
        // シラバスは 3 つのタブに分かれていて、3 つ目は計画の繰り返しなので、1 つ目と 2 つ目だけ見る
        var start = page.IndexOf("id=\"tabs-1\"", StringComparison.Ordinal);
        var end = start >= 0 ? page.IndexOf("id=\"tabs-3\"", start, StringComparison.Ordinal) : -1;
        if (start >= 0)
            page = end > 0 ? page[start..end] : page[start..];
        page = TooltipRegex().Replace(page, "");
        foreach (Match row in SyllabusRowRegex().Matches(page))
        {
            var label = Text(row.Groups[1].Value).Trim().Split('\n')[0].Split('／')[0].Trim();
            if (label != "教室")
                continue;
            return string.Join("、", Text(row.Groups[2].Value).Split(',', '\n')
                .Select(part => part.Split('／')[0].Trim()).Where(part => part != ""));
        }
        return "";
    }

    /// <summary>HTML を文字だけにする（&lt;br&gt; は改行）。</summary>
    private static string Text(string markup)
    {
        markup = BrRegex().Replace(markup, "\n");
        markup = TagRegex().Replace(markup, "");
        return WebUtility.HtmlDecode(markup).Replace('　', ' ');
    }

    [GeneratedRegex(@"class=""rishu-koma-head""[^>]*>\s*([^<]*?)\s*<|class=""rishu-koma-inner""[^>]*>(.*?)</table>", RegexOptions.Singleline)]
    private static partial Regex GridCellRegex();

    [GeneratedRegex(@"^(\d+)校時$")]
    private static partial Regex PeriodHeadRegex();

    [GeneratedRegex(@"^[月火水木金土日]曜日$")]
    private static partial Regex DayHeadRegex();

    [GeneratedRegex(@"^\d{14}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^\(\s*[\d.]+\s*\)$")]
    private static partial Regex CreditRegex();

    [GeneratedRegex(@"_flowExecutionKey(?:=|""\s+value="")([\w-]+)")]
    private static partial Regex FlowKeyRegex();

    [GeneratedRegex(@"refer\('(\d{4})','(\w+)','(\d{14})','ja_JP'\)")]
    private static partial Regex ReferRegex();

    [GeneratedRegex(@"<th[^>]*class=""syllabus-prin[^""]*""[^>]*>(.*?)</th>\s*<td[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex SyllabusRowRegex();

    [GeneratedRegex(@"<div class=""sy-tooltip.*?</div>", RegexOptions.Singleline)]
    private static partial Regex TooltipRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"<!--.*?-->|<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    // 掲示板の一覧の 1 行: 掲載日時・表題（本文へのリンク）・返信未読・ジャンル・所属・氏名
    [GeneratedRegex(@"<tr>\s*<td>\s*(?<date>\d{4}/\d{1,2}/\d{1,2} \d{1,2}:\d{2}:\d{2})\s*</td>\s*<td>\s*<a href=""(?<href>[^""]+)"">(?<title>.*?)</a>\s*</td>\s*<td[^>]*>.*?</td>\s*<td>(?<genre>.*?)</td>\s*<td>(?<dept>.*?)</td>\s*<td>(?<name>.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex NoticeRowRegex();

    // 掲示 1 件を見分ける番号（リンクの keijitype・genrecd・seqNo）
    [GeneratedRegex(@"keijitype=(\d+)&genrecd=(\d+)&seqNo=(\d+)")]
    private static partial Regex NoticeIdRegex();

    // 掲示の本文
    [GeneratedRegex(@"<div class=""keiji-naiyo"">(.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex NoticeBodyRegex();
}

/// <summary>
/// NU-Web の掲示 1 件。Id は「keijitype-genrecd-seqNo」、Genre はジャンル（就職・キャリア、授業 など）、
/// Sender は所属と氏名。Body は本文（開かなかったときは null）。
/// </summary>
public sealed record NuWebNotice(string Id, string Genre, string Sender, string Title, string? Body, DateTime Posted);

/// <summary>NU-Web が思ったページを返さなかった。</summary>
public class NuWebException(string message) : Exception(message);

/// <summary>NU-Web のログインが切れている。</summary>
public sealed class NuWebLoginException() : NuWebException("NU-Web にログインが必要です");
