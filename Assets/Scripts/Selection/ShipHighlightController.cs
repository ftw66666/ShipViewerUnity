using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 舰船部件高亮控制器。
///
/// Responsibilities:
/// 1. 为当前选中对象创建独立的高亮覆盖层；
/// 2. 高亮不再修改原始材质，也不受原材质颜色影响；
/// 3. 运行时将高亮对象放到 TransparentFX 层，作为单独特效层处理；
/// 4. 取消选中时销毁覆盖层，避免污染原始模型结构与材质实例。
/// </summary>
public sealed class ShipHighlightController : MonoBehaviour
{
    [Header("Overlay Highlight")]
    [SerializeField, Tooltip("可选：指定专用高亮材质。若为空，将在运行时自动创建覆盖材质。")]
    private Material overlayMaterialTemplate;

    [SerializeField, Tooltip("高亮覆盖颜色。建议使用半透明高亮色，而不是直接修改原材质色值。")]
    private Color highlightColor = new Color(0.12f, 1f, 0.35f, 0.82f);

    [SerializeField, Min(1f), Tooltip("覆盖层相对原模型的轻微放大倍率，用于避免与原网格完全重叠产生闪烁。")]
    private float overlayScale = 1.0125f;

    [SerializeField, Tooltip("高亮覆盖层所属的 Unity Layer。默认使用 TransparentFX。")]
    private string highlightLayerName = "TransparentFX";

    [SerializeField, Tooltip("是否同时高亮非激活子节点下的 Renderer。")]
    private bool includeInactiveRenderers = true;

    private readonly List<GameObject> overlayInstances = new List<GameObject>();
    private Transform currentTarget;
    private Material runtimeOverlayMaterial;
    private int resolvedHighlightLayer = -1;

    public Transform CurrentTarget => currentTarget;

    private void Awake()
    {
        ResolveHighlightLayer();
    }

    private void OnDestroy()
    {
        ClearHighlight();
        ReleaseRuntimeMaterial();
    }

    public void HighlightTarget(GameObject target)
    {
        HighlightTarget(target != null ? target.transform : null);
    }

    public void HighlightTarget(Transform target)
    {
        if (currentTarget == target)
        {
            return;
        }

        ClearHighlight();
        currentTarget = target;

        if (currentTarget == null)
        {
            return;
        }

        ResolveHighlightLayer();
        Material overlayMaterial = GetOrCreateOverlayMaterial();
        if (overlayMaterial == null)
        {
            Debug.LogWarning("[ShipHighlightController] 无法创建高亮覆盖材质，已跳过本次高亮。请检查当前渲染管线是否可用。", this);
            currentTarget = null;
            return;
        }

        Renderer[] renderers = currentTarget.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
        foreach (Renderer renderer in renderers)
        {
            TryCreateOverlayRenderer(renderer, overlayMaterial);
        }
    }

    public void ClearHighlight()
    {
        for (int i = overlayInstances.Count - 1; i >= 0; i--)
        {
            if (overlayInstances[i] != null)
            {
                Destroy(overlayInstances[i]);
            }
        }

        overlayInstances.Clear();
        currentTarget = null;
    }

    private void TryCreateOverlayRenderer(Renderer sourceRenderer, Material overlayMaterial)
    {
        if (sourceRenderer == null)
        {
            return;
        }

        if (sourceRenderer is ParticleSystemRenderer)
        {
            return;
        }

        if (sourceRenderer is MeshRenderer)
        {
            MeshFilter sourceFilter = sourceRenderer.GetComponent<MeshFilter>();
            if (sourceFilter == null || sourceFilter.sharedMesh == null)
            {
                return;
            }

            GameObject overlayObject = CreateOverlayObject(sourceRenderer.transform, sourceRenderer.gameObject.name);
            MeshFilter overlayFilter = overlayObject.AddComponent<MeshFilter>();
            overlayFilter.sharedMesh = sourceFilter.sharedMesh;

            MeshRenderer overlayRenderer = overlayObject.AddComponent<MeshRenderer>();
            ConfigureOverlayRenderer(overlayRenderer, sourceRenderer, overlayMaterial);
            return;
        }

        if (sourceRenderer is SkinnedMeshRenderer skinnedSource)
        {
            if (skinnedSource.sharedMesh == null)
            {
                return;
            }

            GameObject overlayObject = CreateOverlayObject(sourceRenderer.transform, sourceRenderer.gameObject.name);
            SkinnedMeshRenderer overlayRenderer = overlayObject.AddComponent<SkinnedMeshRenderer>();
            overlayRenderer.sharedMesh = skinnedSource.sharedMesh;
            overlayRenderer.rootBone = skinnedSource.rootBone;
            overlayRenderer.bones = skinnedSource.bones;
            overlayRenderer.localBounds = skinnedSource.localBounds;
            overlayRenderer.updateWhenOffscreen = true;
            ConfigureOverlayRenderer(overlayRenderer, sourceRenderer, overlayMaterial);
        }
    }

    private GameObject CreateOverlayObject(Transform sourceTransform, string sourceName)
    {
        GameObject overlayObject = new GameObject($"__SelectionOverlay__{sourceName}");
        overlayObject.hideFlags = HideFlags.DontSave;
        overlayObject.layer = resolvedHighlightLayer >= 0 ? resolvedHighlightLayer : sourceTransform.gameObject.layer;
        overlayObject.transform.SetParent(sourceTransform, false);
        overlayObject.transform.localPosition = Vector3.zero;
        overlayObject.transform.localRotation = Quaternion.identity;
        overlayObject.transform.localScale = Vector3.one * overlayScale;
        overlayInstances.Add(overlayObject);
        return overlayObject;
    }

    private void ConfigureOverlayRenderer(Renderer overlayRenderer, Renderer sourceRenderer, Material overlayMaterial)
    {
        if (overlayRenderer == null || sourceRenderer == null || overlayMaterial == null)
        {
            return;
        }

        int materialCount = Mathf.Max(1, sourceRenderer.sharedMaterials != null ? sourceRenderer.sharedMaterials.Length : 1);
        Material[] materials = new Material[materialCount];
        for (int i = 0; i < materials.Length; i++)
        {
            materials[i] = overlayMaterial;
        }

        overlayRenderer.sharedMaterials = materials;
        overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
        overlayRenderer.receiveShadows = false;
        overlayRenderer.lightProbeUsage = LightProbeUsage.Off;
        overlayRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        overlayRenderer.allowOcclusionWhenDynamic = false;
    }

    private Material GetOrCreateOverlayMaterial()
    {
        if (overlayMaterialTemplate != null)
        {
            ApplyOverlayMaterialProperties(overlayMaterialTemplate);
            return overlayMaterialTemplate;
        }

        if (runtimeOverlayMaterial == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            if (shader == null)
            {
                return null;
            }

            runtimeOverlayMaterial = new Material(shader)
            {
                name = "ShipSelectionOverlayRuntime",
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = (int)RenderQueue.Transparent
            };
        }

        ApplyOverlayMaterialProperties(runtimeOverlayMaterial);
        return runtimeOverlayMaterial;
    }

    private void ApplyOverlayMaterialProperties(Material material)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", highlightColor);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", highlightColor);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        if (material.HasProperty("_AlphaClip"))
        {
            material.SetFloat("_AlphaClip", 0f);
        }

        if (material.HasProperty("_Cull"))
        {
            material.SetFloat("_Cull", (float)CullMode.Off);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 0f);
        }

        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private void ResolveHighlightLayer()
    {
        if (!string.IsNullOrWhiteSpace(highlightLayerName))
        {
            resolvedHighlightLayer = LayerMask.NameToLayer(highlightLayerName.Trim());
        }

        if (resolvedHighlightLayer < 0)
        {
            resolvedHighlightLayer = LayerMask.NameToLayer("TransparentFX");
        }

        if (resolvedHighlightLayer < 0)
        {
            Debug.LogWarning($"[ShipHighlightController] 未找到 Layer: {highlightLayerName}，高亮覆盖层将回退到源对象 Layer。", this);
        }
    }

    private void ReleaseRuntimeMaterial()
    {
        if (runtimeOverlayMaterial != null)
        {
            Destroy(runtimeOverlayMaterial);
            runtimeOverlayMaterial = null;
        }
    }
}
