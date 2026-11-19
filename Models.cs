using System.Text.Json;
using System.Text.Json.Serialization;

namespace RobotFactoryBot;

// — Служебные DTO —

public record OkResponse([property: JsonPropertyName("ok")] bool Ok);

public record HealthResponse([property: JsonPropertyName("status")] string Status);

public record InfoResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("specVersion")] string[] SpecVersion
);

public record ErrorResponse([property: JsonPropertyName("error")] ErrorDetail Error);
public record ErrorDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message
);

// — Конфигурация мира (/start) —

public class StartRequest
{
    [JsonPropertyName("runId")] public string RunId { get; set; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "practice";
    [JsonPropertyName("horizon")] public int Horizon { get; set; } = 600;
    [JsonPropertyName("eventTimeoutMs")] public int EventTimeoutMs { get; set; } = 2000;
    [JsonPropertyName("idleEvery")] public int IdleEvery { get; set; } = 10;
    [JsonPropertyName("capacity")] public int Capacity { get; set; } = 12;
    [JsonPropertyName("storageFee")] public Dictionary<string, double> StorageFee { get; set; } = new();
    [JsonPropertyName("offerFee")] public double OfferFee { get; set; } = 1.0;
    [JsonPropertyName("buyerCooldown")] public int BuyerCooldown { get; set; } = 8;
    [JsonPropertyName("priceCeiling")] public double PriceCeiling { get; set; } = 100.0;
    [JsonPropertyName("buyers")] public List<string> Buyers { get; set; } = new();
    [JsonPropertyName("attributes")] public Dictionary<string, List<JsonElement>> Attributes { get; set; } = new();
    [JsonPropertyName("machines")] public List<MachineConfig> Machines { get; set; } = new();
}

public class MachineConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("ops")] public List<OperationConfig> Ops { get; set; } = new();
}

public class OperationConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("inputs")] public List<ItemTemplate> Inputs { get; set; } = new();
    [JsonPropertyName("inherit")] public int? Inherit { get; set; }
    [JsonPropertyName("output")] public ItemTemplate Output { get; set; } = new();
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("cost")] public double Cost { get; set; }
}

public class ItemTemplate
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("props")] public Dictionary<string, JsonElement>? Props { get; set; }
}

// — События и состояние (/event) —

public class EventRequest
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("t")] public int T { get; set; }
    [JsonPropertyName("event")] public ArenaEvent Event { get; set; } = new();
    [JsonPropertyName("state")] public WorldState State { get; set; } = new();
    [JsonPropertyName("report")] public List<CommandReport>? Report { get; set; }
}

public class ArenaEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    // Для supply и machine_done
    [JsonPropertyName("item")] public Item? Item { get; set; }

    // Для supply
    [JsonPropertyName("price")] public double? Price { get; set; }

    // Для machine_done
    [JsonPropertyName("machineId")] public string? MachineId { get; set; }
    [JsonPropertyName("opId")] public string? OpId { get; set; }

    // Для sale_result
    [JsonPropertyName("buyerId")] public string? BuyerId { get; set; }
    [JsonPropertyName("itemId")] public string? ItemId { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; } // "sold" | "refused"
    [JsonPropertyName("reason")] public string? Reason { get; set; }   // "below_min_price" | "cooldown"
}

public class WorldState
{
    [JsonPropertyName("cash")] public double Cash { get; set; }
    [JsonPropertyName("storage")] public StorageState Storage { get; set; } = new();
    [JsonPropertyName("machines")] public List<MachineRuntimeState> Machines { get; set; } = new();
    [JsonPropertyName("buyers")] public List<BuyerRuntimeState> Buyers { get; set; } = new();
}

public class StorageState
{
    [JsonPropertyName("capacity")] public int Capacity { get; set; }
    [JsonPropertyName("free")] public int Free { get; set; }
    [JsonPropertyName("items")] public List<Item> Items { get; set; } = new();
}

public class Item
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("props")] public Dictionary<string, JsonElement> Props { get; set; } = new();

    public string? GetColor()
    {
        if (Props != null && Props.TryGetValue("color", out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    public bool? GetHandles()
    {
        if (Props != null && Props.TryGetValue("handles", out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }
}

public class MachineRuntimeState
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("busyUntil")] public int? BusyUntil { get; set; }
}

public class BuyerRuntimeState
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("cooldownUntil")] public int? CooldownUntil { get; set; }
}

public class CommandReport
{
    [JsonPropertyName("cmd")] public int Cmd { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

// — Ответные команды —

public class EventResponse
{
    [JsonPropertyName("commands")] public List<FactoryCommand> Commands { get; set; } = new();
}

public class FactoryCommand
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemId { get; set; }

    [JsonPropertyName("machineId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MachineId { get; set; }

    [JsonPropertyName("opId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OpId { get; set; }

    [JsonPropertyName("itemIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ItemIds { get; set; }

    [JsonPropertyName("buyerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuyerId { get; set; }

    [JsonPropertyName("minPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MinPrice { get; set; }

    public static FactoryCommand Accept(string itemId) => new() { Cmd = "accept", ItemId = itemId };
    public static FactoryCommand Discard(string itemId) => new() { Cmd = "discard", ItemId = itemId };
    public static FactoryCommand Start(string machineId, string opId, params string[] itemIds) => new()
    {
        Cmd = "start",
        MachineId = machineId,
        OpId = opId,
        ItemIds = itemIds.ToList()
    };
    public static FactoryCommand Offer(string buyerId, string itemId, double minPrice) => new()
    {
        Cmd = "offer",
        BuyerId = buyerId,
        ItemId = itemId,
        MinPrice = Math.Round(minPrice, 2)
    };
}

// — Конец прогона (/finish) и отладка (/debug) —

public class FinishRequest
{
    [JsonPropertyName("profit")] public double Profit { get; set; }
    [JsonPropertyName("sold")] public int Sold { get; set; }
    [JsonPropertyName("refused")] public int Refused { get; set; }
    [JsonPropertyName("buyers")] public List<JsonElement>? Buyers { get; set; }
}

public class DebugResponse
{
    [JsonPropertyName("beliefs")] public List<DebugBelief> Beliefs { get; set; } = new();
    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
}

public class DebugBelief
{
    [JsonPropertyName("buyerId")] public string BuyerId { get; set; } = string.Empty;
    [JsonPropertyName("color")] public string Color { get; set; } = string.Empty;
    [JsonPropertyName("handles")] public bool Handles { get; set; }
    [JsonPropertyName("low")] public double Low { get; set; }
    [JsonPropertyName("high")] public double High { get; set; }
}