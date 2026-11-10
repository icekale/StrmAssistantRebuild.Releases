using System.Globalization;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using StrmAssistantRebuild.Common;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Provider;

/// <summary>Adult status is an explicit TMDB details field, never a property invented on Emby's Person.</summary>
public sealed class PersonRefreshService
{
    readonly ILibraryManager library;
    readonly IProviderManager providers;
    readonly QueueManager queue;
    readonly HttpClientFactory http;
    readonly Func<MetadataEnhanceOptions> options;
    readonly Func<Person, CancellationToken, bool, bool, bool, bool, bool, Task<bool>> refresh;
    readonly ILogger logger;
    public PersonRefreshReport? LastReport { get; private set; }
    public PersonRefreshService(ILibraryManager library, IProviderManager providers, QueueManager queue, HttpClientFactory http,
        Func<MetadataEnhanceOptions> options, Func<Person, CancellationToken, bool, bool, bool, bool, bool, Task<bool>> refresh, ILogger logger)
    { this.library = library; this.providers = providers; this.queue = queue; this.http = http; this.options = options; this.refresh = refresh; this.logger = logger; }

    public async Task<PersonRefreshReport> ExecuteAsync(CancellationToken token, IProgress<double> progress)
    {
        token.ThrowIfCancellationRequested();
        var settings = options();
        var rules = MetadataRefreshPolicy.Selected(settings.MetadataRefreshOptions.PersonRefreshPreferences);
        var people = library.GetItemList(new InternalItemsQuery
        { IncludeItemTypes = new[] { "Person" }, IsLocked = false, HasAnyProviderId = new[] { "Tmdb", "Imdb", "Tvdb" } }, token).OfType<Person>().ToArray();
        var report = new PersonRefreshReport { Total = people.Length };
        LastReport = report;
        progress.Report(0);
        // ponytail: serial batch keeps memory/network bounded; both status lookup and native refresh
        // join the shared tier2 quota. Release the lookup lease before pipeline.RefreshAsync acquires it.
        try
        {
            foreach (var person in people)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var plan = MetadataRefreshPolicy.PersonRefresh(person, settings.MetadataRefreshOptions, DateTimeOffset.UtcNow);
                    if (!plan.Metadata && !plan.Images) { report.PreferenceSkipped++; continue; }
                    var supplementOnly = false;
                    if (rules.Contains("ExcludeAdultMetadata"))
                    {
                        var status = await queue.RunSingle(person.InternalId, ct => GetAdultStatusAsync(person, settings, ct), token, secondTier: true).ConfigureAwait(false);
                        if (!status.Adult.HasValue)
                        {
                            report.UnknownSkipped++;
                            logger.Warn("[StrmAssistantRebuild] Person {0} skipped: adult status unknown ({1}).", person.InternalId, status.Reason);
                            continue;
                        }
                        supplementOnly = status.Adult.Value;
                        if (supplementOnly)
                        {
                            plan = (plan.Metadata && PersonSupplement.NeedsMetadata(person), plan.Images && !person.HasImage(ImageType.Primary));
                            if (!plan.Metadata && !plan.Images) { report.KnownExcluded++; continue; }
                        }
                    }
                    // Only the existing pipeline owns item locks, provider calls and Emby-root writes.
                    var changed = await refresh(person, token, plan.Metadata, plan.Images, !supplementOnly && rules.Contains("FullImageRefresh"), rules.Contains("DownloadImagesInAdvance"), supplementOnly).ConfigureAwait(false);
                    if (!supplementOnly) report.Refreshed++;
                    else if (changed) report.Supplemented++;
                    else report.ProviderNegative++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report.Failed++;
                    // Exception messages can contain a request URI/key. Log only type and item identity.
                    logger.Warn("[StrmAssistantRebuild] Person {0} refresh failed: {1}.", person.InternalId, ex.GetType().Name);
                }
                finally { report.Processed++; progress.Report(report.Processed * 100d / report.Total); }
            }
        }
        catch (OperationCanceledException) { report.Cancelled = true; throw; }
        finally
        {
            logger.Info("[StrmAssistantRebuild] Person refresh: total {0}, refreshed {1}, preference-skipped {2}, known-adult-no-missing {3}, unknown-skipped {4}, failed {5}, cancelled {6}, supplemented {7}, provider-negative-or-no-change {8}.",
                report.Total, report.Refreshed, report.PreferenceSkipped, report.KnownExcluded, report.UnknownSkipped, report.Failed, report.Cancelled, report.Supplemented, report.ProviderNegative);
            if (report.UnknownSkipped > 0 && report.Refreshed == 0 && report.Supplemented == 0)
                logger.Warn("[StrmAssistantRebuild] No person was refreshed; {0} have unknown adult status. Configure a TMDB API key and enable the TMDB Person provider, or deselect ExcludeAdultMetadata.", report.UnknownSkipped);
        }
        progress.Report(100);
        if (report.Total > 0 && report.Failed == report.Total) throw new InvalidOperationException("All person refreshes failed; see per-item logs.");
        return report;
    }

    async Task<PersonAdultStatus> GetAdultStatusAsync(Person person, MetadataEnhanceOptions settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var enabled = providers.GetEnabledMetadataProviders(person, library.GetLibraryOptions(person));
            // GetAllMetadataPlugins deliberately omits Person on Emby 4.10.2. Query actual enabled typed providers.
            if (!enabled.OfType<IRemoteMetadataProvider<Person, PersonLookupInfo>>().Any(p => MetadataLanguageService.ProviderKind(p.Name) == "tmdb"))
                return new(null, "tmdb-person-provider-not-enabled");
            var id = person.GetProviderId(MetadataProviders.Tmdb);
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
                return new(null, "tmdb-person-id-missing");
            // Shipped MovieDb has only an internal static ApiKey, not public plugin configuration.
            // Reuse the configured key/HTTP transport; do not scrape private fields or copy a native key.
            if (string.IsNullOrWhiteSpace(settings.AltMovieDbApiKey)) return new(null, "configured-tmdb-key-missing");
            using var details = await MetadataLanguageService.GetTmdbAsync(http, settings, "person/" + number.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
            return details == null ? new(null, "tmdb-details-missing") : ReadAdultStatus(details.RootElement, number);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(null, "tmdb-details-" + ex.GetType().Name); }
    }

    public static PersonAdultStatus ReadAdultStatus(JsonElement details, long expectedId)
    {
        if (details.ValueKind != JsonValueKind.Object) return new(null, "invalid-details-object");
        // Require exact field casing, identity and JSON booleans; missing/null/string false is not false.
        if (details.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False || details.TryGetProperty("status_code", out _))
            return new(null, "tmdb-error-response");
        if (!details.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number != expectedId)
            return new(null, "tmdb-person-id-mismatch");
        if (!details.TryGetProperty("adult", out var adult) || adult.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return new(null, "adult-field-missing-or-invalid");
        return new(adult.GetBoolean(), "tmdb-person-details");
    }
}

public sealed record PersonAdultStatus(bool? Adult, string Reason);
public sealed class PersonRefreshReport
{
    public int Total { get; init; }
    public int Processed { get; set; }
    public int Refreshed { get; set; }
    public int PreferenceSkipped { get; set; }
    // Compatibility name: known adult with no selected missing/unlocked fields or Primary image.
    public int KnownExcluded { get; set; }
    public int Supplemented { get; set; }
    // Includes empty native image results (which may hide a provider failure) and no longer missing fields.
    public int ProviderNegative { get; set; }
    public int UnknownSkipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
}
