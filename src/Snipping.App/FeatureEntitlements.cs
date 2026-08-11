namespace Snipping.App;

/// <summary>
/// Runtime capabilities supplied by the application/licensing layer.
/// This is intentionally not persisted in user settings so a future
/// entitlement provider can become the single source of truth.
/// </summary>
internal sealed class FeatureEntitlements
{
    public bool AnnotationEnhancementsEnabled { get; init; } = true;
}
