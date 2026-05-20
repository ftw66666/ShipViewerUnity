using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Runtime debug helper that toggles visibility for the first N children under a bound root.
///
/// Intended workflow:
/// 1. Bind targetRoot to a model root such as rock or Ship;
/// 2. Click the HUD Debug button to show the debug panel;
/// 3. Move the slider to enable the first N collected children and disable the rest.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(UIDocument))]
public sealed class ShipDebugChildVisibilityController : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Transform targetRoot;
    [SerializeField] private bool includeRoot = false;
    [SerializeField] private bool recursive = true;
    [SerializeField] private bool hideAllOnStart = true;

    [Header("UI")]
    [SerializeField] private ShipViewerUIController uiController;
    [SerializeField] private bool createPanelIfMissing = true;
    [SerializeField] private bool showPanelOnStart = false;

    [Header("Selection Refresh")]
    [SerializeField] private ShipPartSelectionController partSelectionController;
    [SerializeField] private bool rebuildSelectionIndexOnSliderChange = false;

    private readonly List<GameObject> controlledObjects = new List<GameObject>();

    private UIDocument uiDocument;
    private VisualElement root;
    private VisualElement debugPanel;
    private Label summaryLabel;
    private SliderInt visibilitySlider;
    private Button debugButton;
    private int visibleCount;
    private bool panelVisible;
    private bool subscribedToToolbar;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        BindUi();
        SubscribeToolbar();
        RebuildControlledObjects();
        SetPanelVisible(showPanelOnStart);
    }

    private void OnDisable()
    {
        UnsubscribeToolbar();
    }

    public void SetTargetRoot(Transform newTargetRoot)
    {
        targetRoot = newTargetRoot;
        RebuildControlledObjects();
    }

    public void TogglePanel()
    {
        SetPanelVisible(!panelVisible);
    }

    public void SetPanelVisible(bool visible)
    {
        panelVisible = visible;
        if (debugPanel != null)
        {
            debugPanel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
            {
                PositionPanelBelowDebugButton();
            }
        }
    }

    public void RebuildControlledObjects()
    {
        controlledObjects.Clear();

        if (targetRoot != null)
        {
            if (includeRoot)
            {
                controlledObjects.Add(targetRoot.gameObject);
            }

            if (recursive)
            {
                Transform[] transforms = targetRoot.GetComponentsInChildren<Transform>(true);
                foreach (Transform child in transforms)
                {
                    if (child == null || child == targetRoot)
                    {
                        continue;
                    }

                    controlledObjects.Add(child.gameObject);
                }
            }
            else
            {
                for (int i = 0; i < targetRoot.childCount; i++)
                {
                    Transform child = targetRoot.GetChild(i);
                    if (child != null)
                    {
                        controlledObjects.Add(child.gameObject);
                    }
                }
            }
        }

        visibleCount = hideAllOnStart ? 0 : controlledObjects.Count;
        ConfigureSlider();
        ApplyVisibleCount(visibleCount, true);
        UpdateSummaryLabel();
    }

    private void ResolveReferences()
    {
        if (uiDocument == null)
        {
            uiDocument = GetComponent<UIDocument>();
        }

        if (uiController == null)
        {
            uiController = GetComponent<ShipViewerUIController>();
        }

        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }

        if (partSelectionController == null)
        {
            partSelectionController = FindAnyObjectByType<ShipPartSelectionController>();
        }
    }

    private void BindUi()
    {
        root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null)
        {
            return;
        }

        debugPanel = root.Q<VisualElement>("DebugChildVisibilityPanel");
        if (debugPanel == null && createPanelIfMissing)
        {
            debugPanel = CreateDebugPanel();
            root.Add(debugPanel);
        }

        if (debugPanel == null)
        {
            return;
        }

        summaryLabel = debugPanel.Q<Label>("DebugChildVisibilityLabel");
        visibilitySlider = debugPanel.Q<SliderInt>("DebugChildVisibilitySlider");
        debugButton = root.Q<Button>("DebugButton");

        if (visibilitySlider != null)
        {
            visibilitySlider.RegisterValueChangedCallback(evt => ApplyVisibleCount(evt.newValue, false));
        }
    }

    private VisualElement CreateDebugPanel()
    {
        VisualElement panel = new VisualElement
        {
            name = "DebugChildVisibilityPanel"
        };
        panel.AddToClassList("debug-child-panel");
        panel.style.position = Position.Absolute;
        panel.style.width = 420;
        panel.style.paddingLeft = 14;
        panel.style.paddingRight = 14;
        panel.style.paddingTop = 10;
        panel.style.paddingBottom = 10;
        panel.style.backgroundColor = new Color(0.05f, 0.055f, 0.075f, 0.86f);
        panel.style.borderTopLeftRadius = 12;
        panel.style.borderTopRightRadius = 12;
        panel.style.borderBottomLeftRadius = 12;
        panel.style.borderBottomRightRadius = 12;
        panel.style.borderLeftWidth = 1;
        panel.style.borderRightWidth = 1;
        panel.style.borderTopWidth = 1;
        panel.style.borderBottomWidth = 1;
        panel.style.borderLeftColor = new Color(0.74f, 0.96f, 1f, 0.22f);
        panel.style.borderRightColor = new Color(0.74f, 0.96f, 1f, 0.22f);
        panel.style.borderTopColor = new Color(0.74f, 0.96f, 1f, 0.22f);
        panel.style.borderBottomColor = new Color(0.74f, 0.96f, 1f, 0.22f);

        Label label = new Label("未绑定目标")
        {
            name = "DebugChildVisibilityLabel"
        };
        label.style.color = new Color(0.82f, 0.78f, 0.67f, 1f);
        panel.Add(label);

        SliderInt slider = new SliderInt(0, 0)
        {
            name = "DebugChildVisibilitySlider",
            showInputField = true
        };
        slider.style.marginTop = 8;
        panel.Add(slider);

        return panel;
    }

    private void ConfigureSlider()
    {
        if (visibilitySlider == null)
        {
            return;
        }

        visibilitySlider.lowValue = 0;
        visibilitySlider.highValue = controlledObjects.Count;
        visibilitySlider.SetValueWithoutNotify(Mathf.Clamp(visibleCount, 0, controlledObjects.Count));
    }

    private void ApplyVisibleCount(int newVisibleCount, bool forceFullRefresh)
    {
        newVisibleCount = Mathf.Clamp(newVisibleCount, 0, controlledObjects.Count);

        if (forceFullRefresh)
        {
            for (int i = 0; i < controlledObjects.Count; i++)
            {
                SetObjectActive(i, i < newVisibleCount);
            }
        }
        else if (newVisibleCount > visibleCount)
        {
            for (int i = visibleCount; i < newVisibleCount; i++)
            {
                SetObjectActive(i, true);
            }
        }
        else if (newVisibleCount < visibleCount)
        {
            for (int i = newVisibleCount; i < visibleCount; i++)
            {
                SetObjectActive(i, false);
            }
        }

        visibleCount = newVisibleCount;
        if (visibilitySlider != null && visibilitySlider.value != visibleCount)
        {
            visibilitySlider.SetValueWithoutNotify(visibleCount);
        }

        if (rebuildSelectionIndexOnSliderChange && partSelectionController != null)
        {
            partSelectionController.RebuildModelIndex();
        }

        UpdateSummaryLabel();
    }

    private void SetObjectActive(int index, bool active)
    {
        if (index < 0 || index >= controlledObjects.Count)
        {
            return;
        }

        GameObject target = controlledObjects[index];
        if (target != null && target.activeSelf != active)
        {
            target.SetActive(active);
        }
    }

    private void UpdateSummaryLabel()
    {
        if (summaryLabel == null)
        {
            return;
        }

        string rootName = targetRoot != null ? targetRoot.name : "未绑定";
        summaryLabel.text = $"目标：{rootName} · 显示 {visibleCount} / {controlledObjects.Count}";
    }

    private void PositionPanelBelowDebugButton()
    {
        if (debugPanel == null || debugButton == null || root == null)
        {
            return;
        }

        Rect buttonWorldBound = debugButton.worldBound;
        Rect rootWorldBound = root.worldBound;
        float panelLeft = buttonWorldBound.xMin - rootWorldBound.xMin;
        float panelTop = buttonWorldBound.yMax - rootWorldBound.yMin + 8f;
        float maxLeft = Mathf.Max(0f, root.resolvedStyle.width - debugPanel.resolvedStyle.width - 16f);

        debugPanel.style.left = Mathf.Clamp(panelLeft, 16f, maxLeft);
        debugPanel.style.top = panelTop;
        debugPanel.style.bottom = StyleKeyword.Auto;
    }

    private void SubscribeToolbar()
    {
        if (subscribedToToolbar || uiController == null)
        {
            return;
        }

        uiController.ToolbarActionRequested += HandleToolbarActionRequested;
        subscribedToToolbar = true;
    }

    private void UnsubscribeToolbar()
    {
        if (!subscribedToToolbar || uiController == null)
        {
            return;
        }

        uiController.ToolbarActionRequested -= HandleToolbarActionRequested;
        subscribedToToolbar = false;
    }

    private void HandleToolbarActionRequested(string actionId)
    {
        if (string.Equals(actionId, "debug", System.StringComparison.OrdinalIgnoreCase))
        {
            TogglePanel();
        }
    }
}
