using System.IO.Ports;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using OfficeOpenXml;
using OfficeOpenXml.Style;

// --- CONFIGURAÇÃO DO BUILDER ---
var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURAÇÃO DE CORS ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// --- CONFIGURAÇÃO DO BANCO DE DADOS (POSTGRESQL) ---
var dbHost = Environment.GetEnvironmentVariable("DB_HOST") ?? "localhost";
var dbPort = Environment.GetEnvironmentVariable("DB_PORT") ?? "5432";
var dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "RASTREABILIDADES_TSEA";
var dbUser = Environment.GetEnvironmentVariable("DB_USER") ?? "postgres";
var dbPassword = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";

var connectionString = $"Host={dbHost};Port={dbPort};Database={dbName};Username={dbUser};Password={dbPassword};";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// Armazena tokens temporários de reset de senha
var resetTokens = new Dictionary<string, (string Barcode, DateTime Expiry)>();

//  INICIALIZA A PORTA SERIAL GLOBAL
ArduinoSerial.Inicializar();

var app = builder.Build();
app.UseCors("AllowAll");
// ─── THREAD DE LEITURA SERIAL CONTÍNUA ───────────────────
var notificacoesPendentes = new List<AvisoPendente>();
var proximoAvisoId = 1;

var mapeamentoSensores = new Dictionary<string, int>
{
    { "FERRAMENTA_1", 1 },
    { "FERRAMENTA_2", 2 },
    { "FERRAMENTA_3", 3 },
    { "FERRAMENTA_4", 4 }
};

_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            if (ArduinoSerial.Porta != null && ArduinoSerial.Porta.IsOpen && ArduinoSerial.Porta.BytesToRead > 0)
            {
                string resposta = ArduinoSerial.Porta.ReadLine().Trim();
                Console.WriteLine($"[Arduino] Recebido: {resposta}");

                if (resposta == "BOTAO_SAIDA_OK")
                {
                    BotaoControle.ConfirmadoSaida = true;
                    continue;
                }

                // Sensor Magnético - Pino 13
                if (resposta == "PORTA_FECHADA")
                {
                    EstadoPorta.Status = "FECHADA";
                    EstadoPorta.AlarmeSemLogin = false;
                    EstadoPorta.UltimaAtualizacao = DateTime.Now;
                    Console.WriteLine("[Porta] Estado: FECHADA");
                    continue;
                }
                if (resposta == "PORTA_ABERTA")
                {
                    EstadoPorta.Status = "ABERTA";
                    EstadoPorta.UltimaAtualizacao = DateTime.Now;
                    Console.WriteLine("[Porta] Estado: ABERTA");
                    continue;
                }
                if (resposta == "PORTA_SEM_LOGIN")
                {
                    EstadoPorta.Status = "ALERTA_SEM_LOGIN";
                    EstadoPorta.AlarmeSemLogin = true;
                    EstadoPorta.UltimaAtualizacao = DateTime.Now;
                    Console.WriteLine("[ALERTA] Porta aberta sem login ativo!");
                    continue;
                }

                foreach (var sensor in mapeamentoSensores)
                {
                    if (resposta == $"{sensor.Key}_RETIRADA" || resposta == $"{sensor.Key}_DEVOLVIDA")
                    {
                        bool retirada = resposta.EndsWith("_RETIRADA");

                        using var scope = app.Services.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                        var ferramenta = await db.Ferramentas.FindAsync(sensor.Value);
                        if (ferramenta != null)
                        {
                            if (retirada)
                            {
                                // Verifica se havia colaborador registrado (bipe no sistema)
                                bool semRegistro = string.IsNullOrEmpty(ferramenta.Colaborador)
                                                  && ferramenta.Status != "EM_USO";

                                if (semRegistro)
                                {
                                    // Ferramenta saiu do armário sem nenhum bipe — gera alerta
                                    ferramenta.Status = "AUSENTE_SEM_REGISTRO";
                                    await db.SaveChangesAsync();

                                    var codigoFerramenta = !string.IsNullOrWhiteSpace(ferramenta.CodigoBarras)
                                        ? ferramenta.CodigoBarras
                                        : $"TSEA-{ferramenta.Id:D3}";

                                    // Busca o último operador com sessão ativa (DataSaida == null)
                                    var sessaoAtiva = await db.LogsAcesso
                                        .Where(l => l.DataSaida == null)
                                        .OrderByDescending(l => l.DataEntrada)
                                        .FirstOrDefaultAsync();

                                    var operadorSuspeito = sessaoAtiva != null
                                        ? await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == sessaoAtiva.UsuarioId)
                                        : null;

                                    var operadorId    = operadorSuspeito?.CodigoBarras ?? "";
                                    var operadorNome  = operadorSuspeito?.Nome ?? "DESCONHECIDO";

                                    // Gera aviso DIRETO no operador com sessão ativa
                                    if (!string.IsNullOrEmpty(operadorId))
                                    {
                                        notificacoesPendentes.Add(new AvisoPendente
                                        {
                                            Id           = proximoAvisoId++,
                                            FerramentaId = ferramenta.Id,
                                            UsuarioId    = operadorId,
                                            Mensagem     = $"CONFIRMAR_RETIRADA|{codigoFerramenta}|{ferramenta.Descricao}",
                                            Lido         = false,
                                            CriadoEm    = DateTime.UtcNow
                                        });
                                    }

                                    // Também avisa os admins (para o sininho da oficina)
                                    var admins = await db.Usuarios
                                        .Where(u => u.Setor == "ADMIN" && u.Status == "ATIVO")
                                        .ToListAsync();

                                    foreach (var admin in admins)
                                    {
                                        notificacoesPendentes.Add(new AvisoPendente
                                        {
                                            Id           = proximoAvisoId++,
                                            FerramentaId = ferramenta.Id,
                                            UsuarioId    = admin.CodigoBarras,
                                            Mensagem     = $"RETIRADA_SEM_REGISTRO|{codigoFerramenta}|{ferramenta.Descricao}|{operadorId}|{operadorNome}",
                                            Lido         = false,
                                            CriadoEm    = DateTime.UtcNow
                                        });
                                    }

                                    Console.WriteLine($"[ALERTA] {ferramenta.Descricao} retirada sem registro! Suspeito: {operadorNome} ({operadorId})");
                                }
                                else
                                {
                                    // Retirada normal — havia bipe e colaborador registrado
                                    ferramenta.Status = "EM_USO";
                                    await db.SaveChangesAsync();
                                    Console.WriteLine($"[Sensor] {ferramenta.Descricao} → EM_USO");
                                }
                            }
                            else
                            {
                                // Ferramenta devolvida — normaliza e cancela alertas pendentes
                                ferramenta.Status = "DISPONIVEL";
                                ferramenta.Colaborador = null;
                                await db.SaveChangesAsync();

                                var alertasPendentes = notificacoesPendentes
                                    .Where(a => a.FerramentaId == ferramenta.Id && !a.Lido
                                             && a.Mensagem.StartsWith("RETIRADA_SEM_REGISTRO"))
                                    .ToList();
                                foreach (var a in alertasPendentes) a.Lido = true;

                                Console.WriteLine($"[Sensor] {ferramenta.Descricao} → DISPONIVEL");
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Serial Thread] {ex.Message}");
        }

        await Task.Delay(100); // verifica a cada 100ms
    }
});



var frontendRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Frontend"));
if (Directory.Exists(frontendRoot))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new PhysicalFileProvider(frontendRoot),
        RequestPath = string.Empty
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(frontendRoot),
        RequestPath = string.Empty
    });
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    // PostgreSQL automatically applies EF Core migrations/schema via EnsureCreated
}

var smtpConfig = builder.Configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new SmtpSettings();

// --- ALL LOCAL FUNCTIONS (MUST BE BEFORE ENDPOINT MAPPINGS) ---

static string HashPassword(string password)
{
    const int iterations = 100_000;
    byte[] salt = RandomNumberGenerator.GetBytes(16);
    byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
    return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
}

static string NormalizeBarcode(string? barcode)
{
    return string.IsNullOrWhiteSpace(barcode) ? string.Empty : barcode.Trim().ToUpperInvariant();
}

static bool VerifyPassword(string password, string storedHash)
{
    if (string.IsNullOrEmpty(storedHash)) return false;
    var parts = storedHash.Split('.', 3);
    if (parts.Length != 3)
    {
        return password == storedHash;
    }

    if (!int.TryParse(parts[0], out var iterations)) return false;
    var salt = Convert.FromBase64String(parts[1]);
    var expected = Convert.FromBase64String(parts[2]);

    var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
    return CryptographicOperations.FixedTimeEquals(actual, expected);
}

static async Task<bool> SendEmailAsync(SmtpSettings settings, string toEmail, string subject, string body)
{
    try
    {
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.Username, settings.Password)
        };

        using var message = new MailMessage(settings.From ?? "no-reply@tsea.local", toEmail, subject, body);
        await client.SendMailAsync(message);
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"SMTP ERRO: {ex.Message}");
        Console.Error.WriteLine($"SMTP INNER: {ex.InnerException?.Message}");
        return false;
    }
}

// --- FUNCTION TO SEND ARDUINO COMMANDS ---
static void EnviarComandoArduino(string comando)
{
    try
    {
        if (ArduinoSerial.Porta != null && ArduinoSerial.Porta.IsOpen)
        {
            ArduinoSerial.Porta.WriteLine(comando); 
            Console.WriteLine($"[Serial] Comando enviado com sucesso: {comando}");
        }
        else
        {
            Console.WriteLine("[Serial Error] Porta não está aberta.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Serial Error] Erro ao enviar comando: {ex.Message}");
    }
}

// --- HELPERS RELATÓRIO (MUST BE BEFORE ENDPOINT MAPPINGS) ---
static (DateTime inicio, DateTime fim, string? erro) ResolverPeriodo(string tipo, string? dataInicio)
{
    var hoje = DateTime.Today;
    DateTime inicio, fim;
    switch (tipo?.ToUpper())
    {
        case "DIARIO":
            inicio = string.IsNullOrWhiteSpace(dataInicio) ? hoje : DateTime.Parse(dataInicio);
            fim = inicio.AddDays(1).AddSeconds(-1);
            break;
        case "MENSAL":
            if (!string.IsNullOrWhiteSpace(dataInicio) && DateTime.TryParse(dataInicio, out var mRef))
            { inicio = new DateTime(mRef.Year, mRef.Month, 1); fim = inicio.AddMonths(1).AddSeconds(-1); }
            else
            { inicio = new DateTime(hoje.Year, hoje.Month, 1); fim = inicio.AddMonths(1).AddSeconds(-1); }
            break;
        case "ANUAL":
            var ano = string.IsNullOrWhiteSpace(dataInicio) ? hoje.Year : DateTime.Parse(dataInicio).Year;
            inicio = new DateTime(ano, 1, 1); fim = new DateTime(ano, 12, 31, 23, 59, 59);
            break;
        default:
            return (DateTime.MinValue, DateTime.MinValue, "Tipo inválido. Use: DIARIO, MENSAL ou ANUAL.");
    }
    return (inicio, fim, null);
}

static async Task<(
    List<dynamic> movs,
    List<dynamic> logs,
    List<dynamic> atrasadas,
    dynamic resumo,
    dynamic resumoFerr
)> ColetarDadosRelatorio(AppDbContext db, DateTime inicio, DateTime fim)
{
    var movimentacoes = await db.Movimentacoes
        .Where(m => m.DataRetirada >= inicio && m.DataRetirada <= fim).ToListAsync();
    var ferramentas = await db.Ferramentas.AsNoTracking().ToListAsync();
    var usuarios    = await db.Usuarios.AsNoTracking().ToListAsync();
    var logsAcesso  = await db.LogsAcesso
        .Where(l => l.DataEntrada >= inicio && l.DataEntrada <= fim).ToListAsync();

    var atrasadas = ferramentas
        .Where(f => f.Status == "EM_USO" || f.Status == "ATRASADO")
        .Select(f => (dynamic)new {
            f.Id, f.Descricao, f.CodigoBarras, f.Setor, f.Status, f.Colaborador
        }).ToList();

    var movs = movimentacoes.Select(m => {
        var ferr = ferramentas.FirstOrDefault(f => f.Id == m.FerramentaId);
        var user = usuarios.FirstOrDefault(u => u.CodigoBarras == m.UsuarioId);
        var dur  = m.DataDevolucao.HasValue
            ? (m.DataDevolucao.Value - m.DataRetirada).TotalMinutes
            : (DateTime.Now - m.DataRetirada).TotalMinutes;
        return (dynamic)new {
            m.Id,
            Ferramenta = ferr?.Descricao ?? "Desconhecida",
            CodigoBarras = ferr?.CodigoBarras ?? $"TSEA-{m.FerramentaId:D3}",
            Setor = ferr?.Setor ?? "—",
            Colaborador = user?.Nome ?? m.UsuarioId,
            m.DataRetirada,
            DataDevolucao = m.DataDevolucao,
            DevolvidaManualmente = m.DataDevolucao.HasValue,
            DuracaoMinutos = Math.Round(dur, 1),
            Status = m.DataDevolucao.HasValue ? "DEVOLVIDA" : "EM ABERTO"
        };
    }).ToList();

    var logs = logsAcesso.Select(l => {
        var user = usuarios.FirstOrDefault(u => u.CodigoBarras == l.UsuarioId);
        return (dynamic)new {
            l.Id,
            Colaborador = user?.Nome ?? l.UsuarioId,
            l.UsuarioId,
            l.DataEntrada,
            DataSaida = l.DataSaida,
            DuracaoMinutos = l.DataSaida.HasValue
                ? Math.Round((l.DataSaida.Value - l.DataEntrada).TotalMinutes, 1)
                : Math.Round((DateTime.Now - l.DataEntrada).TotalMinutes, 1),
            MotivoSaida = l.MotivoSaida,
            l.StatusAcesso
        };
    }).ToList();

    dynamic resumo = new {
        TotalMovimentacoes  = movimentacoes.Count,
        Devolvidas          = movimentacoes.Count(m => m.DataDevolucao.HasValue),
        EmAberto            = movimentacoes.Count(m => !m.DataDevolucao.HasValue),
        FerramentasAtrasadas = atrasadas.Count,
        TotalLogsAcesso     = logsAcesso.Count,
        UsuariosUnicos      = movimentacoes.Select(m => m.UsuarioId).Distinct().Count()
    };
    dynamic resumoFerr = new {
        Total       = ferramentas.Count,
        Disponiveis = ferramentas.Count(f => f.Status == "DISPONIVEL"),
        EmUso       = ferramentas.Count(f => f.Status == "EM_USO"),
        Manutencao  = ferramentas.Count(f => f.Status == "MANUTENCAO")
    };

    return (movs, logs, atrasadas, resumo, resumoFerr);
}

// --- ENDPOINTS ---

app.MapGet("/debug/smtp", () => new {
    host = smtpConfig.Host,
    port = smtpConfig.Port,
    user = smtpConfig.Username,
    hasPassword = !string.IsNullOrWhiteSpace(smtpConfig.Password),
    from = smtpConfig.From
});

app.MapPost("/usuarios/cadastrar", async (CadastroRequest req, AppDbContext db) =>
{
    string barcodeUpper = req.Barcode.ToUpper();

    if (req.Setor == "ADMIN" && !barcodeUpper.EndsWith("A"))
        return Results.BadRequest("Crachá administrativo inválido (deve terminar com A).");

    if (req.Setor != "ADMIN" && barcodeUpper.EndsWith("A"))
        return Results.BadRequest("Crachás terminados em 'A' são exclusivos para Administradores.");

    var existe = await db.Usuarios.AnyAsync(u => u.CodigoBarras == req.Barcode);
    if (existe) return Results.BadRequest("Crachá já cadastrado.");

    if (req.Setor == "ADMIN" && string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest("Senha obrigatória para cadastro de administrador.");

    if (req.Setor == "ADMIN" && string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest("Email obrigatório para cadastro de administrador.");

    var novoUsuario = new Usuario {
        Nome = req.Nome,
        CodigoBarras = barcodeUpper,
        Setor = req.Setor,
        Status = "ATIVO",
        PasswordHash = req.Setor == "ADMIN" ? HashPassword(req.Password!) : null,
        Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email
    };

    db.Usuarios.Add(novoUsuario);
    await db.SaveChangesAsync();
    return Results.Ok("Usuário cadastrado!");
});

app.MapGet("/smtp/check", () =>
{
    var configured = !string.IsNullOrWhiteSpace(smtpConfig.Host)
        && !smtpConfig.Host.Contains("example.com")
        && smtpConfig.Port > 0
        && !string.IsNullOrWhiteSpace(smtpConfig.Username)
        && !string.IsNullOrWhiteSpace(smtpConfig.Password);

    return Results.Ok(new { configured });
});

app.MapPost("/senha/recuperar", async (PasswordRecoveryRequest req, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Barcode))
        return Results.BadRequest("Crachá é obrigatório.");

    var barcodeUpper = req.Barcode.ToUpper();
    if (!barcodeUpper.EndsWith("A"))
        return Results.BadRequest("Disponível apenas para administradores.");

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == barcodeUpper);
    if (usuario == null) return Results.NotFound("Usuário não cadastrado.");
    if (usuario.Setor != "ADMIN") return Results.BadRequest("Disponível apenas para administradores.");
    if (string.IsNullOrWhiteSpace(usuario.Email)) return Results.BadRequest("Email não cadastrado.");

    var codigo = new Random().Next(100000, 999999).ToString();
    resetTokens[codigo] = (barcodeUpper, DateTime.Now.AddMinutes(10));

    var subject = "Código de recuperação - TSEA";
    var body = $@"Olá {usuario.Nome}, Seu código é: {codigo}";

    await SendEmailAsync(smtpConfig, usuario.Email, subject, body);
    return Results.Ok(new { mensagem = $"Código enviado para {usuario.Email}." });
});

app.MapPost("/senha/redefinir", async (HttpContext http, AppDbContext db) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();

    RedefinirSenhaRequest? req = System.Text.Json.JsonSerializer.Deserialize<RedefinirSenhaRequest>(body,
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (req == null || string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.NovaSenha))
        return Results.BadRequest("Campos obrigatórios ausentes.");

    if (!resetTokens.TryGetValue(req.Token, out var entry) || DateTime.Now > entry.Expiry)
    {
        return Results.BadRequest("Código inválido ou expirado.");
    }

    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == entry.Barcode);
    if (usuario != null)
    {
        usuario.PasswordHash = HashPassword(req.NovaSenha);
        await db.SaveChangesAsync();
        resetTokens.Remove(req.Token);
        return Results.Ok(new { mensagem = "Senha redefinida com sucesso!" });
    }
    return Results.NotFound("Usuário não encontrado.");
});

app.MapPost("/movimentacao/retirar", async (MovimentacaoReq req, AppDbContext db) =>
{
    try 
    {
        Ferramenta? ferramenta = null;

        // 1. Tenta pelo ID numérico direto
        if (req.FerramentaId.HasValue)
            ferramenta = await db.Ferramentas.FindAsync(req.FerramentaId.Value);

        // 2. Tenta pelo CodigoBarras exato
        if (ferramenta == null && !string.IsNullOrWhiteSpace(req.FerramentaCodigoBarras))
        {
            var codigoNorm = req.FerramentaCodigoBarras.Trim().ToUpperInvariant();
            ferramenta = await db.Ferramentas.FirstOrDefaultAsync(f => f.CodigoBarras == codigoNorm);

            // 3. Extrai número do padrão TSEA-001 e busca pelo ID
            if (ferramenta == null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(codigoNorm, @"^TSEA-0*(\d+)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var idExtraido))
                {
                    ferramenta = await db.Ferramentas.FindAsync(idExtraido);
                    // Auto-preenche o CodigoBarras se estava nulo
                    if (ferramenta != null && string.IsNullOrWhiteSpace(ferramenta.CodigoBarras))
                    {
                        ferramenta.CodigoBarras = codigoNorm;
                    }
                }
            }
        }

        if (ferramenta == null) return Results.BadRequest(new { erro = "Ferramenta não encontrada." });

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == req.UsuarioId);
        if (usuario == null) return Results.BadRequest(new { erro = "Usuário não encontrado." });

        // Bloqueia ferramentas do almoxarifado
        var setorNorm = (ferramenta.Setor ?? "").Trim().ToUpperInvariant();
        if (setorNorm == "GERAL" || setorNorm == "ALMOXERIFADO" || setorNorm == "")
            return Results.BadRequest(new { erro = "Esta ferramenta está no almoxarifado e não pode ser retirada por aqui." });

        if (ferramenta.Status == "EM_USO")
            return Results.BadRequest(new { erro = "Esta ferramenta já está em uso." });

        if (ferramenta.Status == "MANUTENCAO")
            return Results.BadRequest(new { erro = "Esta ferramenta está em manutenção." });

        ferramenta.Status = "EM_USO";
        ferramenta.Colaborador = usuario.Nome;

        db.Movimentacoes.Add(new Movimentacoes { FerramentaId = ferramenta.Id, UsuarioId = req.UsuarioId, DataRetirada = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return Results.Ok(new { colaborador = usuario.Nome });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/movimentacao/devolver", async (MovimentacaoReq req, AppDbContext db) =>
{
    Ferramenta? ferramenta = null;

    if (req.FerramentaId.HasValue)
        ferramenta = await db.Ferramentas.FindAsync(req.FerramentaId.Value);

    if (ferramenta == null && !string.IsNullOrWhiteSpace(req.FerramentaCodigoBarras))
    {
        var codigoNorm = req.FerramentaCodigoBarras.Trim().ToUpperInvariant();
        ferramenta = await db.Ferramentas.FirstOrDefaultAsync(f => f.CodigoBarras == codigoNorm);
    }

    if (ferramenta == null) return Results.BadRequest(new { erro = "Ferramenta não encontrada." });
    if (ferramenta.Status != "EM_USO" && ferramenta.Status != "AUSENTE_SEM_REGISTRO")
        return Results.BadRequest(new { erro = "Esta ferramenta não está em uso." });

    ferramenta.Status = "DISPONIVEL";
    ferramenta.Colaborador = null;

    var mov = await db.Movimentacoes.FirstOrDefaultAsync(m => m.FerramentaId == ferramenta.Id && m.DataDevolucao == null);
    if (mov != null) mov.DataDevolucao = DateTime.UtcNow;
    
    await db.SaveChangesAsync();
    return Results.Ok("Devolvida com sucesso!");
});

// --- LISTAR USUÁRIOS (para o admin buscar CodigoBarras pelo nome) ---
app.MapGet("/usuarios", async (AppDbContext db) =>
{
    var usuarios = await db.Usuarios
        .Where(u => u.Status == "ATIVO")
        .Select(u => new { u.Id, u.Nome, u.CodigoBarras, u.Setor, u.Status })
        .ToListAsync();
    return Results.Ok(usuarios);
});

// --- GERENCIAR ACESSO: LISTAR TODOS OS USUÁRIOS (ATIVOS E INATIVOS) ---
app.MapGet("/usuarios/todos", async (AppDbContext db) =>
{
    var usuarios = await db.Usuarios
        .Select(u => new { u.Id, u.Nome, u.CodigoBarras, u.Setor, u.Status, u.Email })
        .OrderBy(u => u.Nome)
        .ToListAsync();
    return Results.Ok(usuarios);
});

// --- GERENCIAR ACESSO: ATIVAR / DESATIVAR USUÁRIO ---
app.MapPut("/usuarios/{id:int}/status", async (int id, HttpContext http, AppDbContext db) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = System.Text.Json.JsonSerializer.Deserialize<AlterarStatusUsuarioRequest>(body,
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (req == null || string.IsNullOrWhiteSpace(req.Status))
        return Results.BadRequest("Status inválido.");

    var novoStatus = req.Status.ToUpperInvariant();
    if (novoStatus != "ATIVO" && novoStatus != "INATIVO")
        return Results.BadRequest("Status deve ser ATIVO ou INATIVO.");

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario == null) return Results.NotFound("Usuário não encontrado.");

    usuario.Status = novoStatus;
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = $"Usuário {(novoStatus == "ATIVO" ? "ativado" : "desativado")} com sucesso." });
});

// --- GERENCIAR ACESSO: EDITAR NOME DO USUÁRIO ---
app.MapPut("/usuarios/{id:int}/editar", async (int id, HttpContext http, AppDbContext db) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = System.Text.Json.JsonSerializer.Deserialize<EditarUsuarioRequest>(body,
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    if (req == null || string.IsNullOrWhiteSpace(req.Nome))
        return Results.BadRequest("Nome é obrigatório.");

    var usuario = await db.Usuarios.FindAsync(id);
    if (usuario == null) return Results.NotFound("Usuário não encontrado.");

    usuario.Nome = req.Nome.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Usuário atualizado com sucesso." });
});

app.MapGet("/ferramentas", async (AppDbContext db) => 
{
    var ferramentas = await db.Ferramentas.AsNoTracking().ToListAsync();
    return Results.Ok(ferramentas);
});

// --- CADASTRAR NOVA FERRAMENTA ---
app.MapPost("/ferramentas", async (Ferramenta nova, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(nova.Descricao))
        return Results.BadRequest(new { erro = "Descrição obrigatória." });

    if (!string.IsNullOrWhiteSpace(nova.CodigoBarras))
    {
        var duplicado = await db.Ferramentas.AnyAsync(f => f.CodigoBarras == nova.CodigoBarras);
        if (duplicado) return Results.BadRequest(new { erro = "Código de barras já cadastrado." });
    }

    nova.Status = "DISPONIVEL";
    nova.VidaUtil = 100;
    db.Ferramentas.Add(nova);
    await db.SaveChangesAsync();
    return Results.Ok(nova);
});

// --- ATUALIZAR FERRAMENTA (setor, descrição, status, vida útil) ---
app.MapPut("/ferramentas/{id:int}", async (int id, FerramentaUpdateRequest req, AppDbContext db) =>
{
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    if (!string.IsNullOrWhiteSpace(req.Descricao))  ferramenta.Descricao  = req.Descricao;
    if (!string.IsNullOrWhiteSpace(req.Setor))       ferramenta.Setor      = req.Setor;
    if (!string.IsNullOrWhiteSpace(req.Status))      ferramenta.Status     = req.Status;
    if (!string.IsNullOrWhiteSpace(req.CodigoBarras)) ferramenta.CodigoBarras = req.CodigoBarras;
    if (req.VidaUtil.HasValue)                       ferramenta.VidaUtil   = req.VidaUtil.Value;

    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta atualizada com sucesso!" });
});

// --- MARCAR FERRAMENTA COMO MANUTENÇÃO ---
app.MapPost("/ferramentas/{id:int}/manutencao", async (int id, AppDbContext db) =>
{
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    ferramenta.Status = "MANUTENCAO";
    ferramenta.Colaborador = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta enviada para manutenção." });
});

// --- EXCLUIR FERRAMENTA ---
app.MapDelete("/ferramentas/{id:int}", async (int id, AppDbContext db) =>
{
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    if (ferramenta.Status == "EM_USO")
        return Results.BadRequest(new { erro = "Não é possível excluir uma ferramenta em uso." });

    db.Ferramentas.Remove(ferramenta);
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta excluída com sucesso." });
});

// --- LIMPAR COLABORADORES INVÁLIDOS ---
app.MapPost("/ferramentas/limpar-colaboradores", async (AppDbContext db) =>
{
    var codigosValidos = await db.Usuarios.Select(u => u.CodigoBarras).ToListAsync();
    var ferramentasComColaborador = await db.Ferramentas
        .Where(f => f.Colaborador != null)
        .ToListAsync();

    int count = 0;
    foreach (var f in ferramentasComColaborador)
    {
        var colaboradorExiste = await db.Usuarios.AnyAsync(u => u.Nome == f.Colaborador);
        if (!colaboradorExiste)
        {
            f.Colaborador = null;
            f.Status = "DISPONIVEL";
            count++;
        }
    }

    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = $"{count} ferramenta(s) corrigida(s)." });
});

// --- ROTA LOGIN CORRIGIDA ---
// --- ROTA LOGIN (CORRIGIDA) ---
app.MapPost("/login", async (LoginRequest req, AppDbContext db) =>
{
    var barcodeNorm = req.Barcode.Trim().ToUpperInvariant();
    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == barcodeNorm);

    if (usuario == null) 
        return Results.NotFound("Usuário não cadastrado.");

    if (usuario.Status != "ATIVO") 
        return Results.BadRequest("Usuário inativo.");

    if (usuario.Setor == "ADMIN")
    {
        if (string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest("Senha obrigatória para login de administrador.");

        if (!VerifyPassword(req.Password, usuario.PasswordHash ?? ""))
            return Results.Unauthorized();
    }

    // Envia o comando direto para o Arduino mexer o Servo e ligar o LED Verde
    EnviarComandoArduino("LOGIN");

    db.LogsAcesso.Add(new LogAcesso { 
        UsuarioId = usuario.CodigoBarras, 
        DataEntrada = DateTime.UtcNow, 
        StatusAcesso = "ATIVO" 
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { nome = usuario.Nome, tipo = usuario.Setor, id = usuario.CodigoBarras });
});

// --- ROTA LOGOUT INTELIGENTE CORRIGIDA ---
// --- ROTA LOGOUT (CORRIGIDA - SEM TRAVAMENTOS) ---
app.MapPost("/logout", async (LogoutRequest req, AppDbContext db) =>
{
    var ultimoLog = await db.LogsAcesso
        .Where(l => l.UsuarioId == req.UsuarioId && l.DataSaida == null)
        .OrderByDescending(l => l.DataEntrada)
        .FirstOrDefaultAsync();

    if (ultimoLog != null)
    {
        ultimoLog.DataSaida = DateTime.UtcNow;
        ultimoLog.MotivoSaida = req.Motivo;
    }
    await db.SaveChangesAsync();

    // Notifica o Arduino que a conta deslogou para iniciar o buzzer
    EnviarComandoArduino("LOGOUT");

    return Results.Ok();
});

// --- ROTA DE CONSULTA DO BOTÃO ---
// --- LEITURA SERIAL DO ARDUINO (SENSORES DE FERRAMENTA) ---

// --- AVISOS ---

// Criar aviso
app.MapPost("/avisos", (AvisoRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.UsuarioId))
        return Results.BadRequest(new { erro = "UsuarioId é obrigatório." });

    var aviso = new AvisoPendente
    {
        Id = proximoAvisoId++,
        FerramentaId = req.FerramentaId,
        UsuarioId = req.UsuarioId.Trim().ToUpperInvariant(),
        Mensagem = req.Mensagem ?? "Você recebeu um aviso do administrador.",
        Lido = false,
        CriadoEm = DateTime.UtcNow
    };
    notificacoesPendentes.Add(aviso);
    return Results.Ok(new { mensagem = "Aviso enviado com sucesso.", id = aviso.Id });
});

// Buscar avisos de um operador
app.MapGet("/avisos/{usuarioId}", (string usuarioId) =>
{
    var norm = usuarioId.Trim().ToUpperInvariant();
    var avisos = notificacoesPendentes
        .Where(a => a.UsuarioId == norm && !a.Lido)
        .OrderByDescending(a => a.CriadoEm)
        .ToList();
    return Results.Ok(avisos);
});

// Marcar aviso como lido
app.MapPost("/avisos/{id:int}/lido", (int id) =>
{
    var aviso = notificacoesPendentes.FirstOrDefault(a => a.Id == id);
    if (aviso == null) return Results.NotFound(new { erro = "Aviso não encontrado." });
    aviso.Lido = true;
    return Results.Ok(new { mensagem = "Aviso marcado como lido." });
});

// --- ENDPOINT STATUS DA PORTA (Sensor Magnético) ---
app.MapGet("/status/porta", () =>
{
    return Results.Ok(new {
        status = EstadoPorta.Status,
        alarmeSemLogin = EstadoPorta.AlarmeSemLogin,
        ultimaAtualizacao = EstadoPorta.UltimaAtualizacao.ToString("yyyy-MM-ddTHH:mm:ss")
    });
});

// --- ENDPOINT PARA SILENCIAR ALERTA (admin reconheceu) ---
app.MapPost("/status/porta/silenciar", () =>
{
    EstadoPorta.AlarmeSemLogin = false;
    if (EstadoPorta.Status == "ALERTA_SEM_LOGIN")
        EstadoPorta.Status = "ABERTA";
    return Results.Ok(new { mensagem = "Alerta silenciado." });
});

// --- ENDPOINT DE RELATÓRIO (JSON) ---
app.MapGet("/relatorio", async (string tipo, string? dataInicio, string? dateFim, AppDbContext db) =>
{
    var (inicio, fim, erro) = ResolverPeriodo(tipo, dataInicio);
    if (erro != null) return Results.BadRequest(new { erro });

    var (movComDetalhes, logsComDetalhes, ferramentasAtrasadas, resumo, resumoFerramentas) =
        await ColetarDadosRelatorio(db, inicio, fim);

    return Results.Ok(new {
        Periodo = tipo.ToUpper(),
        DataInicio = inicio,
        DataFim = fim,
        GeradoEm = DateTime.Now,
        Resumo = resumo,
        Movimentacoes = movComDetalhes,
        LogsAcesso = logsComDetalhes,
        FerramentasAtrasadas = ferramentasAtrasadas,
        ResumoFerramentas = resumoFerramentas
    });
});

// --- ENDPOINT DE RELATÓRIO XLSX FORMATADO ---
app.MapGet("/relatorio/xlsx", async (string tipo, string? dataInicio, AppDbContext db) =>
{
    var (inicio, fim, erro) = ResolverPeriodo(tipo, dataInicio);
    if (erro != null) return Results.BadRequest(new { erro });

    var (movs, logs, atrasadas, resumo, resumoFerr) =
        await ColetarDadosRelatorio(db, inicio, fim);

    ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    using var pkg = new ExcelPackage();

    //  Cores da marca TSEA 
    var vermelho    = System.Drawing.Color.FromArgb(0xCC, 0x00, 0x00);
    var vermelhoClaro = System.Drawing.Color.FromArgb(0xFF, 0xE5, 0xE5);
    var cinzaHeader = System.Drawing.Color.FromArgb(0x1A, 0x1A, 0x1A);
    var cinzaLinha  = System.Drawing.Color.FromArgb(0xF5, 0xF5, 0xF5);
    var verdeStatus = System.Drawing.Color.FromArgb(0x00, 0x80, 0x00);
    var laranjaStatus = System.Drawing.Color.FromArgb(0xFF, 0x88, 0x00);
    var brancoFonte = System.Drawing.Color.White;
    var pretoFonte  = System.Drawing.Color.FromArgb(0x22, 0x22, 0x22);
    var labelPeriodo = tipo.ToUpper() switch {
        "DIARIO" => "DIÁRIO", "MENSAL" => "MENSAL", "ANUAL" => "ANUAL", _ => tipo.ToUpper()
    };

    // 
    //   ABA 1 — RESUMO              
    // 
    var wsR = pkg.Workbook.Worksheets.Add(" Resumo");
    wsR.View.ShowGridLines = false;
    wsR.Column(1).Width = 36;
    wsR.Column(2).Width = 22;
    wsR.Column(3).Width = 22;

    // Título principal
    wsR.Cells["A1:C1"].Merge = true;
    wsR.Cells["A1"].Value = $"TSEA ENERGIA  –  RELATÓRIO {labelPeriodo}";
    wsR.Cells["A1"].Style.Font.Name = "Arial";
    wsR.Cells["A1"].Style.Font.Size = 18;
    wsR.Cells["A1"].Style.Font.Bold = true;
    wsR.Cells["A1"].Style.Font.Color.SetColor(brancoFonte);
    wsR.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
    wsR.Cells["A1"].Style.Fill.BackgroundColor.SetColor(vermelho);
    wsR.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    wsR.Cells["A1"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
    wsR.Row(1).Height = 42;

    // Linha de período/geração
    wsR.Cells["A2"].Value = "Período:";
    wsR.Cells["B2"].Value = $"{inicio:dd/MM/yyyy}  →  {fim:dd/MM/yyyy}";
    wsR.Cells["B2:C2"].Merge = true;
    wsR.Cells["A3"].Value = "Gerado em:";
    wsR.Cells["B3"].Value = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
    wsR.Cells["B3:C3"].Merge = true;
    foreach (var addr in new[]{"A2","A3"}) {
        wsR.Cells[addr].Style.Font.Bold = true;
        wsR.Cells[addr].Style.Font.Color.SetColor(pretoFonte);
    }
    foreach (var addr in new[]{"B2","B3"}) {
        wsR.Cells[addr].Style.Font.Color.SetColor(pretoFonte);
    }
    wsR.Row(2).Height = 18; wsR.Row(3).Height = 18;

    // Função local para seção título
    void SecaoTitulo(ExcelWorksheet ws, int row, string titulo, System.Drawing.Color cor, int colMax = 3) {
        var merge = $"A{row}:{(char)('A'+colMax-1)}{row}";
        ws.Cells[merge].Merge = true;
        ws.Cells[$"A{row}"].Value = titulo;
        ws.Cells[$"A{row}"].Style.Font.Bold = true;
        ws.Cells[$"A{row}"].Style.Font.Size = 11;
        ws.Cells[$"A{row}"].Style.Font.Color.SetColor(brancoFonte);
        ws.Cells[$"A{row}"].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[$"A{row}"].Style.Fill.BackgroundColor.SetColor(cor);
        ws.Cells[$"A{row}"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
        ws.Cells[$"A{row}"].Style.Indent = 1;
        ws.Row(row).Height = 22;
    }

    // Função local para linha de dado
    void LinhaKV(ExcelWorksheet ws, int row, string label, object valor, System.Drawing.Color? bgColor = null) {
        ws.Cells[$"A{row}"].Value = label;
        ws.Cells[$"B{row}"].Value = valor;
        ws.Cells[$"B{row}"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        ws.Cells[$"B{row}"].Style.Font.Bold = true;
        ws.Cells[$"B{row}"].Style.Font.Size = 12;
        if (bgColor.HasValue) {
            foreach (var c in new[]{ $"A{row}", $"B{row}", $"C{row}" }) {
                ws.Cells[c].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[c].Style.Fill.BackgroundColor.SetColor(bgColor.Value);
            }
        }
        ws.Row(row).Height = 20;
    }

    SecaoTitulo(wsR, 5, "  MOVIMENTAÇÕES", System.Drawing.Color.FromArgb(0x33, 0x33, 0x33));
    LinhaKV(wsR, 6,  "  Total de movimentações",        resumo.TotalMovimentacoes);
    LinhaKV(wsR, 7,  "  Devolvidas manualmente",        resumo.Devolvidas,        System.Drawing.Color.FromArgb(0xE8, 0xF5, 0xE9));
    LinhaKV(wsR, 8,  "  Em aberto (não devolvidas)",    resumo.EmAberto,          resumo.EmAberto > 0 ? vermelhoClaro : (System.Drawing.Color?)null);
    LinhaKV(wsR, 9,  "  Ferramentas atrasadas (agora)", resumo.FerramentasAtrasadas, resumo.FerramentasAtrasadas > 0 ? vermelhoClaro : (System.Drawing.Color?)null);
    LinhaKV(wsR, 10, "  Usuários únicos no período",    resumo.UsuariosUnicos);
    LinhaKV(wsR, 11, "  Total de logs de acesso",       resumo.TotalLogsAcesso);

    SecaoTitulo(wsR, 13, "  FERRAMENTAS – SITUAÇÃO ATUAL", System.Drawing.Color.FromArgb(0x33, 0x33, 0x33));
    LinhaKV(wsR, 14, "  Total cadastradas",  resumoFerr.Total);
    LinhaKV(wsR, 15, "  Disponíveis",        resumoFerr.Disponiveis,  System.Drawing.Color.FromArgb(0xE8, 0xF5, 0xE9));
    LinhaKV(wsR, 16, "  Em uso",             resumoFerr.EmUso,        System.Drawing.Color.FromArgb(0xFF, 0xF3, 0xE0));
    LinhaKV(wsR, 17, "  Em manutenção",      resumoFerr.Manutencao,   resumoFerr.Manutencao > 0 ? vermelhoClaro : (System.Drawing.Color?)null);

    // Bordas finas em todo o bloco de dados
    foreach (var rng in new[]{ wsR.Cells["A6:C11"], wsR.Cells["A14:C17"] }) {
        rng.Style.Border.Top.Style    = ExcelBorderStyle.Thin;
        rng.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        rng.Style.Border.Left.Style   = ExcelBorderStyle.Thin;
        rng.Style.Border.Right.Style  = ExcelBorderStyle.Thin;
    }

    // Função para criar cabeçalho de tabela
    void CabecalhoTabela(ExcelWorksheet ws, int row, string[] cols) {
        for (int i = 0; i < cols.Length; i++) {
            ws.Cells[row, i+1].Value = cols[i];
            ws.Cells[row, i+1].Style.Font.Bold = true;
            ws.Cells[row, i+1].Style.Font.Color.SetColor(brancoFonte);
            ws.Cells[row, i+1].Style.Fill.PatternType = ExcelFillStyle.Solid;
            ws.Cells[row, i+1].Style.Fill.BackgroundColor.SetColor(vermelho);
            ws.Cells[row, i+1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            ws.Cells[row, i+1].Style.Border.Top.Style    = ExcelBorderStyle.Thin;
            ws.Cells[row, i+1].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            ws.Cells[row, i+1].Style.Border.Left.Style   = ExcelBorderStyle.Thin;
            ws.Cells[row, i+1].Style.Border.Right.Style  = ExcelBorderStyle.Thin;
        }
        ws.Row(row).Height = 24;
    }

    // Função para aplicar zebra + bordas nas linhas de dados
    void FormatarLinhas(ExcelWorksheet ws, int rowStart, int rowEnd, int colCount, bool comStatus = false, int statusCol = 0) {
        for (int r = rowStart; r <= rowEnd; r++) {
            var bg = (r - rowStart) % 2 == 0 ? cinzaLinha : System.Drawing.Color.White;
            for (int c = 1; c <= colCount; c++) {
                var cell = ws.Cells[r, c];
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(bg);
                cell.Style.Border.Top.Style    = ExcelBorderStyle.Thin;
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                cell.Style.Border.Left.Style   = ExcelBorderStyle.Thin;
                cell.Style.Border.Right.Style  = ExcelBorderStyle.Thin;
                cell.Style.Font.Color.SetColor(pretoFonte);
                cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            }
            ws.Row(r).Height = 18;
        }
    }

    // 
    //   ABA 2 — MOVIMENTAÇÕES       
    // 
    var wsMov = pkg.Workbook.Worksheets.Add(" Movimentações");
    wsMov.View.ShowGridLines = false;
    wsMov.Row(1).Height = 42;
    wsMov.Cells["A1:J1"].Merge = true;
    wsMov.Cells["A1"].Value = $"TSEA ENERGIA  –  MOVIMENTAÇÕES  |  {labelPeriodo}  |  {inicio:dd/MM/yyyy} a {fim:dd/MM/yyyy}";
    wsMov.Cells["A1"].Style.Font.Bold = true; wsMov.Cells["A1"].Style.Font.Size = 14;
    wsMov.Cells["A1"].Style.Font.Color.SetColor(brancoFonte);
    wsMov.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
    wsMov.Cells["A1"].Style.Fill.BackgroundColor.SetColor(vermelho);
    wsMov.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    wsMov.Cells["A1"].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

    var movCols = new[]{"#","Ferramenta","Código Barras","Setor","Colaborador","Data Retirada","Data Devolução","Duração (min)","Devolvida?","Status"};
    var movWidths = new double[]{5,30,16,14,22,20,20,14,14,14};
    CabecalhoTabela(wsMov, 2, movCols);
    for (int i=0;i<movWidths.Length;i++) wsMov.Column(i+1).Width = movWidths[i];

    int movRow = 3;
    foreach (var m in movs) {
        wsMov.Cells[movRow,1].Value  = movRow - 2;
        wsMov.Cells[movRow,2].Value  = m.Ferramenta;
        wsMov.Cells[movRow,3].Value  = m.CodigoBarras;
        wsMov.Cells[movRow,4].Value  = m.Setor;
        wsMov.Cells[movRow,5].Value  = m.Colaborador;
        wsMov.Cells[movRow,6].Value  = m.DataRetirada.ToString("dd/MM/yyyy HH:mm");
        wsMov.Cells[movRow,7].Value  = m.DataDevolucao.HasValue ? m.DataDevolucao.Value.ToString("dd/MM/yyyy HH:mm") : "—";
        wsMov.Cells[movRow,8].Value  = m.DuracaoMinutos;
        wsMov.Cells[movRow,9].Value  = m.DevolvidaManualmente ? "SIM" : "NÃO";
        wsMov.Cells[movRow,10].Value = m.Status;
        movRow++;
    }
    FormatarLinhas(wsMov, 3, movRow-1, 10);
    // Colorir coluna Status
    for (int r = 3; r < movRow; r++) {
        var statusVal = wsMov.Cells[r,10].Value?.ToString() ?? "";
        var cor = statusVal == "DEVOLVIDA" ? System.Drawing.Color.FromArgb(0xE8,0xF5,0xE9) : vermelhoClaro;
        wsMov.Cells[r,10].Style.Fill.BackgroundColor.SetColor(cor);
        wsMov.Cells[r,9].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        wsMov.Cells[r,10].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        wsMov.Cells[r,1].Style.HorizontalAlignment  = ExcelHorizontalAlignment.Center;
        wsMov.Cells[r,8].Style.HorizontalAlignment  = ExcelHorizontalAlignment.Center;
    }

    // 
    //   ABA 3 — LOGS DE ACESSO      
    // 
    var wsLog = pkg.Workbook.Worksheets.Add(" Logs de Acesso");
    wsLog.View.ShowGridLines = false;
    wsLog.Row(1).Height = 42;
    wsLog.Cells["A1:H1"].Merge = true;
    wsLog.Cells["A1"].Value = $"TSEA ENERGIA  –  LOGS DE ACESSO  |  {labelPeriodo}  |  {inicio:dd/MM/yyyy} a {fim:dd/MM/yyyy}";
    wsLog.Cells["A1"].Style.Font.Bold = true; wsLog.Cells["A1"].Style.Font.Size = 14;
    wsLog.Cells["A1"].Style.Font.Color.SetColor(brancoFonte);
    wsLog.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
    wsLog.Cells["A1"].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0x1A,0x1A,0x1A));
    wsLog.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    wsLog.Cells["A1"].Style.VerticalAlignment   = ExcelVerticalAlignment.Center;

    var logCols = new[]{"#","Colaborador","Crachá","Data Entrada","Data Saída","Duração (min)","Motivo Saída","Status"};
    var logWidths = new double[]{5,26,14,20,20,14,20,14};
    CabecalhoTabela(wsLog, 2, logCols);
    for (int i=0;i<logWidths.Length;i++) wsLog.Column(i+1).Width = logWidths[i];

    int logRow = 3;
    foreach (var l in logs) {
        wsLog.Cells[logRow,1].Value = logRow - 2;
        wsLog.Cells[logRow,2].Value = l.Colaborador;
        wsLog.Cells[logRow,3].Value = l.UsuarioId;
        wsLog.Cells[logRow,4].Value = l.DataEntrada.ToString("dd/MM/yyyy HH:mm");
        wsLog.Cells[logRow,5].Value = l.DataSaida.HasValue ? l.DataSaida.Value.ToString("dd/MM/yyyy HH:mm") : "Ainda logado";
        wsLog.Cells[logRow,6].Value = l.DuracaoMinutos;
        wsLog.Cells[logRow,7].Value = l.MotivoSaida ?? "—";
        wsLog.Cells[logRow,8].Value = l.StatusAcesso;
        logRow++;
    }
    FormatarLinhas(wsLog, 3, logRow-1, 8);
    for (int r = 3; r < logRow; r++) {
        wsLog.Cells[r,1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        wsLog.Cells[r,6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        wsLog.Cells[r,8].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    }

    // 
    //   ABA 4 — FERRAMENTAS ATRASADAS   
    // 
    var wsAt = pkg.Workbook.Worksheets.Add(" Atrasadas");
    wsAt.View.ShowGridLines = false;
    wsAt.Row(1).Height = 42;
    wsAt.Cells["A1:F1"].Merge = true;
    wsAt.Cells["A1"].Value = $"TSEA ENERGIA  –  FERRAMENTAS ATRASADAS  |  Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}";
    wsAt.Cells["A1"].Style.Font.Bold = true; wsAt.Cells["A1"].Style.Font.Size = 14;
    wsAt.Cells["A1"].Style.Font.Color.SetColor(brancoFonte);
    wsAt.Cells["A1"].Style.Fill.PatternType = ExcelFillStyle.Solid;
    wsAt.Cells["A1"].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0x99,0x00,0x00));
    wsAt.Cells["A1"].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
    wsAt.Cells["A1"].Style.VerticalAlignment   = ExcelVerticalAlignment.Center;

    var atCols = new[]{"#","Descrição","Código Barras","Setor","Colaborador","Status"};
    var atWidths = new double[]{5,30,16,14,24,14};
    CabecalhoTabela(wsAt, 2, atCols);
    for (int i=0;i<atWidths.Length;i++) wsAt.Column(i+1).Width = atWidths[i];

    int atRow = 3;
    foreach (var f in atrasadas) {
        wsAt.Cells[atRow,1].Value = atRow - 2;
        wsAt.Cells[atRow,2].Value = f.Descricao;
        wsAt.Cells[atRow,3].Value = f.CodigoBarras ?? $"TSEA-{f.Id:D3}";
        wsAt.Cells[atRow,4].Value = f.Setor;
        wsAt.Cells[atRow,5].Value = f.Colaborador ?? "—";
        wsAt.Cells[atRow,6].Value = f.Status;
        atRow++;
    }
    FormatarLinhas(wsAt, 3, atRow-1, 6);
    for (int r = 3; r < atRow; r++) {
        wsAt.Cells[r,1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        wsAt.Cells[r,6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
        // Todas as linhas de atrasadas com fundo vermelho claro na coluna status
        wsAt.Cells[r,6].Style.Fill.BackgroundColor.SetColor(vermelhoClaro);
        wsAt.Cells[r,6].Style.Font.Color.SetColor(System.Drawing.Color.FromArgb(0xCC,0x00,0x00));
        wsAt.Cells[r,6].Style.Font.Bold = true;
    }

    var bytes = pkg.GetAsByteArray();
    var nomeArq = $"TSEA_Relatorio_{labelPeriodo}_{inicio:yyyyMMdd}.xlsx";
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        nomeArq);
});

app.Run();

// ============================================================
// CLASSES — devem ficar SEMPRE após o app.Run()
// ============================================================

// 🌟 CONTROLE DO STATUS DO BOTÃO
public static class BotaoControle
{
    public static bool ConfirmadoSaida { get; set; } = false;
}

// ESTADO DA PORTA (Sensor Magnético - Pino 13)
public static class EstadoPorta
{
    public static string Status { get; set; } = "DESCONHECIDO"; // ABERTA, FECHADA, ALERTA_SEM_LOGIN
    public static DateTime UltimaAtualizacao { get; set; } = DateTime.Now;
    public static bool AlarmeSemLogin { get; set; } = false;
}

//  CONEXÃO SERIAL GLOBAL
public static class ArduinoSerial
{
    public static SerialPort Porta = new SerialPort("COM7", 9600) { ReadTimeout = 150 };

    public static void Inicializar()
    {
        TentarAbrir();
    }

    public static void TentarAbrir()
    {
        try {
            if (!Porta.IsOpen) {
                Porta.Open();
                Console.WriteLine("[Arduino] Porta serial aberta na COM5!");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[Arduino] Erro ao abrir porta: {ex.Message}");
        }
    }
}

// --- MODELOS DE BANCO DE DADOS ---
public class AppDbContext : DbContext {
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Usuario> Usuarios { get; set; }
    public DbSet<Ferramenta> Ferramentas { get; set; }
    public DbSet<LogAcesso> LogsAcesso { get; set; }
    public DbSet<Movimentacoes> Movimentacoes { get; set; } 
}

public class Usuario {
    public int Id { get; set; }
    public string Nome { get; set; } = "";
    public string CodigoBarras { get; set; } = "";
    public string Setor { get; set; } = "";
    public string Status { get; set; } = "ATIVO";
    public string? PasswordHash { get; set; }
    public string? Email { get; set; }
}

public class Ferramenta {
    public int Id { get; set; }
    public string Descricao { get; set; } = "";
    public string Status { get; set; } = "DISPONIVEL";
    public string Setor { get; set; } = "GERAL";
    public int VidaUtil { get; set; } = 100;
    public string? CodigoBarras { get; set; }
    public string? Colaborador { get; set; }
}

public class Movimentacoes {
    [Key] public int Id { get; set; }
    public int FerramentaId { get; set; }
    public string UsuarioId { get; set; } = "";
    public DateTime DataRetirada { get; set; }
    public DateTime? DataDevolucao { get; set; }
}

public class LogAcesso {
    public int Id { get; set; }
    public string UsuarioId { get; set; } = "";
    public DateTime DataEntrada { get; set; } = DateTime.UtcNow;
    public DateTime? DataSaida { get; set; }
    public string? MotivoSaida { get; set; }
    public string StatusAcesso { get; set; } = "ATIVO";
}

public record LoginRequest(string Barcode, string? Password);
public record MovimentacaoReq(int? FerramentaId, string? FerramentaCodigoBarras, string UsuarioId);
public record CadastroRequest(string Nome, string Barcode, string Setor, string? Password, string? Email);
public record PasswordRecoveryRequest(string Barcode);
public record LogoutRequest(string UsuarioId, string Motivo);
public record FerramentaUpdateRequest(string? Descricao, string? Setor, string? Status, int? VidaUtil, string? CodigoBarras);

public class SmtpSettings {
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
}

public record AvisoRequest(int FerramentaId, string UsuarioId, string? Mensagem);

public class AvisoPendente {
    public int Id { get; set; }
    public int FerramentaId { get; set; }
    public string UsuarioId { get; set; } = "";
    public string Mensagem { get; set; } = "";
    public bool Lido { get; set; } = false;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}

public record EditarUsuarioRequest(string Nome);
public record RedefinirSenhaRequest(
    string Token,
    string NovaSenha
);

public record AlterarStatusUsuarioRequest(
    string Status
);