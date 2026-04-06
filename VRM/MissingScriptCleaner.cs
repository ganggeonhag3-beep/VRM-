#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MissingScriptCleaner
{
    [MenuItem("Tools/VRM/Missing 스크립트 삭제")]
    private static void RemoveMissingScriptsFromSelection()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Cleanup", "Play Mode에서는 실행하지 마세요.", "OK");
            return;
        }

        var selected = Selection.gameObjects;
        if (selected == null || selected.Length == 0)
        {
            EditorUtility.DisplayDialog("Cleanup", "Hierarchy 또는 Project에서 오브젝트/프리팹을 선택하세요.", "OK");
            return;
        }

        var totalRemoved = 0;
        foreach (var go in selected)
        {
            if (go == null) continue;

            var assetPath = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab"))
            {
                totalRemoved += RemoveMissingScriptsFromPrefabAsset(assetPath);
            }
            else
            {
                totalRemoved += RemoveMissingScriptsInHierarchy(go);
            }
        }

        EditorUtility.DisplayDialog("Cleanup", $"Removed missing scripts: {totalRemoved}", "OK");
    }

    private static int RemoveMissingScriptsFromPrefabAsset(string prefabAssetPath)
    {
        var root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
        try
        {
            var removed = RemoveMissingScriptsInHierarchy(root);
            if (removed > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
            }
            return removed;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static int RemoveMissingScriptsInHierarchy(GameObject root)
    {
        var removed = 0;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            removed += RemoveMissingScriptsOnGameObject(t.gameObject);
        }
        return removed;
    }

    private static int RemoveMissingScriptsOnGameObject(GameObject go)
    {
        // Prefer Unity's built-in remover (handles prefab instances / model bones more reliably)
        var removed = 0;
        try
        {
            removed = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
            if (removed <= 0)
            {
                return 0;
            }

            Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);

            // Record prefab instance overrides if applicable
            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }
        }
        catch
        {
            // Fallback: manual remove via serialized property (older Unity versions / edge cases)
            var components = go.GetComponents<Component>();
            if (!components.Any(c => c == null))
            {
                return 0;
            }

            Undo.RegisterCompleteObjectUndo(go, "Remove Missing Scripts");

            var so = new SerializedObject(go);
            var prop = so.FindProperty("m_Component");
            for (var i = prop.arraySize - 1; i >= 0; i--)
            {
                var element = prop.GetArrayElementAtIndex(i);
                var compRef = element.FindPropertyRelative("component");
                if (compRef != null && compRef.objectReferenceValue == null)
                {
                    // Unity sometimes requires two deletes to fully remove a null reference
                    prop.DeleteArrayElementAtIndex(i);
                    if (i < prop.arraySize)
                    {
                        var checkElement = prop.GetArrayElementAtIndex(i);
                        var checkRef = checkElement.FindPropertyRelative("component");
                        if (checkRef != null && checkRef.objectReferenceValue == null)
                        {
                            prop.DeleteArrayElementAtIndex(i);
                        }
                    }
                    removed++;
                }
            }
            so.ApplyModifiedProperties();

            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
            }
        }
        finally
        {
            EditorUtility.SetDirty(go);
            if (go.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        return removed;
    }
}
#endif
