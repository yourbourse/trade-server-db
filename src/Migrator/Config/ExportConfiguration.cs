// /********************************************************************
//  *
//  * CONFIDENTIAL
//  * __________________
//  *
//  * Copyright (c)  Your Bourse Limited
//  * All Rights Reserved.
//  *
//  * NOTICE:  All information contained herein is, and remains
//  * the property of Your Bourse Limited. The intellectual and
//  * technical concepts contained herein are proprietary to Your
//  * Bourse Limited and may be covered by UK and Foreign Patents,
//  * patents in process, and are protected by trade secret or
//  * copyright law. Dissemination of this information or reproduction
//  * of this material is strictly forbidden unless prior written
//  * permission is obtained from Your Bourse Limited.
//  */

using System.Text.Json;
using System.Text.Json.Serialization;

namespace YB.TradeServer.Db.Migrator.Config;

/// <summary>
/// The trade server export_config.json, as described in the Export Setup guide.
/// </summary>
/// <remarks>
/// Only the fields the migrator needs are modelled - the connection details and the table names. Everything
/// else in the file (queue watermarks, batch sizes, retry delays) belongs to the trade server and is ignored.
/// </remarks>
public record ExportConfiguration
{
    private static readonly JsonSerializerOptions serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    [JsonPropertyName("pipelines")]
    public IList<ExportPipeline> Pipelines { get; set; } = [];

    public static ExportConfiguration Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Export configuration file not found: {path}", path);
        }

        var content = File.ReadAllText(path);

        var configuration = JsonSerializer.Deserialize<ExportConfiguration>(content, serializerOptions)
            ?? throw new InvalidOperationException($"Unable to parse the export configuration file: {path}");

        if (configuration.Pipelines.Count == 0)
        {
            throw new InvalidOperationException($"The export configuration file does not declare any pipelines: {path}");
        }

        return configuration;
    }
}

public record ExportPipeline
{
    /// <summary>
    /// The exported entity, i.e. "trade".
    /// </summary>
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "trade";

    /// <summary>
    /// The target database, i.e. "postgresql" or "clickhouse".
    /// </summary>
    [JsonPropertyName("target")]
    public string Target { get; set; } = null!;

    [JsonPropertyName("export_config")]
    public ExportTarget ExportTarget { get; set; } = new();
}

public record ExportTarget
{
    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>
    /// The name of the trades table. Substituted into the scripts as ##trades_table##.
    /// </summary>
    [JsonPropertyName("table")]
    public string? Table { get; set; }

    /// <summary>
    /// The name of the export cursor table. Substituted into the scripts as ##cursor_table##.
    /// </summary>
    [JsonPropertyName("cursor_table")]
    public string? CursorTable { get; set; }

    [JsonPropertyName("sender_id")]
    public long? SenderId { get; set; }
}
