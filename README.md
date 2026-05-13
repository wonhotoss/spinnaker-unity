# Spinnaker Unity Bridge

Unity 6 / Windows x64에서 FLIR/Teledyne Spinnaker SDK 4.3.0.190 카메라를 실시간 RGB8 프리뷰로 사용하는 브릿지입니다.

## 구성

- `native/SpinnakerUnityBridge`: C++ DLL 프로젝트
- `unity`: Unity 6 프로젝트
- `unity/Assets/SpinnakerBridge/Runtime/SpinnakerBridge.cs`: C# P/Invoke 래퍼
- `unity/Assets/SpinnakerBridge/Samples/Scenes/SpinnakerPreview.unity`: 샘플 씬
- `scripts/build_native.ps1`: native DLL 빌드 및 Unity plugin 폴더 복사

## 요구사항

- Windows x64
- Unity 6
- Visual Studio 2022 C++ build tools
- Spinnaker SDK 4.3.0.190
- 기본 SDK 경로: `C:\Program Files\Teledyne\Spinnaker`

## 빌드

PowerShell에서 repo root 기준:

```powershell
.\scripts\build_native.ps1 -Configuration Release
```

PowerShell 실행 정책으로 막히면 다음처럼 실행합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_native.ps1 -Configuration Release
```

성공하면 DLL이 다음 위치로 복사됩니다.

```text
unity\Assets\Plugins\x86_64\SpinnakerUnityBridge.dll
```

현재 설치 환경은 Spinnaker 기본 C++ import lib가 `lib64\vs2015`에 있으므로 기본값도 `vs2015`입니다. 다른 설치 구성이면:

```powershell
.\scripts\build_native.ps1 -SpinnakerSdkDir "C:\Program Files\Teledyne\Spinnaker" -SpinnakerLibFlavor vs2015
```

## Unity 실행

1. Unity Hub에서 `unity` 폴더를 프로젝트로 엽니다.
2. `Assets/SpinnakerBridge/Samples/Scenes/SpinnakerPreview.unity`를 엽니다.
3. Play Mode에서 `Initialize`, `Open First`, `Start` 순서로 실행합니다.

C# 래퍼는 시작 시 표준 Spinnaker runtime 경로를 DLL search path에 추가합니다. 따라서 대상 PC에도 Spinnaker SDK/runtime이 설치되어 있어야 합니다.

## 제공 API

고정 API:

- 카메라 초기화/탐색/open/close
- free-run continuous streaming start/stop
- 최신 프레임 RGB8 copy
- exposure auto/time get-set
- gain auto/value get-set
- frame rate enable/value get-set
- gamma enable/value get-set
- white balance auto 및 red/blue balance ratio get-set

범용 GenICam API:

- float node get/set
- int node get/set
- bool node get/set
- enum node get/set
- enum entry 목록 조회

Trigger는 PRD 범위에서 제외했지만, streaming 시작 시 free-run을 보장하기 위해 가능한 경우 `TriggerMode=Off`로 설정합니다.
