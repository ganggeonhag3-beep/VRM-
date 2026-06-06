# VtuberVRMTool - VRM Clothing Merger Tool

Unity에서 VTuber용 VRM 모델에 새로운 의상 에셋을 쉽고 빠르게 결합할 수 있도록 도와주는 에디터 툴 확장 스크립트입니다. 의상 오브젝트의 본(Bone) 구조를 베이스 모델의 스켈레톤에 자동으로 매칭하고 결합하여, 리깅 깨짐 없이 자연스러운 의상 교체를 지원합니다!

## 🛠 요구 사항 (Requirements)

- Unity 2019.4.41f1 이상 (기존엔 불가능 했었다)
- [UniVRM](https://github.com/vrm-c/UniVRM) 패키지 설치 필요
- 포맷: `.vrm` 캐릭터 모델 및 리깅이 완료된 의상 프리팹(FBX)

## 🚀 사용 방법 (How to Use)
스크립트 단독에 경우
1. **스크립트 적용:** `VRMClothingMergerTool.cs` 파일을 Unity 프로젝트 내 `Assets/Editor` 폴더에 넣어줍니다.
2. **툴 열기:** Unity 상단 메뉴에서 `[메뉴 이름, 예: Tools > VRM Clothing Merger]`를 클릭하여 도구 창을 엽니다.
3. **오브젝트 배치:**
   - **Base VRM:** 의상을 입힐 기준 캐릭터(VRM 모델)를 드래그 앤 드롭합니다.
   - **Clothing Prefab:** 캐릭터에게 입힐 의상 오브젝트를 드래그 앤 드롭합니다.
4. **결합 실행:** 창 하단의 `[Merge] (또는 결합하기)` 버튼을 누르면 자동으로 본 구조가 정렬된 통합 모델이 생성됩니다.

unity 패키지의 경우
유니티 패키지를 프로젝트 Assets 폴더에 끌어놓아 Import 버튼을 누르면 됩니다.

## 📝 수정 및 기여 방식 (Forked Version Note)

이 프로젝트는 김망상님의 `VRMClothingMergerTool.cs`을 포크하여 수정 및 개선한 버전이에요 
- 오리지널 저장소: [https://github.com/imdelulukim/VtuberVRMTool](https://github.com/imdelulukim/VtuberVRMTool/blob/main/VRM/VRMClothingMergerTool.cs)

## 📄 라이선스 (License)
없음
자유롭게 쓰세용!
