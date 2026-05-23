using System.Text.Json;
using MongoDB.Bson;
using Npgsql;
using NpgsqlTypes;

namespace aspbackend.Data;

public sealed class MongoDbContext
{
    private readonly string? _connection;
    private NpgsqlDataSource? _dataSource;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _schemaReady;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public MongoDbContext(IConfiguration configuration)
    {
        _connection = configuration.GetConnectionString("Neon")
            ?? configuration["NEON_DATABASE_URL"]
            ?? configuration["DATABASE_URL"];

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new ObjectIdJsonConverter());
        _jsonOptions.Converters.Add(new BsonValueJsonConverter());
    }

    public bool HasConnectionString => !string.IsNullOrWhiteSpace(_connection);

    public Task<List<T>> AllAsync<T>(string table) => QueryAsync<T>(table, null);

    public async Task<T?> GetAsync<T>(string table, ObjectId id)
    {
        var items = await QueryAsync<T>(table, id.ToString());
        return items.FirstOrDefault();
    }

    public async Task InsertAsync<T>(string table, ObjectId id, T value)
    {
        await EnsureSchemaAsync();
        await using var cmd = DataSource.CreateCommand($"""
            insert into {table} (id, data, created_at, updated_at)
            values ($1, $2, now(), now())
            on conflict (id) do update set data = excluded.data, updated_at = now()
            """);
        cmd.Parameters.AddWithValue(id.ToString());
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(value, _jsonOptions), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync();
    }

    public Task ReplaceAsync<T>(string table, ObjectId id, T value) => InsertAsync(table, id, value);

    public async Task DeleteAsync(string table, ObjectId id)
    {
        await EnsureSchemaAsync();
        await using var cmd = DataSource.CreateCommand($"delete from {table} where id = $1");
        cmd.Parameters.AddWithValue(id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteWhereAsync<T>(string table, Func<T, bool> predicate)
    {
        var items = await AllAsync<T>(table);
        foreach (var item in items.Where(predicate))
        {
            var id = (ObjectId)(typeof(T).GetProperty("Id")?.GetValue(item) ?? ObjectId.Empty);
            if (id != ObjectId.Empty) await DeleteAsync(table, id);
        }
    }

    private async Task<List<T>> QueryAsync<T>(string table, string? id)
    {
        await EnsureSchemaAsync();
        var sql = id is null ? $"select data from {table}" : $"select data from {table} where id = $1";
        await using var cmd = DataSource.CreateCommand(sql);
        if (id is not null) cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<T>();
        while (await reader.ReadAsync())
        {
            var json = reader.GetString(0);
            var item = JsonSerializer.Deserialize<T>(json, _jsonOptions);
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            var tables = new[]
            {
                "users", "documents", "document_versions", "tasks", "templates",
                "notifications", "subscriptions", "analytics_daily", "ai_history"
            };

            await using var conn = await DataSource.OpenConnectionAsync();
            foreach (var table in tables)
            {
                await using var cmd = new NpgsqlCommand($"""
                    create table if not exists {table} (
                        id text primary key,
                        data jsonb not null,
                        created_at timestamptz not null default now(),
                        updated_at timestamptz not null default now()
                    );
                    create index if not exists idx_{table}_data_gin on {table} using gin (data);
                    """, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static string NormalizeConnectionString(string connection)
    {
        // Channel Binding=Require can cause timeouts on some networks; Prefer is safer for Neon dev
        if (connection.Contains("Channel Binding=Require", StringComparison.OrdinalIgnoreCase))
            connection = connection.Replace("Channel Binding=Require", "Channel Binding=Prefer", StringComparison.OrdinalIgnoreCase);
        return connection;
    }

    private NpgsqlDataSource DataSource
    {
        get
        {
            if (_dataSource is not null) return _dataSource;
            if (string.IsNullOrWhiteSpace(_connection))
                throw new InvalidOperationException("Database connection missing. Set ConnectionStrings__Neon in Render.");

            var sourceBuilder = new NpgsqlDataSourceBuilder(NormalizeConnectionString(_connection));
            sourceBuilder.ConnectionStringBuilder.Timeout = 30;
            sourceBuilder.ConnectionStringBuilder.CommandTimeout = 60;
            _dataSource = sourceBuilder.Build();
            return _dataSource;
        }
    }
}
