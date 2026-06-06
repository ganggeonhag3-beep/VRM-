#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// VRM 의상 합치기/제거 툴 (단일 파일).
// Unity 2019.4.40, UniVRM 0.99.1 (VRM 0.x) 기준.
// 메뉴: 옷 입히는 툴/옷 입히는 툴 (창 열기)
public class VRMClothingMergerWindow : EditorWindow
{
    private enum Mode { Merge, Remove }
    private enum BackupMode { None, SceneClone, Prefab }
    // 본 매칭 방식: Auto(휴머노이드 우선 후 이름 폴백), 휴머노이드만, 이름만
    private enum MatchMode { Auto, HumanoidOnly, NameOnly }

    private const string PrefAvatarId = "VRMClothingMerger.AvatarId";
    private const string PrefAvatarPath = "VRMClothingMerger.AvatarPath";
    private const string PrefSuffix = "VRMClothingMerger.Suffix";
    private const string PrefBackup = "VRMClothingMerger.BackupMode";
    private const string PrefMoveStatic = "VRMClothingMerger.MoveStatic";
    private const string PrefMatchMode = "VRMClothingMerger.MatchMode";
    private const string PrefFitAlign = "VRMClothingMerger.FitAlign";
    private const string PrefFitScale = "VRMClothingMerger.FitScale";
    private const string PrefFitBindpose = "VRMClothingMerger.FitBindpose";
    private const string PrefFitVertex = "VRMClothingMerger.FitVertex";
    private const string PrefFitKeepNormals = "VRMClothingMerger.FitKeepNormals";

    [SerializeField] private Mode m_mode = Mode.Merge;
    [SerializeField] private GameObject m_avatarRoot;
    [SerializeField] private List<GameObject> m_clothingRoots = new List<GameObject>();
    // 옷별 개별 접미사 (비어 있으면 공통 m_name 사용)
    [SerializeField] private List<string> m_clothingSuffixes = new List<string>();
    [SerializeField] private string m_name = "";
    [SerializeField] private BackupMode m_backupMode = BackupMode.SceneClone;
    [SerializeField] private bool m_checkBlendShapes = true;
    [SerializeField] private bool m_validateBindposes = true;
    [SerializeField] private bool m_moveStaticRenderers = false;
    [SerializeField] private MatchMode m_matchMode = MatchMode.Auto;
    // 옷 맞춤(피팅) 옵션 - 기본은 모두 꺼짐(안전)
    [SerializeField] private bool m_fitBoneAlign = false;     // 본 위치 맞추기
    [SerializeField] private bool m_fitScale = false;         // 키(크기) 맞추기
    [SerializeField] private bool m_fitRecalcBindpose = false; // 메시 휠어짐 보정
    [SerializeField] private bool m_fitVertexAdjust = false;  // 정밀 변형(실험적)
    [SerializeField] private bool m_fitKeepNormals = false;   // 정밀 변형 후 원본 음영(노먼) 유지

    private Vector2 m_scroll;
    private string m_lastReport = "";

    // 제거 미리보기: Transform -> 삭제 여부
    private readonly Dictionary<Transform, bool> m_removeSelection = new Dictionary<Transform, bool>();
    private string m_removePreviewSuffix = "";

    // =========================================================
    // 결과 리포트 / 옵션
    // =========================================================
    private struct MergeResult
    {
        public int MovedBones;
        public int MovedSmrs;
        public int MovedSpringNodes;
        public int MovedStatic;
        public int RenamedObjects;
        public bool ClothingRootDeleted;

        public List<string> Warnings;
        public List<string> Infos;
        public List<string> PlannedBoneMoves;
        public List<string> BoneNameConflicts;

        public int TotalMoved { get { return MovedBones + MovedSmrs + MovedSpringNodes + MovedStatic; } }

        public static MergeResult Create()
        {
            return new MergeResult
            {
                Warnings = new List<string>(),
                Infos = new List<string>(),
                PlannedBoneMoves = new List<string>(),
                BoneNameConflicts = new List<string>(),
            };
        }
    }

    private struct MergeOptions
    {
        public bool DryRun;
        public bool CheckBlendShapes;
        public bool ValidateBindposes;
        public bool KeepWorldPosition;
        public bool MoveStaticRenderers;
        public MatchMode Match;
        // 옷 맞춤(피팅)
        public bool FitBoneAlign;
        public bool FitScale;
        public bool FitRecalcBindpose;
        public bool FitVertexAdjust;
        public bool FitKeepNormals;
    }

    // =========================================================
    // 메뉴 / 생명주기
    // =========================================================
    [MenuItem("\uc637 \uc785\ud788\ub294 \ud234/\uc637 \uc785\ud788\ub294 \ud234 (\ucc3d \uc5f4\uae30)")]
    private static void Open()
    {
        GetWindow<VRMClothingMergerWindow>("\uc758\uc0c1 \ub3c4\uad6c");
    }

    private void OnEnable()
    {
        // 1) 세션 내에서는 InstanceID로 빠르게 복원
        var savedId = EditorPrefs.GetInt(PrefAvatarId, 0);
        if (savedId != 0)
        {
            var obj = EditorUtility.InstanceIDToObject(savedId) as GameObject;
            if (obj != null) m_avatarRoot = obj;
        }
        // 2) 에디터 재시작 등으로 InstanceID가 유효하지 않으면 계층 경로로 복원 (비활성 포함)
        if (m_avatarRoot == null)
        {
            var path = EditorPrefs.GetString(PrefAvatarPath, "");
            if (!string.IsNullOrEmpty(path))
            {
                m_avatarRoot = FindByHierarchyPath(path);
            }
        }
        m_name = EditorPrefs.GetString(PrefSuffix, m_name);
        m_backupMode = (BackupMode)EditorPrefs.GetInt(PrefBackup, (int)BackupMode.SceneClone);
        m_moveStaticRenderers = EditorPrefs.GetBool(PrefMoveStatic, false);
        m_matchMode = (MatchMode)EditorPrefs.GetInt(PrefMatchMode, (int)MatchMode.Auto);
        m_fitBoneAlign = EditorPrefs.GetBool(PrefFitAlign, false);
        m_fitScale = EditorPrefs.GetBool(PrefFitScale, false);
        m_fitRecalcBindpose = EditorPrefs.GetBool(PrefFitBindpose, false);
        m_fitVertexAdjust = EditorPrefs.GetBool(PrefFitVertex, false);
        m_fitKeepNormals = EditorPrefs.GetBool(PrefFitKeepNormals, false);
        if (m_clothingRoots == null) m_clothingRoots = new List<GameObject>();
        if (m_clothingSuffixes == null) m_clothingSuffixes = new List<string>();
        if (m_clothingRoots.Count == 0) m_clothingRoots.Add(null);
        SyncSuffixListSize();
    }

    private void SavePrefs()
    {
        if (m_avatarRoot != null)
        {
            EditorPrefs.SetInt(PrefAvatarId, m_avatarRoot.GetInstanceID());
            EditorPrefs.SetString(PrefAvatarPath, GetHierarchyPath(m_avatarRoot.transform));
        }
        EditorPrefs.SetString(PrefSuffix, m_name ?? "");
        EditorPrefs.SetInt(PrefBackup, (int)m_backupMode);
        EditorPrefs.SetBool(PrefMoveStatic, m_moveStaticRenderers);
        EditorPrefs.SetInt(PrefMatchMode, (int)m_matchMode);
        EditorPrefs.SetBool(PrefFitAlign, m_fitBoneAlign);
        EditorPrefs.SetBool(PrefFitScale, m_fitScale);
        EditorPrefs.SetBool(PrefFitBindpose, m_fitRecalcBindpose);
        EditorPrefs.SetBool(PrefFitVertex, m_fitVertexAdjust);
        EditorPrefs.SetBool(PrefFitKeepNormals, m_fitKeepNormals);
    }

    // \ud53c\ud305\uc73c\ub85c \uc0dd\uc131\ub41c \uba54\uc2dc \uc5d0\uc14b \uc815\ub9ac \uba54\ub274
    [MenuItem("\uc637 \uc785\ud788\ub294 \ud234/\ud53c\ud305 \uba54\uc2dc \uc815\ub9ac (\uc0dd\uc131 \ud3f4\ub354 \uc0ad\uc81c)")]
    private static void CleanupFittedMeshes()
    {
        var dir = "Assets/VRMClothingFittedMeshes";
        if (!AssetDatabase.IsValidFolder(dir))
        {
            EditorUtility.DisplayDialog("\ud53c\ud305 \uba54\uc2dc \uc815\ub9ac", "\uc0dd\uc131\ub41c \ud53c\ud305 \uba54\uc2dc \ud3f4\ub354\uac00 \uc5c6\uc2b5\ub2c8\ub2e4.", "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog("\ud53c\ud305 \uba54\uc2dc \uc815\ub9ac",
            "'" + dir + "' \ud3f4\ub354\ub97c \ud1b5\uc9f8\ub85c \uc0ad\uc81c\ud569\ub2c8\ub2e4.\n\n\uc544\uc9c1 \uc500\uc5d0\uc11c \uc0ac\uc6a9 \uc911\uc778 \ud53c\ud305 \uba54\uc2dc\uac00 \uc788\uc73c\uba74 \uc637\uc774 \uae68\uc9c8 \uc218 \uc788\uc2b5\ub2c8\ub2e4. \uc9c4\ud589\ud560\uae4c\uc694?",
            "\uc0ad\uc81c", "\ucde8\uc18c"))
        {
            return;
        }
        AssetDatabase.DeleteAsset(dir);
        AssetDatabase.Refresh();
        EditorUtility.DisplayDialog("\ud53c\ud305 \uba54\uc2dc \uc815\ub9ac", "\uc644\ub8cc\ud588\uc2b5\ub2c8\ub2e4.", "OK");
    }

    // 계층 경로("A/B/C")로 씀 내 오브젝트 찾기. 비활성 포함.
    private static GameObject FindByHierarchyPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var all = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (var t in all)
        {
            if (t == null) continue;
            if (t.hideFlags != HideFlags.None) continue;
            if (EditorUtility.IsPersistent(t)) continue;
            if (GetHierarchyPath(t) == path) return t.gameObject;
        }
        return null;
    }

    private void SyncSuffixListSize()
    {
        while (m_clothingSuffixes.Count < m_clothingRoots.Count) m_clothingSuffixes.Add("");
        while (m_clothingSuffixes.Count > m_clothingRoots.Count) m_clothingSuffixes.RemoveAt(m_clothingSuffixes.Count - 1);
    }

    // =========================================================
    // UI (\ubdf0)
    // =========================================================
    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        EditorGUILayout.LabelField("VRM \uc758\uc0c1 \ub3c4\uad6c", EditorStyles.boldLabel);
        m_mode = (Mode)GUILayout.Toolbar((int)m_mode, new[] { "\ud569\uce58\uae30", "\uc81c\uac70" });
        GUILayout.Space(4f);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUI.BeginChangeCheck();
            m_avatarRoot = (GameObject)EditorGUILayout.ObjectField("\uc544\ubc14\ud0c0 \ub8e8\ud2b8", m_avatarRoot, typeof(GameObject), true);

            if (m_mode == Mode.Merge)
            {
                DrawClothingList();
            }

            GUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var label = m_mode == Mode.Merge ? "\uacf5\ud1b5 \uc774\ub984(\uc811\ubbf8\uc0ac)" : "\uc774\ub984(\uc811\ubbf8\uc0ac)";
                m_name = EditorGUILayout.TextField(label, m_name);
            }

            if (EditorGUI.EndChangeCheck()) SavePrefs();

            GUILayout.Space(4f);
            m_checkBlendShapes = EditorGUILayout.ToggleLeft("BlendShape \ucda9\ub3cc \uac80\uc0ac", m_checkBlendShapes);
            m_validateBindposes = EditorGUILayout.ToggleLeft("bindpose/rootBone \uac80\uc99d", m_validateBindposes);
            if (m_mode == Mode.Merge)
            {
                m_matchMode = (MatchMode)EditorGUILayout.EnumPopup("\ubcf8 \ub9e4\uce6d \ubc29\uc2dd", m_matchMode);
                m_moveStaticRenderers = EditorGUILayout.ToggleLeft("\uc7a5\uc2dd\ud488 \uac19\uc740 \uace0\uc815 \uba54\uc2dc\ub3c4 \ud568\uaed8 \uc62e\uae30\uae30", m_moveStaticRenderers);

                GUILayout.Space(4f);
                EditorGUILayout.LabelField("\uc637 \ub9de\ucda4(\ud53c\ud305) - \uccb4\ud615\uc774 \ub2e4\ub978 \uc637\uc744 \ub0b4 \uc544\ubc14\ud0c0\uc5d0 \ub9de\ucda4", EditorStyles.boldLabel);
                m_fitBoneAlign = EditorGUILayout.ToggleLeft("\u2460 \ubcf8 \uc704\uce58 \ub9de\ucd94\uae30 (\uc637 \ube7c\ub300\ub97c \ub0b4 \uc544\ubc14\ud0c0 \uc790\uc138\uc5d0 \ub9de\ucda4)", m_fitBoneAlign);
                m_fitScale = EditorGUILayout.ToggleLeft("\u2461 \ud0a4/\ud06c\uae30 \ub9de\ucd94\uae30 (\uc637\uc774 \ub108\ubb34 \ud06c\uac70\ub098 \uc791\uc744 \ub54c)", m_fitScale);
                m_fitRecalcBindpose = EditorGUILayout.ToggleLeft("\u2462 \uba54\uc2dc \ud720\uc5b4\uc9d0 \ubcf4\uc815 (\ubcf8\uc744 \uc625\uae34 \ub4a4 \uc637\uc774 \uae68\uc9c0\uba74)", m_fitRecalcBindpose);
                using (new EditorGUI.DisabledScope(false))
                {
                    m_fitVertexAdjust = EditorGUILayout.ToggleLeft("\u2463 \uc815\ubc00 \ubcc0\ud615 (\uc2e4\ud5d8\uc801, \ub290\ub9ac\uace0 \uacb0\uacfc\ub294 \uc637\ub9c8\ub2e4 \ub2e4\ub984)", m_fitVertexAdjust);
                }
                if (m_fitVertexAdjust)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        m_fitKeepNormals = EditorGUILayout.ToggleLeft("\u2937 \uc6d0\ubcf8 \uc74c\uc601(\ub178\uba3c) \uc720\uc9c0 (\uc74c\uc601\uc774 \uc5b4\uc0c9\ud574\uc9c0\uba74 \ucf30\uae30)", m_fitKeepNormals);
                    }
                }
                if (m_fitBoneAlign || m_fitScale || m_fitRecalcBindpose || m_fitVertexAdjust)
                {
                    EditorGUILayout.HelpBox("\ud53c\ud305\uc740 \uccb4\ud615 \ucc28\uc774\ub97c \uc904\uc5ec\uc8fc\uc9c0\ub9cc, \ucc28\uc774\uac00 \ud06c\uba74 \uad00\ud1b5/\ub4e4\ub6f0\uc774 \ub0a8\uc744 \uc218 \uc788\uc5b4\uc694. \uba3c\uc800 \ubbf8\ub9ac\ubcf4\uae30\ub85c \ud655\uc778\ud558\uace0, \uc6d0\ubcf8\uc740 \ubc31\uc5c5\ub429\ub2c8\ub2e4.", MessageType.Info);
                }
                if (m_fitRecalcBindpose && !m_fitBoneAlign)
                {
                    EditorGUILayout.HelpBox("\u2462 \uba54\uc2dc \ud720\uc5b4\uc9d0 \ubcf4\uc815\uc740 \u2460 \ubcf8 \uc704\uce58 \ub9de\ucd94\uae30\uc640 \ud568\uaed8 \uc368\uc57c \ud6a8\uacfc\uac00 \uc88b\uc2b5\ub2c8\ub2e4.", MessageType.Warning);
                }
                m_backupMode = (BackupMode)EditorGUILayout.EnumPopup("\uc2e4\ud589 \uc804 \ubc31\uc5c5", m_backupMode);
            }

            GUILayout.Space(8f);

            if (m_mode == Mode.Merge)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("\ubbf8\ub9ac\ubcf4\uae30(Dry Run)", GUILayout.Height(26)))
                    {
                        RunMerge(true);
                    }
                    if (GUILayout.Button("\ud569\uce58\uae30 \uc2e4\ud589", GUILayout.Height(26)))
                    {
                        RunMerge(false);
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("\ubbf8\ub9ac\ubcf4\uae30 \uac31\uc2e0", GUILayout.Height(26)))
                    {
                        RefreshRemovePreview();
                    }
                    if (GUILayout.Button("\uc81c\uac70 \uc2e4\ud589", GUILayout.Height(26)))
                    {
                        RunRemove();
                    }
                }
            }
        }

        DrawValidationHints();

        if (m_mode == Mode.Remove)
        {
            DrawRemovePreviewList();
        }

        if (!string.IsNullOrEmpty(m_lastReport))
        {
            GUILayout.Space(6f);
            EditorGUILayout.LabelField("\uacb0\uacfc \ub9ac\ud3ec\ud2b8", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(m_lastReport, MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawClothingList()
    {
        SyncSuffixListSize();
        EditorGUILayout.LabelField("\uc637 \ub8e8\ud2b8 (\ub2e4\uc911 \uac00\ub2a5, \uc811\ubbf8\uc0ac \ube44\uc6b0\uba74 \uacf5\ud1b5\uac12 \uc0ac\uc6a9)");
        for (var i = 0; i < m_clothingRoots.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                m_clothingRoots[i] = (GameObject)EditorGUILayout.ObjectField(m_clothingRoots[i], typeof(GameObject), true);
                m_clothingSuffixes[i] = EditorGUILayout.TextField(m_clothingSuffixes[i], GUILayout.Width(110));
                if (GUILayout.Button("\uc790\ub3d9", GUILayout.Width(40)))
                {
                    if (m_clothingRoots[i] != null) m_clothingSuffixes[i] = m_clothingRoots[i].name;
                }
                if (GUILayout.Button("-", GUILayout.Width(24)))
                {
                    m_clothingRoots.RemoveAt(i);
                    if (i < m_clothingSuffixes.Count) m_clothingSuffixes.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
            }
        }
        if (GUILayout.Button("+ \uc637 \ucd94\uac00"))
        {
            m_clothingRoots.Add(null);
            m_clothingSuffixes.Add("");
        }
    }

    private void DrawValidationHints()
    {
        if (m_avatarRoot == null)
        {
            EditorGUILayout.HelpBox("\uc544\ubc14\ud0c0 \ub8e8\ud2b8\ub97c \ub123\uc5b4\uc8fc\uc138\uc694.", MessageType.Warning);
        }
        if (m_mode == Mode.Merge && !m_clothingRoots.Any(c => c != null))
        {
            EditorGUILayout.HelpBox("\uc637 \ub8e8\ud2b8\ub97c \ucd5c\uc18c \ud558\ub098 \ub123\uc5b4\uc8fc\uc138\uc694.", MessageType.Warning);
        }
        if (m_mode == Mode.Remove && string.IsNullOrWhiteSpace(m_name))
        {
            EditorGUILayout.HelpBox("\uc81c\uac70\ud560 \uc758\uc0c1\uc758 \uc774\ub984\uc744 \uc785\ub825\ud574\uc8fc\uc138\uc694.", MessageType.Warning);
        }
    }

    private void DrawRemovePreviewList()
    {
        if (m_removeSelection.Count == 0) return;

        GUILayout.Space(6f);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("\uc0ad\uc81c \ud6c4\ubcf4: " + m_removeSelection.Count + "\uac1c", EditorStyles.boldLabel);
            if (GUILayout.Button("\uc804\uccb4 \uc120\ud0dd", GUILayout.Width(70)))
            {
                SetAllRemoveSelection(true);
            }
            if (GUILayout.Button("\uc804\uccb4 \ud574\uc81c", GUILayout.Width(70)))
            {
                SetAllRemoveSelection(false);
            }
        }
        var keys = m_removeSelection.Keys.Where(t => t != null).OrderByDescending(GetTransformDepth).ToArray();
        foreach (var t in keys)
        {
            m_removeSelection[t] = EditorGUILayout.ToggleLeft(GetHierarchyPath(t), m_removeSelection[t]);
        }
    }

    private void SetAllRemoveSelection(bool value)
    {
        var keys = m_removeSelection.Keys.ToArray();
        foreach (var k in keys) m_removeSelection[k] = value;
        Repaint();
    }

    private void RefreshRemovePreview()
    {
        m_removeSelection.Clear();
        var targets = CollectRemoveTargets(m_avatarRoot, m_name);
        foreach (var t in targets)
        {
            if (t != null) m_removeSelection[t] = true;
        }
        m_removePreviewSuffix = NormalizeSuffix(m_name);
        m_lastReport = targets.Length == 0
            ? "'" + m_removePreviewSuffix + "' \uc73c\ub85c \ub05d\ub098\ub294 \uc624\ube0c\uc81d\ud2b8\uac00 \uc5c6\uc2b5\ub2c8\ub2e4."
            : "\ubbf8\ub9ac\ubcf4\uae30: " + targets.Length + "\uac1c \ub300\uc0c1";
        Repaint();
    }

    // =========================================================
    // 실행 (\ucee8\ud2b8\ub864\ub7ec)
    // =========================================================
    private void RunMerge(bool dryRun)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \ud569\uce58\uae30", "Play Mode\uc5d0\uc11c\ub294 \uc2e4\ud589\ud558\uc9c0 \ub9c8\uc138\uc694.", "OK");
            return;
        }
        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \ud569\uce58\uae30", "\uc544\ubc14\ud0c0 \ub8e8\ud2b8\ub97c \ub123\uc5b4\uc8fc\uc138\uc694.", "OK");
            return;
        }

        SyncSuffixListSize();

        // 중복 제거 + 아바타 자기참조 차단
        var seen = new HashSet<GameObject>();
        var clothes = new List<KeyValuePair<GameObject, string>>();
        var skipped = 0;
        for (var i = 0; i < m_clothingRoots.Count; i++)
        {
            var c = m_clothingRoots[i];
            if (c == null) continue;
            if (c == m_avatarRoot)
            {
                skipped++;
                continue;
            }
            if (!seen.Add(c))
            {
                skipped++;
                continue;
            }
            var sfx = (i < m_clothingSuffixes.Count && !string.IsNullOrWhiteSpace(m_clothingSuffixes[i]))
                ? m_clothingSuffixes[i]
                : m_name;
            clothes.Add(new KeyValuePair<GameObject, string>(c, sfx));
        }

        if (clothes.Count == 0)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \ud569\uce58\uae30", "\uc720\ud6a8\ud55c \uc637 \ub8e8\ud2b8\uac00 \uc5c6\uc2b5\ub2c8\ub2e4. (\uc544\ubc14\ud0c0\uc640 \uac19\uac70\ub098 \uc911\ubcf5\uc740 \uc81c\uc678\ub429\ub2c8\ub2e4)", "OK");
            return;
        }

        var options = new MergeOptions
        {
            DryRun = dryRun,
            CheckBlendShapes = m_checkBlendShapes,
            ValidateBindposes = m_validateBindposes,
            KeepWorldPosition = true,
            MoveStaticRenderers = m_moveStaticRenderers,
            Match = m_matchMode,
            FitBoneAlign = m_fitBoneAlign,
            FitScale = m_fitScale,
            FitRecalcBindpose = m_fitRecalcBindpose,
            FitVertexAdjust = m_fitVertexAdjust,
            FitKeepNormals = m_fitKeepNormals,
        };

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM \uc758\uc0c1 \ud569\uce58\uae30");

        var sb = new StringBuilder();
        try
        {
            if (!dryRun && m_backupMode != BackupMode.None)
            {
                BackupAvatar(m_avatarRoot, m_backupMode, sb);
            }

            var totalMoved = 0;
            var totalRenamed = 0;
            var allWarnings = new List<string>();
            var allInfos = new List<string>();
            var allConflicts = new List<string>();

            foreach (var pair in clothes)
            {
                var r = Merge(m_avatarRoot, pair.Key, pair.Value, options);
                totalMoved += r.TotalMoved;
                totalRenamed += r.RenamedObjects;
                if (r.Warnings != null) allWarnings.AddRange(r.Warnings);
                if (r.Infos != null) allInfos.AddRange(r.Infos);
                if (r.BoneNameConflicts != null) allConflicts.AddRange(r.BoneNameConflicts);
                if (dryRun && r.PlannedBoneMoves != null && r.PlannedBoneMoves.Count > 0)
                {
                    sb.AppendLine("[" + pair.Key.name + "] \ubcf8 \uc774\ub3d9 \uc608\uc815 " + r.PlannedBoneMoves.Count + "\uac1c");
                }
            }

            var header = (dryRun ? "[Dry Run] " : "") + "\uc774\ub3d9 " + totalMoved + "\uac1c, \uc774\ub984\ubcc0\uacbd " + totalRenamed + "\uac1c";
            if (skipped > 0) header += " (\uc81c\uc678 " + skipped + "\uac1c)";
            sb.Insert(0, header + "\n");

            AppendCappedList(sb, "\u2139 \uc815\ubcf4:", allInfos.Distinct().ToList(), 20);
            AppendCappedList(sb, "\u26a0 \ubcf8 \uc774\ub984 \ucda9\ub3cc \ud6c4\ubcf4:", allConflicts.Distinct().ToList(), 20);
            AppendCappedList(sb, "\u26a0 \uacbd\uace0:", allWarnings.Distinct().ToList(), 20);

            m_lastReport = sb.ToString();
            EditorUtility.DisplayDialog("\uc758\uc0c1 \ud569\uce58\uae30", m_lastReport, "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("\uc758\uc0c1 \ud569\uce58\uae30", "\uc2e4\ud328\ud588\uc2b5\ub2c8\ub2e4. Console\uc744 \ud655\uc778\ud558\uc138\uc694.", "OK");
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private void RunRemove()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", "Play Mode\uc5d0\uc11c\ub294 \uc2e4\ud589\ud558\uc9c0 \ub9c8\uc138\uc694.", "OK");
            return;
        }
        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", "\uc544\ubc14\ud0c0 \ub8e8\ud2b8\ub97c \ub123\uc5b4\uc8fc\uc138\uc694.", "OK");
            return;
        }
        var suffix = NormalizeSuffix(m_name);
        if (string.IsNullOrEmpty(suffix))
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", "\uc774\ub984\uc774 \ube44\uc5b4 \uc788\uc5b4 \uc548\uc804\uc744 \uc704\ud574 \uc911\ub2e8\ud569\ub2c8\ub2e4.", "OK");
            return;
        }

        if (m_removeSelection.Count == 0 || m_removePreviewSuffix != suffix)
        {
            RefreshRemovePreview();
        }

        var selected = m_removeSelection
            .Where(kv => kv.Key != null && kv.Value)
            .Select(kv => kv.Key)
            .ToArray();

        if (selected.Length == 0)
        {
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", "\uc0ad\uc81c\ud560 \ub300\uc0c1\uc774 \uc5c6\uc2b5\ub2c8\ub2e4.", "OK");
            return;
        }

        var displayName = suffix.TrimStart('_');
        var message = displayName + "\n\n\uc0ad\uc81c\ud560 \uc624\ube0c\uc81d\ud2b8: " + selected.Length + "\uac1c\n\n\uc9c4\ud589\ud560\uae4c\uc694?";
        if (!EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70 \ud655\uc778", message, "\uc0ad\uc81c", "\ucde8\uc18c"))
        {
            return;
        }

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM \uc758\uc0c1 \uc81c\uac70");

        try
        {
            var warnings = new List<string>();
            var deleted = Remove(m_avatarRoot, m_name, selected, warnings);
            m_removeSelection.Clear();
            m_lastReport = "\uc0ad\uc81c: " + deleted + "\uac1c";
            if (AssetDatabase.IsValidFolder("Assets/VRMClothingFittedMeshes"))
            {
                m_lastReport += "\n(\ucc38\uace0: \ud53c\ud305\uc73c\ub85c \ub9cc\ub4e0 \uba54\uc2dc \uc5d0\uc14b\uc740 \ub0a8\uc544 \uc788\uc2b5\ub2c8\ub2e4. \uba54\ub274 '\uc637 \uc785\ud788\ub294 \ud234/\ud53c\ud305 \uba54\uc2dc \uc815\ub9ac'\ub85c \uc0ad\uc81c \uac00\ub2a5)";
            }
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", m_lastReport, "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("\uc758\uc0c1 \uc81c\uac70", "\uc2e4\ud328\ud588\uc2b5\ub2c8\ub2e4. Console\uc744 \ud655\uc778\ud558\uc138\uc694.", "OK");
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private static void AppendCappedList(StringBuilder sb, string header, List<string> items, int cap)
    {
        if (items == null || items.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(header);
        foreach (var item in items.Take(cap)) sb.AppendLine("  - " + item);
        if (items.Count > cap)
        {
            sb.AppendLine("  ... \uc678 " + (items.Count - cap) + "\uac1c");
        }
    }

    private static void BackupAvatar(GameObject avatar, BackupMode mode, StringBuilder report)
    {
        if (avatar == null) return;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (mode == BackupMode.Prefab)
        {
            var dir = "Assets/VRMClothingBackups";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + avatar.name + "_Backup_" + stamp + ".prefab");

            // 씀 오브젝트를 프리툭 인스턴스로 연결하지 않도록 복제본을 저장 (원본 연결 변경 방지)
            var temp = Instantiate(avatar);
            temp.name = avatar.name;
            temp.hideFlags = HideFlags.HideAndDontSave;
            var success = false;
            try
            {
                PrefabUtility.SaveAsPrefabAsset(temp, path, out success);
            }
            finally
            {
                DestroyImmediate(temp);
            }
            AssetDatabase.Refresh();

            if (success)
            {
                Debug.Log("[\uc758\uc0c1 \ud569\uce58\uae30] \ud504\ub9ac\ud22d \ubc31\uc5c5 \uc0dd\uc131: " + path + " (Undo\ub85c\ub294 \uc0ad\uc81c\ub418\uc9c0 \uc54a\uc74c)");
                if (report != null) report.AppendLine("\ud504\ub9ac\ud22d \ubc31\uc5c5: " + path + " (Undo \ubbf8\uc801\uc6a9)");
            }
            else
            {
                Debug.LogWarning("[\uc758\uc0c1 \ud569\uce58\uae30] \ud504\ub9ac\ud22d \ubc31\uc5c5 \uc2e4\ud328: " + path);
                if (report != null) report.AppendLine("\u26a0 \ud504\ub9ac\ud22d \ubc31\uc5c5 \uc2e4\ud328");
            }
            return;
        }

        // 씀 내 비활성 복제
        var clone = Instantiate(avatar);
        clone.name = avatar.name + "_Backup_" + stamp;
        clone.SetActive(false);
        Undo.RegisterCreatedObjectUndo(clone, "Backup Avatar");
        Debug.Log("[\uc758\uc0c1 \ud569\uce58\uae30] \uc500 \ubc31\uc5c5 \uc0dd\uc131: " + clone.name + " (\uc791\uc5c5 \ud6c4 \uc0ad\uc81c\ud574\ub3c4 \ub429\ub2c8\ub2e4)");
        if (report != null) report.AppendLine("\uc500 \ubc31\uc5c5: " + clone.name);
    }

    // =========================================================
    // 핵심 로직 (\ubaa8\ub378)
    // =========================================================
    private static MergeResult Merge(GameObject avatarRoot, GameObject clothingRoot, string rawName, MergeOptions options)
    {
        var result = MergeResult.Create();

        if (avatarRoot == null || clothingRoot == null)
        {
            result.Warnings.Add("\uc544\ubc14\ud0c0 \ub8e8\ud2b8\uc640 \uc637 \ub8e8\ud2b8\ub97c \ubaa8\ub450 \ub123\uc5b4\uc8fc\uc138\uc694.");
            return result;
        }

        var avatarRootT = avatarRoot.transform;
        var clothingRootT = clothingRoot.transform;

        if (clothingRootT == avatarRootT)
        {
            result.Warnings.Add("\uc544\ubc14\ud0c0 \ub8e8\ud2b8\uc640 \uc637 \ub8e8\ud2b8\uac00 \uac19\uc2b5\ub2c8\ub2e4.");
            return result;
        }

        var avatarLookup = BuildTransformsByNameLookup(avatarRootT);
        var clothingSmrs = clothingRootT.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(smr => smr != null)
            .ToArray();

        var clothingBones = CollectClothingBonesFromSmrs(clothingSmrs, clothingRootT);
        var nameSuffix = NormalizeSuffix(rawName);
        var keepWorldPosition = options.KeepWorldPosition;

        if (options.CheckBlendShapes)
        {
            CheckBlendShapeCollisions(avatarRoot, clothingSmrs, result);
        }

        // 휴머노이드 매핑 준비: 아바타/옷의 HumanBodyBones -> Transform
        // (Booth/VRoid 등 본 이름이 달라도 표준 부위로 연결하기 위함)
        var useHumanoid = options.Match != MatchMode.NameOnly;
        Dictionary<HumanBodyBones, Transform> avatarHuman = null;
        Dictionary<Transform, HumanBodyBones> clothingHuman = null;
        if (useHumanoid)
        {
            avatarHuman = BuildHumanoidBoneMap(avatarRoot);
            var clothingHumanByBone = BuildHumanoidBoneMap(clothingRoot);
            if (clothingHumanByBone != null)
            {
                clothingHuman = new Dictionary<Transform, HumanBodyBones>();
                foreach (var kv in clothingHumanByBone)
                {
                    if (kv.Value != null) clothingHuman[kv.Value] = kv.Key;
                }
            }
        }
        var humanoidUsable = avatarHuman != null && avatarHuman.Count > 0
            && clothingHuman != null && clothingHuman.Count > 0;

        if (options.Match == MatchMode.HumanoidOnly && !humanoidUsable)
        {
            result.Warnings.Add("\ud734\uba38\ub178\uc774\ub4dc \ub9e4\uce6d \uc804\uc6a9 \ubaa8\ub4dc\uc774\uc9c0\ub9cc \uc544\ubc14\ud0c0/\uc637\uc5d0 Humanoid Animator\uac00 \uc5c6\uc5b4 \uc774\ub984/\uac70\ub9ac \ub9e4\uce6d\uc73c\ub85c \ub300\uccb4\ud569\ub2c8\ub2e4.");
        }
        result.Infos.Add("\ubcf8 \ub9e4\uce6d: " + (humanoidUsable && options.Match != MatchMode.NameOnly ? "\ud734\uba38\ub178\uc774\ub4dc + \uc774\ub984 \ud3f4\ubc31" : "\uc774\ub984/\uac70\ub9ac"));

        // 1) 본 이동 계획
        var bonesToMove = new List<KeyValuePair<Transform, Transform>>();
        var unmatched = 0;
        foreach (var bone in clothingBones)
        {
            if (bone == null) continue;
            Transform avatarMatch = null;

            // (a) 휴머노이드 매칭 우선
            if (humanoidUsable && options.Match != MatchMode.NameOnly)
            {
                HumanBodyBones hbb;
                if (clothingHuman.TryGetValue(bone, out hbb))
                {
                    Transform target;
                    if (avatarHuman.TryGetValue(hbb, out target)) avatarMatch = target;
                }
            }

            // (b) 이름 + 거리 폴백 (정확한 이름 우선, 없으면 좌/우 정규화 키로 재시도)
            if (avatarMatch == null && options.Match != MatchMode.HumanoidOnly)
            {
                List<Transform> candidates;
                if ((avatarLookup.TryGetValue(bone.name, out candidates) && candidates != null && candidates.Count > 0)
                    || (avatarLookup.TryGetValue(NormalizeBoneKey(bone.name), out candidates) && candidates != null && candidates.Count > 0))
                {
                    avatarMatch = PickBestMatch(bone, candidates);
                }
            }

            if (avatarMatch == null) { unmatched++; continue; }
            if (avatarMatch == bone) continue;
            if (avatarMatch.IsChildOf(bone)) continue;
            if (bone.parent == avatarMatch) continue;

            var dist = Vector3.Distance(bone.position, avatarMatch.position);
            if (dist > 0.05f)
            {
                result.BoneNameConflicts.Add(bone.name + " -> " + avatarMatch.name + " (\uac70\ub9ac " + dist.ToString("F3") + "m)");
            }

            bonesToMove.Add(new KeyValuePair<Transform, Transform>(bone, avatarMatch));
            result.PlannedBoneMoves.Add(bone.name + " -> " + GetHierarchyPath(avatarMatch));
        }

        if (unmatched > 0)
        {
            result.Infos.Add("\uc9c1\uc811 \ub9e4\uce6d\ub418\uc9c0 \uc54a\uc740 \uc637 \ubcf8 " + unmatched + "\uac1c\ub294 \ucd5c\uc0c1\uc704 \uc870\uc0c1\uc744 \uc544\ubc14\ud0c0\ub85c \uc62e\uaca8 \uc720\uc9c0\ud569\ub2c8\ub2e4 (\uc2a4\ucee4\ud2b8/\uba38\ub9ac\uce74\ub77d \ud754\ub4e4\ub9bc \ubcf8 \ub4f1).");
        }

        // 정적 메시 수집 (SMR 아닌 MeshRenderer)
        var staticRenderers = options.MoveStaticRenderers
            ? clothingRootT.GetComponentsInChildren<MeshRenderer>(true)
                .Where(mr => mr != null)
                .Select(mr => mr.transform)
                .Where(t => t != null && t.IsChildOf(clothingRootT) && t != clothingRootT)
                .Distinct()
                .ToArray()
            : new Transform[0];

        if (options.DryRun)
        {
            result.MovedBones = bonesToMove.Count;
            result.MovedSmrs = clothingSmrs.Count(s => s != null && s.transform.parent != avatarRootT);
            result.MovedSpringNodes = CollectSpringBoneOwners(clothingRootT)
                .Count(t => t != null && t != clothingRootT);
            result.MovedStatic = staticRenderers.Length;

            // \ud53c\ud305 \uc608\uace0
            if (options.FitScale) result.Infos.Add("\ud0a4/\ud06c\uae30 \ub9de\ucd94\uae30 \uc608\uc815");
            if (options.FitBoneAlign) result.Infos.Add("\ubcf8 \uc704\uce58 \ub9de\ucd94\uae30 \uc608\uc815 (\ubcf8 " + bonesToMove.Count + "\uac1c)");
            if (options.FitRecalcBindpose) result.Infos.Add("\uba54\uc2dc \ud720\uc5b4\uc9d0 \ubcf4\uc815 \uc608\uc815 (\uc0c8 \uba54\uc2dc \uc800\uc7a5)");
            if (options.FitVertexAdjust) result.Infos.Add("\uc815\ubc00 \ubcc0\ud615(\uc2e4\ud5d8\uc801) \uc608\uc815 (\ub290\ub9b4 \uc218 \uc788\uc74c, \uc0c8 \uba54\uc2dc \uc800\uc7a5)");
            return result;
        }

        // 1-0) \ud0a4/\ud06c\uae30 \ub9de\ucd94\uae30: \uc544\ubc14\ud0c0\uc640 \uc637\uc758 Hips \ub192\uc774 \ube44\uc728\ub85c \uc637 \ub8e8\ud2b8 \uc2a4\ucf00\uc77c \ubcf4\uc815 (\ubcf8 \uc774\ub3d9 \uc804 \uc801\uc6a9)
        var scaledForFit = false;
        if (options.FitScale)
        {
            scaledForFit = ApplyScaleFit(avatarRoot, clothingRoot, avatarHuman, clothingHuman, result);
        }

        // 1) 본 이동
        // \uc8fc\uc758: \ud0a4 \ub9de\ucd94\uae30(\uc2a4\ucf00\uc77c)\ub97c \ud588\uac70\ub098 \ubcf8 \uc704\uce58 \ub9de\ucd94\uae30\ub97c \ucf1c\uba74,
        //       \uc6d4\ub4dc \uc704\uce58 \uc720\uc9c0(keepWorldPosition=true)\ub85c \uc625\uae30\uba74 \ubcf4\uc815\uc774 \ubb34\ud6a8\ud654\ub418\ubbc0\ub85c
        //       \uc774 \uacbd\uc6b0\uc5d0\ub294 \ub85c\uceec \uae30\uc900\uc73c\ub85c \ubd99\uc5ec \uc544\ubc14\ud0c0 \ubcf8 \uc790\uc138\ub97c \ub530\ub974\uac8c \ud568
        var alignToBone = options.FitBoneAlign || scaledForFit;
        if (scaledForFit && !options.FitBoneAlign)
        {
            result.Infos.Add("\ud0a4 \ub9de\ucd94\uae30\ub97c \uc801\uc6a9\ud574 \ubcf8\uc744 \uc544\ubc14\ud0c0 \uc790\uc138\uc5d0 \uc790\ub3d9 \uc815\ub82c\ud588\uc2b5\ub2c8\ub2e4 (\uc2a4\ucf00\uc77c \ubcf4\uc815 \uc720\uc9c0\ub97c \uc704\ud574 \ud544\uc694).");
        }
        foreach (var pair in bonesToMove)
        {
            var bone = pair.Key;
            var target = pair.Value;
            if (bone == null || target == null) continue;
            Undo.SetTransformParent(bone, target, "Merge Clothing Bone");
            if (alignToBone)
            {
                bone.SetParent(target, false);
                bone.localPosition = Vector3.zero;
                bone.localRotation = Quaternion.identity;
            }
            else
            {
                bone.SetParent(target, keepWorldPosition);
            }
            result.MovedBones++;
        }

        // 2) 본 이름 접미사
        if (!string.IsNullOrEmpty(nameSuffix))
        {
            foreach (var bone in clothingBones)
            {
                if (bone == null) continue;
                if (bone.name.EndsWith(nameSuffix, StringComparison.Ordinal)) continue;
                Undo.RecordObject(bone.gameObject, "Rename Clothing Bone");
                bone.name = bone.name + nameSuffix;
                result.RenamedObjects++;
            }
        }

        // 3) SMR 오브젝트 이동 + 접미사
        foreach (var smr in clothingSmrs)
        {
            if (smr == null) continue;
            var t = smr.transform;
            if (t == null) continue;

            if (!(t.IsChildOf(avatarRootT) && t.parent == avatarRootT))
            {
                Undo.SetTransformParent(t, avatarRootT, "Move Clothing SkinnedMesh");
                t.SetParent(avatarRootT, keepWorldPosition);
                result.MovedSmrs++;
            }

            if (!string.IsNullOrEmpty(nameSuffix) && !t.name.EndsWith(nameSuffix, StringComparison.Ordinal))
            {
                Undo.RecordObject(t.gameObject, "Rename Clothing SkinnedMesh");
                t.name = t.name + nameSuffix;
                result.RenamedObjects++;
            }
        }

        // 3-2) VRM SpringBone / ColliderGroup 노드 이동 + 접미사
        {
            var smrTransforms = new HashSet<Transform>(clothingSmrs.Where(s => s != null).Select(s => s.transform));
            var springOrdered = CollectSpringBoneOwners(clothingRootT)
                .Where(t => t != null)
                .OrderBy(GetTransformDepth)
                .ToArray();

            foreach (var t in springOrdered)
            {
                if (t == null || t == clothingRootT) continue;
                if (smrTransforms.Contains(t)) continue;
                if (!t.IsChildOf(clothingRootT)) continue;

                if (!(t.IsChildOf(avatarRootT) && t.parent == avatarRootT))
                {
                    Undo.SetTransformParent(t, avatarRootT, "Move VRM SpringBone Node");
                    t.SetParent(avatarRootT, keepWorldPosition);
                    result.MovedSpringNodes++;
                }

                if (!string.IsNullOrEmpty(nameSuffix) && !t.name.EndsWith(nameSuffix, StringComparison.Ordinal))
                {
                    Undo.RecordObject(t.gameObject, "Rename VRM SpringBone Node");
                    t.name = t.name + nameSuffix;
                    result.RenamedObjects++;
                }
            }
        }

        // 3-3) 정적 메시(MeshRenderer) 이동 + 접미사
        //      자식이 부모를 따라 이동되어도 접미사는 모두 적용되도록 이름을 먼저 처리
        if (options.MoveStaticRenderers)
        {
            if (!string.IsNullOrEmpty(nameSuffix))
            {
                foreach (var t in staticRenderers)
                {
                    if (t == null || t == clothingRootT) continue;
                    if (t.name.EndsWith(nameSuffix, StringComparison.Ordinal)) continue;
                    Undo.RecordObject(t.gameObject, "Rename Clothing StaticMesh");
                    t.name = t.name + nameSuffix;
                    result.RenamedObjects++;
                }
            }

            // 그 다음 상위부터 이동 (부모가 먼저 옮겨지면 자식은 따라감)
            foreach (var t in staticRenderers.OrderBy(GetTransformDepth))
            {
                if (t == null || t == clothingRootT) continue;
                if (!t.IsChildOf(clothingRootT)) continue; // 이미 부모와 함께 이동됨

                if (!(t.IsChildOf(avatarRootT) && t.parent == avatarRootT))
                {
                    Undo.SetTransformParent(t, avatarRootT, "Move Clothing StaticMesh");
                    t.SetParent(avatarRootT, keepWorldPosition);
                    result.MovedStatic++;
                }
            }
        }

        // 3-4) \ub9e4\uce6d \uc548 \ub41c \uc637 \ubcf8(\uc2a4\ucee4\ud2b8/\uba38\ub9ac\uce74\ub77d/\ud754\ub4e4\ub9bc \ubcf8 \ub4f1)\uc758 \ucd5c\uc0c1\uc704 \uc870\uc0c1\uc744 \uc544\ubc14\ud0c0\ub85c \uc774\ub3d9
        //      \uc774\ub807\uac8c \ud574\uc57c \uc637 \ub8e8\ud2b8\uc5d0 \ub0a8\uc544 SMR\uc774 \ubb36\uc774\uace0 \uc815\ub9ac\uac00 \uc548 \ub418\ub294 \ubb38\uc81c\ub97c \ub9c9\uc74c
        {
            var movedBoneSet = new HashSet<Transform>();
            foreach (var pair in bonesToMove) { if (pair.Key != null) movedBoneSet.Add(pair.Key); }

            // \uc637 \ub8e8\ud2b8 \uc9c1\ud558 \uc790\uc2dd \uc911, \uc544\uc9c1 \uc637 \ub8e8\ud2b8 \ud558\uc704\uc5d0 \ub0a8\uc544 \uc788\uace0 \ub80c\ub354\ub7ec\ub098 '\uc544\uc9c1 \uc548 \uc625\uae34 \ubcf8'\uc744 \ud3ec\ud568\ud558\ub294 \uac83\ub9cc \uc774\ub3d9
            // (\uc774\ubbf8 \uc625\uaca8\uc9c4 \ubcf8\ub9cc \ub4e4\uc5b4\uc788\ub294 \uc870\uc0c1\uc740 \uac74\ub108\ub6f0\uc5b4 \uacc4\uce35 \uaf2c\uc784 \ubc29\uc9c0)
            var topLevelToMove = new List<Transform>();
            for (var i = 0; i < clothingRootT.childCount; i++)
            {
                var child = clothingRootT.GetChild(i);
                if (child == null) continue;
                if (child.name == FitMarkerName) continue; // \ud53c\ud305 \ub9c8\ucee4\ub294 \uc81c\uc678

                var hasRenderer = child.GetComponentsInChildren<Renderer>(true).Any(r => r != null);
                var hasUnmovedBone = false;
                foreach (var b in clothingBones)
                {
                    if (b == null) continue;
                    if (movedBoneSet.Contains(b)) continue; // \uc774\ubbf8 \uc544\ubc14\ud0c0\ub85c \uc625\uae34 \ubcf8\uc740 \uc81c\uc678
                    if (b.IsChildOf(child)) { hasUnmovedBone = true; break; }
                }
                if (hasRenderer || hasUnmovedBone) topLevelToMove.Add(child);
            }

            foreach (var child in topLevelToMove)
            {
                if (child == null) continue;
                if (!child.IsChildOf(clothingRootT)) continue;

                if (!string.IsNullOrEmpty(nameSuffix) && !child.name.EndsWith(nameSuffix, StringComparison.Ordinal))
                {
                    Undo.RecordObject(child.gameObject, "Rename Clothing Node");
                    child.name = child.name + nameSuffix;
                    result.RenamedObjects++;
                }

                Undo.SetTransformParent(child, avatarRootT, "Move Unmatched Clothing Node");
                child.SetParent(avatarRootT, keepWorldPosition);
                result.MovedSpringNodes++;
            }
        }

        // 3-5) MToon/\uba38\ud2f0\ub9ac\uc5bc \uc548\ub0b4 (VRoid \uc637)
        if (clothingSmrs.Any(s => s != null && s.sharedMaterials != null
            && s.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.IndexOf("MToon", StringComparison.OrdinalIgnoreCase) >= 0)))
        {
            result.Infos.Add("VRoid MToon \uba38\ud2f0\ub9ac\uc5bc \uac10\uc9c0\ub428. \uba38\ud2f0\ub9ac\uc5bc\uc740 \uadf8\ub300\ub85c \uc720\uc9c0\ub429\ub2c8\ub2e4 (\ube4c\ub4dc \ud0c0\uac9f\uc5d0 \ub530\ub77c \uc154\uc774\ub354 \ud3ec\ud568 \ud655\uc778).");
        }

        // 4) 옷 루트 정리
        {
            var remaining = clothingBones
                .Where(b => b != null && b.IsChildOf(clothingRootT))
                .Distinct()
                .ToArray();

            if (remaining.Length > 0)
            {
                result.Warnings.Add("\uc637 \ub8e8\ud2b8 \uc544\ub798\uc5d0 \ub0a8\uc740 \ubcf8\uc774 " + remaining.Length + "\uac1c \uc788\uc5b4 \uc637 \ub8e8\ud2b8 \uc0ad\uc81c\ub97c \uac74\ub108\ub6f0\uc5c8\uc2b5\ub2c8\ub2e4.");
            }
            else if (clothingRootT.GetComponentsInChildren<Renderer>(true).Any(r => r != null))
            {
                result.Warnings.Add("\uc637 \ub8e8\ud2b8 \uc544\ub798\uc5d0 \uc544\uc9c1 \ub80c\ub354\ub7ec\uac00 \ub0a8\uc544 \uc788\uc5b4 \uc0ad\uc81c\ub97c \uac74\ub108\ub6f0\uc5c8\uc2b5\ub2c8\ub2e4.");
            }
            else
            {
                var parent = clothingRootT.parent;
                Undo.DestroyObjectImmediate(clothingRootT.gameObject);
                result.ClothingRootDeleted = true;

                while (parent != null && parent != avatarRootT)
                {
                    if (parent.childCount > 0) break;
                    if (parent.GetComponents<Component>().Length > 1) break;
                    var go = parent.gameObject;
                    parent = parent.parent;
                    Undo.DestroyObjectImmediate(go);
                }
            }
        }

        // 4-2) \uba54\uc2dc \ud720\uc5b4\uc9d0 \ubcf4\uc815 (\ubcf8\uc744 \uc625\uae34 \ub4a4 bindpose \uc7ac\uacc4\uc0b0). \uc6d0\ubcf8 \ubcf4\uc874\uc744 \uc704\ud574 \uc0c8 \uba54\uc2dc\ub85c \uc800\uc7a5.
        if (options.FitRecalcBindpose)
        {
            foreach (var smr in clothingSmrs)
            {
                if (smr == null) continue;
                RecalcBindposes(smr, result, true);
            }
        }

        // 4-3) \uc2e4\ud5d8\uc801 \uc815\ubc00 \ubcc0\ud615. \uc544\ubc14\ud0c0 SMR \ud45c\uba74\uc744 \uae30\uc900\uc73c\ub85c \uc637 \uc815\uc810\uc744 \uc57d\ud558\uac8c \ub04c\uc5b4\ub2f9\uae40.
        if (options.FitVertexAdjust)
        {
            var avatarSmrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(s => s != null && !clothingSmrs.Contains(s))
                .ToArray();
            foreach (var smr in clothingSmrs)
            {
                if (smr == null) continue;
                ApplyVertexAdjust(smr, avatarSmrs, result, options.FitKeepNormals);
            }
        }

        // 5) bindpose / rootBone 검증
        if (options.ValidateBindposes)
        {
            foreach (var smr in clothingSmrs)
            {
                if (smr == null) continue;
                ValidateSkinnedMesh(smr, result);
            }
        }

        return result;
    }

    private static int Remove(GameObject avatarRoot, string rawName, IEnumerable<Transform> overrideTargets, List<string> warnings)
    {
        if (avatarRoot == null)
        {
            if (warnings != null) warnings.Add("\uc544\ubc14\ud0c0 \ub8e8\ud2b8\ub97c \ub123\uc5b4\uc8fc\uc138\uc694.");
            return 0;
        }

        var suffix = NormalizeSuffix(rawName);
        if (string.IsNullOrEmpty(suffix))
        {
            if (warnings != null) warnings.Add("\uc774\ub984\uc774 \ube44\uc5b4 \uc788\uc5b4 \uc548\uc804\uc744 \uc704\ud574 \uc911\ub2e8\ud569\ub2c8\ub2e4.");
            return 0;
        }

        var targets = overrideTargets != null
            ? overrideTargets.Where(t => t != null).ToArray()
            : CollectRemoveTargets(avatarRoot, rawName);

        var sorted = targets
            .Where(t => t != null)
            .OrderByDescending(GetTransformDepth)
            .ToArray();

        var deleted = 0;
        foreach (var t in sorted)
        {
            if (t == null) continue;
            var go = t.gameObject;
            if (go == null) continue;
            Undo.DestroyObjectImmediate(go);
            deleted++;
        }
        return deleted;
    }

    private static Transform[] CollectRemoveTargets(GameObject avatarRoot, string rawName)
    {
        var suffix = NormalizeSuffix(rawName);
        if (avatarRoot == null || string.IsNullOrEmpty(suffix)) return new Transform[0];

        var avatarRootT = avatarRoot.transform;
        return avatarRootT.GetComponentsInChildren<Transform>(true)
            .Where(t => t != null && t != avatarRootT && t.name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
    }

    // =========================================================
    // 검증 헬퍼
    // =========================================================
    private static void CheckBlendShapeCollisions(GameObject avatarRoot, SkinnedMeshRenderer[] clothingSmrs, MergeResult result)
    {
        var hasProxy = avatarRoot.GetComponentsInChildren<Component>(true)
            .Any(c => c != null && c.GetType().Name == "VRMBlendShapeProxy");
        if (!hasProxy) return;

        foreach (var smr in clothingSmrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            if (smr.sharedMesh.blendShapeCount > 0)
            {
                result.Warnings.Add("\uc637 SMR '" + smr.name + "'\uc5d0 BlendShape\uac00 \uc788\uc5b4 \ud45c\uc815 \ud074\ub9bd\uacfc \ucda9\ub3cc \uac00\ub2a5. \uc774\ub984 \ubcc0\uacbd \ud6c4 BlendShapeProxy\ub97c \ud655\uc778\ud558\uc138\uc694.");
            }
        }
    }

    private static void ValidateSkinnedMesh(SkinnedMeshRenderer smr, MergeResult result)
    {
        if (smr.rootBone == null)
        {
            result.Warnings.Add("SMR '" + smr.name + "'\uc758 rootBone\uc774 null\uc785\ub2c8\ub2e4.");
        }

        var bones = smr.bones;
        if (bones != null)
        {
            for (var i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null)
                {
                    result.Warnings.Add("SMR '" + smr.name + "'\uc758 bones[" + i + "]\uac00 null\uc785\ub2c8\ub2e4 (bindpose \uae68\uc9d0 \uac00\ub2a5).");
                    break;
                }
            }
        }

        if (smr.sharedMesh != null && bones != null)
        {
            var bindCount = smr.sharedMesh.bindposes != null ? smr.sharedMesh.bindposes.Length : 0;
            if (bindCount != bones.Length)
            {
                result.Warnings.Add("SMR '" + smr.name + "': bindposes(" + bindCount + ") != bones(" + bones.Length + ").");
            }
        }
    }

    // =========================================================
    // 공용 유틸
    // =========================================================
    // 옷과 아바타의 Hips 세로 높이 비율로 옷 루트 스케일을 보정해 키를 대략 맞춤. 적용했으면 true.
    private static bool ApplyScaleFit(GameObject avatarRoot, GameObject clothingRoot,
        Dictionary<HumanBodyBones, Transform> avatarHuman, Dictionary<Transform, HumanBodyBones> clothingHuman, MergeResult result)
    {
        if (avatarRoot == null || clothingRoot == null) return false;

        // 이미 피팅한 옷에 재실행 시 스케일 누적 방지
        if (HasFitMarker(clothingRoot))
        {
            result.Warnings.Add("\ud0a4 \ub9de\ucd94\uae30: \uc774\ubbf8 \ud53c\ud305\ub41c \uc637\uc774\ub77c \uc2a4\ucf00\uc77c \ubcf4\uc815\uc744 \uac74\ub108\ub6f0\uc5c8\uc2b5\ub2c8\ub2e4 (\uc911\ubcf5 \ud655\ub300 \ubc29\uc9c0).");
            return false;
        }

        Transform avatarHips = null;
        Transform clothingHips = null;
        if (avatarHuman != null) avatarHuman.TryGetValue(HumanBodyBones.Hips, out avatarHips);
        if (clothingHuman != null)
        {
            foreach (var kv in clothingHuman)
            {
                if (kv.Value == HumanBodyBones.Hips) { clothingHips = kv.Key; break; }
            }
        }
        if (avatarHips == null || clothingHips == null)
        {
            result.Warnings.Add("\ud0a4 \ub9de\ucd94\uae30: Hips \ubcf8\uc744 \ucc3e\uc9c0 \ubabb\ud574 \uac74\ub108\ub6f0\uc5c8\uc2b5\ub2c8\ub2e4 (\ud734\uba38\ub178\uc774\ub4dc \uc544\ubc14\ud0c0/\uc637\uc5d0\uc11c\ub9cc \ub3d9\uc791).");
            return false;
        }

        var avatarH = Mathf.Abs(avatarHips.position.y - avatarRoot.transform.position.y);
        var clothingH = Mathf.Abs(clothingHips.position.y - clothingRoot.transform.position.y);
        if (clothingH < 1e-4f || avatarH < 1e-4f) return false;

        var ratio = avatarH / clothingH;
        if (Mathf.Abs(ratio - 1f) < 0.01f) return false; // \uac70\uc758 \uac19\uc73c\uba74 \uc0dd\ub7b5

        Undo.RecordObject(clothingRoot.transform, "Fit Clothing Scale");
        clothingRoot.transform.localScale *= ratio;
        AddFitMarker(clothingRoot);
        result.Infos.Add("\ud0a4 \ub9de\ucd94\uae30 \uc801\uc6a9: \uc637 \ud06c\uae30 x" + ratio.ToString("F3"));
        return true;
    }

    // 메시 휠어짐 보정: 본을 옥긴 뒤 SMR의 bindpose를 새 본 기준으로 다시 계산.
    private static void RecalcBindposes(SkinnedMeshRenderer smr, MergeResult result, bool saveNewMesh)
    {
        if (smr == null || smr.sharedMesh == null) return;
        var bones = smr.bones;
        if (bones == null || bones.Length == 0) return;
        if (smr.rootBone == null) return;

        var mesh = saveNewMesh ? UnityEngine.Object.Instantiate(smr.sharedMesh) : smr.sharedMesh;
        var newBind = new Matrix4x4[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) { newBind[i] = Matrix4x4.identity; continue; }
            // \ud45c\uc900 bindpose \uacf5\uc2dd: bone.worldToLocal x renderer.localToWorld
            // (\ubcf8\uc5d0 \uc2a4\ucf00\uc77c\uc774 \uc11e\uc5ec \uc788\uc73c\uba74 \uacb0\uacfc\uac00 \ud2c0\uc5b4\uc9c8 \uc218 \uc788\uc74c)
            newBind[i] = bones[i].worldToLocalMatrix * smr.transform.localToWorldMatrix;
        }
        mesh.bindposes = newBind;
        if (saveNewMesh)
        {
            mesh = SaveMeshAsset(mesh, smr.name + "_bindfit");
            smr.sharedMesh = mesh;
        }
        else smr.sharedMesh.bindposes = newBind;
        result.Infos.Add("\uba54\uc2dc \ud720\uc5b4\uc9d0 \ubcf4\uc815: " + smr.name + (saveNewMesh ? " (\uc0c8 \uba54\uc2dc \uc800\uc7a5)" : ""));
    }

    // 실험적 정밀 변형: 옷 정점을 가장 가까운 아바타 표면 방향으로 약하게 끌어당김.
    // 공간 해시 그리드로 최근접 탐색을 가속(O(V*R) -> 거의 O(V)).
    // 너무 먼 정점은 건드리지 않고, 거리에 따라 강도를 줄여 왜곡을 최소화.
    // 원본 메시는 건드리지 않고 새 메시로 저장. 결과 품질은 보장하지 않음.
    private static void ApplyVertexAdjust(SkinnedMeshRenderer clothSmr, SkinnedMeshRenderer[] avatarSmrs, MergeResult result, bool keepNormals)
    {
        if (clothSmr == null || clothSmr.sharedMesh == null) return;
        if (avatarSmrs == null || avatarSmrs.Length == 0) return;

        // 아바타 표면 정점을 월드 좌표로 수집 (참조점 상한 상향: ~20000)
        var refPoints = new List<Vector3>();
        foreach (var asmr in avatarSmrs)
        {
            if (asmr == null || asmr.sharedMesh == null) continue;
            var baked = new Mesh();
            try
            {
                asmr.BakeMesh(baked);
                var verts = baked.vertices;
                var l2w = asmr.transform.localToWorldMatrix;
                var step = Mathf.Max(1, verts.Length / 20000);
                for (var i = 0; i < verts.Length; i += step)
                {
                    refPoints.Add(l2w.MultiplyPoint3x4(verts[i]));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baked);
            }
        }
        if (refPoints.Count == 0) return;

        var grid = new SpatialHashGrid(refPoints);

        var mesh = UnityEngine.Object.Instantiate(clothSmr.sharedMesh);
        var clothVerts = mesh.vertices;
        var l2wCloth = clothSmr.transform.localToWorldMatrix;
        var w2lCloth = clothSmr.transform.worldToLocalMatrix;
        var total = clothVerts.Length;

        // 최대 영향 거리: 그리드 셀 크기의 약 3배. 이보다 멀면 끌어당기지 않음.
        var maxDist = grid.CellSize * 3f;
        var maxDistSqr = maxDist * maxDist;
        const float maxPull = 0.35f; // 가까운 정점의 최대 끌어당김 강도
        var moved = 0;

        try
        {
            for (var v = 0; v < total; v++)
            {
                if ((v & 1023) == 0)
                {
                    var cancel = EditorUtility.DisplayCancelableProgressBar(
                        "\uc815\ubc00 \ubcc0\ud615", clothSmr.name + " (" + v + "/" + total + ")",
                        total > 0 ? (float)v / total : 1f);
                    if (cancel)
                    {
                        result.Warnings.Add("\uc815\ubc00 \ubcc0\ud615 \ucde8\uc18c\ub428: " + clothSmr.name);
                        break;
                    }
                }
                var wp = l2wCloth.MultiplyPoint3x4(clothVerts[v]);
                float bestSqr;
                var best = grid.FindNearest(wp, maxDist, out bestSqr);
                if (bestSqr > maxDistSqr) continue; // 너무 멀면 건드리지 않음

                // 거리 폴오프: 가까울수록 강하게, 멀수록 약하게 (부드러운 감쇠)
                var t = Mathf.Clamp01(1f - Mathf.Sqrt(bestSqr) / maxDist);
                var pull = maxPull * (t * t);
                if (pull <= 0f) continue;

                var target = Vector3.Lerp(wp, best, pull);
                clothVerts[v] = w2lCloth.MultiplyPoint3x4(target);
                moved++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        mesh.vertices = clothVerts;
        mesh.RecalculateBounds();
        if (!keepNormals) mesh.RecalculateNormals();
        mesh = SaveMeshAsset(mesh, clothSmr.name + "_vertexfit");
        clothSmr.sharedMesh = mesh;
        result.Infos.Add("\uc815\ubc00 \ubcc0\ud615(\uc2e4\ud5d8\uc801) \uc801\uc6a9: " + clothSmr.name + " (\uc815\uc810 " + moved + "/" + total + "\uac1c \ubcf4\uc815, \uc0c8 \uba54\uc2dc \uc800\uc7a5)");
    }

    // 균일 공간 해시 그리드: 최근접 점 탐색 가속용. 에디터 전용, 경량 구현.
    private class SpatialHashGrid
    {
        public readonly float CellSize;
        private readonly Dictionary<long, List<Vector3>> m_cells = new Dictionary<long, List<Vector3>>();
        private readonly Vector3 m_origin;

        public SpatialHashGrid(List<Vector3> points)
        {
            // 셀 크기: 점 분포 범위 기준으로 적당히 (대략 점당 1개 셀이 되도록)
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (var i = 0; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            m_origin = min;
            var extent = max - min;
            var volume = Mathf.Max(1e-6f, extent.x * extent.y * extent.z);
            var perCell = Mathf.Pow(volume / Mathf.Max(1, points.Count), 1f / 3f);
            CellSize = Mathf.Clamp(perCell, 0.005f, 0.2f); // 0.5cm ~ 20cm

            for (var i = 0; i < points.Count; i++)
            {
                var key = KeyOf(points[i]);
                List<Vector3> list;
                if (!m_cells.TryGetValue(key, out list))
                {
                    list = new List<Vector3>();
                    m_cells[key] = list;
                }
                list.Add(points[i]);
            }
        }

        private long KeyOf(Vector3 p)
        {
            var x = Mathf.FloorToInt((p.x - m_origin.x) / CellSize);
            var y = Mathf.FloorToInt((p.y - m_origin.y) / CellSize);
            var z = Mathf.FloorToInt((p.z - m_origin.z) / CellSize);
            return Encode(x, y, z);
        }

        private static long Encode(int x, int y, int z)
        {
            // 21비트씩 패킹 (충분히 큰 범위)
            const long mask = 0x1FFFFF;
            return ((x & mask) << 42) | ((y & mask) << 21) | (z & mask);
        }

        // wp 기준 maxDist 안에서 가장 가까운 점 반환. 주변 셀만 탐색.
        public Vector3 FindNearest(Vector3 wp, float maxDist, out float bestSqr)
        {
            var radius = Mathf.Max(1, Mathf.CeilToInt(maxDist / CellSize));
            var cx = Mathf.FloorToInt((wp.x - m_origin.x) / CellSize);
            var cy = Mathf.FloorToInt((wp.y - m_origin.y) / CellSize);
            var cz = Mathf.FloorToInt((wp.z - m_origin.z) / CellSize);

            var best = wp;
            bestSqr = float.MaxValue;
            for (var dx = -radius; dx <= radius; dx++)
            for (var dy = -radius; dy <= radius; dy++)
            for (var dz = -radius; dz <= radius; dz++)
            {
                List<Vector3> list;
                if (!m_cells.TryGetValue(Encode(cx + dx, cy + dy, cz + dz), out list)) continue;
                for (var i = 0; i < list.Count; i++)
                {
                    var sqr = (list[i] - wp).sqrMagnitude;
                    if (sqr < bestSqr) { bestSqr = sqr; best = list[i]; }
                }
            }
            return best;
        }
    }

    // 수정된 메시를 씀/재시작 후에도 유지되도록 에셋으로 저장. 원본은 건드리지 않음.
    private static Mesh SaveMeshAsset(Mesh mesh, string baseName)
    {
        if (mesh == null) return null;
        var dir = "Assets/VRMClothingFittedMeshes";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var safe = baseName;
        foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
        var path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + safe + ".asset");
        AssetDatabase.CreateAsset(mesh, path);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    // 좌/우 대칭 표기 정규화: .L/.R, _L/_R, Left/Right, 일본어 左/右 등을 통일 키로.
    private static string NormalizeBoneKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var n = name;
        // 접미사 패턴
        n = System.Text.RegularExpressions.Regex.Replace(n, "(?i)([._-]?)(left|l)$", "#LR#");
        n = System.Text.RegularExpressions.Regex.Replace(n, "(?i)([._-]?)(right|r)$", "#LR#");
        n = n.Replace("\u5de6", "#LR#").Replace("\u53f3", "#LR#");
        return n.ToLowerInvariant();
    }

    // 휴머노이드 Animator에서 HumanBodyBones -> Transform 맵 생성. 없으면 null.
    private static Dictionary<HumanBodyBones, Transform> BuildHumanoidBoneMap(GameObject root)
    {
        if (root == null) return null;
        var animators = root.GetComponentsInChildren<Animator>(true);
        foreach (var animator in animators)
        {
            if (animator == null) continue;
            if (!animator.isHuman) continue;
            if (animator.avatar == null) continue;

            var map = new Dictionary<HumanBodyBones, Transform>();
            for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var hbb = (HumanBodyBones)i;
                Transform t = null;
                try { t = animator.GetBoneTransform(hbb); }
                catch { t = null; }
                if (t != null) map[hbb] = t;
            }
            if (map.Count > 0) return map;
        }
        return null;
    }

    private static Transform PickBestMatch(Transform bone, List<Transform> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        Transform best = null;
        var bestDist = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == null) continue;
            var d = Vector3.Distance(bone.position, c.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
        return best;
    }

    private static int GetTransformDepth(Transform t)
    {
        var d = 0;
        var p = t;
        while (p != null) { d++; p = p.parent; }
        return d;
    }

    private static string GetHierarchyPath(Transform t)
    {
        if (t == null) return "";
        var stack = new Stack<string>();
        var p = t;
        while (p != null) { stack.Push(p.name); p = p.parent; }
        return string.Join("/", stack.ToArray());
    }

    private static Transform[] CollectSpringBoneOwners(Transform clothingRoot)
    {
        var result = new HashSet<Transform>();
        if (clothingRoot == null) return new Transform[0];

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

    private static Dictionary<string, List<Transform>> BuildTransformsByNameLookup(Transform root)
    {
        var dict = new Dictionary<string, List<Transform>>(StringComparer.Ordinal);
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == null) continue;
            // 정확한 이름과 좌/우 정규화 키 둘 다 등록해 매칭률을 높임
            AddLookup(dict, t.name, t);
            var key = NormalizeBoneKey(t.name);
            if (!string.Equals(key, t.name, StringComparison.Ordinal))
            {
                AddLookup(dict, key, t);
            }
        }
        return dict;
    }

    private static void AddLookup(Dictionary<string, List<Transform>> dict, string key, Transform t)
    {
        if (string.IsNullOrEmpty(key)) return;
        List<Transform> list;
        if (!dict.TryGetValue(key, out list))
        {
            list = new List<Transform>();
            dict.Add(key, list);
        }
        if (!list.Contains(t)) list.Add(t);
    }

    private static Transform[] CollectClothingBonesFromSmrs(SkinnedMeshRenderer[] clothingSmrs, Transform clothingRoot)
    {
        var set = new HashSet<Transform>();
        if (clothingSmrs == null) return new Transform[0];

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

    // 피팅된 옷 표시용 마커 이름 (숨겨진 자식 오브젝트로 표시, 런타임 컴포넌트 안 등록).
    private const string FitMarkerName = "__VRMClothingFitted__";

    private static bool HasFitMarker(GameObject root)
    {
        if (root == null) return false;
        return root.transform.Find(FitMarkerName) != null;
    }

    private static void AddFitMarker(GameObject root)
    {
        if (root == null) return;
        if (HasFitMarker(root)) return;
        var marker = new GameObject(FitMarkerName);
        marker.hideFlags = HideFlags.HideInHierarchy;
        Undo.RegisterCreatedObjectUndo(marker, "Add Fit Marker");
        Undo.SetTransformParent(marker.transform, root.transform, "Add Fit Marker");
        marker.transform.SetParent(root.transform, false);
    }

    private static string NormalizeSuffix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim().TrimEnd('_');
        if (string.IsNullOrEmpty(trimmed)) return "";
        if (!trimmed.StartsWith("_", StringComparison.Ordinal)) trimmed = "_" + trimmed;
        return trimmed;
    }
}
#endif
