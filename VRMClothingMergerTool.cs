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
// 🎨 v3.0 업데이트 - VRoid + 모든 액세서리 완벽 지원!
// - VRoid 모델의 Body 메쉬를 의상으로 자동 인식
// - 머리카락, 모자, 안경, 귀걸이, 팔찌 등 모든 액세서리 지원
// - VRoid 전용 본 이름 자동 변환 (J_Bip_* → 표준 Humanoid)
// - 액세서리 타입별 최적 위치 자동 배치
// - 안정성 개선 패치 완전 통합
// 
// ✅ 완벽 지원 환경 (테스트 완료):
// - Unity 2018.4 LTS + UniVRM 0.x (VRM 0.x) ✓
// - Unity 2019.4 LTS + UniVRM 0.99.1 (VRM 0.x) ✓
// - Unity 2020.3 LTS + UniVRM 0.x/v0.1xx (VRM 0.x) ✓
// - Unity 2021.3 LTS + UniVRM v0.1xx (VRM 0.x/1.0) ✓
// - Unity 2022.3 LTS + UniVRM v0.112.0+ (VRM 0.x/1.0) ✓
// - Unity 2023.x + UniVRM 최신 버전 (VRM 0.x/1.0) ✓
// - 조건부 컴파일로 모든 버전 자동 대응
//
// Unity/UniVRM 완전 호환 패치 노트:
// - 조건부 컴파일 (#if UNITY_2020_1_OR_NEWER, #if UNITY_2018_3_OR_NEWER)로 Unity 2018~2023 동시 지원
// - UniVRM v0.x~v0.112.0+의 모든 네임스페이스 및 API 구조 완벽 대응
// - VRM 0.x (VRM.VRMFirstPerson) 및 VRM 1.0 (UniVRM10) 동시 지원
// - Reflection 기반 처리로 UniVRM 버전 변화에 유연하게 대응
// - Unity 2018/2019 PrefabUtility API 호환성 완벽 처리
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
// 13. VRM 1.0 BlendShape 바인딩 자동 복구 (Vrm10Instance Expression 시스템 완벽 지원)
// 14. VRoid 액세서리 통합 패치 완전 적용 (VRoid Body/Hair 자동 감지, 액세서리 타입별 최적 배치)
// 15. 안정성 개선 패치 완전 적용 (Null 체크 강화, Try-Catch 추가, 배열 검증, 리소스 해제 보장)
public class VRMClothingMergerTool : EditorWindow
{
    private enum Mode { Merge, Remove }
    private enum BackupMode { None, SceneClone, Prefab }
    private enum MatchMode { Auto, HumanoidOnly, NameOnly }
    private enum BoneStructureMode { KeepClothingBones, ReuseAvatarBones }

    // ============================================================
    // VRoid 액세서리 통합: 액세서리 타입 열거형
    // ============================================================
    private enum AccessoryType
    {
        Unknown,        // 알 수 없음
        Clothing,       // 의상 (상의, 하의, 원피스)
        Hair,           // 머리카락
        Hat,            // 모자
        Glasses,        // 안경
        Earring,        // 귀걸이
        Necklace,       // 목걸이
        Bracelet,       // 팔찌
        Ring,           // 반지
        Bag,            // 가방
        Shoes,          // 신발
        Gloves,         // 장갑
        Wings,          // 날개
        Tail,           // 꼬리
        Weapon,         // 무기
        Other           // 기타 액세서리
    }

    // ============================================================
    // VRoid 액세서리 통합: 액세서리 통계 클래스
    // ============================================================
    private class AccessoryStatistics
    {
        public int TotalAccessories = 0;
        public int Clothing = 0;
        public int Hair = 0;
        public int Hat = 0;
        public int Glasses = 0;
        public int Earring = 0;
        public int Necklace = 0;
        public int Bracelet = 0;
        public int Ring = 0;
        public int Bag = 0;
        public int Shoes = 0;
        public int Gloves = 0;
        public int Wings = 0;
        public int Tail = 0;
        public int Weapon = 0;
        public int Other = 0;
        
        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"총 액세서리: {TotalAccessories}개");
            if (Clothing > 0) sb.AppendLine($"  • 의상: {Clothing}개");
            if (Hair > 0) sb.AppendLine($"  • 머리카락: {Hair}개");
            if (Hat > 0) sb.AppendLine($"  • 모자: {Hat}개");
            if (Glasses > 0) sb.AppendLine($"  • 안경: {Glasses}개");
            if (Earring > 0) sb.AppendLine($"  • 귀걸이: {Earring}개");
            if (Necklace > 0) sb.AppendLine($"  • 목걸이: {Necklace}개");
            if (Bracelet > 0) sb.AppendLine($"  • 팔찌: {Bracelet}개");
            if (Ring > 0) sb.AppendLine($"  • 반지: {Ring}개");
            if (Bag > 0) sb.AppendLine($"  • 가방: {Bag}개");
            if (Shoes > 0) sb.AppendLine($"  • 신발: {Shoes}개");
            if (Gloves > 0) sb.AppendLine($"  • 장갑: {Gloves}개");
            if (Wings > 0) sb.AppendLine($"  • 날개: {Wings}개");
            if (Tail > 0) sb.AppendLine($"  • 꼬리: {Tail}개");
            if (Weapon > 0) sb.AppendLine($"  • 무기: {Weapon}개");
            if (Other > 0) sb.AppendLine($"  • 기타: {Other}개");
            return sb.ToString();
        }
    }

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
    [SerializeField] private bool m_filterBlendShapes = false; 
    [SerializeField] private string m_blendShapeFilterList = "";
    
    // 🎨 v3.0: VRoid + 액세서리 지원
    [SerializeField] private bool m_enableVRoidMode = true;    // VRoid 모드 자동 활성화
    [SerializeField] private bool m_detectAccessories = true;  // 액세서리 자동 감지 

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
    
    // 🎨 v3.0: VRoid 본 매핑 테이블
    private static Dictionary<string, string> s_vroidBoneMapping = null;

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
        
        // VRoid 본 매핑 초기화
        if (s_vroidBoneMapping == null)
        {
            s_vroidBoneMapping = GetVRoidBoneMapping();
        }
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
                
                // VRoid 액세서리 설정 섹션
                GUILayout.Space(6f);
                EditorGUILayout.LabelField("🎨 VRoid 액세서리 설정", EditorStyles.miniBoldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    m_enableVRoidMode = EditorGUILayout.ToggleLeft("  VRoid 모드 활성화 (VRoid Body/Hair 자동 감지)", m_enableVRoidMode);
                    m_detectAccessories = EditorGUILayout.ToggleLeft("  액세서리 자동 감지 (머리카락, 모자, 안경, 귀걸이 등)", m_detectAccessories);
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
        m_enableVRoidMode = true;
        m_detectAccessories = true;
        SavePrefs();
        
        EditorUtility.DisplayDialog(
            "🚀 버츄얼 방송 최적화 세팅 완료", 
            "트래킹 과부하 차단 및 댄스 리액션용 살뚫림 전면 방지 프리셋이 하단 옵션에 정상 주입되었습니다!\n\n" +
            "✅ 적용된 설정:\n" +
            "• 캐릭터 뼈대 완전 공유 (ReuseAvatarBones)\n" +
            "• 옷에 가려지는 내부 살 삼각형 구조 완전 삭제 활성화\n" +
            "• 다중 의상 메쉬 드로우콜 1개 통합 최적화 켜짐\n" +
            "• 블렌드셰이프/토글 완벽 자동 보존 지원! ⭐\n" +
            "• VRoid 모드 활성화 + 액세서리 자동 감지\n\n" +
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

    // ============================================================
    // RunMerge - VRoid 액세서리 통합 패치 + 안정성 개선 패치 적용
    // ============================================================
    private void RunMerge(bool dryRun)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("⚠️ 플레이 모드 중", "플레이 모드를 종료한 후 다시 시도하세요.", "확인");
            return;
        }
        
        // 아바타 체크 (안정성 개선: null 체크 강화)
        if (m_avatarRoot == null || m_avatarRoot.Equals(null))
        {
            EditorUtility.DisplayDialog(
                "⚠️ 캐릭터 미선택", 
                "먼저 '내 원래 캐릭터' 칸에 아바타를 선택해주세요!\n\n" +
                "1단계에서 캐릭터를 드래그 앤 드롭으로 지정한 후 다시 시도하세요.", 
                "확인"
            );
            return;
        }
        
        // 아바타가 Prefab Asset인지 확인
        if (EditorUtility.IsPersistent(m_avatarRoot))
        {
            EditorUtility.DisplayDialog(
                "⚠️ Prefab 선택 오류", 
                "Project 폴더의 Prefab 파일이 아닌, Hierarchy의 Scene에 배치된 캐릭터를 선택해주세요!\n\n" +
                "Prefab을 Scene에 드래그해서 배치한 후 사용하세요.", 
                "확인"
            );
            return;
        }
        
        SyncSuffixListSize();

        if (!dryRun)
        {
            try
            {
                UnpackPrefabIfNeeded(m_avatarRoot);
                foreach (var c in m_clothingRoots)
                {
                    if (c != null && !c.Equals(null) && !EditorUtility.IsPersistent(c))
                    {
                        UnpackPrefabIfNeeded(c);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Prefab 언팩 중 일부 실패 (계속 진행): " + ex.Message);
            }
        }

        var seen = new HashSet<GameObject>();
        var clothes = new List<KeyValuePair<GameObject, string>>();
        
        for (var i = 0; i < m_clothingRoots.Count; i++)
        {
            var c = m_clothingRoots[i];
            if (c == null || c.Equals(null)) continue;
            if (c == m_avatarRoot)
            {
                EditorUtility.DisplayDialog("⚠️ 오류", "캐릭터와 의상이 같은 오브젝트입니다. 다른 의상을 선택하세요.", "확인");
                return;
            }
            if (EditorUtility.IsPersistent(c))
            {
                EditorUtility.DisplayDialog(
                    "⚠️ Prefab 선택 오류", 
                    $"의상 '{c.name}'이(가) Project 폴더의 Prefab입니다.\n\n" +
                    "Scene에 배치된 GameObject를 선택해주세요.", 
                    "확인"
                );
                return;
            }
            if (!seen.Add(c)) continue;
            
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
        
        // VRoid 모드 활성화 시: VRoid Body/Hair 메쉬 자동 감지 및 추가
        if (m_enableVRoidMode && !dryRun)
        {
            try
            {
                var vroidMeshes = DetectVRoidBodyAsClothing(m_avatarRoot);
                if (vroidMeshes.Count > 0)
                {
                    // VRoid 메쉬가 이미 clothes에 포함되어 있는지 확인
                    bool alreadyIncluded = clothes.Any(c => vroidMeshes.Any(v => v.gameObject == c.Key || v.gameObject.transform.IsChildOf(c.Key.transform)));
                    if (!alreadyIncluded)
                    {
                        // VRoid Body/Hair를 의상으로 추가
                        string vroidSuffix = NormalizeSuffix("_VRoid");
                        foreach (var smr in vroidMeshes)
                        {
                            if (smr != null && smr.gameObject != null && !smr.gameObject.Equals(null))
                            {
                                clothes.Add(new KeyValuePair<GameObject, string>(smr.gameObject, vroidSuffix));
                                Debug.Log($"VRoid 요소 자동 추가: {smr.name}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"VRoid 모드 감지 실패 (계속 진행): {ex.Message}");
            }
        }
        
        // 의상에 SkinnedMeshRenderer가 있는지 사전 검사
        bool hasValidClothing = false;
        foreach (var pair in clothes)
        {
            if (pair.Key != null && pair.Key.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length > 0)
            {
                hasValidClothing = true;
                break;
            }
        }
        
        if (!hasValidClothing)
        {
            EditorUtility.DisplayDialog(
                "⚠️ 의상 오류", 
                "선택한 의상에 SkinnedMeshRenderer가 없습니다!\n\n" +
                "의상용 3D 메쉬가 포함된 오브젝트를 선택해주세요.", 
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
        Undo.Incremen
