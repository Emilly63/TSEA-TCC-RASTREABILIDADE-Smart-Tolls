#include <Servo.h>

#define LED2 2
#define LED1 6
#define SERVO_PIN 3
#define BOTAO 4
#define BUZZER 5

Servo trava;

bool aberto = false;
bool alertaAtivo = false;

void setup() {
  Serial.begin(9600); // Inicia a comunicação com o computador

  trava.attach(SERVO_PIN);
  trava.write(0); // Garante que começa trancado
  delay(500);

  pinMode(LED1, OUTPUT);
  pinMode(LED2, OUTPUT);
  pinMode(BOTAO, INPUT_PULLUP);
  pinMode(BUZZER, OUTPUT);

  digitalWrite(LED1, HIGH);
  digitalWrite(LED2, LOW);
  digitalWrite(BUZZER, LOW);
}

void loop() {
  // 📡 Escuta comandos vindos do sistema C# / Web
  if (Serial.available()) {
    String comando = Serial.readStringUntil('\n');
    comando.trim();

    // 1. Operador fez login -> Abre a porta
    if (comando == "LOGIN") {
      trava.write(90); // Abre o servo
      digitalWrite(LED1, LOW);
      digitalWrite(LED2, HIGH);
      noTone(BUZZER);
      aberto = true;
      alertaAtivo = false;
    }
    
    // 2. Operador saiu da conta OU sessão expirou -> Se a porta ainda estiver aberta, dá o alerta!
    else if (comando == "LOGOUT" || comando == "EXPIRADO") {
      if (aberto) {
        alertaAtivo = true; // Ativa o bip de esquecimento
      }
    }
  }

  // ⏰ Emite o alerta sonoro se o utilizador saiu/expirou e esqueceu-se da porta aberta
  if (alertaAtivo && aberto) {
    // Faz um som intermitente de aviso (bip... bip...)
    tone(BUZZER, 1500);
    delay(200);
    noTone(BUZZER);
    delay(200);
  }

  // 🔒 Fechamento manual pelo Botão Físico
  // O operador aperta o botão para fechar a porta física antes de sair ou ao sair
  if (digitalRead(BOTAO) == LOW) {
    trava.write(0); // Tranca o servo
    digitalWrite(LED1, HIGH);
    digitalWrite(LED2, LOW);
    noTone(BUZZER);
    
    aberto = false;
    alertaAtivo = false; // Desativa o alerta pois a porta foi devidamente fechada
    delay(300);
  }
}