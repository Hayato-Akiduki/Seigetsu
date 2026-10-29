using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using UiButton = Wpf.Ui.Controls.Button;

namespace Seigetsu.Pages;

/// <summary>
/// 掲示板。メモ・付箋・タスクを好きな場所に貼っておける。
/// 貼るのは中身の写しではなく「どれを・どこに」だけなので、付箋やタスクを直せば掲示板の表示も変わる。
/// </summary>
public partial class BoardPage : UserControl, IRefreshable
{
    private const double CardWidth = 240;
    private const double Gap = 24;

    private IReadOnlyList<BoardItem> _items = [];
    private Dictionary<long, Memo> _memos = [];
    private Dictionary<long, StickyNote> _stickies = [];
    private Dictionary<long, TaskItem> _tasks = [];

    // ドラッグ中の 1 枚
    private FrameworkElement? _dragCard;
    private Point _dragStart;
    private Point _dragOrigin;
    private bool _dragMoved;
    private int _topZ;

    public BoardPage()
    {
        InitializeComponent();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        var db = App.Database;
        _memos = db.GetMemos().ToDictionary(m => m.Id); // 先に読む（1 枚も無ければ、ここで作られる）
        _items = db.GetBoardItems();
        _stickies = db.GetStickies().ToDictionary(n => n.Id);
        _tasks = db.AllTasks().ToDictionary(t => t.Id);

        // 外観を切り替えたときも作り直されるので、色はここで決める
        Board.Background = DotBrush();
        Board.Children.Clear();
        _topZ = 0;
        foreach (var item in _items) // 下にあるものから順に置くと、重なり順もそのまま
        {
            if (BuildCard(item) is not { } card)
                continue;
            Canvas.SetLeft(card, item.X);
            Canvas.SetTop(card, item.Y);
            Panel.SetZIndex(card, ++_topZ);
            Board.Children.Add(card);
        }

        SubtitleText.Text = _items.Count > 0
            ? $"{_items.Count}枚 ・ ドラッグで動かせます ・ ダブルクリックで開きます"
            : "メモ・付箋・タスクを、好きな場所に貼っておけます";
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // カードの大きさは並べ終わってから決まるので、そのあとで広さを合わせる
        Dispatcher.BeginInvoke(UpdateBoardSize, DispatcherPriority.Loaded);
    }

    /// <summary>掲示板の広さ。見えている範囲より狭くせず、いちばん遠いカードの先に少し余白を足す。</summary>
    private void UpdateBoardSize()
    {
        double right = 0, bottom = 0;
        foreach (FrameworkElement card in Board.Children)
        {
            right = Math.Max(right, Canvas.GetLeft(card) + card.ActualWidth);
            bottom = Math.Max(bottom, Canvas.GetTop(card) + card.ActualHeight);
        }
        Board.Width = Math.Max(Scroller.ViewportWidth, right + Gap);
        Board.Height = Math.Max(Scroller.ViewportHeight, bottom + Gap);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            UpdateBoardSize();
    }

    /// <summary>コルクボードの代わりの、うすい点の模様。</summary>
    private static Brush DotBrush()
    {
        var dot = ThemeService.Brush("MutedBrush").Clone();
        dot.Opacity = 0.3;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(ThemeService.Brush("SubtleBrush"), null, new RectangleGeometry(new Rect(0, 0, Gap, Gap))));
        drawing.Children.Add(new GeometryDrawing(dot, null, new EllipseGeometry(new Point(Gap / 2, Gap / 2), 1.3, 1.3)));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, Gap, Gap),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    // ---- カード ---------------------------------------------------------
    private Border? BuildCard(BoardItem item)
    {
        Brush background;
        SymbolRegular icon;
        string kind, name;
        UIElement body;
        switch (item.Kind)
        {
            case "memo" when _memos.TryGetValue(item.RefId, out var memo):
                (background, icon, kind) = (ThemeService.Brush("CardBrush"), SymbolRegular.Notepad24, "メモ");
                name = $"メモ「{NotesPage.DisplayTitleOf(memo.Title)}」";
                body = MemoBody(memo);
                break;
            case "sticky" when _stickies.TryGetValue(item.RefId, out var note):
                (background, icon, kind) = (ThemeService.StickyBrush(note), SymbolRegular.Note24, "付箋");
                name = $"付箋「{note.Title}」";
                body = StickyBody(note);
                break;
            case "task" when _tasks.TryGetValue(item.RefId, out var task):
                (background, icon, kind) = (ThemeService.Brush("CardBrush"), SymbolRegular.TaskListSquareLtr24, "タスク");
                name = $"タスク「{task.Title}」";
                body = TaskBody(task);
                break;
            default:
                return null;
        }

        // 上の帯: 種類と、はがすボタン
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var unpin = new UiButton
        {
            Style = (Style)FindResource("IconButton"),
            Padding = new Thickness(5),
            Margin = new Thickness(0),
            ToolTip = "掲示板からはがす",
            Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss24, FontSize = 13, Foreground = ThemeService.Brush("MutedBrush") },
        };
        AutomationProperties.SetName(unpin, $"{name}を掲示板からはがす");
        unpin.Click += (_, _) => Unpin(item);
        DockPanel.SetDock(unpin, Dock.Right);
        header.Children.Add(unpin);
        header.Children.Add(new SymbolIcon
        {
            Symbol = icon, FontSize = 14, Foreground = ThemeService.Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = kind, FontSize = 11, Margin = new Thickness(6, 0, 0, 0),
            Foreground = ThemeService.Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
        });

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(body);

        var card = new Border
        {
            Width = CardWidth,
            Background = background,
            BorderBrush = item.Kind == "sticky" ? Brushes.Transparent : ThemeService.Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 6, 6, 14),
            Child = content,
            Tag = item,
            Cursor = Cursors.SizeAll,
            Focusable = true,
            ToolTip = "ドラッグで移動 ・ ダブルクリックで開く",
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = ThemeService.IsDark ? 0.45 : 0.14 },
            ContextMenu = CardMenu(item),
        };
        AutomationProperties.SetName(card, name);
        card.MouseLeftButtonDown += OnCardMouseDown;
        card.MouseMove += OnCardMouseMove;
        card.MouseLeftButtonUp += OnCardMouseUp;
        card.LostMouseCapture += (_, _) => _dragCard = null;
        card.SizeChanged += (_, _) => UpdateBoardSize();
        card.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Open(item); e.Handled = true; }
            else if (e.Key == Key.Delete) { Unpin(item); e.Handled = true; }
        };
        return card;
    }

    private static UIElement MemoBody(Memo memo)
    {
        var text = memo.Body.Trim();
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = NotesPage.DisplayTitleOf(memo.Title), FontSize = 15, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeService.Brush("TextBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = text == "" ? "（メモは空です）" : text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 240,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = ThemeService.Brush(text == "" ? "MutedBrush" : "TextBrush"),
        });
        return panel;
    }

    private static UIElement StickyBody(StickyNote note)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = note.Title, FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush("TextBrush"),
        });
        if (note.Body != "")
        {
            panel.Children.Add(new TextBlock
            {
                Text = note.Body, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 220, Margin = new Thickness(0, 6, 0, 0), Foreground = ThemeService.Brush("TextBrush"),
            });
        }
        return panel;
    }

    private UIElement TaskBody(TaskItem task)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        // 完了のチェックは掲示板からそのまま付けられる
        var check = new CheckBox
        {
            IsChecked = task.Completed,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, 4, 0),
            MinWidth = 0,
            Cursor = Cursors.Hand,
            ToolTip = task.Completed ? "未完了に戻す" : "完了にする",
        };
        AutomationProperties.SetName(check, $"タスク「{task.Title}」を完了にする");
        check.Click += (_, _) =>
        {
            App.Database.SetTaskCompleted(task.Id, check.IsChecked == true);
            Main.DataChanged();
        };
        grid.Children.Add(check);

        var text = new StackPanel();
        var title = new TextBlock
        {
            Text = task.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.Brush(task.Completed ? "MutedBrush" : "TextBrush"),
        };
        if (task.Completed)
            title.TextDecorations = TextDecorations.Strikethrough;
        text.Children.Add(title);

        var detail = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        detail.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Fill = TaskFormat.PriorityBrush(task.Priority),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = $"優先度 {task.Priority}",
        });
        detail.Children.Add(new TextBlock
        {
            Text = $"{task.Category} ・ ", FontSize = 12, Foreground = ThemeService.Brush("MutedBrush"),
        });
        detail.Children.Add(new TextBlock
        {
            Text = TaskFormat.DueLabel(task.DueDate), FontSize = 12, Foreground = TaskFormat.DueBrush(task.DueDate, task.Completed),
        });
        if (task.Favorite)
        {
            detail.Children.Add(new SymbolIcon
            {
                Symbol = SymbolRegular.Star24, Filled = true, FontSize = 13, Margin = new Thickness(6, 0, 0, 0),
                Foreground = ThemeService.Brush("WarningBrush"),
            });
        }
        text.Children.Add(detail);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private ContextMenu CardMenu(BoardItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(item.Kind == "memo" ? "メモ画面で開く" : "開いて編集", SymbolRegular.Edit24, () => Open(item)));
        if (item.Kind == "sticky")
        {
            var label = App.DesktopStickies.IsShown(item.RefId) ? "デスクトップから外す" : "デスクトップに表示";
            menu.Items.Add(MenuEntry(label, SymbolRegular.DesktopArrowRight24, () => App.DesktopStickies.Toggle(item.RefId)));
        }
        menu.Items.Add(MenuEntry("掲示板からはがす", SymbolRegular.PinOff24, () => Unpin(item)));
        return menu;
    }

    private void Open(BoardItem item)
    {
        switch (item.Kind)
        {
            case "memo":
                // メモの画面は、開いたときにこの設定のメモを出す
                App.Database.SetSetting(NotesPage.SelectedSetting, item.RefId.ToString());
                Main.ShowPage("notes");
                break;
            case "sticky" when _stickies.TryGetValue(item.RefId, out var note):
                Main.OpenStickyDialog(note);
                break;
            case "task" when _tasks.TryGetValue(item.RefId, out var task):
                Main.OpenTaskDialog(task);
                break;
        }
    }

    private void Unpin(BoardItem item)
    {
        App.Database.UnpinBoardItem(item.Id);
        Main.Notify("掲示板からはがしました（メモ・付箋・タスクそのものは残っています）。");
        Refresh();
    }

    // ---- ドラッグで動かす ---------------------------------------------
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        var card = (FrameworkElement)sender;
        if (e.ClickCount == 2)
        {
            _dragCard = null;
            Open((BoardItem)card.Tag);
            e.Handled = true;
            return;
        }
        card.Focus();
        // 触ったものをいちばん上に
        Panel.SetZIndex(card, ++_topZ);
        _dragCard = card;
        _dragStart = e.GetPosition(Board);
        _dragOrigin = new Point(Canvas.GetLeft(card), Canvas.GetTop(card));
        _dragMoved = false;
        card.CaptureMouse();
        e.Handled = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCard != sender || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = e.GetPosition(Board) - _dragStart;
        // 少し動いたくらいでは、クリックとみなして動かさない
        if (!_dragMoved && Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
            return;
        _dragMoved = true;
        Canvas.SetLeft(_dragCard, Math.Max(0, _dragOrigin.X + delta.X));
        Canvas.SetTop(_dragCard, Math.Max(0, _dragOrigin.Y + delta.Y));
        UpdateBoardSize();
        // 端まで持っていったら、その先が見えるようにする
        _dragCard.BringIntoView();
    }

    private void OnCardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragCard != sender)
            return;
        var card = _dragCard;
        card.ReleaseMouseCapture();
        // 動かしていなくても、いちばん上に来た重なり順は残す
        App.Database.MoveBoardItem(((BoardItem)card.Tag).Id, Canvas.GetLeft(card), Canvas.GetTop(card));
        e.Handled = true;
    }

    // ---- 貼る -------------------------------------------------------------
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        // 見えている範囲の左上に、少しずつずらして貼る
        var step = _items.Count % 6 * 28;
        var at = new Point(Scroller.HorizontalOffset + Gap + step, Scroller.VerticalOffset + Gap + step);
        var menu = PinMenu(at);
        menu.PlacementTarget = PinButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnBoardRightClick(object sender, MouseButtonEventArgs e)
    {
        // カードの上の右クリックは、カードのメニューに任せる
        if (e.OriginalSource != Board)
            return;
        var menu = PinMenu(e.GetPosition(Board));
        menu.PlacementTarget = Board;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>まだ貼っていないメモ・付箋・未完了のタスクと、新しく作って貼る項目。</summary>
    private ContextMenu PinMenu(Point at)
    {
        var pinned = _items.Select(i => (i.Kind, i.RefId)).ToHashSet();
        var menu = new ContextMenu();

        var memos = MenuEntry("メモ", SymbolRegular.Notepad24, null);
        foreach (var memo in _memos.Values.Where(m => !pinned.Contains(("memo", m.Id))))
            memos.Items.Add(MenuEntry(NotesPage.DisplayTitleOf(memo.Title), null, () => Pin("memo", memo.Id, at)));
        if (memos.Items.Count == 0)
            memos.Items.Add(new MenuItem { Header = "貼れるメモはありません", IsEnabled = false });
        menu.Items.Add(memos);

        var stickies = MenuEntry("付箋", SymbolRegular.Note24, null);
        foreach (var note in _stickies.Values.OrderByDescending(n => n.Id).Where(n => !pinned.Contains(("sticky", n.Id))))
            stickies.Items.Add(MenuEntry(note.Title, null, () => Pin("sticky", note.Id, at)));
        if (stickies.Items.Count == 0)
            stickies.Items.Add(new MenuItem { Header = "貼れる付箋はありません", IsEnabled = false });
        menu.Items.Add(stickies);

        var tasks = MenuEntry("タスク", SymbolRegular.TaskListSquareLtr24, null);
        foreach (var task in App.Database.GetTasks(filter: TaskFilter.Open).Where(t => !pinned.Contains(("task", t.Id))))
            tasks.Items.Add(MenuEntry($"{task.Title}（{TaskFormat.DueLabel(task.DueDate)}）", null, () => Pin("task", task.Id, at)));
        if (tasks.Items.Count == 0)
            tasks.Items.Add(new MenuItem { Header = "貼れる未完了のタスクはありません", IsEnabled = false });
        menu.Items.Add(tasks);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("新しいメモを作って貼る…", SymbolRegular.Add24, () => AddMemo(at)));
        menu.Items.Add(MenuEntry("新しい付箋を書いて貼る…", SymbolRegular.Add24,
            () => Main.OpenStickyDialog(onAdded: id => App.Database.PinToBoard("sticky", id, at.X, at.Y))));
        menu.Items.Add(MenuEntry("新しいタスクを作って貼る…", SymbolRegular.Add24,
            () => Main.OpenTaskDialog(onAdded: id => App.Database.PinToBoard("task", id, at.X, at.Y))));
        return menu;
    }

    /// <summary>新しいメモを作って貼り、すぐ名前と本文を書けるようにメモの画面で開く。</summary>
    private void AddMemo(Point at)
    {
        var id = App.Database.AddMemo("新しいメモ");
        App.Database.PinToBoard("memo", id, Math.Max(0, at.X), Math.Max(0, at.Y));
        App.Database.SetSetting(NotesPage.SelectedSetting, id.ToString());
        NotesPage.FocusTitleOnOpen = true;
        Main.ShowPage("notes");
        Main.Notify("新しいメモを作って、掲示板に貼りました。");
    }

    private void Pin(string kind, long refId, Point at)
    {
        App.Database.PinToBoard(kind, refId, Math.Max(0, at.X), Math.Max(0, at.Y));
        Main.Notify("掲示板に貼りました。");
        Refresh();
    }

    private static MenuItem MenuEntry(string text, SymbolRegular? icon, Action? onClick)
    {
        // 文字列のまま入れると「_」が下線の印として消えるので、TextBlock で入れる
        var entry = new MenuItem
        {
            Header = new TextBlock { Text = text, MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        if (icon is { } symbol)
            entry.Icon = new SymbolIcon { Symbol = symbol, FontSize = 16 };
        if (onClick is not null)
            entry.Click += (_, _) => onClick();
        return entry;
    }
}
