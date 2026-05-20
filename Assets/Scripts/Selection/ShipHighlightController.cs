using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 舰船部件高亮控制器。
///
/// Responsibilities:
/// 1. 使用 Linework Lite 的 Rendering Layer 机制驱动轮廓高亮；
/// 2. 不再创建覆盖网格，也不修改原始材质；
/// 3. 只在当前目标的 Renderer 上写入指定 renderingLayerMask；
/// 4. 取消高亮时恢复原始 renderingLayerMask，避免污染场景对象状态。
/// </summary>
public sealed class ShipHighlightController : MonoBehaviour
{
    [Header("Linework Lite Outline")]
    [SerializeField, Tooltip("用于轮廓高亮的 Rendering Layer 位索引。需与 Free Outline Settings 中的 RenderingLayer 保持一致。默认使用第 1 位。")]
    [Range(0, 31)] private int outlineRenderingLayerBit = 1;

    [SerializeField, Tooltip("是否同时高亮非激活子节点下的 Renderer。")]
    private bool includeInactiveRenderers = true;

    private readonly List<Renderer> activeRenderers = new List<Renderer>();
    private readonly Dictionary<Renderer, uint> originalRenderingLayerMaskByRenderer = new Dictionary<Renderer, uint>();

    private Transform currentTarget;

    public Transform CurrentTarget => currentTarget;

    private uint OutlineRenderingLayerMask => 1u << Mathf.Clamp(outlineRenderingLayerBit, 0, 31);

    private void OnDestroy()
    {
        ClearHighlight();
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

        Renderer[] renderers = currentTarget.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
        foreach (Renderer renderer in renderers)
        {
            RegisterRendererForOutline(renderer);
        }
    }

    public void HighlightTargets(IEnumerable<GameObject> targets)
    {
        ClearHighlight();

        if (targets == null)
        {
            return;
        }

        foreach (GameObject target in targets)
        {
            if (target == null)
            {
                continue;
            }

            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(includeInactiveRenderers);
            foreach (Renderer renderer in renderers)
            {
                RegisterRendererForOutline(renderer);
            }

            if (currentTarget == null)
            {
                currentTarget = target.transform;
            }
        }
    }

    public void ClearHighlight()
    {
        for (int i = activeRenderers.Count - 1; i >= 0; i--)
        {
            Renderer renderer = activeRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            if (originalRenderingLayerMaskByRenderer.TryGetValue(renderer, out uint originalMask))
            {
                renderer.renderingLayerMask = originalMask;
            }
        }

        activeRenderers.Clear();
        originalRenderingLayerMaskByRenderer.Clear();
        currentTarget = null;
    }

    private void RegisterRendererForOutline(Renderer renderer)
    {
        if (renderer == null)
        {
            return;
        }

        if (renderer is ParticleSystemRenderer)
        {
            return;
        }

        if (activeRenderers.Contains(renderer))
        {
            return;
        }

        uint originalMask = renderer.renderingLayerMask;
        originalRenderingLayerMaskByRenderer[renderer] = originalMask;
        renderer.renderingLayerMask = originalMask | OutlineRenderingLayerMask;
        activeRenderers.Add(renderer);
    }
}
