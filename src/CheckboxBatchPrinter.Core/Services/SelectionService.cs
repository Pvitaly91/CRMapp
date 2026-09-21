namespace CheckboxBatchPrinter.Core.Services;

public static class SelectionService
{
    public static void SetVisibleSelection<T>(IEnumerable<T> visibleItems, Action<T, bool> setter, bool selected)
    {
        foreach (var item in visibleItems)
            setter(item, selected);
    }
}
