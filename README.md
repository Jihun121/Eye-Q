# EYE-Q Conveyor Inspector

WPF, OpenCvSharp, ONNX Runtime, Arduino, Dobot, ASP.NET Core를 이용한 컨베이어 기반 비전 검사 시스템입니다. 
카메라로 wafer 이미지를 촬영하고 YOLO ONNX 모델로 defect를 검출한 뒤,
OK/NG 판정 결과에 따라 Dobot 로봇으로 자동 분류하는 end-to-end 검사 흐름을 구현했습니다.

<img width="1356" height="812" alt="스크린샷 2026-07-01 130903" src="https://github.com/user-attachments/assets/e4f74339-f169-4057-ac13-574c5cfeea59" />
<img width="1343" height="805" alt="스크린샷 2026-07-01 130923" src="https://github.com/user-attachments/assets/5a34f5fc-d1a8-4ec4-8a74-7ecba8f94c2c" />
<img width="1876" height="870" alt="스크린샷 2026-07-01 131942" src="https://github.com/user-attachments/assets/87179c91-9285-4a78-850b-05ef252d5b03" />


## 주요 기능

- 실시간 카메라 preview
- Arduino serial 통신 기반 컨베이어 step motor 제어
- 컨베이어 정지 확인 후 검사 이미지 촬영
- ONNX Runtime 기반 YOLO 검출
- wafer/defect class, confidence, bbox 처리
- 원본 이미지 위 bbox/class/confidence/OK-NG 표시
- `original.jpg`, `result.jpg`, `result.json` 저장
- local backend로 검사 결과 업로드
- SQLite DB 기반 검사 이력 저장
- browser frontend에서 검사 이미지와 결과 확인
- Dobot pick & place 기반 OK/NG 자동 분류

## 시스템 구성

```text
EYE-Q
├─ Views/
│  └─ MainWindow.xaml               # WPF UI
├─ ViewModels/
│  └─ MainViewModel.cs              # 검사 흐름, 장비 제어, UI state
├─ Services/
│  ├─ CameraService.cs              # OpenCvSharp camera capture
│  ├─ OnnxInspectionService.cs      # ONNX inference, bbox drawing
│  ├─ ArduinoMotorService.cs        # Arduino serial motor control
│  ├─ DobotProcessService.cs        # Dobot bridge process control
│  ├─ InspectionResultSaveService.cs
│  └─ InspectionUploadService.cs
├─ Models/
│  └─ InspectionResult.cs
├─ DobotBridge/
│  └─ Program.cs                    # Dobot SDK isolation process
├─ InspectionBackend/
│  ├─ Program.cs                    # ASP.NET Core Minimal API
│  └─ wwwroot/index.html            # local inspection monitor
└─ ConveyorInspector_Firmware/
   └─ ConveyorInspector_Firmware.ino
```

## 기술 스택

### Desktop

- C#
- .NET 8
- WPF
- CommunityToolkit.Mvvm
- OpenCvSharp4
- Microsoft.ML.OnnxRuntime
- System.IO.Ports

### Vision / AI

- YOLO ONNX model
- ONNX Runtime inference
- bbox parsing and drawing
- confidence thresholding
- wafer 내부 defect 판정

### Hardware

- Arduino
- Step motor driver
- Dobot Magician
- Serial JSON protocol

### Backend

- ASP.NET Core Minimal API
- SQLite
- multipart/form-data upload
- static HTML/CSS/JavaScript frontend

## 검사 흐름

```text
1. 컨베이어를 카메라 위치로 이동
2. Arduino move done 응답 수신
3. stop 명령 및 응답 확인
4. 카메라 프레임 변화량 기반 정지 확인
5. 현재 프레임 캡처
6. ONNX 모델 추론
7. wafer/defect 판정
8. bbox overlay 결과 이미지 생성
9. original.jpg / result.jpg / result.json 저장
10. local backend 업로드
11. Dobot으로 OK/NG 분류
```

## ONNX 모델

이 저장소에는 모델 파일을 포함하지 않습니다. 
GitHub 용량과 배포 관리를 위해 `*.onnx`, `*.pt` 파일은 `.gitignore`에서 제외했습니다.

현재 코드 기준 모델 설정:

- 기본 입력 크기: `1024 x 1024`
- 입력 형식: `[1, 3, H, W]`, NCHW, float32, RGB, 0~1 normalized
- 주요 class:
  - `contamination`
  - `crack`
  - `edge_chipping`
  - `pattern_defect`
  - `scratch`
  - `wafer`

모델을 사용하려면 WPF 실행 후 **ONNX 모델 선택** 버튼으로 `.onnx` 파일을 직접 로드합니다.

## 판정 로직

- `wafer` class가 검출되지 않으면 미검출로 처리합니다.
- defect class가 검출되더라도 defect 중심점이 wafer bbox 내부에 있을 때만 유효 defect로 판단합니다.
- 유효 defect가 있으면 `NG`, defect가 없고 wafer가 검출되면 `OK`로 판정합니다.
- 결과 이미지는 wafer를 초록색, 유효 defect를 빨간색, 무시된 후보를 회색으로 표시합니다.

## 결과 저장

검사 1회마다 다음 파일을 저장합니다.

```text
InspectionResults/
└─ yyyyMMdd_HHmmss_fff_OK/
   ├─ original.jpg
   ├─ result.jpg
   └─ result.json
```

`result.json` 예시:

```json
{
  "timestamp": "2026-07-01T09:59:24.0390000+09:00",
  "judgment": "OK",
  "label": "wafer",
  "confidence": 0.9821
}
```

## Local Backend 실행

검사 결과를 browser에서 확인하려면 backend를 실행합니다.

```powershell
dotnet run --project InspectionBackend\InspectionBackend.csproj
```

기본 주소:

```text
http://localhost:5050
```

API:

```text
GET  /api/health
GET  /api/inspections
POST /api/inspections
```

WPF app은 검사 결과 저장 후 다음 파일을 backend로 업로드합니다.

- `result.json`
- `original.jpg`
- `result.jpg`

backend는 SQLite DB와 `uploads/` 폴더에 데이터를 저장합니다. 
이 파일들은 Git에 포함하지 않습니다.

## Arduino Firmware

Arduino firmware는 다음 위치에 있습니다.

```text
ConveyorInspector_Firmware/ConveyorInspector_Firmware.ino
```

필요 라이브러리:

- ArduinoJson

기본 pin:

```text
EN   = 10
STEP = 9
DIR  = 8
```

Serial command 예시:

```json
{"cmd":"move","dir":0,"steps":20800,"delayUs":500}
{"cmd":"stop"}
{"cmd":"ping"}
```

Arduino response 예시:

```json
{"status":"done"}
{"status":"stopped"}
{"status":"pong"}
```

## Dobot 연동

Dobot 제어는 WPF process 내부에서 직접 SDK를 오래 붙잡지 않고, 
`DobotBridge` 별도 process를 통해 수행합니다.

주요 명령:

- `CONNECT {port}`
- `RIGHT`
- `LEFT`
- `ESTOP`
- `RESET`

현재 검사 결과 기준:

- `OK`: RightPlace
- `NG`: LeftPlace
- `NoDetection`: 분류 중단 및 수동 확인

## 실행 방법

### 1. NuGet restore

```powershell
dotnet restore ConveyorInspector.slnx
```

### 2. Build

```powershell
dotnet build ConveyorInspector.slnx
```

### 3. Backend 실행

```powershell
dotnet run --project InspectionBackend\InspectionBackend.csproj
```

### 4. WPF 실행

Visual Studio 또는 VS Code에서 WPF project를 실행합니다.

또는:

```powershell
dotnet run --project ConveyorInspector.csproj
```

### 5. WPF에서 장비 연결

1. Camera 선택 및 시작
2. Arduino COM port 연결
3. Dobot COM port 연결
4. ONNX 모델 로드
5. 검사 실행 또는 자동 검사 시작

## GitHub에 포함하지 않는 파일

다음 파일은 용량, runtime output, 라이선스 이슈 때문에 Git에서 제외합니다.

```text
*.onnx
*.pt
*.zip
bin/
obj/
.vs/
InspectionBackend/inspection.db
InspectionBackend/uploads/
InspectionBackend/*.log
DobotDll.dll
Qt5*.dll
msvcp*.dll
msvcr*.dll
```

모델 파일과 외부 runtime DLL은 별도 배포 또는 GitHub Release 첨부 방식으로 관리하는 것을 권장합니다.

## 개선했던 문제

### 1. 반복 검사 중 메모리 증가

- 검사 이력에서 `BitmapSource` 직접 보관 제거
- 이미지 파일 경로 기반 표시로 변경
- 카메라 preview UI update backpressure 적용
- backend 업로드를 `StreamContent` 방식으로 변경
- 업로드 동시 실행을 `SemaphoreSlim`으로 제한

### 2. 컨베이어가 완전히 멈추기 전 촬영

- Arduino stop 응답 확인 추가
- 최소 안정화 대기 적용
- 카메라 frame difference 기반 정지 확인 추가
- 안정 샘플이 연속으로 확인된 뒤 촬영

### 3. 추가 ONNX 추론 비용

- 90도/270도 회전 보정 추가 추론을 제거했습니다.
- 현재는 검사 1회당 기본 ONNX 추론 1회만 수행합니다.

## 포트폴리오 포인트

이 프로젝트는 단순한 모델 추론 데모가 아니라 
camera, motor, robot, AI model, backend storage, frontend monitor까지 연결한 자동화 비전 검사 시스템입니다.

강조할 수 있는 경험:

- C# WPF 기반 장비 제어 UI 구현
- ONNX Runtime을 활용한 YOLO object detection 연동
- OpenCV 기반 bbox drawing 및 image processing
- Arduino serial protocol 설계 및 motor control
- Dobot robot process bridge 연동
- 검사 결과 file/DB/backend/frontend 파이프라인 구성
- 실제 장비 이슈인 메모리 누적과 촬영 타이밍 문제 개선

