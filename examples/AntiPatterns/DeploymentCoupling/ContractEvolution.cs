using System.Text.Json;
using Commerce.Contracts.Events;

namespace AntiPatterns.DeploymentCoupling;

/// <summary>
/// Problematic shared model. A change to this type forces every service that references it to redeploy.
/// </summary>
public sealed class SharedOrderEntity
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
    public List<SharedInventoryItem> InventoryItems { get; set; } = [];
}

public sealed class SharedInventoryItem
{
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
}

public static class ContractCompatibility
{
    private static readonly string[] OrderConfirmedV1Required = ["orderId", "totalAmount", "currency"];

    public static bool CanDeserializeOrderConfirmedV1(string json)
    {
        var model = JsonSerializer.Deserialize<OrderConfirmedV1>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return model is not null && model.OrderId != Guid.Empty && model.TotalAmount > 0 && !string.IsNullOrWhiteSpace(model.Currency);
    }

    public static IReadOnlyList<string> FindRemovedRequiredFields(string candidateJson)
    {
        using var document = JsonDocument.Parse(candidateJson);
        return OrderConfirmedV1Required
            .Where(property => !document.RootElement.TryGetProperty(property, out _))
            .ToArray();
    }
}
