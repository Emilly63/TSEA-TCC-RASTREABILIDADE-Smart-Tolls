// ============================================================
// TSEA ENERGIA — Controle de Armário (Ajustado conforme o Vídeo)
// ============================================================

#include <Servo.h>

// --- PINOS (Ajustados para coincidir com a fiação física do seu vídeo) ---
const int LED_VERDE    = 6; // Trocado com o pino 6 para sincronizar com a caixa
const int LED_VERMELHO = 7; // Trocado com o pino 7 para sincronizar com a caixa
const int BUZZER       = 5;
const int SERVO_PIN    = 9;
const int BOTAO_PIN    = 2;

// --- SERVO ---
Servo servoArmario;
const int ANGULO_TRANCADO    = 0;   
const int ANGULO_DESTRANCADO = 90;  

// --- ESTADO ---
bool modoAlarme = false;

// --- DEBOUNCE ---
unsigned long ultimoTempoBotao = 0;
const unsigned long DEBOUNCE_MS = 300;

void setup() {
  Serial.begin(9600);

  pinMode(LED_VERDE,    OUTPUT);
  pinMode(LED_VERMELHO, OUTPUT);
  pinMode(BUZZER,       OUTPUT);
  pinMode(BOTAO_PIN,    INPUT_PULLUP); // Interruptor ligado entre o Pino 2 e o GND

  servoArmario.attach(SERVO_PIN);

  // Estado Inicial Securitário: Trancado (LED Vermelho aceso)
  trancarArmario();
  Serial.println("ARDUINO_PRONTO");
}

void loop() {
  // ── Lê comandos vindos do C# Backend ───────────────────────
  if (Serial.available() > 0) {
    String cmd = Serial.readStringUntil('\n');
    cmd.trim();

    if (cmd == "LOGIN") {
      modoAlarme = false;
      noTone(BUZZER); 
      destrancarArmario(); // Abre a tranca e inverte os LEDs
    }
    else if (cmd == "LOGOUT") {
      modoAlarme = true;
      acionarAlarmeAberto(); // Mantém aberto, ativa o LED indicador e liga o Buzzer
    }
  }

  // ── Verifica o clique no interruptor preto do topo ──
  if (modoAlarme) {
    bool botaoPressionado = (digitalRead(BOTAO_PIN) == LOW); 

    if (botaoPressionado) {
      unsigned long agora = millis();
      if (agora - ultimoTempoBotao > DEBOUNCE_MS) {
        ultimoTempoBotao = agora;
        
        // Finaliza o alarme quando o utilizador fecha a porta fisicamente
        noTone(BUZZER);
        modoAlarme = false;
        trancarArmario(); 
        
        Serial.println("BOTAO_SAIDA_OK");
      }
    }
  }
}

// ── Funções de Controlo Físico ───────────────────────────────

void trancarArmario() {
  servoArmario.write(ANGULO_TRANCADO);
  
  // Porta fechada com segurança: Vermelho Liga, Verde Apaga
  digitalWrite(LED_VERDE,    LOW);
  digitalWrite(LED_VERMELHO, HIGH);
}

void destrancarArmario() {
  servoArmario.write(ANGULO_DESTRANCADO);
  
  // Porta liberada para acesso: Verde Liga, Vermelho Apaga
  digitalWrite(LED_VERDE,    HIGH);
  digitalWrite(LED_VERMELHO, LOW);
}

void acionarAlarmeAberto() {
  // Igual ao vídeo: Ao fazer Logout, o LED Verde brilha e o alarme soa
  digitalWrite(LED_VERDE,    HIGH); 
  digitalWrite(LED_VERMELHO, LOW);
  
  // Emite o som contínuo no buzzer (ajuste a frequência se necessário)
  tone(BUZZER, 1000); 
}