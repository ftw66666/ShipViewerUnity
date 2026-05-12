using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 编辑器一键生成点击 Collider 工具。
///
/// Usage:
/// - 选中 ship 根节点
/// - 菜单：Tools/Ship Viewer/Build Selection Colliders For Selected Root
/// - 仅为在 device-catalog.json 中出现过的 model_name 匹配对象补 MeshCollider
/// </summary>
public static class SelectionColliderBuilder
{
    private const string DeviceCatalogPath = "Assets/Data/InfoJson/device-catalog.json";

    [MenuItem("Tools/Ship Viewer/Build Selection Colliders For Selected Root")]
    public static void BuildForSelectedRoot()
    {
        if (Selection.activeTransform == null)
        {
            Debug.LogWarning("[SelectionColliderBuilder] 请先选中 ship 根节点。");
            return;
        }

        Transform root = Selection.activeTransform;
        HashSet<string> modelNames = LoadModelNamesFromCatalog();
        if (modelNames.Count == 0)
        {
            Debug.LogWarning("[SelectionColliderBuilder] 未从 device-catalog.json 读取到任何 model_name。");
            return;
        }

        int created = 0;
        List<string> unmatched = new List<string>(modelNames);
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (Transform child in transforms)
        {
            if (child == null || !modelNames.Contains(child.name))
            {
                continue;
            }

            unmatched.Remove(child.name);
            if (child.GetComponent<Collider>() != null)
            {
                continue;
            }

            MeshFilter meshFilter = child.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                continue;
            }

            Undo.AddComponent<MeshCollider>(child.gameObject);
            created++;
        }

        Debug.Log($"[SelectionColliderBuilder] Build complete. Root={root.name}, Created={created}, Unmatched={unmatched.Count}");
        if (unmatched.Count > 0)
        {
            Debug.Log("[SelectionColliderBuilder] Unmatched model_name list: " + string.Join(", ", unmatched.OrderBy(x => x)));
        }
    }

    private static HashSet<string> LoadModelNamesFromCatalog()
    {
        HashSet<string> result = new HashSet<string>();
        string absolutePath = Path.GetFullPath(DeviceCatalogPath);
        if (!File.Exists(absolutePath))
        {
            return result;
        }

        string json = File.ReadAllText(absolutePath);
        DeviceInfo[] items = JsonHelper.FromJson<DeviceInfo>(json);
        if (items == null)
        {
            return result;
        }

        foreach (DeviceInfo item in items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.model_name))
            {
                continue;
            }

            result.Add(item.model_name.Trim());
        }

        return result;
    }

    [System.Serializable]
    private sealed class DeviceInfo
    {
        public string model_name;
    }

    private static class JsonHelper
    {
        [System.Serializable]
        private sealed class Wrapper<T>
        {
            public T[] Items;
        }

        public static T[] FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>($"{{\"Items\":{json}}}");
            return wrapper?.Items;
        }
    }
}
