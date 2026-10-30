using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// 1 回の自動更新の結果。LacsNote は、LACS にログインできなかったときの手がかり（どこで止まったか、LACS が何と答えたか）。
/// </summary>
public sealed record NagasakiReport(
    bool NuWebNeedsLogin, bool LacsNeedsLogin, int Classes, int Assignments, int Added, string? Error,
    string? LacsNote = null, int NewAnnouncements = 0)
{
    public bool NeedsLogin => NuWebNeedsLogin || LacsNeedsLogin;
}

/// <summary>
/// 長崎大学モードの自動更新。手順は LACS-MCP（作った人の許可を得て使っている）と同じ。
/// - ログインの Cookie は NagasakiSession に残し、アプリを閉じても使えるようにする
/// - 読むのは霽月から直接（LacsClient・NuWebClient）。ページの中のスクリプトは使わない
/// - Cookie で読めないときだけ、見えないブラウザで長大ID のログインに入り直す（長大ID のログインが残っていれば、
///   パスワードなしで通る）。それでもだめなら「ログインが必要」と伝え、NagasakiWindow で本人にログインしてもらう
/// - ログインを延ばすための読み込み（何もしないのに定期的に読むこと）はしない。大学のサイトに記録が残るため
/// 霽月の起動（Windows の起動で自動で起動する）とスリープからの復帰の 30 秒後に更新し、そのあとは PC が動いている間、
/// 前の更新が終わってから 2〜3 時間のうちのランダムな時間ごとに更新する。どこで止まったかは NagasakiLog に書く。
/// </summary>
public sealed class NagasakiSync
{
    // 起動・スリープからの復帰のあと、最初の更新までの待ち時間
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);

    // 2 回目からの更新の間隔は、この範囲のランダムな時間（毎回決まった時刻に読みに行かないように）
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(2);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromHours(3);

    // LACS の連絡事項は、この日数より前に出たものは読まない
    private const int AnnouncementDays = 60;

    // NU-Web の掲示の本文を、1 回の更新で開く数の上限（初めてのときに何百件も開かないように）
    private const int MaxNoticeBodies = 120;
    private const string UserAgent = "Seigetsu";

    private readonly DispatcherTimer _timer = new() { Interval = FirstDelay };
    private bool _started;
    private Task<CoreWebView2Environment>? _environment;
    private HiddenPage? _page;
    private CookieContainer? _jar;
    private Task<NagasakiReport>? _running;

    /// <summary>自動更新が終わるたびに呼ばれる（userAsked は、ボタンから頼まれたときに true）。</summary>
    public event Action<NagasakiReport, bool>? Synced;

    /// <summary>状態が変わったとき（始まった・終わった）に呼ばれる。</summary>
    public event Action? Changed;

    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>設定画面に出す、前回の結果（アプリを閉じても残す）。</summary>
    public static string LastStatus => App.Database.GetSetting(SettingKeys.NagasakiStatus, "まだ更新していません");

    /// <summary>ログインの Cookie（残しておいたものから始める）。</summary>
    private CookieContainer Jar => _jar ??= NagasakiSession.Load();

    /// <summary>自動更新を始める（起動の 30 秒後に 1 回目）。スリープからの復帰も見張る。</summary>
    public void Start()
    {
        _started = true;
        _timer.Tick += async (_, _) =>
        {
            // 1 回ごとに止め、更新が終わったら次の時間を決める（RunAsync の最後）
            _timer.Stop();
            if (NagasakiUniversity.Enabled)
                await SyncAsync(userAsked: false);
            else
                ScheduleNext();
        };
        Schedule(FirstDelay);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>アプリを終わるときに呼ぶ（SystemEvents は静的なイベントなので外しておく）。</summary>
    public void Stop()
    {
        _started = false;
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    // スリープに入るときは止め、戻ったら 30 秒後に更新する。知らせは別のスレッドから来るので、画面のスレッドに移す
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;
        if (e.Mode == PowerModes.Suspend)
            dispatcher.BeginInvoke(() =>
            {
                _timer.Stop();
                NagasakiLog.Write("スリープに入ります（自動更新を止めます）");
            });
        else if (e.Mode == PowerModes.Resume)
            dispatcher.BeginInvoke(() =>
            {
                NagasakiLog.Write("スリープから戻りました（30 秒後に更新します）");
                Schedule(FirstDelay);
            });
    }

    private void Schedule(TimeSpan after)
    {
        if (!_started)
            return;
        _timer.Stop();
        _timer.Interval = after;
        _timer.Start();
    }

    /// <summary>次の更新を、今から 2〜3 時間のうちのランダムな時間に決める。</summary>
    private void ScheduleNext()
    {
        var span = MinInterval + TimeSpan.FromSeconds(Random.Shared.NextDouble() * (MaxInterval - MinInterval).TotalSeconds);
        Schedule(span);
        if (_started)
            NagasakiLog.Write($"次の自動更新：{DateTime.Now + span:M/d H:mm} ごろ");
    }

    /// <summary>ログインの窓もこれを使う（同じ保存場所の WebView2 は、同じ作り方でないと一緒に開けないため）。</summary>
    public Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewDir);

    /// <summary>
    /// 自動更新が残しているログインを、霽月の中のブラウザ（LACS のページ）に渡す。
    /// ブラウザの方がログインしている（Cookie が新しい）ときは、先にそれを受け取ってから渡す。
    /// </summary>
    public async Task ShareLoginWithAsync(CoreWebView2 core)
    {
        if (await LacsSignedInAsync(core))
            await NagasakiSession.MergeFromBrowserAsync(core, Jar);
        NagasakiSession.CopyToBrowser(core, Jar);
    }

    /// <summary>ログインの窓でログインできたあと、そのブラウザの Cookie を受け取って残す。</summary>
    public async Task AdoptBrowserLoginAsync(CoreWebView2 core)
    {
        await NagasakiSession.MergeFromBrowserAsync(core, Jar);
        NagasakiSession.Save(Jar);
        NagasakiLog.Write("ログインの窓のログインを受け取りました");
    }

    /// <summary>今すぐ更新する。すでに更新中なら、その終わりを待つ。</summary>
    public Task<NagasakiReport> SyncAsync(bool userAsked)
    {
        if (_running is { IsCompleted: false })
            return _running;
        _running = RunAsync(userAsked);
        Changed?.Invoke();
        return _running;
    }

    private async Task<NagasakiReport> RunAsync(bool userAsked)
    {
        NagasakiReport report;
        NagasakiLog.Write(userAsked ? "---- 更新を始めます（ボタンから）" : "---- 更新を始めます（自動）");
        try
        {
            report = await ReadAllAsync();
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException)
        {
            report = new NagasakiReport(false, false, 0, 0, 0,
                "ブラウザの部品（Microsoft Edge WebView2 ランタイム）が見つかりません。");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
                                          or JsonException or KeyNotFoundException or TimeoutException)
        {
            report = new NagasakiReport(false, false, 0, 0, 0, error.Message);
        }

        var status = Describe(report);
        NagasakiLog.Write($"結果：{status}");
        App.Database.SetSetting(SettingKeys.NagasakiStatus, status);
        if (!report.NeedsLogin && report.Error is null)
            App.Database.SetSetting(SettingKeys.NagasakiSyncedAt, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        _running = null;
        // 次の自動更新は、この更新が終わってから 2〜3 時間後（ボタンで更新したときも数え直す）
        ScheduleNext();
        Changed?.Invoke();
        Synced?.Invoke(report, userAsked);
        return report;
    }

    /// <summary>結果を 1 行の文にする。</summary>
    public static string Describe(NagasakiReport report)
    {
        var at = DateTime.Now.ToString("M月d日 H:mm");
        if (report.Error is { } error)
            return $"{at}　更新できませんでした（{error}）";
        var parts = new List<string>();
        if (!report.NuWebNeedsLogin)
            parts.Add($"時間割 {report.Classes} コマ");
        if (!report.LacsNeedsLogin)
            parts.Add($"LACS の課題 {report.Assignments} 件" + (report.Added > 0 ? $"（新しく {report.Added} 件）" : "")
                      + (report.NewAnnouncements > 0 ? $"・新しいお知らせ {report.NewAnnouncements} 件" : ""));
        var text = $"{at}　" + (parts.Count > 0 ? string.Join("・", parts) + " を読み込みました" : "");
        if (report.NeedsLogin)
        {
            var sites = report.NuWebNeedsLogin && report.LacsNeedsLogin ? "NU-Web と LACS"
                : report.NuWebNeedsLogin ? "NU-Web" : "LACS";
            text += (parts.Count > 0 ? "。" : "") + $"{sites} にログインが必要です";
            if (report.LacsNeedsLogin && report.LacsNote is { } note)
                text += $"（LACS：{note}）";
        }
        return text;
    }

    private async Task<HiddenPage> PageAsync() => _page ??= await HiddenPage.CreateAsync(await EnvironmentAsync());

    private async Task<NagasakiReport> ReadAllAsync()
    {
        var jar = Jar;
        string? error = null;

        // ---- NU-Web ----
        var classes = 0;
        using var nuWeb = new NuWebClient(jar, UserAgent);
        var nuWebOk = await nuWeb.IsSignedInAsync();
        if (!nuWebOk)
        {
            // 長大ID の入口から入り直す。長大ID のログインが残っていれば、パスワードなしで NU-Web に着く
            var page = await PageAsync();
            var at = await page.NavigateAsync(NagasakiUniversity.NuWebSignIn);
            NagasakiLog.Write($"NU-Web: 長大ID の入口 → {NagasakiLog.Where(at)}");
            await NagasakiSession.MergeFromBrowserAsync(page.Core, jar);
            nuWebOk = await nuWeb.IsSignedInAsync();
        }
        if (nuWebOk)
        {
            try
            {
                var slots = await nuWeb.ReadTimetableAsync();
                if (slots.Count > 0)
                {
                    slots = await AddRoomsAsync(nuWeb, slots);
                    App.Database.ReplaceTimetable(slots);
                    classes = slots.Count;
                }
                else
                {
                    // 履修登録の前などで表が空のときは、前の時間割を消さずに残す
                    classes = App.Database.GetTimetable().Count;
                    error = "NU-Web の履修登録の画面に時間割が見つかりませんでした";
                }
            }
            catch (NuWebLoginException)
            {
                nuWebOk = false;
            }
            catch (Exception nuWebError) when (nuWebError is NuWebException or HttpRequestException or TaskCanceledException)
            {
                error = $"NU-Web から時間割を読み込めませんでした：{nuWebError.Message}";
            }
        }

        // ---- NU-Web の掲示（読めなくても、時間割や LACS の結果はそのまま） ----
        var newNotices = 0;
        if (nuWebOk)
        {
            try
            {
                // 本文は、まだ残していない掲示だけ開く（毎回すべてを開かない）
                var known = App.Database.AnnouncementIds(AnnouncementSource.NuWeb);
                var notices = await nuWeb.ReadNoticesAsync(DateTime.Now.AddDays(-AnnouncementDays), id => !known.Contains(id),
                    MaxNoticeBodies);
                var url = $"https://{NagasakiUniversity.NuWebHost}{NuWebClient.NoticeBoardPath}";
                // 本文を開かなかった掲示（残してあるもの）も、題名やジャンルは新しい値に合わせる
                newNotices = App.Database.SaveAnnouncements(AnnouncementSource.NuWeb, notices
                    .Select(n => new AnnouncementInput(AnnouncementSource.NuWeb, n.Id, n.Sender, n.Genre, n.Title, n.Body,
                        n.Posted, url)));
            }
            catch (NuWebLoginException)
            {
                NagasakiLog.Write("NU-Web: 掲示を読む途中でログインが切れました");
            }
            catch (Exception noticeError) when (noticeError is NuWebException or HttpRequestException or TaskCanceledException)
            {
                NagasakiLog.Write($"NU-Web: 掲示を読み込めませんでした（{noticeError.Message}）");
            }
        }

        // ---- LACS ----
        int assignments = 0, added = 0;
        string? lacsNote = null;
        using var lacs = new LacsClient(jar, UserAgent);
        var lacsOk = await lacs.IsSignedInAsync();
        if (!lacsOk)
        {
            // LACS-MCP と同じく、LACS の最初のページの「長大ID でログイン」のリンクをたどり、users/me が読めるまで待つ
            var page = await PageAsync();
            lacsNote = await SignInToLacsAsync(page);
            var deadline = DateTime.Now.AddSeconds(20);
            while (!lacsOk && DateTime.Now < deadline)
            {
                await NagasakiSession.MergeFromBrowserAsync(page.Core, jar);
                lacsOk = await lacs.IsSignedInAsync();
                if (!lacsOk)
                    await Task.Delay(2000);
            }
            NagasakiLog.Write($"LACS: 長大ID で入り直し → {(lacsOk ? "ログインできました" : "ログインが必要")}（{lacsNote}）");
        }
        if (lacsOk)
        {
            lacsNote = null;
            try
            {
                var now = DateTimeOffset.Now;
                var result = await lacs.ReadAssignmentsAsync(now.AddDays(-7), now.AddDays(120));
                assignments = result.Assignments.Count;
                added = NagasakiUniversity.ImportAssignments(result.Assignments).Added;
                NagasakiLog.Write($"LACS: 授業 {result.Courses} 件、課題 {assignments} 件、読めなかった授業 {result.Failed} 件");
                // 授業が 1 つもなかった、またはどの授業も読めなかったときは、知らせておく
                if (result.Courses == 0)
                    error ??= "LACS に登録している授業が見つかりませんでした";
                else if (result.Failed == result.Courses)
                    error ??= $"LACS の授業 {result.Courses} 件の成績表をどれも読めませんでした";
            }
            catch (LacsLoginException login)
            {
                lacsOk = false;
                lacsNote = login.Message;
            }
            catch (Exception lacsError) when (lacsError is LacsApiException or HttpRequestException
                                                  or TaskCanceledException or JsonException)
            {
                // ログイン切れ以外は LACS の側か通信の不具合。次に直すときの手がかりに理由を残す
                error ??= $"LACS から課題を読み込めませんでした：{lacsError.Message}";
            }
        }

        // ---- LACS の連絡事項（読めなくても、課題や時間割の結果はそのまま） ----
        var newAnnouncements = 0;
        if (lacsOk)
        {
            try
            {
                var result = await lacs.ReadAnnouncementsAsync(DateTimeOffset.Now.AddDays(-AnnouncementDays));
                newAnnouncements = App.Database.SaveAnnouncements(AnnouncementSource.Lacs, result.Announcements.Select(a =>
                    new AnnouncementInput(AnnouncementSource.Lacs, a.Id, a.CourseId, a.CourseName, a.Title, a.Body, a.Created,
                        $"https://{NagasakiUniversity.LacsHost}/ultra/courses/{a.CourseId}/announcements")));
                NagasakiLog.Write($"LACS: 連絡事項 {result.Announcements.Count} 件（新しく {newAnnouncements} 件）、"
                                  + $"読めなかった授業 {result.Failed} 件");
            }
            catch (Exception announcementError) when (announcementError is LacsLoginException or LacsApiException
                                                          or HttpRequestException or TaskCanceledException or JsonException)
            {
                NagasakiLog.Write($"LACS: 連絡事項を読み込めませんでした（{announcementError.Message}）");
            }
        }

        // 新しくもらった Cookie も含めて残す。見えないブラウザは次の更新まで大学のページを開いたままにしない
        NagasakiSession.Save(jar);
        _page?.Clear();
        return new NagasakiReport(!nuWebOk, !lacsOk, classes, assignments, added, error, lacsNote,
            newAnnouncements + newNotices);
    }

    /// <summary>
    /// 各コマに教室を入れる。教室はシラバスにしか書いていないので、まだ知らない時間割コードと、
    /// 読んでから 1 週間たったものだけシラバスを読みに行く（読めなかったものは次の更新でもう一度）。
    /// 教室が読めなくても時間割はそのまま取り込む。
    /// </summary>
    private static async Task<IReadOnlyList<ClassSlot>> AddRoomsAsync(NuWebClient nuWeb, IReadOnlyList<ClassSlot> slots)
    {
        var db = App.Database;
        var (rooms, fresh) = db.GetRooms(DateTime.Now.AddDays(-7));
        var stale = slots.SelectMany(s => s.Codes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Distinct().Where(c => !fresh.Contains(c)).ToList();
        if (stale.Count > 0)
        {
            foreach (var (code, room) in await nuWeb.ReadRoomsAsync(stale))
            {
                db.SaveRoom(code, room);
                rooms[code] = room;
            }
        }
        return NagasakiUniversity.WithRooms(slots, rooms);
    }

    // ---- LACS のログインの入口（ログインの窓・LACS の画面からも使う） ----------------
    /// <summary>LACS の最初のページにある「長大ID でログイン」のリンク（ページによって変わるので、決め打ちせずに読む）。</summary>
    public const string LacsSignInLinkScript =
        "(() => { const a = document.querySelector('a[href*=\"/auth-saml/saml/login\"]'); return a ? a.href : ''; })()";

    /// <summary>
    /// LACS の最初のページを開き、「長大ID でログイン」のリンクをたどる。
    /// 長大ID のログインが残っていれば、そのまま LACS に戻ってくる。止まった所を、うまくいかなかったときの手がかりとして返す。
    /// </summary>
    private static async Task<string> SignInToLacsAsync(HiddenPage page)
    {
        await page.NavigateAsync(NagasakiUniversity.LacsHome);
        var link = await LacsSignInLinkAsync(page.Core);
        var uri = await page.NavigateAsync(link ?? NagasakiUniversity.LacsSignIn);
        var stopped = NagasakiLog.Where(uri);
        return link is null
            ? $"LACS のページにログインのリンクが見つかりませんでした。止まった所：{stopped}"
            : $"止まった所：{stopped}";
    }

    /// <summary>表示中のページから「長大ID でログイン」のリンクを読む。無ければ null。</summary>
    public static async Task<string?> LacsSignInLinkAsync(CoreWebView2 core)
    {
        var json = await core.ExecuteScriptAsync(LacsSignInLinkScript);
        var link = JsonSerializer.Deserialize<string>(json);
        return string.IsNullOrEmpty(link) ? null : link;
    }

    /// <summary>このブラウザの Cookie で、LACS の自分の情報が読めるか。</summary>
    public static async Task<bool> LacsSignedInAsync(CoreWebView2 core)
    {
        var jar = new CookieContainer();
        await NagasakiSession.MergeFromBrowserAsync(core, jar);
        using var client = new LacsClient(jar, UserAgent);
        return await client.IsSignedInAsync();
    }

    /// <summary>このブラウザの Cookie で、NU-Web にログインできているか。</summary>
    public static async Task<bool> NuWebSignedInAsync(CoreWebView2 core)
    {
        var jar = new CookieContainer();
        await NagasakiSession.MergeFromBrowserAsync(core, jar);
        using var client = new NuWebClient(jar, UserAgent);
        return await client.IsSignedInAsync();
    }

    /// <summary>
    /// 見えないブラウザ。画面に出さない窓の上に WebView2 を置き、長大ID のログインの入口を開いて、
    /// 読み込みが落ち着くのを待つ（ログインの Cookie をもらうためだけに使う）。
    /// </summary>
    private sealed class HiddenPage
    {
        private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(45);
        // ログインの途中は、ページが自動で次のページへ移ることがある。この間なにも起きなければ落ち着いたとみなす
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

        // 窓とブラウザは、アプリを閉じるまで持っておく（手放すと消されてしまう）
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        private int _inFlight;
        private DateTime _lastEvent = DateTime.Now;

        private HiddenPage(HwndSource host, CoreWebView2Controller controller)
        {
            _host = host;
            _controller = controller;
            Core = controller.CoreWebView2;
            Core.NavigationStarting += (_, _) => { _inFlight++; _lastEvent = DateTime.Now; };
            Core.NavigationCompleted += (_, _) => { _inFlight = Math.Max(0, _inFlight - 1); _lastEvent = DateTime.Now; };
            // 新しい窓は開かせない
            Core.NewWindowRequested += (_, e) => e.Handled = true;
        }

        public CoreWebView2 Core { get; }

        public static async Task<HiddenPage> CreateAsync(CoreWebView2Environment environment)
        {
            // 画面に出さない窓（WS_VISIBLE を付けない）
            var host = new HwndSource(new HwndSourceParameters("Seigetsu-Nagasaki") { Width = 1280, Height = 900, WindowStyle = 0 });
            var controller = await environment.CreateCoreWebView2ControllerAsync(host.Handle);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 900);
            controller.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            controller.CoreWebView2.IsMuted = true;
            return new HiddenPage(host, controller);
        }

        /// <summary>ページを開き、読み込みが落ち着いたところの URL を返す（時間切れのときもその時点の URL）。</summary>
        public async Task<Uri?> NavigateAsync(string url)
        {
            _lastEvent = DateTime.Now;
            Core.Navigate(url);
            var deadline = DateTime.Now + NavigateTimeout;
            while (DateTime.Now < deadline && (_inFlight > 0 || DateTime.Now - _lastEvent < Quiet))
                await Task.Delay(250);
            return Uri.TryCreate(Core.Source, UriKind.Absolute, out var uri) ? uri : null;
        }

        public void Clear() => Core.Navigate("about:blank");
    }
}
