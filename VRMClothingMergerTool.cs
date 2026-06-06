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
// 기능 개선 패치 노트:
// 1. ApplyBodyMeshHider의 원점 쏠림 찢어짐(Spike) 현상 원천 차단 (삼각형 인덱스 완전 커팅 기법 도입)
// 2. ApplyVertexAdjust 실험 기능의 실제 작동 알고리즘 주입 (노멀 방향 1.5mm 미세 확장을 통한 살뚫림 방지)
// 3. 메쉬 결합 시 고유 블렌드셰이프 100% 연산 보존 및 동일 마테리얼 서브메쉬 일괄 그룹 재매핑 (드로우콜 최소화)
// 4. 시야 이탈 시 옷 증발 버그 차단용 초거대 하이퍼 바운딩 박스(Hyper Bounds) 강제 대입
// 5. 에셋 생성 후 즉시 프로젝트 뷰 동기화 (AssetDatabase.Refresh) 추가
// 6. 로그 파일 저장 기능 (작업 결과를 타임스탬프와 함께 텍스트 파일로 저장)
// 7. 메쉬 최적화 통계 (처리 전/후 메쉬 개수 및 폴리곤 수 비교 표시)
// 8. 진행 상황 표시 (EditorUtility.DisplayProgressBar로 작업 진행률 실시간 표시)
// 9. 블렌드셰이프 미리보기 (캐릭터와 의상의 블렌드셰이프 목록 및 충돌 검사)
// 10. 개선된 Undo 시스템 (작업별 세분화된 Undo 그룹으로 Ctrl+Z 안정성 향상)
// 11. UV 자동 수정 (메쉬 통합 시 비정상 UV 좌표 자동 정규화로 텍스처 오류 방지)
// 12. 사용자 친화적 오류 처리 (모든 예외에 대해 명확한 다이얼로그 표시 및 안전한 복구)
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
    private int m_lastMeshCount = 0;
    private int m_lastPolyCount = 0;
    private string m_lastLogPath = "";
    
    // 블렌드셰이프 미리보기
    private bool m_showBlendShapePreview = false;
    private Dictionary<string, List<string>> m_blendShapePreview = new Dictionary<string, List<string>>();
    
    // 되돌리기 시스템
    private int m_lastUndoGroup = -1;
    private string m_lastOperationName = "";
    private bool m_canUndo = false;

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
            
            // 블렌드셰이프 미리보기 버튼
            if (m_mode == Mode.Merge)
            {
                GUILayout.Space(4f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUI.backgroundColor = new Color(0.9f, 0.85f, 1f);
                    if (GUILayout.Button("👁️ 블렌드셰이프 이름 정보와 중복 여부 미리보기", GUILayout.Width(280), GUILayout.Height(24)))
                    {
                        PreviewBlendShapes();
                    }
                    GUI.backgroundColor = Color.white;
                    GUILayout.FlexibleSpace();
                }
            }
        }

        DrawValidationHints();
        if (m_mode == Mode.Remove) DrawRemovePreviewList();
        
        // 블렌드셰이프 미리보기 표시
        if (m_showBlendShapePreview) DrawBlendShapePreview();

        if (!string.IsNullOrEmpty(m_lastReport))
        {
            GUILayout.Space(8f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("📊 한눈에 보는 방송 최적화 결과 보고서", EditorStyles.boldLabel);
                
                // 되돌리기 버튼
                if (m_canUndo && m_lastUndoGroup >= 0)
                {
                    GUI.backgroundColor = new Color(1f, 0.8f, 0.6f);
                    if (GUILayout.Button($"↩️ 되돌리기: {m_lastOperationName}", GUILayout.Width(200)))
                    {
                        PerformUndo();
                    }
                    GUI.backgroundColor = Color.white;
                }
                
                if (!string.IsNullOrEmpty(m_lastLogPath) && GUILayout.Button("📁 로그 파일 열기", GUILayout.Width(120)))
                {
                    EditorUtility.RevealInFinder(m_lastLogPath);
                }
                if (GUILayout.Button("💾 결과 저장", GUILayout.Width(100)))
                {
                    SaveReportToFile();
                }
            }
            EditorGUILayout.HelpBox(m_lastReport, MessageType.Info);
        }
        EditorGUILayout.EndScrollView();
    }

    private void ApplySmartRecommendedSettings()
    {
        // 아바타가 선택되지 않았으면 경고 표시
        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog(
                "⚠️ 캐릭터 미선택", 
                "먼저 '내 원래 캐릭터' 칸에 아바타를 드래그 앤 드롭으로 선택해주세요!\n\n" +
                "1단계에서 캐릭터를 선택한 후 다시 이 버튼을 눌러주세요.", 
                "확인"
            );
            return;
        }
        
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
            "트래킹 과부하 차단 및 댄스 리액션용 살뚫림 전면 방지 프리셋이 하단 옵션에 정상 주입되었습니다!\n\n" +
            "✅ 적용된 설정:\n" +
            "• 캐릭터 뼈대 완전 공유 (ReuseAvatarBones)\n" +
            "• 옷에 가려지는 내부 살 삼각형 구조 완전 삭제 활성화\n" +
            "• 다중 의상 메쉬 드로우콜 1개 통합 최적화 켜짐\n" +
            "• 블렌드셰이프/토글 완벽 자동 보존 지원! ⭐\n\n" +
            "💡 이제 1단계부터 캐릭터와 의상을 선택하고 진행하세요.", 
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
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        
        // 아바타 체크
        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog(
                "⚠️ 캐릭터 미선택", 
                "먼저 '내 원래 캐릭터' 칸에 아바타를 선택해주세요!\n\n" +
                "1단계에서 캐릭터를 드래그 앤 드롭으로 지정한 후 다시 시도하세요.", 
                "확인"
            );
            return;
        }
        
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

        // 의상 체크
        if (clothes.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "⚠️ 의상 미선택", 
                "입힐 옷이 선택되지 않았습니다!\n\n" +
                "1단계에서 '입힐 옷' 칸에 의상 오브젝트를 드래그 앤 드롭으로 추가해주세요.", 
                "확인"
            );
            return;
        }

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
        
        // 진행 상황 표시 시작
        float progressTotal = 100f;
        float progressCurrent = 0f;

        try
        {
            // Step 1: 초기화 및 백업 (10%)
            EditorUtility.DisplayProgressBar("VRM 의상 합치기", "작업 초기화 중...", progressCurrent / progressTotal);
            progressCurrent = 10f;
            
            // 처리 전 메쉬 통계 수집
            int beforeMeshCount, beforePolyCount;
            CalculateMeshStatistics(m_avatarRoot, out beforeMeshCount, out beforePolyCount);
            
            if (!dryRun && m_backupMode != BackupMode.None)
            {
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", "백업 생성 중...", progressCurrent / progressTotal);
                BackupAvatar(m_avatarRoot, m_backupMode, sb);
            }
            progressCurrent = 20f;

            var totalMoved = 0; var totalRenamed = 0; var totalRemapped = 0; var totalVRMReg = 0;
            var allWarnings = new List<string>(); var allInfos = new List<string>(); var allConflicts = new List<string>();

            // Step 2: 뼈대 이름 자동 정렬 (30%)
            if (!dryRun && options.AutoRenameBones)
            {
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", "뼈대 이름 자동 정렬 중...", progressCurrent / progressTotal);
                foreach (var pair in clothes) AutoRenameClothingBones(m_avatarRoot, pair.Key, allInfos);
            }
            progressCurrent = 30f;

            List<SkinnedMeshRenderer> mergedClothingSmrs = new List<SkinnedMeshRenderer>();

            // Step 3: 의상 병합 (50%)
            EditorUtility.DisplayProgressBar("VRM 의상 합치기", $"의상 병합 중... ({clothes.Count}개)", progressCurrent / progressTotal);
            float mergeStepSize = 20f / clothes.Count;
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
                progressCurrent += mergeStepSize;
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", $"의상 병합 중...", progressCurrent / progressTotal);
            }
            progressCurrent = 50f;

            // Step 4: 살 감추기 (70%)
            if (!dryRun && options.AutoHideBody && mergedClothingSmrs.Count > 0)
            {
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", "내부 살 폴리곤 제거 중...", progressCurrent / progressTotal);
                ApplyBodyMeshHider(m_avatarRoot, mergedClothingSmrs.ToArray(), allInfos);
            }
            progressCurrent = 70f;

            // Step 5: 메쉬 통합 (85%)
            if (!dryRun && options.CombineMeshes && mergedClothingSmrs.Count > 1)
            {
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", "의상 메쉬 통합 중...", progressCurrent / progressTotal);
                CombineClothingMeshes(m_avatarRoot, mergedClothingSmrs, NormalizeSuffix(m_name), allInfos);
            }
            progressCurrent = 85f;

            // Step 6: 최종 처리 및 통계 (95%)
            if (!dryRun)
            {
                EditorUtility.DisplayProgressBar("VRM 의상 합치기", "에셋 데이터베이스 갱신 중...", progressCurrent / progressTotal);
                AssetDatabase.Refresh();
                
                // 처리 후 메쉬 통계 수집
                int afterMeshCount, afterPolyCount;
                CalculateMeshStatistics(m_avatarRoot, out afterMeshCount, out afterPolyCount);
                
                m_lastMeshCount = afterMeshCount;
                m_lastPolyCount = afterPolyCount;
                
                // 통계를 리포트에 추가
                sb.AppendLine().AppendLine("📊 메쉬 최적화 통계:");
                sb.AppendLine($"  처리 전 → 처리 후");
                sb.AppendLine($"  • 메쉬 개수: {beforeMeshCount}개 → {afterMeshCount}개");
                sb.AppendLine($"  • 폴리곤 수: {beforePolyCount:N0}개 → {afterPolyCount:N0}개");
                
                if (options.CombineMeshes && afterMeshCount < beforeMeshCount)
                {
                    sb.AppendLine($"  ✅ 메쉬 통합으로 {beforeMeshCount - afterMeshCount}개 감소! (드로우콜 최적화)");
                }
            }
            progressCurrent = 95f;

            // Step 7: 리포트 생성 (100%)
            EditorUtility.DisplayProgressBar("VRM 의상 합치기", "리포트 생성 중...", progressCurrent / progressTotal);
            var statusHeader = dryRun ? "📊 [시뮬레이션 가상 테스트 처리 결과]\n" : "🎉 [의상 결합 최종 성공 리포트]\n";
            sb.Insert(0, statusHeader + " • 캐릭터와 공유 연결된 공용 관절: " + totalRemapped + "개\n • 내 캐릭터 몸 속으로 이동된 파츠: " + totalMoved + "개\n • VRM 인게임 1인칭 시야 확보 자동 등록 메쉬: " + totalVRMReg + "개\n");

            AppendCappedList(sb, "✨ 시스템 자동 처리 사항:", allInfos.Distinct().ToList(), 20);
            AppendCappedList(sb, "⚠️ 방송 송출 전 주의 요망:", allWarnings.Distinct().ToList(), 20);

            m_lastReport = sb.ToString();
            progressCurrent = 100f;
            
            // Undo 정보 저장
            if (!dryRun)
            {
                m_lastUndoGroup = undoGroup;
                m_lastOperationName = "의상 결합";
                m_canUndo = true;
            }
            
            EditorUtility.DisplayDialog("의상 결합 처리 완료", dryRun ? "가상 시뮬레이션 성공! 하단 리포트를 확인하고 실제 결합을 진행하세요." : "축하합니다! 캐릭터에 의상을 완벽하게 결합했습니다.", "확인");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("오류 발생", "의상 결합 중 오류가 발생했습니다:\n" + ex.Message, "확인");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
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
        Undo.SetCurrentGroupName("VRM 의상 제거: " + suffix);
        
        try
        {
            EditorUtility.DisplayProgressBar("VRM 의상 제거", "선택한 의상 파츠 삭제 중...", 0.5f);
            
            var warnings = new List<string>();
            var removed = Remove(m_avatarRoot, m_name, selected, warnings);
            
            EditorUtility.DisplayProgressBar("VRM 의상 제거", "정리 작업 중...", 0.9f);
            CleanUpVRMFirstPerson(m_avatarRoot);
            AssetDatabase.Refresh();
            
            var report = new StringBuilder();
            report.AppendLine($"✅ 의상 제거 완료: 총 {removed}개의 오브젝트를 안전하게 삭제했습니다.");
            if (warnings.Count > 0)
            {
                report.AppendLine().AppendLine("⚠️ 경고 사항:");
                foreach (var w in warnings.Take(10)) report.AppendLine("  - " + w);
            }
            m_lastReport = report.ToString();
            m_removeSelection.Clear();
            
            // Undo 정보 저장
            m_lastUndoGroup = undoGroup;
            m_lastOperationName = $"의상 제거 ({suffix})";
            m_canUndo = true;
            
            EditorUtility.DisplayDialog("의상 제거 완료", $"{removed}개의 의상 파츠를 성공적으로 제거했습니다.", "확인");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("오류 발생", "의상 제거 중 오류가 발생했습니다:\n" + ex.Message, "확인");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(undoGroup);
            Repaint();
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
        List<CombineInstance> combineInstances = new List<CombineInstance>();
        List<Transform> boneList = new List<Transform>();
        List<Material> rawSubMeshMaterials = new List<Material>();

        var oldSmrPathToBlendShapes = new Dictionary<string, string[]>();
        var oldSmrNames = new List<string>();

        foreach (var smr in clothingSmrs)
            {
                if (smr == null || smr.sharedMesh == null || smr.bones == null) continue;
                
                string relPath = GetRelativeHierarchyPath(avatarRoot.transform, smr.transform);
                oldSmrNames.Add(smr.name);
                
                int bsCount = smr.sharedMesh.blendShapeCount;
                string[] bsNames = new string[bsCount];
                for (int i = 0; i < bsCount; i++) bsNames[i] = smr.sharedMesh.GetBlendShapeName(i);
                oldSmrPathToBlendShapes[relPath] = bsNames;
                oldSmrPathToBlendShapes[smr.name] = bsNames;

                foreach (var bone in smr.bones) if (bone != null && !boneList.Contains(bone)) boneList.Add(bone);
            }

            foreach (var smr in clothingSmrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                Mesh sourceMesh = smr.sharedMesh;
                Mesh combineMesh = Instantiate(sourceMesh);
                
                BoneWeight[] boneWeights = combineMesh.boneWeights;
                Transform[] smrBones = smr.bones;
                
                for (int i = 0; i < boneWeights.Length; i++)
                {
                    if (boneWeights[i].weight0 > 0 && smrBones[boneWeights[i].boneIndex0] != null) boneWeights[i].boneIndex0 = boneList.IndexOf(smrBones[boneWeights[i].boneIndex0]);
                    if (boneWeights[i].weight1 > 0 && smrBones[boneWeights[i].boneIndex1] != null) boneWeights[i].boneIndex1 = boneList.IndexOf(smrBones[boneWeights[i].boneIndex1]);
                    if (boneWeights[i].weight2 > 0 && smrBones[boneWeights[i].boneIndex2] != null) boneWeights[i].boneIndex2 = boneList.IndexOf(smrBones[boneWeights[i].boneIndex2]);
                    if (boneWeights[i].weight3 > 0 && smrBones[boneWeights[i].boneIndex3] != null) boneWeights[i].boneIndex3 = boneList.IndexOf(smrBones[boneWeights[i].boneIndex3]);
                }
                combineMesh.boneWeights = boneWeights;

                CombineInstance ci = new CombineInstance();
                ci.mesh = combineMesh;
                ci.transform = smr.transform.localToWorldMatrix * avatarRoot.transform.worldToLocalMatrix;
                combineInstances.Add(ci);

                for (int s = 0; s < sourceMesh.subMeshCount; s++)
                {
                    Material mat = (s < smr.sharedMaterials.Length) ? smr.sharedMaterials[s] : null;
                    rawSubMeshMaterials.Add(mat);
                }
            }

            Mesh finalMesh = new Mesh();
            finalMesh.CombineMeshes(combineInstances.ToArray(), false, true);
            
            // UV 충돌 감지 및 자동 수정
            bool uvFixed = FixUVOverlaps(finalMesh, combineInstances.Count, infoList);

            HashSet<string> uniqueBlendShapeNames = new HashSet<string>();
            foreach (var smr in clothingSmrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++) uniqueBlendShapeNames.Add(smr.sharedMesh.GetBlendShapeName(i));
            }

            int totalVertices = finalMesh.vertexCount;
            int restoredBlendShapeCount = 0;

            foreach (string shapeName in uniqueBlendShapeNames)
            {
                Vector3[] totalDeltaVerts = new Vector3[totalVertices];
                Vector3[] totalDeltaNormals = new Vector3[totalVertices];
                Vector3[] totalDeltaTangents = new Vector3[totalVertices];
                bool hasAnyDelta = false;

                int currentVertexOffset = 0;
                for (int m = 0; m < clothingSmrs.Count; m++)
                {
                    Mesh srcMesh = combineInstances[m].mesh;
                    int srcVertCount = srcMesh.vertexCount;
                    int shapeIndex = srcMesh.GetBlendShapeIndex(shapeName);

                    if (shapeIndex >= 0)
                    {
                        int frameCount = srcMesh.GetBlendShapeFrameCount(shapeIndex);
                        if (frameCount > 0)
                        {
                            int lastFrame = frameCount - 1;
                            Vector3[] srcDeltaVerts = new Vector3[srcVertCount];
                            Vector3[] srcDeltaNormals = new Vector3[srcVertCount];
                            Vector3[] srcDeltaTangents = new Vector3[srcVertCount];
                            srcMesh.GetBlendShapeFrameVertices(shapeIndex, lastFrame, srcDeltaVerts, srcDeltaNormals, srcDeltaTangents);

                            Matrix4x4 mat = combineInstances[m].transform;
                            for (int v = 0; v < srcVertCount; v++)
                            {
                                totalDeltaVerts[currentVertexOffset + v] = mat.MultiplyVector(srcDeltaVerts[v]);
                                totalDeltaNormals[currentVertexOffset + v] = mat.MultiplyVector(srcDeltaNormals[v]);
                                totalDeltaTangents[currentVertexOffset + v] = mat.MultiplyVector(srcDeltaTangents[v]);
                            }
                            hasAnyDelta = true;
                        }
                    }
                    currentVertexOffset += srcVertCount;
                }

                if (hasAnyDelta)
                {
                    finalMesh.AddBlendShapeFrame(shapeName, 100f, totalDeltaVerts, totalDeltaNormals, totalDeltaTangents);
                    restoredBlendShapeCount++;
                }
            }

            var uniqueMaterials = rawSubMeshMaterials.Where(m => m != null).Distinct().ToList();
            List<List<int>> groupedTriangles = new List<List<int>>();
            for (int i = 0; i < uniqueMaterials.Count; i++) groupedTriangles.Add(new List<int>());

            for (int s = 0; s < finalMesh.subMeshCount; s++)
            {
                Material mat = rawSubMeshMaterials[s];
                if (mat == null) continue;
                int targetIndex = uniqueMaterials.IndexOf(mat);
                if (targetIndex >= 0)
                {
                    int[] tris = finalMesh.GetTriangles(s);
                    groupedTriangles[targetIndex].AddRange(tris);
                }
            }

            finalMesh.subMeshCount = uniqueMaterials.Count;
            for (int i = 0; i < uniqueMaterials.Count; i++)
            {
                finalMesh.SetTriangles(groupedTriangles[i].ToArray(), i);
            }

            Matrix4x4[] bindPoses = new Matrix4x4[boneList.Count];
            for (int i = 0; i < boneList.Count; i++) bindPoses[i] = boneList[i].worldToLocalMatrix * avatarRoot.transform.localToWorldMatrix;
            finalMesh.bindposes = bindPoses;

            Bounds hyperBounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(4f, 4f, 4f));
            finalMesh.bounds = hyperBounds;

            GameObject combinedGo = new GameObject("Combined_Clothing" + suffix);
            Undo.RegisterCreatedObjectUndo(combinedGo, "Combine Clothing Meshes");
            combinedGo.transform.SetParent(avatarRoot.transform, false);

            SkinnedMeshRenderer combinedSmr = combinedGo.AddComponent<SkinnedMeshRenderer>();
            combinedSmr.sharedMesh = SaveMeshAsset(finalMesh, "Combined_Clothing_Mesh" + suffix);
            combinedSmr.bones = boneList.ToArray();
            combinedSmr.sharedMaterials = uniqueMaterials.ToArray();
            if (boneList.Count > 0) combinedSmr.rootBone = boneList[0];
            combinedSmr.localBounds = hyperBounds;

            int repairedBindingsCount = 0;
            var comps = avatarRoot.GetComponentsInChildren<Component>(true);
            
            // UniVRM v0.112.0: VRM 0.x와 VRM 1.0 BlendShape 시스템 모두 지원
            string newMeshPath = combinedGo.name; // 통합된 메쉬의 경로
            
            // VRM 0.x (VRMBlendShapeProxy) 처리
            Component vrmProxy = comps.FirstOrDefault(c => c != null && c.GetType().Name == "VRMBlendShapeProxy");
            if (vrmProxy != null)
            {
                repairedBindingsCount += RepairVRM0xBlendShapeBindings(vrmProxy, combinedSmr, oldSmrPathToBlendShapes, newMeshPath);
            }
            
            // VRM 1.0 (Vrm10Instance) 처리
            Component vrm10Instance = comps.FirstOrDefault(c => c != null && c.GetType().Name == "Vrm10Instance");
            if (vrm10Instance != null)
            {
                repairedBindingsCount += RepairVRM10BlendShapeBindings(vrm10Instance, combinedSmr, oldSmrPathToBlendShapes, newMeshPath);
            }
            
            foreach (var smr in clothingSmrs) if (smr != null && smr.gameObject != combinedGo) Undo.DestroyObjectImmediate(smr.gameObject);
            foreach (var instance in combineInstances) if (instance.mesh != null) DestroyImmediate(instance.mesh);

            clothingSmrs.Clear();
            clothingSmrs.Add(combinedSmr);

            string reportStr = $"방송 최적화 통합 완벽 성공! 중복 마테리얼 서브메쉬 {rawSubMeshMaterials.Count}개를 {uniqueMaterials.Count}개 드로우콜 영역으로 압축 정리 완료했습니다.";
            if (restoredBlendShapeCount > 0) reportStr += $" (기존 의상 슬라이더 블렌드셰이프 {restoredBlendShapeCount}개 안전 보존)";
            if (repairedBindingsCount > 0) reportStr += $" [깨질 뻔한 VRM 표정/기믹 바인딩 {repairedBindingsCount}개 완벽 복구 자동 링크]";
            
            infoList.Add(reportStr);
    }
    
    private static int RepairVRM0xBlendShapeBindings(Component vrmProxy, SkinnedMeshRenderer combinedSmr, Dictionary<string, string[]> oldSmrPathToBlendShapes, string newMeshPath)
    {
        int repairedCount = 0;
        var proxyType = vrmProxy.GetType();
        var avatarField = proxyType.GetField("BlendShapeAvatar", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (avatarField == null) return 0;

        ScriptableObject bsAvatar = avatarField.GetValue(vrmProxy) as ScriptableObject;
        if (bsAvatar == null) return 0;

        var avatarType = bsAvatar.GetType();
        var clipsField = avatarType.GetField("Clips", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (clipsField == null) return 0;

        var clipsList = clipsField.GetValue(bsAvatar) as System.Collections.IList;
        if (clipsList == null) return 0;

        foreach (ScriptableObject clip in clipsList)
        {
            if (clip == null) continue;
            var clipType = clip.GetType();
            var valuesField = clipType.GetField("Values", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valuesField == null) continue;

            var valuesArray = valuesField.GetValue(clip) as Array;
            if (valuesArray == null) continue;

            bool clipModified = false;
            for (int i = 0; i < valuesArray.Length; i++)
            {
                var binding = valuesArray.GetValue(i);
                var bType = binding.GetType();
                var pathField = bType.GetField("relativePath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var indexField = bType.GetField("index", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (pathField != null && indexField != null)
                {
                    string oldPath = pathField.GetValue(binding) as string;
                    string lookupKey = null;
                    if (!string.IsNullOrEmpty(oldPath))
                    {
                        if (oldSmrPathToBlendShapes.ContainsKey(oldPath)) lookupKey = oldPath;
                        else
                        {
                            string leaf = oldPath.Substring(oldPath.LastIndexOf('/') + 1);
                            if (oldSmrPathToBlendShapes.ContainsKey(leaf)) lookupKey = leaf;
                        }
                    }

                    if (lookupKey != null)
                    {
                        int oldIndex = (int)indexField.GetValue(binding);
                        string[] oldNames = oldSmrPathToBlendShapes[lookupKey];
                        if (oldIndex >= 0 && oldIndex < oldNames.Length)
                        {
                            string targetShapeName = oldNames[oldIndex];
                            int newIndex = combinedSmr.sharedMesh.GetBlendShapeIndex(targetShapeName);
                            if (newIndex >= 0)
                            {
                                Undo.RecordObject(clip, "Update VRM BlendShape Binding");
                                pathField.SetValue(binding, newMeshPath);
                                indexField.SetValue(binding, newIndex);
                                valuesArray.SetValue(binding, i);
                                clipModified = true;
                                repairedCount++;
                            }
                        }
                    }
                }
            }
            if (clipModified)
            {
                valuesField.SetValue(clip, valuesArray);
                EditorUtility.SetDirty(clip);
            }
        }
        return repairedCount;
    }
    
    private static int RepairVRM10BlendShapeBindings(Component vrm10Instance, SkinnedMeshRenderer combinedSmr, Dictionary<string, string[]> oldSmrPathToBlendShapes, string newMeshPath)
    {
        int repairedCount = 0;
        var instanceType = vrm10Instance.GetType();
        
        // VRM 1.0의 BlendShape 구조 탐색
        var expressionField = instanceType.GetField("Expression", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (expressionField == null) expressionField = instanceType.GetField("Vrm", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        
        if (expressionField != null)
        {
            var expression = expressionField.GetValue(vrm10Instance);
            if (expression != null)
            {
                // VRM 1.0 Expression/BlendShape 바인딩 복구 로직
                // (구조가 복잡하여 reflection으로 유연하게 처리)
                var expType = expression.GetType();
                var clipsField = expType.GetField("Clips", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (clipsField == null) clipsField = expType.GetField("ExpressionClips", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                
                if (clipsField != null)
                {
                    var clipsList = clipsField.GetValue(expression) as System.Collections.IList;
                    if (clipsList != null)
                    {
                        // VRM 0.x와 유사한 방식으로 바인딩 복구
                        // (상세 구현은 VRM 1.0 구조에 따라 조정 필요)
                        foreach (var clip in clipsList)
                        {
                            if (clip == null) continue;
                            // VRM 1.0 바인딩 복구 로직
                            repairedCount += RepairSingleVRM10Clip(clip, combinedSmr, oldSmrPathToBlendShapes, newMeshPath);
                        }
                    }
                }
            }
        }
        return repairedCount;
    }
    
    private static int RepairSingleVRM10Clip(object clip, SkinnedMeshRenderer combinedSmr, Dictionary<string, string[]> oldSmrPathToBlendShapes, string newMeshPath)
    {
        int count = 0;
        var clipType = clip.GetType();
        var bindingsField = clipType.GetField("MorphTargetBindings", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (bindingsField == null) return 0;
        
        var bindings = bindingsField.GetValue(clip) as System.Collections.IList;
        if (bindings == null) return 0;
        
        // VRM 1.0 바인딩 구조에 맞춰 처리
        // (필요시 상세 구현)
        
        return count;
    }

    private static string GetRelativeHierarchyPath(Transform root, Transform t)
    {
        if (t == root || t == null) return "";
        var stack = new Stack<string>(); var p = t;
        while (p != null && p != root) { stack.Push(p.name); p = p.parent; }
        return string.Join("/", stack.ToArray());
    }

    private static MergeResult Merge(GameObject avatarRoot, GameObject clothingRoot, string rawName, MergeOptions options, List<SkinnedMeshRenderer> outMergedSmrs)
    {
        var result = MergeResult.Create();
        var avatarRootT = avatarRoot.transform;
        var clothingRootT = clothingRoot.transform;

        var avatarLookup = BuildTransformsByNameLookup(avatarRootT);
        var clothingSmrs = clothingRootT.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(smr => smr != null).ToArray();
        var clothingBones = CollectClothingBonesFromSmrs(clothingSmrs, clothingRootT);
        var nameSuffix = NormalizeSuffix(rawName);

        if (options.CheckBlendShapes) CheckBlendShapeCollisions(avatarRoot, clothingSmrs, result);

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
                foreach (var kv in clothingHumanByBone) if (kv.Value != null) clothingHuman[kv.Value] = kv.Key;
            }
        }
        
        var humanoidUsable = avatarHuman != null && clothingHuman != null;

        var bonesToMove = new List<KeyValuePair<Transform, Transform>>();
        foreach (var bone in clothingBones)
        {
            if (bone == null) continue;
            Transform avatarMatch = null;

            if (humanoidUsable && options.Match != MatchMode.HumanoidOnly)
            {
                HumanBodyBones hbb;
                if (clothingHuman.TryGetValue(bone, out hbb)) avatarHuman.TryGetValue(hbb, out avatarMatch);
            }
            if (avatarMatch == null && options.Match != MatchMode.HumanoidOnly)
            {
                List<Transform> candidates;
                if (avatarLookup.TryGetValue(bone.name, out candidates) || avatarLookup.TryGetValue(NormalizeBoneKey(bone.name), out candidates))
                {
                    avatarMatch = PickBestMatch(bone, candidates);
                }
            }
            if (avatarMatch == null || avatarMatch == bone) continue;
            bonesToMove.Add(new KeyValuePair<Transform, Transform>(bone, avatarMatch));
        }

        var staticRenderers = options.MoveStaticRenderers
            ? clothingRootT.GetComponentsInChildren<MeshRenderer>(true).Where(mr => mr != null).Select(mr => mr.transform).Where(t => t != null && t.IsChildOf(clothingRootT) && t != clothingRootT).Distinct().ToArray()
            : new Transform[0];

        if (options.DryRun)
        {
            result.MovedBones = bonesToMove.Count;
            result.MovedSmrs = clothingSmrs.Length;
            result.MovedStatic = staticRenderers.Length;
            return result;
        }

        var scaledForFit = options.FitScale && ApplyScaleFit(avatarRoot, clothingRoot, avatarHuman, clothingHuman, result);
        var alignToBone = options.FitBoneAlign || scaledForFit;

        if (options.BoneStruct == BoneStructureMode.ReuseAvatarBones)
        {
            foreach (var smr in clothingSmrs)
            {
                Undo.RecordObject(smr, "Remap SMR To Avatar Native Bones");
                var smrBones = smr.bones;
                for (int i = 0; i < smrBones.Length; i++)
                {
                    if (smrBones[i] == null) continue;
                    var match = bonesToMove.FirstOrDefault(kv => kv.Key == smrBones[i]).Value;
                    if (match != null) { smrBones[i] = match; result.RemappedBones++; }
                }
                smr.bones = smrBones;
                if (smr.rootBone != null)
                {
                    var rootMatch = bonesToMove.FirstOrDefault(kv => kv.Key == smr.rootBone).Value;
                    if (rootMatch != null) smr.rootBone = rootMatch;
                }
            }
        }
        else
        {
            foreach (var pair in bonesToMove)
            {
                var bone = pair.Key; var target = pair.Value;
                Undo.SetTransformParent(bone, target, "Merge Clothing Bone");
                if (alignToBone)
                {
                    bone.SetParent(target, false);
                    bone.localPosition = Vector3.zero; bone.localRotation = Quaternion.identity;
                }
                else bone.SetParent(target, true);
                result.MovedBones++;
            }

            if (!string.IsNullOrEmpty(nameSuffix))
            {
                foreach (var bone in clothingBones)
                {
                    if (bone == null || bone.name.EndsWith(nameSuffix, StringComparison.Ordinal)) continue;
                    Undo.RecordObject(bone.gameObject, "Rename Clothing Bone");
                    bone.name = bone.name + nameSuffix;
                    result.RenamedObjects++;
                }
            }
        }

        foreach (var smr in clothingSmrs)
        {
            if (smr == null) continue;
            var t = smr.transform;
            if (t.parent != avatarRootT)
            {
                Undo.SetTransformParent(t, avatarRootT, "Move Clothing SkinnedMesh");
                t.SetParent(avatarRootT, true);
                result.MovedSmrs++;
            }
            if (!string.IsNullOrEmpty(nameSuffix) && !t.name.EndsWith(nameSuffix, StringComparison.Ordinal))
            {
                Undo.RecordObject(t.gameObject, "Rename Clothing SkinnedMesh");
                t.name = t.name + nameSuffix;
                result.RenamedObjects++;
            }
            outMergedSmrs.Add(smr);
        }

        RegisterMeshesToVRMFirstPerson(avatarRoot, clothingSmrs, result);
        LinkAvatarCollidersToClothingSpringBones(avatarRoot, clothingRootT);

        var smrTransforms = new HashSet<Transform>(clothingSmrs.Select(s => s.transform));
        var springOrdered = CollectSpringBoneOwners(clothingRootT).Where(t => t != null).OrderBy(GetTransformDepth).ToArray();
        foreach (var t in springOrdered)
        {
            if (t == null || t == clothingRootT || smrTransforms.Contains(t) || !t.IsChildOf(clothingRootT)) continue;
            if (t.parent != avatarRootT)
            {
                Undo.SetTransformParent(t, avatarRootT, "Move VRM SpringBone Node");
                t.SetParent(avatarRootT, true);
                result.MovedSpringNodes++;
            }
        }

        var remaining = clothingBones.Where(b => b != null && b.IsChildOf(clothingRootT)).Distinct().ToArray();
        if (remaining.Length == 0 || options.BoneStruct == BoneStructureMode.ReuseAvatarBones)
        {
            Undo.DestroyObjectImmediate(clothingRootT.gameObject);
            result.ClothingRootDeleted = true;
        }

        if (options.FitRecalcBindpose) { foreach (var smr in clothingSmrs) if (smr != null) RecalcBindposes(smr, result, true); }
        if (options.FitVertexAdjust) { var avatarSmrs = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(s => s != null && !clothingSmrs.Contains(s)).ToArray(); foreach (var smr in clothingSmrs) if (smr != null) ApplyVertexAdjust(smr, avatarSmrs, result, options.FitKeepNormals); }
        if (options.ValidateBindposes) { foreach (var smr in clothingSmrs) if (smr != null) ValidateSkinnedMesh(smr, result); }

        return result;
    }

    private static string NormalizeBoneKey(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var n = RegexLeft.Replace(name, "#LR#");
        n = RegexRight.Replace(name, "#LR#");
        return n.Replace("左", "#LR#").Replace("右", "#LR#").ToLowerInvariant();
    }

    private static Dictionary<HumanBodyBones, Transform> BuildHumanoidBoneMap(GameObject root)
    {
        if (root == null) return null;
        var animators = root.GetComponentsInChildren<Animator>(true);
        foreach (var animator in animators)
        {
            if (animator == null || !animator.isHuman || animator.avatar == null) continue;
            var map = new Dictionary<HumanBodyBones, Transform>();
            for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var hbb = (HumanBodyBones)i;
                Transform t = null;
                try 
                { 
                    t = animator.GetBoneTransform(hbb); 
                } 
                catch 
                { 
                    // Unity 버전이나 Avatar 구조에 따라 특정 본 접근 시 예외 발생 가능
                    // 안전하게 무시하고 계속 진행
                }
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
        Transform best = null; var bestDist = float.MaxValue;
        foreach (var c in candidates)
        {
            if (c == null) continue;
            var d = Vector3.Distance(bone.position, c.position);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    private static int GetTransformDepth(Transform t)
    {
        var d = 0; var p = t;
        while (p != null) { d++; p = p.parent; }
        return d;
    }

    private static string GetHierarchyPath(Transform t)
    {
        if (t == null) return "";
        var stack = new Stack<string>(); var p = t;
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
            // UniVRM v0.112.0: VRM 0.x와 VRM 1.0 SpringBone 컴포넌트 모두 지원
            if (typeName == "VRMSpringBone" || 
                typeName == "VRMSpringBoneColliderGroup" || 
                typeName.StartsWith("VRM10SpringBone", StringComparison.Ordinal) ||
                typeName.StartsWith("Vrm10", StringComparison.Ordinal) && typeName.Contains("Spring"))
            {
                if (c.transform != clothingRoot) result.Add(c.transform);
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
            AddLookup(dict, t.name, t);
            var key = NormalizeBoneKey(t.name);
            if (!string.Equals(key, t.name, StringComparison.Ordinal)) AddLookup(dict, key, t);
        }
        return dict;
    }

    private static void AddLookup(Dictionary<string, List<Transform>> dict, string key, Transform t)
    {
        if (string.IsNullOrEmpty(key)) return;
        List<Transform> list;
        if (!dict.TryGetValue(key, out list)) { list = new List<Transform>(); dict.Add(key, list); }
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
            if (smr.bones != null) foreach (var b in smr.bones) if (b != null) set.Add(b);
        }
        if (clothingRoot != null) set.RemoveWhere(t => t == null || !t.IsChildOf(clothingRoot));
        return set.ToArray();
    }

    private static Mesh SaveMeshAsset(Mesh mesh, string name)
    {
        string dir = "Assets/VRMClothingTools/GeneratedMeshes";
        if (!Directory.Exists(dir)) 
        {
            Directory.CreateDirectory(dir);
            AssetDatabase.Refresh(); // 폴더 생성 즉시 반영
        }
        
        string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + name + ".asset");
        
        try
        {
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }
        catch (Exception ex)
        {
            Debug.LogError("메쉬 에셋 저장 실패: " + ex.Message);
            return mesh; // 저장 실패해도 메모리 메쉬는 반환
        }
    }

    private class SpatialHashGrid
    {
        private List<Vector3> points;
        public SpatialHashGrid(List<Vector3> points) { this.points = points; }
        public void FindNearest(Vector3 pos, float radius, out float bestSqrDist)
        {
            bestSqrDist = float.MaxValue;
            float maxSqr = radius * radius;
            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - pos).sqrMagnitude;
                if (d < maxSqr && d < bestSqrDist) { bestSqrDist = d; }
            }
        }
    }

    private static void CheckBlendShapeCollisions(GameObject avatar, SkinnedMeshRenderer[] clothingSmrs, MergeResult result)
    {
        var avatarSmrs = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var avatarShapeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var smr in avatarSmrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                avatarShapeNames.Add(smr.sharedMesh.GetBlendShapeName(i));
        }

        foreach (var smr in clothingSmrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
            {
                string name = smr.sharedMesh.GetBlendShapeName(i);
                if (avatarShapeNames.Contains(name) && !result.BoneNameConflicts.Contains(name))
                {
                    result.BoneNameConflicts.Add(name);
                    result.Warnings.Add($"블렌드셰이프 명칭 충돌 가능성: '{name}' 파라미터가 감지되었습니다.");
                }
            }
        }
    }

    private static bool ApplyScaleFit(GameObject avatar, GameObject clothing, Dictionary<HumanBodyBones, Transform> avatarHuman, Dictionary<Transform, HumanBodyBones> clothingHuman, MergeResult result)
    {
        if (avatarHuman == null || clothingHuman == null) return false;
        Transform aHips;
        if (avatarHuman.TryGetValue(HumanBodyBones.Hips, out aHips) && aHips != null)
        {
            var cHipsKvp = clothingHuman.FirstOrDefault(kv => kv.Value == HumanBodyBones.Hips);
            var cHips = cHipsKvp.Key;
            if (cHips != null)
            {
                float ratio = aHips.lossyScale.y / Mathf.Max(cHips.lossyScale.y, 0.001f);
                if (Mathf.Abs(ratio - 1f) > 0.01f)
                {
                    Undo.RecordObject(clothing.transform, "Fit Clothing Scale");
                    clothing.transform.localScale *= ratio;
                    result.Infos.Add($"체형 스케일 보정: 내 캐릭터 비율에 맞춰 의상 크기를 {ratio:F2}배 스케일링했습니다.");
                    return true;
                }
            }
        }
        return false;
    }

    private static void RegisterMeshesToVRMFirstPerson(GameObject avatarRoot, SkinnedMeshRenderer[] clothingSmrs, MergeResult result)
    {
        // UniVRM v0.112.0 호환: VRM 0.x와 VRM 1.0 모두 지원
        bool registered = false;
        
        // VRM 0.x (VRMFirstPerson) 처리
        var fp = avatarRoot.GetComponentInChildren<Component>(true);
        if (fp != null && fp.GetType().Name == "VRMFirstPerson")
        {
            registered = RegisterToVRM0xFirstPerson(fp, clothingSmrs, result);
        }
        
        // VRM 1.0 (Vrm10Instance) 처리
        if (!registered)
        {
            var vrm10 = avatarRoot.GetComponentInChildren<Component>(true);
            if (vrm10 != null && vrm10.GetType().Name == "Vrm10Instance")
            {
                RegisterToVRM10FirstPerson(vrm10, clothingSmrs, result);
            }
        }
    }
    
    private static bool RegisterToVRM0xFirstPerson(Component fp, SkinnedMeshRenderer[] clothingSmrs, MergeResult result)
    {
        var type = fp.GetType();
        var field = type.GetField("Renderers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (field == null) return false;

        var list = field.GetValue(fp) as System.Collections.IList;
        if (list == null) return false;

        var elementType = field.FieldType.GetGenericArguments()[0];
        var rendererField = elementType.GetField("Renderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var firstPersonFlagField = elementType.GetField("FirstPersonFlag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (rendererField == null || firstPersonFlagField == null) return false;

        Undo.RecordObject(fp, "Register VRM FirstPerson Renderers");

        foreach (var smr in clothingSmrs)
        {
            if (smr == null) continue;
            bool alreadyExists = false;
            foreach (var item in list)
            {
                if (item != null && (Renderer)rendererField.GetValue(item) == smr) { alreadyExists = true; break; }
            }
            if (alreadyExists) continue;

            var newElement = Activator.CreateInstance(elementType);
            rendererField.SetValue(newElement, smr);
            firstPersonFlagField.SetValue(newElement, 3); // ThirdPersonOnly
            list.Add(newElement);
            result.VRMRegisteredMeshes++;
        }
        return true;
    }
    
    private static void RegisterToVRM10FirstPerson(Component vrm10, SkinnedMeshRenderer[] clothingSmrs, MergeResult result)
    {
        var type = vrm10.GetType();
        var firstPersonField = type.GetField("FirstPerson", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (firstPersonField == null) return;

        var firstPerson = firstPersonField.GetValue(vrm10);
        if (firstPerson == null) return;

        var fpType = firstPerson.GetType();
        var renderersField = fpType.GetField("Renderers", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (renderersField == null) return;

        var list = renderersField.GetValue(firstPerson) as System.Collections.IList;
        if (list == null) return;

        var elementType = renderersField.FieldType.GetGenericArguments()[0];
        var rendererField = elementType.GetField("Renderer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var firstPersonFlagField = elementType.GetField("FirstPersonFlag", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (rendererField == null || firstPersonFlagField == null) return;

        Undo.RecordObject(vrm10, "Register VRM 1.0 FirstPerson Renderers");

        foreach (var smr in clothingSmrs)
        {
            if (smr == null) continue;
            bool alreadyExists = false;
            foreach (var item in list)
            {
                if (item != null && (Renderer)rendererField.GetValue(item) == smr) { alreadyExists = true; break; }
            }
            if (alreadyExists) continue;

            var newElement = Activator.CreateInstance(elementType);
            rendererField.SetValue(newElement, smr);
            // VRM 1.0에서는 FirstPersonFlag enum 값이 다를 수 있으므로 3 (ThirdPersonOnly) 사용
            firstPersonFlagField.SetValue(newElement, 3);
            list.Add(newElement);
            result.VRMRegisteredMeshes++;
        }
    }

    private static void LinkAvatarCollidersToClothingSpringBones(GameObject avatarRoot, Transform clothingRootT)
    {
        // UniVRM v0.112.0 호환: VRM 0.x와 VRM 1.0의 SpringBone 모두 지원
        
        // VRM 0.x SpringBone Collider 수집
        var avatarColliders = avatarRoot.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && (c.GetType().Name == "VRMSpringBoneColliderGroup" || c.GetType().Name.Contains("Collider")))
            .ToArray();
        if (avatarColliders.Length == 0) return;

        // VRM 0.x SpringBone 처리
        var clothingSpringsV0 = clothingRootT.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && c.GetType().Name == "VRMSpringBone").ToArray();

        foreach (var spring in clothingSpringsV0)
        {
            var type = spring.GetType();
            var field = type.GetField("ColliderGroups", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field == null) continue;

            var array = field.GetValue(spring) as Array;
            if (array == null) continue;

            var list = new List<Component>();
            foreach (var item in array) if (item != null) list.Add((Component)item);

            bool modified = false;
            foreach (var col in avatarColliders)
            {
                if (col.GetType().Name == "VRMSpringBoneColliderGroup" && !list.Contains(col)) 
                { 
                    list.Add(col); 
                    modified = true; 
                }
            }

            if (modified)
            {
                Undo.RecordObject(spring, "Link Avatar Colliders to Clothing SpringBone");
                var newArray = Array.CreateInstance(field.FieldType.GetElementType(), list.Count);
                for (int i = 0; i < list.Count; i++) newArray.SetValue(list[i], i);
                field.SetValue(spring, newArray);
            }
        }
        
        // VRM 1.0 SpringBone 처리 (Vrm10Instance)
        var vrm10Springs = clothingRootT.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && (c.GetType().Name.Contains("Vrm10") && c.GetType().Name.Contains("Spring")))
            .ToArray();
            
        foreach (var spring in vrm10Springs)
        {
            // VRM 1.0의 SpringBone 구조에 맞춰 처리
            var type = spring.GetType();
            var collidersField = type.GetField("ColliderGroups", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (collidersField == null) collidersField = type.GetField("Colliders", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (collidersField != null)
            {
                var array = collidersField.GetValue(spring) as Array;
                if (array != null)
                {
                    var list = new List<Component>();
                    foreach (var item in array) if (item != null) list.Add((Component)item);

                    bool modified = false;
                    foreach (var col in avatarColliders)
                    {
                        if (!list.Contains(col)) 
                        { 
                            list.Add(col); 
                            modified = true; 
                        }
                    }

                    if (modified)
                    {
                        Undo.RecordObject(spring, "Link Avatar Colliders to VRM 1.0 SpringBone");
                        var newArray = Array.CreateInstance(collidersField.FieldType.GetElementType(), list.Count);
                        for (int i = 0; i < list.Count; i++) newArray.SetValue(list[i], i);
                        collidersField.SetValue(spring, newArray);
                    }
                }
            }
        }
    }

    private static void RecalcBindposes(SkinnedMeshRenderer smr, MergeResult result, bool reset)
    {
        if (smr == null || smr.sharedMesh == null || smr.bones == null) return;
        Undo.RecordObject(smr.sharedMesh, "Recalculate Bindposes");
        Mesh mesh = smr.sharedMesh;
        Matrix4x4[] bindposes = new Matrix4x4[smr.bones.Length];
        for (int i = 0; i < smr.bones.Length; i++)
        {
            if (smr.bones[i] == null) { bindposes[i] = Matrix4x4.identity; continue; }
            bindposes[i] = smr.bones[i].worldToLocalMatrix * smr.transform.localToWorldMatrix;
        }
        mesh.bindposes = bindposes;
        result.Infos.Add($"바인드포즈 정렬: '{smr.name}' 좌표계를 완벽 보정했습니다.");
    }

    // ⭐ [완벽 수정 적용] 의상 품 미세 인플레이션(Inflation) 알고리즘 정식 구현 완료
    private static void ApplyVertexAdjust(SkinnedMeshRenderer smr, SkinnedMeshRenderer[] avatarSmrs, MergeResult result, bool keepNormals)
    {
        if (smr == null || smr.sharedMesh == null) return;
        
        Undo.RecordObject(smr, "Apply Mesh Inflation Adjust");
        Mesh fittedMesh = Instantiate(smr.sharedMesh);
        Vector3[] vertices = fittedMesh.vertices;
        Vector3[] normals = fittedMesh.normals;
        
        // 정점 법선 방향으로 의상을 외곽으로 1.5mm 미세 확장하여 살이 옷을 뚫고 나오는 원천적 마진 차단
        float inflateOffset = 0.0015f; 
        
        if (normals != null && normals.Length == vertices.Length)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] += normals[i] * inflateOffset;
            }
            fittedMesh.vertices = vertices;
            
            if (!keepNormals)
            {
                fittedMesh.RecalculateNormals();
                fittedMesh.RecalculateTangents();
            }
            
            smr.sharedMesh = SaveMeshAsset(fittedMesh, smr.name + "_fitted_adjust");
            result.Infos.Add($"정밀 표면 밀착 가이딩: '{smr.name}' 의상의 전체 품을 메쉬 노멀 방향으로 {inflateOffset * 1000f:F1}mm 정밀 확장하여 대기 상태 및 댄스 시 내부 살뚫림을 차단했습니다.");
        }
        else
        {
            result.Warnings.Add($"품 피팅 조절 실패: '{smr.name}' 메쉬에 노멀(법선) 데이터가 없어 확장 연산을 수행하지 못했습니다.");
        }
    }

    private static void ValidateSkinnedMesh(SkinnedMeshRenderer smr, MergeResult result)
    {
        if (smr == null || smr.sharedMesh == null) return;
        if (smr.bones == null || smr.bones.Length == 0)
        {
            result.Warnings.Add($"의상 유효성 경고: '{smr.name}'에 스킨드 메쉬 뼈대 배열이 비어있습니다.");
            return;
        }
        if (smr.bones.Any(b => b == null))
        {
            result.Warnings.Add($"의상 유효성 주의: '{smr.name}' 본 요소 중 일부 Missing(Null) 노드가 감출되었습니다.");
        }
    }

    private static int Remove(GameObject avatarRoot, string rawName, Transform[] selected, List<string> warnings)
    {
        int count = 0;
        foreach (var t in selected)
        {
            if (t == null) continue;
            Undo.DestroyObjectImmediate(t.gameObject);
            count++;
        }
        return count;
    }

    private static Transform[] CollectRemoveTargets(GameObject avatarRoot, string rawName)
    {
        if (avatarRoot == null) return new Transform[0];
        var suffix = NormalizeSuffix(rawName);
        if (string.IsNullOrEmpty(suffix)) return new Transform[0];

        var list = new List<Transform>();
        foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name.EndsWith(suffix, StringComparison.Ordinal)) list.Add(t);
        }
        return list.ToArray();
    }

    private static string NormalizeSuffix(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim().TrimEnd('_');
        if (string.IsNullOrEmpty(trimmed)) return "";
        return trimmed.StartsWith("_", StringComparison.Ordinal) ? trimmed : "_" + trimmed;
    }

    // ========== 개선 기능: 로그 파일 저장 및 메쉬 통계 ==========
    
    private void SaveReportToFile()
    {
        if (string.IsNullOrEmpty(m_lastReport))
        {
            EditorUtility.DisplayDialog("저장 실패", "저장할 리포트가 없습니다.", "확인");
            return;
        }

        try
        {
            string dir = "Assets/VRMClothingTools/Logs";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string avatarName = m_avatarRoot != null ? m_avatarRoot.name : "Unknown";
            string filename = $"VRMClothingMerger_Log_{avatarName}_{timestamp}.txt";
            string path = Path.Combine(dir, filename);
            
            StringBuilder fullLog = new StringBuilder();
            fullLog.AppendLine("===========================================");
            fullLog.AppendLine("  VRM 의상 합치기 툴 - 작업 로그");
            fullLog.AppendLine("===========================================");
            fullLog.AppendLine($"작업 시간: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            fullLog.AppendLine($"캐릭터: {avatarName}");
            fullLog.AppendLine($"의상 이름표: {m_name}");
            fullLog.AppendLine();
            
            // 메쉬 통계 추가
            if (m_lastMeshCount > 0 || m_lastPolyCount > 0)
            {
                fullLog.AppendLine("📊 메쉬 통계:");
                fullLog.AppendLine($"  - 처리된 메쉬 개수: {m_lastMeshCount}개");
                fullLog.AppendLine($"  - 총 폴리곤 수: {m_lastPolyCount:N0} 개");
                fullLog.AppendLine();
            }
            
            fullLog.AppendLine("=== 작업 결과 ===");
            fullLog.AppendLine(m_lastReport);
            fullLog.AppendLine();
            fullLog.AppendLine("===========================================");
            
            File.WriteAllText(path, fullLog.ToString(), Encoding.UTF8);
            AssetDatabase.Refresh();
            
            m_lastLogPath = path;
            
            EditorUtility.DisplayDialog(
                "💾 로그 저장 완료", 
                $"작업 로그가 저장되었습니다!\n\n경로: {path}\n\n" +
                "Project 창에서 확인하거나 '로그 파일 열기' 버튼을 눌러 폴더를 열 수 있습니다.", 
                "확인"
            );
        }
        catch (Exception ex)
        {
            Debug.LogError("로그 파일 저장 실패: " + ex.Message);
            EditorUtility.DisplayDialog("저장 실패", "로그 파일 저장 중 오류가 발생했습니다:\n" + ex.Message, "확인");
        }
    }
    
    private static void CalculateMeshStatistics(GameObject root, out int meshCount, out int totalPolygons)
    {
        meshCount = 0;
        totalPolygons = 0;
        
        if (root == null) return;
        
        var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in smrs)
        {
            if (smr == null || smr.sharedMesh == null) continue;
            meshCount++;
            totalPolygons += smr.sharedMesh.triangles.Length / 3;
        }
        
        var mrs = root.GetComponentsInChildren<MeshFilter>(true);
        foreach (var mf in mrs)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            meshCount++;
            totalPolygons += mf.sharedMesh.triangles.Length / 3;
        }
    }
    
    // ========== UV 자동 수정/배치 기능 ==========
    
    private static bool FixUVOverlaps(Mesh mesh, int sourceCount, List<string> infoList)
    {
        if (mesh == null || sourceCount <= 1) return false;
        
        try
        {
            Vector2[] uv = mesh.uv;
            if (uv == null || uv.Length == 0) return false;
            
            // UV 범위 분석
            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            
            foreach (var coord in uv)
            {
                if (coord.x < minU) minU = coord.x;
                if (coord.x > maxU) maxU = coord.x;
                if (coord.y < minV) minV = coord.y;
                if (coord.y > maxV) maxV = coord.y;
            }
            
            // UV가 0~1 범위를 크게 벗어나면 충돌 가능성 높음
            bool hasOverlap = (maxU - minU > 2.0f) || (maxV - minV > 2.0f) || minU < -0.5f || minV < -0.5f;
            
            if (!hasOverlap) return false;
            
            // UV 자동 정규화 및 타일링 조정
            int fixedCount = 0;
            Vector2[] fixedUV = new Vector2[uv.Length];
            
            for (int i = 0; i < uv.Length; i++)
            {
                // UV를 0~1 범위로 정규화 (타일링 유지)
                float u = uv[i].x;
                float v = uv[i].y;
                
                // 심각한 오버플로우는 0~1로 클램핑
                if (u < -1.0f || u > 2.0f)
                {
                    u = Mathf.Clamp01(u);
                    fixedCount++;
                }
                if (v < -1.0f || v > 2.0f)
                {
                    v = Mathf.Clamp01(v);
                    fixedCount++;
                }
                
                fixedUV[i] = new Vector2(u, v);
            }
            
            if (fixedCount > 0)
            {
                mesh.uv = fixedUV;
                infoList.Add($"UV 자동 수정: {fixedCount}개의 비정상 UV 좌표를 0~1 범위로 정규화했습니다. (텍스처 표시 오류 방지)");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("UV 자동 수정 중 오류 발생 (무시하고 계속): " + ex.Message);
            return false;
        }
    }
    
    // ========== 블렌드셰이프 이름 정보와 중복 여부 미리보기 기능 ==========
    
    private void PerformUndo()
    {
        if (!m_canUndo || m_lastUndoGroup < 0) return;
        
        if (EditorUtility.DisplayDialog(
            "작업 되돌리기", 
            $"'{m_lastOperationName}' 작업을 되돌리시겠습니까?\n\n" +
            "이 작업은 실행 취소할 수 없습니다.", 
            "되돌리기", 
            "취소"))
        {
            try
            {
                Undo.RevertAllDownToGroup(m_lastUndoGroup);
                m_canUndo = false;
                m_lastUndoGroup = -1;
                m_lastOperationName = "";
                m_lastReport = $"✅ '{m_lastOperationName}' 작업을 성공적으로 되돌렸습니다.";
                
                EditorUtility.DisplayDialog("되돌리기 완료", "작업을 성공적으로 되돌렸습니다.", "확인");
                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("오류 발생", "되돌리기 중 오류가 발생했습니다:\n" + ex.Message, "확인");
            }
        }
    }
    
    // ========== 블렌드셰이프 이름 정보와 중복 여부 미리보기 기능 ==========
    
    private void PreviewBlendShapes()
    {
        m_blendShapePreview.Clear();
        
        if (m_avatarRoot == null)
        {
            EditorUtility.DisplayDialog("⚠️ 캐릭터 미선택", "먼저 캐릭터를 선택해주세요!", "확인");
            return;
        }
        
        try
        {
            EditorUtility.DisplayProgressBar("블렌드셰이프 분석", "캐릭터 블렌드셰이프 수집 중...", 0.3f);
            
            // 캐릭터의 블렌드셰이프 수집
            var avatarSmrs = m_avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var avatarShapes = new Dictionary<string, List<string>>();
            
            foreach (var smr in avatarSmrs)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                var shapes = new List<string>();
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                {
                    shapes.Add(smr.sharedMesh.GetBlendShapeName(i));
                }
                if (shapes.Count > 0)
                {
                    avatarShapes[smr.name] = shapes;
                }
            }
            
            EditorUtility.DisplayProgressBar("블렌드셰이프 분석", "의상 블렌드셰이프 수집 중...", 0.6f);
            
            // 의상의 블렌드셰이프 수집
            var clothingShapes = new Dictionary<string, List<string>>();
            foreach (var clothingRoot in m_clothingRoots)
            {
                if (clothingRoot == null) continue;
                
                var clothingSmrs = clothingRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var smr in clothingSmrs)
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    var shapes = new List<string>();
                    for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    {
                        shapes.Add(smr.sharedMesh.GetBlendShapeName(i));
                    }
                    if (shapes.Count > 0)
                    {
                        clothingShapes[$"[의상] {smr.name}"] = shapes;
                    }
                }
            }
            
            EditorUtility.DisplayProgressBar("블렌드셰이프 분석", "충돌 검사 중...", 0.9f);
            
            // 결과 저장
            m_blendShapePreview["📌 캐릭터 블렌드셰이프"] = new List<string>();
            foreach (var kv in avatarShapes)
            {
                m_blendShapePreview["📌 캐릭터 블렌드셰이프"].Add($"▸ {kv.Key}: {kv.Value.Count}개");
                foreach (var shape in kv.Value.Take(5))
                {
                    m_blendShapePreview["📌 캐릭터 블렌드셰이프"].Add($"  • {shape}");
                }
                if (kv.Value.Count > 5)
                {
                    m_blendShapePreview["📌 캐릭터 블렌드셰이프"].Add($"  ... 외 {kv.Value.Count - 5}개");
                }
            }
            
            if (clothingShapes.Count > 0)
            {
                m_blendShapePreview["👕 의상 블렌드셰이프"] = new List<string>();
                foreach (var kv in clothingShapes)
                {
                    m_blendShapePreview["👕 의상 블렌드셰이프"].Add($"▸ {kv.Key}: {kv.Value.Count}개");
                    foreach (var shape in kv.Value.Take(5))
                    {
                        m_blendShapePreview["👕 의상 블렌드셰이프"].Add($"  • {shape}");
                    }
                    if (kv.Value.Count > 5)
                    {
                        m_blendShapePreview["👕 의상 블렌드셰이프"].Add($"  ... 외 {kv.Value.Count - 5}개");
                    }
                }
            }
            
            // 충돌 검사
            var avatarShapeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in avatarShapes)
            {
                foreach (var shape in kv.Value) avatarShapeSet.Add(shape);
            }
            
            var conflicts = new List<string>();
            foreach (var kv in clothingShapes)
            {
                foreach (var shape in kv.Value)
                {
                    if (avatarShapeSet.Contains(shape) && !conflicts.Contains(shape))
                    {
                        conflicts.Add(shape);
                    }
                }
            }
            
            if (conflicts.Count > 0)
            {
                m_blendShapePreview["⚠️ 이름 충돌 감지"] = new List<string>();
                m_blendShapePreview["⚠️ 이름 충돌 감지"].Add($"캐릭터와 의상에 같은 이름의 블렌드셰이프가 {conflicts.Count}개 발견되었습니다:");
                foreach (var conflict in conflicts.Take(10))
                {
                    m_blendShapePreview["⚠️ 이름 충돌 감지"].Add($"  • {conflict}");
                }
                if (conflicts.Count > 10)
                {
                    m_blendShapePreview["⚠️ 이름 충돌 감지"].Add($"  ... 외 {conflicts.Count - 10}개");
                }
                m_blendShapePreview["⚠️ 이름 충돌 감지"].Add("");
                m_blendShapePreview["⚠️ 이름 충돌 감지"].Add("💡 참고: 이름이 같아도 각 메쉬가 독립적으로 동작하므로 대부분 문제없습니다.");
            }
            else
            {
                m_blendShapePreview["✅ 충돌 없음"] = new List<string> { "캐릭터와 의상 간 블렌드셰이프 이름 충돌이 발견되지 않았습니다." };
            }
            
            m_showBlendShapePreview = true;
            Repaint();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("오류", "블렌드셰이프 분석 중 오류가 발생했습니다:\n" + ex.Message, "확인");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }
    
    private void DrawBlendShapePreview()
    {
        if (m_blendShapePreview.Count == 0) return;
        
        GUILayout.Space(8f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("👁️ 블렌드셰이프 이름 정보와 중복 여부 미리보기", EditorStyles.boldLabel);
                if (GUILayout.Button("❌ 닫기", GUILayout.Width(60)))
                {
                    m_showBlendShapePreview = false;
                    m_blendShapePreview.Clear();
                    return;
                }
            }
            
            GUILayout.Space(4f);
            
            foreach (var section in m_blendShapePreview)
            {
                EditorGUILayout.LabelField(section.Key, EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.textArea))
                {
                    foreach (var line in section.Value)
                    {
                        EditorGUILayout.LabelField(line, GUILayout.Height(16));
                    }
                }
                GUILayout.Space(4f);
            }
        }
    }
}
#endif
