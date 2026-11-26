namespace OnionLLM.Design;

/// <summary>Easing curves from DESIGN.md §2.7.1 (cubic-bezier approximations for MAUI animations).</summary>
public static class OnionEasing
{
    /// <summary><c>motion.ease.standard</c> — cubic-bezier(0.2, 0, 0, 1).</summary>
    public static readonly Easing Standard = new(t => CubicBezier(t, 0.2, 0, 0, 1));

    /// <summary><c>motion.ease.enter</c> — cubic-bezier(0, 0, 0, 1).</summary>
    public static readonly Easing Enter = new(t => CubicBezier(t, 0, 0, 0, 1));

    /// <summary><c>motion.ease.exit</c> — cubic-bezier(0.3, 0, 1, 1).</summary>
    public static readonly Easing Exit = new(t => CubicBezier(t, 0.3, 0, 1, 1));

    /// <summary><c>motion.ease.spring</c> — critically damped spring with ~4% overshoot.</summary>
    public static readonly Easing Spring = new(t =>
        t >= 1 ? 1 : 1 - Math.Exp(-6.5 * t) * Math.Cos(4.2 * t));

    /// <summary>Evaluates a CSS-style cubic-bezier easing at progress <paramref name="x"/>.</summary>
    public static double CubicBezier(double x, double x1, double y1, double x2, double y2)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        // Solve bezierX(t) = x with Newton-Raphson, falling back to bisection.
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Bezier(t, x1, x2) - x;
            if (Math.Abs(error) < 1e-6)
            {
                return Bezier(t, y1, y2);
            }

            var slope = BezierSlope(t, x1, x2);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            t -= error / slope;
        }

        double low = 0, high = 1;
        t = x;
        for (var i = 0; i < 30; i++)
        {
            var value = Bezier(t, x1, x2);
            if (Math.Abs(value - x) < 1e-6)
            {
                break;
            }

            if (value < x)
            {
                low = t;
            }
            else
            {
                high = t;
            }

            t = (low + high) / 2;
        }

        return Bezier(t, y1, y2);
    }

    private static double Bezier(double t, double p1, double p2) =>
        3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;

    private static double BezierSlope(double t, double p1, double p2) =>
        3 * (1 - t) * (1 - t) * p1 + 6 * (1 - t) * t * (p2 - p1) + 3 * t * t * (1 - p2);
}
