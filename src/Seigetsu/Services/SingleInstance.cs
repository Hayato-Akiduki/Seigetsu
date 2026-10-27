using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace Seigetsu.Services;

/// <summary>
/// 霽月を 1 つだけ動かす。すでに動いているときに起動したら、動いている方に「窓を出して」と知らせて終わる。
/// （タスクトレイに隠れているときにもう一度起動した場合や、同じデータに 2 つが同時に書き込むのを防ぐため）
/// 保存先ごとに区別するので、テスト用の保存先で起動したものとはぶつからない。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private readonly bool _isFirst;
    private RegisteredWaitHandle? _wait;

    public SingleInstance(string dataDir)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToLowerInvariant())))[..16];
        _mutex = new Mutex(initiallyOwned: true, $@"Local\Seigetsu.{key}", out _isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Seigetsu.{key}.Show");
    }

    public bool IsFirst => _isFirst;

    /// <summary>動いている方の霽月に、窓を出すよう知らせる。</summary>
    public void SignalFirstInstance() => _showSignal.Set();

    /// <summary>あとから起動されたときに onShow を画面の側で呼ぶ。</summary>
    public void ListenForShow(Dispatcher dispatcher, Action onShow) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => dispatcher.BeginInvoke(onShow), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (_isFirst)
            _mutex.ReleaseMutex();
        _mutex.Dispose();
        _showSignal.Dispose();
    }
}
