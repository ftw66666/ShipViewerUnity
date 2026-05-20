using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

/// <summary>
/// ShipViewer 运行时性能监控器。
///
/// 功能：
/// 1. 采集 FPS 与帧耗时；
/// 2. 采集渲染顶点数、三角形数；
/// 3. 统计当前场景中的 Renderer 对象数量；
/// 4. 采集 Unity 当前总分配内存；
/// 5. 将结果推送到 ShipViewerUIController 的右下角状态栏。
///
/// 设计说明：
/// - 使用 ProfilerRecorder 读取 Unity Profiler 计数器，避免每帧遍历网格造成额外开销；
/// - 用采样间隔批量刷新 UI，避免每帧字符串格式化导致 GC 压力；
/// - 对象数量统计频率独立控制，因为 FindObjectsByType 比 ProfilerRecorder 更昂贵。
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipViewerPerformanceMonitor : MonoBehaviour
{
    [Header("UI Reference")]
    [SerializeField, Tooltip("右下角状态栏所在的 UI 控制器。为空时会在场景中自动查找。")]
    private ShipViewerUIController uiController;

    [Header("Sampling")]
    [SerializeField, Min(0.05f), Tooltip("状态栏刷新间隔，单位秒。建议 0.25~1 秒，避免频繁刷新 UI 文本。")]
    private float sampleInterval = 0.5f;

    [SerializeField, Min(0.1f), Tooltip("Renderer 对象数量统计间隔，单位秒。该统计相对较重，建议低频执行。")]
    private float objectCountRefreshInterval = 2f;

    [SerializeField, Tooltip("是否在统计对象数量时包含非激活对象。")]
    private bool includeInactiveObjects = true;

    [Header("Fallback")]
    [SerializeField, Tooltip("当 ProfilerRecorder 的渲染计数器不可用时，是否通过 MeshFilter 低频估算顶点和三角形数量。")]
    private bool fallbackToMeshScan = true;

    private ProfilerRecorder verticesRecorder;
    private ProfilerRecorder trianglesRecorder;
    private ProfilerRecorder mainThreadTimeRecorder;
    private ProfilerRecorder systemMemoryRecorder;

    private float sampleTimer;
    private float objectCountTimer;
    private float fpsAccumulator;
    private int fpsSampleCount;
    private int cachedObjectCount;
    private int cachedFallbackVertexCount;
    private int cachedFallbackTriangleCount;

    private static readonly string[] VertexCounterNames =
    {
        "Vertices Count",
        "Vertices"
    };

    private static readonly string[] TriangleCounterNames =
    {
        "Triangles Count",
        "Triangles"
    };

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        ResolveReferences();
        StartRecorders();
        sampleTimer = 0f;
        objectCountTimer = 0f;
        fpsAccumulator = 0f;
        fpsSampleCount = 0;
        RefreshExpensiveCounters();
        PushStatsToUi();
    }

    private void OnDisable()
    {
        DisposeRecorders();
    }

    private void Update()
    {
        float unscaledDeltaTime = Time.unscaledDeltaTime;
        if (unscaledDeltaTime > 0f)
        {
            fpsAccumulator += 1f / unscaledDeltaTime;
            fpsSampleCount++;
        }

        sampleTimer += unscaledDeltaTime;
        objectCountTimer += unscaledDeltaTime;

        if (objectCountTimer >= objectCountRefreshInterval)
        {
            objectCountTimer = 0f;
            RefreshExpensiveCounters();
        }

        if (sampleTimer >= sampleInterval)
        {
            sampleTimer = 0f;
            PushStatsToUi();
            fpsAccumulator = 0f;
            fpsSampleCount = 0;
        }
    }

    private void ResolveReferences()
    {
        if (uiController == null)
        {
            uiController = FindAnyObjectByType<ShipViewerUIController>();
        }
    }

    private void StartRecorders()
    {
        DisposeRecorders();

        mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time", 15);
        systemMemoryRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
        verticesRecorder = StartFirstValidRecorder(ProfilerCategory.Render, VertexCounterNames);
        trianglesRecorder = StartFirstValidRecorder(ProfilerCategory.Render, TriangleCounterNames);
    }

    private void DisposeRecorders()
    {
        DisposeRecorder(ref verticesRecorder);
        DisposeRecorder(ref trianglesRecorder);
        DisposeRecorder(ref mainThreadTimeRecorder);
        DisposeRecorder(ref systemMemoryRecorder);
    }

    private static ProfilerRecorder StartFirstValidRecorder(ProfilerCategory category, string[] counterNames)
    {
        for (int i = 0; i < counterNames.Length; i++)
        {
            ProfilerRecorder recorder = ProfilerRecorder.StartNew(category, counterNames[i]);
            if (recorder.Valid)
            {
                return recorder;
            }

            recorder.Dispose();
        }

        return default;
    }

    private static void DisposeRecorder(ref ProfilerRecorder recorder)
    {
        if (recorder.Valid)
        {
            recorder.Dispose();
        }

        recorder = default;
    }

    private void RefreshExpensiveCounters()
    {
        cachedObjectCount = CountRenderers();

        if (fallbackToMeshScan && (!verticesRecorder.Valid || !trianglesRecorder.Valid))
        {
            CountMeshGeometry(out cachedFallbackVertexCount, out cachedFallbackTriangleCount);
        }
    }

    private int CountRenderers()
    {
        FindObjectsInactive inactiveMode = includeInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
        Renderer[] renderers = FindObjectsByType<Renderer>(inactiveMode, FindObjectsSortMode.None);
        return renderers != null ? renderers.Length : 0;
    }

    private void CountMeshGeometry(out int vertexCount, out int triangleCount)
    {
        vertexCount = 0;
        triangleCount = 0;

        FindObjectsInactive inactiveMode = includeInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
        MeshFilter[] meshFilters = FindObjectsByType<MeshFilter>(inactiveMode, FindObjectsSortMode.None);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            Mesh mesh = meshFilters[i].sharedMesh;
            if (mesh == null)
            {
                continue;
            }

            vertexCount += mesh.vertexCount;
            triangleCount += mesh.triangles.Length / 3;
        }
    }

    private void PushStatsToUi()
    {
        if (uiController == null)
        {
            ResolveReferences();
            if (uiController == null)
            {
                return;
            }
        }

        float fps = fpsSampleCount > 0 ? fpsAccumulator / fpsSampleCount : 0f;
        float frameTimeMs = ReadFrameTimeMilliseconds();
        int vertexCount = ReadCounterAsInt(verticesRecorder, cachedFallbackVertexCount);
        int triangleCount = ReadCounterAsInt(trianglesRecorder, cachedFallbackTriangleCount);
        float memoryMb = ReadMemoryMegabytes();

        uiController.UpdatePerformanceStats(new ShipViewerUIController.ShipViewerPerformanceStats
        {
            fps = fps,
            frameTimeMs = frameTimeMs,
            vertexCount = vertexCount,
            triangleCount = triangleCount,
            objectCount = cachedObjectCount,
            memoryMb = memoryMb
        });
    }

    private float ReadFrameTimeMilliseconds()
    {
        if (mainThreadTimeRecorder.Valid && mainThreadTimeRecorder.LastValue > 0)
        {
            return mainThreadTimeRecorder.LastValue / 1_000_000f;
        }

        return Time.unscaledDeltaTime * 1000f;
    }

    private float ReadMemoryMegabytes()
    {
        if (systemMemoryRecorder.Valid && systemMemoryRecorder.LastValue > 0)
        {
            return systemMemoryRecorder.LastValue / (1024f * 1024f);
        }

        return Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f);
    }

    private static int ReadCounterAsInt(ProfilerRecorder recorder, int fallbackValue)
    {
        if (recorder.Valid && recorder.LastValue > 0)
        {
            return recorder.LastValue > int.MaxValue ? int.MaxValue : (int)recorder.LastValue;
        }

        return fallbackValue;
    }
}
