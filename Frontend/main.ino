// ============================================================
// TSEA ENERGIA — Controle de Armário
// ============================================================

#include <Servo.h>

// ─── SAÍDAS ────────────────────────────────────────────────
const int BUZZER        = 2;
const int SERVO_PIN     = 3;
const int LED_VERMELHO  = 4;
const int LED_VERDE     = 5;
const int ILUMINACAO_1  = 6;
const int ILUMINACAO_2  = 7;

// ─── ENTRADAS ──────────────────────────────────────────────
const int CHAVE_PORTA   = 8;   // Chave física — desliga buzzer e fecha armário
const int SENSOR_1      = 9;   // Ferramenta 1
const int SENSOR_2      = 10;  // Ferramenta 2
const int SENSOR_3      = 11;  // Ferramenta 3
const int SENSOR_4      = 12;  // Ferramenta 4
const int SENSOR_MAG    = 13;  // Sensor magnético — indica se porta está fechada

// ─── SERVO ─────────────────────────────────────────────────
Servo servoArmario;
const int ANGULO_TRANCADO    = 90;
const int ANGULO_DESTRANCADO = 0;

// ─── ESTADO ────────────────────────────────────────────────
bool usuarioLogado = false;
bool modoAlarme    = false;

// ─── SENSORES DE FERRAMENTA ────────────────────────────────
const int pinosSensores[4]      = {SENSOR_1, SENSOR_2, SENSOR_3, SENSOR_4};
bool leituraAnterior[4]         = {HIGH, HIGH, HIGH, HIGH};
unsigned long ultimoDebounce[4] = {0, 0, 0, 0};
const unsigned long DEBOUNCE_MS = 250;

// ─── DEBOUNCE CHAVE ────────────────────────────────────────
unsigned long ultimoDebounceChave = 0;

// ─── SENSOR MAGNÉTICO ──────────────────────────────────────
bool portaAnterior          = HIGH;   // HIGH = aberta, LOW = fechada
unsigned long ultimoDebouncePorta = 0;
const unsigned long DEBOUNCE_PORTA_MS = 300;

// ─── CONTAGEM ──────────────────────────────────────────────
int ferramentasAusentes = 0;

// ============================================================
void setup() {
  Serial.begin(9600);

  pinMode(LED_VERMELHO, OUTPUT);
  pinMode(LED_VERDE,    OUTPUT);
  pinMode(BUZZER,       OUTPUT);
  pinMode(ILUMINACAO_1, OUTPUT);
  pinMode(ILUMINACAO_2, OUTPUT);

  pinMode(CHAVE_PORTA,  INPUT_PULLUP);
  pinMode(SENSOR_1,     INPUT_PULLUP);
  pinMode(SENSOR_2,     INPUT_PULLUP);
  pinMode(SENSOR_3,     INPUT_PULLUP);
  pinMode(SENSOR_4,     INPUT_PULLUP);
  pinMode(SENSOR_MAG,   INPUT_PULLUP);

  servoArmario.attach(SERVO_PIN);
  estadoInicial();

  Serial.println("ARDUINO_PRONTO");
}

// ============================================================
void loop() {

  // ── 1. Comandos do backend ─────────────────────────────
  if (Serial.available() > 0) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();

    if (cmd == "LOGIN") {
      modoAlarme          = false;
      usuarioLogado       = true;
      ferramentasAusentes = 0;
      noTone(BUZZER);
      abrirArmario();
    }
    else if (cmd == "LOGOUT") {
      usuarioLogado = false;
      iniciarAlarme(); // buzzer soa, aguarda chave física
    }
  }

  // ── 2. Sensores de ferramenta (só com armário aberto) ──
  if (usuarioLogado) {
    for (int i = 0; i < 4; i++) {
      bool leituraAtual = digitalRead(pinosSensores[i]);
      unsigned long agora = millis();

      if (leituraAtual != leituraAnterior[i] &&
          agora - ultimoDebounce[i] > DEBOUNCE_MS) {

        ultimoDebounce[i]  = agora;
        leituraAnterior[i] = leituraAtual;

        if (leituraAtual == LOW) {
          ferramentasAusentes++;
          Serial.print("FERRAMENTA_");
          Serial.print(i + 1);
          Serial.println("_RETIRADA");
          Serial.print("TOTAL_AUSENTES:");
          Serial.println(ferramentasAusentes);
        } else {
          if (ferramentasAusentes > 0) ferramentasAusentes--;
          Serial.print("FERRAMENTA_");
          Serial.print(i + 1);
          Serial.println("_DEVOLVIDA");
          Serial.print("TOTAL_AUSENTES:");
          Serial.println(ferramentasAusentes);
        }
      }
    }
  }

  // ── 3. Chave física — desliga buzzer e fecha armário ──
  if (modoAlarme) {
    bool chavePressionada = (digitalRead(CHAVE_PORTA) == LOW);
    unsigned long agora   = millis();

    if (chavePressionada &&
        agora - ultimoDebounceChave > DEBOUNCE_MS) {

      ultimoDebounceChave = agora;
      noTone(BUZZER);
      modoAlarme = false;
      fecharArmario();
      Serial.println("BOTAO_SAIDA_OK");
    }
  }

  // ── 4. Sensor magnético — reporta estado da porta ─────
  // LOW  = porta fechada (imã próximo)
  // HIGH = porta aberta  (imã afastado)
  bool portaAtual = digitalRead(SENSOR_MAG);
  unsigned long agora = millis();

  if (portaAtual != portaAnterior &&
      agora - ultimoDebouncePorta > DEBOUNCE_PORTA_MS) {

    ultimoDebouncePorta = agora;
    portaAnterior       = portaAtual;

    if (portaAtual == LOW) {
      // Porta acabou de fechar
      Serial.println("PORTA_FECHADA");
    } else {
      // Porta acabou de abrir — verifica se há login ativo
      if (usuarioLogado) {
        Serial.println("PORTA_ABERTA");
      } else {
        Serial.println("PORTA_SEM_LOGIN");
      }
    }
  }
}

// ============================================================
void estadoInicial() {
  servoArmario.write(ANGULO_TRANCADO);
  digitalWrite(LED_VERMELHO, HIGH);
  digitalWrite(LED_VERDE,    LOW);
  digitalWrite(ILUMINACAO_1, LOW);
  digitalWrite(ILUMINACAO_2, LOW);
  noTone(BUZZER);
}

void abrirArmario() {
  servoArmario.write(ANGULO_DESTRANCADO);
  digitalWrite(LED_VERMELHO, LOW);
  digitalWrite(LED_VERDE,    HIGH);
  digitalWrite(ILUMINACAO_1, HIGH);
  digitalWrite(ILUMINACAO_2, HIGH);
}

void fecharArmario() {
  servoArmario.write(ANGULO_TRANCADO);
  digitalWrite(LED_VERDE,    LOW);
  digitalWrite(LED_VERMELHO, HIGH);
  digitalWrite(ILUMINACAO_1, LOW);
  digitalWrite(ILUMINACAO_2, LOW);
}

void iniciarAlarme() {
  digitalWrite(LED_VERDE,    HIGH);
  digitalWrite(LED_VERMELHO, LOW);
  tone(BUZZER, 1000);
  modoAlarme = true;
}
