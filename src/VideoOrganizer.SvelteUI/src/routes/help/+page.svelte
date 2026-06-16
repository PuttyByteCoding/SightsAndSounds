<script lang="ts">
  // Help & Features (#169): a single in-app reference of what each section does
  // and the key workflows. Folds in the "FAQ" idea — for a single-user app a
  // straight features/reference page is more useful than a Q&A. Static content;
  // links out to the live Keyboard Shortcuts page.

  type Item = { href: string; title: string; body: string };
  type Group = { heading: string; blurb: string; items: Item[] };

  const groups: Group[] = [
    {
      heading: 'Watch & browse',
      blurb: 'Find a video and play it.',
      items: [
        {
          href: '/browse',
          title: 'Videos',
          body:
            'The main library. Filter with the sidebar tree (tags, flags, folders) using ' +
            'Required / Optional / Excluded buckets, search by name/tag/on-screen text, and ' +
            'play in the built-in player with a scrubber, frame preview, and keyboard seek. ' +
            'The player auto-sizes to fill the screen above the always-visible thumbnail strip.'
        },
        {
          href: '/history',
          title: 'History',
          body: 'Recently watched videos, most recent first — a quick way back to what you were viewing.'
        }
      ]
    },
    {
      heading: 'Organize',
      blurb: 'Tag, flag, and group your library.',
      items: [
        {
          href: '/tags',
          title: 'Tag Management',
          body:
            'Create and edit tag groups and tags (names, aliases, favorites, notes), merge duplicates, ' +
            'and bulk-create. Tags are your own free-form taxonomy.'
        },
        {
          href: '/hidden-tags',
          title: 'Hidden Tags',
          body:
            'Mark tags as "hidden by default" so videos carrying them stay out of every listing unless ' +
            'you explicitly filter for that tag. Hidden means hidden everywhere — browse, search, playlists.'
        },
        {
          href: '/browse',
          title: 'Flags',
          body:
            'Built-in booleans shown in the browse Flags sidebar: Favorite, Needs Review, Playback Issue, ' +
            'To Delete, and the clip flags — Clip (any clip), Embedded (a marked region inside a parent), ' +
            'Exported (a standalone file exported from a parent), and Edited (output of removing blocked ' +
            'sections). Filter by any of them with count badges.'
        }
      ]
    },
    {
      heading: 'Get videos in',
      blurb: 'Bring files into the library.',
      items: [
        {
          href: '/import',
          title: 'Import Tool',
          body:
            'Browse a folder tree and import videos. Pre-stage tags, notes, and flags (Favorite, Clip) on ' +
            'everything in the batch. ffprobe extracts metadata; MD5 + thumbnails are filled in afterward ' +
            'by the background workers. Re-importing the same folder never duplicates.'
        },
        {
          href: '/sources',
          title: 'Sources',
          body:
            'Manage VideoSets — the root folders the library reads from. Enable/disable a source, or ' +
            're-root one (move its base path and rewrite every child video\'s path together).'
        }
      ]
    },
    {
      heading: 'Produce new files',
      blurb: 'Cut clips and trim videos — lossless, no re-encode.',
      items: [
        {
          href: '/clips-export',
          title: 'Export Clips',
          body:
            'Turn clips (a marked [start,end] region inside a video) into their own standalone files. ' +
            'Each clip can be named and previewed (cuts snap to keyframes); the original keeps a ' +
            'breadcrumb band showing what was exported so you don\'t export the same range twice.'
        },
        {
          href: '/remove-blocked',
          title: 'Remove Blocked',
          body:
            'Build a new "trimmed" file with a video\'s "Hide" sections cut out (the kept segments are ' +
            'stream-copied and concatenated). Optionally delete the original afterward via the purge flow.'
        }
      ]
    },
    {
      heading: 'Library health',
      blurb: 'Keep the collection clean and consistent.',
      items: [
        {
          href: '/duplicates',
          title: 'Duplicates',
          body: 'Review flagged duplicate pairs (from the browse "Find duplicates" hunt) and resolve them.'
        },
        {
          href: '/purge',
          title: 'Purge Deleted',
          body:
            'Videos you marked for deletion, split into real files vs clips. Purge for good or restore. ' +
            'Warns when a video still has un-exported embedded clips, so you can export them first.'
        },
        {
          href: '/playback-issues',
          title: 'Playback Issues',
          body: 'Videos flagged as not playing cleanly in the browser — a worklist for re-encoding or investigating.'
        },
        {
          href: '/data-validation',
          title: 'Data Validation',
          body:
            'Surfaces drift between the database and disk: missing files, un-imported leftovers, ' +
            'unreachable sources, and MD5 checks.'
        },
        {
          href: '/moves',
          title: 'File Moves',
          body: 'A log of every file move (from flagging delete/playback-issue or the move tool), each with one-click Undo.'
        }
      ]
    },
    {
      heading: 'System & diagnostics',
      blurb: 'Background work, backups, and references.',
      items: [
        {
          href: '/background-tasks',
          title: 'Background Tasks',
          body:
            'Live status of the import queue and the thumbnail + MD5 workers — progress, failures, and ' +
            'pause/resume/retry controls.'
        },
        {
          href: '/backups',
          title: 'Backups',
          body: 'Create and restore JSON snapshots of the database (tags, videos, settings).'
        },
        { href: '/logs', title: 'Logs', body: 'Structured application log events for troubleshooting.' },
        { href: '/api-docs', title: 'API', body: 'The OpenAPI/Swagger explorer for the backend.' },
        {
          href: '/keyboard-shortcuts',
          title: 'Keyboard Shortcuts',
          body: 'The full list of player and page shortcuts (numpad seeking, flags, navigation, and more).'
        }
      ]
    }
  ];
</script>

<div class="p-4 max-w-4xl mx-auto">
  <h1 class="text-2xl font-semibold mb-1">Help &amp; Features</h1>
  <p class="text-base-content/70 mb-6">
    What each part of the app does and how the main workflows fit together. For the full key list, see
    <a class="link" href="/keyboard-shortcuts">Keyboard Shortcuts</a>.
  </p>

  <div class="flex flex-col gap-6">
    {#each groups as group (group.heading)}
      <section>
        <h2 class="text-lg font-semibold">{group.heading}</h2>
        <p class="text-sm text-base-content/60 mb-2">{group.blurb}</p>
        <div class="grid gap-2 sm:grid-cols-2">
          {#each group.items as item (item.title + item.href)}
            <div class="card bg-base-200 p-3">
              <a class="link link-hover font-medium" href={item.href}>{item.title}</a>
              <p class="text-sm text-base-content/70 mt-1">{item.body}</p>
            </div>
          {/each}
        </div>
      </section>
    {/each}
  </div>
</div>
