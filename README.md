# TSEA-TCC-RASTREABILIDADE-Smart-Tolls
# TSEA System - Sistema de Rastreabilidade e Controle Industrial

Este sistema foi desenvolvido para gerenciar e registrar a movimentação (Entrada, Saída, Devolução, etc.) de ferramentas industriais de forma automatizada por meio da leitura de códigos de barras. O foco principal é a agilidade operacional combinada com a segurança de privilégios de acesso.


## 🚀 Fluxo de Funcionamento Geral

O fluxo de operação foi projetado para rodar em terminais industriais (Kiosks) de forma linear e sem necessidade de interações constantes com teclado e mouse, dependendo majoritariamente do uso do **Leitor de Código de Barras**.

### 1. Tela de Autenticação (Login)
* **Operador Comum:** O colaborador aproxima seu crachá do leitor de código de barras. O sistema processa a leitura instantaneamente, valida a identidade e concede acesso direto ao painel operacional.
* **Administrador (Admin):** Usuários administradores possuem uma camada extra de segurança. Quando o crachá de administrador é bipado, o sistema identifica a regra de privilégio, abre dinamicamente um campo secundário em tela e solicita a inserção de uma senha secreta para liberar o acesso avançado.

### 2. Tela de Operações (Scan de Movimentação)
* Uma vez autenticado, o operador escolhe o modo operacional desejado (Ex: *MODO ENTRADA* ou *MODO SAÍDA*).
* A partir desse momento, o foco do sistema vai automaticamente para o campo de captura de dados.
* Cada ferramenta bipada dispara de maneira autônoma um gatilho de submissão para a API, registrando o histórico de movimentação do ativo associado àquele operador em tempo real. O campo é limpo imediatamente em seguida, preparando o terminal para a próxima ferramenta.


## ⚙️ Tecnologias Utilizadas
* **Backend:** .NET C# (Web API / ASP.NET Core) com persistência em banco de dados relacional.
* **Frontend:** Vanilla JavaScript, HTML5 e CSS3 moderno focado em alta performance e responsividade para telas industriais.
