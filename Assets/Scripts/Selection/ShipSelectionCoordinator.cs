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
            uiController.ModelFocusRequested += HandleModelFocusRequested;
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
            uiController.ModelFocusRequested -= HandleModelFocusRequested;
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

    private void HandleModelFocusRequested(string modelName)
    {
        if (highlightController == null)
        {
            return;
        }

        if (partSelectionController == null || string.IsNullOrWhiteSpace(modelName))
        {
            highlightController.ClearHighlight();
            return;
        }

        if (partSelectionController.TryFindModelObject(modelName, out GameObject targetObject))
        {
            highlightController.HighlightTarget(targetObject);
            return;
        }

        highlightController.ClearHighlight();
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
    }
}
