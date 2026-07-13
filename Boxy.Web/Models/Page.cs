namespace Boxy.Web.Models;

/// <summary>One page of a larger list, plus the metadata a pager needs to render.</summary>
public class Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Number { get; init; }
    public required int Size { get; init; }
    public required int Total { get; init; }

    /// <summary>The active sort key (e.g. "new", "old", "size"), echoed back into pager/sort links.</summary>
    public required string Sort { get; init; }

    public int Pages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)Size);
    public bool HasPrev => Number > 1;
    public bool HasNext => Number < Pages;
    public int FirstItem => Total == 0 ? 0 : (Number - 1) * Size + 1;
    public int LastItem => Math.Min(Number * Size, Total);

    /// <summary>Clamp a requested page into range and turn a sort choice into safe values.</summary>
    public static (int Number, string Sort) Normalize(int? number, string? sort, IEnumerable<string> allowed, string fallbackSort)
    {
        var s = sort is not null && allowed.Contains(sort) ? sort : fallbackSort;
        var n = number is > 0 ? number.Value : 1;
        return (n, s);
    }
}

/// <summary>The bits the shared _Pager partial needs, independent of the item type.</summary>
public class PagerVm
{
    public required int Number { get; init; }
    public required int Pages { get; init; }
    public required int FirstItem { get; init; }
    public required int LastItem { get; init; }
    public required int Total { get; init; }

    /// <summary>Query-string key this pager controls (e.g. "vp", "fp", "p").</summary>
    public required string PageParam { get; init; }

    /// <summary>Plural noun for the count line (e.g. "videos", "files").</summary>
    public required string Noun { get; init; }

    public static PagerVm For<T>(Page<T> page, string pageParam, string noun)
    {
        return new PagerVm
        {
            Number = page.Number,
            Pages = page.Pages,
            FirstItem = page.FirstItem,
            LastItem = page.LastItem,
            Total = page.Total,
            PageParam = pageParam,
            Noun = noun
        };
    }
}
