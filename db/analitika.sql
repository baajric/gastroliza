/*
  Lokalna baza aplikacije, na SQL Serveru klijenta.

  Ovdje je jedino mjesto gdje most piše. POS baze (EtisRpos_*) se nikad ne mijenjaju —
  dbo.Normativi koju koristi kasa ostaje netaknuta.

  Skriptu pokreće most sam pri prvom pokretanju; ovdje stoji da se može i ručno
  pregledati ili izvršiti iz SSMS-a.
*/

IF DB_ID('Analitika') IS NULL
    CREATE DATABASE Analitika;
GO

USE Analitika;
GO

IF OBJECT_ID('dbo.Normativ') IS NULL
CREATE TABLE dbo.Normativ
(
    Id            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Normativ PRIMARY KEY,
    ArtikalSifra  float             NOT NULL,
    ArtikalNaziv  nvarchar(100)     NOT NULL,
    -- Verzionisanje: recept vrijedi od datuma, a zatvara se umjesto da se briše,
    -- da obračun utroška za prošli period ostane tačan.
    VaziOd        date              NOT NULL CONSTRAINT DF_Normativ_VaziOd DEFAULT (CAST(GETDATE() AS date)),
    VaziDo        date              NULL,
    Izmijenjeno   datetime2(0)      NOT NULL CONSTRAINT DF_Normativ_Izmijenjeno DEFAULT (SYSDATETIME())
);
GO

-- Za jedan artikal smije postojati samo jedan otvoren recept.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UQ_Normativ_Aktivan')
CREATE UNIQUE INDEX UQ_Normativ_Aktivan
    ON dbo.Normativ (ArtikalSifra)
    WHERE VaziDo IS NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Normativ_Artikal')
CREATE INDEX IX_Normativ_Artikal ON dbo.Normativ (ArtikalSifra, VaziOd);
GO

IF OBJECT_ID('dbo.NormativStavka') IS NULL
CREATE TABLE dbo.NormativStavka
(
    Id                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_NormativStavka PRIMARY KEY,
    NormativId           int               NOT NULL
        CONSTRAINT FK_NormativStavka_Normativ REFERENCES dbo.Normativ(Id) ON DELETE CASCADE,
    RepromaterijalBarkod float             NOT NULL,
    Naziv                nvarchar(100)     NOT NULL,
    Kolicina             decimal(18,5)     NOT NULL,
    Jm                   nvarchar(10)      NULL,
    -- Nabavna cijena se prepisuje u trenutku unosa, da poskupljenje sirovine
    -- ne promijeni unazad izračunatu cijenu porcije.
    NabavnaCijena        decimal(18,5)     NOT NULL CONSTRAINT DF_NormativStavka_Cijena DEFAULT (0)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NormativStavka_Normativ')
CREATE INDEX IX_NormativStavka_Normativ ON dbo.NormativStavka (NormativId);
GO

IF OBJECT_ID('dbo.Postavke') IS NULL
CREATE TABLE dbo.Postavke
(
    Kljuc      nvarchar(50)  NOT NULL CONSTRAINT PK_Postavke PRIMARY KEY,
    Vrijednost nvarchar(max) NULL
);
GO
