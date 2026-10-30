using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// 長崎大学モードの記録（%APPDATA%\akidesk\nagasaki-log.txt）。うまくいかなかったとき、どの段階で止まったかを見るため。
/// Cookie の値やパスワード、授業名などの中身は書かない（URL の場所と応答の番号、件数だけ）。
/// </summary>
public static class NagasakiLog
{
    private const long MaxBytes = 200_000;

    public static string FilePath => Path.Combine(AppPaths.DataDir, "nagasaki-log.txt");

    public static void Write(string message)
    {
        try
        {
            // 大きくなったら古い分を捨てる
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても、取り込みはそのまま続ける
        }
    }

    /// <summary>URL を「場所」だけにする（? 以降の値は書かない）。</summary>
    public static string Where(Uri? uri) => uri is null ? "（不明）" : $"{uri.Host}{uri.AbsolutePath}";
}

/// <summary>
/// LACS と NU-Web のログインの Cookie を、アプリを閉じても残しておく（LACS-MCP が state.json に残すのと同じ考え）。
/// WebView2 はアプリを閉じるとログインの Cookie を捨ててしまうため、霽月が自分で持つ。
/// ファイル（nagasaki-session.bin）は Windows の DPAPI で暗号化し、この PC のこのユーザーにしか読めない。
/// 大学の 2 つのサイトの Cookie だけを入れる。
/// </summary>
public static class NagasakiSession
{
    private static readonly string[] Hosts = [NagasakiUniversity.LacsHost, NagasakiUniversity.NuWebHost];
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Seigetsu.NagasakiSession");

    private static string FilePath => Path.Combine(AppPaths.DataDir, "nagasaki-session.bin");

    private sealed record Saved(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, DateTime? Expires);

    /// <summary>
    /// CookieContainer が受け付けない Cookie を、jar ごとに別に持つ。
    /// LACS のログインの本体（BbRouter）は値にカンマを含むので、CookieContainer に入れると「不正な値」として断られる。
    /// これを捨てると LACS が毎回「ログインが必要」になるので、ここに取っておき、送るときに Cookie ヘッダーへ足す
    /// （CreateHandler）。サーバーが返した新しい値もここで受け取る。
    /// </summary>
    private static readonly ConditionalWeakTable<CookieContainer, List<Cookie>> Rejected = new();

    private static List<Cookie> RejectedOf(CookieContainer jar) => Rejected.GetValue(jar, _ => []);

    /// <summary>
    /// jar の Cookie で大学のサイトを読むための部品。
    /// Cookie の受け渡しは HttpClientHandler（CookieContainer）に任せず、ここで行う。CookieContainer は返ってきた
    /// Set-Cookie をカンマで切ってしまうため、LACS の BbRouter（値にカンマを含む）が「signature:…」「xsrf:…」などの
    /// 壊れた Cookie と、中身の切れた BbRouter に分かれ、2 回目の読み込みから LACS に「ログインが必要」と言われていた。
    /// 転送（allowRedirect）もここでたどり、転送ごとに Cookie を受け取って、次の行き先に合う Cookie だけを送る。
    /// </summary>
    public static HttpMessageHandler CreateHandler(CookieContainer jar, bool allowRedirect) =>
        new JarHandler(jar, allowRedirect)
        {
            InnerHandler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false },
        };

    private sealed class JarHandler(CookieContainer jar, bool allowRedirect) : DelegatingHandler
    {
        private const int MaxRedirects = 10;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            for (var hop = 0; ; hop++)
            {
                Attach(request);
                var response = await base.SendAsync(request, token);
                var uri = request.RequestUri!;
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var header in setCookies)
                        Take(jar, uri, header);
                }
                var status = (int)response.StatusCode;
                if (!allowRedirect || hop >= MaxRedirects || status is not (301 or 302 or 303 or 307 or 308)
                    || response.Headers.Location is not { } location
                    || (status is 307 or 308 && request.Method != HttpMethod.Get))
                    return response;

                // 転送先へ GET で進む（本文は持って行かない）。Cookie 以外のヘッダー（User-Agent など）は引き継ぐ
                var next = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, location));
                foreach (var h in request.Headers.Where(h => !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
                    next.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Dispose();
                request = next;
            }
        }

        /// <summary>行き先に合う Cookie を Cookie ヘッダーにする（jar のものと、jar に入らなかったもの）。</summary>
        private void Attach(HttpRequestMessage request)
        {
            request.Headers.Remove("Cookie");
            if (request.RequestUri is not { } uri)
                return;
            var parts = new List<string>();
            var fromJar = jar.GetCookieHeader(uri);
            if (fromJar != "")
                parts.Add(fromJar);
            var rejected = RejectedOf(jar);
            lock (rejected)
                parts.AddRange(rejected.Where(c => Matches(c, uri)).Select(c => $"{c.Name}={c.Value}"));
            if (parts.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", parts));
        }
    }

    private static bool Matches(Cookie cookie, Uri uri)
    {
        if (cookie.Expired || (cookie.Expires != DateTime.MinValue && cookie.Expires.ToUniversalTime() < DateTime.UtcNow))
            return false;
        if (cookie.Secure && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var domain = cookie.Domain.TrimStart('.');
        var host = uri.Host;
        var path = cookie.Path == "" ? "/" : cookie.Path;
        return (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
               && uri.AbsolutePath.StartsWith(path, StringComparison.Ordinal);
    }

    /// <summary>
    /// 返ってきた Set-Cookie 1 つを受け取る。「;」で区切るだけで、値の中のカンマでは切らない。
    /// jar に入る形なら jar へ、入らない形（値にカンマなど）なら別に取っておく方へ入れる。期限切れなら消す。
    /// </summary>
    private static void Take(CookieContainer jar, Uri uri, string header)
    {
        var parts = header.Split(';');
        var pair = parts[0].Split('=', 2);
        if (pair.Length != 2 || !IsName(pair[0].Trim()))
            return;
        // Path が無いときは、読んだアドレスの「フォルダ」まで（RFC 6265 の決まり）
        var lastSlash = uri.AbsolutePath.LastIndexOf('/');
        var defaultPath = lastSlash <= 0 ? "/" : uri.AbsolutePath[..lastSlash];
        var cookie = new Cookie(pair[0].Trim(), pair[1].Trim(), defaultPath, uri.Host);
        var hasMaxAge = false;
        foreach (var part in parts.Skip(1))
        {
            var attribute = part.Split('=', 2);
            var key = attribute[0].Trim().ToLowerInvariant();
            var value = attribute.Length == 2 ? attribute[1].Trim() : "";
            switch (key)
            {
                case "domain" when value != "":
                    cookie.Domain = value.StartsWith('.') ? value : "." + value;
                    break;
                case "path" when value.StartsWith('/'):
                    cookie.Path = value;
                    break;
                case "secure":
                    cookie.Secure = true;
                    break;
                case "httponly":
                    cookie.HttpOnly = true;
                    break;
                case "max-age" when int.TryParse(value, out var seconds):
                    hasMaxAge = true;
                    cookie.Expires = seconds <= 0 ? DateTime.UtcNow.AddYears(-1) : DateTime.UtcNow.AddSeconds(seconds);
                    break;
                case "expires" when !hasMaxAge && DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var expires):
                    cookie.Expires = expires;
                    break;
            }
        }
        if (!IsUniversity(cookie.Domain))
            return;

        // 同じ名前・場所の古いものは、jar からも、別に取っておく方からも消してから入れる
        foreach (Cookie old in jar.GetAllCookies())
        {
            if (old.Name == cookie.Name && old.Path == cookie.Path && SameSite(old.Domain, cookie.Domain))
                old.Expired = true;
        }
        var gone = cookie.Expires != DateTime.MinValue && cookie.Expires.ToUniversalTime() < DateTime.UtcNow;
        if (gone || !FitsJar(cookie))
        {
            Remember(jar, cookie); // 期限切れなら、取っておいた方からも消すだけ
            return;
        }
        Remember(jar, new Cookie(cookie.Name, "", cookie.Path, cookie.Domain)); // 取っておいた方の同じものを消す
        try
        {
            jar.Add(cookie);
        }
        catch (CookieException)
        {
            Remember(jar, cookie);
        }
    }

    /// <summary>Cookie の名前として正しいか（RFC 6265 の token。「:」「,」などの区切り文字を含まない）。</summary>
    private static bool IsName(string name) =>
        name != "" && name.All(c => c > ' ' && c < 127 && !"()<>@,;:\\\"/[]?={}".Contains(c));

    /// <summary>CookieContainer がこの Cookie を受け付けるか。</summary>
    private static bool FitsJar(Cookie cookie)
    {
        try
        {
            new CookieContainer().Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
            return true;
        }
        catch (CookieException)
        {
            return false;
        }
    }

    /// <summary>jar に入らない Cookie を取っておく（同じ名前・場所のものは置き換える。期限切れや空の値なら消す）。</summary>
    private static void Remember(CookieContainer jar, Cookie cookie)
    {
        var list = RejectedOf(jar);
        lock (list)
        {
            list.RemoveAll(c => c.Name == cookie.Name && c.Path == cookie.Path
                                && c.Domain.TrimStart('.').Equals(cookie.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
            var gone = cookie.Value == ""
                       || (cookie.Expires != DateTime.MinValue && cookie.Expires.ToUniversalTime() < DateTime.UtcNow);
            if (!gone)
                list.Add(cookie);
        }
    }

    /// <summary>残しておいた Cookie を読む（無い・読めないときは空）。</summary>
    public static CookieContainer Load()
    {
        var jar = new CookieContainer();
        try
        {
            if (!File.Exists(FilePath))
                return jar;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            foreach (var c in JsonSerializer.Deserialize<List<Saved>>(json) ?? [])
            {
                if (c.Expires is { } expires && expires < DateTime.UtcNow)
                    continue;
                // 以前の版が BbRouter をカンマで切って作ってしまった、名前に「:」などを含む壊れた Cookie は捨てる
                if (!IsName(c.Name))
                    continue;
                Add(jar, new Cookie(c.Name, c.Value, c.Path, c.Domain)
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                    Expires = c.Expires ?? DateTime.MinValue,
                });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
                                          or JsonException)
        {
            NagasakiLog.Write($"残しておいたログインを読めませんでした（{error.GetType().Name}）");
        }
        return jar;
    }

    /// <summary>今の Cookie を残す。</summary>
    public static void Save(CookieContainer jar)
    {
        try
        {
            List<Cookie> rejected;
            lock (RejectedOf(jar))
                rejected = [.. RejectedOf(jar)];
            var list = jar.GetAllCookies().Cast<Cookie>().Concat(rejected)
                .Where(c => IsUniversity(c.Domain))
                .Select(c => new Saved(c.Name, c.Value, c.Domain, c.Path, c.Secure, c.HttpOnly,
                    c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime()))
                .ToList();
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(list), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, bytes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            NagasakiLog.Write($"ログインを残せませんでした（{error.GetType().Name}）");
        }
    }

    /// <summary>残しておいたログインを消す（長崎大学モードをオフにしたときなど）。</summary>
    public static void Forget()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// jar のログイン（Cookie）を、ブラウザ（WebView2）にも入れる。WebView2 はアプリを閉じるとログインを捨てるので、
    /// 霽月の中で LACS のページを開くとき、自動更新が残しているログインを渡して、ログインし直さずに済むようにする。
    /// </summary>
    public static void CopyToBrowser(CoreWebView2 core, CookieContainer jar)
    {
        List<Cookie> rejected;
        lock (RejectedOf(jar))
            rejected = [.. RejectedOf(jar)];
        foreach (var c in jar.GetAllCookies().Cast<Cookie>().Concat(rejected))
        {
            if (c.Expired || !IsUniversity(c.Domain)
                || (c.Expires != DateTime.MinValue && c.Expires.ToUniversalTime() < DateTime.UtcNow))
                continue;
            try
            {
                var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path == "" ? "/" : c.Path);
                cookie.IsSecure = c.Secure;
                cookie.IsHttpOnly = c.HttpOnly;
                if (c.Expires != DateTime.MinValue)
                    cookie.Expires = c.Expires.ToUniversalTime();
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
            catch (ArgumentException)
            {
                // ブラウザが受け付けない形の Cookie は渡さない
            }
        }
    }

    /// <summary>ブラウザ（WebView2）が持っている大学の Cookie を、jar に上書きで足す。</summary>
    public static async Task MergeFromBrowserAsync(CoreWebView2 core, CookieContainer jar)
    {
        // アドレスを指定すると、そのパスに送る Cookie しか返らない。NU-Web のログインの JSESSIONID はパスが /campusweb なので、
        // 「https://nuweb.nagasaki-u.ac.jp」で聞くと取りこぼし、NU-Web がログインし直し（ssologin.do）に回してしまう。
        // そこで全部の Cookie をもらい、大学の 2 つのサイトのものだけ入れる（Add で絞る）
        var fresh = (await core.CookieManager.GetCookiesAsync(""))
            .Select(c => c.ToSystemNetCookie()).Where(c => IsUniversity(c.Domain)).ToList();

        // 同じサイト・同じ名前の古い Cookie は消してから入れる。パスやドメインの書き方が違うと CookieContainer は
        // 別の Cookie として両方残し、古い方（パスが長い方）を先に送る。LACS は先頭の JSESSIONID を使うので、
        // ログインし直したのに「ログインが必要」と言われてしまう
        bool Replaced(Cookie old) => fresh.Any(f => f.Name == old.Name && SameSite(f.Domain, old.Domain));
        foreach (Cookie old in jar.GetAllCookies())
        {
            if (Replaced(old))
                old.Expired = true;
        }
        var rejected = RejectedOf(jar);
        lock (rejected)
            rejected.RemoveAll(Replaced);

        foreach (var cookie in fresh)
            Add(jar, cookie);
    }

    private static bool SameSite(string a, string b) =>
        a.TrimStart('.').Equals(b.TrimStart('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUniversity(string domain) =>
        Hosts.Any(h => domain.TrimStart('.').Equals(h, StringComparison.OrdinalIgnoreCase));

    private static void Add(CookieContainer jar, Cookie cookie)
    {
        if (!IsUniversity(cookie.Domain))
            return;
        try
        {
            jar.Add(cookie);
        }
        catch (CookieException)
        {
            // 値にカンマを含むなど、CookieContainer が受け付けない形。捨てずに別に取っておいて、送るときに足す
            Remember(jar, cookie);
        }
    }
}
