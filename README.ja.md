# AppWatcher

日本語 | [English README](README.md)

AppWatcher は、Windows 上で常時稼働させたいデスクトップアプリを監視し、異常終了や応答停止時に自動復旧するための軽量な監視ツールです。

> 現在の状態: **開発中のalpha版**。通常権限・管理者権限アプリの監視、日本語/英語UI、完全終了/再起動、重複登録防止、通常UIと管理者Helper間のIPCなどを実装済みです。beta移行前の最終検証を進めています。

## 設計方針

- プロセス終了を検知して自動再起動する。
- 必要に応じてGUIの応答なしを検知して復旧する。
- 通常権限アプリと管理者権限アプリを1つのダッシュボードで管理する。
- メンテナンス時に監視を簡単に一時停止できる。
- 「何が起きたか」「AppWatcherが何を判断したか」「なぜそう判断したか」をログに残す。
- 常駐部分はイベント駆動を基本とし、CPU負荷をできるだけ小さくする。
- 監視のためだけに対象アプリの実行環境を変えない。
- 対象GUIアプリをSession 0へ移動しない。
- 対象アプリをWindows Job Objectへ強制的に入れない。
- 子プロセスをデフォルトでは管理・終了しない。

他のGUI子プロセスを起動する既存Windowsアプリとの互換性を重視しています。AppWatcher経由で起動しても、エクスプローラーから普通に起動した場合に近い挙動になることを目標にしています。

## コンポーネント

| コンポーネント | 役割 |
| --- | --- |
| `AppWatcher.Agent.exe` | 通常権限アプリを監視する常駐ホスト。トレイアイコンも担当します。 |
| `AppWatcher.Elevated.exe` | 管理者権限アプリを監視する常駐Helper。画面は表示しません。 |
| `AppWatcher.UI.exe` | ダッシュボード、設定編集、イベントログ、診断機能。必要なときだけ開きます。 |
| `AppWatcher.Core.dll` | 監視、設定、ログ、IPCなどの共通ロジック。 |

Agent と Elevated Helper は、どちらも**ログオン中ユーザーの対話型デスクトップセッション**で動作します。Windowsサービスではありません。

## 主な機能

- 実行ファイルのフルパスを使ったプロセス識別。
- 既に起動しているプロセスへのAttach。
- `Process.Exited` を使ったイベント駆動の終了検知。
- 予期しない終了からの自動再起動。
- GUI応答なし検知（既定は記録のみ。アプリ単位で「強制終了して再起動」も選択可）。
- 再起動ループ防止とBackoff。
- アプリ単位の監視一時停止。
- 全体メンテナンスモード。
- 通常権限 / 管理者権限の統合ダッシュボード。
- JSON設定ファイルと3世代バックアップ。
- SQLiteイベント履歴。
- Reason Code付きの詳細ログ。
- 診断ZIP作成。
- 自動起動タスクの未登録・無効・古いパスを検出する起動セルフチェック。
- 実行中アプリ一覧からの追加と通常権限 / 管理者権限の自動判定。
- 安定したアプリIDで絞り込むアプリ単位イベントログ。
- バージョン付き設定Schemaの自動移行と新しいSchemaの安全な拒否。
- 日本語 / English UI。
- 同じ実行ファイルパスの重複登録防止。
- AppWatcher自身の完全終了 / 再起動。
- AgentとElevatedの相互監視（片方が異常終了したら、もう片方が登録済みタスクから起動し直す）。
- 自動再起動・Backoff・応答なしなどのトレイ通知（設定画面で無効化可）。

## ダウンロードと更新

[GitHub Releases](https://github.com/hals5412/AppWatcher/releases)から使用するalphaプレリリースを選びます。どちらもWindows x64向けです。

| ファイル | 必要な環境 |
| --- | --- |
| `AppWatcher-win-x64.zip` | .NET 10 Desktop Runtime（x64）を別途インストール。 |
| `AppWatcher-win-x64-self-contained.zip` | .NETランタイム同梱。ダウンロード容量は大きくなります。 |
| `SHA256SUMS.txt` | 両ZIPのSHA-256チェックサム。 |

ZIP全体を1つのフォルダへ展開し、`AppWatcher.UI.exe`を通常権限で起動します。更新時は先に **AppWatcherを完全終了** し、Agent・Elevated・UI・Coreが同じバージョンになるようアプリ一式を入れ替えてください。監視対象アプリは終了しません。設定とログは後述の別データフォルダに残ります。設置フォルダを変更した場合は、自動起動タスクを再インストール／修復してください。

## 初回起動

ユーザーが起動する入口は **`AppWatcher.UI.exe`** です。UIは通常権限で起動してください。

通常は `AppWatcher.Agent.exe` や `AppWatcher.Elevated.exe` を直接起動する必要はありません。

管理者権限アプリも監視する場合は、初回に一度だけ **ツール → 自動起動タスクをインストール／修復...** を実行し、UACを承認します。

## 「自動起動タスクをインストール／修復...」が行うこと

Windowsタスクスケジューラの `\AppWatcher` フォルダに、現在のWindowsユーザー向けの2つのタスクを作成または更新します。

| タスク | 実行権限 | 用途 |
| --- | --- | --- |
| `Agent` | 通常権限 | 通常アプリの監視 |
| `Elevated` | 最上位権限 | 管理者権限アプリの監視 |

両方とも**現在のWindowsユーザーのログオン時**に起動し、ログオン中の対話型デスクトップセッションでのみ動作します。

これにより、

- Windowsログオン後に監視を自動開始できる。
- 管理者権限Helperを毎回UAC確認なしで起動できる。
- 管理者権限アプリをAppWatcherから自動再起動できる。
- Windowsサービス化によるSession 0問題を避けられる。

という構成になります。

インストール処理ではAppWatcherのファイルを別の場所へコピーしません。タスクの実行先として**現在のAppWatcherフォルダ内のexeパス**を登録します。そのため、AppWatcherフォルダを移動した場合や別フォルダの新ビルドへ入れ替えた場合は、もう一度「自動起動タスクをインストール／修復...」を実行してください。

登録後は `Agent` と `Elevated` のタスクをその場で起動します。

現在の実装では、タスクに以下も設定しています。

- `Agent`: InteractiveToken / 通常権限
- `Elevated`: InteractiveToken / Highest
- ログオン時トリガー
- バッテリー駆動でも停止しない
- 実行時間制限なし
- 多重起動時は新しいインスタンスを作らない
- 優先度は「通常」（Task Schedulerの既定値では低優先度になり、再起動したアプリにも引き継がれるため）。alpha.22以前に登録したタスクはセルフチェックで警告されるので、「自動起動タスクをインストール／修復...」を再実行してください。

## 管理者権限アプリの監視

管理者権限が必要なアプリを監視する場合は、アプリ設定で以下を指定します。

- 実行権限: 管理者
- 監視を有効にする: ON
- AppWatcher起動時に自動起動する: ON
- 既に起動しているプロセスに接続: ON

正常時はダッシュボード上部にAgentと管理者HelperのPID・稼働時間が表示され、対象アプリは「正常 / 管理者」と表示されます。

## GUI子プロセスとの互換性

AppWatcherは対象アプリ起動時に `CREATE_NO_WINDOW`、`DETACHED_PROCESS`、非表示デスクトップ、Windowsサービス、Job Objectへの強制所属をデフォルトでは使用しません。

子プロセスの既定ポリシーも `Unmanaged` です。監視対象アプリから別のGUIアプリが起動された場合も、AppWatcherはその子プロセスをデフォルトでは所有・終了しません。

## データ保存場所

データは次のフォルダへ保存します。

    %LOCALAPPDATA%\AppWatcher\

主なファイル:

    config.json
    config.backup-1.json
    config.backup-2.json
    config.backup-3.json
    events.db
    events.db-wal
    events.db-shm
    appwatcher-fallback.log

## AppWatcherの終了と再起動

ダッシュボードを閉じるだけでは監視は終了しません。AppWatcher全体を停止する場合はトレイまたはダッシュボードから **AppWatcherを完全終了** を使用します。

この操作では監視対象アプリ自体は終了しません。

コマンドラインからも利用できます。

    AppWatcher.UI.exe --shutdown
    AppWatcher.UI.exe --restart

## ビルド

    dotnet restore .\AppWatcher.sln
    dotnet build .\AppWatcher.sln -c Release

実行ファイル一式をまとめる場合:

    .\scripts\publish.ps1

self-containedのwin-x64版:

    .\scripts\publish.ps1 -SelfContained -PackageSuffix "-self-contained"

## 現在のalpha版の制限

- 時間指定Pause / Maintenanceの状態は現在ホストメモリ上にあり、異常終了後の永続化は未実装です。
- `ChildProcessPolicy.TrackOnly` / `StopWithParent` は将来用です。
- TCP / HTTPヘルスチェックは未実装です。
- 対象アプリごとのCPU/RAM履歴収集はまだ行いません。
- 自動アップデート機能は未実装です。
- 言語変更後はAgent / UIの再起動が必要です。

詳細設計は [specification](docs/specification.md)、[architecture](docs/architecture.md)、[manual test plan](docs/manual-test-plan.md) を参照してください。

## 信頼性とログの運用

- 同一ホストの稼働中は、設定再読み込み・Pause解除・メンテナンス解除によって手動Stopを解除しません。再起動する場合はStartまたはRestartを明示します。
- 設定再読み込みは変更された対象へ差分適用します。実行ファイルや権限の変更前は対象を明示的に停止してください。権限変更先でも停止状態を維持します。
- Pauseと手動Stopの永続化は未対応です。AppWatcher自体を再起動した後は、起動設定が再評価されます。
- トレイとダッシュボードの全体Pause／Resumeは両ホストへ送信し、片側が失敗した場合は通知します。
- トレイアイコンは管理者権限アプリの状態も含めて表示します。管理者権限アプリがあるのに管理者Helperへ接続できない場合も異常として扱います。
- AgentとElevatedは30秒ごとに互いの応答を確認し、2回続けて応答がなければ登録済みタスクから相手を起動し直します。一度も応答を確認できていない相手は起動せず、1時間に3回までに制限します。
- 捕捉されなかった例外はfallbackログへ記録します。
- `AttachExisting`が有効な対象は、AppWatcher起動後に外部から起動された場合も約5秒以内に検出して監視へ接続します。未接続の対象だけを実行ファイル名で照合し、手動Stop中は自動接続しません。
- 設定はプロセス間ファイルロックと最新値への部分更新で保存します。ロック待ちが10秒を超えた場合は保存に失敗し、再試行は利用者が行います。
- ログDBの障害は監視を停止させません。ログは最大1024件のキューで処理し、満杯時は超過件数をfallbackへ記録します。障害時のDB再試行は1分以上間隔を空けます。
- 起動後と1時間ごとに古いログを整理します。`global.eventDatabaseMaxMegabytes`は既定100 MiB、最小10 MiBの整理目標です。容量超過時は古いイベントを削除し、必要に応じてDBを縮小します。厳密な瞬間上限ではなく、WALやロック競合により一時超過・縮小延期があります。
- fallbackログは5 MiBで回転し、現在分と旧2世代を保持します。
- 診断ZIPは設定・ログ中の`password`、`passwd`、`token`、`api-key`、`api_key`、`secret`形式の引数をマスクします。過去のログDBも新規DBへマスクして書き出します。任意形式の秘密情報の完全除去は保証しません。
- 自動テストは独立プロジェクトです。テストランナーと補助プロセスは配布ZIPへ含めません。

詳しくは[信頼性改善計画](docs/AppWatcher-reliability-improvement-plan.md)と[実装・検証報告](docs/AppWatcher-reliability-implementation-report.md)を参照してください。隔離Windows環境での実UI・通常権限／管理者権限の組合せ確認と、変更前後のリソース消費実測は未実施です。
