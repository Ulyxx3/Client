using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// Manages fetching and caching the Rhythia online map archive,
/// and downloading individual maps for local import.
/// Uses Godot's built-in HttpRequest for TLS compatibility.
/// Archive URL: https://cdn.rhythia.net/index.json
/// </summary>
public partial class OnlineMapManager : Node
{
    public static OnlineMapManager Instance { get; private set; }

    private const string ARCHIVE_INDEX_URL = "https://cdn.rhythia.net/index.json";
    private const string CACHE_FILE = "online_index_cache.json";
    private const string CACHE_DATE_FILE = "online_index_updated.txt";

    /// <summary>All maps from the online archive (after last successful fetch).</summary>
    public static List<OnlineMap> ArchiveMaps { get; private set; } = [];

    /// <summary>True while an index fetch is in progress.</summary>
    public static bool IsFetchingIndex { get; private set; } = false;

    /// <summary>True while a map download/import is in progress.</summary>
    public static bool IsDownloading { get; private set; } = false;

    /// <summary>Fired on the main thread when the index fetch completes.</summary>
    public static event Action<bool> IndexFetched;

    /// <summary>Fired on the main thread when a map download completes.</summary>
    public static event Action<string, bool> MapDownloaded;

    // Godot HttpRequest nodes for TLS-compatible HTTP
    private HttpRequest _indexRequest;
    private HttpRequest _downloadRequest;

    // Pending download state
    private OnlineMap _pendingDownload;

    public override void _Ready()
    {
        Instance = this;

        _indexRequest = new HttpRequest();
        _indexRequest.UseThreads = true;
        _indexRequest.Timeout = 60;
        _indexRequest.RequestCompleted += OnIndexRequestCompleted;
        AddChild(_indexRequest);

        _downloadRequest = new HttpRequest();
        _downloadRequest.UseThreads = true;
        _downloadRequest.Timeout = 0; // No timeout for large map files
        _downloadRequest.RequestCompleted += OnDownloadRequestCompleted;
        AddChild(_downloadRequest);
    }

    // -------------------------------------------------------------------------
    // Index Fetching & Caching
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetches (or loads from cache) the online archive index.
    /// Fires <see cref="IndexFetched"/> when done.
    /// </summary>
    public void FetchIndex()
    {
        if (IsFetchingIndex) return;

        // If we already have the archive in memory, no need to re-fetch — use it instantly.
        if (ArchiveMaps.Count > 0)
        {
            Logger.Log($"[OnlineMapManager] Archive already loaded ({ArchiveMaps.Count} maps), skipping fetch.");
            IndexFetched?.Invoke(true);
            return;
        }

        IsFetchingIndex = true;

        string cacheFilePath = $"{Constants.USER_FOLDER}/{CACHE_FILE}";
        string cacheDatePath = $"{Constants.USER_FOLDER}/{CACHE_DATE_FILE}";

        // Cancel any previous stuck request before starting a fresh one.
        _indexRequest.CancelRequest();

        // Build headers — use If-Modified-Since if we have a cached date
        var headers = new Godot.Collections.Array<string>();
        if (File.Exists(cacheFilePath) && File.Exists(cacheDatePath))
        {
            try
            {
                string cachedDate = File.ReadAllText(cacheDatePath).Trim();
                headers.Add($"If-Modified-Since: {cachedDate}");
                Logger.Log($"[OnlineMapManager] Using If-Modified-Since: {cachedDate}");
            }
            catch { /* ignore — just fetch fresh */ }
        }

        Logger.Log($"[OnlineMapManager] Requesting {ARCHIVE_INDEX_URL}...");
        Error err = _indexRequest.Request(ARCHIVE_INDEX_URL, [.. headers]);

        if (err != Error.Ok)
        {
            Logger.Error($"[OnlineMapManager] HttpRequest.Request() failed: {err}");
            IsFetchingIndex = false;
            IndexFetched?.Invoke(false);
        }
    }

    private void OnIndexRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        Logger.Log($"[OnlineMapManager] Index response: result={result} code={responseCode}");

        string cacheFilePath = $"{Constants.USER_FOLDER}/{CACHE_FILE}";
        string cacheDatePath = $"{Constants.USER_FOLDER}/{CACHE_DATE_FILE}";

        try
        {
            if (result != (long)HttpRequest.Result.Success)
            {
                Logger.Error($"[OnlineMapManager] Request failed with result code {result}");
                tryLoadFromCache(cacheFilePath);
                return;
            }

            string json;

            if (responseCode == 304 && File.Exists(cacheFilePath))
            {
                // Cache hit — 304 Not Modified
                Logger.Log("[OnlineMapManager] Cache hit (304 Not Modified), loading from disk.");
                json = File.ReadAllText(cacheFilePath);
            }
            else if (responseCode == 200)
            {
                // Fresh data — save to cache
                Logger.Log("[OnlineMapManager] Cache miss (200 OK), saving to disk.");
                json = Encoding.UTF8.GetString(body);
                File.WriteAllText(cacheFilePath, json);
                File.WriteAllText(cacheDatePath, DateTime.UtcNow.ToString("R"));
            }
            else
            {
                Logger.Error($"[OnlineMapManager] Unexpected HTTP {responseCode}");
                tryLoadFromCache(cacheFilePath);
                return;
            }

            ArchiveMaps = ParseIndex(json);
            Logger.Log($"[OnlineMapManager] Loaded {ArchiveMaps.Count} maps from archive.");
            IndexFetched?.Invoke(true);
        }
        catch (Exception e)
        {
            Logger.Error($"[OnlineMapManager] OnIndexRequestCompleted error: {e.Message}");
            tryLoadFromCache(cacheFilePath);
        }
        finally
        {
            IsFetchingIndex = false;
        }
    }

    private void tryLoadFromCache(string cacheFilePath)
    {
        if (File.Exists(cacheFilePath))
        {
            try
            {
                string json = File.ReadAllText(cacheFilePath);
                ArchiveMaps = ParseIndex(json);
                Logger.Log($"[OnlineMapManager] Loaded {ArchiveMaps.Count} maps from fallback cache.");
                IndexFetched?.Invoke(true);
                return;
            }
            catch (Exception e)
            {
                Logger.Error($"[OnlineMapManager] Cache load failed: {e.Message}");
            }
        }

        IndexFetched?.Invoke(false);
    }

    // -------------------------------------------------------------------------
    // JSON Parsing
    // -------------------------------------------------------------------------

    private static List<OnlineMap> ParseIndex(string json)
    {
        var maps = new List<OnlineMap>();

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        foreach (JsonProperty entry in root.EnumerateObject())
        {
            try
            {
                JsonElement data = entry.Value;
                var map = new OnlineMap { Id = entry.Name };

                if (data.TryGetProperty("name", out var nameProp))
                    map.Name = nameProp.GetString() ?? string.Empty;

                if (data.TryGetProperty("song", out var songProp))
                    map.Song = songProp.GetString() ?? string.Empty;

                if (data.TryGetProperty("download", out var dlProp))
                    map.DownloadUrl = dlProp.GetString() ?? string.Empty;

                if (data.TryGetProperty("difficulty", out var diffProp))
                    map.Difficulty = Math.Max(0, diffProp.GetInt32()); // clamp -1 to 0

                if (data.TryGetProperty("difficulty_name", out var diffNameProp))
                    map.DifficultyName = diffNameProp.GetString() ?? string.Empty;

                if (data.TryGetProperty("length_ms", out var lenProp))
                    map.LengthMs = lenProp.GetInt32();

                if (data.TryGetProperty("note_count", out var notesProp))
                    map.NoteCount = notesProp.GetInt32();

                // broken maps have no audio — skip them
                if (data.TryGetProperty("broken", out var brokenProp) && brokenProp.GetBoolean())
                    continue;

                if (data.TryGetProperty("author", out var authorProp))
                {
                    if (authorProp.ValueKind == JsonValueKind.Array)
                    {
                        var authors = new List<string>();
                        foreach (var a in authorProp.EnumerateArray())
                            authors.Add(a.GetString() ?? "");
                        map.Authors = [.. authors];
                    }
                    else if (authorProp.ValueKind == JsonValueKind.String)
                    {
                        map.Authors = [authorProp.GetString() ?? ""];
                    }
                }

                if (string.IsNullOrEmpty(map.DownloadUrl)) continue;

                maps.Add(map);
            }
            catch (Exception e)
            {
                Logger.Log($"[OnlineMapManager] Skipping malformed entry '{entry.Name}': {e.Message}");
            }
        }

        return maps;
    }

    // -------------------------------------------------------------------------
    // Map Download & Import
    // -------------------------------------------------------------------------

    /// <summary>
    /// Downloads an online map's .sspm file and imports it into the local library.
    /// Fires <see cref="MapDownloaded"/> when done.
    /// </summary>
    public void DownloadMap(OnlineMap onlineMap)
    {
        if (IsDownloading)
        {
            _ = ToastNotification.Notify("A download is already in progress.");
            return;
        }

        IsDownloading = true;
        _pendingDownload = onlineMap;

        _ = ToastNotification.Notify($"Downloading: {onlineMap.PrettyTitle}");
        Logger.Log($"[OnlineMapManager] Downloading map '{onlineMap.Id}' from {onlineMap.DownloadUrl}");

        Error err = _downloadRequest.Request(onlineMap.DownloadUrl);

        if (err != Error.Ok)
        {
            Logger.Error($"[OnlineMapManager] Download request failed: {err}");
            IsDownloading = false;
            _ = ToastNotification.Notify($"Download failed: {onlineMap.PrettyTitle}");
            MapDownloaded?.Invoke(onlineMap.Id, false);
        }
    }

    private void OnDownloadRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        OnlineMap onlineMap = _pendingDownload;
        _pendingDownload = null;
        IsDownloading = false;

        if (result != (long)HttpRequest.Result.Success || responseCode != 200)
        {
            Logger.Error($"[OnlineMapManager] Download failed: result={result} code={responseCode}");
            _ = ToastNotification.Notify($"Download failed: {onlineMap?.PrettyTitle}");
            MapDownloaded?.Invoke(onlineMap?.Id ?? "", false);
            return;
        }

        try
        {
            Logger.Log($"[OnlineMapManager] Download complete ({body.Length} bytes), importing...");

            // Parse and encode on background thread so UI doesn't freeze
            Task.Run(() =>
            {
                Map map = MapParser.SSPM(body);
                MapParser.Encode(map);
                MapCache.Load(false);
            }).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Logger.Error($"[OnlineMapManager] Import failed: {task.Exception?.Message}");
                    Callable.From(() =>
                    {
                        _ = ToastNotification.Notify($"Import failed: {onlineMap.PrettyTitle}");
                        MapDownloaded?.Invoke(onlineMap.Id, false);
                    }).CallDeferred();
                }
                else
                {
                    Logger.Log($"[OnlineMapManager] Map '{onlineMap.Id}' imported successfully.");
                    Callable.From(() =>
                    {
                        _ = ToastNotification.Notify($"Downloaded: {onlineMap.PrettyTitle}");
                        MapDownloaded?.Invoke(onlineMap.Id, true);
                    }).CallDeferred();
                }
            });
        }
        catch (Exception e)
        {
            Logger.Error($"[OnlineMapManager] OnDownloadRequestCompleted error: {e.Message}");
            _ = ToastNotification.Notify($"Download failed: {onlineMap?.PrettyTitle}");
            MapDownloaded?.Invoke(onlineMap?.Id ?? "", false);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns archive maps NOT already installed locally, filtered by search queries.
    /// </summary>
    public static List<OnlineMap> GetUninstalledMaps(string searchQuery = "", string authorQuery = "")
    {
        var installedIds = new HashSet<string>();
        foreach (Map m in MapManager.Maps)
            installedIds.Add(m.Name);

        var result = new List<OnlineMap>();
        foreach (OnlineMap om in ArchiveMaps)
        {
            if (installedIds.Contains(om.Id)) continue;

            if (!string.IsNullOrEmpty(searchQuery) &&
                !om.PrettyTitle.Contains(searchQuery, StringComparison.CurrentCultureIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(authorQuery) &&
                !om.PrettyMappers.Contains(authorQuery, StringComparison.CurrentCultureIgnoreCase))
                continue;

            result.Add(om);
        }
        return result;
    }
}
