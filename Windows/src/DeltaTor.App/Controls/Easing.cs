namespace DeltaTor.App.Controls;

/// <summary>The easing curves the Android UI uses (Compose's FastOutSlowIn).</summary>
internal static class Easing
{
    /// <summary>cubic-bezier(0.4, 0, 0.2, 1) — Compose FastOutSlowInEasing.</summary>
    public static float FastOutSlowIn(float x)
    {
        // Bisection on x(t) = x, then evaluate y(t).
        float lo = 0f, hi = 1f, t = x;
        for (var i = 0; i < 24; i++)
        {
            t = (lo + hi) / 2f;
            var v = 1f - t;
            var cx = 3f * v * v * t * 0.4f + 3f * v * t * t * 0.2f + t * t * t;
            if (cx < x) lo = t; else hi = t;
        }
        var u = 1f - t;
        return 3f * u * u * t * 0f + 3f * u * t * t * 1f + t * t * t;
    }
}
