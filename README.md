# Conveyor Inspector — 컨베이어 비전 검사 시스템
<img width="1366" height="842" alt="image" src="https://github.com/user-attachments/assets/c335819e-2162-406f-b7c0-f7b9e3b0d5e9" />

Catppuccin Mocha 테마 기반 WPF + ONNX + OpenCvSharp4 + Arduino 통합 프로젝트입니다.

---

## 프로젝트 구조

```
ConveyorInspector/
├── ConveyorInspector.csproj
├── App.xaml / App.xaml.cs
├── Themes/
│   └── CatppuccinMocha.xaml        ← 전체 테마 리소스
├── Models/
│   ├── InspectionResult.cs
│   └── MotorSettings.cs
├── Services/
│   ├── ArduinoMotorService.cs      ← 시리얼 통신 + 시뮬레이션 모드
│   ├── CameraService.cs            ← OpenCvSharp4 카메라
│   └── OnnxInspectionService.cs    ← ONNX 추론 + 데모 모드
├── ViewModels/
│   └── MainViewModel.cs            ← CommunityToolkit.Mvvm
├── Converters/
│   └── Converters.cs
├── Views/
│   ├── MainWindow.xaml
│   └── MainWindow.xaml.cs
└── ConveyorInspector_Firmware.ino  ← Arduino 펌웨어
```

---

## 시작 방법

### 1. NuGet 패키지 복원

```bash
dotnet restore
```

### 2. 빌드 & 실행

```bash
dotnet build
dotnet run
```

### 3. Arduino 펌웨어 업로드

- **Arduino IDE** 또는 **VS Code + PlatformIO** 사용
- **ArduinoJson** 라이브러리 설치 필요 (라이브러리 관리자 → "ArduinoJson" by Benoit Blanchon)
- `ConveyorInspector_Firmware.ino` 업로드
- 핀 배치: `EN=10, STEP=9, DIR=8`

---

## 동작 흐름

```
[검사 실행] 버튼
     │
     ▼
정방향 3바퀴 회전 (컨베이어 이송)
     │
     ▼
카메라 프레임 캡처
     │
     ▼
ONNX 추론 (모델 미로드 시 데모 모드)
     │
     ├── 불량 → 역방향 3바퀴 회전 (반품)
     └── 정상 → 정방향 3바퀴 회전 (이송)
```

---

## ONNX 모델 연동

- 입력: `[1, 3, 224, 224]` NCHW float32 (ImageNet 정규화)
- 출력: `[1, N]` softmax 확률
- 기본 클래스: `["정상", "불량"]` — `OnnxInspectionService.ClassNames` 수정 가능
- 모델 없이도 **데모 모드**로 동작 (30% 확률 불량 랜덤 시뮬레이션)

---

## 시리얼 프로토콜 (Arduino ↔ C#)

| 방향 | 명령 | 설명 |
|------|------|------|
| C# → Arduino | `{"cmd":"move","dir":0,"steps":4800}` | 정방향 N스텝 |
| C# → Arduino | `{"cmd":"move","dir":1,"steps":4800}` | 역방향 N스텝 |
| C# → Arduino | `{"cmd":"stop"}` | 비상정지 |
| C# → Arduino | `{"cmd":"ping"}` | 연결 확인 |
| Arduino → C# | `{"status":"done"}` | 완료 |
| Arduino → C# | `{"status":"stopped"}` | 정지 완료 |
| Arduino → C# | `{"status":"pong"}` | Ping 응답 |

---

## 시뮬레이션 모드

포트 선택에서 `(시뮬레이션)`을 선택하면 Arduino 없이도 동작합니다.  
카메라 미연결 시 ONNX 데모 추론으로 전체 흐름 테스트 가능합니다.
