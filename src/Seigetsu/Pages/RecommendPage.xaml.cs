using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>おすすめの 1 件。Group が同じものは同じ見出しの下に並ぶ。</summary>
public sealed record Recommendation(string Group, string Name, string Description, string Url, SymbolRegular Icon);

public sealed record RecommendationGroup(string Name, IReadOnlyList<Recommendation> Items);

/// <summary>作者がおすすめするアプリやサイトの一覧。押すとふだんのブラウザで開く。</summary>
public partial class RecommendPage : UserControl
{
    // ここに足したり消したりすれば、画面の一覧が変わる（上から順に並ぶ）
    private static readonly Recommendation[] Items =
    [
        new("アプリ", "Obsidian", "自分のパソコンに保存するメモ帳。メモ同士をリンクでつなげられる",
            "https://obsidian.md/", SymbolRegular.Notebook24),
        new("アプリ", "Notion", "メモ・表・タスク管理をひとつにまとめられるノートアプリ",
            "https://www.notion.so/", SymbolRegular.DocumentText24),
        new("サイト", "DeepL 翻訳", "自然な日本語・英語に訳してくれる翻訳サイト",
            "https://www.deepl.com/translator", SymbolRegular.Translate24),
        new("サイト", "NotebookLM", "学習に特化したAI",
            "https://notebooklm.google.com/", SymbolRegular.Search24),
        new("サイト", "ミニゲームランド", "いろいろなゲームができるサイト",
            "https://mini-game-land.akiduki2ha46.workers.dev/", SymbolRegular.Games24),
        new("サイト","Perplexity","検索用のAI",
            "https://www.perplexity.ai/", SymbolRegular.Globe24)
    ];

    public RecommendPage()
    {
        InitializeComponent();
        Groups.ItemsSource = Items
            .GroupBy(i => i.Group)
            .Select(g => new RecommendationGroup(g.Key, g.ToList()))
            .ToList();
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e) => Open(sender);

    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            Open(sender);
            e.Handled = true;
        }
    }

    private static void Open(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not Recommendation item)
            return;
        // 開くのは http と https だけ
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // ブラウザが開けなくても、霽月は止めない
        }
    }
}
