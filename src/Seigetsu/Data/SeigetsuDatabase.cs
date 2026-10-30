using Microsoft.Data.Sqlite;

namespace Seigetsu.Data;

public sealed record TaskItem(
    long Id, string Title, bool Completed, string Priority, string DueDate, string Category, bool Favorite);

/// <summary>付箋 1 枚。Color は選んだ色の番号（選んでいなければ null で、id から色を決める）。</summary>
public sealed record StickyNote(long Id, string Title, string Body, string CreatedAt, int? Color = null);

/// <summary>
/// メモ 1 枚。Id = 1 は v6 から使っているメモで、本文は v6 と同じ notes の表に入っている。
/// UpdatedAt は最後に書きかえた日時。
/// </summary>
public sealed record Memo(long Id, string Title, string Body, string CreatedAt, string UpdatedAt);

/// <summary>デスクトップに出している付箋 1 枚の、窓の位置と大きさ。</summary>
public sealed record DesktopSticky(long StickyId, double X, double Y, double Width, double Height, bool Topmost);

/// <summary>
/// 時間割の 1 コマ。Weekday は 1=月 〜 6=土、Period は何限目か。TimeText は「8:50～10:20」など（分からなければ空）。
/// Room は教室（シラバスから読む。分からなければ空）、Codes は NU-Web の時間割コード（カンマ区切り）。
/// </summary>
public sealed record ClassSlot(int Weekday, int Period, string Title, string Detail, string TimeText,
    string Room = "", string Codes = "");

/// <summary>お知らせの出どころ。</summary>
public static class AnnouncementSource
{
    public const string Lacs = "lacs";
    public const string NuWeb = "nuweb";
}

/// <summary>
/// 読み込んだお知らせ 1 件（残す前の形）。Source は AnnouncementSource、Id は出どころの中で一意な値。
/// CourseName は LACS なら授業名、NU-Web なら掲示の分類や差出人。Url は開くためのアドレス（無ければ空）。
/// </summary>
/// Body が null のときは、本文を読んでいない（残してあるものは本文をそのままにし、まだ無いものは足さない）。
public sealed record AnnouncementInput(
    string Source, string Id, string CourseId, string CourseName, string Title, string? Body, DateTime Created, string Url);

/// <summary>残してあるお知らせ 1 件（LACS の連絡事項と NU-Web の掲示）。Seen は霽月で読んだかどうか。</summary>
public sealed record AnnouncementItem(
    string Id, string Source, string CourseId, string CourseName, string Title, string Body, DateTime Created, string Url,
    bool Seen);

/// <summary>掲示板に貼ったもの 1 枚。Kind は "memo" / "sticky" / "task"、RefId はそれぞれの id。</summary>
public sealed record BoardItem(long Id, string Kind, long RefId, double X, double Y, long Z);

/// <summary>タスク一覧の絞り込み（v6 と同じ 5 種類）。</summary>
public enum TaskFilter { All, Open, Done, Favorite, Overdue }

/// <summary>
/// v6 と同じ seigetsu.db を読み書きする。テーブル構成も v6 と同じなので、v6 と v7 のどちらで開いても同じデータが見える。
/// </summary>
public sealed class SeigetsuDatabase
{
    // 休憩時間は作業時間に数えない（v6 と同じ）
    private const string BreakMode = "ポモドーロ-休憩";
    private const string PriorityOrder = "CASE priority WHEN '高' THEN 1 WHEN '中' THEN 2 ELSE 3 END";
    private const string TaskColumns = "id, title, completed, priority, due_date, category, favorite";

    private readonly string _connectionString;

    public SeigetsuDatabase(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        BackupOnce();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // v6 と同時に開いていても、相手の書き込みが終わるまで少し待つ
            DefaultTimeout = 5,
        }.ToString();
        CreateTables();
    }

    public string Path { get; }

    /// <summary>v7 が初めて書き込む前の状態を残しておく（すでにあれば何もしない）。</summary>
    public string BackupPath => Path + ".before-v7";

    private void BackupOnce()
    {
        if (File.Exists(Path) && !File.Exists(BackupPath))
            File.Copy(Path, BackupPath);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    private static string Now => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar();
    }

    // ---- テーブルの用意（v6 の db.py と同じ） ---------------------------
    private void CreateTables()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        void Run(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        Run("""
            CREATE TABLE IF NOT EXISTS tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                completed INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL)
            """);

        // 古いデータベースには無い列を足す
        var columns = new HashSet<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA table_info(tasks)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }
        var additions = new (string Name, string Definition)[]
        {
            ("priority", "TEXT NOT NULL DEFAULT '中'"),
            ("due_date", "TEXT NOT NULL DEFAULT ''"),
            ("category", "TEXT NOT NULL DEFAULT '一般'"),
            ("favorite", "INTEGER NOT NULL DEFAULT 0"),
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name))
                Run($"ALTER TABLE tasks ADD COLUMN {name} {definition}");
        }

        Run("""
            CREATE TABLE IF NOT EXISTS notes (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                body TEXT NOT NULL DEFAULT '')
            """);
        Run("INSERT OR IGNORE INTO notes (id, body) VALUES (1, '')");
        // メモを何枚も持てるようにする表（v7 で追加）。v6 の notes の表は変えずに、別の表に持つ。
        // id = 1 は v6 から使っているメモで、本文は notes の表のほうに書く（v6 で開いても同じ中身が見えるように）。
        Run("""
            CREATE TABLE IF NOT EXISTS memo_pages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                body TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL)
            """);
        Run("""
            CREATE TABLE IF NOT EXISTS work_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL,
                duration_seconds INTEGER NOT NULL,
                mode TEXT NOT NULL)
            """);
        Run("""
            CREATE TABLE IF NOT EXISTS playlist (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                position INTEGER NOT NULL DEFAULT 0)
            """);
        Run("""
            CREATE TABLE IF NOT EXISTS sticky_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL)
            """);
        // 掲示板（v7 で追加。v6 はこの表を使わないので、あっても困らない）
        Run("""
            CREATE TABLE IF NOT EXISTS board_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                ref_id INTEGER NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL,
                z INTEGER NOT NULL DEFAULT 0,
                UNIQUE (kind, ref_id))
            """);
        // 付箋の色（v7 で追加）。v6 の sticky_notes の表は変えずに、別の表に持つ
        Run("""
            CREATE TABLE IF NOT EXISTS sticky_colors (
                sticky_id INTEGER PRIMARY KEY,
                color INTEGER NOT NULL)
            """);
        // デスクトップに出している付箋（v7 で追加）
        Run("""
            CREATE TABLE IF NOT EXISTS desktop_stickies (
                sticky_id INTEGER PRIMARY KEY,
                x REAL NOT NULL,
                y REAL NOT NULL,
                width REAL NOT NULL,
                height REAL NOT NULL,
                topmost INTEGER NOT NULL DEFAULT 0)
            """);
        // 長崎大学モード（v7 で追加）。NU-Web から取り込んだ時間割（weekday は 1=月 〜 6=土）
        Run("""
            CREATE TABLE IF NOT EXISTS nu_timetable (
                weekday INTEGER NOT NULL,
                period INTEGER NOT NULL,
                title TEXT NOT NULL,
                detail TEXT NOT NULL DEFAULT '',
                time_text TEXT NOT NULL DEFAULT '',
                room TEXT NOT NULL DEFAULT '',
                codes TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (weekday, period))
            """);
        // 教室（room）と時間割コード（codes、カンマ区切り）は後から足した列。前の形の表には足す
        var timetableColumns = new HashSet<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA table_info(nu_timetable)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                timetableColumns.Add(reader.GetString(1));
        }
        foreach (var name in new[] { "room", "codes" })
        {
            if (!timetableColumns.Contains(name))
                Run($"ALTER TABLE nu_timetable ADD COLUMN {name} TEXT NOT NULL DEFAULT ''");
        }
        // シラバスから読んだ教室（時間割コードごと）。シラバスは読むのに時間がかかるので、覚えておいて 1 週間ごとに読み直す
        Run("""
            CREATE TABLE IF NOT EXISTS nu_rooms (
                code TEXT PRIMARY KEY,
                room TEXT NOT NULL,
                checked_at TEXT NOT NULL)
            """);
        // LACS から取り込んだ課題と、それで作ったタスクの対応（同じ課題を二重に足さないため）
        Run("""
            CREATE TABLE IF NOT EXISTS nu_lacs_tasks (
                item_id TEXT PRIMARY KEY,
                task_id INTEGER NOT NULL)
            """);
        // お知らせ（LACS の連絡事項と NU-Web の掲示。自動更新のたびに読んで残す）。seen は霽月で読んだかどうか。
        // 表の名前は、はじめ LACS だけを入れていたときのまま。source（出どころ）と url は後から足した列
        Run("""
            CREATE TABLE IF NOT EXISTS nu_lacs_announcements (
                id TEXT PRIMARY KEY,
                course_id TEXT NOT NULL,
                course_name TEXT NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                created_at TEXT NOT NULL,
                seen INTEGER NOT NULL DEFAULT 0)
            """);
        var announcementColumns = new HashSet<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "PRAGMA table_info(nu_lacs_announcements)";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                announcementColumns.Add(reader.GetString(1));
        }
        if (!announcementColumns.Contains("source"))
            Run("ALTER TABLE nu_lacs_announcements ADD COLUMN source TEXT NOT NULL DEFAULT 'lacs'");
        if (!announcementColumns.Contains("url"))
            Run("ALTER TABLE nu_lacs_announcements ADD COLUMN url TEXT NOT NULL DEFAULT ''");
        Run("""
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL)
            """);
        // 付箋はもともと最前面に出していたが、ほかの窓のうしろ（壁紙の上）に置くように変えた。前から出していた付箋も一度だけ合わせる
        Run("""
            UPDATE desktop_stickies SET topmost = 0
            WHERE NOT EXISTS (SELECT 1 FROM settings WHERE key = 'desktop_stickies_behind')
            """);
        Run("INSERT OR IGNORE INTO settings (key, value) VALUES ('desktop_stickies_behind', '1')");
        // メモの表を初めて作ったときだけ、今までのメモを 1 枚目として入れる（消したあとに勝手に戻らないよう、一度だけ）
        Run($"""
            INSERT OR IGNORE INTO memo_pages (id, title, body, created_at, updated_at)
            SELECT 1, 'メモ', '', '{Now}', '{Now}'
            WHERE NOT EXISTS (SELECT 1 FROM settings WHERE key = 'memo_pages_ready')
            """);
        Run("INSERT OR IGNORE INTO settings (key, value) VALUES ('memo_pages_ready', '1')");
        transaction.Commit();
    }

    // ---- 設定 ---------------------------------------------------------
    public string GetSetting(string key, string defaultValue = "") =>
        Scalar("SELECT value FROM settings WHERE key = $key", ("$key", key)) as string ?? defaultValue;

    public int GetIntSetting(string key, int defaultValue) =>
        int.TryParse(GetSetting(key, ""), out var value) ? value : defaultValue;

    public void SetSetting(string key, string value) =>
        Execute("""
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, ("$key", key), ("$value", value));

    // ---- タスク -------------------------------------------------------
    /// <summary>追加したタスクの id を返す。</summary>
    public long AddTask(string title, string priority = "中", string dueDate = "", string category = "一般") =>
        Convert.ToInt64(Scalar("""
            INSERT INTO tasks (title, completed, created_at, priority, due_date, category, favorite)
            VALUES ($title, 0, $now, $priority, $due, $category, 0)
            RETURNING id
            """,
            ("$title", title), ("$now", Now), ("$priority", priority), ("$due", dueDate), ("$category", category)));

    public void UpdateTask(long id, string title, string priority, string dueDate, string category) =>
        Execute("UPDATE tasks SET title = $title, priority = $priority, due_date = $due, category = $category WHERE id = $id",
            ("$title", title), ("$priority", priority), ("$due", dueDate), ("$category", category), ("$id", id));

    public void SetTaskCompleted(long id, bool completed) =>
        Execute("UPDATE tasks SET completed = $done WHERE id = $id", ("$done", completed ? 1 : 0), ("$id", id));

    public void ToggleFavorite(long id) =>
        Execute("UPDATE tasks SET favorite = 1 - favorite WHERE id = $id", ("$id", id));

    public void DeleteTask(long id)
    {
        Execute("DELETE FROM tasks WHERE id = $id", ("$id", id));
        Execute("DELETE FROM board_items WHERE kind = 'task' AND ref_id = $id", ("$id", id));
    }

    /// <summary>タスク画面の一覧。並び順は v6 と同じ（未完了 → お気に入り → 優先度 → 締切）。</summary>
    public IReadOnlyList<TaskItem> GetTasks(string search = "", TaskFilter filter = TaskFilter.All)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(title LIKE $search OR category LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{search.Trim()}%");
        }
        switch (filter)
        {
            case TaskFilter.Open:
                where.Add("completed = 0");
                break;
            case TaskFilter.Done:
                where.Add("completed = 1");
                break;
            case TaskFilter.Favorite:
                where.Add("favorite = 1");
                break;
            case TaskFilter.Overdue:
                where.Add("completed = 0 AND due_date <> '' AND due_date < $today");
                command.Parameters.AddWithValue("$today", Today);
                break;
        }
        var clause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        command.CommandText = $"""
            SELECT {TaskColumns} FROM tasks {clause}
            ORDER BY completed, favorite DESC, {PriorityOrder},
                     CASE WHEN due_date = '' THEN 1 ELSE 0 END, due_date, id DESC
            """;
        return ReadTasks(command);
    }

    /// <summary>ホーム用: 期限切れ・今日まで・お気に入り・期限なしの未完了タスク。</summary>
    public IReadOnlyList<TaskItem> TodayTasks(int limit = 8)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TaskColumns} FROM tasks
            WHERE completed = 0 AND ((due_date <> '' AND due_date <= $today) OR favorite = 1 OR due_date = '')
            ORDER BY CASE WHEN due_date <> '' AND due_date <= $today THEN 0 ELSE 1 END,
                     favorite DESC, {PriorityOrder}, due_date, id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$today", Today);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadTasks(command);
    }

    /// <summary>大学ページ用: LACS から取り込んだ課題のうち、まだ完了していないもの（締切の近い順）。</summary>
    public IReadOnlyList<TaskItem> OpenLacsTasks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TaskColumns} FROM tasks
            WHERE completed = 0 AND id IN (SELECT task_id FROM nu_lacs_tasks)
            ORDER BY CASE WHEN due_date = '' THEN 1 ELSE 0 END, due_date, id
            """;
        return ReadTasks(command);
    }

    /// <summary>LACS から取り込んだタスクの、もとの課題（「授業の id/成績表の列の id」）。LACS の課題でなければ null。</summary>
    public string? LacsItemOfTask(long taskId) =>
        Scalar("SELECT item_id FROM nu_lacs_tasks WHERE task_id = $task", ("$task", taskId)) as string;

    /// <summary>カレンダー用: その日が締切のタスク。</summary>
    public IReadOnlyList<TaskItem> TasksOn(string day)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TaskColumns} FROM tasks WHERE due_date = $day ORDER BY completed, {PriorityOrder}, id";
        command.Parameters.AddWithValue("$day", day);
        return ReadTasks(command);
    }

    /// <summary>カレンダー用: 日付ごとの (未完了の数, 完了の数)。</summary>
    public Dictionary<string, (int Open, int Done)> TaskCountsBetween(string start, string end)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT due_date, SUM(completed = 0), SUM(completed = 1) FROM tasks
            WHERE due_date >= $start AND due_date <= $end GROUP BY due_date
            """;
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        var counts = new Dictionary<string, (int, int)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        return counts;
    }

    /// <summary>締切の通知用: 未完了のタスクのうち、(期限切れの数, 今日が締切の数)。</summary>
    public (int Overdue, int Today) DueCounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(due_date < $today), 0), COALESCE(SUM(due_date = $today), 0)
            FROM tasks WHERE completed = 0 AND due_date <> ''
            """;
        command.Parameters.AddWithValue("$today", Today);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// データベースを丸ごと別のファイルに写す（SQLite のバックアップの仕組みを使うので、使っている最中でも壊れずに写せる）。
    /// </summary>
    public void BackupTo(string path)
    {
        using var source = Open();
        // 写し先は使い終わったらすぐ閉じたいので、つなぎっぱなしにしない
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    /// <summary>(未完了の数, 全部の数)</summary>
    public (int Open, int Total) TaskCounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(completed = 0), 0), COUNT(*) FROM tasks";
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public IReadOnlyList<string> Categories()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT category FROM tasks WHERE category <> '' ORDER BY category";
        var categories = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            categories.Add(reader.GetString(0));
        return categories;
    }

    private static List<TaskItem> ReadTasks(SqliteCommand command)
    {
        var tasks = new List<TaskItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(new TaskItem(
                Id: reader.GetInt64(0),
                Title: reader.GetString(1),
                Completed: reader.GetInt64(2) != 0,
                Priority: reader.GetString(3),
                DueDate: reader.GetString(4),
                Category: reader.GetString(5),
                Favorite: reader.GetInt64(6) != 0));
        }
        return tasks;
    }

    // ---- メモ（何枚でも。id = 1 の本文だけは v6 と同じ notes の表） -----------
    // id = 1 の本文は notes の表から読む
    private const string MemoColumns = """
        m.id, m.title, CASE WHEN m.id = 1 THEN COALESCE((SELECT body FROM notes WHERE id = 1), '') ELSE m.body END,
        m.created_at, m.updated_at
        """;

    /// <summary>
    /// メモの一覧（作った順）。1 枚も無ければ、空のメモを 1 枚作る。
    /// 1 枚目を消したあとで v6 がそこに書いた場合は、消えないように 1 枚目として戻す。
    /// </summary>
    public IReadOnlyList<Memo> GetMemos()
    {
        Execute($"""
            INSERT OR IGNORE INTO memo_pages (id, title, body, created_at, updated_at)
            SELECT 1, 'メモ', '', '{Now}', '{Now}'
            WHERE (SELECT body FROM notes WHERE id = 1) <> '' OR NOT EXISTS (SELECT 1 FROM memo_pages)
            """);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MemoColumns} FROM memo_pages m ORDER BY m.id";
        var memos = new List<Memo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            memos.Add(ReadMemo(reader));
        return memos;
    }

    /// <summary>1 枚だけ読む。無ければ null。</summary>
    public Memo? GetMemo(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {MemoColumns} FROM memo_pages m WHERE m.id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMemo(reader) : null;
    }

    private static Memo ReadMemo(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));

    /// <summary>新しいメモを作って、その id を返す。</summary>
    public long AddMemo(string title) =>
        Convert.ToInt64(Scalar("""
            INSERT INTO memo_pages (title, body, created_at, updated_at) VALUES ($title, '', $now, $now) RETURNING id
            """, ("$title", title), ("$now", Now)));

    /// <summary>名前と本文を保存する。id = 1 の本文は notes の表に書く。</summary>
    public void SaveMemo(long id, string title, string body)
    {
        if (id == 1)
        {
            Execute("UPDATE notes SET body = $body WHERE id = 1", ("$body", body));
            body = ""; // 同じ本文を二か所に持たない
        }
        Execute("UPDATE memo_pages SET title = $title, body = $body, updated_at = $now WHERE id = $id",
            ("$title", title), ("$body", body), ("$now", Now), ("$id", id));
    }

    public void DeleteMemo(long id)
    {
        Execute("DELETE FROM memo_pages WHERE id = $id", ("$id", id));
        Execute("DELETE FROM board_items WHERE kind = 'memo' AND ref_id = $id", ("$id", id));
        // 1 枚目を消したら、v6 側のメモも空にする（中身が残っていると、また 1 枚目として戻ってくるため）
        if (id == 1)
            Execute("UPDATE notes SET body = '' WHERE id = 1");
    }

    // ---- BGM のプレイリスト -------------------------------------------
    public IReadOnlyList<Services.Track> GetPlaylist()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path FROM playlist ORDER BY position, id";
        var tracks = new List<Services.Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            tracks.Add(new Services.Track(reader.GetInt64(0), reader.GetString(1)));
        return tracks;
    }

    /// <summary>最後に足す。同じファイルがすでにあれば何もしない（v6 と同じ）。追加したら true。</summary>
    public bool AddTrack(string path) =>
        Execute("""
            INSERT OR IGNORE INTO playlist (path, position)
            VALUES ($path, (SELECT COALESCE(MAX(position), -1) + 1 FROM playlist))
            """, ("$path", path)) > 0;

    public void RemoveTrack(long id) =>
        Execute("DELETE FROM playlist WHERE id = $id", ("$id", id));

    // ---- 付箋 ---------------------------------------------------------
    /// <summary>追加した付箋の id を返す。</summary>
    public long AddSticky(string title, string body) =>
        Convert.ToInt64(Scalar("INSERT INTO sticky_notes (title, body, created_at) VALUES ($title, $body, $now) RETURNING id",
            ("$title", title), ("$body", body), ("$now", Now)));

    public void UpdateSticky(long id, string title, string body) =>
        Execute("UPDATE sticky_notes SET title = $title, body = $body WHERE id = $id",
            ("$title", title), ("$body", body), ("$id", id));

    public void DeleteSticky(long id)
    {
        Execute("DELETE FROM sticky_notes WHERE id = $id", ("$id", id));
        Execute("DELETE FROM board_items WHERE kind = 'sticky' AND ref_id = $id", ("$id", id));
        Execute("DELETE FROM desktop_stickies WHERE sticky_id = $id", ("$id", id));
        Execute("DELETE FROM sticky_colors WHERE sticky_id = $id", ("$id", id));
    }

    public void SetStickyColor(long id, int color) =>
        Execute("INSERT OR REPLACE INTO sticky_colors (sticky_id, color) VALUES ($id, $color)",
            ("$id", id), ("$color", color));

    /// <summary>新しい順（v6 と同じ）。</summary>
    public IReadOnlyList<StickyNote> GetStickies()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.title, s.body, s.created_at, c.color
            FROM sticky_notes s LEFT JOIN sticky_colors c ON c.sticky_id = s.id
            ORDER BY s.id DESC
            """;
        var notes = new List<StickyNote>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            notes.Add(new StickyNote(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        return notes;
    }

    // ---- デスクトップの付箋 ---------------------------------------------
    /// <summary>デスクトップに出している付箋。消された付箋の分は先に片付ける（v6 で消した場合など）。</summary>
    public IReadOnlyList<DesktopSticky> GetDesktopStickies()
    {
        Execute("DELETE FROM desktop_stickies WHERE sticky_id NOT IN (SELECT id FROM sticky_notes)");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sticky_id, x, y, width, height, topmost FROM desktop_stickies ORDER BY sticky_id";
        var items = new List<DesktopSticky>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(new DesktopSticky(reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5) != 0));
        return items;
    }

    /// <summary>デスクトップに出す（最初は壁紙のすぐ上、ほかの窓のうしろ）。すでに出ていれば何もしない。</summary>
    public void ShowStickyOnDesktop(long stickyId, double x, double y, double width, double height) =>
        Execute("""
            INSERT OR IGNORE INTO desktop_stickies (sticky_id, x, y, width, height, topmost)
            VALUES ($id, $x, $y, $w, $h, 0)
            """, ("$id", stickyId), ("$x", x), ("$y", y), ("$w", width), ("$h", height));

    public void SaveDesktopStickyBounds(long stickyId, double x, double y, double width, double height) =>
        Execute("UPDATE desktop_stickies SET x = $x, y = $y, width = $w, height = $h WHERE sticky_id = $id",
            ("$x", x), ("$y", y), ("$w", width), ("$h", height), ("$id", stickyId));

    public void SetDesktopStickyTopmost(long stickyId, bool topmost) =>
        Execute("UPDATE desktop_stickies SET topmost = $top WHERE sticky_id = $id",
            ("$top", topmost ? 1 : 0), ("$id", stickyId));

    /// <summary>デスクトップから外す（付箋そのものは消さない）。</summary>
    public void RemoveStickyFromDesktop(long stickyId) =>
        Execute("DELETE FROM desktop_stickies WHERE sticky_id = $id", ("$id", stickyId));

    // ---- 掲示板 -------------------------------------------------------
    /// <summary>貼ってあるもの。下にあるもの（z が小さい順）から。消されたメモ・付箋・タスクの分は先に片付ける（v6 で消した場合など）。</summary>
    public IReadOnlyList<BoardItem> GetBoardItems()
    {
        Execute("""
            DELETE FROM board_items
            WHERE (kind = 'sticky' AND ref_id NOT IN (SELECT id FROM sticky_notes))
               OR (kind = 'task' AND ref_id NOT IN (SELECT id FROM tasks))
               OR (kind = 'memo' AND ref_id NOT IN (SELECT id FROM memo_pages))
            """);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, ref_id, x, y, z FROM board_items ORDER BY z, id";
        var items = new List<BoardItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(new BoardItem(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetDouble(3), reader.GetDouble(4), reader.GetInt64(5)));
        return items;
    }

    /// <summary>いちばん上に貼る。すでに貼ってあれば何もしない。貼ったら true。</summary>
    public bool PinToBoard(string kind, long refId, double x, double y) =>
        Execute("""
            INSERT OR IGNORE INTO board_items (kind, ref_id, x, y, z)
            VALUES ($kind, $ref, $x, $y, (SELECT COALESCE(MAX(z), 0) + 1 FROM board_items))
            """, ("$kind", kind), ("$ref", refId), ("$x", x), ("$y", y)) > 0;

    /// <summary>動かした場所に置き直し、いちばん上に重ねる。</summary>
    public void MoveBoardItem(long id, double x, double y) =>
        Execute("""
            UPDATE board_items SET x = $x, y = $y, z = (SELECT COALESCE(MAX(z), 0) + 1 FROM board_items)
            WHERE id = $id
            """, ("$x", x), ("$y", y), ("$id", id));

    /// <summary>掲示板からはがす（付箋やタスクそのものは消さない）。</summary>
    public void UnpinBoardItem(long id) =>
        Execute("DELETE FROM board_items WHERE id = $id", ("$id", id));

    // ---- 長崎大学モード -------------------------------------------------
    /// <summary>時間割を丸ごと入れ替える。</summary>
    public void ReplaceTimetable(IEnumerable<ClassSlot> slots)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM nu_timetable";
            clear.ExecuteNonQuery();
        }
        foreach (var slot in slots)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO nu_timetable (weekday, period, title, detail, time_text, room, codes)
                VALUES ($day, $period, $title, $detail, $time, $room, $codes)
                """;
            command.Parameters.AddWithValue("$day", slot.Weekday);
            command.Parameters.AddWithValue("$period", slot.Period);
            command.Parameters.AddWithValue("$title", slot.Title);
            command.Parameters.AddWithValue("$detail", slot.Detail);
            command.Parameters.AddWithValue("$time", slot.TimeText);
            command.Parameters.AddWithValue("$room", slot.Room);
            command.Parameters.AddWithValue("$codes", slot.Codes);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void ClearTimetable() => Execute("DELETE FROM nu_timetable");

    /// <summary>時間割。weekday を指定すると、その曜日（1=月 〜 6=土）だけ。</summary>
    public IReadOnlyList<ClassSlot> GetTimetable(int? weekday = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT weekday, period, title, detail, time_text, room, codes FROM nu_timetable"
                              + (weekday is null ? "" : " WHERE weekday = $day")
                              + " ORDER BY weekday, period";
        if (weekday is { } day)
            command.Parameters.AddWithValue("$day", day);
        var slots = new List<ClassSlot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            slots.Add(new ClassSlot(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return slots;
    }

    /// <summary>覚えている教室（時間割コード → 教室）。checkedSince より前に読んだものは「読み直す」側に入れる。</summary>
    public (Dictionary<string, string> Rooms, HashSet<string> Fresh) GetRooms(DateTime checkedSince)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code, room, checked_at FROM nu_rooms";
        var rooms = new Dictionary<string, string>();
        var fresh = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rooms[reader.GetString(0)] = reader.GetString(1);
            if (DateTime.TryParse(reader.GetString(2), out var at) && at >= checkedSince)
                fresh.Add(reader.GetString(0));
        }
        return (rooms, fresh);
    }

    public void SaveRoom(string code, string room) =>
        Execute("INSERT OR REPLACE INTO nu_rooms (code, room, checked_at) VALUES ($code, $room, $now)",
            ("$code", code), ("$room", room), ("$now", Now));

    /// <summary>
    /// LACS の課題 1 件をタスクにする。初めてならタスクを足し、取り込み済みなら題名と締切だけ合わせる
    /// （優先度など、自分で変えたところはそのまま）。足したら true。
    /// LACS で提出済み（submitted）なら完了にする。未提出に戻っても、自分で付けた完了の印は外さない。
    /// 取り込んだあとに自分でタスクを消していたら、もう一度は足さない。
    /// </summary>
    public bool UpsertLacsTask(string itemId, string title, string dueDate, bool submitted)
    {
        var existing = Scalar("SELECT task_id FROM nu_lacs_tasks WHERE item_id = $item", ("$item", itemId));
        if (existing is not null)
        {
            var id = Convert.ToInt64(existing);
            Execute("UPDATE tasks SET title = $title, due_date = $due WHERE id = $id",
                ("$title", title), ("$due", dueDate), ("$id", id));
            if (submitted)
                SetTaskCompleted(id, true);
            return false;
        }
        var taskId = AddTask(title, "中", dueDate, "LACS");
        if (submitted)
            SetTaskCompleted(taskId, true);
        Execute("INSERT INTO nu_lacs_tasks (item_id, task_id) VALUES ($item, $task)", ("$item", itemId), ("$task", taskId));
        return true;
    }

    // ---- お知らせ（LACS の連絡事項と NU-Web の掲示） ------------------
    // id は出どころ（source）ごとに一意なので、表では「source:id」の形で持つ（LACS の分は前からの id のまま）
    private static string StoredId(string source, string id) => source == AnnouncementSource.Lacs ? id : $"{source}:{id}";

    /// <summary>
    /// 読んだお知らせを残す。新しいものは足し、残してあるものは題名と本文などだけ合わせる（読んだ印はそのまま）。
    /// その出どころを初めて読み込むとき（まだ 1 件も無いとき）は、前からあるものをすべて読んだことにする
    /// （いきなり何十件も新着にしない）。新しく足した数を返す。
    /// </summary>
    public int SaveAnnouncements(string source, IEnumerable<AnnouncementInput> announcements)
    {
        var first = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM nu_lacs_announcements WHERE source = $source",
            ("$source", source))) == 0;
        var added = 0;
        foreach (var a in announcements)
        {
            var id = StoredId(source, a.Id);
            var created = a.Created.ToString("yyyy-MM-ddTHH:mm:ss");
            var changed = Execute("""
                UPDATE nu_lacs_announcements
                SET title = $title, body = COALESCE($body, body), course_id = $courseId, course_name = $course,
                    created_at = $at, url = $url
                WHERE id = $id
                """, ("$id", id), ("$title", a.Title), ("$body", (object?)a.Body ?? DBNull.Value), ("$courseId", a.CourseId),
                ("$course", a.CourseName), ("$at", created), ("$url", a.Url));
            if (changed > 0 || a.Body is null)
                continue;
            Execute("""
                INSERT INTO nu_lacs_announcements (id, source, course_id, course_name, title, body, created_at, url, seen)
                VALUES ($id, $source, $courseId, $course, $title, $body, $at, $url, $seen)
                """, ("$id", id), ("$source", source), ("$courseId", a.CourseId), ("$course", a.CourseName),
                ("$title", a.Title), ("$body", a.Body), ("$at", created), ("$url", a.Url), ("$seen", first ? 1 : 0));
            added++;
        }
        return first ? 0 : added;
    }

    /// <summary>その出どころで残してあるお知らせの id（SaveAnnouncements に渡した形のまま）。</summary>
    public HashSet<string> AnnouncementIds(string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM nu_lacs_announcements WHERE source = $source";
        command.Parameters.AddWithValue("$source", source);
        var prefix = source == AnnouncementSource.Lacs ? "" : source + ":";
        var ids = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            ids.Add(id.StartsWith(prefix, StringComparison.Ordinal) ? id[prefix.Length..] : id);
        }
        return ids;
    }

    /// <summary>残してあるお知らせを、新しい順に。</summary>
    public IReadOnlyList<AnnouncementItem> GetAnnouncements(int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, course_id, course_name, title, body, created_at, url, seen FROM nu_lacs_announcements
            ORDER BY created_at DESC, id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var list = new List<AnnouncementItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            // 分類や差出人は 1 行で出す（前の版が残した改行や続いた空白は詰める）
            list.Add(new AnnouncementItem(reader.GetString(0), reader.GetString(1), OneLine(reader.GetString(2)),
                OneLine(reader.GetString(3)),
                reader.GetString(4), reader.GetString(5),
                DateTime.TryParse(reader.GetString(6), out var at) ? at : DateTime.MinValue,
                reader.GetString(7), reader.GetInt64(8) != 0));
        }
        return list;
    }

    private static string OneLine(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    /// <summary>まだ読んでいないお知らせの数。</summary>
    public int UnseenAnnouncementCount() =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM nu_lacs_announcements WHERE seen = 0"));

    /// <summary>お知らせ 1 件を読んだことにする（id が null なら、source の分すべて。source も null なら全部）。</summary>
    public void MarkAnnouncementSeen(string? id, string? source = null)
    {
        if (id is not null)
            Execute("UPDATE nu_lacs_announcements SET seen = 1 WHERE id = $id", ("$id", id));
        else if (source is not null)
            Execute("UPDATE nu_lacs_announcements SET seen = 1 WHERE seen = 0 AND source = $source", ("$source", source));
        else
            Execute("UPDATE nu_lacs_announcements SET seen = 1 WHERE seen = 0");
    }

    // ---- 作業記録 -----------------------------------------------------
    /// <summary>計った時間を記録する。5 秒未満は押し間違いとみなして記録しない（v6 と同じ）。</summary>
    public void AddSession(int durationSeconds, string mode)
    {
        if (durationSeconds < 5)
            return;
        Execute("INSERT INTO work_sessions (started_at, duration_seconds, mode) VALUES ($at, $seconds, $mode)",
            ("$at", Now), ("$seconds", durationSeconds), ("$mode", mode));
    }

    /// <summary>day（yyyy-MM-dd）以降の作業秒数。null なら累計。</summary>
    public int WorkSecondsSince(string? day = null)
    {
        var value = day is null
            ? Scalar("SELECT COALESCE(SUM(duration_seconds), 0) FROM work_sessions WHERE mode <> $break",
                ("$break", BreakMode))
            : Scalar("""
                SELECT COALESCE(SUM(duration_seconds), 0) FROM work_sessions
                WHERE mode <> $break AND substr(started_at, 1, 10) >= $day
                """, ("$break", BreakMode), ("$day", day));
        return Convert.ToInt32(value);
    }

    public int WorkSecondsToday() => WorkSecondsSince(Today);

    /// <summary>分析用: start（yyyy-MM-dd）以降の日ごとの作業秒数。キーは日付。</summary>
    public Dictionary<string, int> DailyTotals(string start) =>
        GroupedTotals("substr(started_at, 1, 10)", "AND substr(started_at, 1, 10) >= $start", ("$start", start));

    /// <summary>分析用: start（yyyy-MM）以降の月ごとの作業秒数。キーは yyyy-MM。</summary>
    public Dictionary<string, int> MonthlyTotals(string startMonth) =>
        GroupedTotals("substr(started_at, 1, 7)", "AND substr(started_at, 1, 7) >= $start", ("$start", startMonth));

    /// <summary>分析用: 曜日ごとの作業秒数（累計）。キーは "0"（日）〜 "6"（土）。</summary>
    public Dictionary<string, int> WeekdayTotals() => GroupedTotals("strftime('%w', started_at)", "");

    private Dictionary<string, int> GroupedTotals(string key, string extraWhere, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {key}, SUM(duration_seconds) FROM work_sessions
            WHERE mode <> $break {extraWhere}
            GROUP BY {key}
            """;
        command.Parameters.AddWithValue("$break", BreakMode);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        var totals = new Dictionary<string, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                totals[reader.GetString(0)] = reader.GetInt32(1);
        }
        return totals;
    }

    /// <summary>CSV 書き出し用: 作業記録をすべて古い順に。</summary>
    public IReadOnlyList<(string StartedAt, int Seconds, string Mode)> SessionRows()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT started_at, duration_seconds, mode FROM work_sessions ORDER BY started_at";
        var rows = new List<(string, int, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        return rows;
    }

    /// <summary>CSV 書き出し用: タスクをすべて。</summary>
    public IReadOnlyList<TaskItem> AllTasks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TaskColumns} FROM tasks ORDER BY id";
        return ReadTasks(command);
    }
}
