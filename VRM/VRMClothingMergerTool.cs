#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class VRMClothingMergerWindow : EditorWindow
{
    private enum Mode { Merge, Remove }

    [SerializeField] private Mode m_mode = Mode.Merge;
    [SerializeField] private GameObject m_avatarRoot;
    [SerializeField] private GameObject m_clothingRoot;
    [SerializeField] private string m_name = "";

    [MenuItem("Tools/VRM/옷 입히는 툴")]
    private static void Open()
    {
        GetWindow<VRMClothingMergerWindow>("의상 도구");
    }

    // 에디터 창 UI
    private void OnGUI()
    {
        EditorGUILayout.LabelField("VRM 의상 도구", EditorStyles.boldLabel);

        m_mode = (Mode)GUILayout.Toolbar((int)m_mode, new[] { "합치기", "제거" });
        EditorGUILayout.Space(4);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            m_avatarRoot = (GameObject)EditorGUILayout.ObjectField("아바타 루트", m_avatarRoot, typeof(GameObject), true);

            if (m_mode == Mode.Merge)
            {
                m_clothingRoot = (GameObject)EditorGUILayout.ObjectField("옷 루트", m_clothingRoot, typeof(GameObject), true);
            }

            EditorGUILayout.Space(4);
            m_name = EditorGUILayout.TextField("이름", m_name);

            EditorGUILayout.Space(8);
            var buttonLabel = m_mode == Mode.Merge ? "합치기 실행" : "제거 실행";
            if (GUILayout.Button(buttonLabel, GUILayout.Height(26)))
            {
                if (m_mode == Mode.Merge) Merge();
                else Remove();
            }
        }

        if (m_avatarRoot == null)
        {
            EditorGUILayout.HelpBox("아바타 루트를 넣어주세요.", MessageType.Warning);
        }

        if (m_mode == Mode.Merge && m_clothingRoot == null)
        {
            EditorGUILayout.HelpBox("옷 루트를 넣어주세요.", MessageType.Warning);
        }

        if (m_mode == Mode.Remove && string.IsNullOrWhiteSpace(m_name))
        {
            EditorGUILayout.HelpBox("제거할 의상의 이름을 입력해주세요.", MessageType.Warning);
        }
    }

    // 실행: 본 이동 + SMR 이동 + 이름 뒤에 붙이기 + 옷 루트 삭제
    private void Merge()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("의상 합치기", "Play Mode에서는 실행하지 마세요.", "OK");
            return;
        }

        if (m_avatarRoot == null || m_clothingRoot == null)
        {
            EditorUtility.DisplayDialog("의상 합치기", "아바타 루트와 옷 루트를 모두 넣어주세요.", "OK");
            return;
        }

        var avatarRootT = m_avatarRoot.transform;
        var clothingRootT = m_clothingRoot.transform;

        if (clothingRootT == avatarRootT)
        {
            EditorUtility.DisplayDialog("의상 합치기", "아바타 루트와 옷 루트가 같습니다.", "OK");
            return;
        }

        var avatarLookup = BuildFirstTransformByNameLookup(avatarRootT);
        var clothingSmrs = clothingRootT.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(smr => smr != null)
            .ToArray();

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM 의상 합치기");

        var movedBones = 0;
        var movedSmrs = 0;
        var movedSpringNodes = 0;
        var clothingRootDeleted = false;

        try
        {
            // 1) 본 합치기: 옷(SMR)이 실제로 참조하는 본들 중에서, 이름이 같은 아바타 본이 있으면 그 아래로 넣기
            const bool keepWorldPosition = true;

            var clothingBones = CollectClothingBonesFromSmrs(clothingSmrs, clothingRootT);
            foreach (var bone in clothingBones)
            {
                if (bone == null) continue;

                if (!avatarLookup.TryGetValue(bone.name, out var avatarMatch))
                {
                    continue;
                }

                if (avatarMatch == null) continue;
                if (avatarMatch == bone) continue;

                if (avatarMatch.IsChildOf(bone))
                {
                    continue;
                }

                if (bone.parent == avatarMatch)
                {
                    continue;
                }

                Undo.SetTransformParent(bone, avatarMatch, "Merge Clothing Bone");
                bone.SetParent(avatarMatch, keepWorldPosition);
                movedBones++;
            }

            // 2) 이름 붙이기: 옷에 일괄 적용
            var nameSuffix = NormalizeSuffix(m_name);
            if (!string.IsNullOrEmpty(nameSuffix))
            {
                foreach (var bone in clothingBones)
                {
                    if (bone == null) continue;
                    if (bone.name.EndsWith(nameSuffix, StringComparison.Ordinal)) continue;

                    Undo.RecordObject(bone.gameObject, "Rename Clothing Bone");
                    bone.name = bone.name + nameSuffix;
                }
            }

            // 3) SkinnedMeshRenderer 오브젝트를 아바타 루트 바로 아래로 옮기고, 이름도 동일하게 뒤에 붙이기
            foreach (var smr in clothingSmrs)
            {
                if (smr == null) continue;

                var t = smr.transform;
                if (t == null) continue;

                if (!(t.IsChildOf(avatarRootT) && t.parent == avatarRootT))
                {
                    Undo.SetTransformParent(t, avatarRootT, "Move Clothing SkinnedMesh");
                    t.SetParent(avatarRootT, keepWorldPosition);
                    movedSmrs++;
                }

                var suffix = NormalizeSuffix(m_name);
                if (!string.IsNullOrEmpty(suffix) && !t.name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    Undo.RecordObject(t.gameObject, "Rename Clothing SkinnedMesh");
                    t.name = t.name + suffix;
                }
            }

            // 3-2) VRM SpringBone / ColliderGroup 노드(예: secondary)도 아바타 루트로 옮기고 접미사 붙이기
            //      타입 이름으로 감지해서 UniVRM 어셈블리 의존을 피함
            {
                var smrTransforms = new HashSet<Transform>(clothingSmrs.Where(s => s != null).Select(s => s.transform));
                var springOwners = CollectSpringBoneOwners(clothingRootT);
                var springOrdered = springOwners
                    .Where(t => t != null)
                    .OrderBy(GetTransformDepth) // 상위부터 처리 (부모 이동 시 자식도 따라옴)
                    .ToArray();

                foreach (var t in springOrdered)
                {
                    if (t == null) continue;
                    if (t == clothingRootT) continue;
                    if (smrTransforms.Contains(t)) continue; // 이미 SMR로 이동됨

                    // 이전 반복에서 부모가 먼저 옮겨졌으면 이미 옷 루트 하위가 아님
                    if (!t.IsChildOf(clothingRootT)) continue;

                    if (!(t.IsChildOf(avatarRootT) && t.parent == avatarRootT))
                    {
                        Undo.SetTransformParent(t, avatarRootT, "Move VRM SpringBone Node");
                        t.SetParent(avatarRootT, keepWorldPosition);
                        movedSpringNodes++;
                    }

                    var sfx = NormalizeSuffix(m_name);
                    if (!string.IsNullOrEmpty(sfx) && !t.name.EndsWith(sfx, StringComparison.Ordinal))
                    {
                        Undo.RecordObject(t.gameObject, "Rename VRM SpringBone Node");
                        t.name = t.name + sfx;
                    }
                }
            }

            // 4) 옷 루트 삭제: 옷 루트 아래에 본이 남아있으면 SMR이 깨질 수 있으므로 삭제를 건너뜀
            {
                var remainingUnderClothingRoot = clothingBones
                    .Where(b => b != null && b.IsChildOf(clothingRootT))
                    .Distinct()
                    .ToArray();

                if (remainingUnderClothingRoot.Length > 0)
                {
                    Debug.LogWarning($"[의상 합치기] 옷 루트 아래에 남은 본이 {remainingUnderClothingRoot.Length}개 있어서, 옷 루트 삭제를 건너뛰었습니다.");
                }
                else
                {
                    // 옷 루트 + 그 위로 비어있는 부모들도 함께 삭제 (avatarRootT 에서 멈춤)
                    var parent = clothingRootT.parent;
                    Undo.DestroyObjectImmediate(clothingRootT.gameObject);
                    clothingRootDeleted = true;

                    while (parent != null && parent != avatarRootT)
                    {
                        if (parent.childCount > 0) break;
                        if (parent.GetComponents<Component>().Length > 1) break; // Transform 외 컴포넌트 있음

                        var go = parent.gameObject;
                        parent = parent.parent;
                        Undo.DestroyObjectImmediate(go);
                    }
                }
            }

            var totalMoved = movedBones + movedSmrs + movedSpringNodes;
            var summary = $"완료\n\n이동: {totalMoved}개" +
                          (clothingRootDeleted ? "" : "\n\n⚠ 옷 루트 정리 안 됨 (Console 확인)");
            EditorUtility.DisplayDialog("의상 합치기", summary, "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("의상 합치기", "실패했습니다. Console을 확인하세요.", "OK");
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    // 제거 메서드
    private void Remove()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("의상 제거", "Play Mode에서는 실행하지 마세요.", "OK");
            return;
        }

        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog("의상 제거", "아바타 루트를 넣어주세요.", "OK");
            return;
        }

        var suffix = NormalizeSuffix(m_name);
        if (string.IsNullOrEmpty(suffix))
        {
            EditorUtility.DisplayDialog("의상 제거", "이름이 비어 있어 안전을 위해 중단합니다.", "OK");
            return;
        }

        var avatarRootT = m_avatarRoot.transform;

        // 접미사로 끝나는 모든 Transform 수집 (SMR/본 구분 없이)
        var targets = avatarRootT.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && t != avatarRootT && t.name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();

        if (targets.Length == 0)
        {
            EditorUtility.DisplayDialog("의상 제거", $"'{suffix}' 으로 끝나는 오브젝트가 없습니다.", "OK");
            return;
        }

        var displayName = suffix.TrimStart('_');
        var message = $"{displayName}\n\n삭제할 오브젝트: {targets.Length}개\n\n진행할까요?";

        if (!EditorUtility.DisplayDialog("의상 제거 확인", message, "삭제", "취소"))
        {
            return;
        }

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM 의상 제거");

        var deletedCount = 0;

        try
        {
            // 깊은 것부터 처리
            var sorted = targets
                .Where(t => t != null)
                .OrderByDescending(GetTransformDepth)
                .ToArray();

            foreach (var t in sorted)
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null) continue;

                Undo.DestroyObjectImmediate(go);
                deletedCount++;
            }

            EditorUtility.DisplayDialog("의상 제거", $"완료\n\n삭제: {deletedCount}개", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("의상 제거", "실패했습니다. Console을 확인하세요.", "OK");
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private static int GetTransformDepth(Transform t)
    {
        var d = 0;
        var p = t;
        while (p != null) { d++; p = p.parent; }
        return d;
    }

    private static Transform[] CollectSpringBoneOwners(Transform clothingRoot)
    {
        var result = new HashSet<Transform>();
        if (clothingRoot == null) return Array.Empty<Transform>();

        var comps = clothingRoot.GetComponentsInChildren<Component>(true);
        foreach (var c in comps)
        {
            if (c == null) continue;
            var typeName = c.GetType().Name;
            if (typeName == "VRMSpringBone"
                || typeName == "VRMSpringBoneColliderGroup"
                || typeName.StartsWith("VRM10SpringBone", StringComparison.Ordinal))
            {
                if (c.transform != clothingRoot)
                {
                    result.Add(c.transform);
                }
            }
        }
        return result.ToArray();
    }

    // 아바타 본 이름 -> Transform 룩업 만들기
    private Dictionary<string, Transform> BuildFirstTransformByNameLookup(Transform root)
    {
        var dict = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;
            if (!dict.ContainsKey(t.name))
            {
                dict.Add(t.name, t);
            }
        }
        return dict;
    }

    // 옷 SMR이 실제로 쓰는 본(rootBone/bones) 수집
    private Transform[] CollectClothingBonesFromSmrs(SkinnedMeshRenderer[] clothingSmrs, Transform clothingRoot)
    {
        var set = new HashSet<Transform>();
        if (clothingSmrs == null) return Array.Empty<Transform>();

        foreach (var smr in clothingSmrs)
        {
            if (smr == null) continue;

            if (smr.rootBone != null) set.Add(smr.rootBone);
            var bones = smr.bones;
            if (bones != null)
            {
                for (var i = 0; i < bones.Length; i++)
                {
                    var b = bones[i];
                    if (b != null) set.Add(b);
                }
            }
        }

        if (clothingRoot != null)
        {
            set.RemoveWhere(t => t == null || !t.IsChildOf(clothingRoot));
        }

        return set.ToArray();
    }

    // 이름 뒤에 붙이는 값
    private string NormalizeSuffix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var trimmed = raw.Trim();

        trimmed = trimmed.TrimEnd('_');
        if (string.IsNullOrEmpty(trimmed))
        {
            return "";
        }

        if (!trimmed.StartsWith("_", StringComparison.Ordinal))
        {
            trimmed = "_" + trimmed;
        }

        return trimmed;
    }

}
#endif
