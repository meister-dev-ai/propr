// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.TenantData;

/// <summary>
///     Inserts rows into any table of the migrated schema. Required columns without a default get a value of their
///     type, and a foreign key that the caller does not set gets a new parent row, so a test can place a row in every
///     table that carries a tenant or a client without knowing each table.
/// </summary>
internal sealed class SchemaRowSeeder(NpgsqlConnection connection)
{
    private Dictionary<string, List<Column>>? _columns;
    private Dictionary<(string Table, string Column), (string Table, string Column)>? _foreignKeys;

    /// <summary>The uuid columns named <c>client_id</c>, <c>tenant_id</c>, <c>ClientId</c> or <c>TenantId</c>, with their tables.</summary>
    public async Task<IReadOnlyList<(string Table, string Column)>> OwnedColumnsAsync()
    {
        await LoadAsync();
        // A text column of the same name holds an external identifier, such as the OAuth client of an SSO provider.
        return
        [
            .. _columns!.SelectMany(table => table.Value.Where(column => Owned(column.Name) && column.Type == "uuid")
                .Select(column => (table.Key, column.Name))).OrderBy(item => item.Key, StringComparer.Ordinal)
        ];
    }

    public static bool Owned(string column) => column is "client_id" or "tenant_id" or "ClientId" or "TenantId";

    /// <summary>Inserts one row and returns the value of its <c>id</c> column, when it has one.</summary>
    public async Task<object?> InsertAsync(string table, IReadOnlyDictionary<string, object> values)
    {
        await LoadAsync();
        var row = new Dictionary<string, object>(values, StringComparer.Ordinal);
        foreach (var column in _columns![table].Where(column => !row.ContainsKey(column.Name) && column.Required))
        {
            row[column.Name] = _foreignKeys!.TryGetValue((table, column.Name), out var parent)
                ? await InsertAsync(parent.Table, Inherit(parent.Table, row)) ?? throw new InvalidOperationException(
                    $"{parent.Table} has no id column for {table}.{column.Name}.")
                : Value(column);
        }

        // Optional references stay empty, so no further parent rows are created.
        var names = row.Keys.ToList();
        var returning = _columns[table].Any(column => column.Name == "id") ? " RETURNING id" :
            _columns[table].Any(column => column.Name == "Id") ? " RETURNING \"Id\"" : "";
        await using var command = new NpgsqlCommand(
            $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(name => $"\"{name}\""))}) VALUES " +
            $"({string.Join(", ", names.Select((name, index) => row[name] is Sql sql ? sql.Text : $"@p{index}"))}){returning}",
            connection);
        for (var index = 0; index < names.Count; index++)
        {
            if (row[names[index]] is Sql)
            {
                continue;
            }

            command.Parameters.Add(
                row[names[index]] is Jsonb json
                    ? new NpgsqlParameter($"p{index}", NpgsqlTypes.NpgsqlDbType.Jsonb) { Value = json.Text }
                    : new NpgsqlParameter($"p{index}", row[names[index]]));
        }

        return returning.Length > 0 ? await command.ExecuteScalarAsync() : await command.ExecuteNonQueryAsync();
    }

    // A parent row belongs to the same client and tenant as the row that needs it.
    private Dictionary<string, object> Inherit(string parent, IReadOnlyDictionary<string, object> child) =>
        _columns![parent].Where(column => Owned(column.Name) && child.ContainsKey(column.Name))
            .ToDictionary(column => column.Name, column => child[column.Name], StringComparer.Ordinal);

    private static object Value(Column column) => column.Type switch
    {
        "uuid" => Guid.NewGuid(),
        "text" or "character varying" => column.Name.EndsWith("json", StringComparison.Ordinal)
            ? "{}"
            : ("s" + Guid.NewGuid().ToString("N"))[..Math.Min(33, column.MaximumLength ?? 33)],
        "integer" or "smallint" => column.Type == "smallint" ? (object)(short)0 : 0,
        "bigint" => 0L,
        "boolean" => false,
        "numeric" => 0m,
        "real" => 0f,
        "double precision" => 0d,
        "timestamp with time zone" => DateTimeOffset.UtcNow,
        "date" => DateOnly.FromDateTime(DateTime.UtcNow),
        "bytea" => Array.Empty<byte>(),
        "jsonb" or "json" => new Jsonb("{}"),
        "ARRAY" => new Sql($"'{{}}'::{column.Format}"),
        "USER-DEFINED" when column.Format.StartsWith("vector(", StringComparison.Ordinal) =>
            new Sql($"array_fill(0::real, ARRAY[{column.Format[7..^1]}])::{column.Format}"),
        _ => throw new NotSupportedException($"No seed value for {column.Name} of type {column.Type}.")
    };

    private async Task LoadAsync()
    {
        if (_columns is not null)
        {
            return;
        }

        _columns = new(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT c.table_name, c.column_name, c.data_type, c.is_nullable = 'NO' AND c.column_default IS NULL
                           AND c.is_identity = 'NO' AND c.is_generated = 'NEVER', c.character_maximum_length,
                           (SELECT format_type(a.atttypid, a.atttypmod) FROM pg_attribute a
                            WHERE a.attrelid = format('%I', c.table_name)::regclass AND a.attname = c.column_name)
                         FROM information_schema.columns c JOIN information_schema.tables t
                           ON t.table_name = c.table_name AND t.table_schema = c.table_schema
                         WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
                         ORDER BY c.table_name, c.ordinal_position
                         """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var table = reader.GetString(0);
                if (!_columns.TryGetValue(table, out var list))
                {
                    _columns[table] = list = [];
                }

                list.Add(
                    new(
                        reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetString(5)));
            }
        }

        _foreignKeys = new();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT kcu.table_name, kcu.column_name, ccu.table_name, ccu.column_name
                         FROM information_schema.table_constraints tc
                         JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                         JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name AND tc.table_schema = ccu.table_schema
                         WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
                         """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                _foreignKeys[(reader.GetString(0), reader.GetString(1))] = (reader.GetString(2), reader.GetString(3));
            }
        }
    }

    private sealed record Column(string Name, string Type, bool Required, int? MaximumLength, string Format);

    /// <summary>A value written as SQL, for a type without a parameter mapping such as a pgvector column.</summary>
    private sealed record Sql(string Text);

    /// <summary>A JSON value, sent with the <c>jsonb</c> type.</summary>
    public sealed record Jsonb(string Text);
}
