-- PostgreSQL initialization script for RASTREABILIDADES_TSEA
-- 1. CREATE DATABASE (run in default postgres database first)
-- CREATE DATABASE RASTREABILIDADES_TSEA;

-- 2. CONNECT TO RASTREABILIDADES_TSEA DATABASE AND RUN BELOW

-- Create ENUM types
CREATE TYPE setor_enum AS ENUM ('ADMIN', 'SOLDAGEM', 'MONTAGEM', 'USINAGEM', 'OPERADOR', 'SOLDA', 'PRODUCAO');
CREATE TYPE status_enum AS ENUM ('ATIVO', 'INATIVO');
CREATE TYPE ferramenta_status_enum AS ENUM ('DISPONIVEL', 'EM_USO', 'MANUTENCAO');
CREATE TYPE motivo_saida_enum AS ENUM ('MANUAL', 'EXPIRADO', 'SISTEMA');

-- 3. DROP EXISTING TABLES (if they exist)
DROP TABLE IF EXISTS Movimentacoes;
DROP TABLE IF EXISTS LogsAcesso;
DROP TABLE IF EXISTS Ferramentas;
DROP TABLE IF EXISTS Usuarios;

-- 4. CREATE USUARIOS TABLE
CREATE TABLE Usuarios (
    Id SERIAL PRIMARY KEY,
    Nome VARCHAR(100),
    CodigoBarras VARCHAR(20) UNIQUE,
    Setor setor_enum DEFAULT 'OPERADOR',
    Status status_enum DEFAULT 'ATIVO',
    Email VARCHAR(255),
    PasswordHash VARCHAR(255)
);

-- 5. CREATE FERRAMENTAS TABLE
CREATE TABLE Ferramentas (
    Id SERIAL PRIMARY KEY,
    Descricao VARCHAR(100),
    Status ferramenta_status_enum DEFAULT 'DISPONIVEL',
    Colaborador VARCHAR(100),
    VidaUtil INT DEFAULT 100,
    Setor VARCHAR(50) DEFAULT 'GERAL'
);

-- 6. CREATE MOVIMENTACOES TABLE
CREATE TABLE Movimentacoes (
    Id SERIAL PRIMARY KEY,
    FerramentaId INT REFERENCES Ferramentas(Id),
    UsuarioId VARCHAR(20),
    DataRetirada TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    DataDevolucao TIMESTAMP NULL
);

-- 7. CREATE LOGSACESSO TABLE
CREATE TABLE LogsAcesso (
    Id SERIAL PRIMARY KEY,
    UsuarioId VARCHAR(50),
    DataEntrada TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    DataSaida TIMESTAMP NULL,
    MotivoSaida motivo_saida_enum,
    StatusAcesso VARCHAR(20) DEFAULT 'ATIVO'
);

-- 8. INSERT INITIAL DATA

-- Insert Administrator
INSERT INTO Usuarios (Nome, CodigoBarras, Setor, Status, Email, PasswordHash) 
VALUES ('ADMINISTRADOR PADRAO', 'TSEA-001A', 'ADMIN', 'ATIVO', 'emillysabrina152009@gmail.com', '123456');

-- Insert Test Operators
INSERT INTO Usuarios (Nome, CodigoBarras, Setor, Status, Email) 
VALUES 
    ('OPERADOR TESTE 01', '1010', 'OPERADOR', 'ATIVO', 'emillysabrinabarbosaalmeida@gmail.com'),
    ('OPERADOR TESTE 02', '2020', 'OPERADOR', 'ATIVO', 'joaopedropereiragomed@gmail.com');

-- Insert Initial Tools
INSERT INTO Ferramentas (Descricao, Status, Setor) 
VALUES 
    ('FURADEIRA BOSCH 01', 'DISPONIVEL', 'SOLDAGEM'),
    ('ESMERILHADEIRA 02', 'DISPONIVEL', 'MONTAGEM'),
    ('CHAVE DE IMPACTO 03', 'DISPONIVEL', 'USINAGEM');

-- 9. Create indexes for better performance
CREATE INDEX idx_usuarios_codigobarras ON Usuarios(CodigoBarras);
CREATE INDEX idx_ferramentas_status ON Ferramentas(Status);
CREATE INDEX idx_movimentacoes_ferramentaid ON Movimentacoes(FerramentaId);
CREATE INDEX idx_logsacesso_usuarioid ON LogsAcesso(UsuarioId);

-- 10. Verify data
SELECT 'Usuarios' AS table_name, COUNT(*) as row_count FROM Usuarios
UNION ALL
SELECT 'Ferramentas', COUNT(*) FROM Ferramentas
UNION ALL
SELECT 'Movimentacoes', COUNT(*) FROM Movimentacoes
UNION ALL
SELECT 'LogsAcesso', COUNT(*) FROM LogsAcesso;

