// Port of packages/shared/src/domain/weather/types.ts — keep in lockstep with the original.

namespace Fluitown.Domain;

/// <summary>
/// Allocation-free output of the shared weather timeline. This is presentation truth, not gameplay state.
/// Callers keep one frame and let `sampleWeatherInto` mutate it.
/// </summary>
public sealed class WeatherFrame
{
    public double cloudCover;
    public double lightScale;
    public double ambientScale;
    public double cooling;
    /// <summary>Persistent deterministic snow lying on suitable ground, 0..1.</summary>
    public double snowCover;
    public double lightning;
}
