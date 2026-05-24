using System.IO.Ports;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;

// --- CONFIGURAÇÃO DO BUILDER ---
var builder = WebApplication.CreateBuilder(args);

// --- CONFIGURAÇÃO DE CORS ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// --- CONFIGURAÇÃO DO BANCO DE DADOS (MYSQL) ---
var connectionString = "server=localhost;port=3306;database=RASTREABILIDADES_TSEA;user=root;password=";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 31))));

// Armazena tokens temporários de reset de senha
var resetTokens = new Dictionary<string, (string Barcode, DateTime Expiry)>();

// 🌟 INICIALIZA A PORTA SERIAL GLOBAL
ArduinoSerial.Inicializar();

var app = builder.Build();
app.UseCors("AllowAll");

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

    var connection = db.Database.GetDbConnection();
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = @"
        SELECT COUNT(*)
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
          AND TABLE_NAME = 'Usuarios'
          AND COLUMN_NAME = 'PasswordHash'
    ";
    var exists = Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
    if (!exists)
    {
        command.CommandText = "ALTER TABLE Usuarios ADD COLUMN PasswordHash VARCHAR(255) DEFAULT NULL;";
        command.ExecuteNonQuery();
    }

    command.CommandText = @"
        SELECT COUNT(*)
        FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
          AND TABLE_NAME = 'Usuarios'
          AND COLUMN_NAME = 'Email'
    ";
    var emailExists = Convert.ToInt32(command.ExecuteScalar() ?? 0) > 0;
    if (!emailExists)
    {
        command.CommandText = "ALTER TABLE Usuarios ADD COLUMN Email VARCHAR(255) DEFAULT NULL;";
        command.ExecuteNonQuery();
    }
    connection.Close();
}

var smtpConfig = builder.Configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new SmtpSettings();
var notificacoesPendentes = new List<AvisoPendente>();
var proximoAvisoId = 1;

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

        db.Movimentacoes.Add(new Movimentacoes { FerramentaId = ferramenta.Id, UsuarioId = req.UsuarioId, DataRetirada = DateTime.Now });
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
    if (ferramenta.Status != "EM_USO") return Results.BadRequest(new { erro = "Esta ferramenta não está em uso." });

    ferramenta.Status = "DISPONIVEL";
    ferramenta.Colaborador = null;

    var mov = await db.Movimentacoes.FirstOrDefaultAsync(m => m.FerramentaId == ferramenta.Id && m.DataDevolucao == null);
    if (mov != null) mov.DataDevolucao = DateTime.Now;
    
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
        DataEntrada = DateTime.Now, 
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
        ultimoLog.DataSaida = DateTime.Now;
        ultimoLog.MotivoSaida = req.Motivo;
    }
    await db.SaveChangesAsync();

    // Notifica o Arduino que a conta deslogou para iniciar o buzzer
    EnviarComandoArduino("LOGOUT");

    return Results.Ok();
});

// --- ROTA DE CONSULTA DO BOTÃO ---
app.MapGet("/api/arduino/status-botao", () => {
    if (BotaoControle.ConfirmadoSaida) {
        BotaoControle.ConfirmadoSaida = false; 
        return Results.Ok(new { confirmado = true });
    }

    try 
    {
        if (ArduinoSerial.Porta.IsOpen && ArduinoSerial.Porta.BytesToRead > 0)
        {
            // Lê TODAS as linhas do buffer para não perder o BOTAO_SAIDA_OK
            while (ArduinoSerial.Porta.BytesToRead > 0)
            {
                string resposta = ArduinoSerial.Porta.ReadLine().Trim();
                Console.WriteLine($"[Arduino] Recebido: {resposta}");
                if (resposta == "BOTAO_SAIDA_OK")
                {
                    Console.WriteLine("[Arduino] Confirmação do botão físico detetada!");
                    return Results.Ok(new { confirmado = true });
                }
            }
        }
    }
    catch { /* Evita travar a API com timeouts corriqueiros */ }

    return Results.Ok(new { confirmado = false });
});

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
        CriadoEm = DateTime.Now
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

app.Run();

// --- FUNÇÃO DE ENVIO ---
void EnviarComandoArduino(string comando)
{
    try
    {
        if (ArduinoSerial.Porta != null && ArduinoSerial.Porta.IsOpen)
        {
            // CORREÇÃO ESSENCIAL: Usar WriteLine para enviar a quebra de linha '\n'
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

// ============================================================
// CLASSES — devem ficar SEMPRE após o app.Run()
// ============================================================

// 🌟 CONTROLE DO STATUS DO BOTÃO
public static class BotaoControle
{
    public static bool ConfirmadoSaida { get; set; } = false;
}

// 🔌 CONEXÃO SERIAL GLOBAL
public static class ArduinoSerial
{
    public static SerialPort Porta = new SerialPort("COM5", 9600) { ReadTimeout = 150 };
    
    public static void Inicializar()
    {
        try {
            if (!Porta.IsOpen) {
                Porta.Open();
                Console.WriteLine("[Arduino] Conexão Serial global aberta com sucesso na COM5!");
            }
        } catch (Exception ex) {
            Console.WriteLine($"[Arduino] Erro crítico ao abrir porta serial global: {ex.Message}");
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
    public DateTime DataEntrada { get; set; } = DateTime.Now;
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
    public DateTime CriadoEm { get; set; } = DateTime.Now;
}

public record RedefinirSenhaRequest(string Token, string NovaSenha);