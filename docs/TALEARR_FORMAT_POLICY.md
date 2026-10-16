# TaleArr format policy

Settings > Indexers > **Format policy** decides which format TaleArr grabs for a book it wants:

| Policy | What TaleArr grabs |
|---|---|
| **Both** (default) | Every monitored format, each on its own. Upstream behaviour. |
| **Prefer audiobook, accept ebook, upgrade to audio later** | The audiobook. The ebook only once the audiobook has been searched and has been missing for the fallback time (24 hours by default), and not while the audiobook is downloaded or downloading. The audiobook stays wanted, so when it appears later it is grabbed; the ebook is kept. |
| **Audiobook only** | Audiobooks. Ebook releases are skipped. |
| **Ebook only** | Ebooks. Audiobook releases are skipped. |

There is a default for the whole library and an override per author (Per author > Add author).
"Use default" removes an override.

## How it works with per-format monitoring

Monitoring is unchanged: each book has an audiobook entry and an ebook entry, each monitored or
not, and the author has an audiobook and an ebook switch. Monitoring decides **which books are
wanted in which format**. The policy only filters the grabs:

| Monitored | Both | Prefer audiobook | Audiobook only | Ebook only |
|---|---|---|---|---|
| Audiobook only | audiobook | audiobook | audiobook | nothing |
| Ebook only | ebook | ebook (no audiobook is wanted, so nothing to wait for) | nothing | ebook |
| Both | both | audiobook; ebook after the fallback time if the audiobook is still missing | audiobook | ebook |

So for "prefer audiobook" monitor both formats of the books you want: the audiobook entry drives
the search and the later upgrade to audio, the ebook entry is the fallback. Nothing is
unmonitored or deleted by the policy, and a policy never adds monitoring.

The check runs in the decision engine for every release (RSS, automatic and interactive search).
It is a soft filter: interactive search shows a skipped release with the label "Format policy"
and the reason (for example "waiting for the audiobook until 2026-10-17 12:00 UTC before
accepting the ebook"), and you can still grab it there. The fallback time counts from when the
audiobook entry was added, and the audiobook must have been searched at least once. Additional
recordings (TaleArr recordings) are not counted as the book's audiobook.

## Settings and storage

| Setting | Config row |
|---|---|
| Default | `talearr.formatpolicy.default` (`Both`, `PreferAudio`, `AudioOnly`, `EbookOnly`) |
| Fallback after | `talearr.formatpolicy.fallbackhours` (0 to 2160) |
| Per author | `talearr.formatpolicy.authors` (JSON, author id to policy) |

No schema change, no migrations, no `config.xml` edits, no provider types.

## API

| Route | What it does |
|---|---|
| `GET /api/v1/config/talearrformatpolicy` | `{"default": "preferAudio", "fallbackHours": 24, "authors": [{"authorId": 4, "authorName": "John Grisham", "policy": "audioOnly"}]}` |
| `PUT /api/v1/config/talearrformatpolicy/1` | Saves the same shape. |
| `GET /api/v1/config/talearrformatpolicy/author/{authorId}` | The policy that applies to one author. |

## Rolling back to Chaptarr

Chaptarr ignores the `talearr.formatpolicy.*` rows and has no format policy check, so it grabs
every monitored format again (the "Both" behaviour). Monitoring is untouched by the policy, so
nothing else changes. If you used "prefer audiobook" with both formats monitored, expect
Chaptarr to grab the waiting ebooks.

## Series monitoring and wishlists

These were built on separate branches. Once merged, a series rule or wishlist that monitors both
formats under "prefer audiobook" gets the audiobook first and the ebook as the fallback, because
the policy filters the grabs of every monitored entry, whatever monitored it.
