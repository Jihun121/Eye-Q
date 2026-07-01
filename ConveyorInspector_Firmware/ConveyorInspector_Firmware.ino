/**
 * ConveyorInspector_Firmware.ino
 * ─────────────────────────────────────────────
 * TMC2208 / A4988 스텝모터 드라이버용 Arduino 펌웨어
 * C# WPF 앱과 JSON 시리얼 프로토콜로 통신합니다.
 *
 * 핀 배치 (기본값, 필요 시 수정):
 *   EN_PIN   → 10
 *   STEP_PIN → 9
 *   DIR_PIN  → 8
 *
 * 명령 포맷 (C# → Arduino):
 *   {"cmd":"move","dir":0,"steps":4800}   → 정방향 N 스텝
 *   {"cmd":"move","dir":1,"steps":4800}   → 역방향 N 스텝
 *   {"cmd":"stop"}                        → 즉시 정지
 *   {"cmd":"ping"}                        → 연결 확인
 *
 * 응답 포맷 (Arduino → C#):
 *   {"status":"done"}
 *   {"status":"error","msg":"..."}
 *   {"status":"pong"}
 *   {"status":"stopped"}
 *
 * 주의: ArduinoJson 라이브러리 필요
 *       라이브러리 관리자에서 "ArduinoJson" by Benoit Blanchon 설치
 */

#include <ArduinoJson.h>

// ── 핀 설정 ──────────────────────────────────────────────────────
const int EN_PIN   = 10;
const int STEP_PIN = 9;
const int DIR_PIN  = 8;

// ── 모터 파라미터 ─────────────────────────────────────────────────
const int STEPS_PER_REV = 1600;  // 8 마이크로스텝 기준 1바퀴
const int STEP_DELAY    = 5000;  // 펄스 간격 (μs) — 속도 조절

// ── 상태 변수 ──────────────────────────────────────────────────────
volatile bool stopRequested = false;

void setup() {
  Serial.begin(115200);
  Serial.setTimeout(5000);

  pinMode(EN_PIN,   OUTPUT);
  pinMode(STEP_PIN, OUTPUT);
  pinMode(DIR_PIN,  OUTPUT);

  // 초기 비활성화 (전류 차단)
  digitalWrite(EN_PIN, HIGH);
  digitalWrite(STEP_PIN, LOW);
  digitalWrite(DIR_PIN, LOW);

  // 준비 완료 신호
  Serial.println("{\"status\":\"ready\"}");
}

void loop() {
  if (!Serial.available()) return;

  String raw = Serial.readStringUntil('\n');
  raw.trim();
  if (raw.length() == 0) return;

  // JSON 파싱
  StaticJsonDocument<256> doc;
  DeserializationError err = deserializeJson(doc, raw);
  if (err) {
    Serial.println("{\"status\":\"error\",\"msg\":\"json parse failed\"}");
    return;
  }

  const char* cmd = doc["cmd"];

  // ── ping ──
  if (strcmp(cmd, "ping") == 0) {
    Serial.println("{\"status\":\"pong\"}");
    return;
  }

  // ── stop ──
  if (strcmp(cmd, "stop") == 0) {
    stopRequested = true;
    digitalWrite(EN_PIN, HIGH);
    Serial.println("{\"status\":\"stopped\"}");
    return;
  }

  // ── move ──
  if (strcmp(cmd, "move") == 0) {
    int  dir   = doc["dir"]   | 0;      // 0: 정방향, 1: 역방향
    long steps = doc["steps"] | (long)(STEPS_PER_REV * 3);

    stopRequested = false;
    executeMove(dir, steps);
    return;
  }

  Serial.println("{\"status\":\"error\",\"msg\":\"unknown cmd\"}");
}

void executeMove(int dir, long steps) {
  // 방향 설정
  digitalWrite(DIR_PIN, dir == 0 ? LOW : HIGH);

  // 모터 활성화
  digitalWrite(EN_PIN, LOW);
  delayMicroseconds(10);

  for (long i = 0; i < steps; i++) {
    if (stopRequested) {
      digitalWrite(EN_PIN, HIGH);
      Serial.println("{\"status\":\"stopped\"}");
      return;
    }
    digitalWrite(STEP_PIN, HIGH);
    delayMicroseconds(STEP_DELAY);
    digitalWrite(STEP_PIN, LOW);
    delayMicroseconds(STEP_DELAY);
  }

  // 모터 비활성화 (발열 감소)
  digitalWrite(EN_PIN, HIGH);

  Serial.println("{\"status\":\"done\"}");
}
