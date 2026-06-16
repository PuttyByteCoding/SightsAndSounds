using System.Text;
using System.Text.Json;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API;

/// <summary>
/// Keyset (cursor) pagination for the filtered browse query (#127). Each sort
/// mode defines a total order <c>(key, Id)</c>; the cursor carries the last
/// row's key + id so the next page is a WHERE on that order rather than an
/// OFFSET (which is O(n) at large offsets and re-scans the whole prefix).
///
/// Shuffle is seedable: the order is <c>md5(id || seed)</c>, deterministic for a
/// given seed, so pages don't overlap or repeat as the user scrolls but a new
/// seed reshuffles. The cursor key for shuffle is that md5 — computed in SQL for
/// the WHERE and re-computed identically in C# for the *next* cursor (Postgres
/// md5(uuid::text) == C# MD5 of Guid.ToString()).
/// </summary>
internal static class VideoPagination
{
    public enum SortMode { Shuffle, FileName, FileSize, Duration, FolderFile }

    public static SortMode ParseSort(string? s) => s switch
    {
        "fileName" => SortMode.FileName,
        "fileSize" => SortMode.FileSize,
        "duration" => SortMode.Duration,
        "folderFile" => SortMode.FolderFile,
        _ => SortMode.Shuffle,
    };

    public sealed record Cursor(string Key, Guid Id);

    public static string Encode(Cursor c) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c)));

    public static Cursor? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return JsonSerializer.Deserialize<Cursor>(json);
        }
        catch
        {
            return null; // malformed cursor → start from the first page
        }
    }

    /// <summary>The cursor key for a materialized row, matching the SQL sort key.</summary>
    public static string KeyOf(Video v, SortMode mode, string seed) => mode switch
    {
        SortMode.FileName => v.FileName,
        SortMode.FileSize => v.FileSize.ToString(Inv),
        SortMode.Duration => v.Duration.Ticks.ToString(Inv),
        SortMode.FolderFile => v.FilePath,
        SortMode.Shuffle => Md5Hex(v.Id.ToString() + seed),
        _ => v.FileName,
    };

    /// <summary>
    /// Orders <paramref name="q"/> by the mode's total order and, when a cursor
    /// is given, keeps only rows strictly after it. The caller Takes the page.
    /// Direction is ignored for Shuffle (its order is the seeded hash).
    /// </summary>
    public static IQueryable<Video> OrderAndSeek(
        IQueryable<Video> q, SortMode mode, bool desc, Cursor? c, string seed)
    {
        switch (mode)
        {
            case SortMode.FileName: return SeekString(q, fileName: true, c?.Key, c?.Id, desc);
            case SortMode.FolderFile: return SeekString(q, fileName: false, c?.Key, c?.Id, desc);
            case SortMode.FileSize: return SeekFileSize(q, c, desc);
            case SortMode.Duration: return SeekDuration(q, c, desc);
            case SortMode.Shuffle:
            default: return SeekShuffle(q, seed, c?.Key, c?.Id);
        }
    }

    private static IQueryable<Video> SeekFileSize(IQueryable<Video> q, Cursor? c, bool desc)
    {
        if (desc)
        {
            q = q.OrderByDescending(v => v.FileSize).ThenByDescending(v => v.Id);
            if (c is not null) { var k = long.Parse(c.Key, Inv); var id = c.Id; q = q.Where(v => v.FileSize < k || (v.FileSize == k && v.Id.CompareTo(id) < 0)); }
        }
        else
        {
            q = q.OrderBy(v => v.FileSize).ThenBy(v => v.Id);
            if (c is not null) { var k = long.Parse(c.Key, Inv); var id = c.Id; q = q.Where(v => v.FileSize > k || (v.FileSize == k && v.Id.CompareTo(id) > 0)); }
        }
        return q;
    }

    private static IQueryable<Video> SeekDuration(IQueryable<Video> q, Cursor? c, bool desc)
    {
        if (desc)
        {
            q = q.OrderByDescending(v => v.Duration).ThenByDescending(v => v.Id);
            if (c is not null) { var k = new TimeSpan(long.Parse(c.Key, Inv)); var id = c.Id; q = q.Where(v => v.Duration < k || (v.Duration == k && v.Id.CompareTo(id) < 0)); }
        }
        else
        {
            q = q.OrderBy(v => v.Duration).ThenBy(v => v.Id);
            if (c is not null) { var k = new TimeSpan(long.Parse(c.Key, Inv)); var id = c.Id; q = q.Where(v => v.Duration > k || (v.Duration == k && v.Id.CompareTo(id) > 0)); }
        }
        return q;
    }

    private static IQueryable<Video> SeekString(IQueryable<Video> q, bool fileName, string? ck, Guid? cid, bool desc)
    {
        if (fileName)
        {
            if (desc)
            {
                q = q.OrderByDescending(v => v.FileName).ThenByDescending(v => v.Id);
                if (ck is not null && cid is { } id)
                    q = q.Where(v => string.Compare(v.FileName, ck) < 0 || (v.FileName == ck && v.Id.CompareTo(id) < 0));
            }
            else
            {
                q = q.OrderBy(v => v.FileName).ThenBy(v => v.Id);
                if (ck is not null && cid is { } id)
                    q = q.Where(v => string.Compare(v.FileName, ck) > 0 || (v.FileName == ck && v.Id.CompareTo(id) > 0));
            }
        }
        else // folderFile → order by full path (folder-then-file)
        {
            if (desc)
            {
                q = q.OrderByDescending(v => v.FilePath).ThenByDescending(v => v.Id);
                if (ck is not null && cid is { } id)
                    q = q.Where(v => string.Compare(v.FilePath, ck) < 0 || (v.FilePath == ck && v.Id.CompareTo(id) < 0));
            }
            else
            {
                q = q.OrderBy(v => v.FilePath).ThenBy(v => v.Id);
                if (ck is not null && cid is { } id)
                    q = q.Where(v => string.Compare(v.FilePath, ck) > 0 || (v.FilePath == ck && v.Id.CompareTo(id) > 0));
            }
        }
        return q;
    }

    private static IQueryable<Video> SeekShuffle(IQueryable<Video> q, string seed, string? ck, Guid? cid)
    {
        q = q.OrderBy(v => VideoOrganizerDbContext.Md5(v.Id.ToString() + seed)).ThenBy(v => v.Id);
        if (ck is not null && cid is { } id)
            q = q.Where(v =>
                string.Compare(VideoOrganizerDbContext.Md5(v.Id.ToString() + seed), ck) > 0
                || (VideoOrganizerDbContext.Md5(v.Id.ToString() + seed) == ck && v.Id.CompareTo(id) > 0));
        return q;
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static string Md5Hex(string input)
        => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(input)));
}
