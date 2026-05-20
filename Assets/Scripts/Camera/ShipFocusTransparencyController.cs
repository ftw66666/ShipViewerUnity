using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Focus 模式非目标透明化控制器。
///
/// 第一版采用“替换非目标 Renderer 的 sharedMaterials 为共享透明材质”的方案：
/// - 不复制整船模型；
/// - 不逐个实例化材质；
/// - 退出 Focus 时恢复原始 sharedMaterials。
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipFocusTransparencyController : MonoBehaviour
{
    [Header("Scene References")]
    [SerializeField, Tooltip("需要透明化管理的整船根节点。为空时会使用场景中第一个 ShipPartSelectionController 所在索引的根对象范围之外的自动查找方案。")]
    private Transform shipRoot;

    [Header("Transparency")]
    [SerializeField, Range(0.05f, 0.8f), Tooltip("默认透明材质的 Alpha。")]
    private float dimAlpha = 0.18f;

    [SerializeField, Tooltip("非目标对象使用的共享透明材质。为空时运行时创建一个简单透明材质。")]
    private Material dimMaterial;

    [SerializeField, Tooltip("是否包含非激活 Renderer。")]
    private bool includeInactiveRenderers = true;

    [Header("Debug")]
    [SerializeField] private bool logTransparency = true;

    private readonly Dictionary<Renderer, Material[]> originalMaterialsByRenderer = new Dictionary<Renderer, Material[]>();
    private readonly HashSet<Renderer> targetRenderers = new HashSet<Renderer>();

    private void Awake()
    {
        EnsureDimMaterial();
    }

    private void OnDestroy()
    {
        Restore();
    }

    public void ApplyFocusTransparency(IReadOnlyList<GameObject> focusTargets)
    {
        Restore();
        EnsureDimMaterial();
        targetRenderers.Clear();

        if (focusTargets == null || focusTargets.Count == 0)
        {
            return;
        }

        for (int i = 0; i < focusTargets.Count; i++)
        {
            GameObject target = focusTargets[i];
            if (target == null)
            {
                continue;
            }

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
            foreach (Renderer renderer in renderers)
            {
                if (IsEligibleRenderer(renderer))
                {
                    targetRenderers.Add(renderer);
                }
            }
        }

        Renderer[] allRenderers = GetManagedRenderers();
        int dimmedCount = 0;
        foreach (Renderer renderer in allRenderers)
        {
            if (!IsEligibleRenderer(renderer) || targetRenderers.Contains(renderer))
            {
                continue;
            }

            originalMaterialsByRenderer[renderer] = renderer.sharedMaterials;
            renderer.sharedMaterials = BuildDimMaterialArray(renderer.sharedMaterials != null ? renderer.sharedMaterials.Length : 1);
            dimmedCount++;
        }

        if (logTransparency)
        {
            Debug.Log($"[ShipTransparency] Applied. Targets={targetRenderers.Count}, Dimmed={dimmedCount}");
        }
    }

    public void Restore()
    {
        int restoredCount = 0;
        foreach (KeyValuePair<Renderer, Material[]> pair in originalMaterialsByRenderer)
        {
            if (pair.Key == null)
            {
                continue;
            }

            pair.Key.sharedMaterials = pair.Value;
            restoredCount++;
        }

        originalMaterialsByRenderer.Clear();
        targetRenderers.Clear();

        if (logTransparency && restoredCount > 0)
        {
            Debug.Log($"[ShipTransparency] Restored. Renderers={restoredCount}");
        }
    }

    private Renderer[] GetManagedRenderers()
    {
        if (shipRoot != null)
        {
            return shipRoot.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
        }

        FindObjectsInactive inactiveMode = includeInactiveRenderers ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
        return FindObjectsByType<Renderer>(inactiveMode, FindObjectsSortMode.None);
    }

    private bool IsEligibleRenderer(Renderer renderer)
    {
        return renderer != null && !(renderer is ParticleSystemRenderer);
    }

    private Material[] BuildDimMaterialArray(int slotCount)
    {
        int count = Mathf.Max(1, slotCount);
        Material[] materials = new Material[count];
        for (int i = 0; i < count; i++)
        {
            materials[i] = dimMaterial;
        }

        return materials;
    }

    private void EnsureDimMaterial()
    {
        if (dimMaterial != null)
        {
            return;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        dimMaterial = new Material(shader)
        {
            name = "Runtime Focus Dim Material"
        };

        Color color = new Color(0.42f, 0.48f, 0.55f, dimAlpha);
        dimMaterial.color = color;
        dimMaterial.SetColor("_BaseColor", color);
        dimMaterial.SetFloat("_Surface", 1f);
        dimMaterial.SetFloat("_AlphaClip", 0f);
        dimMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        dimMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        dimMaterial.SetFloat("_ZWrite", 0f);
        dimMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        dimMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }
}
