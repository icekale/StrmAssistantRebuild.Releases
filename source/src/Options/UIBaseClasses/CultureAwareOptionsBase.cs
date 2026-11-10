using System.ComponentModel;
using System.Runtime.CompilerServices;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Conditions;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

/// <summary>Refresh localized help on each newly built native editor tree.</summary>
public abstract class CultureAwareOptionsBase : EditableOptionsBase
{
    public override IEditObjectContainer CreateEditContainer()
    {
        var container = (EditObjectContainer)base.CreateEditContainer();
        RefreshDescriptions(container);
        FieldCapabilityGates.Relax(container);
        return container;
    }

    public static void RefreshDescriptions(EditObjectContainer container) =>
        RefreshDescriptions(container.Object, container.EditorRoot);

    private static void RefreshDescriptions(object model, EditorGroup group)
    {
        var properties = TypeDescriptor.GetProperties(model);
        foreach (var editor in group.EditorItems)
        {
            var property = properties[editor.Name];
            if (property == null) continue;
            // MemberDescriptor.Description caches its first string; the attribute getter does not.
            foreach (Attribute attribute in property.Attributes)
            {
                if (attribute is LocalizedDescriptionAttribute description)
                {
                    editor.Description = description.Description;
                    break;
                }
            }
            // Option groups are nested IEditableObject models, not collection editors.
            if (editor is EditorGroup child && property.GetValue(model) is MediaBrowser.Model.GenericEdit.IEditableObject nested)
                RefreshDescriptions(nested, child);
        }
    }
}

/// <summary>
/// View-time field gates (M143/F8). A complete controlled group can stay unsupported while individual fields
/// already have verified consumers. Generated option attributes stay byte-identical to the original inventory;
/// only the per-view editor tree drops the group-level Enabled condition for fields whose own host evidence passed.
/// Gates are transient per model instance and never persisted or read from client data.
/// Kept in this file so every project compiling the options base also compiles the gates.
/// </summary>
public static class FieldCapabilityGates
{
    private static readonly ConditionalWeakTable<EditableOptionsBase, HashSet<string>> Allowed = new();
    private static readonly HashSet<string> CapabilityFlags = new(StringComparer.Ordinal)
    {
        "IsModSupported", "IsMovieDbPluginSupported", "IsTvdbPluginSupported", "IsOriginalPosterSupported",
        "IsNfoMetadataPluginSupported", "IsOpenSubtitlesPluginSupported"
    };

    /// <summary>Replace the allowed field set for this exact model instance (called from RuntimeCapabilities.Apply).</summary>
    public static void Set(EditableOptionsBase model, IEnumerable<string> fields) =>
        Allowed.AddOrUpdate(model, new HashSet<string>(fields, StringComparer.Ordinal));

    public static bool IsAllowed(EditableOptionsBase model, string field) =>
        Allowed.TryGetValue(model, out var fields) && fields.Contains(field);

    /// <summary>Remove group-capability Enabled conditions for allowed fields. Visible and value conditions are kept.</summary>
    public static void Relax(EditObjectContainer container)
    {
        var root = container.EditorRoot;
        var conditions = root?.PropertyConditions;
        if (root == null || conditions == null || conditions.Length == 0 || container.Object is not EditableOptionsBase model) return;
        var kept = conditions.Where(condition => !Removable(model, condition)).ToArray();
        if (kept.Length != conditions.Length) root.PropertyConditions = kept;
    }

    private static bool Removable(EditableOptionsBase model, PropertyCondition condition)
    {
        if (condition.ConditionType != PropertyConditionType.Enabled || condition.AffectedPropertyId == null || condition.TargetPropertyId == null) return false;
        var affected = condition.AffectedPropertyId.Split('.');
        var target = condition.TargetPropertyId.Split('.');
        // The capability flag must belong to the same nested option model as the affected field.
        if (affected.Length != target.Length || !CapabilityFlags.Contains(target[^1])) return false;
        for (var i = 0; i < affected.Length - 1; i++) if (!string.Equals(affected[i], target[i], StringComparison.Ordinal)) return false;
        object? owner = model;
        for (var i = 0; i < affected.Length - 1 && owner != null; i++)
            owner = owner.GetType().GetProperty(affected[i])?.GetValue(owner);
        return owner is EditableOptionsBase options && IsAllowed(options, affected[^1]);
    }
}
