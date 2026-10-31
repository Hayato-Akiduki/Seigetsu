using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>
/// 連絡の本文を、文字を選んでコピーでき、中の URL を押せる形で出すための部品。
/// 読むだけの RichTextBox（IsReadOnly・IsDocumentEnabled）に、XAML から LinkText.Text="{Binding Body}" と付けて使う。
/// LACS のリンクは霽月の LACS のページで、ほかはふだんのブラウザで開く（http と https だけ）。
/// </summary>
public static class LinkText
{
    // 本文の中の URL。全角のかっこや句読点、空白の手前で終わりにする
    private static readonly Regex UrlPattern =
        new(@"https?://[^\s<>""'（）「」『』【】、。，．　]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkText), new PropertyMetadata("", OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box)
            box.Document = Build((string?)e.NewValue ?? "", box);
    }

    /// <summary>
    /// 本文を FlowDocument にする。文字の大きさ・色・書体は box に合わせる。
    /// URL の終わりの「.」「,」「)」などは、文の区切りとみなしてリンクに含めない。
    /// </summary>
    public static FlowDocument Build(string text, RichTextBox box)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            var line = lines[i];
            var at = 0;
            foreach (Match m in UrlPattern.Matches(line))
            {
                var url = m.Value.TrimEnd('.', ',', ')', ']', '>', ';', ':', '!', '?');
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;
                if (m.Index > at)
                    paragraph.Inlines.Add(new Run(line[at..m.Index]));
                var link = new Hyperlink(new Run(url))
                {
                    NavigateUri = uri,
                    Foreground = ThemeService.Brush("AccentBrush"),
                    Cursor = Cursors.Hand,
                    ToolTip = IsLacs(uri) ? "霽月の LACS のページで開きます" : "ブラウザで開きます",
                };
                link.Click += (_, _) => Open(uri, box);
                paragraph.Inlines.Add(link);
                at = m.Index + url.Length;
            }
            if (at < line.Length)
                paragraph.Inlines.Add(new Run(line[at..]));
        }
        return new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            Foreground = box.Foreground,
        };
    }

    private static bool IsLacs(Uri uri) => uri.Host.Equals(NagasakiUniversity.LacsHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>リンクを開く。LACS は霽月の LACS のページで、ほかはふだんのブラウザで（http / https だけ）。</summary>
    private static void Open(Uri uri, DependencyObject from)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return;
        if (IsLacs(uri) && (Window.GetWindow(from) ?? Application.Current.MainWindow) is MainWindow main)
            main.ShowLacsUrl(uri.AbsoluteUri);
        else
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
    }
}
