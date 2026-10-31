namespace NexusMods.App.UI.Pages.MyLoadouts;

public enum ProfileSortOrder { RecentlyCreated, Name, AppliedFirst }

public record ProfileFilter(string SearchText, bool OnlyApplied, ProfileSortOrder SortOrder)
{
    public static readonly ProfileFilter Default = new(string.Empty, false, ProfileSortOrder.RecentlyCreated);
    public bool IsFiltering => !string.IsNullOrWhiteSpace(SearchText) || OnlyApplied;
}
