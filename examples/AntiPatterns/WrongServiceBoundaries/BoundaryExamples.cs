namespace AntiPatterns.WrongServiceBoundaries;

public sealed record OrderDraft(string CustomerId, string ProductId, int Quantity);

public interface IValidationService
{
    Task ValidateAsync(OrderDraft draft, CancellationToken cancellationToken);
}

public interface IRepositoryService
{
    Task SaveAsync(OrderDraft draft, CancellationToken cancellationToken);
}

public interface IControllerService
{
    Task<int> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken);
}

public sealed class RemoteCallLog
{
    public List<string> Calls { get; } = [];
}

/// <summary>
/// Technical-layer services. Placing one order crosses a validation service and a repository service.
/// </summary>
public sealed class LayeredControllerService(IValidationService validation, IRepositoryService repository, RemoteCallLog log) : IControllerService
{
    public async Task<int> PlaceAsync(OrderDraft draft, CancellationToken cancellationToken)
    {
        log.Calls.Add("controller");
        await validation.ValidateAsync(draft, cancellationToken);
        await repository.SaveAsync(draft, cancellationToken);
        return log.Calls.Count;
    }
}

public sealed class RemoteValidationService(RemoteCallLog log) : IValidationService
{
    public Task ValidateAsync(OrderDraft draft, CancellationToken cancellationToken)
    {
        log.Calls.Add("validation-service");
        if (draft.Quantity <= 0)
        {
            throw new InvalidOperationException("Quantity must be positive.");
        }

        return Task.CompletedTask;
    }
}

public sealed class RemoteRepositoryService(RemoteCallLog log) : IRepositoryService
{
    public Task SaveAsync(OrderDraft draft, CancellationToken cancellationToken)
    {
        log.Calls.Add("repository-service");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The same business capability kept inside one module. No remote hop is required to validate and save.
/// </summary>
public sealed class OrderPlacementModule
{
    public int Place(OrderDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.CustomerId) || draft.Quantity <= 0)
        {
            throw new InvalidOperationException("The order draft is invalid.");
        }

        return 1;
    }
}
