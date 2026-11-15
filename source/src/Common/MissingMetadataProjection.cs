using System.Globalization;
using MediaBrowser.Model.Dto;

namespace StrmAssistantRebuild.Common;

/// <summary>Response-only episode/additional-part titles; never reads parent metadata or a library.</summary>
public static class MissingMetadataProjection
{
    /// <summary>Caller proves this is one aligned native all-Video AdditionalPart response batch.</summary>
    public static BaseItemDto[] ProjectAdditionalParts(BaseItemDto[] dtos, bool enabled)
    {
        if (!enabled || dtos.Length == 0 || dtos.Length > 20000 || dtos.Any(d => d?.Type != "Video")) return dtos;
        BaseItemDto[]? copy = null;
        for (var i = 0; i < dtos.Length; i++)
        {
            // Original AA---------/A----------.cs:2604-2621 (M167: literals recovered): one part "下部分", several "第{i+2}部分".
            var name = dtos.Length == 1 ? "下部分" : "第" + (i + 2).ToString(CultureInfo.InvariantCulture) + "部分";
            if (dtos[i].Name == name) continue;
            copy ??= (BaseItemDto[])dtos.Clone();
            copy[i] = MultiVersionProjection.Clone(dtos[i]);
            copy[i].Name = name;
        }
        return copy ?? dtos;
    }

    /// <param name="authorizedFileNameWithoutExtension">
    /// Basename from the corresponding, already-authorized native Episode. The caller validates
    /// item/DTO identity and type before passing it. This value is only compared, never returned.
    /// </param>
    public static BaseItemDto? Project(BaseItemDto? dto, bool enabled, string? authorizedFileNameWithoutExtension)
    {
        // Original AA---------/A----------.cs:2684–2739: Episode, index present, ordinal filename equality.
        // No virtual-item, season, overview, image, parent metadata or positive-index rule exists there.
        if (!enabled || dto?.Type != "Episode" || !dto.IndexNumber.HasValue ||
            string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(authorizedFileNameWithoutExtension) ||
            !string.Equals(dto.Name, authorizedFileNameWithoutExtension, StringComparison.Ordinal)) return dto;

        // Original AA---------/A----------.cs:2703-2705 (M167: literal recovered): "第 {N} 集", independent of the UI language.
        var name = "第 " + dto.IndexNumber.Value.ToString(CultureInfo.InvariantCulture) + " 集";
        if (string.Equals(dto.Name, name, StringComparison.Ordinal)) return dto;
        var copy = MultiVersionProjection.Clone(dto);
        copy.Name = name;
        return copy;
    }
}
