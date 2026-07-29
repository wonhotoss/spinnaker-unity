# 카메라 건강 진단(Camera Health Check) 유니티 이식 — 타당성 판정 및 이식 계획

> 작성: 2026-07-30 · 원본: `D:\projects\MOVIN-Studio-Diagnostics` (commit `3074b49`)

## Context

`MOVIN-Studio-Diagnostics`는 Jetson 기기에서 **오프라인**으로 동작한다. `sensor_capture`가 30프레임 버스트를 PNG/PCD로 저장하고, `sensor_diagnosis`가 그 파일들을 읽어 카메라/LiDAR/cross/환경 4축을 판정한다. 이 중 카메라 축은 두 파일에 집중되어 있다.

- `src/diagnosis/metrics/camera_metrics.cc` — 프레임 1장당 지표 7종 + 4×4 grid 통계
- `src/diagnosis/checkers/camera_health_checker.cc` — 프레임 집합에 대한 시간 집계 → 서브스코어/CAM_* 코드/verdict 입력
- 보조: `retinex.cc`(illumination L만 사용), `aggregate.h`, `config.h`, `health_score.h`, `error_codes.h`, `decision_engine.cc`

이 문서의 목적은 그 카메라 축을 **유니티에서 라이브 20fps(프레임당 50ms 예산)로 상시 동작**시키는 설계를 확정하는 것이다. 이 저장소(`spinnaker-unity`)는 최종 제품이 아니라 **이식 가능성 검진 + 성능/정합 테스트 하네스**다. 실제 적용 대상 프로젝트는 JPEG 인코딩 이미지를 수신해 `ImageConversion.LoadImage`로 Texture2D를 들고 있는 구조이므로, 입력 경로 비용(디코드/리드백)까지 함께 검증한다.

확정된 요구사항:
- **수치 정합**: OpenCV와 동등 수준. `diagnosis_config.json` 임계값을 그대로 재사용할 수 있어야 함
- **범위**: per-frame 메트릭 + 슬라이딩 윈도우 판정 + 카메라 단독 verdict (LiDAR/cross/calibration 제외)
- **동작 방식**: 원본의 `--frames 30` 버스트를 **실시간 수신 30프레임(= 20fps에서 1.5초)** 으로 대체하고, **수신하는 매 프레임마다 윈도우를 1칸 밀며 30프레임 체크를 라이브로 지속**한다. 즉 verdict가 20Hz로 갱신된다
- **성능 수단**: 워커 스레드 우선(순수 CPU). 컴퓨트 셰이더 미사용. 부족할 때만 Burst

이 "매 프레임 갱신" 요구사항이 설계에 주는 직접적 결과 두 가지:
- **데시메이션(N프레임마다 분석)은 1순위 폴백에서 제외**된다. 30프레임 윈도우의 시간 폭이 1.5초에서 3초 이상으로 늘어나 판정 의미가 바뀌기 때문이다. 프레임당 50ms는 협상 불가한 예산이 된다(§2)
- 윈도우에 구멍이 생기는 원인을 **"카메라/소스가 프레임을 못 줬다"와 "분석기가 못 따라가 건너뛰었다"로 반드시 분리**해야 한다. 후자를 전자로 집계하면 성능 부족이 곧바로 `CAM_FPS_LOW` 오진으로 나타난다(§3)

---

## 1. 판정: 이식 가능하다 (순수 CPU + 멀티스레드로 충분)

카메라 축 알고리즘 전체가 **OpenCV 의존 = 재현 가능한 커널 연산 5종**으로 환원되며, 프레임 간 상태는 "직전 gray 1장"뿐이다. GPU가 필요한 요소(전역 정렬, 반복 최적화, 큰 커널의 임의 컨볼루션)는 없다.

| 원본 연산 | 유니티 구현 | 정합성 | 난이도 |
| --- | --- | --- | --- |
| `cvtColor(BGR2GRAY)` | BT.601 고정소수점 `(R*4899+G*9617+B*1868+8192)>>14` | **비트 일치** | 낮음 |
| `mean`, `countNonZero(<15)`, `countNonZero(>250)`, `calcHist`+entropy | 256-bin 히스토그램 1회에서 4개 지표 전부 유도 | **비트 일치**(정수 히스토그램) | 낮음 |
| `Laplacian(ksize=1)` + `meanStdDev` | 커널 `[[0,1,0],[1,-4,1],[0,1,0]]`, BORDER_REFLECT_101, sum/sumsq(N 분모) | 부동소수 결합순서 오차(~1e-9 상대) | 낮음 |
| `Canny(50,150)` | Sobel3×3(BORDER_REPLICATE) → L1 magnitude → OpenCV식 TG22 정수 NMS(`m>prev && m>=next`) → 이력 임계 flood-fill | 알고리즘 동일, 경계행 처리로 소수 픽셀 차 | **중간(최대 난관)** |
| `GaussianBlur(σ)` + `resize(INTER_AREA/LINEAR)` (retinex illumination) | ksize=`round(σ*8+1)\|1`, OpenCV 커널식, separable, 정수배 INTER_AREA=박스 평균 | 정수배 축소는 정확 재현 가능 | 중간 |
| checker/aggregate/decision (`camera_health_checker.cc` 등) | 스칼라 수식 그대로 이식 | 동일 | 낮음 |

핵심 구조 최적화 3개로 원본의 중복 계산을 제거한다(수식 변경 아님).

1. **gray + 히스토그램 + 직전 프레임 차분 + grid 셀 합**을 단일 패스로 융합 → 원본의 5개 전체 패스가 1개로
2. **retinex illumination**: `10^(mean of log10)` = `cbrt(G15·G80·G250)` (3-scale이므로 대수적으로 동일) → 전체 해상도 log/pow 4패스 제거. 게다가 세 blur 맵은 이미 1/4·1/8 해상도에서 만들어지므로 평균을 축소 격자에서 평가 가능(§5에서 오차 측정 후 채택 결정)
3. **Canny 1회**로 전역 edge map을 만들고 4×4 grid 셀 밀도를 그 맵에서 집계 → 원본이 셀별로 `EdgeDensity()`를 다시 부르며 발생하는 2번째 Canny 제거(§5에서 등가성 검증 후 채택 결정)

---

## 2. 실시간 예산 (프레임당 50ms @ 1440×1080 = 1.56Mpx)

### 입력 I/O — JPEG/`LoadImage` 경로가 핵심 관심사

`LoadImage`는 **CPU에서 디코드해 CPU 사본을 유지한 뒤 GPU에 업로드**한다. `markNonReadable:false`(기본값)이면 텍스처가 CPU 사본을 계속 들고 있으므로:

- `tex.GetRawTextureData<byte>()` / `GetPixelData<byte>(0)` = **내부 버퍼 뷰 반환(복사 없음), GPU 리드백 없음, 지연 없음**
- 단 Texture2D API는 메인 스레드 전용이고 다음 `LoadImage`가 버퍼를 무효화하므로, 메인 스레드에서 분석용 이중 버퍼로 `memcpy` 1회(RGB24 4.6MB ≈ **0.5–1.5ms**) 후 워커에 넘긴다
- **즉, 이 경로에서 추가되는 리드백 비용은 memcpy 1회뿐이다.** JPEG 디코드(~8–15ms)와 GPU 업로드는 표시용으로 대상 프로젝트가 이미 지불하는 비용이다
- 예외: 어딘가에서 `markNonReadable:true`로 호출하거나 소스가 RenderTexture면 CPU 사본이 없다 → `AsyncGPUReadback` 폴백(+1–3프레임 지연, CPU 거의 0). 건강 모니터링은 지연에 둔감하므로 허용 가능하지만, **해상도 의존 지표(laplacian_var, edge_density) 때문에 리드백은 원해상도로** 해야 한다

이 저장소(테스트 하네스)에서는 `SpinnakerBridge.TryGetLatestFrame`이 이미 RGB8 managed byte[]를 주므로 리드백이 아예 없다. 즉 **두 경로를 같은 코어에 물리는 어댑터**만 있으면 된다.

### 계산 비용 — 추정치 (Phase 1에서 실측으로 대체할 값)

| 단계 | 전체 패스 | 단일 스레드 (Mono/IL2CPP 추정) | 4스레드 | Burst+SIMD |
| --- | --- | --- | --- | --- |
| gray+hist+diff+cell합 (융합) | 1 | 4–8ms | 1–2ms | 0.3–0.6ms |
| Laplacian variance | 1 | 8–14ms | 2–4ms | 0.5–1ms |
| Canny (Sobel+NMS+hysteresis) | ~3 | 20–35ms | 6–10ms | 2–4ms |
| retinex illumination (INTER_AREA×2 + 소형 blur + cbrt) | ~2 | 5–9ms | 2–3ms | 0.5–1ms |
| grid 집계 (edge map 재사용) | — | <1ms | <1ms | — |
| **합계** | | **~40–70ms** | **~12–20ms** | **~4–7ms** |

(기준: 개발기 Ryzen 3 3300X 4C/8T. Mono/IL2CPP는 .NET CoreCLR보다 스칼라 루프에서 1.2–2.5× 느린 것으로 가정)

윈도우 집계(30프레임 × 지표 7종 스칼라 재계산)는 **프레임당 수 마이크로초**라 예산에서 무시할 수 있다. 매 프레임 verdict를 갱신해도 비용은 위 표의 per-frame 메트릭이 전부다.

**결론**: 단일 스레드 순수 C#은 예산 경계선이라 위험하고, **행 밴드 병렬화 + 전용 워커 스레드면 50ms 예산 안에 2.5–4배 여유**로 들어온다. Burst는 여유 확보용 옵션, 컴퓨트 셰이더는 불필요(hysteresis가 GPU 비친화적이고 리드백 지연·렌더 경합만 추가).

매 프레임 분석이 요구사항이므로 예산 초과 시 폴백 순서는 ① `com.unity.burst` 추가(구조 변경 없음, 3–5배) → ② 분석 해상도를 설정으로 고정 축소 + 해당 해상도에서 임계값 재튜닝(scale 의존 지표 때문에 필수) → ③ 최후수단으로 프레임 간격 분석(윈도우 시간 폭이 늘어나 판정 의미가 바뀜을 문서화하고 수용) 이다. ③을 1순위로 쓰지 않는 것이 이번 요구사항의 핵심 제약이다.

---

## 3. 설계

### 모듈 (신규 `unity/Assets/Diagnostics/Runtime/`)

- `OpenCvOps.cs` — OpenCV 등가 원시연산: gray+hist 융합 패스, Laplacian, Sobel/Canny(NMS+hysteresis), 가우시안 커널 생성, 정수배 INTER_AREA / INTER_LINEAR. Unity API 비의존, `Span<byte>` 기반
- `CameraMetricsCore.cs` — `camera_metrics.cc`의 `CameraFrameMetrics` 대응 구조체와 `Compute()`. 원본과 동일한 7지표 + `grid_total/dark_cells/flat_cells`
- `CameraHealthWindow.cs` — 링 버퍼(**30프레임 = 1.5s @20fps**, 원본 `--frames 30` 기본값과 동일) + `camera_health_checker.cc` 이식 + `aggregate.h`의 `Mean/Stddev/Fraction/BandScore`. 프레임 도착마다 1칸 밀고 윈도우 전체 재계산(§3.1)
- `CameraVerdict.cs` — 카메라 단독 판정 + `decision_engine.cc`의 `AssessEnvironment`(카메라 메트릭만 사용하므로 그대로 이식 가능) + `error_codes.h`의 CAM_*/ENV_* 부분집합
- `DiagnosisConfigModel.cs` — `config.h`의 `CameraThresholds`/`TemporalConfig`/`ScoreWeights` 미러. `diagnosis_config.json`을 그대로 붙여넣어 로드(JsonUtility)
- `FrameSource.cs` — 입력 어댑터 3종: ① Spinnaker byte[] RGB8 ② readable Texture2D(`GetRawTextureData` + memcpy) ③ `AsyncGPUReadback` 폴백
- `CameraHealthRunner.cs` — MonoBehaviour. 획득 → 버퍼 풀(2~3개) → 워커 큐 → 결과 수신

### 3.1 슬라이딩 윈도우 = 30프레임 버스트의 실시간 등가물

원본은 30프레임을 모아 `CheckCameraHealth(images, requested=30, captured_pairs)`를 1회 호출한다. 실시간 등가물은 **직전 30프레임의 per-frame 메트릭을 링 버퍼에 들고, 프레임이 도착할 때마다 같은 집계식을 다시 도는 것**이다. 프레임당 이미지 연산은 1회뿐이고(새 프레임 1장), 재사용되는 것은 이미 계산된 스칼라 메트릭 30개다.

**집계는 증분(running sum)이 아니라 매번 전체 재계산**한다. n=30이라 비용이 무의미하게 작고, `Stddev(mean_intensity)`(exposure_std) 같은 값이 증분 방식에서 누적 오차로 드리프트하는 것을 피할 수 있으며, 무엇보다 C++ 체커를 같은 30프레임에 돌린 결과와 **식이 완전히 동일**해진다(정합 검증이 그대로 성립).

원본과 정확히 같은 값이 나오게 하려면 지켜야 할 규칙 3개:

1. **dup 판정의 off-by-one.** C++는 루프에서 직전 프레임과 비교하므로 30프레임에서 비교는 29회이고 `dup_frac = dup_frames / 30`이다. 링 버퍼에서는 가장 오래된 프레임의 dup 플래그가 이미 윈도우를 빠져나간 프레임과 비교해 만들어진 값이므로, **가장 오래된 슬롯의 dup 플래그는 합산에서 제외하고 분모는 30을 유지**해야 원본과 일치한다.
2. **`n`은 윈도우에 실제로 들어있는 분석 완료 프레임 수**다. 기동 직후나 분석기 스킵으로 n<30이면 `Fraction()`의 분모가 달라지고, `min_frames=3` 미만이면 verdict는 `TEMPORARY_UNAVAILABLE`이다(원본 `decision_engine.cc:53`과 동일한 게이트).
3. **`missing_frac`은 윈도우의 벽시계 폭에서 구한다.** 원본의 `captured_pairs / requested_frames`를 실시간에서는 `expected = round(window_span_ms / (1000/20))`, `received` = 그 구간에 소스가 실제로 준 프레임 수로 대체한다. 링 버퍼를 "마지막 30개"로만 관리하면 초당 10프레임만 들어와도 윈도우가 조용히 3초로 늘어나며 결손을 못 잡는다 → **집계용 링은 개수(30) 기준, 결손 판정은 시간 폭 기준**으로 둘을 분리해 유지한다.

**소스 드롭 vs 분석기 스킵 분리** (요구사항상 가장 중요한 실패 모드):
- `source_received` — 소스(카메라/JPEG 스트림)가 준 프레임 수. 프레임 ID/시퀀스 결손으로 판정. → `missing_frac`에 반영되는 유일한 입력
- `analyzed` / `analyzer_skipped` — 워커가 바빠서 건너뛴 수. **`missing_frac`에 절대 섞지 않는다.** 윈도우의 표본 수 `n`만 줄이고, 별도 진단 카운터로 노출해 성능 문제로 읽히게 한다
- 즉 워커 과부하가 `CAM_FPS_LOW`로 새지 않는다. 대신 `analyzer_skipped`가 올라가면 그것이 "예산 초과" 신호다

**verdict 갱신 주기와 지연**: verdict는 매 프레임(20Hz) 갱신되고 항상 "직전 1.5초"를 의미한다. 워커 파이프라인이 약 1프레임(50ms) 지연을 더한다 — 건강 모니터링 용도에는 무해하다.

**표시 안정성**: 윈도우가 29/30 겹치므로 연속 verdict는 강하게 상관되지만, 임계 근처에서는 20Hz로 깜빡일 수 있다. API는 원본 그대로의 raw verdict를 노출하고, UI 표시용으로만 옵션 디바운스(같은 verdict가 K회 연속일 때 갱신, 기본 K=5 ≈ 0.25s)를 둔다. 판정 로직 자체는 건드리지 않는다.

### 스레딩

- 메인 스레드: 프레임 획득 + memcpy + 결과 소비만 (합 1–2ms 목표)
- 전용 장수명 워커 스레드 1개(ThreadPool 아님 — 유니티 잡 스레드와의 경합 회피). 워커 내부에서 패스별로 행 밴드 병렬화(`MaxDegreeOfParallelism = ProcessorCount-2`). hysteresis만 순차(엣지 픽셀 2–6%만 접근하므로 1ms 미만)
- 워커가 바쁘면 프레임 **스킵(latest-wins)**. 요구사항상 스킵은 정상 동작이 아니라 **예산 초과 알람**이다 → `analyzer_skipped`로 계수하고 `missing_frac`과 철저히 분리(§3.1). 정상 상태에서 이 값은 0이어야 하며, PlayMode 검증의 합격 기준에 포함한다

### 카메라 단독 verdict

`DecideStatus`는 LiDAR가 offline이면 `kLidarCheck`를 반환하므로 그대로 쓸 수 없다. `DecideCameraOnly()`를 새로 둔다.

```
window < min_frames(3)         → TEMPORARY_UNAVAILABLE
camera.offline                 → CAMERA_CHECK
camera.unusable                → CAMERA_CHECK
camera.status == Error         → CAMERA_CHECK
camera.low_texture || Unknown  → TEMPORARY_UNAVAILABLE
else                           → OK
```
+ `AssessEnvironment`의 ENV_BACKLIGHT / ENV_LOW_VISIBILITY cue를 함께 노출.

### frame/FPS 축의 입력원 문제

`missing_frac`을 윈도우 시간 폭 기준으로 구한다는 것은 §3.1에서 정했다. 남은 문제는 **소스가 실제로 준 프레임 수를 어디서 얻느냐**이고, 여기서 **네이티브 브릿지의 latest-wins 단일 버퍼가 드롭을 은폐**한다. 브릿지가 노출하는 `rawImage->GetFrameID()`(`SpinnakerUnityBridge.cpp:256`)는 카메라 기준 연속 번호지만, Unity가 폴링을 거를 때의 앱측 스킵과 카메라측 드롭이 구분되지 않는다.

→ `sub_get_stream_stats`(획득 스레드가 수신한 누적 프레임 수 / incomplete 이미지 수 / 마지막 프레임 ID)를 네이티브에 추가한다. 매 프레임 판정 구조에서는 이 카운터가 **frame 축의 유일한 정당한 입력원**이다(앱 폴링 주기·워커 스킵과 무관한 값이어야 하므로). 대상 프로젝트에서는 JPEG 스트림의 시퀀스 번호/수신 타임스탬프가 같은 역할을 한다. 추가되기 전까지는 frame 축을 "미평가"로 두고 verdict에서 제외한다(잘못된 `CAM_FPS_LOW`보다 낫다). 이 상태에서도 나머지 4개 서브스코어(exposure/sharpness/occlusion/temporal)는 정상 동작하므로 검진 자체는 진행 가능하다 — 단 가중치 `cam_frame=0.30`을 남은 축에 재분배할지, 100점으로 고정할지 결정해 문서화한다.

### 이식하지 않는 것

LiDAR/cross/calibration 축 전체, `retinex.cc`의 SSR/MSR/MSRCR/MSRCP·Percentile·SimplestColorBalance(진단은 illumination L만 사용), PCD I/O, 리포트 JSON 스키마 전체(축약 결과만 노출).

---

## 4. 채널 순서 (놓치기 쉬운 정합 포인트)

기기 경로는 Bayer 디모자이크 결과가 실제로는 RGB 순서이고, `sensor_capture.cpp:322`에서 RGB→BGR 스왑 후 저장하므로 **PNG는 정상 색상**이고, 진단은 `IMREAD_COLOR`(BGR)로 읽어 올바른 luma를 계산한다. 유니티 브릿지는 Spinnaker `Convert(PixelFormat_RGB8)` 결과를 그대로 준다(`SpinnakerUnityBridge.cpp:246`). 따라서 유니티에서는 **바이트 인덱스 [0]=R,[1]=G,[2]=B에 0.299/0.587/0.114를 대응**시켜야 원본과 같은 값이 나온다. 바이트 순서를 그대로 BGR로 가정하면 luma가 `0.185*(R-B)`만큼 틀어진다.

---

## 5. 원본과의 의도적 차이 — 반드시 측정 후 채택 결정

"동등 수준 정합"이 요구사항이므로, 아래 3개는 **가정하지 말고 실측**한다. 오차가 허용범위를 넘으면 비싼 쪽으로 되돌린다.

1. **grid 셀 edge density를 전역 edge map에서 집계** — 원본은 셀별로 Canny를 다시 돌리므로 셀 경계 border 처리와 hysteresis 범위가 다르다. `grid_flat_edge_density = 0.005`는 상대적으로 타이트해서 셀 분류가 뒤집힐 수 있다. 실측 후 뒤집히면 셀별 Canny로 복귀(그래도 총 2× Canny로 예산 내)
2. **illumination L 평균을 축소 격자에서 평가** — 원본은 bilinear 업샘플 후 전체 해상도에서 cbrt·평균. 업샘플 경계 반픽셀 때문에 완전 동일하지 않다. 목표 오차 ±0.5 L(임계 50/140 대비 무해). 초과 시 전체 해상도 cbrt 1패스로 복귀(+3–6ms)
3. **`log10` 합 → `cbrt(곱)`** — 3-scale에서 대수적으로 동일. 부동소수 오차만 확인

---

## 6. 작업 순서

1. **Phase 0 — 골든값 생성기**: `tools/ref_camera_metrics.py` (`uv run --with opencv-python --with numpy`). `camera_metrics.cc`를 cv2로 1:1 재현해 프레임별 지표 JSON 출력. 개발기에는 OpenCV가 없고 Python 3.13이 있으므로 이 경로가 유일한 로컬 기준선(기기에서 C++ 바이너리를 돌릴 수 있으면 1프레임으로 교차 확인)
2. **Phase 1 — 코어 + 콘솔 하네스**: `OpenCvOps.cs` / `CameraMetricsCore.cs`를 Unity 비의존으로 작성하고, `tools/CamHealthBench` (dotnet 콘솔)에서 ① 골든값 대조 ② 단일/다중 스레드 실측. 여기서 §5의 3개 근사를 판정
3. **Phase 2 — 슬라이딩 윈도우 + verdict**: `CameraHealthWindow.cs`(30프레임 링, 매 프레임 전체 재계산, §3.1의 dup off-by-one/`n`/시간폭 규칙), `CameraVerdict.cs`, `DiagnosisConfigModel.cs`. `diagnosis_config.json`을 그대로 로드해 임계값 일치 확인. **등가성 테스트**: 저장된 30프레임 캡처를 순서대로 밀어넣고 마지막 윈도우 결과가 C++ `CheckCameraHealth`를 같은 30장에 돌린 결과와 일치하는지 검증
4. **Phase 3 — 실시간 러너**: `FrameSource.cs`(3경로), `CameraHealthRunner.cs`(워커 + 버퍼 풀 + `source_received`/`analyzed`/`analyzer_skipped` 카운터 분리). `EvaluationPreviewController`와 별개 컴포넌트로 두고, UI에는 지표/스코어/코드/verdict 패널 + 스킵 카운터만 추가. 네이티브 `sub_get_stream_stats` 추가는 이 단계에 포함
5. **Phase 4 — 유니티 실측**: Editor(Mono)와 IL2CPP 빌드에서 프레임당 p95 측정 및 `analyzer_skipped == 0` 확인. 초과 시 §2의 폴백 순서(① Burst → ② 해상도 고정 축소 + 재튜닝 → ③ 프레임 간격 분석)를 따른다. 밴드 루프는 처음부터 `IJobParallelFor`로 치환 가능한 형태(인덱스 범위만 받는 정적 함수)로 작성해 ①이 구조 변경 없이 가능하게 한다
6. **Phase 5 — JPEG 경로 검증**: 동일 프레임을 대상 프로젝트의 JPEG 품질로 인코딩→디코딩해 지표 변화량 측정, 임계값 이관 가능 여부 판단(§7)

---

## 7. 검증

- **정합**: 프레임별 대조 허용오차 — `mean_intensity` ±0.05, `dark/saturated_ratio` ±0.0005, `entropy` ±0.01, `laplacian_var` ±1%, `edge_density` ±2%, `illumination_L` ±0.5, **grid dark/flat 셀 개수는 완전 일치**. 대상: `MOVIN-Studio-Diagnostics/tmp/camera.png` + 실제 capture 세트
- **윈도우 등가성**: 캡처 30프레임을 순서대로 스트리밍 주입 → 마지막 윈도우의 서브스코어/`total`/CAM_* 코드 집합이 C++ `CheckCameraHealth`(같은 30장, `requested_frames=30`)와 일치. 이어서 31번째 프레임을 넣어 윈도우가 1칸 밀리고 §3.1-1의 dup 규칙대로 값이 바뀌는지 확인
- **성능**: 콘솔 하네스(CoreCLR) 및 Unity PlayMode에서 1440×1080 프레임당 p95 < 50ms, 메인 스레드 추가 부하 < 2ms, **20fps 5분 연속 구동에서 `analyzer_skipped == 0`**, verdict 갱신 주기 ≈ 20Hz
- **JPEG 영향 정량화**: JPEG 양자화는 blocking/ringing으로 `laplacian_var`·`edge_density`·`entropy`를 이동시킨다(특히 평탄 영역의 8×8 블록 엣지 → `grid_flat_edge_density 0.005` 판정에 민감). PNG 기준값 vs JPEG-Q 기준값 차이를 표로 남기고, 필요하면 JPEG 경로용 임계 오프셋을 문서화. **JPEG 품질과 해상도가 고정되어야 임계값이 안정적**이라는 전제도 함께 명시
- **동작 확인(실기)**: 라이브 카메라에서 렌즈 가림 → `CAM_OCCLUDED`, 노출 강제 하강 → `CAM_TOO_DARK`, 초점 흐림 → `CAM_BLURRY`. 각 코드가 **결함 발생 후 최대 1윈도우(1.5s) 내에 뜨고, 해제 후 1.5s 내에 사라지는지** 확인(윈도우 길이가 곧 반응 지연이다). 임계 근처에서의 verdict 깜빡임 여부와 디바운스(K=5) 적용 전후를 함께 기록

---

## 8. 리스크 / 열린 문제

- **Canny 재현이 유일한 실질 난관.** OpenCV는 SIMD 슬라이딩 윈도우 구현이라 경계행에서 소수 픽셀 차가 남을 수 있다. edge_density 임계(0.020)는 느슨해 문제없지만 grid 0.005는 민감 → §5-1과 함께 판정
- **대상 프로젝트 미확인 사항**: JPEG 해상도/품질, `LoadImage`의 `markNonReadable` 사용 여부, 프레임 시퀀스 번호 제공 여부. 셋 다 예산·정합·FPS축 설계에 직접 영향
- **윈도우 길이 = 반응 지연 = 잡음 내성의 트레이드오프.** 30프레임/1.5초는 원본과 같은 값이라 `error_fraction=0.60`/`warning_fraction=0.30`을 그대로 이관할 수 있다는 것이 최대 장점이다. 더 짧게 가면 반응은 빨라지지만 소수 프레임이 판정을 뒤집는다(10프레임 윈도우면 3프레임만 어두워도 WARNING) → 길이를 바꿀 경우 fraction 임계도 함께 재검토해야 한다
- **`duplicate_diff_max=0.5`의 실시간 오탐 가능성.** 완전 정지 장면에서 센서 노이즈가 0.5 LSB 미만이면 정상 프레임이 중복으로 계수된다. 원본 캡처도 같은 20Hz 조건이었으므로 값은 그대로 이관하되, 실기에서 정지 장면의 `dup_frac`을 측정해 확인 항목에 포함한다
- `laplacian_var`/`edge_density`는 해상도 의존 지표다. 어떤 이유로든 분석 해상도를 바꾸면 임계값 재튜닝이 필요하다 → 분석 해상도를 설정으로 고정하고 로그에 남긴다
- 대안 경로(정합 리스크를 0으로 만들고 싶을 때, 재확인용): C++ 코드를 기존 `SpinnakerUnityBridge.dll`에 그대로 얹어 P/Invoke로 호출. OpenCV DLL 동반(수십 MB)과 에디터 리로드 이슈를 감수하는 대신 기기와 단일 소스를 유지할 수 있다. 지금은 순수 C# 경로를 우선하고, Phase 1 정합 결과가 허용범위를 못 맞출 때의 백업으로 둔다

---

## 부록 A. 원본 프로젝트가 30프레임에서 추출하는 지표 전량

이식 대상의 정확한 경계를 고정하기 위한 인벤토리. 출처는 `camera_metrics.cc` / `camera_health_checker.cc` / `decision_engine.cc` / `cross_metrics.cc` / `camera_test.cpp`.

### A.1 프레임 1장당 원시 지표 — 7 스칼라 + grid 3 (`ComputeCameraMetrics`)

| 지표 | 계산 | 임계 (config) |
| --- | --- | --- |
| `mean_intensity` | `cv::mean(gray)` | (직접 임계 없음, env cue와 exposure_std에 사용) |
| `dark_pixel_ratio` | `gray < 15` 비율 | `dark_pixel_ratio_max 0.50` |
| `saturated_pixel_ratio` | `gray > 250` 비율 | `saturated_pixel_ratio_max 0.10` |
| `illumination_L` | retinex 다중스케일(σ=15/80/250) surround의 화소평균 | `illum_dark_max 50`, `illum_bright_min 140` |
| `laplacian_var` | `Laplacian(ksize=1)`의 분산 (초점/블러) | `laplacian_var_min 100` |
| `edge_density` | `Canny(50,150)` 엣지 픽셀 비율 | `edge_density_min 0.020` |
| `entropy` | 256-bin 히스토그램 엔트로피 | `entropy_min 3.0` |
| `grid_total` | `grid²` = 16 | — |
| `grid_dark_cells` | 셀 평균 < 20 인 셀 수 | `grid_dark_mean 20` |
| `grid_flat_cells` | 셀 edge density < 0.005 인 셀 수 | `grid_flat_edge_density 0.005` |

### A.2 프레임당 이진 플래그 7종 (checker 루프에서 파생)

- `too_dark` = `illumination_L ≤ 50` **또는** `dark_ratio > 0.50`
- `too_bright` = `illumination_L ≥ 140` **또는** `saturated_ratio > 0.10`
- `low_texture` = `edge_density < 0.020` **그리고** `entropy < 3.0`
- `blurry` = `laplacian_var < 100` **그리고** `!low_texture` (저텍스처 장면을 블러로 오판하지 않도록)
- `occluded` = `flat_ratio ≥ 0.75`, `partial_occluded` = `0.25 ≤ flat_ratio < 0.75`, 단 `flat_ratio = max(grid_dark_cells, grid_flat_cells) / 16`
- `duplicate` = `mean|gray_i − gray_{i−1}| < 0.5` (직전 프레임과의 비교 = 유일한 순서 의존)

### A.3 30프레임 시간 집계

- 플래그별 비율: `dark_frac`, `bright_frac`, `blur_frac`, `low_texture_frac`, `occ_frac`, `partial_frac`, `dup_frac`
- `missing_frac` = `1 − min(1, captured_pairs/requested_frames)` ← **실시간에서 의미가 바뀌는 유일한 항목**(§3.1-3)
- `exposure_std` = `Stddev(mean_intensity[30])`, `exposure_unstable` = `> 25`
- 리포트용 평균: 7지표의 `Mean(...)`
- `BandScore`: `frac ≥ 0.60 → 40` / `≥ 0.30 → 70` / else `100`

**중요: 30프레임의 순서나 추세는 보지 않는다.** "몇 프레임이 이상인가"의 비율만 쓴다. 순서 의존은 `duplicate`(연속 비교)와 `exposure_std`(집합 분산)뿐이므로 슬라이딩 윈도우로 옮기는 것이 자연스럽다.

### A.4 서브스코어 5개 → total → status

```
frame     = BandScore(max(missing_frac, dup_frac))
exposure  = min(BandScore(dark_frac), BandScore(bright_frac));  unstable이면 min(·,70)
sharpness = BandScore(blur_frac)
occlusion = min(BandScore(occ_frac), BandScore(partial_frac × 0.5))
temporal  = unstable ? 70 : 100;  dup_frac ≥ 0.30이면 min(·,70)
total     = 0.30·frame + 0.20·exposure + 0.20·sharpness + 0.20·occlusion + 0.10·temporal
status    = total ≥ 80 OK / ≥ 60 WARNING / < 60 ERROR
```
부가 플래그: `offline`(로드 가능 프레임 0), `low_texture`(`low_texture_frac ≥ 0.60`), `unusable`(`dark/bright/occ_frac ≥ 0.60` → 가중평균과 무관하게 **강제 ERROR**. 새까만 프레임이 frame/temporal 100점에 희석돼 ~64점으로 WARNING이 되는 것을 막는 장치)

### A.5 CAM_* 코드 11종 (정의 12종 중 `CAM_002 stale timestamp`는 미사용)

`CAM_001` no frame / `CAM_003` fps low / `CAM_004` duplicated frame / `CAM_101` too dark / `CAM_102` overexposed / `CAM_103` exposure unstable / `CAM_201` blurry / `CAM_202` lens contamination(30프레임 **평균** `edge_density`·`entropy`·`laplacian_var`가 동시에 미달) / `CAM_203` low texture / `CAM_301` occluded / `CAM_302` partially occluded

### A.6 리포트에 남는 카메라 metric 9개

`mean_intensity`, `illumination_L`, `saturated_ratio`, `dark_ratio`, `laplacian_var`, `edge_density`, `entropy`(이상 7개는 30프레임 평균), `exposure_std`, `frames`(집계에 쓰인 프레임 수)

### A.7 환경 cue — 카메라 지표만으로 산출 (그대로 이식 가능)

- `ENV_003 backlight`: `saturated_ratio > 0.10` **그리고** `illumination_L ≥ 112`(= 0.8 × 140)
- `ENV_002 low visibility`: `mean_intensity ≤ 50` **그리고** `entropy < 3.0`

### A.8 같은 30프레임을 cross 축이 다시 소비 (이번 이식 범위 밖)

`cross_metrics.cc`는 이미지를 **undistort → gray → `Canny(50,150)` 재계산**한 뒤 LiDAR 투영과 비교한다: `camera_edge_density`(guard 0.020), `inside_ratio`/`inside_count`, `projected_centroid` → `jitter = √(σcx² + σcy²)`, `edge_align_score`(LiDAR depth-discontinuity boundary 픽셀 중 카메라 엣지를 3px 팽창시킨 영역에 들어오는 비율). 집계는 `low_cov_frac`/`poor_align_frac`/`jitter`. LiDAR가 없으면 성립하지 않으므로 제외하되, **카메라 축과 완전히 별개로 Canny를 한 번 더 돌린다**는 점은 나중에 LiDAR를 합칠 때 edge map 공유로 절약할 여지로 남는다.

### A.9 `camera_test`(단순 체크)가 30프레임에서 보는 것

이미지 픽셀은 **해상도 확인에만** 사용한다(min/max가 정확히 1440×1080). FPS는 이미지가 아니라 `capture_meta.json`의 `measured_matched_rate_hz`에서 읽는다.

### A.10 이식 관점의 계산량 요약

프레임 1장당 실제로 도는 이미지 연산: gray 변환 **2회**(`ComputeCameraMetrics` 내부 + checker의 중복 검출 루프에서 재계산, `camera_health_checker.cc:79`), Canny **17회**(전체 1 + 4×4 grid 셀 16), Laplacian 1회, retinex FastGaussian 3회(축소·blur·업샘플) + 전체 해상도 log/pow 4패스, 그리고 mean/countNonZero/calcHist가 각각 별도 전체 패스. 30프레임이면 Canny 510회다. §1의 구조 최적화 3개는 여기서 나온 것이며, 계산식을 바꾸지 않고 이 중복만 제거한다.
