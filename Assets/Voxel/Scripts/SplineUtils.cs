using UnityEngine;

/// <summary>
/// Piecewise-linear spline evaluation over an array of (input, output) control points.
/// Points must be sorted ascending by x (input value).
/// </summary>
public static class SplineUtils
{
    /// <summary>Evaluates the spline at <paramref name="t"/>, clamping to endpoints.</summary>
    public static float Evaluate(Vector2[] points, float t)
    {
        if (points == null || points.Length == 0) return 0f;
        if (points.Length == 1) return points[0].y;
        if (t <= points[0].x) return points[0].y;
        if (t >= points[points.Length - 1].x) return points[points.Length - 1].y;

        for (int i = 0; i < points.Length - 1; i++)
        {
            if (t < points[i + 1].x)
            {
                float span = points[i + 1].x - points[i].x;
                float frac = (span > 0f) ? (t - points[i].x) / span : 0f;
                return Mathf.Lerp(points[i].y, points[i + 1].y, frac);
            }
        }

        return points[points.Length - 1].y;
    }

    // ── Default control points ─────────────────────────────────────────────────

    /// <summary>
    /// Continentalness (C ∈ [-1,1]) → base height offset added to SeaLevel.
    /// Negative C = ocean basin, positive = inland highlands.
    /// </summary>
    public static Vector2[] DefaultContinentalnessSpline => new Vector2[]
    {
        new Vector2(-1.0f, -60f),
        new Vector2(-0.1f,   0f),
        new Vector2( 0.2f,  20f),
        new Vector2( 0.6f,  50f),
        new Vector2( 1.0f,  70f),
    };

    /// <summary>
    /// Erosion (E ∈ [0,1]) → height scale applied to the ridged PV amplitude.
    /// Low E = dramatic terrain, high E = flat/eroded plains.
    /// </summary>
    public static Vector2[] DefaultErosionSpline => new Vector2[]
    {
        new Vector2(0.0f, 1.00f),
        new Vector2(0.5f, 0.30f),
        new Vector2(0.8f, 0.08f),
        new Vector2(1.0f, 0.03f),
    };
}
