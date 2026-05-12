using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 舰船部件点击选中控制器。
///
/// Responsibilities:
/// 1. 从主相机发射 Raycast，命中舰船上的具体对象；
/// 2. 将命中的 GameObject / Transform 映射到 model_name；
/// 3. 调用 UI 控制器展开并选中对应损伤树节点；
/// 4. 预留高亮控制接口，由外部高亮系统接管具体视觉效果；
/// 5. 可选：运行时为可选中的对象自动补 Collider。
/// </summary>
public sealed class ShipPartSelectionController : MonoBehaviour
{
    public enum RuntimeColliderMode
    {
        Disabled,
        MeshCollider
    }

    [Header("Scene References")]
    [SerializeField] private Camera raycastCamera;
    [SerializeField] private Transform shipRoot;
    [SerializeField] private ShipViewerUIController uiController;

    [Header("Input System")]
    [SerializeField, Tooltip("可选：现有 InputActionAsset。若不指定，将回退到 Mouse.current。")]
    private InputActionAsset inputActions;
    [SerializeField] private string clickActionPath = "UI/Click";
    [SerializeField] private string pointerPositionActionPath = "UI/Point";

    [Header("Selection")]
    [SerializeField] private LayerMask raycastMask = ~0;
    [SerializeField] private float maxDistance = 5000f;
    [SerializeField] private bool requireLeftMouseClick = true;
    [SerializeField] private bool logSelection = true;
    [SerializeField] private bool logRaycastDetails = true;

    [Header("Runtime Collider Auto Build")]
    [SerializeField] private RuntimeColliderMode runtimeColliderMode = RuntimeColliderMode.Disabled;
    [SerializeField] private bool autoBuildCollidersOnStart = false;
    [SerializeField] private bool onlyBuildForMappedModels = true;
    [SerializeField] private bool logColliderBuild = true;

    private readonly Dictionary<int, string> modelNameByInstanceId = new Dictionary<int, string>();
    private readonly Dictionary<Transform, string> modelNameByTransform = new Dictionary<Transform, string>();
    private readonly Dictionary<string, Transform> primaryTransformByModelName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

    private InputAction clickAction;
    private InputAction pointerPositionAction;
    private bool clickActionEnabledByThisComponent;
    private bool pointerActionEnabledByThisComponent;
    private bool triedRuntimeColliderBuild;

    public event Action<GameObject, string, string> ShipPartSelected;

    private void Awake()
    {
        ResolveReferences();
        ResolveInputActions();
        RebuildModelIndex();
    }

    private void Start()
    {
        TryBuildRuntimeColliders();
        RebuildModelIndex();
    }

    private void OnEnable()
    {
        ResolveReferences();
        ResolveInputActions();
        EnableResolvedInputActions();
        if (modelNameByInstanceId.Count == 0)
        {
            RebuildModelIndex();
        }
    }

    private void OnDisable()
    {
        DisableResolvedInputActions();
    }

    private void Update()
    {
        if (!triedRuntimeColliderBuild)
        {
            TryBuildRuntimeColliders();
        }

        if (!requireLeftMouseClick)
        {
            return;
        }

        if (WasSelectionPressedThisFrame())
        {
            Vector2 screenPosition = ReadPointerScreenPosition();
            if (IsPointerBlockedByUI(screenPosition))
            {
                return;
            }

            TrySelectFromScreenPosition(screenPosition);
        }
    }

    public void RebuildModelIndex()
    {
        modelNameByInstanceId.Clear();
        modelNameByTransform.Clear();
        primaryTransformByModelName.Clear();

        if (shipRoot == null)
        {
            return;
        }

        Transform[] transforms = shipRoot.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in transforms)
        {
            if (child == null)
            {
                continue;
            }

            string modelName = child.name;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                continue;
            }

            modelNameByTransform[child] = modelName;
            modelNameByInstanceId[child.gameObject.GetInstanceID()] = modelName;
            if (!primaryTransformByModelName.ContainsKey(modelName))
            {
                primaryTransformByModelName[modelName] = child;
            }
        }
    }

    public void BuildRuntimeCollidersNow()
    {
        triedRuntimeColliderBuild = false;
        TryBuildRuntimeColliders();
        RebuildModelIndex();
    }

    public bool TrySelectFromScreenPosition(Vector2 screenPosition)
    {
        if (raycastCamera == null)
        {
            ResolveReferences();
        }

        if (raycastCamera == null)
        {
            return false;
        }

        Ray ray = raycastCamera.ScreenPointToRay(screenPosition);
        if (logRaycastDetails)
        {
            Debug.Log($"[ShipPartSelection] Raycast start: screen={screenPosition}, origin={ray.origin}, direction={ray.direction}, maxDistance={maxDistance}, mask={raycastMask.value}");
        }

        if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, raycastMask, QueryTriggerInteraction.Ignore))
        {
            if (logRaycastDetails)
            {
                Debug.Log("[ShipPartSelection] Raycast miss: no collider hit.");
            }
            return false;
        }

        if (logRaycastDetails)
        {
            Debug.Log($"[ShipPartSelection] Raycast hit: object={hit.collider.gameObject.name}, point={hit.point}, distance={hit.distance}, collider={hit.collider.GetType().Name}");
        }

        return TrySelectHit(hit);
    }

    public bool TrySelectHit(RaycastHit hit)
    {
        if (hit.collider == null)
        {
            return false;
        }

        GameObject hitObject = hit.collider.gameObject;
        if (!TryResolveModelName(hitObject.transform, out string modelName))
        {
            if (logRaycastDetails)
            {
                Debug.LogWarning($"[ShipPartSelection] Raycast hit object but failed to resolve model name: object={hitObject.name}, path={GetTransformPath(hitObject.transform)}");
            }
            return false;
        }

        string damageNodeId = null;
        if (uiController != null)
        {
            uiController.ShowDeviceByModelName(modelName);
            uiController.TryGetDamageNodeIdByModelName(modelName, out damageNodeId);
            if (!string.IsNullOrWhiteSpace(damageNodeId))
            {
                uiController.TryRevealAndSelectDamageNode(damageNodeId, false, false);
            }
        }

        ShipPartSelected?.Invoke(hitObject, modelName, damageNodeId);

        if (logSelection)
        {
            Debug.Log($"[ShipPartSelection] Selected: object={hitObject.name}, model={modelName}, damageNode={damageNodeId}");
        }

        return true;
    }

    public bool TryResolveModelName(Transform hitTransform, out string modelName)
    {
        modelName = null;
        if (hitTransform == null)
        {
            return false;
        }

        Transform current = hitTransform;
        while (current != null)
        {
            if (modelNameByTransform.TryGetValue(current, out modelName))
            {
                return true;
            }

            int instanceId = current.gameObject.GetInstanceID();
            if (modelNameByInstanceId.TryGetValue(instanceId, out modelName))
            {
                return true;
            }

            current = current.parent;
        }

        return false;
    }

    public bool TryFindModelObject(string modelName, out GameObject targetObject)
    {
        targetObject = null;
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (primaryTransformByModelName.Count == 0)
        {
            RebuildModelIndex();
        }

        if (!primaryTransformByModelName.TryGetValue(modelName.Trim(), out Transform targetTransform) || targetTransform == null)
        {
            return false;
        }

        targetObject = targetTransform.gameObject;
        return true;
    }

    private void ResolveReferences()
    {
        if (raycastCamera == null)
        {
            raycastCamera = Camera.main;
        }

        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }
    }

    private void ResolveInputActions()
    {
        clickAction = ResolveAction(clickActionPath);
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
        clickActionEnabledByThisComponent = EnableActionIfNeeded(clickAction);
        pointerActionEnabledByThisComponent = EnableActionIfNeeded(pointerPositionAction);
    }

    private void DisableResolvedInputActions()
    {
        DisableActionIfOwned(clickAction, clickActionEnabledByThisComponent);
        DisableActionIfOwned(pointerPositionAction, pointerActionEnabledByThisComponent);
        clickActionEnabledByThisComponent = false;
        pointerActionEnabledByThisComponent = false;
    }

    private bool WasSelectionPressedThisFrame()
    {
        if (clickAction != null)
        {
            return clickAction.WasPressedThisFrame();
        }

        return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
    }

    private Vector2 ReadPointerScreenPosition()
    {
        if (pointerPositionAction != null)
        {
            return pointerPositionAction.ReadValue<Vector2>();
        }

        return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
    }

    private bool IsPointerBlockedByUI(Vector2 screenPosition)
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return true;
        }

        return uiController != null && uiController.IsScreenPositionBlockedByUI(screenPosition);
    }

    private void TryBuildRuntimeColliders()
    {
        if (triedRuntimeColliderBuild || !autoBuildCollidersOnStart || runtimeColliderMode == RuntimeColliderMode.Disabled)
        {
            triedRuntimeColliderBuild = true;
            return;
        }

        triedRuntimeColliderBuild = true;

        if (shipRoot == null)
        {
            return;
        }

        int createdCount = 0;
        Transform[] transforms = shipRoot.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in transforms)
        {
            if (child == null)
            {
                continue;
            }

            if (onlyBuildForMappedModels && !CanResolveModelFromCatalog(child.name))
            {
                continue;
            }

            if (EnsureRuntimeCollider(child))
            {
                createdCount++;
            }
        }

        if (logColliderBuild)
        {
            Debug.Log($"[ShipPartSelection] Runtime collider build complete. Created={createdCount}, Mode={runtimeColliderMode}");
        }
    }

    private bool CanResolveModelFromCatalog(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || uiController == null)
        {
            return false;
        }

        return uiController.TryGetDamageNodeIdByModelName(modelName, out _);
    }

    private bool EnsureRuntimeCollider(Transform target)
    {
        if (target == null || target.GetComponent<Collider>() != null)
        {
            return false;
        }

        switch (runtimeColliderMode)
        {
            case RuntimeColliderMode.MeshCollider:
                {
                    MeshFilter meshFilter = target.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    {
                        return false;
                    }

                    MeshCollider meshCollider = target.gameObject.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = meshFilter.sharedMesh;
                    return true;
                }
            case RuntimeColliderMode.Disabled:
            default:
                return false;
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

    private static string GetTransformPath(Transform target)
    {
        if (target == null)
        {
            return "<null>";
        }

        System.Text.StringBuilder builder = new System.Text.StringBuilder(target.name);
        Transform current = target.parent;
        while (current != null)
        {
            builder.Insert(0, current.name + "/");
            current = current.parent;
        }

        return builder.ToString();
    }
}
