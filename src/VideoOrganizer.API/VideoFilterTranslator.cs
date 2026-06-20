using System.Linq.Expressions;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.API;

/// <summary>
/// Translates the three-way <see cref="PlaylistFilterRequest"/> slots into a
/// SQL-evaluable EF predicate so the database does the filtering instead of the
/// app pulling every video under the enabled roots into memory (#127).
///
/// Every slot is pushed down only where it is *provably* safe to do so:
///   Required (AND)  — each translatable term narrows the set; a row that fails
///                     one required term can never be in the result.
///   Excluded (NOT)  — each translatable term removes rows that match it; those
///                     rows can never be in the result either.
///   Optional (OR)   — only pushed when EVERY optional term translates, because
///                     a row matching an *untranslatable* optional term (a
///                     Folder) must survive to the in-memory pass.
///   Auto-hide       — always translatable; removes suppressed rows.
///
/// The only untranslatable term today is <see cref="FilterRefType.Folder"/>
/// (directory-equality with case-insensitive path semantics). When one is
/// present, <see cref="Apply"/> reports <c>needsInMemory = true</c> and the
/// caller runs the existing exact in-memory <c>MatchesFilter</c> pass over the
/// (already SQL-narrowed) set, so behaviour is identical to before — just over
/// far fewer rows.
/// </summary>
internal static class VideoFilterTranslator
{
    /// <summary>
    /// Narrows <paramref name="query"/> by every safely-translatable predicate.
    /// Returns whether the caller must still apply the in-memory pass (true iff
    /// some term — a Folder filter — could not be translated).
    /// </summary>
    public static (IQueryable<Video> query, bool needsInMemory) Apply(
        IQueryable<Video> query,
        IReadOnlyList<FilterRef> required,
        IReadOnlyList<FilterRef> optional,
        IReadOnlyList<FilterRef> excluded,
        IReadOnlyCollection<Guid> autoHideTagIds)
    {
        var needsInMemory = false;

        // Required (AND): push translatable terms; defer untranslatable ones.
        foreach (var f in required)
        {
            var p = TryTranslate(f);
            if (p is null) needsInMemory = true;
            else query = query.Where(p);
        }

        // Excluded (AND NOT): push translatable terms; defer untranslatable ones.
        foreach (var f in excluded)
        {
            var p = TryTranslate(f);
            if (p is null) needsInMemory = true;
            else query = query.Where(Not(p));
        }

        // Optional (OR): only safe to push when ALL terms translate.
        if (optional.Count > 0)
        {
            var preds = optional.Select(TryTranslate).ToList();
            if (preds.All(p => p is not null))
            {
                Expression<Func<Video, bool>>? orPred = null;
                foreach (var p in preds)
                    orPred = orPred is null ? p : Or(orPred, p!);
                query = query.Where(orPred!);
            }
            else
            {
                needsInMemory = true;
            }
        }

        // Auto-hide (AND NOT-any): always SQL-translatable.
        if (autoHideTagIds.Count > 0)
        {
            var ids = autoHideTagIds.ToList();
            query = query.Where(v => !v.VideoTags.Any(vt => ids.Contains(vt.TagId)));
        }

        return (query, needsInMemory);
    }

    /// <summary>
    /// A SQL-translatable predicate for the given filter ref, or <c>null</c> if
    /// the ref needs in-memory evaluation (Folder). Mirrors the per-type
    /// semantics of <c>ApiEndpoints.MatchesFilter</c> exactly — including that a
    /// malformed Tag/Missing/Status value matches nothing.
    /// </summary>
    public static Expression<Func<Video, bool>>? TryTranslate(FilterRef f)
    {
        switch (f.Type)
        {
            case FilterRefType.Tag:
                return Guid.TryParse(f.Value, out var tid)
                    ? v => v.VideoTags.Any(vt => vt.TagId == tid)
                    : NeverMatches;

            case FilterRefType.Missing:
                if (f.Value.StartsWith("tagGroup:", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(f.Value["tagGroup:".Length..], out var gid))
                    return v => !v.VideoTags.Any(vt => vt.Tag != null && vt.Tag.TagGroupId == gid);
                return NeverMatches;

            case FilterRefType.Status:
                return f.Value switch
                {
                    "needsReview" => v => v.NeedsReview,
                    "playbackIssue" => v => v.PlaybackIssue,
                    "markedForDeletion" => v => v.MarkedForDeletion,
                    "favorite" => v => v.IsFavorite,
                    // Clip flags (#167). "clip" is the umbrella; the others narrow.
                    "clip" => v => v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip,
                    "embedded" => v => v.ParentVideoId.HasValue,
                    "exported" => v => v.IsExportedClip,
                    "edited" => v => v.IsEdited,
                    // Back-compat: the old single clip flag value.
                    "isClip" => v => v.ParentVideoId.HasValue || v.IsClip || v.IsExportedClip,
                    _ => NeverMatches,
                };

            case FilterRefType.Folder:
            default:
                return null; // not translatable → caller falls back to memory
        }
    }

    private static readonly Expression<Func<Video, bool>> NeverMatches = _ => false;

    // --- expression composition (rebinds both lambdas onto one parameter) -----

    private static Expression<Func<Video, bool>> Or(
        Expression<Func<Video, bool>> a, Expression<Func<Video, bool>> b)
        => Combine(a, b, Expression.OrElse);

    private static Expression<Func<Video, bool>> Not(Expression<Func<Video, bool>> a)
    {
        var p = Expression.Parameter(typeof(Video), "v");
        var body = Expression.Not(Rebind(a, p));
        return Expression.Lambda<Func<Video, bool>>(body, p);
    }

    private static Expression<Func<Video, bool>> Combine(
        Expression<Func<Video, bool>> a, Expression<Func<Video, bool>> b,
        Func<Expression, Expression, Expression> op)
    {
        var p = Expression.Parameter(typeof(Video), "v");
        var body = op(Rebind(a, p), Rebind(b, p));
        return Expression.Lambda<Func<Video, bool>>(body, p);
    }

    private static Expression Rebind(Expression<Func<Video, bool>> e, ParameterExpression p)
        => new ParameterReplacer(e.Parameters[0], p).Visit(e.Body)!;

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
