using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>1 回の自動更新の結果。</summary>
public sealed record NagasakiReport(
    bool NuWebNeedsLogin, bool LacsNeedsLogin, int Classes, int Assignments, int Added, string? Error)
{
    public bool NeedsLogin => NuWebNeedsLogin || LacsNeedsLogin;
}

/// <summary>
/// 長崎大学モードの自動更新。起動の 30 秒後と、その後 3 時間ごとに、見えないブラウザ（WebView2）で
/// NU-Web の時間割と LACS の課題を読みに行く。ログインが切れていたら、読まずに「ログインが必要」とだけ伝える
/// （パスワードは扱わないので、ログインは NagasakiWindow で本人にしてもらう）。
/// ログインの窓とこの見えないブラウザは同じ保存場所（AppPaths.WebViewDir）を使うので、ログインの状態を共有する。
/// </summary>
public sealed class NagasakiSync
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(3);

    private readonly DispatcherTimer _timer = new() { Interval = FirstDelay };
    private Task<CoreWebView2Environment>? _environment;
    private HiddenPage? _page;
    private Task<NagasakiReport>? _running;

    /// <summary>自動更新が終わるたびに呼ばれる（userAsked は、ボタンから頼まれたときに true）。</summary>
    public event Action<NagasakiReport, bool>? Synced;

    /// <summary>状態が変わったとき（始まった・終わった）に呼ばれる。</summary>
    public event Action? Changed;

    public bool IsRunning => _running is { IsCompleted: false };

    /// <summary>設定画面に出す、前回の結果（アプリを閉じても残す）。</summary>
    public static string LastStatus => App.Database.GetSetting(SettingKeys.NagasakiStatus, "まだ更新していません");

    public void Start()
    {
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (NagasakiUniversity.Enabled)
                await SyncAsync(userAsked: false);
        };
        _timer.Start();
    }

    /// <summary>ログインの窓もこれを使う（同じ保存場所の WebView2 は、同じ作り方でないと一緒に開けないため）。</summary>
    public Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewDir);

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

        App.Database.SetSetting(SettingKeys.NagasakiStatus, Describe(report));
        if (!report.NeedsLogin && report.Error is null)
            App.Database.SetSetting(SettingKeys.NagasakiSyncedAt, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        _running = null;
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
            parts.Add($"LACS の課題 {report.Assignments} 件" + (report.Added > 0 ? $"（新しく {report.Added} 件）" : ""));
        var text = $"{at}　" + (parts.Count > 0 ? string.Join("・", parts) + " を読み込みました" : "");
        if (report.NeedsLogin)
        {
            var sites = report.NuWebNeedsLogin && report.LacsNeedsLogin ? "NU-Web と LACS"
                : report.NuWebNeedsLogin ? "NU-Web" : "LACS";
            text += (parts.Count > 0 ? "。" : "") + $"{sites} にログインが必要です";
        }
        return text;
    }

    private async Task<NagasakiReport> ReadAllAsync()
    {
        _page ??= await HiddenPage.CreateAsync(await EnvironmentAsync());
        var page = _page;

        // NU-Web: 一括ログインの入口から入り、ログインできていれば（右上の「ログアウト」がある）時間割の画面を読む
        int classes = 0;
        string? error = null;
        var uri = await page.NavigateAsync(NagasakiUniversity.NuWebSignIn);
        var nuWebOk = uri?.Host == NagasakiUniversity.NuWebHost
                      && await page.EvalBoolAsync("!!document.getElementById('logout')");
        if (nuWebOk)
        {
            await page.NavigateAsync(NagasakiUniversity.TimetableUrl);
            using var message = await page.RunAsync(NagasakiUniversity.TimetableScript, "timetable");
            var slots = NagasakiUniversity.ParseTimetable(message.RootElement.GetProperty("tables"));
            if (slots.Count > 0)
            {
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

        // LACS: 長大ID の入口から入り、LACS に戻ってこられたら課題を読む
        int assignments = 0, added = 0;
        var lacsOk = false;
        uri = await page.NavigateAsync(NagasakiUniversity.LacsSignIn);
        if (uri?.Host == NagasakiUniversity.LacsHost)
        {
            using var message = await page.RunAsync(NagasakiUniversity.LacsScript, "lacs");
            var root = message.RootElement;
            if (root.TryGetProperty("error", out var lacsError))
            {
                // "login" はログインが切れているだけ。それ以外は LACS の側の不具合なので、理由として残す
                lacsOk = lacsError.GetString() != "login";
                if (lacsOk)
                    error ??= "LACS から課題を読み込めませんでした"
                              + (root.TryGetProperty("status", out var status) ? $"（応答 {status}）" : "");
            }
            else
            {
                lacsOk = true;
                var list = NagasakiUniversity.ParseLacsItems(root.GetProperty("items"));
                assignments = list.Count;
                added = NagasakiUniversity.ImportAssignments(list).Added;
            }
        }

        // 次の自動更新まで大学のページを開いたままにしない
        page.Clear();
        return new NagasakiReport(!nuWebOk, !lacsOk, classes, assignments, added, error);
    }

    /// <summary>
    /// 見えないブラウザ。画面に出さない窓の上に WebView2 を置き、ページを開いて読み込みが落ち着くのを待ったり、
    /// ページの中でスクリプトを動かして chrome.webview.postMessage の結果を受け取ったりする。
    /// </summary>
    private sealed class HiddenPage
    {
        private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(60);
        // ログインの途中は、ページが自動で次のページへ移ることがある。この間なにも起きなければ落ち着いたとみなす
        private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

        // 窓とブラウザは、アプリを閉じるまで持っておく（手放すと消されてしまう）
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        private readonly CoreWebView2 _core;
        private int _inFlight;
        private DateTime _lastEvent = DateTime.Now;
        private (string Kind, TaskCompletionSource<string> Result)? _waiting;

        private HiddenPage(HwndSource host, CoreWebView2Controller controller)
        {
            _host = host;
            _controller = controller;
            var core = _core = controller.CoreWebView2;
            core.NavigationStarting += (_, _) => { _inFlight++; _lastEvent = DateTime.Now; };
            core.NavigationCompleted += (_, _) => { _inFlight = Math.Max(0, _inFlight - 1); _lastEvent = DateTime.Now; };
            core.WebMessageReceived += OnMessage;
            // 新しい窓は開かせない
            core.NewWindowRequested += (_, e) => e.Handled = true;
        }

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
            _core.Navigate(url);
            var deadline = DateTime.Now + NavigateTimeout;
            while (DateTime.Now < deadline && (_inFlight > 0 || DateTime.Now - _lastEvent < Quiet))
                await Task.Delay(250);
            return Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri) ? uri : null;
        }

        public async Task<bool> EvalBoolAsync(string script) => await _core.ExecuteScriptAsync(script) == "true";

        /// <summary>スクリプトを動かし、kind が同じ postMessage の中身を受け取る。</summary>
        public async Task<JsonDocument> RunAsync(string script, string kind)
        {
            var result = new TaskCompletionSource<string>();
            _waiting = (kind, result);
            await _core.ExecuteScriptAsync(script);
            var done = await Task.WhenAny(result.Task, Task.Delay(ScriptTimeout));
            _waiting = null;
            if (done != result.Task)
                throw new TimeoutException("大学のサイトからの返事がありませんでした");
            return JsonDocument.Parse(result.Task.Result);
        }

        public void Clear() => _core.Navigate("about:blank");

        private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // 大学のサイトから来たものだけを受け取る
            if (_waiting is not { } waiting || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
                || source.Host is not (NagasakiUniversity.LacsHost or NagasakiUniversity.NuWebHost))
                return;
            var text = e.TryGetWebMessageAsString();
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == waiting.Kind)
                    waiting.Result.TrySetResult(text);
            }
            catch (JsonException)
            {
                // 関係のないメッセージは無視する
            }
        }
    }
}
