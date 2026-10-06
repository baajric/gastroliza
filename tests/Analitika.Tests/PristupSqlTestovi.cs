using Analitika.Agent;
using Microsoft.Data.SqlClient;

namespace Analitika.Tests;

/// <summary>
/// Prava koja instalacija daje servisu mosta, provjerena na stvarnom SQL Serveru.
/// Umjesto virtuelnog Windows naloga koristi se privremeni SQL login (EXECUTE AS ga
/// glumi bez prijave), koji se na kraju briše.
/// </summary>
public class PristupSqlTestovi
{
    private const string Veza =
        "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

    private static bool BazaDostupna()
    {
        try
        {
            using var v = new SqlConnection(Veza);
            v.Open();
            using var k = v.CreateCommand();
            k.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin') * (SELECT COUNT(*) FROM sys.databases WHERE name = 'EtisRpos_2026')";
            return (int)k.ExecuteScalar()! == 1;
        }
        catch { return false; }
    }

    private static async Task<object?> SkalarAsync(SqlConnection v, string sql)
    {
        await using var k = v.CreateCommand();
        k.CommandText = sql;
        return await k.ExecuteScalarAsync();
    }

    [SkippableFact]
    public async Task Most_cita_bazu_kase_a_pise_samo_u_Analitika()
    {
        Skip.IfNot(BazaDostupna(), "Lokalni SQL Server sa sysadmin pravom nije dostupan.");

        var login = "test_most_" + Guid.NewGuid().ToString("N")[..8];
        await using var v = new SqlConnection(Veza);
        await v.OpenAsync();
        await SkalarAsync(v, $"CREATE LOGIN [{login}] WITH PASSWORD = '{Guid.NewGuid():N}aA1!', CHECK_POLICY = OFF;");

        try
        {
            await PristupSql.DodijeliAsync(v, login);
            await SkalarAsync(v, "USE [master];");

            await SkalarAsync(v, $"EXECUTE AS LOGIN = '{login}';");
            try
            {
                Assert.Equal(1, await SkalarAsync(v, "SELECT HAS_DBACCESS('EtisRpos_2026');"));
                Assert.NotNull(await SkalarAsync(v, "SELECT TOP 1 DATUM FROM [EtisRpos_2026].dbo.POS_Stavke;"));

                // Upis u bazu kase mora biti odbijen.
                await Assert.ThrowsAsync<SqlException>(() =>
                    SkalarAsync(v, "CREATE TABLE [EtisRpos_2026].dbo.MostTest (Id int);"));

                // U Analitika smije praviti tabele (normativi, uvezene godine).
                await SkalarAsync(v, "USE [Analitika]; CREATE TABLE dbo.MostTestPrava (Id int); DROP TABLE dbo.MostTestPrava; USE [master];");
            }
            finally
            {
                await SkalarAsync(v, "REVERT;");
            }
        }
        finally
        {
            await SkalarAsync(v, $"""
                USE [Analitika];
                IF USER_ID('{login}') IS NOT NULL DROP USER [{login}];
                USE [master];
                DROP LOGIN [{login}];
                """);
        }
    }
}
