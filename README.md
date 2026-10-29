# nexco-mcp

NEXCO 西日本の [高速料金・ルート検索](https://search.w-nexco.co.jp/) で、高速道路の料金・ルート・所要時間を調べる、ローカル用の MCP サーバー（stdio）。
NEXCO 西日本の管轄に限らず、NEXCO 東日本・中日本や首都高・阪神高速などを含む全国の IC 間を調べられる。

> [!IMPORTANT]
> **非公式**のツールです。西日本高速道路株式会社とは関係がありません。
> 公開された API ではなく、サイトの画面と同じやり方で検索しているので、サイトが改修されると動かなくなることがあります。
> 料金はサイトの計算結果をそのまま返します。正式な料金はサイトや料金所で確かめてください。

## ツール

| ツール | 内容 |
|---|---|
| `search_highway_route` | IC から IC までのルートを探す。ルートごとに通常料金（現金）・ETC・ETC2.0 料金、距離、所要時間（渋滞予測込みも）、出発/到着時刻、通る道路と、料金所ごとの料金・ETC 割引（休日・深夜など）、IC・JCT の通過時刻を返す |
| `search_ics` | IC を名前・読みで探す。IC コード、都道府県、道路、出入りの方向の制限（注意書き） |
| `search_roads` | 道路名を探す（`avoid_roads` に渡す名前） |

`search_highway_route` の主な引数:

| 引数 | 既定 | 内容 |
|---|---|---|
| `from` / `to` | （必須） | IC 名（「IC」は付けても付けなくてもよい）または IC コード。同名の IC が複数あると候補を返す |
| `via` | なし | 通過 IC（最大 3 つ） |
| `avoid_roads` | なし | 通らない道路（最大 2 つ。例: 中国道, 新名神, 一般道） |
| `date` / `time` | 今（日本時間） | `YYYY-MM-DD` / `HH:MM`。ETC 割引はこの日時で決まる |
| `time_type` | `departure` | `departure`（出発）, `arrival`（到着） |
| `car_type` | `normal` | `kei`（軽・自動二輪）, `normal`（普通車）, `medium`（中型車）, `large`（大型車）, `extra_large`（特大車） |
| `sort` | `time` | `time`（時間順）, `distance`（距離順）, `toll`（通常料金順）, `etc_toll`（ETC 料金順） |
| `count` | `3` | 返すルートの数（1〜5） |
| `include_sapa` | `false` | 区間ごとの SA・PA と通過時刻も返す |

IC の一覧はサイトから読み、12 時間覚えておく。

## 使い方

[mise](https://mise.jdx.dev/) で .NET 10 を入れて、単一バイナリを作る。

```bash
mise run publish
```

Claude Code に登録する。

```bash
claude mcp add --scope user nexco -- "$PWD/dist/nexco-mcp"
```

環境変数 `NEXCO_BASE_URL` で接続先を変えられる（テスト用）。
