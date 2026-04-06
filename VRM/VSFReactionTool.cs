#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRM;
using VSeeFace;

public sealed class VSFReactionTool : EditorWindow
{
    private const string GeneratedFolderPath = "Assets/VSF Generated";
    private const float MinimumClipSeconds = 1f / 60f;
    private const string ToolTitle = "VSF Reaction Tool";

    private Vector2 scrollPosition;
    private GameObject targetObject;
    private SimpleReactionConfig config = new SimpleReactionConfig();

    private enum TriggerJoinMode
    {
        Any,
        All,
    }

    private sealed class SimpleReactionConfig
    {
        public string firstTriggerName = "eyeBlinkLeft";
        public string secondTriggerName = "eyeBlinkRight";
        public bool useSecondTrigger = true;
        public TriggerJoinMode joinMode = TriggerJoinMode.Any;
        public float enterThreshold = 0.6f;
        public AnimationClip reactionSourceClip;
    }

    [MenuItem("Tools/VRM/VSF에서 반응형 애니메이션 설정하는 툴")]
    // 메뉴에서 에디터 창을 여는 시작점입니다.
    private static void OpenWindow()
    {
        var window = GetWindow<VSFReactionTool>(ToolTitle);
        window.minSize = new Vector2(500f, 460f);
        window.Show();
    }

    // 입력값을 검사한 뒤 프리팹 또는 씬 오브젝트에 설정을 적용합니다.
    private static void ApplyReaction(GameObject selected, SimpleReactionConfig config)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog(ToolTitle, "Play Mode에서는 실행하지 마세요.", "OK");
            return;
        }

        if (selected == null)
        {
            throw new InvalidOperationException("적용할 오브젝트를 선택하세요.");
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        config.firstTriggerName = (config.firstTriggerName ?? string.Empty).Trim();
        config.secondTriggerName = (config.secondTriggerName ?? string.Empty).Trim();
        config.enterThreshold = Mathf.Clamp01(config.enterThreshold);

        if (string.IsNullOrEmpty(config.firstTriggerName))
        {
            throw new InvalidOperationException("감지할 블렌드쉐입 이름을 입력하세요.");
        }

        if (config.useSecondTrigger && string.IsNullOrEmpty(config.secondTriggerName))
        {
            throw new InvalidOperationException("추가 블렌드쉐입 이름을 입력하세요.");
        }

        if (config.useSecondTrigger
            && string.Equals(config.firstTriggerName, config.secondTriggerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("두 블렌드쉐입 이름은 서로 다르게 입력하세요.");
        }

        if (config.reactionSourceClip == null)
        {
            throw new InvalidOperationException("재생할 애니메이션을 넣어주세요.");
        }

        var prefabPath = AssetDatabase.GetAssetPath(selected);
        if (EditorUtility.IsPersistent(selected)
            && !string.IsNullOrEmpty(prefabPath)
            && prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            ApplyToPrefabAsset(prefabPath, config);
            return;
        }

        ApplyToSceneOrPrefabObject(selected, config);
    }

    // 프리팹 에셋을 직접 열어서 반응형 애니메이션 설정을 저장합니다.
    private static void ApplyToPrefabAsset(string prefabPath, SimpleReactionConfig config)
    {
        if (!File.Exists(prefabPath))
        {
            EditorUtility.DisplayDialog(ToolTitle, $"프리팹을 찾을 수 없습니다.\n{prefabPath}", "OK");
            return;
        }

        var succeeded = false;
        var root = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            ApplySetup(root, config, GeneratedFolderPath);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            succeeded = true;
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(ToolTitle, ex.Message, "OK");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        if (succeeded)
        {
            EditorUtility.DisplayDialog(ToolTitle, $"적용 완료:\n{prefabPath}", "OK");
        }
    }

    // 씬에 있는 오브젝트나 프리팹 인스턴스에 설정을 적용합니다.
    private static void ApplyToSceneOrPrefabObject(GameObject selected, SimpleReactionConfig config)
    {
        try
        {
            ApplySetup(selected, config, GeneratedFolderPath);
            RecordObjectChanges(selected);
            EditorUtility.DisplayDialog(ToolTitle, $"적용 완료:\n{selected.name}", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(ToolTitle, ex.Message, "OK");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // 아바타에 필요한 컴포넌트, 클립, 컨트롤러를 연결하는 핵심 적용 로직입니다.
    private static void ApplySetup(GameObject root, SimpleReactionConfig config, string generatedFolder)
    {
        if (root == null)
        {
            throw new ArgumentNullException(nameof(root));
        }

        if (root.GetComponent<VRMBlendShapeProxy>() == null)
        {
            throw new InvalidOperationException("VRMBlendShapeProxy가 붙은 아바타 루트를 넣어주세요.");
        }

        EnsureFolder(generatedFolder);

        var rootAnimator = root.GetComponent<Animator>();
        if (rootAnimator == null)
        {
            throw new InvalidOperationException("루트에 Animator가 없습니다.");
        }

        var vsfAnimations = root.GetComponent<VSF_Animations>();
        if (vsfAnimations == null)
        {
            vsfAnimations = root.AddComponent<VSF_Animations>();
        }

        var animationRoot = root.transform.Find("Body") ?? root.transform;
        var bodyAnimator = animationRoot.GetComponent<Animator>();
        if (bodyAnimator == null)
        {
            bodyAnimator = animationRoot.gameObject.AddComponent<Animator>();
        }

        if (bodyAnimator != rootAnimator && bodyAnimator.avatar == null && rootAnimator.avatar != null)
        {
            bodyAnimator.avatar = rootAnimator.avatar;
        }

        var driverRoot = GetOrCreateChild(root.transform, "VSF Reaction Drivers");
        var firstDriver = EnsureFloatDriver(driverRoot, "Trigger 1 Driver", bodyAnimator, "Trigger1");
        var firstDriverClipPath = $"{generatedFolder}/VSF_ReactionTrigger1.anim";
        var secondDriverClipPath = $"{generatedFolder}/VSF_ReactionTrigger2.anim";
        var reactionClipPath = $"{generatedFolder}/VSF_Reaction.anim";
        var controllerPath = $"{generatedFolder}/VSF_Reaction.controller";

        var firstDriverClip = CreateOrUpdateDriverClip(firstDriverClipPath, GetRelativePath(root.transform, firstDriver.transform));

        VSF_SetAnimatorFloat secondDriver = null;
        AnimationClip secondDriverClip = null;
        if (config.useSecondTrigger)
        {
            secondDriver = EnsureFloatDriver(driverRoot, "Trigger 2 Driver", bodyAnimator, "Trigger2");
            secondDriverClip = CreateOrUpdateDriverClip(secondDriverClipPath, GetRelativePath(root.transform, secondDriver.transform));
        }
        else
        {
            RemoveChildIfExists(driverRoot, "Trigger 2 Driver");
        }

        var bodyPath = animationRoot == root.transform ? string.Empty : animationRoot.name;
        var reactionClip = CreateOrUpdateReactionClip(reactionClipPath, config.reactionSourceClip, bodyPath);
        var controller = CreateOrUpdateController(controllerPath, reactionClip, config);

        bodyAnimator.runtimeAnimatorController = controller;

        RemoveGeneratedDriverEntries(vsfAnimations, firstDriverClipPath, secondDriverClipPath);
        UpsertBlendshapeDriver(vsfAnimations, config.firstTriggerName, firstDriverClip);
        if (config.useSecondTrigger)
        {
            UpsertBlendshapeDriver(vsfAnimations, config.secondTriggerName, secondDriverClip);
        }

        EditorUtility.SetDirty(firstDriver);
        if (secondDriver != null)
        {
            EditorUtility.SetDirty(secondDriver);
        }
        EditorUtility.SetDirty(bodyAnimator);
        EditorUtility.SetDirty(vsfAnimations);
        EditorUtility.SetDirty(root);
    }

    // 씬 오브젝트나 프리팹 인스턴스의 변경 사항을 Unity에 기록합니다.
    private static void RecordObjectChanges(GameObject root)
    {
        if (root == null)
        {
            return;
        }

        EditorUtility.SetDirty(root);
        if (PrefabUtility.IsPartOfPrefabInstance(root))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(root);
        }

        if (root.scene.IsValid())
        {
            EditorSceneManager.MarkSceneDirty(root.scene);
        }
    }

    // 에디터 창 UI를 그리고 사용자가 입력한 값을 받습니다.
    private void OnGUI()
    {
        var blendShapeOptions = GetBlendShapeOptions();
        var hasBlendShapeOptions = blendShapeOptions.Length > 0;
        var canApply = targetObject != null && hasBlendShapeOptions && config.reactionSourceClip != null;
        var previousLabelWidth = EditorGUIUtility.labelWidth;
        var previousWideMode = EditorGUIUtility.wideMode;
        var bodyStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            richText = false,
        };

        EditorGUIUtility.labelWidth = 145f;
        EditorGUIUtility.wideMode = true;

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        GUILayout.Space(6f);
        EditorGUILayout.LabelField("반응형 애니메이션 설정", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("블렌드쉐입 값이 들어오면 바로 애니메이션 재생", MessageType.Info);

        DrawSectionStart("아바타 선택");
        EditorGUILayout.LabelField("VRMBlendShapeProxy와 BlendShapeAvatar가 붙은 아바타 루트 넣기", bodyStyle);
        GUILayout.Space(4f);
        targetObject = (GameObject)EditorGUILayout.ObjectField("아바타 오브젝트", targetObject, typeof(GameObject), true);

        if (!hasBlendShapeOptions)
        {
            EditorGUILayout.HelpBox("상위나 하위가 아니라, BlendShapeAvatar가 붙은 아바타 루트를 직접 넣어주세요.", MessageType.Warning);
        }
        DrawSectionEnd();

        GUILayout.Space(6f);
        DrawSectionStart("블렌드쉐입 설정");
        EditorGUILayout.LabelField("기준이 될 표정과 필요하면 추가 표정을 선택", bodyStyle);
        GUILayout.Space(4f);
        using (new EditorGUI.DisabledScope(!hasBlendShapeOptions))
        {
            config.firstTriggerName = DrawBlendShapePopup("기준 블렌드쉐입", config.firstTriggerName, blendShapeOptions);
        }

        config.useSecondTrigger = EditorGUILayout.ToggleLeft("추가 블렌드쉐입도 같이 보기", config.useSecondTrigger);

        using (new EditorGUI.DisabledScope(!config.useSecondTrigger || !hasBlendShapeOptions))
        {
            config.secondTriggerName = DrawBlendShapePopup("추가 블렌드쉐입", config.secondTriggerName, blendShapeOptions);
            config.joinMode = (TriggerJoinMode)EditorGUILayout.EnumPopup("발동 방식", config.joinMode);
        }

        if (config.useSecondTrigger)
        {
            EditorGUILayout.HelpBox("Any: 둘 중 하나만 기준값을 넘으면 발동\nAll: 둘 다 기준값을 넘어야 발동", MessageType.None);
        }
        DrawSectionEnd();

        GUILayout.Space(6f);
        DrawSectionStart("동작 설정");
        EditorGUILayout.LabelField("값이 들어오기 시작하는 기준과 재생할 애니메이션 설정", bodyStyle);
        GUILayout.Space(4f);
        config.enterThreshold = EditorGUILayout.FloatField("시작으로 보는 값", config.enterThreshold);
        config.reactionSourceClip = (AnimationClip)EditorGUILayout.ObjectField("재생할 애니메이션", config.reactionSourceClip, typeof(AnimationClip), false);

        if (config.reactionSourceClip == null)
        {
            EditorGUILayout.HelpBox("재생할 애니메이션은 직접 넣어주세요.", MessageType.Warning);
        }

        EditorGUILayout.HelpBox($"현재 설정\n값이 {config.enterThreshold:0.##} 이상이면 바로 실행합니다.", MessageType.None);
        DrawSectionEnd();

        GUILayout.Space(8f);
        using (new EditorGUI.DisabledScope(!canApply))
        {
            if (GUILayout.Button("적용", GUILayout.Height(36f)))
            {
                Apply();
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUIUtility.labelWidth = previousLabelWidth;
        EditorGUIUtility.wideMode = previousWideMode;
    }

    // 각 UI 구역의 시작 박스를 그립니다.
    private static void DrawSectionStart(string title)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        GUILayout.Space(2f);
    }

    // 각 UI 구역의 박스를 닫습니다.
    private static void DrawSectionEnd()
    {
        EditorGUILayout.EndVertical();
    }

    // 선택한 아바타의 BlendShapeAvatar에서 블렌드쉐입 이름 목록을 가져옵니다.
    private string[] GetBlendShapeOptions()
    {
        if (targetObject == null)
        {
            return Array.Empty<string>();
        }

        var proxy = targetObject.GetComponent<VRMBlendShapeProxy>();
        var clips = proxy != null ? proxy.BlendShapeAvatar : null;
        if (clips == null || clips.Clips == null)
        {
            return Array.Empty<string>();
        }

        return clips.Clips
            .Where(clip => clip != null && !string.IsNullOrEmpty(clip.Key.Name))
            .Select(clip => clip.Key.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

            // 현재 값에 맞는 블렌드쉐입 드롭다운을 그립니다.
    private string DrawBlendShapePopup(string label, string currentValue, string[] options)
    {
        var currentIndex = Array.FindIndex(options, option => string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var nextIndex = EditorGUILayout.Popup(label, currentIndex, options);
        return options.Length > 0 ? options[nextIndex] : currentValue;
    }

    // 창에서 입력한 설정값으로 실제 적용 함수를 호출합니다.
    private void Apply()
    {
        try
        {
            ApplyReaction(targetObject, config);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog(ToolTitle, ex.Message, "OK");
        }
    }

    // Animator 파라미터에 값을 넣어줄 VSF_SetAnimatorFloat 드라이버를 준비합니다.
    private static VSF_SetAnimatorFloat EnsureFloatDriver(Transform parent, string objectName, Animator targetAnimator, string parameterName)
    {
        var child = GetOrCreateChild(parent, objectName);
        var driver = child.GetComponent<VSF_SetAnimatorFloat>() ?? child.gameObject.AddComponent<VSF_SetAnimatorFloat>();
        driver.targetAnimator = targetAnimator;
        driver.parameterName = parameterName;
        driver.parameterValue = 0f;
        return driver;
    }

    // 블렌드쉐입 값을 Animator 파라미터로 보내는 짧은 드라이버 클립을 만듭니다.
    private static AnimationClip CreateOrUpdateDriverClip(string assetPath, string relativePath)
    {
        var clip = LoadOrCreateClip(assetPath);
        ClearClipCurves(clip);
        var binding = EditorCurveBinding.FloatCurve(relativePath, typeof(VSF_SetAnimatorFloat), "parameterValue");
        var curve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(MinimumClipSeconds, 1f));
        AnimationUtility.SetEditorCurve(clip, binding, curve);
        SetClipLoop(clip, false);
        SetClipLength(clip, MinimumClipSeconds);
        EditorUtility.SetDirty(clip);
        return clip;
    }

    // 사용자가 고른 원본 애니메이션을 현재 아바타 구조에 맞게 복사합니다.
    private static AnimationClip CreateOrUpdateReactionClip(string assetPath, AnimationClip sourceClip, string bodyPath)
    {
        var clip = LoadOrCreateClip(assetPath);
        ClearClipCurves(clip);

        foreach (var binding in AnimationUtility.GetCurveBindings(sourceClip))
        {
            var rebound = binding;
            rebound.path = RebasePath(binding.path, bodyPath);
            AnimationUtility.SetEditorCurve(clip, rebound, AnimationUtility.GetEditorCurve(sourceClip, binding));
        }

        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(sourceClip))
        {
            var rebound = binding;
            rebound.path = RebasePath(binding.path, bodyPath);
            AnimationUtility.SetObjectReferenceCurve(clip, rebound, AnimationUtility.GetObjectReferenceCurve(sourceClip, binding));
        }

        AnimationUtility.SetAnimationEvents(clip, AnimationUtility.GetAnimationEvents(sourceClip));
        SetClipLoop(clip, false);
        EditorUtility.SetDirty(clip);
        return clip;
    }

    // Trigger 값이 들어오면 바로 Play로 들어가는 Animator Controller를 만듭니다.
    private static AnimatorController CreateOrUpdateController(string assetPath, AnimationClip reactionClip, SimpleReactionConfig config)
    {
        AssetDatabase.DeleteAsset(assetPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
        controller.AddParameter("Trigger1", AnimatorControllerParameterType.Float);
        if (config.useSecondTrigger)
        {
            controller.AddParameter("Trigger2", AnimatorControllerParameterType.Float);
        }

        var stateMachine = controller.layers[0].stateMachine;
        foreach (var childState in stateMachine.states.ToArray())
        {
            stateMachine.RemoveState(childState.state);
        }

        var idle = stateMachine.AddState("Idle", new Vector3(200f, 120f, 0f));
        var play = stateMachine.AddState("Play", new Vector3(460f, 120f, 0f));

        idle.writeDefaultValues = true;
        play.motion = reactionClip;
        play.writeDefaultValues = true;
        stateMachine.defaultState = idle;

        if (!config.useSecondTrigger || config.joinMode == TriggerJoinMode.Any)
        {
            var enterFirst = idle.AddTransition(play);
            ConfigureImmediateTransition(enterFirst);
            enterFirst.AddCondition(AnimatorConditionMode.Greater, config.enterThreshold, "Trigger1");

            if (config.useSecondTrigger)
            {
                var enterSecond = idle.AddTransition(play);
                ConfigureImmediateTransition(enterSecond);
                enterSecond.AddCondition(AnimatorConditionMode.Greater, config.enterThreshold, "Trigger2");
            }
        }
        else
        {
            var enterAll = idle.AddTransition(play);
            ConfigureImmediateTransition(enterAll);
            enterAll.AddCondition(AnimatorConditionMode.Greater, config.enterThreshold, "Trigger1");
            enterAll.AddCondition(AnimatorConditionMode.Greater, config.enterThreshold, "Trigger2");
        }

        ConfigureExitTransition(play.AddTransition(idle));

        EditorUtility.SetDirty(controller);
        return controller;
    }

    // VSF_Animations 목록에 블렌드쉐입용 드라이버 클립을 추가하거나 교체합니다.
    private static void UpsertBlendshapeDriver(VSF_Animations vsfAnimations, string blendshapeName, AnimationClip clip)
    {
        var list = vsfAnimations.animations != null
            ? new List<VSF_Animations.BlendshapeAnimPair>(vsfAnimations.animations)
            : new List<VSF_Animations.BlendshapeAnimPair>();

        list.RemoveAll(x => string.Equals(x.blendshapeName, blendshapeName, StringComparison.OrdinalIgnoreCase));
        list.Add(new VSF_Animations.BlendshapeAnimPair(blendshapeName, clip, false));
        vsfAnimations.animations = list.ToArray();
    }

    // 이전에 생성했던 드라이버 항목을 VSF_Animations 목록에서 제거합니다.
    private static void RemoveGeneratedDriverEntries(VSF_Animations vsfAnimations, params string[] generatedClipPaths)
    {
        if (vsfAnimations == null || vsfAnimations.animations == null || generatedClipPaths == null || generatedClipPaths.Length == 0)
        {
            return;
        }

        var pathSet = new HashSet<string>(generatedClipPaths, StringComparer.OrdinalIgnoreCase);
        vsfAnimations.animations = vsfAnimations.animations
            .Where(x =>
            {
                var clipPath = AssetDatabase.GetAssetPath(x.animation)?.Replace('\\', '/');
                return string.IsNullOrEmpty(clipPath) || !pathSet.Contains(clipPath);
            })
            .ToArray();
    }

            // 이름으로 자식 오브젝트를 찾고, 없으면 새로 만듭니다.
    private static Transform GetOrCreateChild(Transform parent, string childName)
    {
        var child = parent.Find(childName);
        if (child != null)
        {
            return child;
        }

        var transform = new GameObject(childName).transform;
        transform.SetParent(parent, false);
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one;
        return transform;
    }

    // 더 이상 필요 없는 자식 오브젝트가 있으면 삭제합니다.
    private static void RemoveChildIfExists(Transform parent, string childName)
    {
        var child = parent.Find(childName);
        if (child != null)
        {
            UnityEngine.Object.DestroyImmediate(child.gameObject);
        }
    }

    // 루트 기준으로 애니메이션 바인딩에 쓸 상대 경로를 계산합니다.
    private static string GetRelativePath(Transform root, Transform target)
    {
        if (target == root)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (var current = target; current != null && current != root; current = current.parent)
        {
            parts.Add(current.name);
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    // 생성 파일을 저장할 폴더가 없으면 만들어 둡니다.
    private static void EnsureFolder(string folderPath)
    {
        var parts = folderPath.Split('/');
        var current = parts[0];
        for (var index = 1; index < parts.Length; index++)
        {
            var next = parts[index];
            var candidate = current + "/" + next;
            if (!AssetDatabase.IsValidFolder(candidate))
            {
                AssetDatabase.CreateFolder(current, next);
            }
            current = candidate;
        }
    }

    // 애니메이션 클립을 불러오고, 없으면 새로 생성합니다.
    private static AnimationClip LoadOrCreateClip(string assetPath)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
        if (clip != null)
        {
            return clip;
        }

        clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(assetPath) };
        AssetDatabase.CreateAsset(clip, assetPath);
        AssetDatabase.ImportAsset(assetPath);
        return AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
    }

    // 기존에 들어 있던 곡선과 이벤트를 비워서 다시 생성할 준비를 합니다.
    private static void ClearClipCurves(AnimationClip clip)
    {
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            AnimationUtility.SetEditorCurve(clip, binding, null);
        }

        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
        {
            AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
        }

        AnimationUtility.SetAnimationEvents(clip, Array.Empty<AnimationEvent>());
    }

    // 원본 클립 경로를 현재 아바타의 Body 기준 경로로 다시 맞춥니다.
    private static string RebasePath(string sourcePath, string bodyPath)
    {
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(bodyPath))
        {
            return sourcePath;
        }

        if (string.Equals(sourcePath, bodyPath, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var prefix = bodyPath + "/";
        return sourcePath.StartsWith(prefix, StringComparison.Ordinal)
            ? sourcePath.Substring(prefix.Length)
            : sourcePath;
    }

    // 조건이 만족되면 바로 넘어가는 전이 설정을 만듭니다.
    private static void ConfigureImmediateTransition(AnimatorStateTransition transition)
    {
        transition.hasExitTime = false;
        transition.hasFixedDuration = true;
        transition.duration = 0f;
        transition.offset = 0f;
    }

    // 클립이 끝난 뒤 다음 상태로 넘어가는 전이 설정을 만듭니다.
    private static void ConfigureExitTransition(AnimatorStateTransition transition)
    {
        transition.hasExitTime = true;
        transition.hasFixedDuration = true;
        transition.exitTime = 1f;
        transition.duration = 0f;
        transition.offset = 0f;
    }

    // 애니메이션 클립의 반복 관련 설정을 정리합니다.
    private static void SetClipLoop(AnimationClip clip, bool loopTime)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.loopTime = loopTime;
        settings.loopBlend = false;
        settings.keepOriginalPositionXZ = false;
        settings.keepOriginalPositionY = true;
        settings.keepOriginalOrientation = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
    }

    // 애니메이션 클립 길이를 원하는 초로 맞춥니다.
    private static void SetClipLength(AnimationClip clip, float stopTime)
    {
        var settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.stopTime = stopTime;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
    }
}
#endif
