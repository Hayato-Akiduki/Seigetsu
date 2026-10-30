using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>
/// 長崎大学モードのログインの窓。LACS-MCP のログインと同じ順に進む。
/// 1. LACS の最初のページを開き、「長大ID でログイン」のリンクをたどる（本人が長大ID でログインする）
/// 2. 2 秒ごとに、この窓の Cookie で LACS の自分の情報（users/me）が読めるか確かめる
/// 3. 読めたら NU-Web の長大ID の入口へ。長大ID のログインが残っているので、ふつうはそのまま通る。同じく 2 秒ごとに確かめる
/// 4. 両方そろったら Cookie を NagasakiSync に渡して残し、取り込みを動かして閉じる
/// パスワードはこの窓のページ（大学のサイト）に本人が打つだけで、霽月は読まない。
/// </summary>
public partial class NagasakiWindow : FluentWindow
{
    private enum Step { Lacs, NuWeb, Syncing, Done }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _check = new() { Interval = CheckInterval };
    private Step _step = Step.Lacs;
    private bool _checking;
    // LACS の「長大ID でログイン」のリンクを、もうたどったか（ログイン画面に戻されたときに何度もたどらないため）
    private bool _lacsLinkFollowed;

    public NagasakiWindow()
    {
        InitializeComponent();
        StatusText.Text = "長大ID でログインしてください。ログインが済むと、時間割と LACS の課題を自動で取り込みます。";
        _check.Tick += async (_, _) => await CheckAsync();
        Closed += (_, _) => _check.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 見えないブラウザ（自動更新）と同じ作り方にする
            await Browser.EnsureCoreWebView2Async(await App.Nagasaki.EnvironmentAsync());
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException
                                          or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = "ブラウザの部品（Microsoft Edge WebView2 ランタイム）が見つからないため開けませんでした。"
                              + "Microsoft のサイトから「WebView2 ランタイム」を入れてから、もう一度お試しください。";
            return;
        }
        var core = Browser.CoreWebView2;
        core.NavigationCompleted += OnNavigationCompleted;
        // 新しい窓で開くリンクも、この窓の中で開く
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            core.Navigate(args.Uri);
        };
        NagasakiLog.Write("ログインの窓：LACS の最初のページを開きます");
        core.Navigate(NagasakiUniversity.LacsHome);
        _check.Start();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is { CanGoBack: true } core)
            core.GoBack();
    }

    // LACS の最初のページに着いたら、「長大ID でログイン」のリンクを、押す代わりにたどる
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var core = Browser.CoreWebView2;
        if (_step != Step.Lacs || _lacsLinkFollowed || !e.IsSuccess
            || !Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || uri.Host != NagasakiUniversity.LacsHost)
            return;
        if (await NagasakiSync.LacsSignInLinkAsync(core) is { } link)
        {
            _lacsLinkFollowed = true;
            NagasakiLog.Write($"ログインの窓：長大ID でログインのリンクをたどります（{NagasakiLog.Where(uri)}）");
            core.Navigate(link);
        }
    }

    /// <summary>2 秒ごとに、いまの段階のログインができたか確かめる。</summary>
    private async Task CheckAsync()
    {
        if (_checking || Browser.CoreWebView2 is not { } core)
            return;
        _checking = true;
        try
        {
            if (_step == Step.Lacs && await NagasakiSync.LacsSignedInAsync(core))
            {
                NagasakiLog.Write("ログインの窓：LACS にログインできました。NU-Web に進みます");
                _step = Step.NuWeb;
                StatusText.Text = "LACS にログインできました。続けて NU-Web に入っています…";
                core.Navigate(NagasakiUniversity.NuWebSignIn);
            }
            else if (_step == Step.NuWeb && await NagasakiSync.NuWebSignedInAsync(core))
            {
                NagasakiLog.Write("ログインの窓：NU-Web にもログインできました");
                await SyncAsync(core);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task SyncAsync(CoreWebView2 core)
    {
        _step = Step.Syncing;
        _check.Stop();
        StatusText.Text = "ログインできました。時間割と LACS の課題を取り込んでいます…（初めてのときは教室も読むので 1〜2 分かかります）";
        await App.Nagasaki.AdoptBrowserLoginAsync(core);
        var report = await App.Nagasaki.SyncAsync(userAsked: true);
        if (!IsLoaded)
            return;

        _step = Step.Done;
        var ok = report.Error is null && !report.NeedsLogin;
        StatusText.Text = NagasakiSync.Describe(report) + (ok ? "。この窓は自動で閉じます。" : "");
        if (ok)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Close();
        }
    }
}
