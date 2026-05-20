using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 毁伤树局部聚焦相机控制器。
///
/// Responsibilities:
/// 1. 接收单个或多个模型对象，计算聚合 Bounds；
/// 2. 根据 Bounds 自动推导适合局部观察的中心点、距离、水平角和垂直角；
/// 3. 驱动独立的 CinemachineCamera + CinemachineOrbitalFollow；
/// 4. 在 Focus 模式下提供与主相机一致的右键 Orbit 和滚轮缩放体验。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CinemachineCamera))]
[RequireComponent(typeof(CinemachineOrbitalFollow))]
[RequireComponent(typeof(CinemachineRotationComposer))]
public sealed class ShipDamageFocusCameraController : MonoBehaviour
{
    [Header("Focus Target")]
    [SerializeField, Tooltip("Focus 相机围绕的锚点。为空时会自动创建运行时锚点。")]
    private Transform focusTarget;

    [Header("Framing")]
    [SerializeField, Min(1f), Tooltip("Focus 相机视场角。")]
    private float fieldOfView = 38f;

    [SerializeField, Range(1.05f, 3f), Tooltip("自动取景的额外留白系数。")]
    private float framingPadding = 1.35f;

    [SerializeField, Min(0.01f), Tooltip("目标尺寸过小时使用的最小半径。")]
    private float minimumFocusRadius = 0.35f;

    [SerializeField, Tooltip("垂直 Orbit 角度范围。")]
    private Vector2 verticalAngleRange = new Vector2(8f, 80f);

    [SerializeField, Tooltip("局部缩放倍率范围。实际距离 = 自动计算距离 × 缩放倍率。")]
    private Vector2 zoomScaleRange = new Vector2(0.45f, 2.6f);

    [Header("Cinemachine Tracking")]
    [SerializeField, Tooltip("Focus 目标移动阻尼。")]
    private Vector3 positionDamping = new Vector3(0.18f, 0.18f, 0.18f);

    [SerializeField, Tooltip("Focus 相机旋转阻尼。")]
    private Vector3 rotationDamping = new Vector3(0f, 0.08f, 0f);

    [Header("Manual Orbit Input")]
    [SerializeField, Tooltip("是否必须按住右键才允许局部 Orbit。")]
    private bool requireRightMouseToOrbit = true;

    [SerializeField, Tooltip("鼠标位于 UI 上方时是否阻止滚轮缩放。右键 Orbit 不受该项影响。")]
    private bool blockZoomWhenPointerOverUi = true;

    [SerializeField, Tooltip("是否反转垂直 Orbit 输入。")]
    private bool invertVerticalInput = false;

    [SerializeField, Min(0.001f), Tooltip("水平环绕速度。")]
    private float horizontalSensitivity = 0.12f;

    [SerializeField, Min(0.001f), Tooltip("垂直环绕速度。")]
    private float verticalSensitivity = 0.10f;

    [SerializeField, Min(0.001f), Tooltip("滚轮缩放速度。")]
    private float zoomSensitivity = 0.08f;

    [Header("Input Actions (Optional)")]
    [SerializeField, Tooltip("可选：现有 InputActionAsset。若不指定，将回退到 Mouse.current。")]
    private InputActionAsset inputActions;

    [SerializeField] private string lookActionPath = "Player/Look";
    [SerializeField] private string orbitModifierActionPath = "UI/RightClick";
    [SerializeField] private string zoomActionPath = "UI/ScrollWheel";

    [Header("Debug")]
    [SerializeField] private bool logFocusEvents = true;

    private CinemachineCamera cinemachineCamera;
    private CinemachineOrbitalFollow orbitalFollow;
    private CinemachineRotationComposer rotationComposer;
    private ShipViewerUIController uiController;

    private InputAction lookAction;
    private InputAction orbitModifierAction;
    private InputAction zoomAction;
    private bool lookActionEnabledByThisComponent;
    private bool orbitModifierEnabledByThisComponent;
    private bool zoomActionEnabledByThisComponent;

    private Bounds currentFocusBounds;
    private bool hasFocus;
    private float currentBaseDistance = 1f;
    private int lastFocusTargetSignature;

    public CinemachineCamera Camera => cinemachineCamera;
    public Transform FocusTarget => focusTarget;
    public bool HasFocus => hasFocus;
    public Bounds CurrentFocusBounds => currentFocusBounds;

    private void Awake()
    {
        CacheComponents();
        ResolveReferences();
        ResolveInputActions();
        EnsureFocusTarget();
        ApplyCinemachineSetup();
    }

    private void OnEnable()
    {
        CacheComponents();
        ResolveReferences();
        ResolveInputActions();
        EnsureFocusTarget();
        ApplyCinemachineSetup();
        EnableResolvedInputActions();
    }

    private void OnDisable()
    {
        DisableResolvedInputActions();
    }

    private void OnValidate()
    {
        fieldOfView = Mathf.Clamp(fieldOfView, 1f, 120f);
        framingPadding = Mathf.Max(1.05f, framingPadding);
        minimumFocusRadius = Mathf.Max(0.01f, minimumFocusRadius);
        horizontalSensitivity = Mathf.Max(0.001f, horizontalSensitivity);
        verticalSensitivity = Mathf.Max(0.001f, verticalSensitivity);
        zoomSensitivity = Mathf.Max(0.001f, zoomSensitivity);

        if (verticalAngleRange.y < verticalAngleRange.x)
        {
            verticalAngleRange.y = verticalAngleRange.x;
        }

        if (zoomScaleRange.x < 0.05f)
        {
            zoomScaleRange.x = 0.05f;
        }

        if (zoomScaleRange.y < zoomScaleRange.x)
        {
            zoomScaleRange.y = zoomScaleRange.x;
        }

        CacheComponents();
        ApplyCinemachineSetup();
    }

    private void LateUpdate()
    {
        if (!hasFocus || orbitalFollow == null)
        {
            return;
        }

        HandleManualOrbitInput();
    }

    public bool FocusTargets(IReadOnlyList<GameObject> targets, Camera referenceCamera, string contextLabel)
    {
        CacheComponents();
        ResolveReferences();
        EnsureFocusTarget();
        ApplyCinemachineSetup();

        if (!TryCalculateBounds(targets, out Bounds bounds, out int rendererCount))
        {
            hasFocus = false;
            if (logFocusEvents)
            {
                Debug.LogWarning($"[ShipFocusCamera] Focus failed. No valid renderers. Context={contextLabel}");
            }
            return false;
        }

        int focusTargetSignature = BuildFocusTargetSignature(targets);
        bool preserveCurrentHorizontalAngle = hasFocus &&
                                              orbitalFollow != null &&
                                              focusTargetSignature == lastFocusTargetSignature;

        currentFocusBounds = bounds;
        hasFocus = true;

        Vector3 center = bounds.center;
        float radius = Mathf.Max(bounds.extents.magnitude, minimumFocusRadius);
        float aspect = referenceCamera != null ? Mathf.Max(0.1f, referenceCamera.aspect) : 16f / 9f;
        float verticalFovRad = Mathf.Deg2Rad * Mathf.Clamp(fieldOfView, 1f, 120f);
        float horizontalFovRad = 2f * Mathf.Atan(Mathf.Tan(verticalFovRad * 0.5f) * aspect);
        float distanceByHeight = bounds.extents.y / Mathf.Tan(verticalFovRad * 0.5f);
        float distanceByWidth = Mathf.Max(bounds.extents.x, bounds.extents.z) / Mathf.Tan(horizontalFovRad * 0.5f);
        currentBaseDistance = Mathf.Max(distanceByHeight, distanceByWidth, radius) * framingPadding;
        currentBaseDistance = Mathf.Max(currentBaseDistance, minimumFocusRadius * 2f);

        Vector3 focusDirection = CalculateReferenceDirection(center, referenceCamera);
        float horizontalAngle = preserveCurrentHorizontalAngle
            ? orbitalFollow.HorizontalAxis.Value
            : Mathf.Atan2(focusDirection.x, focusDirection.z) * Mathf.Rad2Deg;
        float verticalAngle = Mathf.Asin(Mathf.Clamp(focusDirection.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;
        verticalAngle = Mathf.Clamp(Mathf.Abs(verticalAngle), verticalAngleRange.x, verticalAngleRange.y);

        focusTarget.position = center;
        ConfigureLensAndOrbit(currentBaseDistance, horizontalAngle, verticalAngle);
        lastFocusTargetSignature = focusTargetSignature;

        if (logFocusEvents)
        {
            Debug.Log($"[ShipFocusCamera] Focus targets. Context={contextLabel}, Renderers={rendererCount}, Center={center}, Size={bounds.size}, Radius={radius:0.###}, Distance={currentBaseDistance:0.###}, H={horizontalAngle:0.#}, V={verticalAngle:0.#}, PreserveH={preserveCurrentHorizontalAngle}");
        }

        return true;
    }

    public void ClearFocus()
    {
        hasFocus = false;
        lastFocusTargetSignature = 0;
    }

    private void CacheComponents()
    {
        if (cinemachineCamera == null)
        {
            cinemachineCamera = GetComponent<CinemachineCamera>();
        }

        if (orbitalFollow == null)
        {
            orbitalFollow = GetComponent<CinemachineOrbitalFollow>();
        }

        if (rotationComposer == null)
        {
            rotationComposer = GetComponent<CinemachineRotationComposer>();
        }
    }

    private void ResolveReferences()
    {
        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }
    }

    private void EnsureFocusTarget()
    {
        if (focusTarget != null)
        {
            return;
        }

        GameObject targetObject = new GameObject("Ship Focus Camera Target");
        targetObject.hideFlags = HideFlags.DontSave;
        focusTarget = targetObject.transform;
    }

    private void ApplyCinemachineSetup()
    {
        if (cinemachineCamera == null || orbitalFollow == null || rotationComposer == null || focusTarget == null)
        {
            return;
        }

        cinemachineCamera.Follow = focusTarget;
        cinemachineCamera.LookAt = focusTarget;

        LensSettings lens = cinemachineCamera.Lens;
        lens.FieldOfView = fieldOfView;
        lens.NearClipPlane = 0.03f;
        lens.FarClipPlane = 10000f;
        cinemachineCamera.Lens = lens;

        orbitalFollow.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
        orbitalFollow.TargetOffset = Vector3.zero;
        orbitalFollow.TrackerSettings = new TrackerSettings
        {
            BindingMode = BindingMode.LockToTargetWithWorldUp,
            PositionDamping = positionDamping,
            AngularDampingMode = AngularDampingMode.Euler,
            RotationDamping = rotationDamping,
            QuaternionDamping = 0f
        };

        orbitalFollow.HorizontalAxis.Range = new Vector2(-180f, 180f);
        orbitalFollow.HorizontalAxis.Wrap = true;
        orbitalFollow.HorizontalAxis.Recentering.Enabled = false;

        orbitalFollow.VerticalAxis.Range = verticalAngleRange;
        orbitalFollow.VerticalAxis.Wrap = false;
        orbitalFollow.VerticalAxis.Recentering.Enabled = false;

        orbitalFollow.RadialAxis.Range = zoomScaleRange;
        orbitalFollow.RadialAxis.Wrap = false;
        orbitalFollow.RadialAxis.Recentering.Enabled = false;

        rotationComposer.Composition.ScreenPosition = Vector2.zero;
        rotationComposer.TargetOffset = Vector3.zero;
    }

    private void ConfigureLensAndOrbit(float baseDistance, float horizontalAngle, float verticalAngle)
    {
        ApplyCinemachineSetup();

        orbitalFollow.Radius = Mathf.Max(0.01f, baseDistance);
        orbitalFollow.HorizontalAxis.Value = NormalizeHorizontal(horizontalAngle);
        orbitalFollow.VerticalAxis.Value = orbitalFollow.VerticalAxis.ClampValue(verticalAngle);
        orbitalFollow.RadialAxis.Value = 1f;
    }

    private bool TryCalculateBounds(IReadOnlyList<GameObject> targets, out Bounds bounds, out int rendererCount)
    {
        bounds = default;
        rendererCount = 0;
        bool hasBounds = false;

        if (targets == null)
        {
            return false;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            GameObject target = targets[i];
            if (target == null)
            {
                continue;
            }

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }

                rendererCount++;
            }
        }

        return hasBounds;
    }

    private static int BuildFocusTargetSignature(IReadOnlyList<GameObject> targets)
    {
        if (targets == null || targets.Count == 0)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            for (int i = 0; i < targets.Count; i++)
            {
                GameObject target = targets[i];
                hash = (hash * 31) + (target != null ? target.GetInstanceID() : 0);
            }

            return hash;
        }
    }

    private Vector3 CalculateReferenceDirection(Vector3 center, Camera referenceCamera)
    {
        if (referenceCamera != null)
        {
            Vector3 direction = referenceCamera.transform.position - center;
            if (direction.sqrMagnitude > 0.0001f)
            {
                return direction.normalized;
            }
        }

        return new Vector3(0.65f, 0.35f, -0.68f).normalized;
    }

    private void HandleManualOrbitInput()
    {
        bool canOrbit = CanUseOrbitPointer();
        bool pointerBlocked = blockZoomWhenPointerOverUi && IsPointerBlockedByUI(Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero);
        Vector2 lookDelta = ReadLookDelta();
        float zoomDelta = pointerBlocked ? 0f : ReadZoomDelta();
        bool changed = false;

        if (canOrbit && lookDelta.sqrMagnitude > 0.000001f)
        {
            float verticalDirection = invertVerticalInput ? -1f : 1f;
            orbitalFollow.HorizontalAxis.Value = orbitalFollow.HorizontalAxis.ClampValue(
                orbitalFollow.HorizontalAxis.Value + lookDelta.x * horizontalSensitivity);
            orbitalFollow.VerticalAxis.Value = orbitalFollow.VerticalAxis.ClampValue(
                orbitalFollow.VerticalAxis.Value + lookDelta.y * verticalSensitivity * verticalDirection);
            changed = true;
        }

        if (Mathf.Abs(zoomDelta) > 0.000001f)
        {
            orbitalFollow.RadialAxis.Value = orbitalFollow.RadialAxis.ClampValue(
                orbitalFollow.RadialAxis.Value - zoomDelta * zoomSensitivity);
            changed = true;
        }

        if (changed)
        {
            orbitalFollow.HorizontalAxis.Value = orbitalFollow.HorizontalAxis.ClampValue(NormalizeHorizontal(orbitalFollow.HorizontalAxis.Value));
            orbitalFollow.VerticalAxis.Value = orbitalFollow.VerticalAxis.ClampValue(orbitalFollow.VerticalAxis.Value);
            orbitalFollow.RadialAxis.Value = orbitalFollow.RadialAxis.ClampValue(orbitalFollow.RadialAxis.Value);
        }
    }

    private bool CanUseOrbitPointer()
    {
        if (!requireRightMouseToOrbit)
        {
            return true;
        }

        if (orbitModifierAction != null)
        {
            return orbitModifierAction.IsPressed();
        }

        return Mouse.current != null && Mouse.current.rightButton.isPressed;
    }

    private Vector2 ReadLookDelta()
    {
        if (lookAction != null)
        {
            return lookAction.ReadValue<Vector2>();
        }

        return Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
    }

    private float ReadZoomDelta()
    {
        if (zoomAction != null)
        {
            return zoomAction.ReadValue<Vector2>().y;
        }

        return Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
    }

    private bool IsPointerBlockedByUI(Vector2 screenPosition)
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            return true;
        }

        return uiController != null && uiController.IsScreenPositionBlockedByUI(screenPosition);
    }

    private void ResolveInputActions()
    {
        lookAction = ResolveAction(lookActionPath);
        orbitModifierAction = ResolveAction(orbitModifierActionPath);
        zoomAction = ResolveAction(zoomActionPath);
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
        lookActionEnabledByThisComponent = EnableActionIfNeeded(lookAction);
        orbitModifierEnabledByThisComponent = EnableActionIfNeeded(orbitModifierAction);
        zoomActionEnabledByThisComponent = EnableActionIfNeeded(zoomAction);
    }

    private void DisableResolvedInputActions()
    {
        DisableActionIfOwned(lookAction, lookActionEnabledByThisComponent);
        DisableActionIfOwned(orbitModifierAction, orbitModifierEnabledByThisComponent);
        DisableActionIfOwned(zoomAction, zoomActionEnabledByThisComponent);

        lookActionEnabledByThisComponent = false;
        orbitModifierEnabledByThisComponent = false;
        zoomActionEnabledByThisComponent = false;
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

    private static float NormalizeHorizontal(float angle)
    {
        float normalized = Mathf.Repeat(angle + 180f, 360f) - 180f;
        if (Mathf.Approximately(normalized, -180f))
        {
            normalized = 180f;
        }

        return normalized;
    }
}
