using System;
using Godot;

/// <summary>
/// Toggle button that switches the MapList between local maps and the online archive.
/// Placed in the SubTopBar of the Play menu.
/// </summary>
public partial class OnlineToggleButton : Button
{
    public static OnlineToggleButton Instance { get; private set; }

    private bool _onlineMode = false;

    public override void _Ready()
    {
        Instance = this;

        // Always start from a known offline state.
        // This handles returning from gameplay where the node may or may not be recreated.
        _onlineMode = false;
        Text = "Online";
        Disabled = false;
        SelfModulate = new Color(1, 1, 1, 1);

        // Subscribe to static C# events from OnlineMapManager
        OnlineMapManager.IndexFetched += OnIndexFetched;
        OnlineMapManager.MapDownloaded += OnMapDownloaded;
    }

    public override void _ExitTree()
    {
        OnlineMapManager.IndexFetched -= OnIndexFetched;
        OnlineMapManager.MapDownloaded -= OnMapDownloaded;
    }

    public override void _Pressed()
    {
        if (OnlineMapManager.IsFetchingIndex) return;

        _onlineMode = !_onlineMode;

        if (_onlineMode)
        {
            // If archive is already in memory: switch instantly, no loading needed.
            if (OnlineMapManager.ArchiveMaps.Count > 0)
            {
                switchToOnline();
            }
            else
            {
                setLoadingState();
                OnlineMapManager.Instance.FetchIndex();
            }
        }
        else
        {
            switchToOffline();
        }
    }

    // -------------------------------------------------------------------------

    private void OnIndexFetched(bool success)
    {
        if (!_onlineMode) return;

        if (success)
        {
            switchToOnline();
        }
        else
        {
            _onlineMode = false;
            Disabled = false;
            Text = "Online";
            SelfModulate = new Color(1, 1, 1, 1);
            _ = ToastNotification.Notify("Failed to load online archive.");
        }
    }

    private void OnMapDownloaded(string mapId, bool success)
    {
        if (!_onlineMode) return;

        MapList.Instance.UpdateOnlineMaps(
            OnlineMapManager.GetUninstalledMaps(
                MapList.Instance.SearchQuery,
                MapList.Instance.AuthorQuery
            )
        );
    }

    private void switchToOnline()
    {
        Disabled = false;
        Text = "Offline";
        SelfModulate = Color.FromHtml("ff7ba8");

        try
        {
            MapList.Instance?.SetOnlineMode(
                true,
                OnlineMapManager.GetUninstalledMaps(
                    MapList.Instance?.SearchQuery ?? "",
                    MapList.Instance?.AuthorQuery ?? ""
                )
            );
        }
        catch (Exception e)
        {
            Logger.Error($"[OnlineToggleButton] switchToOnline error: {e.Message}");
            // Reset to safe offline state so the button is never stuck
            _onlineMode = false;
            Text = "Online";
            SelfModulate = new Color(1, 1, 1, 1);
        }
    }

    private void switchToOffline()
    {
        Text = "Online";
        SelfModulate = new Color(1, 1, 1, 1);
        MapList.Instance.SetOnlineMode(false, null);
    }

    private void setLoadingState()
    {
        Disabled = true;
        Text = "Loading...";
        SelfModulate = new Color(1, 1, 1, 0.5f);
    }
}
