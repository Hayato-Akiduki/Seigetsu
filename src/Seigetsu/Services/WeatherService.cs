using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Seigetsu.Data;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Seigetsu.Services;

/// <summary>いまの天気 1 回分。</summary>
public sealed record WeatherInfo(string Place, string Text, SymbolRegular Icon, double? Temperature, double? Wind);

public enum WeatherStatus { Loading, Ok, Error }

/// <summary>
/// 天気（v6 と同じく Open-Meteo の無料 API を使う。キーはいらない）。
/// 地域は設定の weather_city（v6 と同じ）。起動したときと、その後 30 分ごとに取り直す。
/// </summary>
public sealed class WeatherService
{
    public const string DefaultCity = "Nagasaki"; // v6 と同じ

    private static readonly HttpClient Http = CreateClient();

    // 天気コード → (説明, アイコン)。v6 と同じ分け方
    private static readonly (int[] Codes, string Text, SymbolRegular Icon)[] Codes =
    [
        ([0], "快晴", SymbolRegular.WeatherSunny24),
        ([1, 2], "晴れ時々くもり", SymbolRegular.WeatherPartlyCloudyDay24),
        ([3], "くもり", SymbolRegular.WeatherCloudy24),
        ([45, 48], "霧", SymbolRegular.WeatherFog24),
        ([51, 53, 55, 56, 57], "霧雨", SymbolRegular.WeatherDrizzle24),
        ([61, 63, 65, 66, 67], "雨", SymbolRegular.WeatherRain24),
        ([71, 73, 75, 77], "雪", SymbolRegular.WeatherSnow24),
        ([80, 81, 82], "にわか雨", SymbolRegular.WeatherRainShowersDay24),
        ([85, 86], "にわか雪", SymbolRegular.WeatherSnowShowerDay24),
        ([95, 96, 99], "雷雨", SymbolRegular.WeatherThunderstorm24),
    ];

    private readonly DispatcherTimer _auto = new() { Interval = TimeSpan.FromMinutes(30) };

    // インターネットが一時的に切れていただけのこともあるので、取れなかったときは 3 分後にもう一度
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromMinutes(3) };

    private Task? _running; // 取りに行っている最中なら、その処理
    private bool _again;    // 取りに行っている最中に、もう一度頼まれた（地域を変えたときなど）

    public WeatherService()
    {
        _auto.Tick += async (_, _) => await RefreshAsync();
        _retry.Tick += async (_, _) =>
        {
            _retry.Stop();
            await RefreshAsync();
        };
    }

    public WeatherStatus Status { get; private set; } = WeatherStatus.Loading;

    public WeatherInfo? Current { get; private set; }

    /// <summary>取れなかったときの理由（画面に出す）。</summary>
    public string Message { get; private set; } = "";

    /// <summary>状態が変わったとき（画面の表示を合わせる）。</summary>
    public event Action? Changed;

    public static string City => App.Database.GetSetting(SettingKeys.City, DefaultCity);

    public async void Start()
    {
        _auto.Start();
        await RefreshAsync();
    }

    /// <summary>
    /// 天気を取り直す。取りに行っている最中に呼ばれたら、それが終わってからもう一度取りに行く
    /// （途中で地域を変えても、新しい地域の天気になるように）。返す Task は、その全部が終わるまで待てる。
    /// </summary>
    public Task RefreshAsync()
    {
        if (_running is not null)
        {
            _again = true;
            return _running;
        }
        _running = RunAsync();
        return _running;
    }

    private async Task RunAsync()
    {
        try
        {
            do
            {
                _again = false;
                await FetchOnceAsync();
            } while (_again);
        }
        finally
        {
            _running = null;
        }
    }

    private async Task FetchOnceAsync()
    {
        Status = WeatherStatus.Loading;
        Changed?.Invoke();
        try
        {
            Current = await FetchAsync(City);
            Status = WeatherStatus.Ok;
            _retry.Stop();
        }
        catch (LookupException error)
        {
            // 地域の名前が見つからないときは、何度取り直しても同じなので、もう一度は取りに行かない
            Status = WeatherStatus.Error;
            Message = error.Message;
            _retry.Stop();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Status = WeatherStatus.Error;
            Message = "インターネットにつながっていないか、天気のサービスが応えませんでした。3 分後にもう一度確かめます。";
            _retry.Stop();
            _retry.Start();
        }
        Changed?.Invoke();
    }

    private static async Task<WeatherInfo> FetchAsync(string city)
    {
        var query = Uri.EscapeDataString(city);
        using var geo = await GetJsonAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=ja&format=json");
        if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            throw new LookupException($"「{city}」が見つかりませんでした。設定画面で地域を確かめてください。");
        var place = results[0];
        var latitude = place.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var longitude = place.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        var name = place.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;

        using var forecast = await GetJsonAsync(
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}" +
            "&current=temperature_2m,weather_code,wind_speed_10m&timezone=auto");
        var current = forecast.RootElement.GetProperty("current");
        var code = current.TryGetProperty("weather_code", out var c) ? c.GetInt32() : -1;
        var (text, icon) = Describe(code);
        return new WeatherInfo(name, text, icon, Number(current, "temperature_2m"), Number(current, "wind_speed_10m"));
    }

    private static (string Text, SymbolRegular Icon) Describe(int code)
    {
        foreach (var (codes, text, icon) in Codes)
        {
            if (codes.Contains(code))
                return (text, icon);
        }
        return ("不明", SymbolRegular.WeatherCloudy24);
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) }; // v6 と同じ 8 秒
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "7";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Seigetsu/{version}");
        return client;
    }

    private sealed class LookupException(string message) : Exception(message);
}
