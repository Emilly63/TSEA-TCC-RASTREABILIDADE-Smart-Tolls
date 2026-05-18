using System.IO.Ports;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;

// ✅ DEIXE APENAS ESSA LINHA ABAIXO. Delete o "using (SerialPort porta..." que estava aqui em cima!
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

// ENDPOINT TEMPORÁRIO DE DEBUG
app.MapGet("/debug/smtp", () => new {
    host = smtpConfig.Host,
    port = smtpConfig.Port,
    user = smtpConfig.Username,
    hasPassword = !string.IsNullOrWhiteSpace(smtpConfig.Password),
    from = smtpConfig.From
});

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

// 1. CADASTRAR NOVO FUNCIONÁRIO
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
    var body = $@"Olá {usuario.Nome},

Seu código de recuperação de senha é:

{codigo}

Digite este código na tela do sistema TSEA.
O código expira em 10 minutos.

Se não foi você, ignore este email.

Atenciosamente,
Equipe TSEA";

    var emailEnviado = await SendEmailAsync(smtpConfig, usuario.Email, subject, body);

    if (!emailEnviado)
        return Results.BadRequest(new {
            mensagem = "Não foi possível enviar o código de recuperação por email. Verifique o spam, as configurações de SMTP do backend ou entre em contato com o administrador.",
            semEmail = true
        });

    return Results.Ok(new {
        mensagem = $"Código enviado para {usuario.Email}. Se não receber em alguns minutos, verifique spam ou entre em contato com o administrador.",
        semEmail = false
    });
});

app.MapPost("/senha/redefinir", async (HttpContext http, AppDbContext db) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();

    RedefinirSenhaRequest? req = null;
    try
    {
        req = System.Text.Json.JsonSerializer.Deserialize<RedefinirSenhaRequest>(body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Erro ao deserializar: {ex.Message}");
        return Results.BadRequest("Formato inválido.");
    }

    if (req == null || string.IsNullOrWhiteSpace(req.Token) || string.IsNullOrWhiteSpace(req.NovaSenha))
        return Results.BadRequest("Código e nova senha são obrigatórios.");

    if (!resetTokens.TryGetValue(req.Token, out var entry))
    {
        return Results.BadRequest("Código inválido ou já utilizado.");
    }

    if (DateTime.Now > entry.Expiry)
    {
        resetTokens.Remove(req.Token);
        return Results.BadRequest("Código expirado. Solicite um novo.");
    }
    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == entry.Barcode);
    if (usuario == null) return Results.NotFound("Usuário não encontrado.");

    usuario.PasswordHash = HashPassword(req.NovaSenha);
    await db.SaveChangesAsync();
    resetTokens.Remove(req.Token);

    return Results.Ok(new { mensagem = "Senha redefinida com sucesso! Faça o login." });
});

app.MapPost("/movimentacao/retirar", async (MovimentacaoReq req, AppDbContext db) =>
{
    try 
    {
        Ferramenta? ferramenta = null;
        if (req.FerramentaId.HasValue)
        {
            ferramenta = await db.Ferramentas.FindAsync(req.FerramentaId.Value);
        }

        if (ferramenta == null && !string.IsNullOrWhiteSpace(req.FerramentaCodigoBarras))
        {
            var codigoUpper = NormalizeBarcode(req.FerramentaCodigoBarras);
            var ferramentas = await db.Ferramentas
                .Where(f => f.CodigoBarras != null && f.CodigoBarras.ToUpper() == codigoUpper)
                .ToListAsync();

            if (ferramentas.Count == 0 && codigoUpper.EndsWith("A"))
            {
                var codigoSemA = codigoUpper.Substring(0, codigoUpper.Length - 1);
                ferramentas = await db.Ferramentas
                    .Where(f => f.CodigoBarras != null && f.CodigoBarras.ToUpper() == codigoSemA)
                    .ToListAsync();
            }

            if (ferramentas.Count > 1)
                return Results.BadRequest(new { erro = "Mais de uma ferramenta possui este código de barras. Remova a duplicata e tente novamente." });

            ferramenta = ferramentas.SingleOrDefault();
        }

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == req.UsuarioId);

        if (ferramenta == null) return Results.BadRequest("ID ou código de barras da ferramenta não existe!");
        if (usuario == null) return Results.BadRequest("Usuário não encontrado!");

        var setorFerramenta = (ferramenta.Setor ?? "").Trim().ToUpper();
        if (setorFerramenta == "ALMOXERIFADO") return Results.BadRequest(new { erro = "Não é possível pegar, pois a ferramenta está no almoxerifado." });
        if (ferramenta.Status == "EM_USO") return Results.BadRequest("Ferramenta já está em uso.");

        ferramenta.Status = "EM_USO";
        ferramenta.Colaborador = usuario.Nome;

        db.Movimentacoes.Add(new Movimentacoes { 
            FerramentaId = ferramenta.Id, 
            UsuarioId = req.UsuarioId, 
            DataRetirada = DateTime.Now 
        });

        await db.SaveChangesAsync();
        return Results.Ok(new { mensagem = "Retirada concluída!", colaborador = usuario.Nome });
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/ferramentas/{id}/pode-retirar", async (int id, AppDbContext db) =>
{
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    var setorFerramenta = (ferramenta.Setor ?? "").Trim().ToUpper();
    if (setorFerramenta == "ALMOXERIFADO") 
        return Results.BadRequest(new { erro = "Não é possível pegar, pois a ferramenta está no almoxerifado." });
    
    if (ferramenta.Status == "EM_USO") 
        return Results.BadRequest(new { erro = "Ferramenta já está em uso." });

    if (ferramenta.Status == "MANUTENCAO")
        return Results.BadRequest(new { erro = "Ferramenta está em manutenção." });

    return Results.Ok(new { mensagem = "Ferramenta pode ser retirada." });
});

app.MapPost("/movimentacao/devolver", async (MovimentacaoReq req, AppDbContext db) =>
{
    Ferramenta? ferramenta = null;
    if (req.FerramentaId.HasValue)
    {
        ferramenta = await db.Ferramentas.FindAsync(req.FerramentaId.Value);
    }

    if (ferramenta == null && !string.IsNullOrWhiteSpace(req.FerramentaCodigoBarras))
    {
        var codigoUpper = NormalizeBarcode(req.FerramentaCodigoBarras);
        var ferramentas = await db.Ferramentas
            .Where(f => f.CodigoBarras != null && f.CodigoBarras.ToUpper() == codigoUpper)
            .ToListAsync();

        if (ferramentas.Count == 0 && codigoUpper.EndsWith("A"))
        {
            var codigoSemA = codigoUpper.Substring(0, codigoUpper.Length - 1);
            ferramentas = await db.Ferramentas
                .Where(f => f.CodigoBarras != null && f.CodigoBarras.ToUpper() == codigoSemA)
                .ToListAsync();
        }

        if (ferramentas.Count > 1)
            return Results.BadRequest(new { erro = "Mais de uma ferramenta possui este código de barras. Remova a duplicata e tente novamente." });

        ferramenta = ferramentas.SingleOrDefault();
    }

    if (ferramenta == null) return Results.BadRequest("Ferramenta não encontrada!");

    var setorFerramenta = (ferramenta.Setor ?? "").Trim().ToUpper();
    if (setorFerramenta == "ALMOXERIFADO") return Results.BadRequest("Ferramenta está no almoxerifado.");

    var ferramentaIdParaMov = ferramenta.Id;
    var mov = await db.Movimentacoes
        .Where(m => m.FerramentaId == ferramentaIdParaMov && m.DataDevolucao == null)
        .FirstOrDefaultAsync();
    
    if (ferramenta != null) {
        ferramenta.Status = "DISPONIVEL";
        ferramenta.Colaborador = null;
    }

    if (mov != null) mov.DataDevolucao = DateTime.Now;
    
    await db.SaveChangesAsync();
    return Results.Ok("Ferramenta devolvida com sucesso!");
});

app.MapPost("/ferramentas", async (Ferramenta f, AppDbContext db) => {
    try {
        if (string.IsNullOrEmpty(f.Status)) f.Status = "DISPONIVEL";
        if (string.IsNullOrEmpty(f.Setor)) f.Setor = "GERAL";

        if (!string.IsNullOrWhiteSpace(f.CodigoBarras))
        {
            var codigoUpper = NormalizeBarcode(f.CodigoBarras);
            if (await db.Ferramentas.AnyAsync(x => x.CodigoBarras != null && x.CodigoBarras.ToUpper() == codigoUpper))
                return Results.BadRequest(new { erro = "Já existe uma ferramenta cadastrada com este código de barras." });

            f.CodigoBarras = codigoUpper;
        }

        db.Ferramentas.Add(f);
        await db.SaveChangesAsync();
        return Results.Ok(new { mensagem = "Ferramenta cadastrada com sucesso!", id = f.Id });
    }
    catch (Exception ex) {
        return Results.BadRequest(new { erro = ex.Message });
    }
});

app.MapPut("/ferramentas/{id}", async (int id, FerramentaUpdateRequest update, AppDbContext db) => {
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });
    if (ferramenta.Status == "EM_USO") return Results.BadRequest(new { erro = "Não é possível editar uma ferramenta que está em uso." });

    if (!string.IsNullOrEmpty(update.Setor) && update.Setor.Trim().ToUpper() == "ALMOXERIFADO" && ferramenta.Status == "EM_USO")
        return Results.BadRequest(new { erro = "Não é possível enviar ferramenta em uso para o almoxerifado." });

    if (!string.IsNullOrEmpty(update.Descricao)) ferramenta.Descricao = update.Descricao;
    if (!string.IsNullOrEmpty(update.Setor)) ferramenta.Setor = update.Setor;
    if (!string.IsNullOrEmpty(update.CodigoBarras))
    {
        var codigoUpper = NormalizeBarcode(update.CodigoBarras);
        if (await db.Ferramentas.AnyAsync(x => x.Id != id && x.CodigoBarras != null && x.CodigoBarras.ToUpper() == codigoUpper))
            return Results.BadRequest(new { erro = "Já existe outra ferramenta com este mesmo código de barras." });
        ferramenta.CodigoBarras = codigoUpper;
    }
    if (update.VidaUtil.HasValue) ferramenta.VidaUtil = update.VidaUtil.Value;
    if (!string.IsNullOrEmpty(update.Status)) ferramenta.Status = update.Status;

    await db.SaveChangesAsync();
    return Results.Ok(new { Urban = "Ferramenta atualizada com sucesso." });
});

app.MapPost("/ferramentas/{id}/manutencao", async (int id, AppDbContext db) => {
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    var setorFerramenta = (ferramenta.Setor ?? "").Trim().ToUpper();
    if (setorFerramenta == "ALMOXERIFADO") return Results.BadRequest(new { erro = "Ferramenta está no almoxerifado." });

    ferramenta.Status = "MANUTENCAO";
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta marcada como manutenção." });
});

app.MapPost("/avisos", async (AvisoRequest req) => {
    if (req.FerramentaId <= 0 || string.IsNullOrWhiteSpace(req.UsuarioId))
        return Results.BadRequest(new { erro = "FerramentaId e UsuarioId são obrigatórios." });

    var aviso = new AvisoPendente {
        Id = proximoAvisoId++,
        FerramentaId = req.FerramentaId,
        UsuarioId = req.UsuarioId.ToUpperInvariant(),
        Mensagem = string.IsNullOrWhiteSpace(req.Mensagem) ? "Ferramenta não devolvida dentro do prazo." : req.Mensagem,
        Lido = false,
        CriadoEm = DateTime.Now
    };

    notificacoesPendentes.Add(aviso);
    return Results.Ok(new { mensagem = "Aviso enviado ao operador.", avisoId = aviso.Id });
});

app.MapGet("/avisos/{usuarioId}", (string usuarioId) =>
    Results.Ok(notificacoesPendentes
        .Where(a => !a.Lido && a.UsuarioId.Equals(usuarioId, StringComparison.OrdinalIgnoreCase))
        .Select(a => new { a.Id, a.FerramentaId, a.UsuarioId, a.Mensagem, a.CriadoEm })
        .ToList())
);

app.MapPost("/avisos/{id}/lido", (int id) => {
    var aviso = notificacoesPendentes.FirstOrDefault(a => a.Id == id);
    if (aviso == null) return Results.NotFound(new { erro = "Aviso não encontrado." });
    aviso.Lido = true;
    return Results.Ok(new { mensagem = "Aviso marcado como lido." });
});

app.MapPost("/ferramentas/limpar-colaboradores", async (AppDbContext db) => {
    try {
        var usuarios = await db.Usuarios.AsNoTracking().Select(u => u.Nome.ToLower()).ToListAsync();
        var ferramentas = await db.Ferramentas.ToListAsync();
        
        int removidas = 0;
        foreach (var f in ferramentas) {
            if (!string.IsNullOrEmpty(f.Colaborador) && !usuarios.Contains(f.Colaborador.ToLower())) {
                f.Colaborador = null;
                removidas++;
            }
        }
        
        await db.SaveChangesAsync();
        return Results.Ok(new { mensagem = $"{removidas} colaboradores inválidos foram removidos." });
    } catch (Exception ex) {
        return Results.Problem($"Erro ao limpar colaboradores: {ex.Message}");
    }
});

app.MapDelete("/ferramentas/{id}", async (int id, AppDbContext db) => {
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    db.Ferramentas.Remove(ferramenta);
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta excluída com sucesso." });
});

app.MapPatch("/ferramentas/{id}/manutencao", async (int id, AppDbContext db) => {
    var ferramenta = await db.Ferramentas.FindAsync(id);
    if (ferramenta == null) return Results.NotFound(new { erro = "Ferramenta não encontrada." });

    ferramenta.Status = "MANUTENCAO";
    await db.SaveChangesAsync();
    return Results.Ok(new { mensagem = "Ferramenta marcada como manutenção." });
});

app.MapGet("/ferramentas", async (AppDbContext db) => 
{
    try 
    {
        var ferramentas = await db.Ferramentas.AsNoTracking().ToListAsync();
        var movimentacoes = await db.Movimentacoes
            .Where(m => m.DataDevolucao == null)
            .AsNoTracking()
            .ToListAsync();
        var usuarios = await db.Usuarios.AsNoTracking().ToListAsync();

        var resultado = ferramentas.Select(f => {
            var mov = movimentacoes.FirstOrDefault(m => m.FerramentaId == f.Id);
            string colabNome = "---";
            string colabId = "";

            if ((f.Status == "EM_USO" || f.Status == "ATRASADO") && mov != null) {
                var user = usuarios.FirstOrDefault(u => u.CodigoBarras.Equals(mov.UsuarioId, StringComparison.OrdinalIgnoreCase));
                colabNome = user?.Nome ?? (f.Colaborador ?? "Desconhecido");
                colabId = mov.UsuarioId;
            }

            return new {
                id = f.Id,
                codigoBarras = f.CodigoBarras,
                descricao = f.Descricao,
                status = f.Status,
                setor = f.Setor,
                vidaUtil = f.VidaUtil,
                colaborador = colabNome,
                colaboradorId = colabId
            };
        });

        return Results.Ok(resultado);
    }
    catch (Exception ex) 
    {
        return Results.Problem("Erro ao carregar dados: " + ex.Message);
    }
});

app.MapGet("/ferramentas/duplicatas", async (AppDbContext db) => {
    var duplicatas = await db.Ferramentas
        .Where(f => f.CodigoBarras != null)
        .GroupBy(f => f.CodigoBarras)
        .Where(g => g.Count() > 1)
        .Select(g => new {
            codigoBarras = g.Key,
            quantidade = g.Count(),
            ferramentas = g.Select(f => new { f.Id, f.Descricao, f.Setor, f.Status }).ToList()
        })
        .ToListAsync();
    return Results.Ok(duplicatas);
});

// --- ROTA LOGIN ---
app.MapPost("/login", async (LoginRequest req, AppDbContext db) =>
{
    var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.CodigoBarras == req.Barcode);
    if (usuario == null) return Results.NotFound("Usuário não cadastrado.");

    if (usuario.Setor == "ADMIN")
    {
        if (string.IsNullOrWhiteSpace(req.Password))
            return Results.BadRequest("Senha obrigatória para login de administrador.");

        if (string.IsNullOrEmpty(usuario.PasswordHash) || !VerifyPassword(req.Password, usuario.PasswordHash))
            return Results.BadRequest("Senha incorreta.");
    }
    
    EnviarComandoArduino("LOGIN");
    db.LogsAcesso.Add(new LogAcesso { UsuarioId = usuario.CodigoBarras, DataEntrada = DateTime.Now, StatusAcesso = "ATIVO" });
    await db.SaveChangesAsync();
    return Results.Ok(new { nome = usuario.Nome, tipo = usuario.Setor });
});

// --- ROTA LOGOUT ---
app.MapPost("/logout", async (LogoutRequest req, AppDbContext db) =>
{
    var ultimoLog = await db.LogsAcesso
        .Where(l => l.UsuarioId == req.UsuarioId && l.DataSaida == null)
        .OrderByDescending(l => l.DataEntrada)
        .FirstOrDefaultAsync();

    if (ultimoLog != null) {
        ultimoLog.DataSaida = DateTime.Now;
        ultimoLog.MotivoSaida = req.Motivo;
    }
    await db.SaveChangesAsync();
    EnviarComandoArduino("LOGOUT");
    return Results.Ok();
});

// ⚠️ ESSA DEVE SER A ÚLTIMA LINHA ANTES DA FUNÇÃO ISOLADA
app.Run();

// --- FUNÇÃO PERSONALIZADA (ISOLADA APÓS O RUN) ---
void EnviarComandoArduino(string comando)
{
    try
    {
        using (SerialPort porta = new SerialPort("COM5", 9600))
        {
            porta.Open();
            porta.WriteLine(comando);
            porta.Close();
        }
        Console.WriteLine($"[Arduino] Comando enviado com sucesso: {comando}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Arduino] Erro ao comunicar na porta: {ex.Message}");
    }
}

// --- MODELOS ---
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