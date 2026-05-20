using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 舰船点击选中总协调器。
///
/// Responsibilities:
/// 1. 监听 ShipPartSelectionController 的命中事件；
/// 2. 驱动高亮控制器；
/// 3. 保证模型点击、右侧信息、左侧毁伤树展开选中联动执行。
/// </summary>
public sealed class ShipSelectionCoordinator : MonoBehaviour
{
    [SerializeField] private ShipPartSelectionController partSelectionController;
    [SerializeField] private ShipHighlightController highlightController;
    [SerializeField] private ShipViewerUIController uiController;
    [SerializeField] private ShipCameraModeManager cameraModeManager;
    [SerializeField] private ShipFocusTransparencyController transparencyController;
    [SerializeField] private bool focusCameraOnDamageTreeSelection = true;
    [SerializeField] private bool dimNonFocusedObjects = true;

    private bool suppressNextModelFocusRequest;

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        if (partSelectionController != null)
        {
            partSelectionController.ShipPartSelected += HandleShipPartSelected;
        }

        if (uiController != null)
        {
            uiController.DamageTreeNodeSelected += HandleDamageTreeNodeSelected;
            uiController.ModelFocusRequested += HandleModelFocusRequested;
            uiController.ViewModeChanged += HandleViewModeChanged;
            uiController.ToolbarActionRequested += HandleToolbarActionRequested;
        }
    }

    private void OnDisable()
    {
        if (partSelectionController != null)
        {
            partSelectionController.ShipPartSelected -= HandleShipPartSelected;
        }

        if (uiController != null)
        {
            uiController.DamageTreeNodeSelected -= HandleDamageTreeNodeSelected;
            uiController.ModelFocusRequested -= HandleModelFocusRequested;
            uiController.ViewModeChanged -= HandleViewModeChanged;
            uiController.ToolbarActionRequested -= HandleToolbarActionRequested;
        }
    }

    private void HandleShipPartSelected(GameObject hitObject, string modelName, string damageNodeId)
    {
        if (highlightController != null)
        {
            highlightController.HighlightTarget(hitObject);
        }

        Debug.Log($"[ShipSelectionCoordinator] model={modelName}, damageNode={damageNodeId}, object={(hitObject != null ? hitObject.name : "null")}");
    }

    private void HandleDamageTreeNodeSelected(ShipViewerUIController.DamageTreeNode node)
    {
        if (highlightController == null || uiController == null || partSelectionController == null || node == null)
        {
            return;
        }

        List<string> modelNames = new List<string>();
        if (!uiController.TryGetModelNamesByDamageNodeHierarchy(node.nodeId, modelNames))
        {
            highlightController.ClearHighlight();
            return;
        }

        List<GameObject> targetObjects = new List<GameObject>();
        foreach (string modelName in modelNames)
        {
            if (partSelectionController.TryFindModelObject(modelName, out GameObject targetObject))
            {
                targetObjects.Add(targetObject);
            }
        }

        if (targetObjects.Count > 0)
        {
            suppressNextModelFocusRequest = true;
            highlightController.HighlightTargets(targetObjects);
            if (dimNonFocusedObjects && transparencyController != null)
            {
                transparencyController.ApplyFocusTransparency(targetObjects);
            }

            if (focusCameraOnDamageTreeSelection && cameraModeManager != null)
            {
                cameraModeManager.FocusTargets(targetObjects, $"DamageNode={node.nodeId}, Name={node.displayName}, Models={modelNames.Count}, Objects={targetObjects.Count}");
            }

            Debug.Log($"[ShipSelectionCoordinator] Damage node selected. node={node.nodeId}, name={node.displayName}, models={modelNames.Count}, objects={targetObjects.Count}");
        }
        else
        {
            highlightController.ClearHighlight();
            transparencyController?.Restore();
            cameraModeManager?.SwitchToOverview(false);
            Debug.LogWarning($"[ShipSelectionCoordinator] Damage node selected but no model object found. node={node.nodeId}, name={node.displayName}, models={modelNames.Count}");
        }
    }

    private void HandleViewModeChanged(string viewMode)
    {
        RestoreOverviewState();
        Debug.Log($"[ShipSelectionCoordinator] View mode requested. Restored overview camera. viewMode={viewMode}");
    }

    private void HandleToolbarActionRequested(string actionId)
    {
        if (string.Equals(actionId, "clear", System.StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectionStatePreserveCamera();
            Debug.Log("[ShipSelectionCoordinator] Clear action cleared selection state while preserving current camera.");
            return;
        }

        if (string.Equals(actionId, "reset", System.StringComparison.OrdinalIgnoreCase))
        {
            highlightController?.ClearHighlight();
            RestoreOverviewState();
            Debug.Log("[ShipSelectionCoordinator] Reset action cleared selection state and restored overview camera.");
        }
    }

    private void ClearSelectionStatePreserveCamera()
    {
        suppressNextModelFocusRequest = false;
        highlightController?.ClearHighlight();
        transparencyController?.Restore();
    }

    private void RestoreOverviewState()
    {
        suppressNextModelFocusRequest = false;
        transparencyController?.Restore();
        cameraModeManager?.SwitchToOverview(false);
    }

    private void HandleModelFocusRequested(string modelName)
    {
        if (suppressNextModelFocusRequest)
        {
            suppressNextModelFocusRequest = false;
            Debug.Log($"[ShipSelectionCoordinator] Suppressed primary model focus because damage tree group focus is active. model={modelName}");
            return;
        }

        if (highlightController == null)
        {
            return;
        }

        // 毁伤树切换目标时，先无条件清空当前高亮，
        // 即使后续找不到目标，也要保证场景中没有遗留高亮。
        highlightController.ClearHighlight();

        if (partSelectionController == null || string.IsNullOrWhiteSpace(modelName))
        {
            return;
        }

        if (partSelectionController.TryFindModelObject(modelName, out GameObject targetObject))
        {
            highlightController.HighlightTarget(targetObject);
        }
    }

    private void ResolveReferences()
    {
        if (partSelectionController == null)
        {
            partSelectionController = FindAnyObjectByType<ShipPartSelectionController>();
        }

        if (highlightController == null)
        {
            highlightController = FindAnyObjectByType<ShipHighlightController>();
        }

        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }

        if (cameraModeManager == null)
        {
            cameraModeManager = FindAnyObjectByType<ShipCameraModeManager>();
        }

        if (transparencyController == null)
        {
            transparencyController = FindAnyObjectByType<ShipFocusTransparencyController>();
        }
    }
}
