using System.Text;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmAssistantRebuild.Options;

namespace StrmAssistantRebuild.Common;

/// <summary>Candidate IDs only. The native repository remains responsible for every access check.</summary>
public sealed class ChineseSearchIndex : IDisposable
{
    private readonly ILibraryManager library;
    private readonly EmbyPathPolicy paths;
    private readonly string file;
    private readonly ILogger logger;
    private readonly object sync = new(), configurationSync = new(), saveSync = new();
    private readonly Timer saveTimer;
    private Dictionary<long, Entry> entries = new();
    private readonly Dictionary<long, Entry?> duringBuild = new();
    private const string TokenStrategy = "raw-literal-pinyin-v1";
    private Settings settings = new(false, false, false, false, false, false, false, Array.Empty<string>());
    private bool ready, building, disposed;
    public bool IsReady { get { lock (sync) return ready && settings.Enabled && !disposed; } }
    public int Count { get { lock (sync) return entries.Count; } }

    public ChineseSearchIndex(ILibraryManager library, EmbyPathPolicy paths, string indexFile, ILogger logger)
    {
        this.library = library; this.paths = paths; this.file = paths.RequireWritablePath(indexFile); this.logger = logger;
        saveTimer = new Timer(_ => Persist(), null, Timeout.Infinite, Timeout.Infinite);
        library.ItemAdded += Changed; library.ItemUpdated += Changed; library.ItemRemoved += Removed;
    }

    /// <summary>Call on startup/options save, outside repository calls. Initial build is synchronous.</summary>
    public void Configure(ModOptions options, CancellationToken cancellationToken = default, IProgress<double>? progress = null, bool forceRebuild = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tuning = Parse<ModOptions.SearchTuningOption>(options.SearchTuningPreferences);
        var next = new Settings(options.EnhanceChineseSearch,
            !string.Equals(options.EnhanceChineseSearchMode, "Lite", StringComparison.OrdinalIgnoreCase) && options.EnhanceChineseSearchMode != "1",
            tuning.Contains(ModOptions.SearchTuningOption.ExcludeOriginalTitle),
            tuning.Contains(ModOptions.SearchTuningOption.DigitsAsTmdbId),
            tuning.Contains(ModOptions.SearchTuningOption.SuppressSearchSuggestions),
            tuning.Contains(ModOptions.SearchTuningOption.TraditionalToSimplifiedChinese),
            tuning.Contains(ModOptions.SearchTuningOption.MixedChineseVariants),
            ResolveScope(options.SearchScope));
        lock (configurationSync)
        {
            lock (sync)
            {
                if (disposed) return;
                var wasEnabled = settings.Enabled;
                settings = next;
                if (!next.Enabled) { ready = false; return; }
                if (ready && wasEnabled && !forceRebuild) return; // A manual force rebuild always re-reads the library.
                building = true; ready = false; duringBuild.Clear();
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0);
                var cached = forceRebuild ? new Dictionary<long, Entry>() : Load();
                var rebuilt = new Dictionary<long, Entry>();
                // ponytail: linear lookup; add postings only after measured large-library latency.
                // ponytail: offset scan is eventually consistent during deletes; use a host read-context snapshot if strong scan consistency is needed.
                const int batch = 500;
                var total = 0;
                if (progress != null)
                {
                    var countQuery = IndexQuery(0, 0); countQuery.EnableTotalRecordCount = true;
                    total = library.GetItemsResult(countQuery, cancellationToken).TotalRecordCount;
                }
                for (var start = 0; ; start += batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var items = library.GetItemList(IndexQuery(start, batch), cancellationToken);
                    foreach (var item in items)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (item.InternalId <= 0) throw new InvalidDataException("Host index projection returned an item without a positive InternalId.");
                        var type = item.GetType().Name;
                        if (!cached.TryGetValue(item.InternalId, out var entry) || entry.Name != (item.Name ?? "") ||
                            entry.OriginalTitle != (item.OriginalTitle ?? "") || entry.Type != type || entry.Tmdb != NumericSearchPolicy.Tmdb(item))
                            entry = Create(item);
                        rebuilt[item.InternalId] = entry;
                    }
                    progress?.Report(Math.Min(99, 100d * (start + items.Length) / Math.Max(1, Math.Max(total, start + items.Length))));
                    if (items.Length < batch) break;
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync)
                {
                    foreach (var change in duringBuild)
                        if (change.Value is null) rebuilt.Remove(change.Key); else rebuilt[change.Key] = change.Value;
                    entries = rebuilt; building = false; ready = true; duringBuild.Clear();
                }
                Persist(cancellationToken);
                progress?.Report(100);
                logger.Info("[StrmAssistantRebuild] Chinese search candidate index ready: {0} items.", rebuilt.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (sync) { building = false; ready = false; duringBuild.Clear(); }
                throw;
            }
            catch (Exception error)
            {
                lock (sync) { building = false; ready = false; duringBuild.Clear(); }
                logger.Warn("[StrmAssistantRebuild] Chinese search index inactive: {0}", error.Message);
            }
        }
    }

    private static InternalItemsQuery IndexQuery(int? start, int? limit) => new()
    {
        Recursive = true, StartIndex = start, Limit = limit, EnableTotalRecordCount = false,
        EnableAutoSort = false, OrderBy = new[] { ("Id", SortOrder.Ascending) },
        GroupByPresentationUniqueKey = false, EnableGroupByMetadataKey = false,
        AllowOwnedItems = true, IncludeHiddenUserViews = true,
        // Id must be explicit: title-only projections otherwise yield InternalId=0 on 4.10.2.
        DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions { Fields = new[] { ItemFields.Id, ItemFields.Name, ItemFields.OriginalTitle, ItemFields.ProviderIds } }
    };

    /// <summary>Null means leave native search untouched; an empty array means no matching candidates.</summary>
    internal long[]? Candidates(string? term, bool matchAnyWord, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(term)) return null;
        var words = SearchWords(term);
        if (words.Length == 0) return null;
        lock (sync)
        {
            if (!ready || !settings.Enabled || disposed) return null;
            var numeric = settings.DigitsAsTmdbId ? NumericSearchPolicy.Id(term) : null;
            // Original query constructor uses whole-expression OR, not per-character or per-word expansion.
            // Mixed takes precedence; single-direction simplification replaces, rather than unions, the query.
            var queries = settings.MixedVariants
                ? new[] { term, PinyinUtility.ToSimplified(term), PinyinUtility.ToTraditional(PinyinUtility.ToSimplified(term)) }
                : new[] { settings.SimplifyQuery ? PinyinUtility.ToSimplified(term) : term };
            var enhancedWords = queries.Distinct(StringComparer.Ordinal).Select(SearchWords).Where(w => w.Length > 0).ToArray();
            var matches = entries.Values.Where(entry =>
            {
                token.ThrowIfCancellationRequested();
                var enhanced = settings.Types.Length == 0 || settings.Types.Contains(entry.Type, StringComparer.OrdinalIgnoreCase);
                // Only verified TMDB families in SearchScope switch modes. Other families keep M14 title matching/witness.
                if (numeric != null && enhanced && NumericSearchPolicy.Supports(entry.Type))
                    return string.Equals(entry.Tmdb, numeric, StringComparison.Ordinal);
                var name = enhanced && settings.Full ? entry.FullName : entry.LiteralName;
                var original = enhanced && settings.Full ? entry.FullOriginal : entry.LiteralOriginal;
                bool Match(string word) => name.Any(token => token.Contains(word, StringComparison.Ordinal)) ||
                    (!settings.ExcludeOriginal && original.Any(token => token.Contains(word, StringComparison.Ordinal)));
                bool MatchQuery(string[] queryWords) => matchAnyWord ? queryWords.Any(Match) : queryWords.All(Match);
                return enhanced ? enhancedWords.Any(MatchQuery) : MatchQuery(words);
            }).Select(entry => entry.Id);
            var result = (numeric == null ? matches : matches.Take(NumericSearchPolicy.CandidateBudget + 1)).ToArray();
            token.ThrowIfCancellationRequested();
            return numeric != null && result.Length > NumericSearchPolicy.CandidateBudget ? null : result;
        }
    }

    internal bool IsNumericSearch(string? term)
    {
        lock (sync) return ready && settings.Enabled && !disposed && settings.DigitsAsTmdbId && NumericSearchPolicy.Id(term) != null;
    }
    internal bool SuppressSearchSuggestions { get { lock (sync) return settings.Enabled && !disposed && settings.SuppressSuggestions; } }
    internal ILibraryManager Library => library;
    internal string? CandidateType(long id) { lock (sync) return entries.TryGetValue(id, out var entry) ? entry.Type : null; }
    internal bool CandidatesHaveOnlyTypes(IEnumerable<long> ids, ISet<string> types)
    {
        lock (sync) return ids.All(id => entries.TryGetValue(id, out var entry) && types.Contains(entry.Type));
    }

    private void Changed(object? sender, ItemChangeEventArgs e) => Update(e.Item, false);
    private void Removed(object? sender, ItemChangeEventArgs e) => Update(e.Item, true);
    private void Update(BaseItem? item, bool removed)
    {
        if (item is null || item.InternalId <= 0) return;
        try
        {
            lock (sync)
            {
                if (disposed || !settings.Enabled) return;
                Entry? entry = removed ? null : Create(item);
                if (building) duringBuild[item.InternalId] = entry;
                if (entry is null) entries.Remove(item.InternalId); else entries[item.InternalId] = entry;
                saveTimer.Change(1000, Timeout.Infinite);
            }
        }
        catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Chinese search index event: {0}", error.Message); }
    }

    private static Entry Create(BaseItem item) => new()
    {
        Id = item.InternalId, Type = item.GetType().Name, Name = item.Name ?? "", OriginalTitle = item.OriginalTitle ?? "", Tmdb = NumericSearchPolicy.Tmdb(item),
        LiteralName = Literal(item.Name), LiteralOriginal = Literal(item.OriginalTitle),
        FullName = PinyinUtility.GetSearchTokens(item.Name, item is Person, includeChineseVariants: false),
        FullOriginal = PinyinUtility.GetSearchTokens(item.OriginalTitle, item is Person, includeChineseVariants: false)
    };
    private static string[] Literal(string? value) => new[] { (value ?? "").ToLowerInvariant(), Compact(value ?? "") }
        .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
    private static string[] SearchWords(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(Compact).Where(w => w.Length > 0).ToArray();
    private static string Compact(string value)
    {
        var result = new StringBuilder();
        foreach (var rune in value.ToLowerInvariant().EnumerateRunes()) if (Rune.IsLetterOrDigit(rune)) result.Append(rune.ToString());
        return result.ToString();
    }
    private static T[] Parse<T>(string? text) where T : struct, Enum => (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(s => Enum.TryParse<T>(s, true, out var value) && Enum.IsDefined(typeof(T), value) ? (T?)value : null)
        .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
    internal static string[] ResolveScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return Array.Empty<string>();
        var types = Parse<ModOptions.SearchItemType>(scope).SelectMany(t => t switch
        {
            ModOptions.SearchItemType.Collection => new[] { "BoxSet" },
            ModOptions.SearchItemType.Music => new[] { "Audio", "MusicVideo" },
            ModOptions.SearchItemType.LiveTv => new[] { "LiveTvChannel", "LiveTvProgram", "LiveTVSeries" },
            ModOptions.SearchItemType.Genre => new[] { "Genre", "MusicGenre", "GameGenre" },
            _ => new[] { t.ToString() }
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return types.Length == 0 ? new[] { "__NoEnhancedType__" } : types; // Unknown selection never expands to all.
    }
    private Dictionary<long, Entry> Load()
    {
        try
        {
            paths.RequireWritablePath(file);
            if (!File.Exists(file)) return new();
            const long maxCacheBytes = 64L * 1024 * 1024;
            if (new FileInfo(file).Length > maxCacheBytes) throw new InvalidDataException("Search cache exceeds 64 MiB.");
            using var stream = File.OpenRead(file);
            var document = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions { MaxDepth = 8 });
            if (document is not { Version: 3 } || document.Strategy != TokenStrategy || document.Items is null || document.Items.Length > 100000) return new();
            return document.Items.Where(e => e is not null && e.Id > 0 && e.Type is not null && e.Tmdb is { Length: <= NumericSearchPolicy.MaxIdLength } &&
                new[] { e.FullName, e.LiteralName, e.FullOriginal, e.LiteralOriginal }.All(tokens => tokens is not null && tokens.Length <= 64 && tokens.All(t => t is not null))).ToDictionary(e => e.Id);
        }
        catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Rebuilding Chinese search cache: {0}", error.Message); return new(); }
    }
    private void Persist(CancellationToken token = default)
    {
        lock (saveSync)
        {
            string? temporary = null;
            try
            {
                Document document;
                lock (sync) { if (!ready || building) return; document = new Document { Strategy = TokenStrategy, Items = entries.Values.ToArray() }; }
                token.ThrowIfCancellationRequested();
                var destination = paths.RequireWritablePath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                temporary = paths.RequireWritablePath(file + ".tmp");
                var json = JsonSerializer.Serialize(document);
                token.ThrowIfCancellationRequested();
                File.WriteAllText(temporary, json);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.Warn("[StrmAssistantRebuild] Chinese search persistence: {0}", error.Message); }
            finally { if (temporary != null && File.Exists(temporary)) { try { File.Delete(paths.RequireWritablePath(temporary)); } catch (IOException error) { logger.Warn("[StrmAssistantRebuild] Search temp cleanup: {0}", error.Message); } } }
        }
    }
    public void Dispose()
    {
        lock (configurationSync)
        {
            lock (sync) { if (disposed) return; disposed = true; saveTimer.Dispose(); }
            library.ItemAdded -= Changed; library.ItemUpdated -= Changed; library.ItemRemoved -= Removed;
            Persist();
        }
    }
    private sealed record Settings(bool Enabled, bool Full, bool ExcludeOriginal, bool DigitsAsTmdbId, bool SuppressSuggestions, bool SimplifyQuery, bool MixedVariants, string[] Types);
    // Tokens are independent of query flags: toggling flags safely reuses this strategy, never old always-on tokens.
    public sealed class Document { public int Version { get; set; } = 3; public string Strategy { get; set; } = ""; public Entry[] Items { get; set; } = Array.Empty<Entry>(); }
    public sealed class Entry
    {
        public long Id { get; set; }
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string OriginalTitle { get; set; } = "";
        public string Tmdb { get; set; } = "";
        public string[] LiteralName { get; set; } = Array.Empty<string>();
        public string[] LiteralOriginal { get; set; } = Array.Empty<string>();
        public string[] FullName { get; set; } = Array.Empty<string>();
        public string[] FullOriginal { get; set; } = Array.Empty<string>();
    }
}
