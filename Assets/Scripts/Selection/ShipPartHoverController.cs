using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Runtime hover tooltip controller for ship parts.
/// Performs scene raycasts while the pointer is over the viewport and shows
/// a lightweight tooltip near the cursor using ShipViewerUIController.
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipPartHoverController : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField] private Camera raycastCamera;
    [SerializeField] private ShipPartSelectionController partSelectionController;
    [SerializeField] private ShipViewerUIController uiController;

    [Header("Input System")]
    [SerializeField] private InputActionAsset inputActions;
    [SerializeField] private string pointerPositionActionPath = "UI/Point";

    [Header("Hover Raycast")]
    [SerializeField] private LayerMask raycastMask = ~0;
    [SerializeField] private float maxDistance = 5000f;
    [SerializeField] private bool hideWhenPointerOverUi = true;
    [SerializeField] private bool logHoverDetails = false;

    private InputAction pointerPositionAction;
    private bool pointerActionEnabledByThisComponent;
    private string currentDisplayName;
    private string currentModelName;

    private void Awake()
    {
        ResolveReferences();
        ResolveInputActions();
    }

    private void OnEnable()
    {
        ResolveReferences();
        ResolveInputActions();
        EnableResolvedInputActions();
    }

    private void OnDisable()
    {
        DisableResolvedInputActions();
        uiController?.HideHoverTooltip();
        currentDisplayName = null;
        currentModelName = null;
    }

    private void Update()
    {
        ResolveReferences();
        if (raycastCamera == null || uiController == null || partSelectionController == null)
        {
            return;
        }

        Vector2 screenPosition = ReadPointerScreenPosition();
        if (hideWhenPointerOverUi && uiController.IsScreenPositionBlockedByUI(screenPosition))
        {
            HideTooltip();
            return;
        }

        Ray ray = raycastCamera.ScreenPointToRay(screenPosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Ignore))
        {
            HideTooltip();
            return;
        }

        if (!partSelectionController.TryResolveModelName(hit.collider != null ? hit.collider.transform : null, out string modelName))
        {
            modelName = hit.collider != null ? hit.collider.gameObject.name : null;
        }

        if (!uiController.TryGetHoverTooltipContent(modelName, out string displayName, out string secondaryModelName))
        {
            HideTooltip();
            return;
        }

        bool sameContent = string.Equals(currentDisplayName, displayName, System.StringComparison.Ordinal) &&
                           string.Equals(currentModelName, secondaryModelName, System.StringComparison.Ordinal);

        if (!sameContent)
        {
            uiController.ShowHoverTooltip(displayName, secondaryModelName, screenPosition);
            currentDisplayName = displayName;
            currentModelName = secondaryModelName;
        }
        else
        {
            uiController.UpdateHoverTooltipPosition(screenPosition);
        }

        if (logHoverDetails)
        {
            Debug.Log($"[ShipPartHover] hit={hit.collider.gameObject.name}, display={displayName}, model={secondaryModelName}");
        }
    }

    private void ResolveReferences()
    {
        if (raycastCamera == null)
        {
            raycastCamera = Camera.main;
        }

        if (partSelectionController == null)
        {
            partSelectionController = FindAnyObjectByType<ShipPartSelectionController>();
        }

        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }
    }

    private void ResolveInputActions()
    {
        pointerPositionAction = ResolveAction(pointerPositionActionPath);
    }

    private InputAction ResolveAction(string actionPath)
    {
        if (inputActions == null || string.IsNullOrWhiteSpace(actionPath))
        {
            return null;
        }

        return inputActions.FindAction(actionPath, false);
    }

    private void EnableResolvedInputActions()
    {
        pointerActionEnabledByThisComponent = EnableActionIfNeeded(pointerPositionAction);
    }

    private void DisableResolvedInputActions()
    {
        DisableActionIfOwned(pointerPositionAction, pointerActionEnabledByThisComponent);
        pointerActionEnabledByThisComponent = false;
    }

    private Vector2 ReadPointerScreenPosition()
    {
        if (pointerPositionAction != null)
        {
            return pointerPositionAction.ReadValue<Vector2>();
        }

        return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
    }

    private void HideTooltip()
    {
        if (currentDisplayName != null || currentModelName != null)
        {
            uiController?.HideHoverTooltip();
            currentDisplayName = null;
            currentModelName = null;
        }
    }

    private static bool EnableActionIfNeeded(InputAction action)
    {
        if (action == null || action.enabled)
        {
            return false;
        }

        action.Enable();
        return true;
    }

    private static void DisableActionIfOwned(InputAction action, bool owned)
    {
        if (!owned || action == null || !action.enabled)
        {
            return;
        }

        action.Disable();
    }
}
