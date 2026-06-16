# Video upscaling — research (#176)

Status: **research only.** No code yet. Lays out the options, the architecture
question, and a recommendation so we can pick a direction before building.

The ask: upscaling needs fine-tuning, so let the app **generate 2+ upscaled
versions** of a video and provide a **compare view that plays them side-by-side
(audio muted)** to judge quality. Open question in the issue: *should this be a
separate tool with an API the main app calls?*

---

## 1. The two tiers of upscaling

### Tier A — classic interpolation (ffmpeg, already installed)
`scale` (lanczos / spline / bicubic) and `zscale`. Fast, CPU/GPU-cheap,
deterministic, no extra dependencies. **But** these only resample existing
pixels — a bigger, smoother image, *not* genuinely more detail. Useful as a
cheap baseline and for the "fine-tuning" knobs (sharpen/denoise pre/post):

```
ffmpeg -i in.mp4 -vf "zscale=w=2560:h=1440:f=lanczos,unsharp=5:5:0.8" -c:a copy out.mp4
# or hwaccel: scale_vaapi / scale_cuda / scale_vulkan on the NVIDIA box
```

ffmpeg also ships a DNN **super-resolution** filter (`sr` with SRCNN/ESPCN
models, 2–4×), but it needs a DNN backend built in (tensorflow/native), the
models are dated, and gains are modest — not worth the build complexity here.

### Tier B — AI super-resolution (real detail reconstruction)
Frame-by-frame neural upscaling. The practical self-hosted, GPU options (all
have NCNN-Vulkan CLI builds that run on the existing NVIDIA card):

| Tool | Strength | Notes |
|---|---|---|
| **Real-ESRGAN** (ncnn-vulkan) | general-purpose photo/video, the de-facto default | slow (~2h for a 16s FHD→4K clip); has an `anime` model too |
| **Real-CUGAN** | anime/cartoon, fast over large libraries | good detail retention, much faster than waifu2x |
| **waifu2x** (ncnn-vulkan) | anime/line-art fidelity (best in blind tests) | weaker on live-action |
| **RealSR** (ncnn-vulkan) | ~2.5× faster than Real-ESRGAN | slightly less detail |
| **Topaz Video AI** | best **temporal consistency** | commercial/paid, not open-source |

Key caveats that make "fine-tuning + compare" exactly the right framing:
- **Model choice is content-dependent** (anime vs live-action vs retro) and
  there's no universal winner — hence generating several variants and eyeballing.
- **Temporal consistency** (flicker between frames) is the main weakness of
  frame-by-frame open tools; Topaz is strongest. Worth comparing per source.
- **Cost is real**: minutes-to-hours per clip on a GPU. This is a deliberate,
  queued, "pick a few candidates" operation — never automatic on import.

How AI tools run: extract frames → upscale each with the model → reassemble
with the original audio/timing. (Real-ESRGAN-video wrappers automate this.)

---

## 2. The workflow the issue actually wants

A **tuning loop**, not a one-shot convert:
1. Pick a source video.
2. Define **2+ candidate recipes** — e.g. `lanczos+sharpen`, `Real-ESRGAN x2`,
   `Real-CUGAN x2 (anime)`, `Real-ESRGAN x4`. Each = backend + model + scale +
   optional denoise/sharpen knobs.
3. Queue them (background, GPU-bound, one at a time) → each produces a file.
4. **Compare view**: a grid that plays all candidates (and ideally the original)
   **synced and muted**, so you can scrub the same moment across versions and
   judge sharpness/artifacts/temporal stability.
5. Keep the winner (it's already a normal library video); discard the rest.

This mirrors the **Export Clips / Remove Blocked** pages we already built: a
queue + background jobs + a progress singleton, and each output is **ingested as
a fresh top-level Video** (ffprobe → row → Md5/thumbnail backfill) via the shared
`MediaExport` helpers. An "Upscaled" flag (like Clip/Edited, #167) would mark and
let you filter the candidates.

The compare view is the genuinely new UI piece: N `<video>` elements in a grid,
one transport that seeks/plays/pauses all of them together, all muted.

---

## 3. Should it be a separate tool with an API?

**Recommendation: a separate, self-contained "upscaler" service with a small
HTTP/job API — but built only when we move past the ffmpeg/classic tier.**

- **For Tier A (classic ffmpeg)** — *no separate tool.* It's just another
  `MediaExport`-style background job in the existing API, exactly like clip
  export. Build the variants + compare-view workflow here first; it delivers the
  whole "generate N versions and compare" loop with zero new infrastructure.
- **For Tier B (AI/GPU)** — *yes, isolate it.* Reasons:
  - Heavy/uneven deps (CUDA/Vulkan, model files, Python or NCNN binaries) that
    shouldn't bloat or destabilize the .NET API container.
  - Long GPU-bound jobs want their own queue + horizontal GPU workers (the
    standard pattern — cf. NVIDIA NIM, worker-pool-behind-a-queue designs).
  - Clean seam: the main app POSTs `{ sourcePath, recipe } → jobId`, polls
    status, and ingests the produced file when done. The upscaler can evolve
    (swap models, add GPUs) without touching the main app.
  - The main app stays the same whether the backend is local ffmpeg or a remote
    GPU box.

So: **one job abstraction in the main app** (recipe + queue + ingest + compare
view), with a **pluggable backend** — `ffmpeg` in-process for Tier A, and an
HTTP call to the separate upscaler service for Tier B. Start Tier A in-app;
stand up the service when we add AI models.

---

## 4. Cross-cutting notes

- **Each upscaled output is a new file** → ingest like the clip/trim exports
  (new Video, Md5 + thumbnails backfilled). Reuse `MediaExport`.
- **Flag, don't tag** (#167): add an **Upscaled** flag so candidates are
  filterable; record the recipe (backend/model/scale) in the video's Notes or a
  small property so you remember what produced the keeper.
- **Never on import / never automatic** — it's expensive and subjective.
- **Audio/subtitles**: copy through unchanged (`-c:a copy`); upscaling is video-only.
- **Disk**: candidates are large; the compare step should make it easy to delete
  the losers (route them straight to the purge flow).
- The NVIDIA GPU on this box (already used for HEVC decode) makes Tier B viable
  locally via NCNN-Vulkan builds.

---

## 5. Recommendation & phased plan

1. **Phase 1 — variants + compare, ffmpeg backend (in-app).** A new "Upscale"
   page: pick a video, define N recipes (scale + algorithm + sharpen/denoise),
   queue them as `MediaExport` jobs, ingest outputs, and a synced muted-grid
   **compare view**. Delivers the full tuning loop with no new infra. Add the
   **Upscaled** flag.
2. **Phase 2 — pluggable AI backend.** Define the recipe to also name a model
   (Real-ESRGAN / Real-CUGAN / waifu2x). Implement it behind the same job
   abstraction, initially shelling to a local NCNN-Vulkan binary (optional, like
   tesseract/HandBrake — skipped if absent).
3. **Phase 3 — separate upscaler service** (only if Phase 2's in-process GPU
   jobs prove too heavy/slow to share the API process): extract Tier B into a
   queue-backed GPU worker with an HTTP API the main app calls.

---

## 6. Open decisions for you

1. **Scope of Phase 1** — ship the compare-view + variants loop on the
   **ffmpeg/classic tier first**, or wait and do AI models up front?
2. **Which AI tool** to target for Phase 2 — Real-ESRGAN (general) and/or
   Real-CUGAN/waifu2x (if your content is anime/animated)? What's the typical
   source content (live-action vs animated)?
3. **Separate service now or later?** I recommend later (Phase 3), once the
   in-app loop exists — but if you already want a dedicated GPU box, we design
   the API seam from the start.
4. **Compare view**: original + N candidates in one synced grid — how many
   candidates at once is realistic for you (2? 4?)?

---

## Sources
- [FFmpeg Scaler Documentation (lanczos/spline/bicubic, zscale)](https://ffmpeg.org/ffmpeg-scaler.html)
- [How to Upscale & Enhance Video with FFmpeg (2026)](https://wavespeed.ai/blog/posts/blog-how-to-upscale-enhance-video-quality-ffmpeg/)
- [Enhancing Video Quality with Super-Resolution (SRCNN/ESPCN sr filter)](https://streaminglearningcenter.com/encoding/enhancing-video-quality-with-super-resolution.html)
- [Best Open-Source AI Video Upscalers 2026 (Real-ESRGAN / Real-CUGAN / RealSR / waifu2x)](https://unifab.ai/resource/open-source-video-upscaler)
- [AI Anime Upscalers: Waifu2x vs Real-ESRGAN vs Topaz (quality/temporal)](https://www.alibaba.com/product-insights/ai-anime-upscalers-waifu2x-vs-real-esrgan-vs-topaz-video-ai-which-preserves-line-art-integrity-best.html)
- [NVIDIA NIM — self-hosted GPU inference microservices (API seam pattern)](https://developer.nvidia.com/nim)
- [Upscaler — consolidated CLI for open-source image/video upscaling](https://github.com/hollowaykeanho/Upscaler)
