using System;
using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// 舰船查看器环绕相机控制器。
/// 基于 Cinemachine 3.1.6 的 CinemachineCamera + CinemachineOrbitalFollow +
/// CinemachineRotationComposer 组合，实现：
/// 1. 鼠标自由环绕与滚轮缩放；
/// 2. 通过 ShipViewerUIController 的 ViewModeChanged 事件切换预设视角；
/// 3. 尽量不修改现有 UI 脚本，采用事件订阅的最小侵入式集成方案。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CinemachineCamera))]
[RequireComponent(typeof(CinemachineOrbitalFollow))]
[RequireComponent(typeof(CinemachineRotationComposer))]
public sealed class ShipOrbitCameraController : MonoBehaviour
{
    /// <summary>
    /// 预设视角类型。
    /// </summary>
    public enum ShipViewPreset
    {
        Front,
        Top,
        Right,
        Isometric
    }

    /// <summary>
    /// 单个预设视角数据。
    /// </summary>
    [Serializable]
    public struct ViewPresetDefinition
    {
        [Tooltip("仅用于 Inspector 中标识该预设的用途。")]
        public ShipViewPreset preset;

        [Tooltip("水平环绕角度，单位：度。0 通常表示目标后方，180 表示目标前方。")]
        public float horizontalAngle;

        [Tooltip("垂直仰角，单位：度。")]
        public float verticalAngle;

        [Tooltip("相对 baseRadius 的缩放倍数。1 表示使用基础距离。")]
        public float distanceScale;
    }

    [Header("Scene References")]
    [SerializeField, Tooltip("被观察的舰船根节点。建议指定 ship 根对象，而不是某个网格子节点。")]
    private Transform shipTarget;

    [SerializeField, Tooltip("现有 UI 控制器。若不指定，会在场景中自动查找。")]
    private ShipViewerUIController uiController;

    [SerializeField, Tooltip("若鼠标位于 UI 上方，则阻止场景相机环绕和滚轮缩放。")]
    private bool blockSceneInputWhenPointerOverUi = true;

    [Header("Framing")]
    [SerializeField, Tooltip("目标观察点偏移，使用目标局部坐标。用于把相机关注点从模型 pivot 调整到船体中心。")]
    private Vector3 targetOffset = Vector3.zero;

    [SerializeField, Min(0.1f), Tooltip("基础环绕半径。实际距离 = baseRadius × distanceScale。")]
    private float baseRadius = 35f;

    [SerializeField, Tooltip("垂直视角限制。最小值表示允许俯视到的最低角度，用于避免视角过平。")]
    private Vector2 verticalAngleRange = new Vector2(5f, 75f);

    [SerializeField, Tooltip("缩放倍率限制。1 表示基础距离。")]
    private Vector2 zoomScaleRange = new Vector2(0.6f, 1.8f);

    [Header("Cinemachine Tracking")]
    [SerializeField, Tooltip("位置阻尼。值越小越灵敏，值越大越稳重。")]
    private Vector3 positionDamping = new Vector3(0.25f, 0.25f, 0.25f);

    [SerializeField, Tooltip("旋转阻尼。由于使用 LockToTargetWithWorldUp，通常只需要关注 Y 轴阻尼。")]
    private Vector3 rotationDamping = new Vector3(0f, 0.1f, 0f);

    [Header("Manual Orbit Input")]
    [SerializeField, Tooltip("是否必须按住右键才允许鼠标自由环绕。建议开启，避免与 UI 点击冲突。")]
    private bool requireRightMouseToOrbit = true;

    [SerializeField, Tooltip("是否反转垂直输入。关闭时，鼠标上移 = 相机上抬。")]
    private bool invertVerticalInput = false;

    [SerializeField, Min(0.001f), Tooltip("水平环绕速度。")]
    private float horizontalSensitivity = 0.12f;

    [SerializeField, Min(0.001f), Tooltip("垂直环绕速度。")]
    private float verticalSensitivity = 0.10f;

    [SerializeField, Min(0.001f), Tooltip("滚轮缩放速度。正值即可。")]
    private float zoomSensitivity = 0.08f;

    [Header("Input Actions (Optional)")]
    [SerializeField, Tooltip("可选：现有 InputActionAsset。若不指定，将回退到 Mouse.current 直接读取。")]
    private InputActionAsset inputActions;

    [SerializeField] private string lookActionPath = "Player/Look";
    [SerializeField] private string orbitModifierActionPath = "UI/RightClick";
    [SerializeField] private string zoomActionPath = "UI/ScrollWheel";

    [Header("Preset Views")]
    [SerializeField, Tooltip("启动时默认应用的视角。")]
    private ShipViewPreset defaultPreset = ShipViewPreset.Isometric;

    [SerializeField, Min(0f), Tooltip("切换预设视角时的平滑时间。0 表示立即切换。")]
    private float presetBlendTime = 0.35f;

    [SerializeField, Tooltip("前视：通常让相机位于舰船前方朝向舰船。")]
    private ViewPresetDefinition frontPreset = new ViewPresetDefinition
    {
        preset = ShipViewPreset.Front,
        horizontalAngle = 180f,
        verticalAngle = 8f,
        distanceScale = 1.0f
    };

    [SerializeField, Tooltip("俯视：更适合总览甲板结构。")]
    private ViewPresetDefinition topPreset = new ViewPresetDefinition
    {
        preset = ShipViewPreset.Top,
        horizontalAngle = 0f,
        verticalAngle = 75f,
        distanceScale = 1.15f
    };

    [SerializeField, Tooltip("右视：查看舰船右舷。")]
    private ViewPresetDefinition rightPreset = new ViewPresetDefinition
    {
        preset = ShipViewPreset.Right,
        horizontalAngle = -90f,
        verticalAngle = 10f,
        distanceScale = 1.0f
    };

    [SerializeField, Tooltip("等距视：作为默认展示视图。")]
    private ViewPresetDefinition isometricPreset = new ViewPresetDefinition
    {
        preset = ShipViewPreset.Isometric,
        horizontalAngle = 135f,
        verticalAngle = 25f,
        distanceScale = 1.1f
    };

    private CinemachineCamera cinemachineCamera;
    private CinemachineOrbitalFollow orbitalFollow;
    private CinemachineRotationComposer rotationComposer;

    private InputAction lookAction;
    private InputAction orbitModifierAction;
    private InputAction zoomAction;

    private bool lookActionEnabledByThisComponent;
    private bool orbitModifierEnabledByThisComponent;
    private bool zoomActionEnabledByThisComponent;

    private bool subscribedToUi;
    private bool presetBlending;
    private float targetHorizontalAngle;
    private float targetVerticalAngle;
    private float targetDistanceScale = 1f;
    private float horizontalBlendVelocity;
    private float verticalBlendVelocity;
    private float distanceBlendVelocity;

    private void Reset()
    {
        CacheComponents();
        ResolveSceneReferences();
        ApplyCinemachineSetup();
        ApplyPreset(defaultPreset, true);
    }

    private void Awake()
    {
        CacheComponents();
        ResolveSceneReferences();
        ApplyCinemachineSetup();
        ResolveInputActions();
    }

    private void OnEnable()
    {
        ResolveSceneReferences();
        ApplyCinemachineSetup();
        ResolveInputActions();
        EnableResolvedInputActions();
        SubscribeUiEvents();
        ApplyPreset(defaultPreset, true);
    }

    private void OnDisable()
    {
        UnsubscribeUiEvents();
        DisableResolvedInputActions();
    }

    private void OnValidate()
    {
        baseRadius = Mathf.Max(0.1f, baseRadius);
        presetBlendTime = Mathf.Max(0f, presetBlendTime);
        horizontalSensitivity = Mathf.Max(0.001f, horizontalSensitivity);
        verticalSensitivity = Mathf.Max(0.001f, verticalSensitivity);
        zoomSensitivity = Mathf.Max(0.001f, zoomSensitivity);

        if (zoomScaleRange.x < 0.05f)
        {
            zoomScaleRange.x = 0.05f;
        }

        if (zoomScaleRange.y < zoomScaleRange.x)
        {
            zoomScaleRange.y = zoomScaleRange.x;
        }

        if (verticalAngleRange.y < verticalAngleRange.x)
        {
            verticalAngleRange.y = verticalAngleRange.x;
        }

        CacheComponents();
        ApplyCinemachineSetup();
    }

    private void LateUpdate()
    {
        if (orbitalFollow == null)
        {
            return;
        }

        ApplyCameraTargets();
        ApplyTrackedOffsets();

        bool usedManualInput = HandleManualOrbitInput();
        if (usedManualInput)
        {
            presetBlending = false;
            return;
        }

        if (presetBlending)
        {
            UpdatePresetBlend();
        }
    }

    /// <summary>
    /// 外部可直接调用：切到前视。
    /// </summary>
    public void SetFrontView()
    {
        ApplyPreset(ShipViewPreset.Front, false);
    }

    /// <summary>
    /// 外部可直接调用：切到俯视。
    /// </summary>
    public void SetTopView()
    {
        ApplyPreset(ShipViewPreset.Top, false);
    }

    /// <summary>
    /// 外部可直接调用：切到右视。
    /// </summary>
    public void SetRightView()
    {
        ApplyPreset(ShipViewPreset.Right, false);
    }

    /// <summary>
    /// 外部可直接调用：切到等距视。
    /// </summary>
    public void SetIsometricView()
    {
        ApplyPreset(ShipViewPreset.Isometric, false);
    }

    /// <summary>
    /// 外部可直接调用：按 UI 中文模式名切换视角。
    /// 与 ShipViewerUIController.ViewModeChanged 事件值保持一致。
    /// </summary>
    /// <param name="viewMode">前视 / 俯视 / 右视 / 等轴测</param>
    public void ApplyViewMode(string viewMode)
    {
        if (string.IsNullOrWhiteSpace(viewMode))
        {
            return;
        }

        switch (viewMode.Trim())
        {
            case "前视":
                ApplyPreset(ShipViewPreset.Front, false);
                break;
            case "俯视":
                ApplyPreset(ShipViewPreset.Top, false);
                break;
            case "右视":
                ApplyPreset(ShipViewPreset.Right, false);
                break;
            case "等轴测":
            case "等距视":
                ApplyPreset(ShipViewPreset.Isometric, false);
                break;
            default:
                Debug.LogWarning($"[ShipOrbitCameraController] 未识别的视角模式：{viewMode}");
                break;
        }
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

    private void ResolveSceneReferences()
    {
        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }

        if (shipTarget == null && cinemachineCamera != null && cinemachineCamera.Follow != null)
        {
            shipTarget = cinemachineCamera.Follow;
        }
    }

    private void ApplyCinemachineSetup()
    {
        if (cinemachineCamera == null || orbitalFollow == null || rotationComposer == null)
        {
            return;
        }

        ApplyCameraTargets();

        orbitalFollow.OrbitStyle = CinemachineOrbitalFollow.OrbitStyles.Sphere;
        orbitalFollow.Radius = baseRadius;
        orbitalFollow.TargetOffset = targetOffset;
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
        orbitalFollow.HorizontalAxis.Center = 0f;
        orbitalFollow.HorizontalAxis.Recentering.Enabled = false;

        orbitalFollow.VerticalAxis.Range = verticalAngleRange;
        orbitalFollow.VerticalAxis.Wrap = false;
        orbitalFollow.VerticalAxis.Center = Mathf.Clamp((verticalAngleRange.x + verticalAngleRange.y) * 0.5f, verticalAngleRange.x, verticalAngleRange.y);
        orbitalFollow.VerticalAxis.Recentering.Enabled = false;

        orbitalFollow.RadialAxis.Range = zoomScaleRange;
        orbitalFollow.RadialAxis.Wrap = false;
        orbitalFollow.RadialAxis.Center = Mathf.Clamp(1f, zoomScaleRange.x, zoomScaleRange.y);
        orbitalFollow.RadialAxis.Recentering.Enabled = false;

        rotationComposer.Composition.ScreenPosition = Vector2.zero;

        ApplyTrackedOffsets();
        ClampAxisValues();
    }

    private void ApplyCameraTargets()
    {
        if (cinemachineCamera == null || shipTarget == null)
        {
            return;
        }

        if (cinemachineCamera.Follow != shipTarget)
        {
            cinemachineCamera.Follow = shipTarget;
        }

        if (cinemachineCamera.LookAt != shipTarget)
        {
            cinemachineCamera.LookAt = shipTarget;
        }
    }

    private void ApplyTrackedOffsets()
    {
        if (orbitalFollow != null)
        {
            orbitalFollow.TargetOffset = targetOffset;
        }

        if (rotationComposer != null)
        {
            rotationComposer.TargetOffset = targetOffset;
        }
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

    private void SubscribeUiEvents()
    {
        if (subscribedToUi || uiController == null)
        {
            return;
        }

        uiController.ViewModeChanged += HandleViewModeChanged;
        subscribedToUi = true;
    }

    private void UnsubscribeUiEvents()
    {
        if (!subscribedToUi || uiController == null)
        {
            return;
        }

        uiController.ViewModeChanged -= HandleViewModeChanged;
        subscribedToUi = false;
    }

    private void HandleViewModeChanged(string viewMode)
    {
        ApplyViewMode(viewMode);
    }

    private bool HandleManualOrbitInput()
    {
        Vector2 screenPosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        if (blockSceneInputWhenPointerOverUi && IsPointerBlockedByUI(screenPosition))
        {
            return false;
        }

        Vector2 lookDelta = ReadLookDelta();
        float zoomDelta = ReadZoomDelta();
        bool canOrbit = CanUseOrbitPointer();
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
            ClampAxisValues();
        }

        return changed;
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

    private void ApplyPreset(ShipViewPreset preset, bool instant)
    {
        ViewPresetDefinition definition = GetPresetDefinition(preset);

        targetHorizontalAngle = NormalizeHorizontal(definition.horizontalAngle);
        targetVerticalAngle = Mathf.Clamp(definition.verticalAngle, Mathf.Max(0.01f, verticalAngleRange.x), verticalAngleRange.y);
        targetDistanceScale = Mathf.Clamp(definition.distanceScale, zoomScaleRange.x, zoomScaleRange.y);

        if (instant || presetBlendTime <= 0f)
        {
            SetOrbitState(targetHorizontalAngle, targetVerticalAngle, targetDistanceScale);
            presetBlending = false;
            horizontalBlendVelocity = 0f;
            verticalBlendVelocity = 0f;
            distanceBlendVelocity = 0f;
            return;
        }

        presetBlending = true;
    }

    private ViewPresetDefinition GetPresetDefinition(ShipViewPreset preset)
    {
        return preset switch
        {
            ShipViewPreset.Front => frontPreset,
            ShipViewPreset.Top => topPreset,
            ShipViewPreset.Right => rightPreset,
            _ => isometricPreset
        };
    }

    private void UpdatePresetBlend()
    {
        float smoothTime = Mathf.Max(0.0001f, presetBlendTime);
        float nextHorizontal = Mathf.SmoothDampAngle(
            orbitalFollow.HorizontalAxis.Value,
            targetHorizontalAngle,
            ref horizontalBlendVelocity,
            smoothTime);
        float nextVertical = Mathf.SmoothDamp(
            orbitalFollow.VerticalAxis.Value,
            targetVerticalAngle,
            ref verticalBlendVelocity,
            smoothTime);
        float nextDistance = Mathf.SmoothDamp(
            orbitalFollow.RadialAxis.Value,
            targetDistanceScale,
            ref distanceBlendVelocity,
            smoothTime);

        SetOrbitState(nextHorizontal, nextVertical, nextDistance);

        bool horizontalDone = Mathf.Abs(Mathf.DeltaAngle(orbitalFollow.HorizontalAxis.Value, targetHorizontalAngle)) < 0.1f;
        bool verticalDone = Mathf.Abs(orbitalFollow.VerticalAxis.Value - targetVerticalAngle) < 0.05f;
        bool distanceDone = Mathf.Abs(orbitalFollow.RadialAxis.Value - targetDistanceScale) < 0.01f;

        if (horizontalDone && verticalDone && distanceDone)
        {
            SetOrbitState(targetHorizontalAngle, targetVerticalAngle, targetDistanceScale);
            presetBlending = false;
            horizontalBlendVelocity = 0f;
            verticalBlendVelocity = 0f;
            distanceBlendVelocity = 0f;
        }
    }

    private void SetOrbitState(float horizontal, float vertical, float distanceScale)
    {
        orbitalFollow.HorizontalAxis.Value = orbitalFollow.HorizontalAxis.ClampValue(NormalizeHorizontal(horizontal));
        orbitalFollow.VerticalAxis.Value = orbitalFollow.VerticalAxis.ClampValue(vertical);
        orbitalFollow.RadialAxis.Value = orbitalFollow.RadialAxis.ClampValue(distanceScale);
    }

    private void ClampAxisValues()
    {
        if (orbitalFollow == null)
        {
            return;
        }

        orbitalFollow.HorizontalAxis.Value = orbitalFollow.HorizontalAxis.ClampValue(NormalizeHorizontal(orbitalFollow.HorizontalAxis.Value));
        orbitalFollow.VerticalAxis.Value = orbitalFollow.VerticalAxis.ClampValue(Mathf.Max(0.01f, orbitalFollow.VerticalAxis.Value));
        orbitalFollow.RadialAxis.Value = orbitalFollow.RadialAxis.ClampValue(orbitalFollow.RadialAxis.Value);

        if (rotationComposer != null)
        {
            rotationComposer.Composition.ScreenPosition = Vector2.zero;
        }
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
