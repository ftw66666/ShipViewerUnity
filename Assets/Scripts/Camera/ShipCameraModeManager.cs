using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// ShipViewer 多相机模式管理器。
///
/// Responsibilities:
/// 1. 维护总览相机与局部 Focus 相机的优先级；
/// 2. 配置 CinemachineBrain 的默认 Blend；
/// 3. 对外提供进入 Focus / 返回总览接口。
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipCameraModeManager : MonoBehaviour
{
    [Header("Cameras")]
    [SerializeField, Tooltip("总览虚拟相机，通常是挂有 ShipOrbitCameraController 的 CinemachineCamera。")]
    private CinemachineCamera overviewCamera;

    [SerializeField, Tooltip("毁伤节点局部 Focus 相机。")]
    private ShipDamageFocusCameraController focusCameraController;

    [SerializeField, Tooltip("场景主 Camera 上的 CinemachineBrain。为空时会自动查找。")]
    private CinemachineBrain brain;

    [SerializeField, Tooltip("用于计算取景方向和宽高比的输出 Camera。为空时使用 Camera.main。")]
    private Camera outputCamera;

    [Header("Priority")]
    [SerializeField] private int overviewPriority = 10;
    [SerializeField] private int focusPriority = 30;
    [SerializeField] private int inactivePriority = 0;

    [Header("Blend")]
    [SerializeField, Min(0f), Tooltip("总览 / Focus 虚拟相机切换的默认混合时间。")]
    private float defaultBlendTime = 0.75f;

    [Header("Debug")]
    [SerializeField] private bool logCameraMode = true;

    public bool IsFocusMode { get; private set; }
    public ShipDamageFocusCameraController FocusCameraController => focusCameraController;
    public Camera OutputCamera => outputCamera != null ? outputCamera : Camera.main;

    private void Awake()
    {
        ResolveReferences();
        ConfigureBrainBlend();
        SwitchToOverview(true);
    }

    private void OnEnable()
    {
        ResolveReferences();
        ConfigureBrainBlend();
    }

    private void OnValidate()
    {
        defaultBlendTime = Mathf.Max(0f, defaultBlendTime);
    }

    public bool FocusTargets(System.Collections.Generic.IReadOnlyList<GameObject> targets, string contextLabel)
    {
        ResolveReferences();
        ConfigureBrainBlend();

        if (focusCameraController == null)
        {
            Debug.LogWarning("[ShipCameraMode] Focus failed: focusCameraController is null.");
            return false;
        }

        if (!focusCameraController.FocusTargets(targets, OutputCamera, contextLabel))
        {
            SwitchToOverview(false);
            return false;
        }

        SetCameraPriority(overviewCamera, overviewPriority);
        SetCameraPriority(focusCameraController.Camera, focusPriority);
        IsFocusMode = true;

        if (logCameraMode)
        {
            Debug.Log($"[ShipCameraMode] Switch to Focus. Context={contextLabel}, OverviewPriority={overviewPriority}, FocusPriority={focusPriority}, Blend={defaultBlendTime:0.##}s");
        }

        return true;
    }

    public void SwitchToOverview(bool instant)
    {
        ResolveReferences();

        if (instant && brain != null)
        {
            CinemachineBlendDefinition blend = brain.DefaultBlend;
            blend.Time = 0f;
            brain.DefaultBlend = blend;
        }
        else
        {
            ConfigureBrainBlend();
        }

        SetCameraPriority(overviewCamera, overviewPriority);
        if (focusCameraController != null)
        {
            SetCameraPriority(focusCameraController.Camera, inactivePriority);
            focusCameraController.ClearFocus();
        }

        IsFocusMode = false;

        if (logCameraMode)
        {
            Debug.Log($"[ShipCameraMode] Switch to Overview. Instant={instant}");
        }
    }

    private void ResolveReferences()
    {
        if (outputCamera == null)
        {
            outputCamera = Camera.main;
        }

        if (brain == null && outputCamera != null)
        {
            brain = outputCamera.GetComponent<CinemachineBrain>();
        }

        if (brain == null)
        {
            brain = FindAnyObjectByType<CinemachineBrain>();
        }

        if (overviewCamera == null)
        {
            ShipOrbitCameraController overviewController = FindAnyObjectByType<ShipOrbitCameraController>();
            if (overviewController != null)
            {
                overviewCamera = overviewController.GetComponent<CinemachineCamera>();
            }
        }

        if (focusCameraController == null)
        {
            focusCameraController = FindAnyObjectByType<ShipDamageFocusCameraController>();
        }
    }

    private void ConfigureBrainBlend()
    {
        if (brain == null)
        {
            return;
        }

        CinemachineBlendDefinition blend = brain.DefaultBlend;
        blend.Style = CinemachineBlendDefinition.Styles.EaseInOut;
        blend.Time = defaultBlendTime;
        brain.DefaultBlend = blend;
    }

    private static void SetCameraPriority(CinemachineCamera camera, int priority)
    {
        if (camera == null)
        {
            return;
        }

        PrioritySettings settings = camera.Priority;
        settings.Enabled = true;
        settings.Value = priority;
        camera.Priority = settings;
    }
}
