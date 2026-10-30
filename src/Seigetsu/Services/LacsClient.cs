using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seigetsu.Services;

/// <summary>LACS のログインが切れている（または入れていない）。</summary>
public sealed class LacsLoginException(string reason) : Exception(reason);

/// <summary>
/// LACS の授業 1 つ。Id は API で使う内部の id（_123_1）、Code は授業の番号。
/// Term は学期の名前（例:「2026年度後期」。分からなければ空）、Current は今その学期の授業が行われているか。
/// </summary>
public sealed record LacsCourse(string Id, string Code, string Name, string Term = "", bool Current = true)
{
    /// <summary>授業のページ（Ultra のコース一覧の画面）。</summary>
    public string Url => $"https://{NagasakiUniversity.LacsHost}/ultra/courses/{Id}/outline";
}

/// <summary>
/// 授業の資料のファイル 1 つ。ファイルの項目なら EmbeddedUrl は null（添付から受け取る）、
/// 本文に埋め込まれたファイルなら、そのアドレスと Xid（ファイルの番号）。Folder は LACS の中のフォルダの道すじ。
/// </summary>
public sealed record LacsFile(string CourseId, string ContentId, string? EmbeddedUrl, string Name, string Folder,
    string Xid = "")
{
    public string Key => $"{ContentId}/{Xid}";
}

/// <summary>
/// LACS の連絡事項 1 件。Id は「授業の id/連絡の id」。Body は HTML を文字だけにしたもの。
/// Created は出された日時（公開の始まりが決まっていれば、その日時）。
/// </summary>
public sealed record LacsAnnouncement(
    string Id, string CourseId, string CourseName, string Title, string Body, DateTime Created);

/// <summary>連絡事項を読んだ結果。Courses は授業の数、Failed は読めなかった授業の数。</summary>
public sealed record LacsAnnouncementResult(IReadOnlyList<LacsAnnouncement> Announcements, int Courses, int Failed);

/// <summary>LACS を読んだ結果。Courses は今の授業の数、Failed は読めなかった授業の数。</summary>
public sealed record LacsResult(IReadOnlyList<LacsAssignment> Assignments, int Courses, int Failed);

/// <summary>
/// LACS（Blackboard Learn）の公開 REST API を読む。ログインの窓や見えないブラウザで受け取った Cookie を付けて、
/// 霽月から直接問い合わせる（LACS のページの中からだと、ページの安全の設定で通信が止められるため）。
/// 読むだけで、提出や書き込みは一切しない（GET しか使わない）。
/// 1. 自分（users/me）と、登録している授業（users/{id}/courses）
/// 2. 授業ごとの成績表の列（gradebook/columns）。課題やテストは必ず列を作り、締切（grading.due）はここにある
/// 3. 自分の成績の状態（gradebook/users/{id}）。NeedsGrading / Graded なら提出済み
/// </summary>
public sealed partial class LacsClient : IDisposable
{
    private const string Api = "/learn/api/public";
    private const int Parallel = 4;

    private readonly HttpClient _http;

    /// <param name="cookieHeader">LACS の Cookie（「名前=値; 名前=値」）。</param>
    /// <param name="handler">試験用に差し替えられる。ふだんは null。</param>
    public LacsClient(string cookieHeader, string userAgent, HttpMessageHandler? handler = null)
    {
        // 転送にはついて行かない（ログイン切れのときはログイン画面へ転送されるので、それで気づく）
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"https://{NagasakiUniversity.LacsHost}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (cookieHeader != "")
            _http.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        if (userAgent != "")
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>
    /// 残しておいたログイン（NagasakiSession）の Cookie で読む。LACS が返した新しい Cookie も jar に入るので、
    /// 読み終わったら NagasakiSession.Save で残し直せる。
    /// </summary>
    public LacsClient(CookieContainer jar, string userAgent)
    {
        // 値にカンマを含む LACS のログインの Cookie（BbRouter）は jar に入らないので、NagasakiSession の部品で足して送る
        _http = new HttpClient(NagasakiSession.CreateHandler(jar, allowRedirect: false))
        {
            BaseAddress = new Uri($"https://{NagasakiUniversity.LacsHost}"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (userAgent != "")
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>この Cookie で LACS にログインできているか（自分の情報が読めるか）。</summary>
    public async Task<bool> IsSignedInAsync()
    {
        try
        {
            using var _ = await GetAsync($"{Api}/v1/users/me");
            return true;
        }
        catch (Exception error) when (error is LacsLoginException or LacsApiException or HttpRequestException
                                          or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 登録している授業（公開中のもの）を、名前の順に読む。学期（Term）と、今の学期か（Current）も付ける。
    /// ログインが切れていれば LacsLoginException。
    /// </summary>
    public async Task<IReadOnlyList<LacsCourse>> ReadCoursesAsync()
    {
        using var me = await GetAsync($"{Api}/v1/users/me");
        var userId = me.RootElement.GetProperty("id").GetString()!;
        return (await ReadCourseListAsync(userId, withTerms: true))
            .OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList();
    }

    // ---- 学期 -----------------------------------------------------------
    private sealed record TermInfo(string Name, DateTimeOffset? Start, DateTimeOffset? End);

    /// <summary>
    /// 授業に学期を付ける。学期は terms/{id} の名前と期間（availability.duration）から決める（LACS-MCP と同じ考え）。
    /// LACS の学期の終わりには成績をつける期間も入っている（前期は 10 月まで）ので、
    /// 同じ種類（クォーター・前期後期）の次の学期が始まっていたら、前の学期は終わったとみなす。
    /// 種類や日付が分からない学期は「今の学期」に入れる（まちがって隠さないように）。
    /// </summary>
    private async Task<List<LacsCourse>> WithTermsAsync(List<LacsCourse> courses, Dictionary<string, string> termOf)
    {
        var terms = new Dictionary<string, TermInfo>();
        foreach (var id in termOf.Values.Distinct())
        {
            try
            {
                using var term = await GetAsync($"{Api}/v1/terms/{id}");
                var root = term.RootElement;
                DateTimeOffset? start = null, end = null;
                if (root.TryGetProperty("availability", out var availability)
                    && availability.TryGetProperty("duration", out var duration))
                {
                    if (DateTimeOffset.TryParse(Str(duration, "start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var s))
                        start = s;
                    if (DateTimeOffset.TryParse(Str(duration, "end"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var e))
                        end = e;
                }
                // 「2026年度後期 / 2026 Fall」のような名前は、日本語の方だけ残す
                terms[id] = new TermInfo(Str(root, "name").Split(" / ")[0].Trim(), start, end);
            }
            catch (LacsApiException)
            {
                // 学期が読めない授業は、学期なし（今の学期）のまま
            }
        }

        var now = DateTimeOffset.Now;
        bool IsCurrent(TermInfo term)
        {
            var kind = TermKind(term.Name);
            if (term.Start is not { } start || start > now)
                return term.Start is null;
            if (term.End is { } end && end < now)
                return false;
            if (kind is null or "year")
                return true;
            return !terms.Values.Any(t => TermKind(t.Name) == kind && t.Start is { } s && start < s && s <= now);
        }

        return courses.Select(c => termOf.TryGetValue(c.Id, out var id) && terms.TryGetValue(id, out var term)
            ? c with { Term = term.Name, Current = IsCurrent(term) }
            : c).ToList();
    }

    private static string? TermKind(string name)
    {
        var m = Regex.Match(name, "([1-4])Q|(前期|後期)|(通年)");
        return !m.Success ? null : m.Groups[1].Success ? "quarter" : m.Groups[2].Success ? "half" : "year";
    }

    // ---- 資料（ファイル） -------------------------------------------------
    /// <summary>
    /// 授業の中にあるファイルを全部探す（LACS-MCP の materials と同じ）。フォルダの中もたどる。
    /// ファイルの項目のほか、文書の本文や課題の説明に埋め込まれた PDF なども拾う。
    /// まだ受けていない小テスト・試験は開かない（開くと時間が始まったり、回数を使ったりすることがあるため）。
    /// </summary>
    public async Task<IReadOnlyList<LacsFile>> ReadMaterialsAsync(string courseId, int maxItems = 400)
    {
        var files = new List<LacsFile>();
        var seen = 0;

        async Task WalkAsync(string? parentId, string folder, int depth)
        {
            var path = $"{Api}/v1/courses/{courseId}/contents" + (parentId is null ? "" : $"/{parentId}/children");
            List<JsonElement> items;
            try
            {
                items = await GetAllAsync(path);
            }
            catch (LacsApiException)
            {
                return; // 見られないフォルダは飛ばす
            }
            foreach (var item in items)
            {
                if (++seen > maxItems)
                    return;
                var id = Str(item, "id");
                var title = Str(item, "title");
                item.TryGetProperty("contentHandler", out var handler);
                var kind = Str(handler, "id");
                if (kind is "resource/x-bb-asmt-test-link" or "resource/x-bb-assessment" && QuizName().IsMatch(title))
                    continue;
                if (kind == "resource/x-bb-file")
                {
                    var name = handler.TryGetProperty("file", out var file) ? Str(file, "fileName") : "";
                    files.Add(new LacsFile(courseId, id, null, name == "" ? title : name, folder));
                }
                var markup = Str(item, "body") + Str(handler, "instructions");
                foreach (Match link in EmbeddedLink().Matches(markup))
                {
                    var url = WebUtility.HtmlDecode(link.Groups[1].Value);
                    if (Xid().Match(url) is not { Success: true } xid)
                        continue;
                    var name = url.Split('?')[0].Split('/')[^1];
                    if (BbFile().Match(link.Value) is { Success: true } raw)
                    {
                        try
                        {
                            using var info = JsonDocument.Parse(WebUtility.HtmlDecode(raw.Groups[1].Value));
                            var fileName = Str(info.RootElement, "fileName");
                            name = fileName != "" ? fileName : Str(info.RootElement, "displayName") is { Length: > 0 } d ? d : name;
                        }
                        catch (JsonException)
                        {
                        }
                    }
                    files.Add(new LacsFile(courseId, id, url, Uri.UnescapeDataString(name), folder, xid.Groups[1].Value));
                }
                var hasChildren = item.TryGetProperty("hasChildren", out var h) && h.ValueKind == JsonValueKind.True;
                if (hasChildren && depth < 6)
                {
                    // フォルダとレッスンだけをフォルダにする（Ultra の文書は中身を同じフォルダのまま読む）
                    var isPage = handler.ValueKind == JsonValueKind.Object && handler.TryGetProperty("isBbPage", out var p)
                                 && p.ValueKind == JsonValueKind.True;
                    var sub = kind is "resource/x-bb-folder" or "resource/x-bb-lesson" && !isPage
                        ? $"{folder}/{title}".Trim('/') : folder;
                    await WalkAsync(id, sub, depth + 1);
                }
            }
        }

        await WalkAsync(null, "", 0);
        // 同じファイルが 2 か所から張られていることがある
        return files.GroupBy(f => f.Key).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// ファイルを 1 つ保存する。同じ名前で同じ大きさのファイルがあれば保存しない（false）。
    /// ファイルは LACS か Blackboard のファイル置き場（*.blackboard.com）からしか受け取らない。
    /// </summary>
    public async Task<bool> DownloadAsync(LacsFile file, string path)
    {
        var url = file.EmbeddedUrl ?? await FileUrlAsync(file.CourseId, file.ContentId)
                  ?? throw new LacsApiException(404, "この資料にはファイルがありません");
        var target = new Uri(new Uri($"https://{NagasakiUniversity.LacsHost}"), url);
        using var plain = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        for (var hop = 0; hop < 8; hop++)
        {
            if (target.Host.StartsWith("nuidp.", StringComparison.OrdinalIgnoreCase)
                || target.AbsolutePath.Contains("/auth-saml/") || target.AbsolutePath.Contains("/webapps/login"))
                throw new LacsLoginException("LACS のログインが切れています");
            var onLacs = target.Host.Equals(NagasakiUniversity.LacsHost, StringComparison.OrdinalIgnoreCase);
            if (!onLacs && !target.Host.EndsWith(".blackboard.com", StringComparison.OrdinalIgnoreCase))
                throw new LacsApiException(0, $"ファイルの置き場がいつもと違うので受け取りませんでした（{target.Host}）");
            // LACS のログインの Cookie は LACS にだけ送る
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await (onLacs ? _http : plain).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                target = new Uri(target, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new LacsApiException((int)response.StatusCode, file.Name);
            if (response.Content.Headers.ContentType?.MediaType == "text/html"
                && !file.Name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                && !file.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                throw new LacsApiException(0, $"{file.Name} はファイルではなく LACS のページでした");

            var size = response.Content.Headers.ContentLength;
            if (File.Exists(path) && size is { } length && new FileInfo(path).Length == length)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var part = path + ".part";
            await using (var output = File.Create(part))
                await response.Content.CopyToAsync(output);
            if (File.Exists(path) && SameFile(part, path))
            {
                File.Delete(part);
                return false;
            }
            File.Move(part, FreePath(path));
            return true;
        }
        throw new LacsApiException(0, "転送が多すぎて受け取れませんでした");
    }

    private static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>そのファイル名が使われていれば「名前 (2).pdf」のようにする。</summary>
    public static string FreePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>Windows で使えるファイル名・フォルダ名にする（長すぎる名前は縮める）。</summary>
    public static string SafeName(string text)
    {
        text = Regex.Replace(text, @"[<>:""/\\|?*\x00-\x1f]", "_").Trim(' ', '.', '●');
        if (text.Length > 60)
        {
            var ext = Path.GetExtension(text);
            text = ext.Length is > 0 and <= 6 ? text[..(60 - ext.Length - 1)] + "…" + ext : text[..59] + "…";
        }
        return text == "" ? "_" : text;
    }

    [GeneratedRegex(@"<a\b[^>]*?href=""([^""]*/bbcswebdav/[^""]*)""[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedLink();

    [GeneratedRegex(@"data-bbfile=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex BbFile();

    [GeneratedRegex(@"/xid-([0-9_]+)")]
    private static partial Regex Xid();

    [GeneratedRegex(@"テスト|クイズ|試験|理解度|確認問題|\b(quiz|exam|test)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuizName();

    /// <summary>
    /// 授業の資料（ファイル）1 つの、中身そのもののアドレス。ブラウザで開くと、LACS の画面を通さずに
    /// ファイルが出る（PDF なら Edge の PDF 表示）。添付が無ければ null。PDF があれば PDF を選ぶ。
    /// </summary>
    public async Task<string?> FileUrlAsync(string courseId, string contentId)
    {
        var path = $"{Api}/v1/courses/{courseId}/contents/{contentId}/attachments";
        var attachments = await GetAllAsync(path);
        var chosen = attachments.FirstOrDefault(a => Str(a, "mimeType") == "application/pdf"
                                                     || Str(a, "fileName").EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (chosen.ValueKind == JsonValueKind.Undefined)
            chosen = attachments.FirstOrDefault();
        var id = chosen.ValueKind == JsonValueKind.Object ? Str(chosen, "id") : "";
        // LACS のページは部品を別のサイトから読むので、相対のアドレスにせず、必ず LACS のアドレスを付ける
        return id == "" ? null : $"https://{NagasakiUniversity.LacsHost}{path}/{id}/download";
    }

    /// <summary>
    /// 課題（成績表の列）の画面のアドレス。列が課題やテストのコンテンツに結び付いていれば、その画面
    /// （授業のページの課題のリンクと同じ「/ultra/courses/{授業}/assessment/{コンテンツ}/overview」）。結び付きが無ければ null。
    /// </summary>
    public async Task<string?> AssignmentUrlAsync(string courseId, string columnId)
    {
        using var column = await GetAsync($"{Api}/v2/courses/{courseId}/gradebook/columns/{columnId}");
        var contentId = Str(column.RootElement, "contentId");
        return contentId == "" ? null
            : $"https://{NagasakiUniversity.LacsHost}/ultra/courses/{courseId}/assessment/{contentId}/overview?courseId={courseId}";
    }

    private async Task<List<LacsCourse>> ReadCourseListAsync(string userId, bool withTerms = false)
    {
        var courses = new List<LacsCourse>();
        var termOf = new Dictionary<string, string>();
        foreach (var membership in await GetAllAsync($"{Api}/v1/users/{userId}/courses?expand=course&limit=100"))
        {
            if (!membership.TryGetProperty("course", out var course))
                continue;
            // 公開が終わった授業は読まない
            if (course.TryGetProperty("availability", out var availability)
                && Str(availability, "available") == "No")
                continue;
            courses.Add(new LacsCourse(Str(course, "id"), Str(course, "courseId"), Str(course, "name").Trim()));
            if (Str(course, "termId") is { Length: > 0 } termId)
                termOf[Str(course, "id")] = termId;
        }
        return withTerms ? await WithTermsAsync(courses, termOf) : courses;
    }

    /// <summary>
    /// 登録している授業の連絡事項のうち、since より後に出たものを読む（下書きと、まだ公開前のものは除く）。
    /// ログインが切れていれば LacsLoginException。1 つの授業が読めなくても、ほかの授業は読む。
    /// </summary>
    public async Task<LacsAnnouncementResult> ReadAnnouncementsAsync(DateTimeOffset since)
    {
        using var me = await GetAsync($"{Api}/v1/users/me");
        var userId = me.RootElement.GetProperty("id").GetString()!;
        var courses = await ReadCourseListAsync(userId);

        var announcements = new List<LacsAnnouncement>();
        var failed = 0;
        var now = DateTimeOffset.Now;
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(courses.Select(async course =>
        {
            await gate.WaitAsync();
            try
            {
                foreach (var item in await GetAllAsync($"{Api}/v1/courses/{course.Id}/announcements"))
                {
                    if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                        continue;
                    // 公開の期間が決まっていれば、その始まりを出された日時とみなす（まだ始まっていなければ出さない）
                    var created = Date(Str(item, "created"));
                    if (item.TryGetProperty("availability", out var availability)
                        && availability.TryGetProperty("duration", out var duration)
                        && Date(Str(duration, "start")) is { } start)
                    {
                        if (start > now)
                            continue;
                        created = start;
                    }
                    // 公開の終わりが過ぎたものも出さない
                    if (item.TryGetProperty("availability", out var shown)
                        && shown.TryGetProperty("duration", out var span)
                        && Date(Str(span, "end")) is { } end && end < now)
                        continue;
                    if (created is not { } when || when < since)
                        continue;
                    var id = Str(item, "id");
                    if (id == "")
                        continue;
                    var found = new LacsAnnouncement($"{course.Id}/{id}", course.Id, course.Name,
                        Str(item, "title").Trim(), HtmlToText(Str(item, "body")), when.ToLocalTime().DateTime);
                    lock (announcements)
                        announcements.Add(found);
                }
            }
            catch (LacsLoginException)
            {
                // 公開が終わった授業や権限のない授業も 401 / 403 を返す。ログインできていれば、この授業だけ飛ばす
                if (!await StillSignedInAsync())
                    throw;
                Interlocked.Increment(ref failed);
            }
            catch (Exception error) when (error is HttpRequestException or LacsApiException or JsonException
                                              or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        }));
        return new LacsAnnouncementResult(
            announcements.OrderByDescending(a => a.Created).ToList(), courses.Count, failed);
    }

    private static DateTimeOffset? Date(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null;

    /// <summary>連絡事項の本文（HTML）を、改行を残した文字だけにする。</summary>
    public static string HtmlToText(string html)
    {
        if (html == "")
            return "";
        var options = RegexOptions.IgnoreCase;
        var text = Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h\d|/tr)\b[^>]*>", "\n", options);
        text = Regex.Replace(text, @"<\s*li\b[^>]*>", "・", options);
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        // 空の行は続けて 2 つ以上置かない
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim() == "" && (result.Count == 0 || result[^1] == ""))
                continue;
            result.Add(line.Trim() == "" ? "" : line);
        }
        return string.Join("\n", result).Trim();
    }

    /// <summary>締切が from 〜 to の課題を読む。ログインが切れていれば LacsLoginException。</summary>
    public async Task<LacsResult> ReadAssignmentsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        using var me = await GetAsync($"{Api}/v1/users/me");
        var userId = me.RootElement.GetProperty("id").GetString()!;

        var courses = await ReadCourseListAsync(userId);

        var assignments = new List<LacsAssignment>();
        var failed = 0;
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(courses.Select(async course =>
        {
            await gate.WaitAsync();
            try
            {
                var found = await ReadCourseAsync(course.Id, course.Name, userId, from, to);
                lock (assignments)
                    assignments.AddRange(found);
            }
            catch (LacsLoginException)
            {
                // 公開が終わった授業や権限のない授業も 401 / 403 を返す。ログインできていれば、この授業だけ飛ばす
                if (!await StillSignedInAsync())
                    throw;
                Interlocked.Increment(ref failed);
            }
            catch (Exception error) when (error is HttpRequestException or LacsApiException or JsonException
                                              or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
            {
                Interlocked.Increment(ref failed); // 1 つの授業が読めなくても、ほかの授業は読む
            }
            finally
            {
                gate.Release();
            }
        }));
        return new LacsResult(assignments.OrderBy(a => a.Due).ToList(), courses.Count, failed);
    }

    private async Task<List<LacsAssignment>> ReadCourseAsync(
        string courseId, string courseName, string userId, DateTimeOffset from, DateTimeOffset to)
    {
        var due = new List<(string Id, string Name, DateTimeOffset Due)>();
        foreach (var column in await GetAllAsync($"{Api}/v2/courses/{courseId}/gradebook/columns"))
        {
            if (!column.TryGetProperty("grading", out var grading)
                || !DateTimeOffset.TryParse(Str(grading, "due"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
                || when < from || when > to)
                continue;
            due.Add((Str(column, "id"), Str(column, "name").Trim(), when));
        }
        if (due.Count == 0)
            return [];

        // 提出したかどうか。読めなくても課題は取り込む
        var status = new Dictionary<string, string>();
        try
        {
            foreach (var grade in await GetAllAsync($"{Api}/v2/courses/{courseId}/gradebook/users/{userId}"))
                status[Str(grade, "columnId")] = Str(grade, "status");
        }
        catch (LacsApiException)
        {
        }

        return due.Where(d => d.Id != "" && d.Name != "").Select(d => new LacsAssignment(
            $"{courseId}/{d.Id}", d.Name, courseName.Trim(), d.Due.ToLocalTime().DateTime,
            // NeedsGrading（採点待ち）と Graded（採点済み）は、何か提出してあるということ
            status.GetValueOrDefault(d.Id, "") is "NeedsGrading" or "Graded")).ToList();
    }

    /// <summary>
    /// 授業ごとの読み込みで 401 / 403 が返ったとき、本当にログインが切れたのかを確かめる（自分の情報が読めるか）。
    /// LACS は、公開が終わった授業や権限のない授業にも 401 / 403 を返すため。
    /// </summary>
    private async Task<bool> StillSignedInAsync()
    {
        try
        {
            using var _ = await GetAsync($"{Api}/v1/users/me");
            return true;
        }
        catch (Exception error) when (error is LacsLoginException or LacsApiException or HttpRequestException
                                          or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>ページに分かれた一覧（paging.nextPage）をたどって、results を全部集める。</summary>
    private async Task<List<JsonElement>> GetAllAsync(string url)
    {
        var all = new List<JsonElement>();
        for (var page = 0; url != "" && page < 20; page++)
        {
            using var document = await GetAsync(url);
            if (document.RootElement.TryGetProperty("results", out var results))
                all.AddRange(results.EnumerateArray().Select(e => e.Clone()));
            url = document.RootElement.TryGetProperty("paging", out var paging) ? Str(paging, "nextPage") : "";
        }
        return all;
    }

    private async Task<JsonDocument> GetAsync(string url)
    {
        using var response = await _http.GetAsync(url);
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        // ログインできているときは何度も読むので、記録はうまくいかなかったときだけ
        if (url.EndsWith("/users/me", StringComparison.Ordinal) && !response.IsSuccessStatusCode)
            NagasakiLog.Write($"LACS: users/me → {(int)response.StatusCode} {type}"
                              + (response.Headers.Location is { } to ? $"（転送先 {NagasakiLog.Where(to.IsAbsoluteUri ? to : null)}）" : ""));
        // ログインが切れていると、401 / 403 か、ログイン画面への転送か、JSON でない（ログイン画面の）ページが返る
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (int)response.StatusCode is >= 300 and < 400
            || (response.IsSuccessStatusCode && !type.Contains("json", StringComparison.OrdinalIgnoreCase)))
            throw new LacsLoginException($"LACS の応答 {(int)response.StatusCode}"
                                         + (type == "" ? "" : $"（{type}）") + $"（{url.Split('?')[0]}）");
        if (!response.IsSuccessStatusCode)
            throw new LacsApiException((int)response.StatusCode, url.Split('?')[0]);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}

/// <summary>LACS が「ログイン切れ」以外のエラーを返した。</summary>
public sealed class LacsApiException(int status, string path) : Exception($"応答 {status}（{path}）");
