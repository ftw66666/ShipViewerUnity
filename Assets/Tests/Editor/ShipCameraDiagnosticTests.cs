using NUnit.Framework;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ShipViewer.Tests.Editor
{
    /// <summary>
    /// 舰船相机诊断测试。
    ///
    /// 设计目标：
    /// 1. 不修改任何现有运行时代码；
    /// 2. 直接加载当前 SampleScene，检查相机链路、导入模型几何尺寸、碰撞体与 Cinemachine 配置；
    /// 3. 用失败断言明确指出导致“CameraRig 在转，但输出摄像机看起来不动/功能失效”的高风险配置。
    /// </summary>
    public sealed class ShipCameraDiagnosticTests
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string MainSceneRootName = "--- Main Scene ---";
        private const string ExpectedShipTargetName = "Ship";

        [SetUp]
        public void OpenSampleScene()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), $"无法加载诊断场景：{ScenePath}");
        }

        [Test]
        public void CameraPipeline_RequiredComponentsExistAndTargetsAreResolvable()
        {
            Camera mainCamera = Camera.main;
            Assert.IsNotNull(mainCamera, "场景中必须存在 Tag=MainCamera 的输出 Camera。");
            Assert.IsTrue(mainCamera.enabled, "Main Camera 组件必须启用，否则 CinemachineBrain 无法输出画面。");

            CinemachineBrain brain = mainCamera.GetComponent<CinemachineBrain>();
            Assert.IsNotNull(brain, "Main Camera 上必须挂载 CinemachineBrain。");
            Assert.IsTrue(brain.enabled, "CinemachineBrain 必须启用。");

            ShipOrbitCameraController overviewController = Object.FindFirstObjectByType<ShipOrbitCameraController>();
            Assert.IsNotNull(overviewController, "场景中必须存在 ShipOrbitCameraController。");

            CinemachineCamera overviewCamera = overviewController.GetComponent<CinemachineCamera>();
            CinemachineOrbitalFollow overviewOrbital = overviewController.GetComponent<CinemachineOrbitalFollow>();
            CinemachineRotationComposer overviewComposer = overviewController.GetComponent<CinemachineRotationComposer>();
            Assert.IsNotNull(overviewCamera, "Overview CameraRig 必须挂载 CinemachineCamera。");
            Assert.IsNotNull(overviewOrbital, "Overview CameraRig 必须挂载 CinemachineOrbitalFollow。");
            Assert.IsNotNull(overviewComposer, "Overview CameraRig 必须挂载 CinemachineRotationComposer。");
            Assert.IsTrue(overviewCamera.enabled, "Overview CinemachineCamera 必须启用。");
            Assert.IsNotNull(overviewCamera.Follow, "Overview CinemachineCamera.Follow 不能为空。");
            Assert.IsNotNull(overviewCamera.LookAt, "Overview CinemachineCamera.LookAt 不能为空。");
            Assert.AreEqual(ExpectedShipTargetName, overviewCamera.Follow.name, "Overview Follow 目标应指向导入舰船根节点 Ship。");
            Assert.AreSame(overviewCamera.Follow, overviewCamera.LookAt, "Overview Follow 与 LookAt 应保持同一个目标，避免环绕中心和朝向中心不一致。");

            ShipDamageFocusCameraController focusController = Object.FindFirstObjectByType<ShipDamageFocusCameraController>();
            Assert.IsNotNull(focusController, "场景中必须存在 ShipDamageFocusCameraController。");
            Assert.IsNotNull(focusController.GetComponent<CinemachineCamera>(), "Focus CameraRig 必须挂载 CinemachineCamera。");
            Assert.IsNotNull(focusController.GetComponent<CinemachineOrbitalFollow>(), "Focus CameraRig 必须挂载 CinemachineOrbitalFollow。");

            ShipCameraModeManager modeManager = Object.FindFirstObjectByType<ShipCameraModeManager>();
            Assert.IsNotNull(modeManager, "场景中必须存在 ShipCameraModeManager，用于切换总览/Focus 相机优先级。");

            Debug.Log($"[ShipCameraDiagnosticTests] MainCamera={mainCamera.name}, Position={mainCamera.transform.position}, Brain={brain.name}");
            Debug.Log($"[ShipCameraDiagnosticTests] Overview={overviewController.name}, Follow={overviewCamera.Follow.name}, Radius={overviewOrbital.Radius}, H={overviewOrbital.HorizontalAxis.Value}, V={overviewOrbital.VerticalAxis.Value}, R={overviewOrbital.RadialAxis.Value}");
        }

        [Test]
        public void ImportedShipGeometry_IsFiniteAndReportsScaleMetrics()
        {
            Transform ship = FindShipTarget();
            Bounds bounds = CalculateRenderableBounds(ship, out int rendererCount, out int invalidRendererCount);
            int transformCount = ship.GetComponentsInChildren<Transform>(true).Length;

            Assert.Greater(rendererCount, 0, "Ship 下必须至少存在一个有效 Renderer。");
            Assert.AreEqual(0, invalidRendererCount, "Renderer bounds 中不能出现 NaN/Infinity 或负尺寸，否则 Cinemachine 取景和 Bounds 聚合会失效。");

            Debug.Log($"[ShipCameraDiagnosticTests] ShipTransforms={transformCount}, Renderers={rendererCount}");
            Debug.Log($"[ShipCameraDiagnosticTests] ShipBoundsCenter={bounds.center}, ShipBoundsSize={bounds.size}, BoundsExtentsMagnitude={bounds.extents.magnitude:0.###}");
        }

        [Test]
        public void OverviewCameraOrbitDistance_IsLargeEnoughForImportedShipBounds()
        {
            Transform ship = FindShipTarget();
            Bounds bounds = CalculateRenderableBounds(ship, out int rendererCount, out int invalidRendererCount);
            Assert.Greater(rendererCount, 0, "无法计算 Ship Bounds：有效 Renderer 数量为 0。");
            Assert.AreEqual(0, invalidRendererCount, "存在非法 Renderer Bounds，需先修复模型导入数据。");

            ShipOrbitCameraController overviewController = Object.FindFirstObjectByType<ShipOrbitCameraController>();
            Assert.IsNotNull(overviewController, "场景中必须存在 ShipOrbitCameraController。");

            CinemachineOrbitalFollow orbitalFollow = overviewController.GetComponent<CinemachineOrbitalFollow>();
            Assert.IsNotNull(orbitalFollow, "Overview CameraRig 缺少 CinemachineOrbitalFollow。");

            float configuredBaseRadius = ReadSerializedFloat(overviewController, "baseRadius");
            float currentOrbitDistance = Mathf.Max(0.01f, orbitalFollow.Radius) * Mathf.Max(0.01f, orbitalFollow.RadialAxis.Value);
            float boundsRadius = bounds.extents.magnitude;

            Debug.Log($"[ShipCameraDiagnosticTests] ConfiguredBaseRadius={configuredBaseRadius:0.###}, RuntimeOrbitRadius={orbitalFollow.Radius:0.###}, RadialScale={orbitalFollow.RadialAxis.Value:0.###}, CurrentOrbitDistance={currentOrbitDistance:0.###}");
            Debug.Log($"[ShipCameraDiagnosticTests] BoundsExtents={bounds.extents}, BoundsRadius={boundsRadius:0.###}");
            Debug.Log("[ShipCameraDiagnosticTests] 诊断标准：总览相机环绕距离应至少大于模型包围球半径，否则输出相机处在导入模型内部/过近区域，表现为 CameraRig 角度变化但画面几乎不产生有效位移。");

            Assert.GreaterOrEqual(
                currentOrbitDistance,
                boundsRadius,
                $"总览相机距离过小：CurrentOrbitDistance={currentOrbitDistance:0.###}，但导入模型 BoundsRadius={boundsRadius:0.###}。这会让主相机落在约 {bounds.size.x:0.##}x{bounds.size.y:0.##}x{bounds.size.z:0.##} 的大型模型内部或近距离区域，是‘相机在转但看起来不动/无法正常观察’的直接原因。"
            );
        }

        [Test]
        public void ImportedShip_HasCollidersForRaycastSelectionWorkflow()
        {
            Transform ship = FindShipTarget();
            int rendererCount = ship.GetComponentsInChildren<Renderer>(true).Length;
            int colliderCount = ship.GetComponentsInChildren<Collider>(true).Length;

            Debug.Log($"[ShipCameraDiagnosticTests] RendererCount={rendererCount}, ColliderCount={colliderCount}");
            Debug.Log("[ShipCameraDiagnosticTests] 诊断标准：ShipPartSelectionController 依赖 Physics.Raycast；如果导入的 1wObject 模型没有 Collider，模型点击/射线选择会失效。该项是选择链路的独立风险，不等同于 UI 本身故障。");

            Assert.Greater(
                colliderCount,
                0,
                $"导入模型下没有任何 Collider，但当前选择脚本使用 Physics.Raycast。RendererCount={rendererCount}，ColliderCount={colliderCount}。"
            );
        }

        [Test]
        public void CameraModeManager_RuntimeReferencesCanBeResolvedEvenWhenInspectorFieldsAreEmpty()
        {
            ShipCameraModeManager modeManager = Object.FindFirstObjectByType<ShipCameraModeManager>();
            Assert.IsNotNull(modeManager, "场景中必须存在 ShipCameraModeManager。");

            SerializedObject serializedObject = new SerializedObject(modeManager);
            Object overviewReference = serializedObject.FindProperty("overviewCamera")?.objectReferenceValue;
            Object focusReference = serializedObject.FindProperty("focusCameraController")?.objectReferenceValue;
            Object brainReference = serializedObject.FindProperty("brain")?.objectReferenceValue;
            Object outputCameraReference = serializedObject.FindProperty("outputCamera")?.objectReferenceValue;

            ShipOrbitCameraController overviewController = Object.FindFirstObjectByType<ShipOrbitCameraController>();
            ShipDamageFocusCameraController focusController = Object.FindFirstObjectByType<ShipDamageFocusCameraController>();
            CinemachineBrain brain = Object.FindFirstObjectByType<CinemachineBrain>();
            Camera mainCamera = Camera.main;

            Assert.IsNotNull(overviewController, "overviewCamera Inspector 字段为空时，运行时必须能通过 ShipOrbitCameraController 自动解析。");
            Assert.IsNotNull(focusController, "focusCameraController Inspector 字段为空时，运行时必须能自动解析 ShipDamageFocusCameraController。");
            Assert.IsNotNull(brain, "brain Inspector 字段为空时，运行时必须能自动解析 CinemachineBrain。");
            Assert.IsNotNull(mainCamera, "outputCamera Inspector 字段为空时，运行时必须能通过 Camera.main 自动解析。");

            Debug.Log($"[ShipCameraDiagnosticTests] SerializedOverview={(overviewReference != null ? overviewReference.name : "<null>")}, SerializedFocus={(focusReference != null ? focusReference.name : "<null>")}, SerializedBrain={(brainReference != null ? brainReference.name : "<null>")}, SerializedOutput={(outputCameraReference != null ? outputCameraReference.name : "<null>")}");
            Debug.Log("[ShipCameraDiagnosticTests] 说明：这些 Inspector 引用当前可以为空，因为现有代码在 Awake/OnEnable 中会自动解析；若运行时日志显示解析失败，再回到此项检查对象命名/组件是否被删除。");
        }

        private static Transform FindShipTarget()
        {
            ShipOrbitCameraController overviewController = Object.FindFirstObjectByType<ShipOrbitCameraController>();
            Assert.IsNotNull(overviewController, "找不到 ShipOrbitCameraController，无法定位 Ship 目标。");

            CinemachineCamera overviewCamera = overviewController.GetComponent<CinemachineCamera>();
            Assert.IsNotNull(overviewCamera, "Overview Controller 缺少 CinemachineCamera。");
            Assert.IsNotNull(overviewCamera.Follow, "Overview CinemachineCamera.Follow 为空，无法定位 Ship 目标。");
            Assert.AreEqual(ExpectedShipTargetName, overviewCamera.Follow.name, "Overview Follow 未指向预期 Ship 根节点。");

            Transform root = GameObject.Find(MainSceneRootName)?.transform;
            Assert.IsNotNull(root, $"找不到场景根对象：{MainSceneRootName}");
            Assert.IsTrue(overviewCamera.Follow.IsChildOf(root), $"Ship 目标应位于 {MainSceneRootName} 层级下。");

            return overviewCamera.Follow;
        }

        private static Bounds CalculateRenderableBounds(Transform root, out int rendererCount, out int invalidRendererCount)
        {
            Assert.IsNotNull(root, "计算 Bounds 时 root 不能为空。");

            rendererCount = 0;
            invalidRendererCount = 0;
            bool hasBounds = false;
            Bounds aggregateBounds = default;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || renderer is ParticleSystemRenderer)
                {
                    continue;
                }

                Bounds rendererBounds = renderer.bounds;
                if (!IsFinite(rendererBounds.center) || !IsFinite(rendererBounds.size) || rendererBounds.size.x < 0f || rendererBounds.size.y < 0f || rendererBounds.size.z < 0f)
                {
                    invalidRendererCount++;
                    continue;
                }

                rendererCount++;
                if (!hasBounds)
                {
                    aggregateBounds = rendererBounds;
                    hasBounds = true;
                }
                else
                {
                    aggregateBounds.Encapsulate(rendererBounds);
                }
            }

            Assert.IsTrue(hasBounds, "未能从 Ship 下的 Renderer 聚合出有效 Bounds。");
            return aggregateBounds;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x)
                   && !float.IsNaN(value.y)
                   && !float.IsNaN(value.z)
                   && !float.IsInfinity(value.x)
                   && !float.IsInfinity(value.y)
                   && !float.IsInfinity(value.z);
        }

        private static float ReadSerializedFloat(Object target, string propertyName)
        {
            SerializedObject serializedObject = new SerializedObject(target);
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            Assert.IsNotNull(property, $"找不到序列化字段：{propertyName}");
            Assert.AreEqual(SerializedPropertyType.Float, property.propertyType, $"字段 {propertyName} 不是 float 类型。");
            return property.floatValue;
        }
    }
}
