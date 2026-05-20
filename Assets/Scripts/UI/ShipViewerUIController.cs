using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Runtime UI Toolkit controller for the ShipViewer HUD.
///
/// Responsibilities:
/// - Load damage tree nodes from CSV.
/// - Load device details from device-catalog.json.
/// - Render the top toolbar, left damage tree, right information panel and bottom performance bar.
/// - Expose stable public events and methods for camera focus, model picking and performance updates.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public sealed class ShipViewerUIController : MonoBehaviour
{
    [Header("Data Sources")]
    [SerializeField] private string damageTreeCsvPath = "Assets/Data/TreeNodeCsv/damage-tree-nodes.csv";
    [SerializeField] private string deviceCatalogJsonPath = "Assets/Data/InfoJson/device-catalog.json";

    [Header("Behavior")]
    [SerializeField] private bool loadOnEnable = true;
    [SerializeField] private bool logInterfaceEvents = true;

    public event Action<string> ToolbarActionRequested;
    public event Action<string> ViewModeChanged;
    public event Action<string> DamageTreeFilterChanged;
    public event Action<DamageTreeNode> DamageTreeNodeSelected;
    public event Action<string> FocusDamageNodeRequested;
    public event Action<string> ModelFocusRequested;

    private UIDocument uiDocument;
    private VisualElement root;
    private VisualElement damageTreeContainer;
    private Label treeCountBadge;
    private Label treeRootBadge;
    private Label treeLeafBadge;
    private Label treeLinkedBadge;
    private Label damageTreeHint;
    private DropdownField damageTreeFilterDropdown;
    private readonly Dictionary<string, Button> viewModeButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
    private Label currentSelectionLabel;
    private Label infoSourceBadge;
    private Image deviceImage;
    private Label deviceNameLabel;
    private Label deviceIdLabel;
    private Label parentNodeValue;
    private Label materialValue;
    private Label thicknessValue;
    private Label sizeValue;
    private Label functionValue;
    private Label infoEmptyState;
    private Label loadStatusLabel;
    private Label fpsValue;
    private Label frameTimeValue;
    private Label vertexValue;
    private Label triangleValue;
    private Label objectValue;
    private Label memoryValue;
    private VisualElement hoverTooltip;
    private Label hoverTooltipDisplayName;
    private Label hoverTooltipModelName;

    private readonly List<DamageTreeNode> allDamageNodes = new List<DamageTreeNode>();
    private readonly Dictionary<string, DamageTreeNode> damageNodeById = new Dictionary<string, DamageTreeNode>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceInfo> deviceByModelName = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DeviceInfo>> devicesByDamageLeafId = new Dictionary<string, List<DeviceInfo>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceInfo> primaryDeviceByDamageNodeId = new Dictionary<string, DeviceInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly List<VisualElement> selectedTreeRows = new List<VisualElement>();
    private readonly HashSet<string> expandedDamageNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly List<DamageTreeRow> visibleDamageRows = new List<DamageTreeRow>();
    private ListView damageTreeListView;
    private string currentDamageTreeFilter = "全部节点";

    private DamageTreeNode selectedDamageNode;
    private DeviceInfo selectedDeviceInfo;

    public IReadOnlyList<DamageTreeNode> DamageNodes => allDamageNodes;
    public DamageTreeNode SelectedDamageNode => selectedDamageNode;
    public DeviceInfo SelectedDeviceInfo => selectedDeviceInfo;

    private void Awake()
    {
        uiDocument = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        BindVisualElements();
        RegisterToolbarCallbacks();
        ConfigureDropdowns();

        if (loadOnEnable)
        {
            ReloadAllData();
        }
    }

    /// <summary>
    /// Reloads both CSV and JSON data, then rebuilds the left damage tree.
    /// </summary>
    public void ReloadAllData()
    {
        LoadDamageTreeCsv(damageTreeCsvPath);
        LoadDeviceCatalogJson(deviceCatalogJsonPath);
        RebuildDamageTree("全部节点");
        SetLoadStatus($"已加载 {allDamageNodes.Count} 个损伤节点，{deviceByModelName.Count} 条设备详情");
    }

    /// <summary>
    /// Public interface reserved for external model click scripts.
    /// Call this when a 3D model object is picked by name.
    /// </summary>
    public void ShowDeviceByModelName(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            ShowEmptyDeviceState("模型名称为空，无法查询设备详情。", "模型点击");
            return;
        }

        if (deviceByModelName.TryGetValue(modelName.Trim(), out DeviceInfo info))
        {
            UpdateInfoPanel(info, "模型点击");
            SetCurrentSelection($"模型：{info.model_name} / {info.display_name}");
            return;
        }

        ShowEmptyDeviceState($"未找到设备详情：模型名称 = {modelName}", "模型点击");
        SetCurrentSelection($"模型：{modelName}");
    }

    /// <summary>
    /// 判断给定屏幕坐标是否命中了需要拦截场景输入的 UI 区域。
    /// 用于阻止点击 UI 时继续触发场景 Raycast、滚轮缩放或相机环绕。
    /// </summary>
    public bool IsScreenPositionBlockedByUI(Vector2 screenPosition)
    {
        if (root == null)
        {
            return false;
        }

        IPanel panel = root.panel;
        if (panel == null)
        {
            return false;
        }

        Vector2 panelPosition = RuntimePanelUtils.ScreenToPanel(panel, screenPosition);
        VisualElement pickedElement = panel.Pick(panelPosition);
        if (pickedElement == null)
        {
            return false;
        }

        VisualElement current = pickedElement;
        while (current != null)
        {
            if (current is Button ||
                current is DropdownField ||
                current is ScrollView ||
                current.name == "TopToolbar" ||
                current.name == "DamageTreePanel" ||
                current.name == "InfoPanel" ||
                current.name == "PerformanceBar" ||
                current.name == "DamageTreeContainer" ||
                current.name == "InfoScroll" ||
                current.name == "DebugChildVisibilityPanel" ||
                current.name == "DebugChildVisibilitySlider")
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    public void ShowHoverTooltip(string displayName, string modelName, Vector2 screenPosition)
    {
        if (hoverTooltip == null)
        {
            return;
        }

        SetText(hoverTooltipDisplayName, NonEmpty(displayName, "未命名对象"));
        SetText(hoverTooltipModelName, NonEmpty(modelName, "--"));
        hoverTooltip.style.display = DisplayStyle.Flex;
        UpdateHoverTooltipPosition(screenPosition);
    }

    public void UpdateHoverTooltipPosition(Vector2 screenPosition)
    {
        if (hoverTooltip == null || root == null)
        {
            return;
        }

        const float offsetX = 14f;
        const float offsetY = 18f;
        const float fallbackWidth = 220f;
        const float fallbackHeight = 56f;
        const float margin = 12f;

        float panelWidth = hoverTooltip.resolvedStyle.width > 1f ? hoverTooltip.resolvedStyle.width : fallbackWidth;
        float panelHeight = hoverTooltip.resolvedStyle.height > 1f ? hoverTooltip.resolvedStyle.height : fallbackHeight;
        float rootWidth = root.resolvedStyle.width;
        float rootHeight = root.resolvedStyle.height;

        float left = screenPosition.x + offsetX;
        float top = screenPosition.y + offsetY;

        if (left + panelWidth + margin > rootWidth)
        {
            left = Mathf.Max(margin, screenPosition.x - panelWidth - offsetX);
        }

        if (top + panelHeight + margin > rootHeight)
        {
            top = Mathf.Max(margin, screenPosition.y - panelHeight - offsetY);
        }

        hoverTooltip.style.left = left;
        hoverTooltip.style.top = top;
    }

    public void HideHoverTooltip()
    {
        if (hoverTooltip == null)
        {
            return;
        }

        hoverTooltip.style.display = DisplayStyle.None;
    }

    public bool TryGetHoverTooltipContent(string modelName, out string displayName, out string secondaryModelName)
    {
        displayName = null;
        secondaryModelName = null;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        string normalizedName = modelName.Trim();
        secondaryModelName = normalizedName;

        if (deviceByModelName.TryGetValue(normalizedName, out DeviceInfo info) && info != null)
        {
            displayName = NonEmpty(info.display_name, normalizedName);
            secondaryModelName = NonEmpty(info.model_name, normalizedName);
            return true;
        }

        displayName = normalizedName;
        return true;
    }

    /// <summary>
    /// Public helper for external selection systems.
    /// Uses the loaded device catalog to resolve a damage node id by model name.
    /// </summary>
    public bool TryGetDamageNodeIdByModelName(string modelName, out string damageNodeId)
    {
        damageNodeId = null;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (!deviceByModelName.TryGetValue(modelName.Trim(), out DeviceInfo info) || info == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.damage_leaf_id))
        {
            return false;
        }

        damageNodeId = info.damage_leaf_id.Trim();
        return true;
    }

    /// <summary>
    /// 根据损伤节点获取默认聚焦模型名称。
    /// 当一个损伤节点关联多个模型时，返回首个映射项作为默认聚焦对象。
    /// </summary>
    public bool TryGetPrimaryModelNameByDamageNodeId(string damageNodeId, out string modelName)
    {
        modelName = null;
        if (!TryGetPrimaryDeviceByDamageNodeId(damageNodeId, out DeviceInfo info) || info == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.model_name))
        {
            return false;
        }

        modelName = info.model_name.Trim();
        return true;
    }

    /// <summary>
    /// 收集指定毁伤节点及其全部子孙节点关联的模型名称。
    /// 用于外部高亮系统在选择父节点时同时高亮所有子节点模型。
    /// </summary>
    public bool TryGetModelNamesByDamageNodeHierarchy(string damageNodeId, List<string> modelNames)
    {
        if (modelNames == null || string.IsNullOrWhiteSpace(damageNodeId))
        {
            return false;
        }

        if (!damageNodeById.TryGetValue(damageNodeId.Trim(), out DamageTreeNode node) || node == null)
        {
            return false;
        }

        int startCount = modelNames.Count;
        AppendModelNamesByDamageNodeHierarchy(node, modelNames);
        return modelNames.Count > startCount;
    }

    /// <summary>
    /// Public helper for external model/raycast systems.
    /// Expands ancestor nodes, refreshes the visible list, scrolls to the row,
    /// then selects the node and optionally emits focus events.
    /// </summary>
    public bool TryRevealAndSelectDamageNode(string damageNodeId, bool triggerFocus = true, bool refreshInfoPanel = true)
    {
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            return false;
        }

        if (!damageNodeById.TryGetValue(damageNodeId.Trim(), out DamageTreeNode node) || node == null)
        {
            return false;
        }

        ExpandAncestorNodes(node);
        RebuildVisibleDamageRows();

        int rowIndex = FindVisibleDamageRowIndex(node);
        if (rowIndex >= 0 && damageTreeListView != null)
        {
            damageTreeListView.ScrollToItem(rowIndex);
        }

        SelectDamageNode(node, null, triggerFocus, refreshInfoPanel);
        RefreshDamageTreeListView();
        return true;
    }

    /// <summary>
    /// Public interface reserved for external damage tree or camera scripts.
    /// Call this to update the right information panel by damage node id.
    /// </summary>
    public void ShowDeviceByDamageNode(string damageNodeId)
    {
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            ShowEmptyDeviceState("损伤节点为空，无法查询设备详情。", "损伤树");
            return;
        }

        string normalizedId = damageNodeId.Trim();
        if (TryGetPrimaryDeviceByDamageNodeId(normalizedId, out DeviceInfo info))
        {
            int mappedCount = devicesByDamageLeafId.TryGetValue(normalizedId, out List<DeviceInfo> devices) ? devices.Count : 1;
            UpdateInfoPanel(info, mappedCount > 1 ? $"损伤树 · {mappedCount} 项" : "损伤树");
            return;
        }

        if (damageNodeById.TryGetValue(normalizedId, out DamageTreeNode node))
        {
            ShowNodeOnlyInfo(node);
            return;
        }

        ShowEmptyDeviceState($"未找到设备详情：损伤节点 = {damageNodeId}", "损伤树");
    }

    /// <summary>
    /// Public interface reserved for camera/model focus systems.
    /// This only emits the focus event; it does not move camera directly.
    /// </summary>
    public void RequestFocusDamageNode(string damageNodeId)
    {
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            return;
        }

        FocusDamageNodeRequested?.Invoke(damageNodeId.Trim());
        if (logInterfaceEvents)
        {
            Debug.Log($"[ShipViewerUI] FocusDamageNodeRequested: {damageNodeId}");
        }
    }

    /// <summary>
    /// Public interface reserved for model focus systems.
    /// This only emits the model focus event; it does not move camera directly.
    /// </summary>
    public void RequestFocusModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        ModelFocusRequested?.Invoke(modelName.Trim());
        if (logInterfaceEvents)
        {
            Debug.Log($"[ShipViewerUI] ModelFocusRequested: {modelName}");
        }
    }

    /// <summary>
    /// Public bottom-bar update interface for performance monitor scripts.
    /// </summary>
    public void UpdatePerformanceStats(ShipViewerPerformanceStats stats)
    {
        SetText(fpsValue, $"FPS {stats.fps:0}");
        SetText(frameTimeValue, $"Frame {stats.frameTimeMs:0.0} ms");
        SetText(vertexValue, $"顶点 {FormatLargeNumber(stats.vertexCount)}");
        SetText(triangleValue, $"面片 {FormatLargeNumber(stats.triangleCount)}");
        SetText(objectValue, $"对象 {FormatLargeNumber(stats.objectCount)}");
        SetText(memoryValue, $"内存 {stats.memoryMb:0.0} MB");
    }

    /// <summary>
    /// Convenience overload for simple performance updates.
    /// </summary>
    public void UpdatePerformanceStats(float fps, float frameTimeMs, int vertexCount, int triangleCount, int objectCount, float memoryMb)
    {
        UpdatePerformanceStats(new ShipViewerPerformanceStats
        {
            fps = fps,
            frameTimeMs = frameTimeMs,
            vertexCount = vertexCount,
            triangleCount = triangleCount,
            objectCount = objectCount,
            memoryMb = memoryMb
        });
    }

    public void SetLoadStatus(string message)
    {
        SetText(loadStatusLabel, string.IsNullOrWhiteSpace(message) ? "--" : message);
    }

    public void SetCurrentSelection(string message)
    {
        SetText(currentSelectionLabel, string.IsNullOrWhiteSpace(message) ? "未选择模型或损伤节点" : message);
    }

    public void ClearCurrentSelection()
    {
        selectedDamageNode = null;
        selectedDeviceInfo = null;

        foreach (VisualElement selectedRow in selectedTreeRows)
        {
            if (selectedRow == null)
            {
                continue;
            }

            selectedRow.RemoveFromClassList("damage-tree-row-selected");
            selectedRow.RemoveFromClassList("damage-tree-row-ancestor");
            selectedRow.RemoveFromClassList("damage-tree-row-hovered");
            selectedRow.RemoveFromClassList("damage-tree-row-pressed");
        }

        selectedTreeRows.Clear();
        RefreshDamageTreeListView();
        ResetInfoPanelToUnselectedState();
        SetCurrentSelection(null);
    }

    private void BindVisualElements()
    {
        root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("[ShipViewerUI] UIDocument.rootVisualElement is null. Please assign ShipViewerHUD.uxml to the UIDocument.");
            return;
        }

        damageTreeContainer = root.Q<VisualElement>("DamageTreeContainer");
        treeCountBadge = root.Q<Label>("TreeCountBadge");
        damageTreeHint = root.Q<Label>("DamageTreeHint");
        damageTreeFilterDropdown = root.Q<DropdownField>("DamageTreeFilterDropdown");
        treeRootBadge = root.Q<Label>("TreeRootBadge");
        treeLeafBadge = root.Q<Label>("TreeLeafBadge");
        treeLinkedBadge = root.Q<Label>("TreeLinkedBadge");
        currentSelectionLabel = root.Q<Label>("CurrentSelectionLabel");
        infoSourceBadge = root.Q<Label>("InfoSourceBadge");
        deviceImage = root.Q<Image>("DeviceImage");
        deviceNameLabel = root.Q<Label>("DeviceNameLabel");
        deviceIdLabel = root.Q<Label>("DeviceIdLabel");
        parentNodeValue = root.Q<Label>("ParentNodeValue");
        materialValue = root.Q<Label>("MaterialValue");
        thicknessValue = root.Q<Label>("ThicknessValue");
        sizeValue = root.Q<Label>("SizeValue");
        functionValue = root.Q<Label>("FunctionValue");
        infoEmptyState = root.Q<Label>("InfoEmptyState");
        loadStatusLabel = root.Q<Label>("LoadStatusLabel");
        fpsValue = root.Q<Label>("FpsValue");
        frameTimeValue = root.Q<Label>("FrameTimeValue");
        vertexValue = root.Q<Label>("VertexValue");
        triangleValue = root.Q<Label>("TriangleValue");
        objectValue = root.Q<Label>("ObjectValue");
        memoryValue = root.Q<Label>("MemoryValue");
        hoverTooltip = root.Q<VisualElement>("HoverTooltip");
        hoverTooltipDisplayName = root.Q<Label>("HoverTooltipDisplayName");
        hoverTooltipModelName = root.Q<Label>("HoverTooltipModelName");
    }

    private void RegisterToolbarCallbacks()
    {
        RegisterToolbarButton("OpenButton", "open");
        RegisterResetAsIsometricButton("ResetButton");
        HideToolbarButton("WireframeButton");
        RegisterToolbarButton("DebugButton", "debug");
        HideToolbarButton("PerformanceButton");
        HideToolbarButton("AnimationButton");
        HideToolbarButton("ScreenshotButton");
        RegisterToolbarButton("ClearButton", "clear");
        RegisterViewModeButton("FrontViewButton", "前视");
        RegisterViewModeButton("TopViewButton", "俯视");
        RegisterViewModeButton("RightViewButton", "右视");
        RegisterViewModeButton("IsoViewButton", "等轴测");
    }

    private void RegisterViewModeButton(string buttonName, string viewMode)
    {
        Button button = root?.Q<Button>(buttonName);
        if (button == null)
        {
            return;
        }

        viewModeButtons[viewMode] = button;
        button.clicked += () =>
        {
            SetActiveViewMode(viewMode);
            ViewModeChanged?.Invoke(viewMode);
            SetLoadStatus($"视角切换：{viewMode}");
            if (logInterfaceEvents)
            {
                Debug.Log($"[ShipViewerUI] ViewModeChanged: {viewMode}");
            }
        };
    }

    private void SetActiveViewMode(string viewMode)
    {
        foreach (KeyValuePair<string, Button> pair in viewModeButtons)
        {
            if (pair.Value == null)
            {
                continue;
            }

            pair.Value.EnableInClassList("view-mode-button-active", string.Equals(pair.Key, viewMode, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RegisterToolbarButton(string buttonName, string actionId)
    {
        Button button = root?.Q<Button>(buttonName);
        if (button == null)
        {
            return;
        }

        button.clicked += () =>
        {
            bool isClearAction = string.Equals(actionId, "clear", StringComparison.OrdinalIgnoreCase);
            if (isClearAction)
            {
                ClearCurrentSelection();
            }

            ToolbarActionRequested?.Invoke(actionId);
            SetLoadStatus(isClearAction ? "已清空当前选择" : $"工具栏动作：{actionId}");
            if (logInterfaceEvents)
            {
                Debug.Log($"[ShipViewerUI] ToolbarActionRequested: {actionId}");
            }
        };
    }

    private void HideToolbarButton(string buttonName)
    {
        Button button = root?.Q<Button>(buttonName);
        if (button != null)
        {
            button.style.display = DisplayStyle.None;
        }
    }

    private void RegisterResetAsIsometricButton(string buttonName)
    {
        Button button = root?.Q<Button>(buttonName);
        if (button == null)
        {
            return;
        }

        button.clicked += () =>
        {
            SetActiveViewMode("等轴测");
            ViewModeChanged?.Invoke("等轴测");
            ClearCurrentSelection();
            ToolbarActionRequested?.Invoke("reset");
            SetLoadStatus("已重置：等轴测并清空选择");
            if (logInterfaceEvents)
            {
                Debug.Log("[ShipViewerUI] ResetButton remapped to isometric view and cleared selection.");
            }
        };
    }

    private void ConfigureDropdowns()
    {
        SetActiveViewMode("等轴测");

        if (damageTreeFilterDropdown != null)
        {
            damageTreeFilterDropdown.choices = new List<string> { "全部节点", "仅叶子节点", "仅系统层级", "已有关联详情" };
            damageTreeFilterDropdown.index = 0;
            damageTreeFilterDropdown.RegisterValueChangedCallback(evt =>
            {
                DamageTreeFilterChanged?.Invoke(evt.newValue);
                RebuildDamageTree(evt.newValue);
                if (logInterfaceEvents)
                {
                    Debug.Log($"[ShipViewerUI] DamageTreeFilterChanged: {evt.newValue}");
                }
            });
        }
    }

    private void LoadDamageTreeCsv(string path)
    {
        allDamageNodes.Clear();
        damageNodeById.Clear();

        string absolutePath = ResolveProjectPath(path);
        if (!File.Exists(absolutePath))
        {
            SetLoadStatus($"损伤树 CSV 不存在：{path}");
            return;
        }

        string[] lines = File.ReadAllLines(absolutePath);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            List<string> columns = SplitCsvLine(line);
            if (columns.Count < 4)
            {
                Debug.LogWarning($"[ShipViewerUI] Skip malformed CSV line {i + 1}: {line}");
                continue;
            }

            DamageTreeNode node = new DamageTreeNode
            {
                nodeId = columns[0].Trim(),
                parentNodeId = columns[1].Trim(),
                displayName = columns[2].Trim(),
                sortOrder = ParseInt(columns[3], 0)
            };

            if (string.IsNullOrWhiteSpace(node.nodeId))
            {
                continue;
            }

            allDamageNodes.Add(node);
            damageNodeById[node.nodeId] = node;
        }

        foreach (DamageTreeNode node in allDamageNodes)
        {
            node.children.Clear();
        }

        foreach (DamageTreeNode node in allDamageNodes)
        {
            if (!string.IsNullOrWhiteSpace(node.parentNodeId) && damageNodeById.TryGetValue(node.parentNodeId, out DamageTreeNode parent))
            {
                node.parent = parent;
                parent.children.Add(node);
            }
        }

        foreach (DamageTreeNode node in allDamageNodes)
        {
            node.children.Sort((a, b) => a.sortOrder.CompareTo(b.sortOrder));
        }

        allDamageNodes.Sort((a, b) => a.sortOrder.CompareTo(b.sortOrder));
    }

    private void LoadDeviceCatalogJson(string path)
    {
        deviceByModelName.Clear();
        devicesByDamageLeafId.Clear();
        primaryDeviceByDamageNodeId.Clear();

        string absolutePath = ResolveProjectPath(path);
        if (!File.Exists(absolutePath))
        {
            SetLoadStatus($"设备详情 JSON 不存在：{path}");
            return;
        }

        string json = File.ReadAllText(absolutePath);
        DeviceInfoList list;
        try
        {
            list = JsonUtility.FromJson<DeviceInfoList>($"{{\"items\":{json}}}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ShipViewerUI] Failed to parse device catalog JSON: {ex.Message}");
            return;
        }

        if (list?.items == null)
        {
            return;
        }

        foreach (DeviceInfo info in list.items)
        {
            if (info == null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(info.model_name))
            {
                deviceByModelName[info.model_name.Trim()] = info;
            }

            if (!string.IsNullOrWhiteSpace(info.damage_leaf_id))
            {
                string damageLeafId = info.damage_leaf_id.Trim();
                if (!devicesByDamageLeafId.TryGetValue(damageLeafId, out List<DeviceInfo> devices))
                {
                    devices = new List<DeviceInfo>();
                    devicesByDamageLeafId[damageLeafId] = devices;
                }

                devices.Add(info);
                if (!primaryDeviceByDamageNodeId.ContainsKey(damageLeafId))
                {
                    primaryDeviceByDamageNodeId[damageLeafId] = info;
                }
            }
        }
    }

    private void RebuildDamageTree(string filter)
    {
        if (damageTreeContainer == null)
        {
            return;
        }

        currentDamageTreeFilter = string.IsNullOrWhiteSpace(filter) ? "全部节点" : filter;
        selectedTreeRows.Clear();
        EnsureDamageTreeListView();
        RebuildVisibleDamageRows();

        List<DamageTreeNode> roots = allDamageNodes
            .Where(node => string.IsNullOrWhiteSpace(node.parentNodeId) || !damageNodeById.ContainsKey(node.parentNodeId))
            .OrderBy(node => node.sortOrder)
            .ToList();

        int visibleCount = CountVisibleNodes(roots, filter ?? "全部节点");
        int rootCount = roots.Count;
        int leafCount = allDamageNodes.Count(node => node.children.Count == 0);
        int linkedCount = allDamageNodes.Count(node => devicesByDamageLeafId.ContainsKey(node.nodeId));
        SetText(treeCountBadge, $"{visibleCount} 节点");
        SetText(treeRootBadge, $"根 {rootCount}");
        SetText(treeLeafBadge, $"叶 {leafCount}");
        SetText(treeLinkedBadge, $"详情 {linkedCount}");
        SetText(damageTreeHint, $"数据：{Path.GetFileName(damageTreeCsvPath)} + {Path.GetFileName(deviceCatalogJsonPath)} · 点击按钮展开/关闭子节点并触发详情与高亮接口");
    }

    private void EnsureDamageTreeListView()
    {
        if (damageTreeContainer == null || damageTreeListView != null)
        {
            return;
        }

        damageTreeContainer.Clear();
        damageTreeListView = new ListView(visibleDamageRows, 46, MakeDamageTreeButton, BindDamageTreeButton)
        {
            selectionType = SelectionType.None,
            showAlternatingRowBackgrounds = AlternatingRowBackground.None,
            virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            fixedItemHeight = 46,
            horizontalScrollingEnabled = false,
            style =
            {
                flexGrow = 1,
                flexShrink = 1,
                minHeight = 0
            }
        };
        damageTreeListView.AddToClassList("damage-tree-view");
        damageTreeListView.AddToClassList("shipviewer-scrollable");
        damageTreeContainer.Add(damageTreeListView);

        ScrollView listScrollView = damageTreeListView.Q<ScrollView>();
        if (listScrollView != null)
        {
            listScrollView.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
            listScrollView.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        }
    }

    private void RebuildVisibleDamageRows()
    {
        visibleDamageRows.Clear();

        List<DamageTreeNode> roots = allDamageNodes
            .Where(node => string.IsNullOrWhiteSpace(node.parentNodeId) || !damageNodeById.ContainsKey(node.parentNodeId))
            .OrderBy(node => node.sortOrder)
            .ToList();

        AppendDamageButtonRows(visibleDamageRows, roots, 0, currentDamageTreeFilter);

        if (damageTreeListView != null)
        {
            damageTreeListView.itemsSource = visibleDamageRows;
            damageTreeListView.Rebuild();
        }
    }

    private void AppendDamageButtonRows(List<DamageTreeRow> target, IEnumerable<DamageTreeNode> nodes, int depth, string filter)
    {
        foreach (DamageTreeNode node in nodes.OrderBy(node => node.sortOrder))
        {
            bool visible = ShouldDisplayNode(node, filter);
            bool hasVisibleDescendant = HasDisplayableDescendant(node, filter);
            if (!visible && !hasVisibleDescendant)
            {
                continue;
            }

            target.Add(new DamageTreeRow
            {
                node = node,
                depth = depth
            });

            if (IsDamageNodeExpanded(node))
            {
                AppendDamageButtonRows(target, node.children, depth + 1, filter);
            }
        }
    }

    private VisualElement MakeDamageTreeButton()
    {
        Button row = new Button();
        RegisterDamageTreeButtonInteractionCallbacks(row);
        row.clicked += () =>
        {
            if (row.userData is DamageTreeRow damageTreeRow)
            {
                OnDamageTreeButtonClicked(damageTreeRow.node);
            }
        };

        return row;
    }

    /// <summary>
    /// 注册损伤树节点的交互状态回调。
    /// 说明：损伤树节点会在 C# 中写入行内样式，USS 的 :hover / :active 容易被行内样式覆盖；
    /// 因此这里用 Pointer 事件同步写入悬浮/按下样式，确保“整车”“车身外观”等系统节点也有明确反馈。
    /// </summary>
    private void RegisterDamageTreeButtonInteractionCallbacks(Button row)
    {
        row.RegisterCallback<PointerEnterEvent>(_ => ApplyDamageTreeRowInteractionStyles(row, true, false));
        row.RegisterCallback<PointerLeaveEvent>(_ => ApplyDamageTreeRowInteractionStyles(row, false, false));
        row.RegisterCallback<PointerDownEvent>(_ => ApplyDamageTreeRowInteractionStyles(row, true, true));
        row.RegisterCallback<PointerUpEvent>(_ => ApplyDamageTreeRowInteractionStyles(row, true, false));
        row.RegisterCallback<PointerCancelEvent>(_ => ApplyDamageTreeRowInteractionStyles(row, false, false));
    }

    private void BindDamageTreeButton(VisualElement element, int index)
    {
        if (!(element is Button row) || index < 0 || index >= visibleDamageRows.Count)
        {
            return;
        }

        DamageTreeRow damageTreeRow = visibleDamageRows[index];
        DamageTreeNode node = damageTreeRow.node;
        row.userData = damageTreeRow;
        row.text = BuildButtonTitle(node);
        ApplyDamageTreeRowClasses(row, node, damageTreeRow.depth);

        bool isExpanded = IsDamageNodeExpanded(node);
        row.EnableInClassList("damage-tree-row-expanded", isExpanded);

        bool isSelected = selectedDamageNode != null && string.Equals(selectedDamageNode.nodeId, node.nodeId, StringComparison.OrdinalIgnoreCase);
        bool isAncestor = !isSelected && IsAncestorOfSelectedNode(node);
        row.EnableInClassList("damage-tree-row-selected", isSelected);
        row.EnableInClassList("damage-tree-row-ancestor", isAncestor);
        ApplyDamageTreeRowBaseStyles(row, node, isExpanded);
        ApplyDamageTreeRowStateStyles(row, isSelected, isAncestor);
        row.RemoveFromClassList("damage-tree-row-hovered");
        row.RemoveFromClassList("damage-tree-row-pressed");
    }

    private void OnDamageTreeButtonClicked(DamageTreeNode node)
    {
        if (node == null)
        {
            return;
        }

        if (node.children.Count > 0)
        {
            int rowIndex = FindVisibleDamageRowIndex(node);
            int rowDepth = rowIndex >= 0 ? visibleDamageRows[rowIndex].depth : 0;

            if (IsDamageNodeExpanded(node))
            {
                expandedDamageNodeIds.Remove(node.nodeId);
                RemoveExpandedChildRows(rowIndex, rowDepth);
            }
            else
            {
                expandedDamageNodeIds.Add(node.nodeId);
                InsertExpandedChildRows(node, rowIndex, rowDepth + 1);
            }
        }

        SelectDamageNode(node, null, true, true);
        RefreshDamageTreeListView();
    }

    private void ExpandAncestorNodes(DamageTreeNode node)
    {
        DamageTreeNode current = node?.parent;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.nodeId))
            {
                expandedDamageNodeIds.Add(current.nodeId);
            }

            current = current.parent;
        }
    }

    private int FindVisibleDamageRowIndex(DamageTreeNode node)
    {
        if (node == null)
        {
            return -1;
        }

        for (int i = 0; i < visibleDamageRows.Count; i++)
        {
            if (string.Equals(visibleDamageRows[i].node.nodeId, node.nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void RemoveExpandedChildRows(int parentIndex, int parentDepth)
    {
        if (parentIndex < 0)
        {
            RebuildVisibleDamageRows();
            return;
        }

        int removeStart = parentIndex + 1;
        int removeCount = 0;
        while (removeStart + removeCount < visibleDamageRows.Count && visibleDamageRows[removeStart + removeCount].depth > parentDepth)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            visibleDamageRows.RemoveRange(removeStart, removeCount);
        }
    }

    private void InsertExpandedChildRows(DamageTreeNode node, int parentIndex, int childDepth)
    {
        if (node == null)
        {
            return;
        }

        List<DamageTreeRow> appendedRows = new List<DamageTreeRow>();
        AppendDamageButtonRows(appendedRows, node.children, childDepth, currentDamageTreeFilter);

        if (parentIndex < 0)
        {
            RebuildVisibleDamageRows();
            return;
        }

        visibleDamageRows.InsertRange(parentIndex + 1, appendedRows);
    }

    private void RefreshDamageTreeListView()
    {
        if (damageTreeListView == null)
        {
            return;
        }

        damageTreeListView.itemsSource = visibleDamageRows;
        damageTreeListView.Rebuild();
    }

    private bool IsDamageNodeExpanded(DamageTreeNode node)
    {
        return node != null && !string.IsNullOrWhiteSpace(node.nodeId) && expandedDamageNodeIds.Contains(node.nodeId);
    }

    private bool HasDisplayableDescendant(DamageTreeNode node, string filter)
    {
        if (node == null)
        {
            return false;
        }

        foreach (DamageTreeNode child in node.children)
        {
            if (ShouldDisplayNode(child, filter) || HasDisplayableDescendant(child, filter))
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyDamageTreeRowClasses(VisualElement row, DamageTreeNode node, int depth)
    {
        if (row == null || node == null)
        {
            return;
        }

        row.AddToClassList("damage-tree-row");
        row.EnableInClassList("damage-tree-row-system", node.children.Count > 0);
        row.EnableInClassList("damage-tree-row-leaf", node.children.Count == 0);
        row.EnableInClassList("damage-tree-row-linked", devicesByDamageLeafId.ContainsKey(node.nodeId));
        row.style.marginLeft = depth * 14;
    }

    private void ApplyDamageTreeRowBaseStyles(VisualElement row, DamageTreeNode node, bool isExpanded)
    {
        if (row == null || node == null)
        {
            return;
        }

        Color backgroundColor = new Color(0.118f, 0.122f, 0.145f, 0.52f);
        Color foregroundColor = new Color(0.89f, 0.882f, 0.914f, 1f);
        Color borderColor = new Color(1f, 1f, 1f, 0.06f);

        row.style.backgroundColor = new StyleColor(backgroundColor);
        row.style.color = new StyleColor(foregroundColor);
        row.style.borderLeftColor = new StyleColor(borderColor);
        row.style.borderRightColor = new StyleColor(borderColor);
        row.style.borderTopColor = new StyleColor(borderColor);
        row.style.borderBottomColor = new StyleColor(borderColor);
    }

    private void ApplyDamageTreeRowStateStyles(VisualElement row, bool isSelected, bool isAncestor)
    {
        if (row == null)
        {
            return;
        }

        if (isSelected)
        {
            row.style.borderLeftColor = new StyleColor(new Color(0.451f, 1f, 0.541f, 0.92f));
            row.style.borderRightColor = new StyleColor(new Color(0.451f, 1f, 0.541f, 0.92f));
            row.style.borderTopColor = new StyleColor(new Color(0.451f, 1f, 0.541f, 0.92f));
            row.style.borderBottomColor = new StyleColor(new Color(0.451f, 1f, 0.541f, 0.92f));
            return;
        }

        if (isAncestor)
        {
            row.style.borderLeftColor = new StyleColor(new Color(1f, 0.843f, 0f, 0.82f));
            row.style.borderRightColor = new StyleColor(new Color(1f, 0.843f, 0f, 0.82f));
            row.style.borderTopColor = new StyleColor(new Color(1f, 0.843f, 0f, 0.82f));
            row.style.borderBottomColor = new StyleColor(new Color(1f, 0.843f, 0f, 0.82f));
        }
    }

    /// <summary>
    /// 应用损伤树节点的悬浮/按下样式。
    /// 按下态优先级最高，悬浮态次之，默认态会回退到选中/祖先/普通节点样式。
    /// </summary>
    private void ApplyDamageTreeRowInteractionStyles(Button row, bool isHovered, bool isPressed)
    {
        if (row == null || !(row.userData is DamageTreeRow damageTreeRow) || damageTreeRow.node == null)
        {
            return;
        }

        DamageTreeNode node = damageTreeRow.node;
        bool isExpanded = IsDamageNodeExpanded(node);
        bool isSelected = selectedDamageNode != null && string.Equals(selectedDamageNode.nodeId, node.nodeId, StringComparison.OrdinalIgnoreCase);
        bool isAncestor = !isSelected && IsAncestorOfSelectedNode(node);

        ApplyDamageTreeRowBaseStyles(row, node, isExpanded);
        ApplyDamageTreeRowStateStyles(row, isSelected, isAncestor);

        row.EnableInClassList("damage-tree-row-hovered", isHovered && !isPressed);
        row.EnableInClassList("damage-tree-row-pressed", isPressed);

        if (isPressed)
        {
            ApplyDamageTreeRowPressedStyles(row);
            return;
        }

        if (isHovered)
        {
            ApplyDamageTreeRowHoverStyles(row);
        }
    }

    private void ApplyDamageTreeRowHoverStyles(VisualElement row)
    {
        row.style.backgroundColor = new StyleColor(new Color(0.220f, 0.224f, 0.247f, 0.56f));
        row.style.color = new StyleColor(new Color(1f, 0.965f, 0.875f, 1f));
        row.style.borderLeftColor = new StyleColor(new Color(0f, 0.890f, 0.992f, 0.38f));
        row.style.borderRightColor = new StyleColor(new Color(0f, 0.890f, 0.992f, 0.38f));
        row.style.borderTopColor = new StyleColor(new Color(0f, 0.890f, 0.992f, 0.38f));
        row.style.borderBottomColor = new StyleColor(new Color(0f, 0.890f, 0.992f, 0.38f));
    }

    private void ApplyDamageTreeRowPressedStyles(VisualElement row)
    {
        row.style.backgroundColor = new StyleColor(new Color(0f, 0.890f, 0.992f, 0.18f));
        row.style.color = new StyleColor(new Color(1f, 0.965f, 0.875f, 1f));
        row.style.borderLeftColor = new StyleColor(new Color(1f, 0.965f, 0.875f, 0.78f));
        row.style.borderRightColor = new StyleColor(new Color(1f, 0.965f, 0.875f, 0.78f));
        row.style.borderTopColor = new StyleColor(new Color(1f, 0.965f, 0.875f, 0.78f));
        row.style.borderBottomColor = new StyleColor(new Color(1f, 0.965f, 0.875f, 0.78f));
    }

    private void SelectDamageNode(DamageTreeNode node, VisualElement rowElement, bool triggerFocus, bool refreshInfoPanel)
    {
        if (node == null)
        {
            return;
        }

        foreach (VisualElement selectedRow in selectedTreeRows)
        {
            if (selectedRow != null)
            {
                selectedRow.RemoveFromClassList("damage-tree-row-selected");
                selectedRow.RemoveFromClassList("damage-tree-row-ancestor");
            }
        }

        selectedTreeRows.Clear();
        if (rowElement != null)
        {
            rowElement.AddToClassList("damage-tree-row-selected");
            selectedTreeRows.Add(rowElement);
        }

        selectedDamageNode = node;
        DamageTreeNodeSelected?.Invoke(node);

        if (refreshInfoPanel)
        {
            SetCurrentSelection($"损伤节点：{node.displayName} ({node.nodeId})");
            ShowDeviceByDamageNode(node.nodeId);
        }

        if (triggerFocus)
        {
            RequestFocusDamageNode(node.nodeId);
            if (TryGetPrimaryModelNameByDamageNodeId(node.nodeId, out string modelName))
            {
                RequestFocusModel(modelName);
            }
        }
    }

    private bool ShouldDisplayNode(DamageTreeNode node, string filter)
    {
        switch (filter)
        {
            case "仅叶子节点":
                return node.children.Count == 0;
            case "仅系统层级":
                return node.children.Count > 0;
            case "已有关联详情":
                return devicesByDamageLeafId.ContainsKey(node.nodeId);
            case "全部节点":
            default:
                return true;
        }
    }

    private int CountVisibleNodes(IEnumerable<DamageTreeNode> roots, string filter)
    {
        int count = 0;
        foreach (DamageTreeNode node in roots)
        {
            CountVisibleNodeRecursive(node, filter, ref count);
        }

        return count;
    }

    private void CountVisibleNodeRecursive(DamageTreeNode node, string filter, ref int count)
    {
        if (node == null)
        {
            return;
        }

        if (ShouldDisplayNode(node, filter))
        {
            count++;
        }

        foreach (DamageTreeNode child in node.children)
        {
            CountVisibleNodeRecursive(child, filter, ref count);
        }
    }

    private string BuildButtonTitle(DamageTreeNode node)
    {
        if (node == null)
        {
            return "--";
        }

        return NonEmpty(node.displayName, "未命名节点");
    }

    private void UpdateInfoPanel(DeviceInfo info, string source)
    {
        selectedDeviceInfo = info;
        DamageTreeNode node = ResolveDamageNode(info?.damage_leaf_id);
        bool isLeafNode = node == null || node.children.Count == 0;
        SetText(infoSourceBadge, isLeafNode ? "设备" : "层级");
        SetText(deviceNameLabel, node != null ? NonEmpty(node.displayName, NonEmpty(info.display_name, "未命名节点")) : NonEmpty(info.display_name, "未命名设备"));
        SetText(deviceIdLabel, $"节点ID：{NonEmpty(info.damage_leaf_id, "--")}");
        SetText(parentNodeValue, $"父节点ID：{NonEmpty(node?.parentNodeId, "--")}");
        SetText(materialValue, NonEmpty(info.material, "--"));
        SetText(thicknessValue, NonEmpty(info.thickness, "--"));
        SetText(sizeValue, NonEmpty(info.size, "--"));
        SetText(functionValue, NonEmpty(info.function, "--"));
        SetText(infoEmptyState, node != null
            ? $"节点名称：{node.displayName} · 模型：{NonEmpty(info.model_name, "--")} · 关联映射：{BuildMappedModelSummary(info.damage_leaf_id)}"
            : $"模型：{NonEmpty(info.model_name, "--")} · 关联映射：{BuildMappedModelSummary(info.damage_leaf_id)}");
        TryLoadDeviceImage(info.image_path);
    }

    private void ShowNodeOnlyInfo(DamageTreeNode node)
    {
        selectedDeviceInfo = null;
        bool isLeafNode = node == null || node.children.Count == 0;
        SetText(infoSourceBadge, isLeafNode ? "设备" : "层级");
        SetText(deviceNameLabel, NonEmpty(node?.displayName, "未命名节点"));
        SetText(deviceIdLabel, $"节点ID：{NonEmpty(node?.nodeId, "--")}");
        SetText(parentNodeValue, $"父节点ID：{NonEmpty(node?.parentNodeId, "--")}");
        SetText(materialValue, "待补充");
        SetText(thicknessValue, "待补充");
        SetText(sizeValue, "待补充");
        SetText(functionValue, "待补充");
        SetText(infoEmptyState, $"节点名称：{NonEmpty(node?.displayName, "--")} · 父节点ID：{NonEmpty(node?.parentNodeId, "--")} · 关联映射：{BuildMappedModelSummary(node?.nodeId)}");
        if (deviceImage != null)
        {
            deviceImage.image = null;
        }
    }

    private void ResetInfoPanelToUnselectedState()
    {
        SetText(infoSourceBadge, "等待选择");
        SetText(deviceNameLabel, "未选择节点");
        SetText(deviceIdLabel, "节点ID：--");
        SetText(parentNodeValue, "父节点ID：--");
        SetText(materialValue, "--");
        SetText(thicknessValue, "--");
        SetText(sizeValue, "--");
        SetText(functionValue, "--");
        SetText(infoEmptyState, "数据来自 device-catalog.json；未匹配时会显示空状态，不中断交互。");
        if (deviceImage != null)
        {
            deviceImage.image = null;
        }
    }

    private void ShowEmptyDeviceState(string message, string source)
    {
        selectedDeviceInfo = null;
        SetText(infoSourceBadge, source);
        SetText(deviceNameLabel, "未找到节点");
        SetText(deviceIdLabel, "节点ID：--");
        SetText(parentNodeValue, "父节点ID：--");
        SetText(materialValue, "待补充");
        SetText(thicknessValue, "待补充");
        SetText(sizeValue, "待补充");
        SetText(functionValue, message);
        SetText(infoEmptyState, message);
        if (deviceImage != null)
        {
            deviceImage.image = null;
        }
    }

    private DamageTreeNode ResolveDamageNode(string damageNodeId)
    {
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            return null;
        }

        damageNodeById.TryGetValue(damageNodeId.Trim(), out DamageTreeNode node);
        return node;
    }

    private bool TryGetPrimaryDeviceByDamageNodeId(string damageNodeId, out DeviceInfo info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            return false;
        }

        return primaryDeviceByDamageNodeId.TryGetValue(damageNodeId.Trim(), out info) && info != null;
    }

    private void AppendModelNamesByDamageNodeHierarchy(DamageTreeNode node, List<string> modelNames)
    {
        if (node == null || modelNames == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.nodeId) &&
            devicesByDamageLeafId.TryGetValue(node.nodeId.Trim(), out List<DeviceInfo> devices) &&
            devices != null)
        {
            foreach (DeviceInfo device in devices)
            {
                if (device == null || string.IsNullOrWhiteSpace(device.model_name))
                {
                    continue;
                }

                string modelName = device.model_name.Trim();
                if (!modelNames.Contains(modelName, StringComparer.OrdinalIgnoreCase))
                {
                    modelNames.Add(modelName);
                }
            }
        }

        foreach (DamageTreeNode child in node.children)
        {
            AppendModelNamesByDamageNodeHierarchy(child, modelNames);
        }
    }

    private bool IsAncestorOfSelectedNode(DamageTreeNode node)
    {
        if (node == null || selectedDamageNode == null)
        {
            return false;
        }

        DamageTreeNode current = selectedDamageNode.parent;
        while (current != null)
        {
            if (string.Equals(current.nodeId, node.nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    private string BuildMappedModelSummary(string damageNodeId)
    {
        if (string.IsNullOrWhiteSpace(damageNodeId))
        {
            return "--";
        }

        if (!devicesByDamageLeafId.TryGetValue(damageNodeId.Trim(), out List<DeviceInfo> devices) || devices == null || devices.Count == 0)
        {
            return "无场景模型映射";
        }

        IEnumerable<string> modelNames = devices
            .Where(device => device != null && !string.IsNullOrWhiteSpace(device.model_name))
            .Select(device => device.model_name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        string summary = string.Join("、", modelNames);
        return string.IsNullOrWhiteSpace(summary) ? "无场景模型映射" : summary;
    }

    private void TryLoadDeviceImage(string imagePath)
    {
        if (deviceImage == null)
        {
            return;
        }

        deviceImage.image = null;
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            SetText(infoEmptyState, "设备详情已加载；未配置设备图片。 ");
            return;
        }

        string normalizedPath = imagePath.Replace('\\', '/').Trim();
        string absolutePath = normalizedPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            ? ResolveProjectPath(normalizedPath)
            : Path.Combine(Application.dataPath, normalizedPath).Replace('\\', '/');

        if (!File.Exists(absolutePath))
        {
            SetText(infoEmptyState, $"设备图片不存在：{imagePath}");
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(absolutePath);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (texture.LoadImage(bytes))
            {
                deviceImage.image = texture;
                SetText(infoEmptyState, "设备详情与图片已加载。 ");
            }
        }
        catch (Exception ex)
        {
            SetText(infoEmptyState, $"设备图片加载失败：{ex.Message}");
        }
    }

    private static string ResolveProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Application.dataPath;
        }

        string normalizedPath = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedPath))
        {
            return normalizedPath;
        }

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName?.Replace('\\', '/') ?? Application.dataPath;
        return Path.Combine(projectRoot, normalizedPath).Replace('\\', '/');
    }

    private static List<string> SplitCsvLine(string line)
    {
        List<string> result = new List<string>();
        if (line == null)
        {
            return result;
        }

        bool inQuotes = false;
        string current = string.Empty;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current);
                current = string.Empty;
            }
            else
            {
                current += c;
            }
        }

        result.Add(current);
        return result;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private static void SetText(Label label, string value)
    {
        if (label != null)
        {
            label.text = value;
        }
    }

    private static string NonEmpty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string FormatLargeNumber(int value)
    {
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    [Serializable]
    public sealed class DamageTreeNode
    {
        public string nodeId;
        public string parentNodeId;
        public string displayName;
        public int sortOrder;
        [NonSerialized] public DamageTreeNode parent;
        [NonSerialized] public readonly List<DamageTreeNode> children = new List<DamageTreeNode>();
    }

    [Serializable]
    public sealed class DeviceInfo
    {
        public string model_name;
        public string display_name;
        public string damage_leaf_id;
        public string image_path;
        public string material;
        public string thickness;
        public string size;
        public string function;
    }

    private sealed class DamageTreeRow
    {
        public DamageTreeNode node;
        public int depth;
    }

    [Serializable]
    private sealed class DeviceInfoList
    {
        public DeviceInfo[] items;
    }

    [Serializable]
    public struct ShipViewerPerformanceStats
    {
        public float fps;
        public float frameTimeMs;
        public int vertexCount;
        public int triangleCount;
        public int objectCount;
        public float memoryMb;
    }
}
