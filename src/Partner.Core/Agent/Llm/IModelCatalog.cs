namespace Partner.Core.Agent.Llm;

/// <summary>
/// An endpoint that can list the model ids it serves. Both wire protocols implement it; the settings page
/// uses it to fill the model dropdown without knowing which protocol the endpoint speaks.
/// </summary>
public interface IModelCatalog
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);
}
