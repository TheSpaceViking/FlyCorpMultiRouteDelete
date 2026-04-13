using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GameLogic;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using PathCreation;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using WPMF;
using Object = UnityEngine.Object;

namespace FlyCorpMultiRouteDelete;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.spaceviking.flycorp.multi-route-delete";
    public const string PluginName = "FlyCorp Multi Route Delete";
    public const string PluginVersion = "0.5.4";

    private static readonly bool EnableStartupFeedback = true;
    private static readonly bool EnableRouteSeamWrap = false;
    private const int BatchSaleRoutesPerFrame = 8;
    private const double BulkSaleRefundMultiplier = 1.6d;
    private const int RouteWrapMaintenanceIntervalFrames = 180;
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbTopMost = 0x00040000;
    private static readonly Color SelectedTextColor = new(1f, 0.88f, 0.28f, 1f);
    private static readonly Color TransparentHitboxColor = new(1f, 1f, 1f, 0.001f);
    private static readonly string[] RefundMemberNames = { "DeleteRouteRefund", "DeleteRefund", "RefundAmount" };

    private static readonly HashSet<string> QueuedRouteIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RouteItemState> RouteItemStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WrappedRouteVisualState> WrappedRouteStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SeamWrapDiagnosticSignatures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object QueueLock = new();

    private static ManualLogSource? _log;
    private static ConfigEntry<bool>? _seamWrapDiagnosticsEntry;
    private static RoutesStats? _activeRoutesStats;
    private static BatchRunner? _runner;
    private static TextMeshProUGUI? _selectionStatusLabel;
    private static Button? _deleteSelectedButton;
    private static Button? _deleteAllButton;
    private static Button? _clearSelectionButton;
    private static bool _isExecutingBatchSale;
    private static bool _runnerRegistered;
    private static bool _startupFeedbackScheduled;
    private static bool _startupFeedbackShown;
    private static bool _bulkRefundWarningLogged;
    private static int _batchProcessedCount;
    private static int _batchTargetCount;
    private static List<PlaneMover>? _pendingBatchRoutes;
    private static int _pendingBatchIndex;
    private static int _pendingBatchSoldCount;
    private static bool _pendingClearAllSelection;
    private static RouteInfoUIController? _pendingRouteInfoUi;
    private static Type? _refundBindingRootType;
    private static List<RefundMemberBinding>? _refundBindings;
    private static int _routeWrapMaintenanceFrame;
    private static bool _mapBoundsInitialized;
    private static float _mapMinX;
    private static float _mapMaxX;
    private static float _mapMinY;
    private static float _mapMaxY;
    private static float _mapWidth;
    private static float _mapHeight;

    public override void Load()
    {
        _log = Log;
        if (EnableRouteSeamWrap)
        {
            _seamWrapDiagnosticsEntry = Config.Bind(
                "Debug",
                "EnableSeamWrapDiagnostics",
                true,
                "Enable detailed seam-wrap diagnostics in BepInEx/LogOutput.log. Useful when wrapped routes render in the wrong place.");
        }

        EnsureRunner();

        var harmony = new Harmony(PluginGuid);

        Patch(harmony,
            typeof(RouteItem).GetMethod(nameof(RouteItem.Fill)),
            postfix: nameof(RouteItemFillPostfix));

        Patch(harmony,
            typeof(RouteItem).GetMethod(nameof(RouteItem.OnDisable), AccessTools.all),
            postfix: nameof(RouteItemOnDisablePostfix));

        Patch(harmony,
            typeof(RoutesStats).GetMethod(nameof(RoutesStats.OnEnable), AccessTools.all),
            postfix: nameof(RoutesStatsOnEnablePostfix));

        Patch(harmony,
            typeof(RoutesStats).GetMethod(nameof(RoutesStats.FillRoutesPanel), AccessTools.all),
            postfix: nameof(RoutesStatsFillRoutesPanelPostfix));

        if (EnableRouteSeamWrap)
        {
            Patch(harmony,
                typeof(PlaneMover).GetMethod(nameof(PlaneMover.RunTheRoute), AccessTools.all),
                postfix: nameof(PlaneMoverRunTheRoutePostfix));

            Patch(harmony,
                typeof(PlaneBehavior).GetMethod(nameof(PlaneBehavior.Run), AccessTools.all),
                postfix: nameof(PlaneBehaviorRunPostfix));
        }

        Patch(harmony,
            AccessTools.Method(typeof(RouteInfoUIController), nameof(RouteInfoUIController.SellRoute), new[] { typeof(PlaneMover), typeof(bool), typeof(bool) }),
            postfix: nameof(RouteInfoControllerSellRouteStaticPostfix));

        ScheduleStartupFeedback();

        Log.LogInfo(
            "Loaded. Open the Routes tab, use the Select toggle on each route row, then use Delete Selected or Delete All to batch-sell routes through the game's normal route-sale flow. " +
            "Bulk route deletes use an 80% refund override. Seam-wrap route rendering is currently disabled. " +
            "A startup confirmation dialog will appear shortly.");
    }

    private static void Patch(Harmony harmony, System.Reflection.MethodInfo? target, string? prefix = null, string? postfix = null)
    {
        if (target == null)
            throw new InvalidOperationException("A required patch target was not found.");

        harmony.Patch(
            target,
            prefix: prefix == null ? null : new HarmonyMethod(typeof(Plugin).GetMethod(prefix, AccessTools.all)),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(Plugin).GetMethod(postfix, AccessTools.all)));
    }

    private static void RoutesStatsOnEnablePostfix(RoutesStats __instance)
    {
        try
        {
            _activeRoutesStats = __instance;
            EnsureRoutesTabUi(__instance);
            PruneQueueToKnownRoutes();
            RefreshRoutesTabUi();
        }
        catch (Exception ex)
        {
            LogError("RoutesStatsOnEnablePostfix failed", ex);
        }
    }

    private static void RoutesStatsFillRoutesPanelPostfix(RoutesStats __instance)
    {
        try
        {
            _activeRoutesStats = __instance;
            EnsureRoutesTabUi(__instance);
            PruneQueueToKnownRoutes();
            RefreshSelectionStatusLabel();
        }
        catch (Exception ex)
        {
            LogError("RoutesStatsFillRoutesPanelPostfix failed", ex);
        }
    }

    private static void PlaneMoverRunTheRoutePostfix(PlaneMover __instance)
    {
        try
        {
            TryApplyRouteSeamWrap(__instance);
        }
        catch (Exception ex)
        {
            LogError("PlaneMoverRunTheRoutePostfix failed", ex);
        }
    }

    private static void PlaneBehaviorRunPostfix(PlaneMover planeMover)
    {
        try
        {
            TryApplyRouteSeamWrap(planeMover);
        }
        catch (Exception ex)
        {
            LogError("PlaneBehaviorRunPostfix failed", ex);
        }
    }

    private static void RouteInfoControllerSellRouteStaticPostfix(PlaneMover pm)
    {
        try
        {
            CleanupWrappedRoute(pm);
        }
        catch (Exception ex)
        {
            LogError("RouteInfoControllerSellRouteStaticPostfix failed", ex);
        }
    }

    private static void RouteItemFillPostfix(RouteItem __instance, PlaneMover route)
    {
        try
        {
            if (__instance == null || route == null)
                return;

            var routeId = GetRouteId(route);
            if (routeId == null)
                return;

            var objectId = GetObjectId(__instance);
            if (!RouteItemStates.TryGetValue(objectId, out var state))
            {
                state = new RouteItemState(
                    __instance,
                    routeId,
                    NormalizeRouteName(__instance._routeName?.text ?? string.Empty),
                    __instance._routeName?.color ?? Color.white);
                RouteItemStates[objectId] = state;
            }
            else
            {
                state.Item = __instance;
                state.BaseName = NormalizeRouteName(__instance._routeName?.text ?? string.Empty);
                state.BaseTextColor = __instance._routeName?.color ?? Color.white;
            }

            EnsureRouteSelectionToggle(__instance, state);
            RefreshRouteItemVisual(__instance);
            RefreshSelectionStatusLabel();
        }
        catch (Exception ex)
        {
            LogError("RouteItemFillPostfix failed", ex);
        }
    }

    private static void RouteItemOnDisablePostfix(RouteItem __instance)
    {
        try
        {
            if (__instance == null)
                return;

            RouteItemStates.Remove(GetObjectId(__instance));
            RefreshSelectionStatusLabel();
        }
        catch (Exception ex)
        {
            LogError("RouteItemOnDisablePostfix failed", ex);
        }
    }

    private static void ToggleQueuedRoute(string routeId, string routeName)
    {
        string message;
        lock (QueueLock)
        {
            if (!QueuedRouteIds.Add(routeId))
            {
                QueuedRouteIds.Remove(routeId);
                message = $"Removed from queue: {routeName} ({QueuedRouteIds.Count} selected)";
            }
            else
            {
                message = $"Added to queue: {routeName} ({QueuedRouteIds.Count} selected)";
            }
        }

        LogInfo(message);
    }

    private static void ToggleQueuedRouteAndRefresh(string routeId, string routeName)
    {
        if (_isExecutingBatchSale)
            return;

        ToggleQueuedRoute(routeId, routeName);
        RefreshRoutesTabUi();
    }

    private static void DeleteSelectedRoutesClicked()
    {
        try
        {
            if (_isExecutingBatchSale)
                return;

            var targets = GetSelectedRouteMovers();
            if (targets.Count == 0)
            {
                LogInfo("Delete Selected requested, but no routes are queued.");
                RefreshRoutesTabUi();
                return;
            }

            SellRoutesUsingVanillaUi(targets, clearAllSelection: false);
        }
        catch (Exception ex)
        {
            LogError("DeleteSelectedRoutesClicked failed", ex);
        }
    }

    private static void DeleteAllRoutesClicked()
    {
        try
        {
            if (_isExecutingBatchSale)
                return;

            var targets = GetAllRouteMovers();
            if (targets.Count == 0)
            {
                LogInfo("Delete All Routes requested, but there are no active routes.");
                RefreshRoutesTabUi();
                return;
            }

            SellRoutesUsingVanillaUi(targets, clearAllSelection: true);
        }
        catch (Exception ex)
        {
            LogError("DeleteAllRoutesClicked failed", ex);
        }
    }

    private static void ClearSelectionClicked()
    {
        lock (QueueLock)
        {
            QueuedRouteIds.Clear();
        }

        LogInfo("Cleared queued routes.");
        RefreshRoutesTabUi();
    }

    private static void SellRoutesUsingVanillaUi(IReadOnlyList<PlaneMover> routes, bool clearAllSelection)
    {
        var routeInfoUi = ResolveRouteInfoUi();
        if (routeInfoUi == null)
        {
            LogInfo("Route UI controller was not available. Unable to sell routes.");
            return;
        }

        var routeSnapshot = DistinctRoutes(routes)
            .Where(RouteStillExists)
            .ToList();

        if (routeSnapshot.Count == 0)
        {
            LogInfo("The selected routes no longer exist.");
            PruneQueueToKnownRoutes();
            RefreshRoutesTabUi();
            return;
        }

        BeginBatchSale(routeSnapshot.Count);

        if (_runner == null || _runner.Pointer == IntPtr.Zero)
        {
            SellRoutesBlocking(routeInfoUi, routeSnapshot, clearAllSelection);
            return;
        }

        _pendingBatchRoutes = routeSnapshot;
        _pendingBatchIndex = 0;
        _pendingBatchSoldCount = 0;
        _pendingClearAllSelection = clearAllSelection;
        _pendingRouteInfoUi = routeInfoUi;
    }

    private static RouteInfoUIController? ResolveRouteInfoUi()
    {
        var gameManager = GameManager.Instance;
        if (gameManager == null || gameManager.uiMenuEvent == null)
            return null;

        return gameManager.uiMenuEvent.routeInfoUIController;
    }

    private static void RefreshRoutesStats()
    {
        if (_activeRoutesStats == null)
            return;

        try
        {
            _activeRoutesStats.ClearData();
            _activeRoutesStats.FillRoutesPanel();
            _activeRoutesStats.FillProfitableRoutesPanel();
        }
        catch (Exception ex)
        {
            LogError("RefreshRoutesStats failed", ex);
        }
    }

    private static void EnsureRoutesTabUi(RoutesStats? routesStats)
    {
        if (routesStats == null)
            return;

        var template = routesStats._totalRoutesText;
        if (template == null)
            return;

        var parent = template.transform.parent;
        if (parent == null)
            return;

        var controlsRoot = parent.Find("FlyCorpBulkControls");
        var controlsGo = controlsRoot != null ? controlsRoot.gameObject : new GameObject("FlyCorpBulkControls");
        controlsGo.transform.SetParent(parent, false);

        var controlsRect = controlsGo.GetComponent<RectTransform>() ?? controlsGo.AddComponent<RectTransform>();
        var templateRect = template.GetComponent<RectTransform>();
        if (templateRect != null)
        {
            templateRect.anchoredPosition = new Vector2(templateRect.anchoredPosition.x, 14f);
            controlsRect.anchorMin = templateRect.anchorMin;
            controlsRect.anchorMax = templateRect.anchorMax;
            controlsRect.pivot = templateRect.pivot;
            controlsRect.sizeDelta = new Vector2(1120f, 44f);
            controlsRect.anchoredPosition = templateRect.anchoredPosition + new Vector2(0f, -36f);
        }

        _selectionStatusLabel = EnsureHeaderLabel(controlsGo.transform, template, "FlyCorpSelectionStatus", new Vector2(-420f, 0f), new Vector2(250f, 34f), TextAlignmentOptions.Left);
        _deleteSelectedButton = EnsureHeaderButton(controlsGo.transform, template, "FlyCorpDeleteSelected", "Delete Selected", new Vector2(-135f, 0f), new Vector2(210f, 34f), DeleteSelectedRoutesClicked);
        _deleteAllButton = EnsureHeaderButton(controlsGo.transform, template, "FlyCorpDeleteAll", "Delete All", new Vector2(90f, 0f), new Vector2(170f, 34f), DeleteAllRoutesClicked);
        _clearSelectionButton = EnsureHeaderButton(controlsGo.transform, template, "FlyCorpClearSelection", "Clear", new Vector2(285f, 0f), new Vector2(120f, 34f), ClearSelectionClicked);
        RefreshSelectionStatusLabel();
        UpdateBulkButtonState();
    }

    private static Button EnsureHeaderButton(Transform parent, TextMeshProUGUI template, string objectName, string label, Vector2 position, Vector2 size, Action action)
    {
        var existing = parent.Find(objectName);
        Button button;
        Image image;
        TextMeshProUGUI? text;

        if (existing != null)
        {
            button = existing.GetComponent<Button>();
            image = existing.GetComponent<Image>();
            text = existing.GetComponentInChildren<TextMeshProUGUI>(true);
        }
        else
        {
            var buttonGo = new GameObject(objectName);
            buttonGo.transform.SetParent(parent, false);
            var rect = buttonGo.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            image = buttonGo.AddComponent<Image>();
            button = buttonGo.AddComponent<Button>();

            text = Object.Instantiate(template, buttonGo.transform);
            text.name = "Label";
        }

        button.name = objectName;
        button.gameObject.SetActive(true);
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener((UnityAction)action);
        image.color = TransparentHitboxColor;

        if (text != null)
        {
            text.text = label;
            text.color = template.color;
            text.fontSize = Math.Max(16f, template.fontSize * 0.38f);
            text.alignment = TextAlignmentOptions.Center;
            ConfigureRect(text.GetComponent<RectTransform>(), Vector2.zero, size);
        }

        var buttonRect = button.GetComponent<RectTransform>();
        ConfigureRect(buttonRect, position, size);

        return button;
    }

    private static TextMeshProUGUI? EnsureHeaderLabel(Transform parent, TextMeshProUGUI template, string objectName, Vector2 position, Vector2 size, TextAlignmentOptions alignment)
    {
        var existing = parent.Find(objectName);
        var label = existing != null
            ? existing.GetComponent<TextMeshProUGUI>()
            : Object.Instantiate(template, parent);

        if (label == null)
            return null;

        label.name = objectName;
        label.gameObject.SetActive(true);
        label.fontSize = Math.Max(16f, template.fontSize * 0.4f);
        label.color = SelectedTextColor;
        label.alignment = alignment;
        ConfigureRect(label.GetComponent<RectTransform>(), position, size);

        return label;
    }

    private static void RefreshRoutesTabUi()
    {
        RemoveDestroyedRouteItems();
        foreach (var state in RouteItemStates.Values.ToList())
            RefreshRouteItemVisual(state.Item);

        RefreshSelectionStatusLabel();
    }

    private static void RefreshSelectionStatusLabel()
    {
        if (_selectionStatusLabel == null)
            return;

        if (_isExecutingBatchSale && _batchTargetCount > 0)
        {
            _selectionStatusLabel.text = $"Deleting: {_batchProcessedCount} / {_batchTargetCount}";
            return;
        }

        var selectedCount = GetQueuedCount();
        var totalCount = GetActiveRouteCount();
        _selectionStatusLabel.text = $"Selected: {selectedCount} / {totalCount}";
    }

    private static void RefreshRouteItemVisual(RouteItem? item)
    {
        if (item == null)
            return;

        if (!RouteItemStates.TryGetValue(GetObjectId(item), out var state))
            return;

        var selected = IsQueued(state.RouteId);
        if (item._routeName != null)
        {
            item._routeName.text = state.BaseName;
            item._routeName.color = selected ? SelectedTextColor : state.BaseTextColor;
        }

        if (state.ToggleLabel != null)
        {
            state.ToggleLabel.text = selected ? "Selected" : "Select";
            state.ToggleLabel.color = selected ? SelectedTextColor : state.BaseTextColor;
        }

        if (state.ToggleButton != null)
        {
            var hitbox = state.ToggleButton.GetComponent<Image>();
            if (hitbox != null)
                hitbox.color = selected ? new Color(1f, 1f, 1f, 0.08f) : TransparentHitboxColor;
        }
    }

    private static void RemoveDestroyedRouteItems()
    {
        foreach (var key in RouteItemStates.Keys.ToList())
        {
            if (!RouteItemStates.TryGetValue(key, out var state))
                continue;

            if (state.Item == null || state.Item.Pointer == IntPtr.Zero)
                RouteItemStates.Remove(key);
        }
    }

    private static List<PlaneMover> GetSelectedRouteMovers()
    {
        var selectedIds = GetQueuedSnapshot();
        return GetAllRouteMovers()
            .Where(route => selectedIds.Contains(GetRouteId(route) ?? string.Empty))
            .ToList();
    }

    private static List<PlaneMover> GetAllRouteMovers()
    {
        var routes = new List<PlaneMover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_activeRoutesStats?._activeRoutes != null)
        {
            foreach (var route in _activeRoutesStats._activeRoutes)
            {
                if (route == null)
                    continue;

                var routeId = GetRouteId(route);
                if (routeId != null && seen.Add(routeId))
                    routes.Add(route);
            }
        }

        if (routes.Count > 0)
            return routes;

        if (PlaneWayData.Instance?.PlaneWaysList == null)
            return routes;

        foreach (var planeWay in PlaneWayData.Instance.PlaneWaysList)
        {
            var route = planeWay.planeMover;
            if (route == null)
                continue;

            var routeId = GetRouteId(route);
            if (routeId != null && seen.Add(routeId))
                routes.Add(route);
        }

        return routes;
    }

    private static IEnumerable<PlaneMover> DistinctRoutes(IEnumerable<PlaneMover> routes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (route == null)
                continue;

            var routeId = GetRouteId(route);
            if (routeId == null || !seen.Add(routeId))
                continue;

            yield return route;
        }
    }

    private static bool RouteStillExists(PlaneMover route)
    {
        var routeId = GetRouteId(route);
        if (routeId == null)
            return false;

        return GetAllRouteMovers().Any(existing => string.Equals(GetRouteId(existing), routeId, StringComparison.OrdinalIgnoreCase));
    }

    private static void PruneQueueToKnownRoutes()
    {
        var liveIds = new HashSet<string>(GetAllRouteMovers().Select(GetRouteId).Where(id => !string.IsNullOrWhiteSpace(id))!, StringComparer.OrdinalIgnoreCase);
        lock (QueueLock)
        {
            QueuedRouteIds.RemoveWhere(id => !liveIds.Contains(id));
        }
    }

    private static void RemoveRouteFromQueue(PlaneMover route)
    {
        var routeId = GetRouteId(route);
        if (routeId == null)
            return;

        lock (QueueLock)
        {
            QueuedRouteIds.Remove(routeId);
        }
    }

    private static HashSet<string> GetQueuedSnapshot()
    {
        lock (QueueLock)
        {
            return new HashSet<string>(QueuedRouteIds, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int GetQueuedCount()
    {
        lock (QueueLock)
        {
            return QueuedRouteIds.Count;
        }
    }

    private static bool IsQueued(string routeId)
    {
        lock (QueueLock)
        {
            return QueuedRouteIds.Contains(routeId);
        }
    }

    private static int GetActiveRouteCount()
    {
        if (_activeRoutesStats?._activeRoutes != null && _activeRoutesStats._activeRoutes.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in _activeRoutesStats._activeRoutes)
            {
                var routeId = GetRouteId(route);
                if (routeId != null)
                    seen.Add(routeId);
            }

            return seen.Count;
        }

        return PlaneWayData.Instance?.PlaneWaysList?.Count ?? 0;
    }

    private static string? GetRouteId(PlaneMover? route)
    {
        if (route == null || route.Pointer == IntPtr.Zero)
            return null;

        return route.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture);
    }

    private static string GetObjectId(Object obj) => obj.Pointer.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    private static string DescribeRoute(PlaneMover route)
    {
        foreach (var state in RouteItemStates.Values)
        {
            if (string.Equals(state.RouteId, GetRouteId(route), StringComparison.OrdinalIgnoreCase))
                return state.BaseName;
        }

        return route.name ?? route.GetType().Name;
    }

    private static void EnsureRouteSelectionToggle(RouteItem item, RouteItemState state)
    {
        TextMeshProUGUI? templateText = item._routeName ?? _activeRoutesStats?._totalRoutesText;
        if (templateText == null)
            return;

        var existing = item.transform.Find("FlyCorpSelectionToggle");
        Button button;
        Image image;
        TextMeshProUGUI? label;

        if (existing != null)
        {
            button = existing.GetComponent<Button>();
            image = existing.GetComponent<Image>();
            label = existing.GetComponentInChildren<TextMeshProUGUI>(true);
        }
        else
        {
            var buttonGo = new GameObject("FlyCorpSelectionToggle");
            buttonGo.transform.SetParent(item.transform, false);
            var rect = buttonGo.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);

            image = buttonGo.AddComponent<Image>();
            button = buttonGo.AddComponent<Button>();

            label = Object.Instantiate(templateText, buttonGo.transform);
            label.name = "Label";
        }

        ConfigureRect(button.GetComponent<RectTransform>(), new Vector2(28f, 0f), new Vector2(120f, 34f));
        image.color = TransparentHitboxColor;

        if (label != null)
        {
            label.fontSize = Math.Max(16f, templateText.fontSize * 0.42f);
            label.alignment = TextAlignmentOptions.Left;
            ConfigureRect(label.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(120f, 34f));
        }

        var routeName = state.BaseName;
        var routeId = state.RouteId;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener((UnityAction)(Action)(() => ToggleQueuedRouteAndRefresh(routeId, routeName)));

        state.ToggleButton = button;
        state.ToggleLabel = label;
    }

    private static void ConfigureRect(RectTransform? rect, Vector2 position, Vector2 size)
    {
        if (rect == null)
            return;

        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void EnsureRunner()
    {
        try
        {
            if (!_runnerRegistered)
            {
                ClassInjector.RegisterTypeInIl2Cpp<BatchRunner>();
                _runnerRegistered = true;
            }

            if (_runner != null && _runner.Pointer != IntPtr.Zero)
                return;

            var go = new GameObject("FlyCorpMultiRouteDeleteRunner");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<BatchRunner>();
        }
        catch (Exception ex)
        {
            LogError("EnsureRunner failed", ex);
        }
    }

    private static void BeginBatchSale(int routeCount)
    {
        _isExecutingBatchSale = true;
        _batchProcessedCount = 0;
        _batchTargetCount = routeCount;
        UpdateBulkButtonState();
        RefreshSelectionStatusLabel();
    }

    private static void FinishBatchSale(bool clearAllSelection, int soldCount)
    {
        _isExecutingBatchSale = false;
        _batchProcessedCount = 0;
        _batchTargetCount = 0;

        if (clearAllSelection)
        {
            lock (QueueLock)
            {
                QueuedRouteIds.Clear();
            }
        }

        PruneQueueToKnownRoutes();
        RefreshRoutesStats();
        RefreshRoutesTabUi();
        UpdateBulkButtonState();

        LogInfo(soldCount > 0
            ? $"Sold {soldCount} route(s) using the game's normal single-route sale flow."
            : "No routes were sold.");
    }

    private static void SellRoutesBlocking(RouteInfoUIController routeInfoUi, List<PlaneMover> routeSnapshot, bool clearAllSelection)
    {
        var soldCount = 0;

        try
        {
            foreach (var route in routeSnapshot)
            {
                _batchProcessedCount++;
                if (!RouteStillExists(route))
                    continue;

                routeInfoUi.Filling(route);
                TryApplyBulkRefundOverride(routeInfoUi);
                routeInfoUi.SellRoute();
                soldCount++;
                RemoveRouteFromQueue(route);
            }
        }
        finally
        {
            FinishBatchSale(clearAllSelection, soldCount);
        }
    }

    private static void ProcessPendingBatch()
    {
        if (!_isExecutingBatchSale || _pendingBatchRoutes == null)
            return;

        try
        {
            var routeInfoUi = _pendingRouteInfoUi ?? ResolveRouteInfoUi();
            if (routeInfoUi == null)
            {
                LogInfo("Route UI controller was not available. Unable to sell routes.");
                CompletePendingBatch();
                return;
            }

            var processedThisFrame = 0;
            while (_pendingBatchRoutes != null &&
                   _pendingBatchIndex < _pendingBatchRoutes.Count &&
                   processedThisFrame < BatchSaleRoutesPerFrame)
            {
                var route = _pendingBatchRoutes[_pendingBatchIndex++];
                _batchProcessedCount = _pendingBatchIndex;

                if (!RouteStillExists(route))
                    continue;

                routeInfoUi.Filling(route);
                TryApplyBulkRefundOverride(routeInfoUi);
                routeInfoUi.SellRoute();
                _pendingBatchSoldCount++;
                processedThisFrame++;
                RemoveRouteFromQueue(route);
            }

            _pendingRouteInfoUi = routeInfoUi;
            RefreshSelectionStatusLabel();

            if (_pendingBatchRoutes == null || _pendingBatchIndex >= _pendingBatchRoutes.Count)
                CompletePendingBatch();
        }
        catch (Exception ex)
        {
            LogError("ProcessPendingBatch failed", ex);
            CompletePendingBatch();
        }
    }

    private static void CompletePendingBatch()
    {
        var clearAllSelection = _pendingClearAllSelection;
        var soldCount = _pendingBatchSoldCount;

        _pendingBatchRoutes = null;
        _pendingBatchIndex = 0;
        _pendingBatchSoldCount = 0;
        _pendingClearAllSelection = false;
        _pendingRouteInfoUi = null;

        FinishBatchSale(clearAllSelection, soldCount);
    }

    private static void UpdateBulkButtonState()
    {
        SetButtonState(_deleteSelectedButton, !_isExecutingBatchSale);
        SetButtonState(_deleteAllButton, !_isExecutingBatchSale);
        SetButtonState(_clearSelectionButton, !_isExecutingBatchSale);
    }

    private static void SetButtonState(Button? button, bool interactable)
    {
        if (button == null)
            return;

        button.interactable = interactable;
        var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            var color = label.color;
            label.color = new Color(color.r, color.g, color.b, interactable ? 1f : 0.45f);
        }
    }

    private static void MaintainWrappedRoutes()
    {
        if (!EnableRouteSeamWrap)
            return;

        WrapActiveRoutePlanes();

        _routeWrapMaintenanceFrame++;
        if (_routeWrapMaintenanceFrame < RouteWrapMaintenanceIntervalFrames)
            return;

        _routeWrapMaintenanceFrame = 0;

        try
        {
            var liveRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var route in GetAllRouteMovers())
            {
                var routeId = GetRouteId(route);
                if (routeId == null)
                    continue;

                liveRouteIds.Add(routeId);
                TryApplyRouteSeamWrap(route);
            }

            foreach (var staleRouteId in WrappedRouteStates.Keys.Where(id => !liveRouteIds.Contains(id)).ToList())
                CleanupWrappedRoute(staleRouteId);
        }
        catch (Exception ex)
        {
            LogError("MaintainWrappedRoutes failed", ex);
        }
    }

    private static void TryApplyRouteSeamWrap(PlaneMover? route)
    {
        if (!EnableRouteSeamWrap)
            return;

        if (route == null || route.Pointer == IntPtr.Zero)
            return;

        var routeId = GetRouteId(route) ?? "unknown";

        if (!TryEnsureMapBounds(out var minX, out var maxX, out var minY, out var maxY))
        {
            LogSeamWrapDiagnostic(routeId, "bounds-missing",
                $"Route {DescribeRoute(route)} [{routeId}] could not calculate map bounds; seam wrap skipped.");
            return;
        }

        if (!TryGetRouteEndpoints(route, out var start, out var end))
        {
            LogSeamWrapDiagnostic(routeId, "endpoints-missing",
                $"Route {DescribeRoute(route)} [{routeId}] could not resolve endpoint coordinates; seam wrap skipped.");
            return;
        }

        var shiftX = ComputeWrapShift(start.x, end.x, maxX - minX);
        if (Mathf.Approximately(shiftX, 0f))
        {
            LogSeamWrapDiagnostic(routeId, "no-wrap",
                $"Route {DescribeRoute(route)} [{routeId}] does not need seam wrap. Start={FormatVector(start)} End={FormatVector(end)} Width={_mapWidth:F3} DeltaX={(end.x - start.x):F3}");
            CleanupWrappedRoute(route);
            return;
        }

        var primaryStart = new Vector3(start.x, start.y, 0f);
        var primaryEnd = new Vector3(end.x + shiftX, end.y, 0f);
        var primaryPaths = CollectRoutePathCreators(route);
        LogSeamWrapDiagnostic(routeId, "route",
            $"Route {DescribeRoute(route)} [{routeId}] wrap requested. Start={FormatVector(start)} End={FormatVector(end)} PrimaryStart={FormatVector(primaryStart)} PrimaryEnd={FormatVector(primaryEnd)} ShiftX={shiftX:F3} BoundsX=[{minX:F3}, {maxX:F3}] BoundsY=[{minY:F3}, {maxY:F3}] RouteTransform={DescribeTransform(route.transform)}");
        for (var i = 0; i < primaryPaths.Count; i++)
            ApplyWrappedBezierPath(primaryPaths[i], primaryStart, primaryEnd, minY, maxY, routeId, $"primary[{i}]");

        var state = GetOrCreateWrappedRouteState(routeId, route, shiftX);
        state.Route = route;
        state.ShiftX = shiftX;

        var visualPath = ResolvePrimaryVisualPath(route);
        if (visualPath != null && visualPath.Pointer != IntPtr.Zero)
        {
            var mirrorPath = EnsureMirrorPathCreator(state, visualPath);
            if (mirrorPath != null && mirrorPath.Pointer != IntPtr.Zero)
            {
                var mirrorOffset = new Vector3(-shiftX, 0f, 0f);
                ApplyWrappedBezierPath(mirrorPath, primaryStart + mirrorOffset, primaryEnd + mirrorOffset, minY, maxY, routeId, "mirror");
            }
        }
        else
        {
            DestroyMirrorPath(state);
        }
    }

    private static WrappedRouteVisualState GetOrCreateWrappedRouteState(string routeId, PlaneMover route, float shiftX)
    {
        if (!WrappedRouteStates.TryGetValue(routeId, out var state))
        {
            state = new WrappedRouteVisualState(route, shiftX);
            WrappedRouteStates[routeId] = state;
            return state;
        }

        return state;
    }

    private static void CleanupWrappedRoute(PlaneMover? route)
    {
        var routeId = GetRouteId(route);
        if (routeId == null)
            return;

        CleanupWrappedRoute(routeId);
    }

    private static void CleanupWrappedRoute(string routeId)
    {
        if (!WrappedRouteStates.TryGetValue(routeId, out var state))
            return;

        DestroyMirrorPath(state);
        WrappedRouteStates.Remove(routeId);
    }

    private static void DestroyMirrorPath(WrappedRouteVisualState state)
    {
        if (state.MirrorObject != null && state.MirrorObject.Pointer != IntPtr.Zero)
            Object.Destroy(state.MirrorObject);

        state.MirrorObject = null;
        state.MirrorPath = null;
    }

    private static PathCreator? EnsureMirrorPathCreator(WrappedRouteVisualState state, PathCreator sourcePath)
    {
        if (state.MirrorPath != null && state.MirrorPath.Pointer != IntPtr.Zero)
            return state.MirrorPath;

        if (sourcePath == null || sourcePath.Pointer == IntPtr.Zero)
            return null;

        var sourceObject = sourcePath.gameObject;
        if (sourceObject == null || sourceObject.Pointer == IntPtr.Zero)
            return null;

        var mirrorObject = Object.Instantiate(sourceObject);
        mirrorObject.name = $"{sourceObject.name} [WrapMirror]";
        mirrorObject.transform.SetParent(sourceObject.transform.parent, false);
        SetLayerRecursively(mirrorObject.transform, 2);

        state.MirrorObject = mirrorObject;
        state.MirrorPath = mirrorObject.GetComponent<PathCreator>();
        return state.MirrorPath;
    }

    private static void SetLayerRecursively(Transform? root, int layer)
    {
        if (root == null)
            return;

        root.gameObject.layer = layer;
        for (var i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }

    private static void WrapActiveRoutePlanes()
    {
        if (!TryEnsureMapBounds(out var minX, out var maxX, out _, out _) || _mapWidth <= 0f)
            return;

        foreach (var entry in WrappedRouteStates.ToList())
        {
            var state = entry.Value;
            var route = state.Route;
            if (route == null || route.Pointer == IntPtr.Zero)
            {
                CleanupWrappedRoute(entry.Key);
                continue;
            }

            if (route.Planes == null)
                continue;

            foreach (var plane in route.Planes)
            {
                if (plane == null || plane.Pointer == IntPtr.Zero)
                    continue;

                var transform = plane.transform;
                if (transform == null)
                    continue;

                var position = transform.position;
                if (state.ShiftX < 0f && position.x < minX)
                {
                    position.x += _mapWidth;
                    transform.position = position;
                }
                else if (state.ShiftX > 0f && position.x > maxX)
                {
                    position.x -= _mapWidth;
                    transform.position = position;
                }
            }
        }
    }

    private static List<PathCreator> CollectRoutePathCreators(PlaneMover route)
    {
        var paths = new List<PathCreator>();
        var seen = new HashSet<long>();

        AddPathCreator(paths, seen, route.Path);
        AddPathCreator(paths, seen, route.TravelLine);

        var planeWay = FindPlaneWay(route);
        AddPathCreator(paths, seen, planeWay?.Line);

        if (route.Planes != null)
        {
            foreach (var plane in route.Planes)
            {
                if (plane == null || plane.Pointer == IntPtr.Zero)
                    continue;

                AddPathCreator(paths, seen, plane._path);
            }
        }

        return paths;
    }

    private static void AddPathCreator(List<PathCreator> paths, HashSet<long> seen, PathCreator? pathCreator)
    {
        if (pathCreator == null || pathCreator.Pointer == IntPtr.Zero)
            return;

        var key = pathCreator.Pointer.ToInt64();
        if (seen.Add(key))
            paths.Add(pathCreator);
    }

    private static PathCreator? ResolvePrimaryVisualPath(PlaneMover route)
    {
        if (route.TravelLine != null && route.TravelLine.Pointer != IntPtr.Zero)
            return route.TravelLine;

        if (route.Path != null && route.Path.Pointer != IntPtr.Zero)
            return route.Path;

        return FindPlaneWay(route)?.Line;
    }

    private static PlaneWay? FindPlaneWay(PlaneMover route)
    {
        if (PlaneWayData.Instance?.PlaneWaysList == null)
            return null;

        foreach (var planeWay in PlaneWayData.Instance.PlaneWaysList)
        {
            if (planeWay == null || planeWay.Pointer == IntPtr.Zero)
                continue;

            if (planeWay.planeMover != null &&
                planeWay.planeMover.Pointer == route.Pointer)
                return planeWay;
        }

        return null;
    }

    private static void ApplyWrappedBezierPath(PathCreator? pathCreator, Vector3 start, Vector3 end, float minY, float maxY, string? routeId = null, string? pathLabel = null)
    {
        if (pathCreator == null || pathCreator.Pointer == IntPtr.Zero)
            return;

        var existingPath = pathCreator.bezierPath;
        var pathSpace = existingPath != null && existingPath.Pointer != IntPtr.Zero
            ? existingPath.Space
            : PathSpace.xy;

        var worldAnchors = BuildRouteAnchors(start, end, minY, maxY);
        var transform = pathCreator.transform;
        var pathAnchors = transform == null
            ? worldAnchors
            : worldAnchors.Select(anchor => transform.InverseTransformPoint(anchor)).ToList();

        var il2CppAnchors = new Il2CppSystem.Collections.Generic.List<Vector3>();
        foreach (var anchor in pathAnchors)
            il2CppAnchors.Add(anchor);

        var il2CppAnchorEnumerable = new Il2CppSystem.Collections.Generic.IEnumerable<Vector3>(il2CppAnchors.Pointer);
        var bezierPath = new BezierPath(il2CppAnchorEnumerable, false, pathSpace);

        if (existingPath != null && existingPath.Pointer != IntPtr.Zero)
        {
            bezierPath.ControlPointMode = existingPath.ControlPointMode;
            bezierPath.AutoControlLength = existingPath.AutoControlLength;
            bezierPath.FlipNormals = existingPath.FlipNormals;
            bezierPath.GlobalNormalsAngle = existingPath.GlobalNormalsAngle;
        }

        pathCreator.bezierPath = bezierPath;

        if (!string.IsNullOrWhiteSpace(routeId))
        {
            var worldAnchorText = string.Join(", ", worldAnchors.Select(FormatVector));
            var localAnchorText = string.Join(", ", pathAnchors.Select(FormatVector));

            LogSeamWrapDiagnostic(routeId!, $"path-{pathLabel ?? GetObjectId(pathCreator)}",
                $"Route [{routeId}] applied wrap path to {pathLabel ?? "path"} {DescribePathCreator(pathCreator)} Space={pathSpace} WorldAnchors=[{worldAnchorText}] PathLocalAnchors=[{localAnchorText}]");
        }
    }

    private static List<Vector3> BuildRouteAnchors(Vector3 start, Vector3 end, float minY, float maxY)
    {
        var midpoint = (start + end) * 0.5f;
        var verticalDirection = midpoint.y >= (minY + maxY) * 0.5f ? 1f : -1f;
        var arcHeight = Mathf.Clamp(Mathf.Abs(end.x - start.x) * 0.35f, _mapHeight * 0.05f, _mapHeight * 0.2f);
        midpoint.y = Mathf.Clamp(midpoint.y + (arcHeight * verticalDirection), minY - (_mapHeight * 0.1f), maxY + (_mapHeight * 0.15f));

        return new List<Vector3>
        {
            start,
            midpoint,
            end
        };
    }

    private static bool TryGetRouteEndpoints(PlaneMover route, out Vector2 start, out Vector2 end)
    {
        start = default;
        end = default;

        var startCity = route.StartCity;
        var endCity = route.DestinationCity;
        return TryGetCityLocation(startCity, out start) &&
               TryGetCityLocation(endCity, out end);
    }

    private static bool TryGetCityLocation(City? city, out Vector2 location)
    {
        location = default;
        if (city == null || city.Pointer == IntPtr.Zero)
            return false;

        try
        {
            location = city.GetLocation();
            if (IsFinite(location))
                return true;
        }
        catch
        {
        }

        try
        {
            location = city.unity2DLocation;
            return IsFinite(location);
        }
        catch
        {
            location = default;
            return false;
        }
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.x) &&
               !float.IsInfinity(value.y);
    }

    private static float ComputeWrapShift(float startX, float endX, float width)
    {
        if (width <= 0f)
            return 0f;

        var delta = endX - startX;
        if (Mathf.Abs(delta) <= width * 0.5f)
            return 0f;

        return delta > 0f ? -width : width;
    }

    private static bool TryEnsureMapBounds(out float minX, out float maxX, out float minY, out float maxY)
    {
        if (!_mapBoundsInitialized || _mapWidth <= 0f || _mapHeight <= 0f)
            RecalculateMapBounds();

        minX = _mapMinX;
        maxX = _mapMaxX;
        minY = _mapMinY;
        maxY = _mapMaxY;
        return _mapBoundsInitialized && _mapWidth > 0f && _mapHeight > 0f;
    }

    private static void RecalculateMapBounds()
    {
        var hasPoint = false;
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var totalPoints = 0;

        void Accumulate(Il2CppSystem.Collections.Generic.List<City>? cities)
        {
            if (cities == null)
                return;

            for (var i = 0; i < cities.Count; i++)
            {
                var city = cities[i];
                if (!TryGetCityLocation(city, out var location))
                    continue;

                hasPoint = true;
                totalPoints++;
                minX = Mathf.Min(minX, location.x);
                maxX = Mathf.Max(maxX, location.x);
                minY = Mathf.Min(minY, location.y);
                maxY = Mathf.Max(maxY, location.y);
            }
        }

        var planeWayData = PlaneWayData.Instance;
        Accumulate(planeWayData?.PlayableCitiesList);
        Accumulate(planeWayData?.PathfindingCitiesList);
        Accumulate(planeWayData?.OpenedCitiesList);

        if (!hasPoint)
        {
            _mapBoundsInitialized = false;
            _mapWidth = 0f;
            _mapHeight = 0f;
            return;
        }

        _mapMinX = minX;
        _mapMaxX = maxX;
        _mapMinY = minY;
        _mapMaxY = maxY;
        _mapWidth = maxX - minX;
        _mapHeight = Mathf.Max(1f, maxY - minY);
        _mapBoundsInitialized = _mapWidth > 0f;

        LogSeamWrapDiagnostic("map", "bounds",
            $"Map bounds recalculated from {totalPoints} city points. X=[{_mapMinX:F3}, {_mapMaxX:F3}] Y=[{_mapMinY:F3}, {_mapMaxY:F3}] Width={_mapWidth:F3} Height={_mapHeight:F3}");
    }

    private static void TryApplyBulkRefundOverride(RouteInfoUIController routeInfoUi)
    {
        try
        {
            var bindings = GetRefundBindings(routeInfoUi.GetType());
            if (bindings.Count == 0)
            {
                LogMissingBulkRefundSupportOnce();
                return;
            }

            if (!TryReadVanillaRefund(routeInfoUi, bindings, out var vanillaRefund) || vanillaRefund == 0)
                return;

            var targetRefund = (ulong)Math.Round(vanillaRefund * BulkSaleRefundMultiplier, MidpointRounding.AwayFromZero);
            if (targetRefund <= vanillaRefund)
                return;

            var wroteAny = false;
            foreach (var binding in bindings)
                wroteAny |= binding.TryWrite(routeInfoUi, targetRefund);

            if (!wroteAny)
                LogMissingBulkRefundSupportOnce();
        }
        catch (Exception ex)
        {
            LogError("TryApplyBulkRefundOverride failed", ex);
        }
    }

    private static IReadOnlyList<RefundMemberBinding> GetRefundBindings(Type rootType)
    {
        if (_refundBindingRootType == rootType && _refundBindings != null)
            return _refundBindings;

        _refundBindingRootType = rootType;
        _refundBindings = DiscoverRefundBindings(rootType);
        return _refundBindings;
    }

    private static List<RefundMemberBinding> DiscoverRefundBindings(Type rootType)
    {
        var bindings = new List<RefundMemberBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRefundBindingsForContainer(bindings, seen, rootType, null, "RouteInfoUIController");

        foreach (var carrier in GetCandidateCarrierMembers(rootType))
        {
            var carrierType = GetMemberType(carrier);
            if (carrierType == null)
                continue;

            AddRefundBindingsForContainer(bindings, seen, carrierType, carrier, carrier.Name);
        }

        return bindings;
    }

    private static void AddRefundBindingsForContainer(
        List<RefundMemberBinding> bindings,
        HashSet<string> seen,
        Type containerType,
        MemberInfo? carrierMember,
        string carrierLabel)
    {
        foreach (var memberName in RefundMemberNames)
        {
            var binding = CreateRefundBinding(containerType, carrierMember, carrierLabel, memberName);
            if (binding == null)
                continue;

            if (seen.Add(binding.CacheKey))
                bindings.Add(binding);
        }
    }

    private static IEnumerable<MemberInfo> GetCandidateCarrierMembers(Type rootType)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var carrierMembers = new List<MemberInfo>();
        try
        {
            carrierMembers.AddRange(rootType.GetFields(flags)
                .Where(field => IsCandidateCarrierType(field.FieldType)));
        }
        catch
        {
        }

        try
        {
            carrierMembers.AddRange(rootType.GetProperties(flags)
                .Where(property =>
                    property.CanRead &&
                    property.GetIndexParameters().Length == 0 &&
                    IsCandidateCarrierType(property.PropertyType)));
        }
        catch
        {
        }

        return carrierMembers
            .OrderBy(member => ScoreCarrierMember(member))
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static int ScoreCarrierMember(MemberInfo member)
    {
        var label = $"{member.Name} {GetMemberType(member)?.Name}";
        var score = 100;
        if (label.IndexOf("refund", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 60;
        if (label.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 40;
        if (label.IndexOf("sell", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 30;
        if (label.IndexOf("price", StringComparison.OrdinalIgnoreCase) >= 0 ||
            label.IndexOf("cost", StringComparison.OrdinalIgnoreCase) >= 0)
            score -= 20;

        return score;
    }

    private static bool IsCandidateCarrierType(Type type)
    {
        return type != typeof(string) &&
               !type.IsPrimitive &&
               !type.IsEnum &&
               !type.IsValueType;
    }

    private static RefundMemberBinding? CreateRefundBinding(Type containerType, MemberInfo? carrierMember, string carrierLabel, string memberName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        try
        {
            var property = containerType.GetProperties(flags)
                .FirstOrDefault(candidate =>
                    candidate.CanRead &&
                    candidate.CanWrite &&
                    candidate.GetIndexParameters().Length == 0 &&
                    string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase) &&
                    IsSupportedRefundNumericType(candidate.PropertyType));

            if (property != null)
                return new RefundMemberBinding(carrierMember, carrierLabel, property, property.PropertyType);
        }
        catch
        {
        }

        try
        {
            var field = containerType.GetFields(flags)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase) &&
                    IsSupportedRefundNumericType(candidate.FieldType));

            if (field != null)
                return new RefundMemberBinding(carrierMember, carrierLabel, field, field.FieldType);
        }
        catch
        {
        }

        return null;
    }

    private static bool TryReadVanillaRefund(RouteInfoUIController routeInfoUi, IReadOnlyList<RefundMemberBinding> bindings, out ulong vanillaRefund)
    {
        foreach (var binding in bindings)
        {
            if (binding.TryRead(routeInfoUi, out vanillaRefund) && vanillaRefund > 0)
                return true;
        }

        vanillaRefund = 0;
        return false;
    }

    private static bool IsSupportedRefundNumericType(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }

    private static Type? GetMemberType(MemberInfo member)
    {
        return member switch
        {
            FieldInfo field => field.FieldType,
            PropertyInfo property => property.PropertyType,
            _ => null
        };
    }

    private static bool TryGetMemberValue(object target, MemberInfo member, out object? value)
    {
        try
        {
            value = member switch
            {
                FieldInfo field => field.GetValue(target),
                PropertyInfo property => property.GetValue(target),
                _ => null
            };
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static bool TrySetMemberValue(object target, MemberInfo member, object value)
    {
        try
        {
            switch (member)
            {
                case FieldInfo field:
                    field.SetValue(target, value);
                    return true;
                case PropertyInfo property:
                    property.SetValue(target, value);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertNumericValue(object? value, out ulong numericValue)
    {
        switch (value)
        {
            case byte v:
                numericValue = v;
                return true;
            case sbyte v when v >= 0:
                numericValue = (ulong)v;
                return true;
            case short v when v >= 0:
                numericValue = (ulong)v;
                return true;
            case ushort v:
                numericValue = v;
                return true;
            case int v when v >= 0:
                numericValue = (ulong)v;
                return true;
            case uint v:
                numericValue = v;
                return true;
            case long v when v >= 0:
                numericValue = (ulong)v;
                return true;
            case ulong v:
                numericValue = v;
                return true;
            case float v when v >= 0:
                numericValue = (ulong)Math.Round(v, MidpointRounding.AwayFromZero);
                return true;
            case double v when v >= 0:
                numericValue = (ulong)Math.Round(v, MidpointRounding.AwayFromZero);
                return true;
            case decimal v when v >= 0:
                numericValue = (ulong)Math.Round(v, MidpointRounding.AwayFromZero);
                return true;
            default:
                numericValue = 0;
                return false;
        }
    }

    private static bool TryConvertRefundValue(ulong value, Type targetType, out object? convertedValue)
    {
        try
        {
            if (targetType == typeof(byte))
            {
                convertedValue = value > byte.MaxValue ? byte.MaxValue : (byte)value;
                return true;
            }

            if (targetType == typeof(sbyte))
            {
                convertedValue = value > (ulong)sbyte.MaxValue ? sbyte.MaxValue : (sbyte)value;
                return true;
            }

            if (targetType == typeof(short))
            {
                convertedValue = value > (ulong)short.MaxValue ? short.MaxValue : (short)value;
                return true;
            }

            if (targetType == typeof(ushort))
            {
                convertedValue = value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;
                return true;
            }

            if (targetType == typeof(int))
            {
                convertedValue = value > int.MaxValue ? int.MaxValue : (int)value;
                return true;
            }

            if (targetType == typeof(uint))
            {
                convertedValue = value > uint.MaxValue ? uint.MaxValue : (uint)value;
                return true;
            }

            if (targetType == typeof(long))
            {
                convertedValue = value > long.MaxValue ? long.MaxValue : (long)value;
                return true;
            }

            if (targetType == typeof(ulong))
            {
                convertedValue = value;
                return true;
            }

            if (targetType == typeof(float))
            {
                convertedValue = (float)value;
                return true;
            }

            if (targetType == typeof(double))
            {
                convertedValue = (double)value;
                return true;
            }

            if (targetType == typeof(decimal))
            {
                convertedValue = (decimal)value;
                return true;
            }
        }
        catch
        {
        }

        convertedValue = null;
        return false;
    }

    private static void LogMissingBulkRefundSupportOnce()
    {
        if (_bulkRefundWarningLogged)
            return;

        _bulkRefundWarningLogged = true;
        LogInfo("Bulk refund override could not resolve the game's refund fields. Batch deletes will use vanilla refunds.");
    }

    private static bool IsSeamWrapDiagnosticsEnabled() => EnableRouteSeamWrap && (_seamWrapDiagnosticsEntry?.Value ?? false);

    private static void LogInfo(string message) => _log?.LogInfo(message);

    private static void LogError(string message, Exception ex) => _log?.LogError($"{message}: {ex}");

    private static void LogSeamWrapDiagnostic(string scope, string category, string message)
    {
        if (!IsSeamWrapDiagnosticsEnabled())
            return;

        var key = $"{scope}:{category}";
        if (SeamWrapDiagnosticSignatures.TryGetValue(key, out var previousMessage) &&
            string.Equals(previousMessage, message, StringComparison.Ordinal))
            return;

        SeamWrapDiagnosticSignatures[key] = message;
        LogInfo($"[SeamWrap] {message}");
    }

    private static string DescribePathCreator(PathCreator? pathCreator)
    {
        if (pathCreator == null || pathCreator.Pointer == IntPtr.Zero)
            return "PathCreator(null)";

        return $"PathCreator[{GetObjectId(pathCreator)}] Name='{pathCreator.name}' Transform={DescribeTransform(pathCreator.transform)}";
    }

    private static string DescribeTransform(Transform? transform)
    {
        if (transform == null)
            return "Transform(null)";

        var parentName = transform.parent == null ? "null" : transform.parent.name;
        return $"Name='{transform.name}' Pos={FormatVector(transform.position)} LocalPos={FormatVector(transform.localPosition)} Scale={FormatVector(transform.localScale)} Parent='{parentName}'";
    }

    private static string FormatVector(Vector2 value) => $"({value.x:F3}, {value.y:F3})";

    private static string FormatVector(Vector3 value) => $"({value.x:F3}, {value.y:F3}, {value.z:F3})";

    private static void ScheduleStartupFeedback()
    {
        if (!EnableStartupFeedback)
            return;

        if (_startupFeedbackShown || _startupFeedbackScheduled)
            return;

        _startupFeedbackScheduled = true;

        var thread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(1500);
                ShowStartupFeedback();
            }
            catch (Exception ex)
            {
                LogError("Startup feedback thread failed", ex);
            }
        })
        {
            IsBackground = true,
            Name = "FlyCorpMultiRouteDelete-StartupFeedback"
        };

        thread.Start();
    }

    private static void ShowStartupFeedback()
    {
        if (_startupFeedbackShown)
            return;

        _startupFeedbackShown = true;

        var message =
            $"{PluginName} loaded successfully.\r\n\r\n" +
            "Open Statistics -> Routes.\r\n" +
            "Use the Select toggle on each route row.\r\n" +
            "Use Delete Selected or Delete All.\r\n\r\n" +
            "Bulk route deletes refund 80% of route value.";

        LogInfo("Startup feedback displayed.");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        try
        {
            MessageBoxW(IntPtr.Zero, message, PluginName, MbOk | MbIconInformation | MbTopMost);
        }
        catch (Exception ex)
        {
            LogError("Failed to show startup confirmation dialog", ex);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static string NormalizeRouteName(string routeName)
    {
        const string selectedPrefix = "[Selected] ";
        return routeName.StartsWith(selectedPrefix, StringComparison.OrdinalIgnoreCase)
            ? routeName[selectedPrefix.Length..]
            : routeName;
    }

    private sealed class RouteItemState
    {
        public RouteItemState(RouteItem item, string routeId, string baseName, Color baseTextColor)
        {
            Item = item;
            RouteId = routeId;
            BaseName = baseName;
            BaseTextColor = baseTextColor;
        }

        public RouteItem Item { get; set; }

        public string RouteId { get; }

        public string BaseName { get; set; }

        public Color BaseTextColor { get; set; }

        public Button? ToggleButton { get; set; }

        public TextMeshProUGUI? ToggleLabel { get; set; }
    }

    private sealed class WrappedRouteVisualState
    {
        public WrappedRouteVisualState(PlaneMover route, float shiftX)
        {
            Route = route;
            ShiftX = shiftX;
        }

        public PlaneMover Route { get; set; }

        public float ShiftX { get; set; }

        public GameObject? MirrorObject { get; set; }

        public PathCreator? MirrorPath { get; set; }
    }

    private sealed class RefundMemberBinding
    {
        public RefundMemberBinding(MemberInfo? carrierMember, string carrierLabel, MemberInfo valueMember, Type valueType)
        {
            CarrierMember = carrierMember;
            CarrierLabel = carrierLabel;
            ValueMember = valueMember;
            ValueType = valueType;
            CacheKey = $"{carrierLabel}.{valueMember.Name}";
        }

        public MemberInfo? CarrierMember { get; }

        public string CarrierLabel { get; }

        public MemberInfo ValueMember { get; }

        public Type ValueType { get; }

        public string CacheKey { get; }

        public bool TryRead(object root, out ulong value)
        {
            value = 0;
            if (!TryResolveTarget(root, out var target) || target == null)
                return false;

            return TryGetMemberValue(target, ValueMember, out var rawValue) &&
                   TryConvertNumericValue(rawValue, out value);
        }

        public bool TryWrite(object root, ulong value)
        {
            if (!TryResolveTarget(root, out var target) || target == null)
                return false;

            return TryConvertRefundValue(value, ValueType, out var convertedValue) &&
                   convertedValue != null &&
                   TrySetMemberValue(target, ValueMember, convertedValue);
        }

        private bool TryResolveTarget(object root, out object? target)
        {
            if (CarrierMember == null)
            {
                target = root;
                return true;
            }

            return TryGetMemberValue(root, CarrierMember, out target) && target != null;
        }
    }

    private sealed class BatchRunner : MonoBehaviour
    {
        public BatchRunner(IntPtr ptr) : base(ptr)
        {
        }

        public BatchRunner() : base(ClassInjector.DerivedConstructorPointer<BatchRunner>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        public void Update()
        {
            ProcessPendingBatch();
            MaintainWrappedRoutes();
        }
    }
}
