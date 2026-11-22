using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaLibrary.Service.Tests;

public sealed class FoundationTests : IDisposable
{
    private readonly TestDirectory temporary = new();

    private string DatabasePath => Path.Combine(temporary.FullName, "本地素材库.sqlite3");

    [Fact]
    public void InitialMigrationPersistsAndSupportsChineseTrigramQueries()
    {
        using (var database = new LibraryDatabase(DatabasePath))
        {
            var health = database.Health();
            Assert.Equal("wal", health.JournalMode);
            Assert.True(health.ForeignKeys);
            Assert.True(health.Fts5Trigram);
        }

        using var reopened = new LibraryDatabase(DatabasePath);
        Assert.Equal(11, reopened.Health().SchemaVersion);
        using var inspect = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString());
        inspect.Open();
        using var command = inspect.CreateCommand();
        command.CommandText = "SELECT count(*) FROM workspace_state WHERE id = 1";
        Assert.Equal(1L, command.ExecuteScalar());

        command.CommandText = """
            CREATE VIRTUAL TABLE temp.samples USING fts5(remark, tokenize='trigram');
            INSERT INTO temp.samples(remark) VALUES ('青岛海边日落'), ('三亚海边日落');
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT remark FROM temp.samples WHERE remark MATCH $term";
        command.Parameters.AddWithValue("$term", "青岛海边");
        Assert.Equal("青岛海边日落", command.ExecuteScalar());
    }

    [Fact]
    public async Task ProtocolPreservesIdsAndContinuesAfterInvalidRequests()
    {
        using var database = new LibraryDatabase(DatabasePath);
        using var input = new StringReader("""
            {
            {"v":2,"id":"错误版本","method":"app.health","params":{}}
            {"v":1,"id":"不支持","method":"draft.unknown","params":{}}
            {"v":1,"id":"多余参数","method":"app.health","params":{"extra":true}}
            {"v":1,"id":"健康检查","method":"app.health","params":{}}
            """);
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        await new JsonLinesServer(database, input, output, diagnostics).RunAsync();

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        AssertError(lines[0], null, "INVALID_ARGUMENT");
        AssertError(lines[1], "错误版本", "INVALID_ARGUMENT");
        AssertError(lines[2], "不支持", "METHOD_NOT_FOUND");
        AssertError(lines[3], "多余参数", "INVALID_ARGUMENT");
        using var response = JsonDocument.Parse(lines[4]);
        Assert.Equal("健康检查", response.RootElement.GetProperty("id").GetString());
        var health = response.RootElement.GetProperty("result");
        Assert.Equal(1, health.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("ready", health.GetProperty("database").GetProperty("status").GetString());
        Assert.Equal("unavailable", health.GetProperty("capabilities").GetProperty("wic").GetString());
        Assert.Equal("", diagnostics.ToString());
    }

    private static void AssertError(string line, string? id, string code)
    {
        using var response = JsonDocument.Parse(line);
        var envelope = response.RootElement;
        Assert.Equal(1, envelope.GetProperty("v").GetInt32());
        Assert.Equal(id, envelope.GetProperty("id").GetString());
        Assert.Equal(code, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.False(envelope.TryGetProperty("result", out _));
    }

    public void Dispose() => temporary.Dispose();
}
