using System.Windows;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>
/// 長崎大学モードのログインの窓。長大ID でログインしてもらい、済んだら自動更新（NagasakiSync）を動かして閉じる。
/// まず NU-Web から入り、LACS だけログインが残っていなければ、続けて LACS のログインに進む。
/// パスワードはこの窓のページ（大学のサイト）に本人が打つだけで、霽月は読まない。
/// </summary>
public partial class NagasakiWindow : FluentWindow
{
    private enum Step { NuWeb, Lacs, Syncing, Done }

    private Step _step = Step.NuWeb;

    public NagasakiWindow()
    {
        InitializeComponent();
        StatusText.Text = "長大ID でログインしてください。ログインが済むと、時間割と LACS の課題を自動で取り込みます。";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 見えないブラウザ（自動更新）と同じ作り方にして、ログインの状態を共有する
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
        core.Navigate(NagasakiUniversity.NuWebSignIn);
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is { CanGoBack: true } core)
            core.GoBack();
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var uri))
            return;
        var signedIn = _step switch
        {
            // NU-Web のホームに「ログアウト」が出たら、ログインできている
            Step.NuWeb => uri.Host == NagasakiUniversity.NuWebHost
                          && await Browser.CoreWebView2.ExecuteScriptAsync("!!document.getElementById('logout')") == "true",
            // LACS の画面（/ultra/...）に戻ってきたら、ログインできている
            Step.Lacs => uri.Host == NagasakiUniversity.LacsHost
                         && uri.AbsolutePath.StartsWith("/ultra", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (signedIn)
            await SyncAsync();
    }

    private async Task SyncAsync()
    {
        var wasLacs = _step == Step.Lacs;
        _step = Step.Syncing;
        StatusText.Text = "ログインできました。時間割と LACS の課題を取り込んでいます…";
        var report = await App.Nagasaki.SyncAsync(userAsked: true);
        if (!IsLoaded)
            return;

        if (report.LacsNeedsLogin && !wasLacs)
        {
            // NU-Web だけ入れた。LACS のログインへ進む
            _step = Step.Lacs;
            StatusText.Text = NagasakiSync.Describe(report) + "。続けて LACS にログインしてください。";
            Browser.CoreWebView2.Navigate(NagasakiUniversity.LacsSignIn);
            return;
        }

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
