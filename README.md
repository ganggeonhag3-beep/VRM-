# VtuberVRMTool - VRM Clothing Merger Tool

Unity에서 VTuber용 VRM 모델에 새로운 의상 에셋을 쉽고 빠르게 결합할 수 있도록 도와주는 에디터 툴 확장 스크립트입니다. 의상 오브젝트의 본(Bone) 구조를 베이스 모델의 스켈레톤에 자동으로 매칭하고 결합하여, 리깅 깨짐 없이 자연스러운 의상 교체를 지원합니다!

## 🛠 요구 사항 (Requirements)
Unity 2019.4.41f1에서 Unity 2023 ~~.~~ 버전 까지 호환! (그 이후 버전은 유니티 구조 호환 안됨)
- Unity 2019.4.41f1 이상 (V See Face에서는 2019.4.40f1, VRChat Creator Companion에선 2022.3.22f1, Warudo에선 2021.3.45f2)
- [UniVRM](https://github.com/vrm-c/UniVRM) 패키지 설치 필요
- 포맷: `.vrm` 캐릭터 모델 (vroid 모델도 가능합니다) 및 리깅이 완료된 의상 프리팹(FBX)

VRoid 아바타 <-> Booth 아바타, 비전용 아바타 옷 <-> 내 아바타 에도 적용이 가능합니다. (비 전용 아바타 옷은 굳이 거칠 필요 없이 한번에 나에게 맞게 적용됩니다)
(단 경고가 뜨는 경우 안될 수 있어요ㅠㅠ)

| |
|---|
| **[📦 여기를 클릭하여 다운로드]([](https://github.com/ganggeonhag3-beep/VRM-/releases/tag/VRMClothingMergerTool))** |
## 🚀 사용 방법 (How to Use)
**스크립트 단독에 경우**
스크립트 적용: `VRMClothingMergerTool.cs` 파일을 다운로드 받고, Unity 프로젝트 내 'Assets'을 열고, 우클릭 후 create -> Foldeer 선택 후 이름을 옷 입히는 툴로 저장하고 옷 입히는 툴 폴더를 열어요. 옷 입히는 툴 폴더를 연 상태에서 우클릭 후 Show in Exploer를 클릭 후 아까 다운로드 VRMClothingMergerTool.cs 파일을 복사한다음 연 폴더에 붙여넣어줍니다.

**unity 패키지 파일의 경우 (이게 제일 쉬움)**
유니티 패키지를 프로젝트 Assets 폴더에 끌어놓아 Import 버튼을 누르면 됩니다.

## 툴  사용법
**1. 툴 열기** : Unity 상단 메뉴바에서 옷 입히는 툴 -> 옷 입히는 툴 (창 열기)를 클릭하여 도구 창을 엽니다.
 **2. 오브젝트 배치:**
 - **Base VRM:** 의상을 입힐 기준 캐릭터(VRM 모델)를 드래그 앤 드롭합니다.
   - **Clothing Prefab:** 캐릭터에게 입힐 의상 오브젝트를 드래그 앤 드롭합니다.
**3. 옷 입히기 실행** :  진짜로 캐릭터에 옷 입히기 버튼을 누르면 옷 입히기가 됩니다.

이것 뿐 아니라 등등등 여러 기능이 있으니 잘 활용해보시길!

## 📝 수정 및 기여 (Forked Version Note)
이 프로젝트는 김망상님의 `VRMClothingMergerTool.cs`을 포크하여 수정 및 개선(개선 정도가 아니라 사실 많이)한 버전이에요 
- 오리지널 저장소: [https://github.com/imdelulukim/VtuberVRMTool](https://github.com/imdelulukim/VtuberVRMTool/blob/main/VRM/VRMClothingMergerTool.cs)


**여기엔 라이선스가 있습니다!**
의외죠? 남에 거(김망상님 거) 포크한건데........
진짜 있어요. 무시 무시 하죠! 진짜입니다.
라이선스 파일 잘 읽어 보세요! (잘 안읽으면 바보되요. 갑자기 이상한 사람 됩니다 진짜로 저 오해받는 사람 만들 수 있어요 진짜로 이 라이선스 파일 정확히 '이' 라이선스 파일 잘 안읽으면 저를 이상하게 볼 수 있어요. 좀 라이선스 좀 읽자!)
(ㅋㅋㅋㅋㅋㅋ)

그리고 이 프로젝트에 들어가서 위쪽 옆에 (위쪽에서 오른쪽) Star버튼 (별모양 버튼이다. 대충 이 프로젝트 팔로우 버튼 느낌) 눌러 주시면 저는 행복합니다!!!
