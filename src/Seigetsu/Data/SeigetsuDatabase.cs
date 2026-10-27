using Microsoft.Data.Sqlite;

namespace Seigetsu.Data;

public sealed record TaskItem(
    long Id, string Title, bool Completed, string Priority, string DueDate, string Category, bool Favorite);

public sealed record StickyNote(long Id, string Title, string Body, string CreatedAt);

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
        Run("""
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL)
            """);
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
    public void AddTask(string title, string priority = "中", string dueDate = "", string category = "一般") =>
        Execute("""
            INSERT INTO tasks (title, completed, created_at, priority, due_date, category, favorite)
            VALUES ($title, 0, $now, $priority, $due, $category, 0)
            """,
            ("$title", title), ("$now", Now), ("$priority", priority), ("$due", dueDate), ("$category", category));

    public void UpdateTask(long id, string title, string priority, string dueDate, string category) =>
        Execute("UPDATE tasks SET title = $title, priority = $priority, due_date = $due, category = $category WHERE id = $id",
            ("$title", title), ("$priority", priority), ("$due", dueDate), ("$category", category), ("$id", id));

    public void SetTaskCompleted(long id, bool completed) =>
        Execute("UPDATE tasks SET completed = $done WHERE id = $id", ("$done", completed ? 1 : 0), ("$id", id));

    public void ToggleFavorite(long id) =>
        Execute("UPDATE tasks SET favorite = 1 - favorite WHERE id = $id", ("$id", id));

    public void DeleteTask(long id) =>
        Execute("DELETE FROM tasks WHERE id = $id", ("$id", id));

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

    // ---- メモ（1 つだけ。id = 1 の行） -----------------------------------
    public string GetNote() => Scalar("SELECT body FROM notes WHERE id = 1") as string ?? "";

    public void SaveNote(string body) =>
        Execute("UPDATE notes SET body = $body WHERE id = 1", ("$body", body));

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
    public void AddSticky(string title, string body) =>
        Execute("INSERT INTO sticky_notes (title, body, created_at) VALUES ($title, $body, $now)",
            ("$title", title), ("$body", body), ("$now", Now));

    public void UpdateSticky(long id, string title, string body) =>
        Execute("UPDATE sticky_notes SET title = $title, body = $body WHERE id = $id",
            ("$title", title), ("$body", body), ("$id", id));

    public void DeleteSticky(long id) =>
        Execute("DELETE FROM sticky_notes WHERE id = $id", ("$id", id));

    /// <summary>新しい順（v6 と同じ）。</summary>
    public IReadOnlyList<StickyNote> GetStickies()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, body, created_at FROM sticky_notes ORDER BY id DESC";
        var notes = new List<StickyNote>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            notes.Add(new StickyNote(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return notes;
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
