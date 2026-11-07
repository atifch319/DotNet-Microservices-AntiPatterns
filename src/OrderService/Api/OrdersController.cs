using System.Net.Http.Json;
using Commerce.Contracts.Hosting;
using Commerce.Orders.Application;
using Commerce.Orders.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Commerce.Orders.Api;

[ApiController]
public sealed class OrdersController(OrderApplication orders, InventoryAvailabilityClient inventory) : ControllerBase
{
    [HttpPost("/api/orders")]
    public async Task<ActionResult<OrderResponse>> Create(
        [FromBody] CreateOrderRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        request ??= new CreateOrderRequest();
        request.Lines ??= [];
        var result = await orders.CreateAsync(
            new CreateOrder(request.CustomerId, request.PaymentSimulation, request.Currency, request.Lines.Select(line => new CreateOrderLine(line.ProductId, line.Quantity, line.UnitPrice)).ToList()),
            idempotencyKey ?? "",
            cancellationToken);

        var body = OrderResponse.From(result.Order);
        if (result.Status == CreateOrderStatus.Replay)
        {
            return Ok(body);
        }

        return AcceptedAtAction(nameof(Get), new { id = result.Order.Id }, body);
    }

    [HttpPost("/api/orders/{id:guid}/compensation/retry")]
    public async Task<ActionResult<OrderResponse>> RetryCompensation(Guid id, CancellationToken cancellationToken)
    {
        var result = await orders.RetryCompensationAsync(id, cancellationToken);
        return result.Status switch
        {
            CompensationRetryStatus.NotFound => NotFound(),
            CompensationRetryStatus.Rejected => Conflict(new { title = "Compensation retry was rejected", detail = result.Detail }),
            _ => AcceptedAtAction(nameof(Get), new { id }, OrderResponse.From(result.Order!))
        };
    }

    [HttpGet("/api/orders/{id:guid}")]
    public async Task<ActionResult<OrderResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(id, cancellationToken);
        return order is null ? NotFound() : Ok(OrderResponse.From(order));
    }

    [HttpGet("/api/orders/{id:guid}/status")]
    public async Task<ActionResult<OrderStatusResponse>> GetStatus(Guid id, CancellationToken cancellationToken)
    {
        var order = await orders.GetAsync(id, cancellationToken);
        return order is null
            ? NotFound()
            : Ok(new OrderStatusResponse(order.Id, order.Status.ToString(), order.FailureReason, order.UpdatedAt));
    }

    /// <summary>
    /// A justified synchronous call: the caller needs the current quantity before deciding.
    /// Placing an order does not use this path.
    /// </summary>
    [HttpGet("/api/orders/availability/{productId}")]
    public async Task<ActionResult<InventoryAvailability>> Availability(string productId, CancellationToken cancellationToken)
    {
        var availability = await inventory.GetAsync(productId, cancellationToken);
        return availability is null ? NotFound() : Ok(availability);
    }
}

public sealed class InventoryAvailabilityClient(HttpClient http)
{
    public async Task<InventoryAvailability?> GetAsync(string productId, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/api/inventory/{Uri.EscapeDataString(productId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventoryAvailability>(cancellationToken);
    }
}

public sealed record InventoryAvailability(string ProductId, int AvailableQuantity, int ReservedQuantity);

public sealed class CreateOrderRequest
{
    public string CustomerId { get; set; } = "";
    public string PaymentSimulation { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public List<CreateOrderLineRequest> Lines { get; set; } = [];
}

public sealed class CreateOrderLineRequest
{
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed record OrderResponse(
    Guid Id,
    string Status,
    string CustomerId,
    decimal TotalAmount,
    string Currency,
    string IdempotencyKey,
    Guid? ReservationId,
    Guid? PaymentId,
    string? FailureReason,
    IReadOnlyList<OrderLineResponse> Lines,
    IReadOnlyList<OrderTransitionResponse> Transitions)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.Status.ToString(),
        order.CustomerId,
        order.TotalAmount,
        order.Currency,
        order.IdempotencyKey,
        order.ReservationId,
        order.PaymentId,
        order.FailureReason,
        order.Items.Select(item => new OrderLineResponse(item.ProductId, item.Quantity, item.UnitPrice)).ToList(),
        order.Transitions
            .OrderBy(transition => transition.OccurredAt)
            .Select(transition => new OrderTransitionResponse(transition.FromStatus.ToString(), transition.ToStatus.ToString(), transition.Trigger, transition.OccurredAt))
            .ToList());
}

public sealed record OrderLineResponse(string ProductId, int Quantity, decimal UnitPrice);

public sealed record OrderTransitionResponse(string From, string To, string Trigger, DateTimeOffset OccurredAt);

public sealed record OrderStatusResponse(Guid OrderId, string Status, string? FailureReason, DateTimeOffset UpdatedAt);
