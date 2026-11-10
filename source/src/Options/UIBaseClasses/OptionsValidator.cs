using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

namespace StrmAssistantRebuild.Options.UIBaseClasses;

public static class OptionsValidator
{
    public static List<string> Validate(EditableOptionsBase model, Common.EmbyPathPolicy? paths = null)
    {
        var errors = new List<string>();
        Visit(model, model.GetType().Name, errors, paths);
        return errors;
    }
    public static void NormalizeDiscovery(EditableOptionsBase model)
    {
        if (model is NetworkOptions network && Common.NetworkEndpointRules.TryNormalizeUrl(network.LocalDiscoveryUrl, out var normalized, out _)) network.LocalDiscoveryUrl = normalized;
        foreach (var property in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetIndexParameters().Length == 0 && property.SetMethod?.IsPublic == true && !property.IsDefined(typeof(IgnoreOnPersistAttribute)) && property.GetValue(model) is EditableOptionsBase child)
                NormalizeDiscovery(child);
    }
    private static void Visit(EditableOptionsBase model, string prefix, List<string> errors, Common.EmbyPathPolicy? paths)
    {
        if (model is MetadataBuildOptions cache) errors.AddRange(Common.MetadataCacheRules.Validate(cache, paths).Select(e => prefix + "." + e));
        if (model is MetadataEnhanceOptions metadata && !Provider.MovieDbEndpointRules.ValidateOptions(metadata, out var metadataError))
            errors.Add(prefix + ": " + metadataError);
        if (model is NetworkOptions network && !Common.NetworkEndpointRules.TryNormalizeUrl(network.LocalDiscoveryUrl, out _, out var discoveryError))
            errors.Add(prefix + ".LocalDiscoveryUrl: " + discoveryError);
        foreach (var property in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true || property.IsDefined(typeof(IgnoreOnPersistAttribute))) continue;
            var value = property.GetValue(model);
            var key = prefix + "." + property.Name;
            if (property.Name == "MarkerEnabledLibraryScope" && value is string markerScope)
            {
                try { ScopeRules.ValidateMarkerScope(markerScope); }
                catch (ArgumentException error) { errors.Add(key + ": " + error.Message); }
            }
            if (value is EditableOptionsBase child) { Visit(child, key, errors, paths); continue; }
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsEnum && value != null && !Enum.IsDefined(type, value)) errors.Add(key + ": invalid enum value.");
            if (value != null && (type == typeof(int) || type == typeof(double) || type == typeof(long)))
            {
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var min = property.GetCustomAttribute<MinValueAttribute>();
                var max = property.GetCustomAttribute<MaxValueAttribute>();
                if (!double.IsFinite(number) || (min != null && number < min.MinValue) || (max != null && number > max.MaxValue))
                    errors.Add(key + ": value is outside the supported range.");
            }
            if (property.IsDefined(typeof(RequiredAttribute)) && property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false && ConditionsPass(model, property) &&
                (value == null || value is string text && string.IsNullOrWhiteSpace(text))) errors.Add(key + ": a value is required.");
        }
    }
    private static bool ConditionsPass(object model, PropertyInfo property)
    {
        foreach (var attribute in property.CustomAttributes.Where(a => a.AttributeType.Name is "VisibleConditionAttribute" or "EnabledConditionAttribute"))
        {
            var args = attribute.ConstructorArguments;
            if (args.Count < 2 || args[0].Value is not string source) continue;
            var current = model.GetType().GetProperty(source)?.GetValue(model);
            var condition = Enum.GetName(args[1].ArgumentType, args[1].Value!);
            if (condition == "IsTrue" && !Equals(current, true) || condition == "IsFalse" && !Equals(current, false)) return false;
            if (args.Count > 2)
            {
                var expected = args[2].Value;
                var equal = Equals(current, expected) || Convert.ToString(current, CultureInfo.InvariantCulture) == Convert.ToString(expected, CultureInfo.InvariantCulture);
                if (condition == "IsEqual" && !equal || condition == "IsNotEqual" && equal) return false;
            }
        }
        return true;
    }
}
