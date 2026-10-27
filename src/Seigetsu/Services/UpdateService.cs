using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace Seigetsu.Services;

/// <summary>
/// 自動更新。GitHub Releases に置いた新しい版を確認し、裏でダウンロードしておく。
/// 入れ替えは、アプリを閉じたとき（または設定画面の「今すぐ更新して再起動」）に行う。
/// インストールしたものだけが対象で、開発中に dotnet run で起動したときは何もしない。
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// 新しい版を置く GitHub のリポジトリ。Seigetsu.csproj の UpdateRepositoryUrl に書いた値が入る。
    /// </summary>
    public static readonly string RepositoryUrl =
        typeof(UpdateService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepositoryUrl")?.Value?.Trim() ?? "";

    // 起動してすぐは画面の準備を優先し、少し待ってから確認する。その後は 4 時間ごと
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly UpdateManager? _manager;
    private readonly DispatcherTimer _timer = new();
    private Dispatcher? _dispatcher;
    private UpdateInfo? _ready;
    private bool _busy;

    public UpdateService()
    {
        if (RepositoryUrl == "")
            return;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false, null), null, null);
        }
        catch (Exception)
        {
            _manager = null; // インストール情報が読めないときなど。更新はしないがアプリは動かす
        }
    }

    /// <summary>状態が変わったとき（設定画面の表示を合わせるため）。</summary>
    public event Action? Changed;

    /// <summary>新しい版のダウンロードが終わったとき。引数は版の番号。</summary>
    public event Action<string>? UpdateReady;

    public bool CanUpdate => _manager is { IsInstalled: true };

    public string CurrentVersion =>
        _manager is { IsInstalled: true } ? _manager.CurrentVersion?.ToString() ?? "" : "";

    /// <summary>ダウンロード済みで、入れ替えを待っている版（無ければ null）。</summary>
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public string Status { get; private set; } = "";

    public void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        if (!CanUpdate)
        {
            Status = RepositoryUrl == ""
                ? "更新の確認先（GitHub のリポジトリ）がまだ設定されていません。"
                : "自動更新は、インストールした霽月だけで使えます（いまは開発用の起動です）。";
            return;
        }
        Status = "新しい版はまだ確認していません。";
        _timer.Interval = FirstCheckDelay;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync(userAsked: false);
        };
        _timer.Start();
    }

    /// <summary>新しい版を確認し、あれば裏でダウンロードする。</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (!CanUpdate || _busy || _ready is not null)
            return;
        _busy = true;
        SetStatus("新しい版を確認しています…");
        try
        {
            var update = await _manager!.CheckForUpdatesAsync();
            if (update is null)
            {
                SetStatus($"最新の版です（{DateTime.Now:M/d HH:mm} に確認）。");
                return;
            }
            var version = update.TargetFullRelease.Version.ToString();
            SetStatus($"新しい版 {version} をダウンロードしています…");
            await _manager.DownloadUpdatesAsync(update, progress => SetStatus($"新しい版 {version} をダウンロードしています…（{progress}%）"), default);
            _ready = update;
            SetStatus($"新しい版 {version} の準備ができました。アプリを閉じると更新されます。");
            UpdateReady?.Invoke(version);
        }
        catch (Exception error)
        {
            // ネットにつながっていないときなど。いまの版はそのまま使える
            SetStatus(userAsked
                ? $"新しい版を確認できませんでした。時間をおいて試してください。（{error.Message}）"
                : "新しい版を確認できませんでした。次の確認を待っています。");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>ダウンロード済みの版にいますぐ入れ替えて、霽月を起動し直す。</summary>
    public void ApplyAndRestart()
    {
        if (_ready is not null)
            _manager!.ApplyUpdatesAndRestart(_ready.TargetFullRelease, null);
    }

    /// <summary>アプリを閉じたあとで、ダウンロード済みの版に入れ替える（起動はし直さない）。</summary>
    public void ApplyOnExit()
    {
        if (_ready is not null)
            _manager!.WaitExitThenApplyUpdates(_ready.TargetFullRelease, true, false, null);
    }

    // ダウンロードの進み具合は別のスレッドから届くので、画面のスレッドに渡してから知らせる
    private void SetStatus(string status)
    {
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetStatus(status));
            return;
        }
        Status = status;
        Changed?.Invoke();
    }
}
