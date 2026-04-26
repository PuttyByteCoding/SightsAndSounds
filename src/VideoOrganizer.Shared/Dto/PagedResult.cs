namespace VideoOrganizer.Shared.Dto;

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Skip, int Take);