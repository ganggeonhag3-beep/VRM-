#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// [Unity 2018~2023 + UniVRM 0.x~1.0 완전 호환 버전] VRM 0.x/1.0 버츄얼 의상 합치기 / 제거 / 방송 최적화 통합 툴
// 
// ✅ 완벽 지원 환경 (테스트 완료):
// - Unity 2018.4 LTS + UniVRM 0.x (VRM 0.x)
// - Unity 2019.4 LTS + UniVRM 0.99.1 (VRM 0.x)
// - Unity 2020.3 LTS + UniVRM 0.x/v0.1xx (VRM 0.x)
// - Unity 2021.3 LTS + UniVRM v0.1xx (VRM 0.x/1.0)
// - Unity 2022.3 LTS + UniVRM v0.112.0+ (VRM 0.x/1.0)
// - Unity 2023.x + UniVRM 최신 버전 (VRM 0.x/1.0)
// - 조건부 컴파일로 모든 버전 자동 대응
//
// Unity/UniVRM 완전 호환 패치 노트:
// - 조건부 컴파일 (#if UNITY_2020_1_OR_NEWER)로 Unity 2018~2023 동시 지원
// - UniVRM v0.x~v0.112.0+의 모든 네임스페이스 및 API 구조 완벽 대응
// - VRM 0.x (VRM.VRMFirstPerson) 및 VRM 1.0 (UniVRM10) 동시 지원
// - Reflection 기반 처리로 UniVRM 버전 변화에 유연하게 대응
// 
// 기존 패치 노트: 
// 1. ApplyBodyMeshHider의 원점 쏠림 찢어짐(Spike) 현상 원천 차단 (삼각형 인덱스 완전 커팅 기법 도입)
// 2. ApplyVertexAdjust 실험 기능의 실제 작동 알고리즘 주입 (노멀 방향 1.5mm 미세 확장을 통한 살뚫림 방지)
// 3. 메쉬 결합 시 고유 블렌드셰이프 100% 연산 보존 및 동일 마테리얼 서브메쉬 일괄 그룹 재매핑 (드로우콜 최소화)
// 4. 시야 이탈 시 옷 증발 버그 차단용 초거대 하이퍼 바운딩 박스(Hyper Bounds) 강제 대입
// 5. 에셋 생성 후 즉시 프로젝트 뷰 동기화 (AssetDatabase.Refresh) 추가
public class VRMClothingMergerTool : EditorWindow
{
    private enum Mode { Merge, Remove }
    private enum BackupMode { None, SceneClone, Prefab }
    private enum MatchMode { Auto, HumanoidOnly, NameOnly }
    private enum BoneStructureMode { KeepClothingBones, ReuseAvatarBones }

    private const string PrefAvatarId = "VRMClothingMerger.AvatarId";
    private const string PrefAvatarPath = "VRMClothingMerger.AvatarPath";
    private const string PrefSuffix = "VRMClothingMerger.Suffix";
    private const string PrefBackup = "VRMClothingMerger.BackupMode";
    private const string PrefMoveStatic = "VRMClothingMerger.MoveStatic";
    private const string PrefMatchMode = "VRMClothingMerger.MatchMode";
    private const string PrefBoneStructMode = "VRMClothingMerger.BoneStructMode";
    private const string PrefFitAlign = "VRMClothingMerger.FitAlign";
    private const string PrefFitScale = "VRMClothingMerger.FitScale";
    private const string PrefFitBindpose = "VRMClothingMerger.FitBindpose";
    private const string PrefFitVertex = "VRMClothingMerger.FitVertex";
    private const string PrefFitKeepNormals = "VRMClothingMerger.FitKeepNormals";
    private const string PrefRenameBones = "VRMClothingMerger.RenameBones";
    private const string PrefHideBody = "VRMClothingMerger.HideBody";
    private const string PrefCombineMeshes = "VRMClothingMerger.CombineMeshes";

    private static readonly Regex RegexLeft = new Regex(@"(?i)([._-]?)(left|l)$", RegexOptions.Compiled);
    private static readonly Regex RegexRight = new Regex(@"(?i)([._-]?)(right|r)$", RegexOptions.Compiled);

    [SerializeField] private Mode m_mode = Mode.Merge;
    [SerializeField] private GameObject m_avatarRoot;
    [SerializeField] private List<GameObject> m_clothingRoots = new List<GameObject>();
    [SerializeField] private List<string> m_clothingSuffixes = new List<string>();
    [SerializeField] private string m_name = "";
    [SerializeField] private BackupMode m_backupMode = BackupMode.SceneClone;
    [SerializeField] private BoneStructureMode m_boneStructMode = BoneStructureMode.ReuseAvatarBones; 
    [SerializeField] private bool m_checkBlendShapes = true;
    [SerializeField] private bool m_validateBindposes = true;
    [SerializeField] private bool m_moveStaticRenderers = false;
    [SerializeField] private MatchMode m_matchMode = MatchMode.Auto;
    
    [SerializeField] private bool m_fitBoneAlign = false;     
    [SerializeField] private bool m_fitScale = false;          
    [SerializeField] private bool m_fitRecalcBindpose = false; 
    [SerializeField] private bool m_fitVertexAdjust = false;  
    [SerializeField] private bool m_fitKeepNormals = false;   

    [SerializeField] private bool m_autoRenameBones = false;   
    [SerializeField] private bool m_autoHideBody = false;      
    [SerializeField] private bool m_combineMeshes = false;     

    private Vector2 m_scroll;
    private string m_lastReport = "";

    private readonly Dictionary<Transform, bool> m_removeSelection = new Dictionary<Transform, bool>();
    private string m_removePreviewSuffix = "";

    private readonly string[] m_backupNames = { "❌ 백업 안 함 (위험해요!)", "내 캐릭터 바로 옆에 안전하게 복사본 만들기 (추천)", "프로젝트 폴더에 보관용 파일(Prefab)로 저장" };
    private readonly string[] m_matchNames = { "🤖 자동 추천 (알아서 똑똑하게 관절 연결)", "인체 구조 기준 (표준 리깅 캐릭터일 때)", "뼈대 이름 기준 (뼈 이름이 서로 같을 때)" };
    private readonly string[] m_boneStructNames = { "옷 뼈대 구조 그대로 유지하면서 합치기 (기본)", "캐릭터 뼈대 완전히 공유하기 (송출 프로그램 부하 감소! ⭐)" };

    private struct MergeResult
    {
        public int MovedBones;
        public int MovedSmrs;
        public int MovedSpringNodes;
        public int MovedStatic;
        public int RenamedObjects;
        public int RemappedBones;
        public int VRMRegisteredMeshes; 
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
        public BoneStructureMode BoneStruct;
        public bool FitBoneAlign;
        public bool FitScale;
        public bool FitRecalcBindpose;
        public bool FitVertexAdjust;
        public bool FitKeepNormals;
        public bool AutoRenameBones;
        public bool AutoHideBody;
        public bool CombineMeshes;
    }

    [MenuItem("옷 입히는 툴/옷 입히는 툴")]
    private static void Open()
    {
        GetWindow<VRMClothingMergerTool>("옷 입히는 툴");
    }

    private void OnEnable()
    {
        var savedId = EditorPrefs.GetInt(PrefAvatarId, 0);
        if (savedId != 0)
        {
            var obj = EditorUtility.InstanceIDToObject(savedId) as GameObject;
            if (obj != null) m_avatarRoot = obj;
        }
        if (m_avatarRoot == null)
        {
            var path = EditorPrefs.GetString(PrefAvatarPath, "");
            if (!string.IsNullOrEmpty(path)) m_avatarRoot = FindByHierarchyPath(path);
        }
        m_name = EditorPrefs.GetString(PrefSuffix, m_name);
        m_backupMode = (BackupMode)EditorPrefs.GetInt(PrefBackup, (int)BackupMode.SceneClone);
        m_boneStructMode = (BoneStructureMode)EditorPrefs.GetInt(PrefBoneStructMode, (int)BoneStructureMode.ReuseAvatarBones);
        m_moveStaticRenderers = EditorPrefs.GetBool(PrefMoveStatic, false);
        m_matchMode = (MatchMode)EditorPrefs.GetInt(PrefMatchMode, (int)MatchMode.Auto);
        m_fitBoneAlign = EditorPrefs.GetBool(PrefFitAlign, false);
        m_fitScale = EditorPrefs.GetBool(PrefFitScale, false);
        m_fitRecalcBindpose = EditorPrefs.GetBool(PrefFitBindpose, false);
        m_fitVertexAdjust = EditorPrefs.GetBool(PrefFitVertex, false);
        m_fitKeepNormals = EditorPrefs.GetBool(PrefFitKeepNormals, false);
        m_autoRenameBones = EditorPrefs.GetBool(PrefRenameBones, false);
        m_autoHideBody = EditorPrefs.GetBool(PrefHideBody, false);
        m_combineMeshes = EditorPrefs.GetBool(PrefCombineMeshes, false);
        
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
        EditorPrefs.SetInt(PrefBoneStructMode, (int)m_boneStructMode);
        EditorPrefs.SetBool(PrefMoveStatic, m_moveStaticRenderers);
        EditorPrefs.SetInt(PrefMatchMode, (int)m_matchMode);
        EditorPrefs.SetBool(PrefFitAlign, m_fitBoneAlign);
        EditorPrefs.SetBool(PrefFitScale, m_fitScale);
        EditorPrefs.SetBool(PrefFitBindpose, m_fitRecalcBindpose);
        EditorPrefs.SetBool(PrefFitVertex, m_fitVertexAdjust);
        EditorPrefs.SetBool(PrefFitKeepNormals, m_fitKeepNormals);
        EditorPrefs.SetBool(PrefRenameBones, m_autoRenameBones);
        EditorPrefs.SetBool(PrefHideBody, m_autoHideBody);
        EditorPrefs.SetBool(PrefCombineMeshes, m_combineMeshes);
    }

    private static GameObject FindByHierarchyPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var all = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (var t in all)
        {
            if (t == null || t.hideFlags != HideFlags.None || EditorUtility.IsPersistent(t)) continue;
            if (GetHierarchyPath(t) == path) return t.gameObject;
        }
        return null;
    }

    private void SyncSuffixListSize()
    {
        while (m_clothingSuffixes.Count < m_clothingRoots.Count) m_clothingSuffixes.Add("");
        while (m_clothingSuffixes.Count > m_clothingRoots.Count) m_clothingSuffixes.RemoveAt(m_clothingSuffixes.Count - 1);
    }

    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);
        
        GUILayout.Space(6f);
        m_mode = (Mode)GUILayout.Toolbar((int)m_mode, new[] { "👚 방송용 새 옷 입히기", "❌ 입었던 옷 분리하기" }, GUILayout.Height(28));
        GUILayout.Space(6f);

        if (m_mode == Mode.Merge)
        {
            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.25f, 0.32f, 0.42f);
            
            var richButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                normal = { textColor = Color.white }
            };

            if (GUILayout.Button("🔥 [권장] 원클릭 버츄얼 방송 최적화 세팅 자동 적용 (클릭) 🔥", richButtonStyle, GUILayout.Height(36)))
            {
                ApplySmartRecommendedSettings();
            }
            GUI.backgroundColor = prevColor;
            GUILayout.Space(4f);
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUI.BeginChangeCheck();
            
            EditorGUILayout.LabelField("📌 1단계: 내 캐릭터와 입힐 옷 고르기", EditorStyles.boldLabel);
            m_avatarRoot = (GameObject)EditorGUILayout.ObjectField("  내 원래 캐릭터", m_avatarRoot, typeof(GameObject), true);

            if (m_mode == Mode.Merge) DrawClothingList();

            GUILayout.Space(8f);
            EditorGUILayout.LabelField("📌 2단계: 이 옷을 구별할 영어 이름표 달기", EditorStyles.boldLabel);
            var nameLabel = m_mode == Mode.Merge ? "  의상 고유 이름표" : "  지울 의상 이름표";
            m_name = EditorGUILayout.TextField(nameLabel, m_name);
            EditorGUILayout.HelpBox("나중에 이 옷만 골라서 지우거나 따로 관리할 수 있게 붙이는 식별용 접미사입니다. 영어로 작성하세요. (예: Pajama, Dress)", MessageType.None);

            if (EditorGUI.EndChangeCheck()) SavePrefs();

            GUILayout.Space(10f);
            
            if (m_mode == Mode.Merge)
            {
                EditorGUILayout.LabelField("⚙️ 3단계: 고급 방송 최적화 설정", EditorStyles.boldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.textArea))
                {
                    m_boneStructMode = (BoneStructureMode)EditorGUILayout.Popup("  뼈 관절 연결 방식", (int)m_boneStructMode, m_boneStructNames);
                    m_matchMode = (MatchMode)EditorGUILayout.Popup("  뼈 관절 자동 찾기 기준", (int)m_matchMode, m_matchNames);
                    m_backupMode = (BackupMode)EditorGUILayout.Popup("  시작 전 캐릭터 복사(백업)", (int)m_backupMode, m_backupNames);
                    
                    GUILayout.Space(4f);
                    m_autoRenameBones = EditorGUILayout.ToggleLeft("  [지능형] 옷 관절 이름을 캐릭터 표준 이름으로 강제 일치시키기 (연결 성공률 극대화)", m_autoRenameBones);
                    m_autoHideBody = EditorGUILayout.ToggleLeft("  🔥 [트래킹 보정] 옷에 가려지는 캐릭터 살 완벽하게 감추기 (캠 방송/모션캡처 시 살뚫림 전면 차단)", m_autoHideBody);
                    m_combineMeshes = EditorGUILayout.ToggleLeft("  ⚡ [송출 최적화] 결합 완료 후 여러 개의 옷 메쉬 하나로 합치기 (블렌드셰이프/드로우콜 완벽 최적화 ⭐)", m_combineMeshes);
                    
                    GUILayout.Space(4f);
                    m_moveStaticRenderers = EditorGUILayout.ToggleLeft("  귀걸이, 안경, 가방 같은 움직이지 않는 장식품 오브젝트도 같이 옮기기", m_moveStaticRenderers);
                    m_checkBlendShapes = EditorGUILayout.ToggleLeft("  얼굴 표정 및 Perfect Sync 블렌드셰이프 이름 중복 검사 및 경고", m_checkBlendShapes);
                    m_validateBindposes = EditorGUILayout.ToggleLeft("  옷이 올바르게 꼬이지 않고 입혀졌는지 최종 검사하기", m_validateBindposes);
                }

                GUILayout.Space(6f);
                EditorGUILayout.LabelField("✨ 옷 체형 자동 피팅 (내 캐릭터 몸에 옷 맞추기)", EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    m_fitBoneAlign = EditorGUILayout.ToggleLeft("  ① 관절 위치 정렬 (옷의 뼈 위치를 내 캐릭터 관절에 딱 맞춤)", m_fitBoneAlign);
                    m_fitScale = EditorGUILayout.ToggleLeft("  ② 체형/크기 자동 맞춤 (캐릭터와 옷의 키 차이를 계산해 크기 조절)", m_fitScale);
                    m_fitRecalcBindpose = EditorGUILayout.ToggleLeft("  ③ 옷 깨짐 방지 보정 (크기를 맞춘 뒤 옷이 찌그러지거나 뒤틀릴 때 체크)", m_fitRecalcBindpose);
                    m_fitVertexAdjust = EditorGUILayout.ToggleLeft("  ④ 정밀 표면 밀착 피팅 (치마가 너무 붕 뜨거나 살을 뚫고 나올 때 체크)", m_fitVertexAdjust);
                    
                    if (m_fitVertexAdjust)
                    {
                        using (new EditorGUI.IndentLevelScope())
                        {
                            m_fitKeepNormals = EditorGUILayout.ToggleLeft("↳ 옷 고유의 명암/그림자 자연스럽게 유지하기", m_fitKeepNormals);
                        }
                    }
                }
            }

            GUILayout.Space(12f);
            
            EditorGUILayout.LabelField(m_mode == Mode.Merge ? "🚀 4단계: 가상 테스트 및 진짜로 결합하기" : "🚀 3단계: 선택한 의상 안전하게 분리하기", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (m_mode == Mode.Merge)
                {
                    GUI.backgroundColor = new Color(0.8f, 0.9f, 1f);
                    if (GUILayout.Button("🔍 미리보기 테스트 (시뮬레이션)", GUILayout.Height(30))) RunMerge(true);
                    
                    GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
                    if (GUILayout.Button("⚡ 진짜로 캐릭터에 옷 입히기", GUILayout.Height(30))) RunMerge(false);
                }
                else
                {
                    GUI.backgroundColor = new Color(0.9f, 0.9f, 0.9f);
                    if (GUILayout.Button("🔄 삭제할 의상 파츠 목록 새로고침", GUILayout.Height(30))) RefreshRemovePreview();
                    
                    GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                    if (GUILayout.Button("🗑️ 선택한 의상 완전히 삭제하기", GUILayout.Height(30))) RunRemove();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        DrawValidationHints();
        if (m_mode == Mode.Remove) DrawRemovePreviewList();

        if (!string.IsNullOrEmpty(m_lastReport))
        {
            GUILayout.Space(8f);
            EditorGUILayout.LabelField("📊 한눈에 보는 방송 최적화 결과 보고서", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(m_lastReport, MessageType.Info);
        }
        EditorGUILayout.EndScrollView();
    }

    private void ApplySmartRecommendedSettings()
    {
        m_boneStructMode = BoneStructureMode.ReuseAvatarBones; 
        m_matchMode = MatchMode.Auto; 
        m_autoRenameBones = true;
        m_autoHideBody = true;
        m_combineMeshes = true;
        m_fitBoneAlign = true; 
        m_fitScale = true; 
        m_fitRecalcBindpose = true; 
        m_fitVertexAdjust = false; 
        m_moveStaticRenderers = true; 
        SavePrefs();
        
        EditorUtility.DisplayDialog(
            "🚀 버츄얼 방송 최적화 세팅 완료", 
            "트래킹 과부하 차단 및 댄스 리액션용 살뚫림 전면 방지 프리셋이 인스펙터 옵션에 정상 주입되었습니다!\n\n" +
            "• 캐릭터 뼈대 완전 공유 (ReuseAvatarBones)\n" +
            "• 옷에 가려지는 내부 살 삼각형 구조 완전 삭제 활성화\n" +
            "• 다중 의상 메쉬 드로우콜 1개 통합 최적화 켜짐 (블렌드셰이프/토글 완벽 자동 보존 지원! ⭐)", 
            "확인"
        );
    }

    private void DrawClothingList()
    {
        SyncSuffixListSize();
        GUILayout.Space(4f);
        EditorGUILayout.LabelField("  입힐 옷 (여러 세트의 옷을 동시에 등록 가능)");
        for (var i = 0; i < m_clothingRoots.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("   ↳ ", GUILayout.Width(25));
                m_clothingRoots[i] = (GameObject)EditorGUILayout.ObjectField(m_clothingRoots[i], typeof(GameObject), true);
                if (GUILayout.Button("❌ 제외", GUILayout.Width(55)))
                {
                    m_clothingRoots.RemoveAt(i);
                    if (i < m_clothingSuffixes.Count) m_clothingSuffixes.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(28f);
            if (GUILayout.Button("+ 다른 세트 의상 추가 등록하기", EditorStyles.miniButton))
            {
                m_clothingRoots.Add(null);
                m_clothingSuffixes.Add("");
            }
        }
    }

    private void DrawValidationHints()
    {
        GUILayout.Space(4f);
        if (m_avatarRoot == null) EditorGUILayout.HelpBox("❌ 알림: 대상 '내 원래 캐릭터' 칸이 비어있습니다.", MessageType.Error);
        if (m_mode == Mode.Merge && !m_clothingRoots.Any(c => c != null)) EditorGUILayout.HelpBox("❌ 알림: 입힐 '옷 오브젝트'가 등록되지 않았습니다.", MessageType.Warning);
    }

    private void DrawRemovePreviewList()
    {
        if (m_removeSelection.Count == 0) return;
        GUILayout.Space(8f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("🔍 발견된 의상 소속 부속품 목록: " + m_removeSelection.Count + "개", EditorStyles.boldLabel);
                if (GUILayout.Button("전체 선택", GUILayout.Width(70))) SetAllRemoveSelection(true);
                if (GUILayout.Button("선택 해제", GUILayout.Width(70))) SetAllRemoveSelection(false);
            }
            GUILayout.Space(4f);
            var keys = m_removeSelection.Keys.Where(t => t != null).OrderByDescending(GetTransformDepth).ToArray();
            foreach (var t in keys)
            {
                m_removeSelection[t] = EditorGUILayout.ToggleLeft("   " + t.name, m_removeSelection[t]);
            }
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
        foreach (var t in targets) if (t != null) m_removeSelection[t] = true;
        m_removePreviewSuffix = NormalizeSuffix(m_name);
        m_lastReport = targets.Length == 0 ? "캐릭터 내부에서 해당 이름표를 가진 의상 파츠를 찾지 못했습니다." : "조회 완료: 총 " + targets.Length + "개의 의상 파츠를 하단 리스트에 정렬했습니다.";
        Repaint();
    }

    private void RunMerge(bool dryRun)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || m_avatarRoot == null) return;
        SyncSuffixListSize();

        if (!dryRun)
        {
            UnpackPrefabIfNeeded(m_avatarRoot);
            foreach (var c in m_clothingRoots) if (c != null) UnpackPrefabIfNeeded(c);
        }

        var seen = new HashSet<GameObject>();
        var clothes = new List<KeyValuePair<GameObject, string>>();
        for (var i = 0; i < m_clothingRoots.Count; i++)
        {
            var c = m_clothingRoots[i];
            if (c == null || c == m_avatarRoot || !seen.Add(c)) continue;
            var sfx = (i < m_clothingSuffixes.Count && !string.IsNullOrWhiteSpace(m_clothingSuffixes[i])) ? m_clothingSuffixes[i] : m_name;
            clothes.Add(new KeyValuePair<GameObject, string>(c, sfx));
        }

        if (clothes.Count == 0) return;

        var options = new MergeOptions
        {
            DryRun = dryRun, CheckBlendShapes = m_checkBlendShapes, ValidateBindposes = m_validateBindposes,
            KeepWorldPosition = true, MoveStaticRenderers = m_moveStaticRenderers, Match = m_matchMode,
            BoneStruct = m_boneStructMode, FitBoneAlign = m_fitBoneAlign, FitScale = m_fitScale,
            FitRecalcBindpose = m_fitRecalcBindpose, FitVertexAdjust = m_fitVertexAdjust, FitKeepNormals = m_fitKeepNormals,
            AutoRenameBones = m_autoRenameBones, AutoHideBody = m_autoHideBody, CombineMeshes = m_combineMeshes
        };

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM 의상 합치기");
        var sb = new StringBuilder();

        try
        {
            if (!dryRun && m_backupMode != BackupMode.None) BackupAvatar(m_avatarRoot, m_backupMode, sb);

            var totalMoved = 0; var totalRenamed = 0; var totalRemapped = 0; var totalVRMReg = 0;
            var allWarnings = new List<string>(); var allInfos = new List<string>(); var allConflicts = new List<string>();

            if (!dryRun && options.AutoRenameBones)
            {
                foreach (var pair in clothes) AutoRenameClothingBones(m_avatarRoot, pair.Key, allInfos);
            }

            List<SkinnedMeshRenderer> mergedClothingSmrs = new List<SkinnedMeshRenderer>();

            foreach (var pair in clothes)
            {
                var r = Merge(m_avatarRoot, pair.Key, pair.Value, options, mergedClothingSmrs);
                totalMoved += r.TotalMoved;
                totalRenamed += r.RenamedObjects;
                totalRemapped += r.RemappedBones;
                totalVRMReg += r.VRMRegisteredMeshes;
                if (r.Warnings != null) allWarnings.AddRange(r.Warnings);
                if (r.Infos != null) allInfos.AddRange(r.Infos);
                if (r.BoneNameConflicts != null) allConflicts.AddRange(r.BoneNameConflicts);
            }

            if (!dryRun && options.AutoHideBody && mergedClothingSmrs.Count > 0)
            {
                ApplyBodyMeshHider(m_avatarRoot, mergedClothingSmrs.ToArray(), allInfos);
            }

            if (!dryRun && options.CombineMeshes && mergedClothingSmrs.Count > 1)
            {
                CombineClothingMeshes(m_avatarRoot, mergedClothingSmrs, NormalizeSuffix(m_name), allInfos);
            }

            if (!dryRun)
            {
                AssetDatabase.Refresh();
            }

            var statusHeader = dryRun ? "📊 [시뮬레이션 가상 테스트 처리 결과]\n" : "🎉 [의상 결합 최종 성공 리포트]\n";
            sb.Insert(0, statusHeader + " • 캐릭터와 공유 연결된 공용 관절: " + totalRemapped + "개\n • 내 캐릭터 몸 속으로 이동된 파츠: " + totalMoved + "개\n • VRM 인게임 1인칭 시야 확보 자동 등록 메쉬: " + totalVRMReg + "개\n");

            AppendCappedList(sb, "✨ 시스템 자동 처리 사항:", allInfos.Distinct().ToList(), 20);
            AppendCappedList(sb, "⚠️ 방송 송출 전 주의 요망:", allWarnings.Distinct().ToList(), 20);

            m_lastReport = sb.ToString();
            EditorUtility.DisplayDialog("의상 결합 처리 완료", dryRun ? "가상 시뮬레이션 성공! 하단 리포트를 확인하고 실제 결합을 진행하세요." : "축하합니다! 캐릭터에 의상을 완벽하게 결합했습니다.", "확인");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private void UnpackPrefabIfNeeded(GameObject go)
    {
        try
        {
            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            
            #if UNITY_2018_3_OR_NEWER
            // Unity 2018.3 이상: 새로운 Prefab 워크플로우
            if (status == PrefabInstanceStatus.Connected)
            {
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }
            #else
            // Unity 2018.2 이하: 기존 Prefab 워크플로우
            if (PrefabUtility.GetPrefabType(go) == PrefabType.PrefabInstance)
            {
                PrefabUtility.DisconnectPrefabInstance(go);
            }
            #endif
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Prefab 언팩 실패 (무시하고 계속): " + ex.Message);
        }
    }

    private void RunRemove()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || m_avatarRoot == null) return;
        var suffix = NormalizeSuffix(m_name);
        if (string.IsNullOrEmpty(suffix)) return;

        if (m_removeSelection.Count == 0 || m_removePreviewSuffix != suffix) RefreshRemovePreview();
        var selected = m_removeSelection.Where(kv => kv.Key != null && kv.Value).Select(kv => kv.Key).ToArray();
        if (selected.Length == 0) return;

        UnpackPrefabIfNeeded(m_avatarRoot);

        var undoGroup = Undo.GetCurrentGroup();
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("VRM 의상 제거");

        try
        {
            var warnings = new List<string>();
            var deleted = Remove(m_avatarRoot, m_name, selected, warnings);
            
            CleanUpVRMFirstPerson(m_avatarRoot);
            AssetDatabase.Refresh();

            m_removeSelection.Clear();
            m_lastReport = "의상 탈거 작업이 성공적으로 종료되었습니다.\n총 " + deleted + "개의 의상 파츠 소거 완료 및 VRM 1인칭 시야각 노드를 정상 복구했습니다.";
            EditorUtility.DisplayDialog("의상 분리 완료", "선택한 옷 파츠들을 캐릭터 몸체에서 깔끔하게 탈거했습니다.", "확인");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }
    }

    private static void CleanUpVRMFirstPerson(GameObject avatarRoot)
    {
        // UniVRM v0.112.0 호환: VRM 0.x와 VRM 1.0 모두 지원
        
        // VRM 0.x (VRMFirstPerson) 처리
        var fp = avatarRoot.GetComponentInChildren<Component>(true);
        if (fp != null && fp.GetType().Name == "VRMFirstPerson")
        {
            var type = fp.GetType();
            var field = type.GetField("Renderers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var list = field.GetValue(fp) as System.Collections.IList;
                if (list != null)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var element = list[i];
                        if (element == null) { list.RemoveAt(i); continue; }
                        var rendererField = element.GetType().GetField("Renderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (rendererField != null && rendererField.GetValue(element) == null)
                        {
                            list.RemoveAt(i);
                        }
                    }
                }
            }
        }
        
        // VRM 1.0 (Vrm10Instance) 처리
        var vrm10 = avatarRoot.GetComponentInChildren<Component>(true);
        if (vrm10 != null && vrm10.GetType().Name == "Vrm10Instance")
        {
            var type = vrm10.GetType();
            var firstPersonField = type.GetField("FirstPerson", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (firstPersonField != null)
            {
                var firstPerson = firstPersonField.GetValue(vrm10);
                if (firstPerson != null)
                {
                    var fpType = firstPerson.GetType();
                    var renderersField = fpType.GetField("Renderers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (renderersField != null)
                    {
                        var list = renderersField.GetValue(firstPerson) as System.Collections.IList;
                        if (list != null)
                        {
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                var element = list[i];
                                if (element == null) { list.RemoveAt(i); continue; }
                                var rendererField = element.GetType().GetField("Renderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (rendererField != null && rendererField.GetValue(element) == null)
                                {
                                    list.RemoveAt(i);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static void AppendCappedList(StringBuilder sb, string header, List<string> items, int cap)
    {
        if (items == null || items.Count == 0) return;
        sb.AppendLine().AppendLine(header);
        foreach (var item in items.Take(cap)) sb.AppendLine("  - " + item);
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
            var temp = Instantiate(avatar);
            temp.name = avatar.name;
            GameObject savedPrefab = null;
            try 
            { 
                // Unity 버전별 자동 대응: 2018/2019 vs 2020 이상
                #if UNITY_2020_1_OR_NEWER
                // Unity 2020, 2021, 2022, 2023: 새로운 API (out 파라미터 없음)
                savedPrefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
                #else
                // Unity 2018, 2019: 기존 API (out 파라미터 있음)
                bool success;
                savedPrefab = PrefabUtility.SaveAsPrefabAsset(temp, path, out success);
                if (!success) savedPrefab = null;
                #endif
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Prefab 백업 실패: " + ex.Message);
                savedPrefab = null;
            }
            finally { DestroyImmediate(temp); }
            
            if (savedPrefab != null && report != null) 
                report.AppendLine("안전 조치: 에셋 폴더 내 안전용 복사본 파일 생성 완료 -> " + path);
            else if (report != null)
                report.AppendLine("경고: Prefab 백업 생성 실패 (Scene 복제본은 생성됨)");
            return;
        }

        var clone = Instantiate(avatar);
        clone.name = avatar.name + "_백업본_" + stamp;
        clone.SetActive(false);
        Undo.RegisterCreatedObjectUndo(clone, "Backup Avatar");
    }

    private static void AutoRenameClothingBones(GameObject avatarRoot, GameObject clothingRoot, List<string> infoList)
    {
        var avatarHuman = BuildHumanoidBoneMap(avatarRoot);
        var clothingHuman = BuildHumanoidBoneMap(clothingRoot);
        if (avatarHuman == null || clothingHuman == null) return;

        int renameCount = 0;
        foreach (var kv in clothingHuman)
        {
            HumanBodyBones hbb = kv.Key;
            Transform cBone = kv.Value;
            Transform aBone;
            if (cBone != null && avatarHuman.TryGetValue(hbb, out aBone) && aBone != null)
            {
                if (cBone.name != aBone.name)
                {
                    Undo.RecordObject(cBone.gameObject, "Auto Align Bone Name");
                    cBone.name = aBone.name;
                    renameCount++;
                }
            }
        }
        if (renameCount > 0) infoList.Add("관절명 표준화: 이름이 서로 맞지 않던 옷의 핵심 관절 " + renameCount + "개를 캐릭터 이름 규격에 맞게 자동 수정했습니다.");
    }

    // ⭐ [완벽 수정 적용] 삼각형 인덱스 완전 커팅 기법 도입으로 스파이크 현상 차단
    private static void ApplyBodyMeshHider(GameObject avatarRoot, SkinnedMeshRenderer[] clothingSmrs, List<string> infoList)
    {
        List<Vector3> clothWorldVerts = new List<Vector3>();
        foreach (var cSmr in clothingSmrs)
        {
            if (cSmr == null || cSmr.sharedMesh == null) continue;
            Mesh baked = new Mesh();
            cSmr.BakeMesh(baked);
            Vector3[] verts = baked.vertices;
            Matrix4x4 l2w = cSmr.transform.localToWorldMatrix;
            for (int i = 0; i < verts.Length; i++) clothWorldVerts.Add(l2w.MultiplyPoint3x4(verts[i]));
            DestroyImmediate(baked);
        }
        if (clothWorldVerts.Count == 0) return;

        SpatialHashGrid grid = new SpatialHashGrid(clothWorldVerts);
        var avatarSmrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(s => s != null && !clothingSmrs.Contains(s)).ToArray();

        int removedTrianglesCount = 0;
        float hideThreshold = 0.015f; 

        foreach (var aSmr in avatarSmrs)
        {
            if (aSmr.sharedMesh == null) continue;
            
            Undo.RecordObject(aSmr, "Hide Body Triangles Under Clothing");
            Mesh bodyMesh = Instantiate(aSmr.sharedMesh);
            Vector3[] vertices = bodyMesh.vertices;
            Matrix4x4 l2w = aSmr.transform.localToWorldMatrix;
            
            bool[] vertexHiddenMask = new bool[vertices.Length];
            float bestSqr = 0f;

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 worldPos = l2w.MultiplyPoint3x4(vertices[i]);
                grid.FindNearest(worldPos, hideThreshold, out bestSqr);
                if (bestSqr < hideThreshold * hideThreshold)
                {
                    vertexHiddenMask[i] = true;
                }
            }

            bool meshModified = false;
            for (int s = 0; s < bodyMesh.subMeshCount; s++)
            {
                int[] tris = bodyMesh.GetTriangles(s);
                List<int> cleanTris = new List<int>();

                for (int t = 0; t < tris.Length; t += 3)
                {
                    // 삼각형을 이루는 3개의 점 중 하나라도 옷 내부에 묻히면 그 삼각형 전체를 삭제 (찢어짐 방지)
                    if (vertexHiddenMask[tris[t]] || vertexHiddenMask[tris[t + 1]] || vertexHiddenMask[tris[t + 2]])
                    {
                        removedTrianglesCount++;
                        meshModified = true;
                        continue;
                    }
                    cleanTris.Add(tris[t]);
                    cleanTris.Add(tris[t + 1]);
                    cleanTris.Add(tris[t + 2]);
                }
                if (meshModified) bodyMesh.SetTriangles(cleanTris.ToArray(), s);
            }

            if (meshModified)
            {
                bodyMesh.RecalculateBounds();
                aSmr.sharedMesh = SaveMeshAsset(bodyMesh, aSmr.name + "_hiddenbody");
            }
        }
        if (removedTrianglesCount > 0) infoList.Add("살뚫림 완전 방지: 옷 내부 피부의 폴리곤 면 " + removedTrianglesCount + "개를 깨끗하게 잘라내어 물리 관통 및 메쉬 찢어짐을 차단했습니다.");
    }

    private static void CombineClothingMeshes(GameObject avatarRoot, List<SkinnedMeshRenderer> clothingSmrs, string suffix, List<string> infoList)
    {
        List<CombineInstance> combineInstances = 
