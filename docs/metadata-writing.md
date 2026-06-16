# Writing metadata to video files — research (#18)

Status: **research only.** No code yet. This lays out the options, the standards,
and a recommendation so we can pick a direction before building.

The ask (from the issue + discussion): let the app **write its metadata into the
video files** (or alongside them). Two hard problems fall out of that:

1. Writing bytes into a file **changes its MD5**, and we use MD5 for duplicate
   detection — so we'd need to track MD5 history (or rethink what we hash).
2. **Where** should the metadata live — embedded in the file, or in a sidecar?
3. **Which fields/standard** should we use so the metadata is portable and
   readable by other tools?

---

## 1. The constraints that shape this (from our codebase)

These are the facts the design has to respect — see the inline references.

- **MD5 is a whole-file hash.** `Md5BackfillService.ComputeMd5Async`
  (`src/VideoOrganizer.API/Services/Md5BackfillService.cs`) streams the **entire
  file** through `MD5`. So *any* byte change — including a metadata edit —
  produces a different hash. Hashing is deferred (a background worker), and
  `Video.Md5` is nullable until computed.
- **MD5 drives dedup, not a DB constraint.** The index on `Video.Md5`
  (`VideoConfiguration.cs`) is **non-unique**. When the worker computes a hash
  and finds another video with the same one, it re-flags the row
  `NeedsReview = true` and appends a note. Path uniqueness is enforced separately
  by the partial unique index on `(FilePath, FileName) WHERE ParentVideoId IS NULL`.
  So a stale MD5 doesn't corrupt the schema — it silently **breaks duplicate
  detection** and can produce a spurious "duplicate" review flag.
- **Originals are treated as sacred.** Files live under VideoSet roots (e.g.
  `/mnt/Stuff`) and are addressed by path. The only writes we do today are
  **user-initiated moves** (issue #4), which are logged reversibly in
  `FileMoveLog`. We never remux or edit file bytes. The app reads files; it
  writes *side-channel* artifacts (thumbnails, OCR rows) **outside** the source
  tree. Re-encodes from the Mac are HEVC in `.mp4`/`.mov`; there are also `.mkv`
  and others (`VideoFileExtensions`).
- **Clips share the parent's file.** A clip is a `Video` row with
  `ParentVideoId` set and the **same `FilePath`** as its parent. Writing into a
  parent's file therefore changes the bytes under every clip of it.
- **We already shell out to ffmpeg/ffprobe** (Xabe.FFmpeg for probe;
  raw ffmpeg in `ThumbnailGenerator`/the OCR scanner). Adding metadata writes is
  not a new kind of dependency.
- **We have a rich metadata model to export:** `Video` fields (`Notes`,
  `CameraType`, `VideoQuality`, `IsFavorite`, `CreationTime`…), the tag system
  (`TagGroup`/`Tag`, with aliases), and the custom-property system
  (`PropertyDefinition` + `VideoPropertyValue`, typed Text/Number/Date/Bool/Url).

---

## 2. Decision A — where does the metadata live?

Three shapes. They're not mutually exclusive; the DB stays the source of truth in
all of them.

### Option A1 — Sidecar files (recommended starting point)
Write a companion file next to the video: `myclip.mp4` → `myclip.nfo` (Kodi/
Jellyfin XML) and/or `myclip.xmp` (Adobe XMP) and/or `myclip.json`.

- **Originals never change** → **MD5 is never invalidated. No MD5 history needed
  at all.** This alone resolves problem #1.
- Portable & standard: Jellyfin/Kodi read `.nfo` natively and local `.nfo`
  *overrides* online providers; Adobe/DAM tools read `.xmp`.
- Round-trips losslessly — no container quirks, no field-length limits, no
  re-mux.
- Clips are fine — a sidecar per row, or one per file.
- **Cons:** sidecars can get separated from the video on copy/move (we'd move
  them together, like we already move files); the metadata isn't *inside* the
  file, so a player that only reads embedded tags won't see it.

### Option A2 — Embedded in the file
Write tags into the container itself (MP4 atoms / Matroska tags).

- **Pro:** travels *with* the file anywhere; visible to players/Finder/Explorer.
- **Cons:** mutates the original (against our "files are sacred" posture),
  **changes the MD5**, changes mtime, and for MP4 typically requires a **full
  remux** (rewrite the whole file). This is where problem #1 and #2 bite.

### Option A3 — DB is truth, export on demand
Keep everything in Postgres (as now); add an **export** action that writes a
sidecar and/or embeds — per video or in bulk — and an **import** that reads them
back. Best of both: no behavioral change unless the user asks to materialize.

> **Recommendation:** start with **A1 sidecar (`.nfo` + optional `.xmp`)** as the
> default, structured as **A3** (DB stays truth; "Write metadata to file(s)" is an
> explicit export action). Add **A2 embedding as an opt-in** later for users who
> want tags inside the file. This sidesteps the MD5 problem for the common case
> and keeps originals untouched by default.

---

## 3. Decision B — the MD5 problem (only bites if we embed)

If we embed (A2), the file's MD5 changes. There are three ways to handle it; they
can be combined.

### B1 — Hash the *content*, not the *file* (the real fix)
ffmpeg can hash the **decoded stream** rather than the container bytes:

```
ffmpeg -i input.mp4 -map 0 -c copy -f streamhash -hash md5 -
```

`streamhash` (and `framemd5`) compute a checksum over the **media payload**,
which is **stable across metadata edits and container remuxes** — change the
title tag and the stream hash is identical. If duplicate detection keyed on a
*content hash* instead of a *whole-file* hash, **embedding metadata would not
break dedup at all**, and we'd also catch "same video, different container/
remux" duplicates that a file hash misses today.

- **Pro:** the most correct long-term answer; makes dedup robust to *any* repack.
- **Cons:** a migration (compute content hashes for the library); audio frame
  alignment needs care (`framemd5` notes), and it's a bigger conceptual change
  to the dedup feature. Could be introduced as an **additional** `Video.ContentMd5`
  column alongside the existing file `Md5`.

### B2 — MD5 history table (if we keep file-hash dedup *and* embed)
Add `OcrTextLine`-style history: `Md5History { Id, VideoId, Md5, Algorithm,
ComputedAt, Reason }`. On a metadata write: recompute the file MD5, push the old
one into history, store the new current hash. Dedup checks current hashes;
history lets us recognize a file we ourselves rewrote (e.g. on re-import) and
audit changes.

- **Pro:** minimal change to existing dedup; full audit trail.
- **Cons:** doesn't make dedup smarter (two different remuxes of the same video
  still look distinct); pure bookkeeping around a self-inflicted problem.

### B3 — Re-hash in place, no history
Simplest: after writing, recompute and overwrite `Video.Md5` (or set it null so
the worker re-hashes). No history, no dedup intelligence — just "keep the stored
hash truthful."

> **Recommendation:** if/when we add embedding, do **B1 (content hash as a new
> `ContentMd5` column)** as the principled fix, and keep **B3** for the file hash
> so the stored value stays truthful. **B2 (history)** only earns its keep if you
> specifically want an audit log of file rewrites — worth noting it's *only*
> needed because embedding mutates files; **the sidecar default (A1) makes the
> entire MD5 problem disappear.**

---

## 4. Decision C — which fields / standard?

There's no single universal video-metadata standard; these are the real ones and
how our data maps onto them.

| Standard | What it is | Where it's used |
|---|---|---|
| **Kodi/Jellyfin NFO** | Pragmatic XML sidecar (`<movie>…</movie>`) | Home-media servers; **local NFO overrides online sources** |
| **XMP** (Adobe) | RDF/XML metadata, embeddable or `.xmp` sidecar; hosts Dublin Core, IPTC, EXIF namespaces | Adobe tools, DAMs, pro workflows |
| **Dublin Core** | 15 core descriptive terms (`dc:title`, `dc:creator`, `dc:subject`, `dc:description`, `dc:date`, `dc:rights`…) | The lingua franca inside XMP |
| **IPTC Video Metadata Hub** | Rich video-specific schema (mappable to XMP/MP4/MKV) | News/media; most complete video schema |
| **MP4/ISOBMFF atoms** | iTunes-style `udta/meta` atoms (`©nam` title, `©cmt` comment, `©gen` genre, `desc`, `keyw`…) | What players/Finder read in `.mp4`/`.mov` |
| **Matroska tags** | Open XML-ish tag system (TITLE, COMMENT, KEYWORDS, DATE_RELEASED…) | `.mkv` |

**Portable field set** (supported across MP4 `-metadata`, MKV tags, NFO, and
XMP/Dublin Core) — a safe common denominator to start with:

| Our data | Standard field |
|---|---|
| `FileName` / a display title | `title` / `dc:title` / `©nam` |
| `Notes` | `comment` + `description` / `dc:description` / `©cmt` |
| Tags (all groups, flattened) | `keywords` / `dc:subject` / Matroska `KEYWORDS` |
| Tag-group/value pairs, custom properties | XMP custom namespace, or NFO `<tag>`/`<genre>`; MP4 freeform `----` atoms |
| `CreationTime` | `date` / `xmp:CreateDate` / `creation_time` |
| `CameraType`, `VideoQuality` | XMP custom ns or NFO `<tag>` |
| `IsFavorite`, ratings | `rating` / NFO `<userrating>` |

> **Recommendation:** make **NFO the default** export (best ecosystem payoff,
> trivially round-trippable), with the field mapping above. Offer **XMP** as a
> second emitter for pro/DAM users. For *embedding* (later), use ffmpeg
> `-metadata` for the portable subset and accept that complex structures
> (typed custom properties) only fully round-trip via sidecar.

---

## 5. Embedding mechanics (for the opt-in A2 path)

If/when we embed, the mechanism differs sharply by container — and so does the
MD5 blast radius:

- **Matroska (`.mkv`): `mkvpropedit` edits tags in place** without remuxing —
  fast, rewrites only the tag/seek bytes. (Still changes the file MD5, but it's
  near-instant and low-risk.)
- **MP4/MOV (`.mp4`/`.m4v`/`.mov`):** `ffmpeg -i in -map 0 -c copy -metadata
  key=val out` does a **lossless remux** (no re-encode) but **rewrites the whole
  file**. `AtomicParsley` can edit iTunes atoms more in-place; `exiftool` writes
  many MP4/QuickTime tags. All change the MD5.
- **Never re-encode.** Always stream-copy (`-c copy`) / in-place edit — re-encoding
  is slow and lossy.
- **Safe-write pattern:** reuse the issue-#4 move discipline — write to a temp
  file on the same volume, then atomically replace; log the operation (an
  `Md5History`/audit row) so it's traceable and, ideally, reversible. Recompute
  the file hash (B3) and content hash (B1) right after.
- **Clips:** a parent rewrite changes bytes under all its clips. Either
  invalidate/re-probe affected clip rows after a write, or block embedding on
  videos that have clips. (Sidecars avoid this entirely.)
- **Tool dependency:** embedding adds `mkvpropedit` (MKVToolNix) and/or
  `AtomicParsley`/`exiftool` as optional host binaries — same "503 + install
  hint" pattern we used for tesseract (#5). Sidecars need **no** new binary.

---

## 6. Recommendation & phased plan

**Default posture:** DB stays the source of truth (A3). Adding metadata is an
explicit **export** action, not an automatic file mutation.

- **Phase 1 — NFO sidecar export/import (no MD5 problem, no new binaries).**
  "Write metadata to file" emits `<video>.nfo` next to each file; an importer
  reads `.nfo` back into tags/notes/properties. Move sidecars alongside files in
  the existing move/undo flow. *This satisfies issue #18 for the common case
  with zero risk to originals or dedup.*
- **Phase 2 — XMP sidecar emitter** (Dublin Core + a SightsAndSounds namespace)
  for pro/DAM interop.
- **Phase 3 (opt-in) — true embedding.** `mkvpropedit` for MKV, ffmpeg `-c copy`
  / AtomicParsley for MP4. Introduce `Video.ContentMd5` via `streamhash` (B1) so
  dedup survives repacks; keep the file `Md5` truthful (B3); add `Md5History`
  (B2) only if you want a rewrite audit log.

---

## 7. Open decisions for you

1. **Embed in the file, or sidecar?** (I recommend **sidecar-first**, embedding
   opt-in later — it makes the MD5 problem vanish and keeps originals untouched.)
2. **If we ever embed: fix dedup with a content hash (B1), or just track MD5
   history (B2)?** (I recommend B1 — it makes dedup *better*, not just patched.)
3. **Which sidecar format first — NFO (home-media) or XMP (pro/DAM)?** (I
   recommend **NFO** for ecosystem payoff.)
4. **Scope of fields** — start with the portable subset in §4, or go straight for
   the full IPTC Video Metadata Hub mapping?
5. **Two-way or one-way?** Just write metadata out, or also import sidecars/
   embedded tags back in (and reconcile conflicts with the DB)?

---

## Sources

- [FFmpeg Formats Documentation — streamhash / framemd5 / hash](https://ffmpeg.org/ffmpeg-formats.html)
- [SWGDE — Technical Notes on FFmpeg for Forensic Video Examinations](https://www.swgde.org/documents/published-complete-listing/16-v-002-technical-notes-on-ffmpeg-for-forensic-video-examinations/)
- [Kdenlive Manual — Adding Metadata to MP4 Video (ffmpeg `-metadata -c copy`)](https://docs.kdenlive.org/en/tips_and_tricks/how-tos/adding_meta_data_to_mp4_video.html)
- [An Archivist's Guide to Matroska — metadata & mkvpropedit](https://github.com/amiaopensource/An_Archivists_Guide_To_Matroska/blob/master/metadata.md)
- [AtomicParsley (iTunes-style MP4 atoms)](https://github.com/wez/atomicparsley)
- [How to Get and Change Video Metadata in Linux (exiftool/ffmpeg/mkvpropedit)](https://www.dotlinux.net/blog/how-to-get-and-change-video-metadata-in-linux/)
- [Jellyfin — Local .nfo metadata](https://jellyfin.org/docs/general/server/metadata/nfo/)
- [IPTC Video Metadata Hub — User Guide](https://iptc.org/std/videometadatahub/userguide/)
- [W3C — XMP for video annotations (Dublin Core / XMP namespaces)](https://www.w3.org/2008/WebVideo/Annotations/drafts/ontology10/WD/XMP.html)
- [datahacker — Multimedia Metadata Deep Dive (NFO vs embedded, container atoms)](https://datahacker.blog/home-theater/media-servers/multimedia-metadata-deep-dive)
