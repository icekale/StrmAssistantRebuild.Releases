using MediaBrowser.Model.Dto;

namespace StrmAssistantRebuild.Common;

/// <summary>Response-only library badge; the caller supplies a count scoped to this item and viewer.</summary>
internal static class ItemCountProjection
{
    internal static BaseItemDto? Project(BaseItemDto? dto, bool enabled, int? viewerScopedTotal)
    {
        // No ChildCount fallback: native collection-folder ChildCount can be a sentinel (1).
        // No new UserData: respect a native response that omitted user data.
        if (!enabled || dto?.Type != "CollectionFolder" || dto.UserData == null ||
            viewerScopedTotal is not >= 0 || dto.UserData.UnplayedItemCount == viewerScopedTotal) return dto;
        var copy = MultiVersionProjection.Clone(dto);
        copy.UserData = MultiVersionProjection.Clone(dto.UserData);
        copy.UserData.UnplayedItemCount = viewerScopedTotal;
        return copy;
    }

    internal static BaseItemDto? ProjectEpisodes(BaseItemDto? dto, bool enabled, int? viewerScopedTotal)
    {
        if (!enabled || dto?.Type is not ("Series" or "Season") || dto.UserData == null || viewerScopedTotal is not >= 0 ||
            dto.UserData.UnplayedItemCount == viewerScopedTotal && dto.RecursiveItemCount == viewerScopedTotal) return dto;
        var copy = MultiVersionProjection.Clone(dto);
        copy.UserData = MultiVersionProjection.Clone(dto.UserData);
        copy.UserData.UnplayedItemCount = viewerScopedTotal;
        // Explicit response contract: supply a successful total even when the caller omitted this field.
        // The Web badge consumes RecursiveItemCount, never mistakes an unchanged unplayed count for a total.
        copy.RecursiveItemCount = viewerScopedTotal;
        return copy;
    }
}
