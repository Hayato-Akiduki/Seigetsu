# 霽月 - Seigetsu（C# 版 v7・開発中）

v6（Python 版、`C:\Claude\Seigetsu`）を C# の WPF で作り直しているものです。

## 動かし方

```bat
dotnet run --project src\Seigetsu
```

## データ

v6 と同じ `%APPDATA%\akidesk\seigetsu.db` を読み書きします。テーブルの形も v6 と同じなので、v6 と v7 のどちらで開いても同じデータが見えます。

v7 を初めて起動したとき、それまでのデータを `seigetsu.db.before-v7` として同じフォルダに一度だけコピーします。
v7 で何かおかしくなったら、このファイルを `seigetsu.db` に戻せば元の状態に戻ります。

テスト用に別のデータで起動したいときは、v6 と同じ環境変数 `SEIGETSU_DATA_DIR` に保存先フォルダを指定します。
予期しないエラーは、同じフォルダの `error-v7.log` に記録されます。

## インストーラーと自動更新

[Velopack](https://velopack.io) で、インストーラー（`Setup.exe`）と自動更新を作っています。

- インストール先は `%LocalAppData%\Seigetsu`（管理者の権限はいらない）。スタートメニューとデスクトップにショートカットができる。
- データは `%APPDATA%\akidesk` なので、更新やアンインストールでは消えない。
- 起動して 20 秒後と、その後 4 時間ごとに新しい版を確認する。あれば裏でダウンロードして、アプリを閉じたときに入れ替える。設定画面の「今すぐ更新して再起動」でもすぐ入れ替えられる。
- `dotnet run` で起動したとき（インストールしていないとき）は、自動更新は動かない。

### インストーラーを作る

```bat
powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 7.0.0
```

`Releases` フォルダに `Seigetsu-win-Setup.exe` などができます（このフォルダは git に入れません）。

### GitHub Releases に公開する（初めてのとき）

1. GitHub で **公開（Public）** のリポジトリを作る。利用者はここから更新を受け取るので、公開にしておく必要がある。
2. `src\Seigetsu\Seigetsu.csproj` の `UpdateRepositoryUrl` に、そのリポジトリの URL を書く（いまは `https://github.com/Hayato-Akiduki/Seigetsu`）。
3. GitHub の Settings → Developer settings → Personal access tokens → **Fine-grained tokens** で、そのリポジトリだけに **Contents: Read and write** の権限を付けたトークンを作る。
4. PowerShell でトークンを環境変数に入れてから公開する（トークンはファイルに書かない）。

```powershell
$env:GITHUB_TOKEN = "（作ったトークン）"
.\build-release.ps1 -Version 7.0.0 -Upload
```

配るときは、次のリンクを渡せば、いつでも最新のインストーラーが落とせます。
`https://github.com/Hayato-Akiduki/Seigetsu/releases/latest/download/Seigetsu-win-Setup.exe`

### 新しい版を出すとき

`-Version` の番号を前より大きくして（例: `7.0.1`）、同じように `-Upload` を付けて実行する。
前の版との差分ファイルも自動で作られるので、利用者のダウンロードは小さく済む。

### コード署名（まだしていない）

署名していないので、初めてインストールする人には Windows の SmartScreen の警告が出ます（「詳細情報」→「実行」で進める）。
署名するときは、`build-release.ps1` の `vpk pack` に `--signParams`（証明書）か `--azureTrustedSignFile`（Azure Trusted Signing）を足します。

## 使っているもの

- .NET 10 / WPF
- [WPF-UI](https://github.com/lepoco/wpfui)：Windows 11 風の部品とアイコン
- Microsoft.Data.Sqlite：SQLite データベース
- [NAudio](https://github.com/naudio/NAudio) / NAudio.Vorbis：BGM の再生（mp3 / wav / ogg）。どちらも MIT ライセンス
- [Velopack](https://velopack.io)：インストーラーと自動更新。MIT ライセンス

## フォルダ構成

```
src/Seigetsu/
  App.xaml(.cs)          起動処理と共通の色・スタイル
  MainWindow.xaml(.cs)   左メニューと画面の切り替え
  Data/                  保存先のパスとデータベース
  Services/              外観の切り替え、集中タイマー、BGM の再生、CSV の書き出し、タスクトレイ、1 つだけ起動
  Pages/                 10 の画面（ホーム・タスク・タイマー・メモ・カレンダー・分析・付箋・掲示板・BGM・設定）
  Dialogs/               入力画面（タスク・付箋の追加と編集）
  Controls/              共通の部品（棒グラフ）
  Assets/                アイコン
```

## 進み具合

- [x] 土台（ウィンドウ、左メニュー、外観、データベースの読み込み、ホーム）
- [x] タスク（追加・編集・完了・お気に入り・削除・検索・絞り込み、`Ctrl+N`）
- [x] 集中タイマー（ストップウォッチ・ポモドーロ、左メニュー下の小さなタイマー、終了時の記録）
- [x] メモ（自動保存、画面を移るとき・閉じるときの保存、文字数、TXT 書き出し）
- [x] 付箋（追加・編集・削除、4 色、`Ctrl+Enter` で保存）
- [x] カレンダー（月表示、締切の印、選んだ日のタスク、この日にタスクを追加）
- [x] 分析（合計のタイル、週間・曜日別・月間の棒グラフ、表で見る、CSV 書き出し）
- [x] 左メニューの開閉（左上のボタンか `Ctrl+B`。閉じるとアイコンだけになり、次に起動したときも同じ状態）と、明るい／暗い外観への対応
- [x] 時計の大画面表示（ホームの時刻かボタン、または `F11` で開く。日付・秒・動いている集中タイマーも出す。表示中は画面が暗くならない。`Esc`・ダブルクリック・`F11` で閉じる）
- [x] 付箋をデスクトップに表示（付箋画面のカード右上のボタンか、掲示板の付箋の右クリックから。いちばん手前に固定・解除、ドラッグで移動、ふちで大きさ変更、ダブルクリックで編集。霽月をタスクトレイにしまっても出たまま、次に起動したときも同じ場所に出る。`desktop_stickies` の表に保存）
- [x] 掲示板（メモ・付箋・タスクを好きな場所に貼る、ドラッグで移動、重なり順、ダブルクリックで開く、タスクの完了、新しく作って貼る。`Ctrl+8`。v7 だけの機能で、`board_items` の表に保存）
- [x] BGM（プレイリスト、前後の曲、リピート・シャッフル、音量、ドラッグ＆ドロップで追加、左下に曲名）
- [x] 設定（外観、ポモドーロの長さ、× ボタンの動き、CSV 書き出し、データのフォルダを開く、このアプリについて）
- [x] タスクトレイ（開く・終了、× ボタンでタスクトレイに格納、ポモドーロの終わりを Windows の通知で知らせる）
- [x] 1 つだけ起動（動いているときにもう一度起動すると、動いている方の窓が前に出る）
- [ ] v6 にあってまだ移していないもの：ホームの背景画像、天気、スクリーンショット、電卓、文字数カウンター
- [x] インストーラーと自動更新の仕組み（Velopack）
- [ ] GitHub Releases への初めての公開（リポジトリとトークンの用意が必要）
- [ ] コード署名
