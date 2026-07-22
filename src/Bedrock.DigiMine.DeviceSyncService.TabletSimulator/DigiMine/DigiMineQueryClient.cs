using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.DigiMine;

public static class DigiMineQueryClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<DigiMineQueryResult> QueryAsync(
        DigiMineQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var baseUrl = (request.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("DigiMine configuration base URL is required.");
        }

        var token = NormalizeBearerToken(request.BearerToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Bearer token is required.");
        }

        var target = (request.Target ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("Query target is required.");
        }

        if (!target.StartsWith('/'))
        {
            target = "/" + target;
        }

        var pageNumber = request.PageNumber <= 0 ? 1 : request.PageNumber;
        var pageSize = request.PageSize <= 0 ? 10 : Math.Min(request.PageSize, 100);

        var body = new
        {
            target,
            pageParameters = new { pageNumber, pageSize },
            searchText = request.SearchText?.Trim() ?? string.Empty,
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/api/v1.0/Common/query");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(request.OperationalUnitId))
        {
            message.Headers.TryAddWithoutValidation(
                "x-operational-unit-id",
                request.OperationalUnitId.Trim());
        }

        message.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = responseText.Length > 400 ? responseText[..400] + "…" : responseText;
            throw new InvalidOperationException(
                $"DigiMine query failed ({(int)response.StatusCode} {response.ReasonPhrase}): {snippet}");
        }

        using var document = JsonDocument.Parse(responseText);
        return MapTabularResponse(document.RootElement);
    }

    public static DigiMineQueryResult MapTabularResponse(JsonElement root)
    {
        var items = new List<DigiMineListItem>();
        var totalCount = 0;
        var pageCount = 0;

        if (root.TryGetProperty("Metadata", out var metadata)
            && metadata.TryGetProperty("totalCount", out var total)
            && total.TryGetInt32(out var totalValue))
        {
            totalCount = totalValue;
        }
        else if (root.TryGetProperty("metadata", out var metadataCamel)
                 && metadataCamel.TryGetProperty("totalCount", out var totalCamel)
                 && totalCamel.TryGetInt32(out var totalCamelValue))
        {
            totalCount = totalCamelValue;
        }

        if (root.TryGetProperty("paging", out var paging)
            && paging.TryGetProperty("pageCount", out var pages)
            && pages.TryGetInt32(out var pageCountValue))
        {
            pageCount = pageCountValue;
        }

        if (!TryGetRows(root, out var rows))
        {
            return new DigiMineQueryResult
            {
                Items = [],
                TotalCount = totalCount,
                PageCount = pageCount,
            };
        }

        foreach (var row in rows.EnumerateArray())
        {
            var id = ReadString(row, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = ReadCellValue(row, "name");
            var type = ReadCellValue(row, "type");
            var serial = ReadCellValue(row, "serialNumber");
            var status = ReadCellValue(row, "status");
            var category = ReadCellValue(row, "deviceCategory");
            var assignedType = ReadCellValue(row, "assignedToEntityType");
            var assignedName = ReadCellValue(row, "assignedToEntityName");

            var subtitleParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(type)) subtitleParts.Add(type);
            if (!string.IsNullOrWhiteSpace(category)) subtitleParts.Add(category);
            if (!string.IsNullOrWhiteSpace(serial)) subtitleParts.Add(serial);
            if (!string.IsNullOrWhiteSpace(status)) subtitleParts.Add(status);
            if (!string.IsNullOrWhiteSpace(assignedType) || !string.IsNullOrWhiteSpace(assignedName))
            {
                subtitleParts.Add(
                    $"Assigned: {assignedType}{(string.IsNullOrWhiteSpace(assignedName) ? "" : " / " + assignedName)}");
            }

            items.Add(new DigiMineListItem
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                Subtitle = string.Join(" · ", subtitleParts),
            });
        }

        if (totalCount == 0)
        {
            totalCount = items.Count;
        }

        return new DigiMineQueryResult
        {
            Items = items,
            TotalCount = totalCount,
            PageCount = pageCount,
        };
    }

    private static bool TryGetRows(JsonElement root, out JsonElement rows)
    {
        if (root.TryGetProperty("Data", out var data)
            && data.TryGetProperty("rows", out rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("data", out var dataCamel)
            && dataCamel.TryGetProperty("rows", out rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        rows = default;
        return false;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    private static string ReadCellValue(JsonElement row, string propertyName)
    {
        if (!row.TryGetProperty(propertyName, out var cell))
        {
            return string.Empty;
        }

        if (cell.ValueKind == JsonValueKind.Object
            && cell.TryGetProperty("value", out var nested))
        {
            return nested.ValueKind switch
            {
                JsonValueKind.String => nested.GetString()?.Trim() ?? string.Empty,
                JsonValueKind.Number => nested.GetRawText(),
                JsonValueKind.Null => string.Empty,
                _ => nested.ToString()?.Trim() ?? string.Empty,
            };
        }

        return cell.ValueKind switch
        {
            JsonValueKind.String => cell.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => cell.GetRawText(),
            _ => string.Empty,
        };
    }

    private static string NormalizeBearerToken(string? token)
    {
        var value = token?.Trim() ?? string.Empty;
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Bearer ".Length..].Trim();
        }

        return value;
    }
}

public sealed class DigiMineQueryRequest
{
    public string BaseUrl { get; set; } = string.Empty;
    public string BearerToken { get; set; } = string.Empty;
    public string OperationalUnitId { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 10;
}

public sealed class DigiMineQueryResult
{
    public IReadOnlyList<DigiMineListItem> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int PageCount { get; init; }
}

public sealed class DigiMineListItem
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
}
