namespace Numos.CoreSim.IntegrationTests.Euler;

/// <summary>
///     Exact solution of the one-dimensional Riemann problem for an ideal gas with one adiabatic index, after
///     Toro, "Riemann Solvers and Numerical Methods for Fluid Dynamics", chapter 4.
/// </summary>
/// <remarks>
///     The star pressure is the root of <c>f(p) = f_L(p) + f_R(p) + (u_R − u_L)</c>, where each side's function is
///     the shock relation for <c>p &gt; p_K</c> and the rarefaction relation otherwise. Newton iteration from the
///     two-rarefaction guess converges in a handful of steps for every test here. Sampling then picks the region a
///     similarity coordinate <c>ξ = x / t</c> falls in.
/// </remarks>
internal sealed class ExactRiemannSolver
{
    private readonly double _gamma;
    private readonly double _rhoL;
    private readonly double _uL;
    private readonly double _pL;
    private readonly double _rhoR;
    private readonly double _uR;
    private readonly double _pR;
    private readonly double _cL;
    private readonly double _cR;

    internal ExactRiemannSolver(
        double rhoL, double uL, double pL,
        double rhoR, double uR, double pR,
        double gamma)
    {
        _gamma = gamma;
        _rhoL = rhoL;
        _uL = uL;
        _pL = pL;
        _rhoR = rhoR;
        _uR = uR;
        _pR = pR;
        _cL = Math.Sqrt(gamma * pL / rhoL);
        _cR = Math.Sqrt(gamma * pR / rhoR);

        double p = TwoRarefactionGuess();
        for (int iteration = 0; iteration < 50; iteration++)
        {
            double f = SideFunction(p, _rhoL, _pL, _cL, out double dfL) +
                       SideFunction(p, _rhoR, _pR, _cR, out double dfR) +
                       (_uR - _uL);

            double next = p - f / (dfL + dfR);
            if (next < 0d)
                next = 1e-6 * p;

            bool converged = Math.Abs(next - p) < 1e-12 * p;
            p = next;
            if (converged)
                break;
        }

        StarPressure = p;
        StarVelocity = 0.5d * (_uL + _uR) +
                       0.5d * (SideFunction(p, _rhoR, _pR, _cR, out _) - SideFunction(p, _rhoL, _pL, _cL, out _));

        StarDensityLeft = StarDensity(p, _rhoL, _pL);
        StarDensityRight = StarDensity(p, _rhoR, _pR);
    }

    internal double StarPressure { get; }
    internal double StarVelocity { get; }
    internal double StarDensityLeft { get; }
    internal double StarDensityRight { get; }

    /// <summary>
    ///     Speed of the left wave's head (the rarefaction head or the shock).
    /// </summary>
    internal double LeftHeadSpeed => StarPressure > _pL
        ? _uL - _cL * Math.Sqrt(ShockFactor(StarPressure / _pL))
        : _uL - _cL;

    /// <summary>
    ///     Speed of the left wave's tail, equal to the head for a shock.
    /// </summary>
    internal double LeftTailSpeed => StarPressure > _pL
        ? LeftHeadSpeed
        : StarVelocity - _cL * Math.Pow(StarPressure / _pL, (_gamma - 1d) / (2d * _gamma));

    internal double RightTailSpeed => StarPressure > _pR
        ? RightHeadSpeed
        : StarVelocity + _cR * Math.Pow(StarPressure / _pR, (_gamma - 1d) / (2d * _gamma));

    internal double RightHeadSpeed => StarPressure > _pR
        ? _uR + _cR * Math.Sqrt(ShockFactor(StarPressure / _pR))
        : _uR + _cR;

    /// <summary>
    ///     State at similarity coordinate <paramref name="xi" /> = <c>(x − x₀) / t</c>.
    /// </summary>
    internal (double Density, double Velocity, double Pressure) Sample(double xi)
    {
        if (xi <= StarVelocity)
        {
            if (StarPressure > _pL)
            {
                return xi <= LeftHeadSpeed
                    ? (_rhoL, _uL, _pL)
                    : (StarDensityLeft, StarVelocity, StarPressure);
            }

            if (xi <= LeftHeadSpeed)
                return (_rhoL, _uL, _pL);

            if (xi >= LeftTailSpeed)
                return (StarDensityLeft, StarVelocity, StarPressure);

            return InsideFan(xi, _rhoL, _uL, _pL, _cL, -1d);
        }

        if (StarPressure > _pR)
        {
            return xi >= RightHeadSpeed
                ? (_rhoR, _uR, _pR)
                : (StarDensityRight, StarVelocity, StarPressure);
        }

        if (xi >= RightHeadSpeed)
            return (_rhoR, _uR, _pR);

        if (xi <= RightTailSpeed)
            return (StarDensityRight, StarVelocity, StarPressure);

        return InsideFan(xi, _rhoR, _uR, _pR, _cR, 1d);
    }

    private (double, double, double) InsideFan(double xi, double rho, double u, double p, double c, double sign)
    {
        // sign = −1 for the left fan, +1 for the right one.
        double gm1 = _gamma - 1d;
        double gp1 = _gamma + 1d;
        double factor = 2d / gp1 - sign * gm1 / (gp1 * c) * (u - xi);
        double density = rho * Math.Pow(factor, 2d / gm1);
        double velocity = 2d / gp1 * (-sign * c + gm1 / 2d * u + xi);
        double pressure = p * Math.Pow(factor, 2d * _gamma / gm1);
        return (density, velocity, pressure);
    }

    private double TwoRarefactionGuess()
    {
        double exponent = (_gamma - 1d) / (2d * _gamma);
        double numerator = _cL + _cR - 0.5d * (_gamma - 1d) * (_uR - _uL);
        double denominator = _cL / Math.Pow(_pL, exponent) + _cR / Math.Pow(_pR, exponent);
        double guess = Math.Pow(numerator / denominator, 1d / exponent);
        return Math.Max(guess, 1e-6 * Math.Min(_pL, _pR));
    }

    private double SideFunction(double p, double rho, double pK, double c, out double derivative)
    {
        if (p > pK)
        {
            double a = 2d / ((_gamma + 1d) * rho);
            double b = (_gamma - 1d) / (_gamma + 1d) * pK;
            double root = Math.Sqrt(a / (p + b));
            derivative = root * (1d - 0.5d * (p - pK) / (p + b));
            return (p - pK) * root;
        }

        double ratio = p / pK;
        double exponent = (_gamma - 1d) / (2d * _gamma);
        derivative = Math.Pow(ratio, -(_gamma + 1d) / (2d * _gamma)) / (rho * c);
        return 2d * c / (_gamma - 1d) * (Math.Pow(ratio, exponent) - 1d);
    }

    private double StarDensity(double p, double rho, double pK)
    {
        double ratio = p / pK;
        if (p > pK)
        {
            double g = (_gamma - 1d) / (_gamma + 1d);
            return rho * (ratio + g) / (g * ratio + 1d);
        }

        return rho * Math.Pow(ratio, 1d / _gamma);
    }

    private double ShockFactor(double ratio)
    {
        return (_gamma + 1d) / (2d * _gamma) * ratio + (_gamma - 1d) / (2d * _gamma);
    }
}
