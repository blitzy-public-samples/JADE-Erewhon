namespace Erewhon.Application.Common;

/// <summary>
/// Precision, scale and rounding of money and percentage values.
/// </summary>
public static class DecimalRules
{
    /// <summary>
    /// Number of decimal places held by money and percentage values.
    /// </summary>
    public const int Scale = 2;

    /// <summary>
    /// Total number of digits held by a money value.
    /// </summary>
    public const int Precision = 12;

    /// <summary>
    /// Total number of digits held by a percentage value.
    /// </summary>
    public const int PercentPrecision = 5;

    /// <summary>
    /// Rounds to 2 decimal places, ties away from zero.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The value rounded to <see cref="Scale"/> decimal places.</returns>
    public static decimal Round2(decimal value) =>
        Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Rounds to 2 decimal places, ties away from zero.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The value rounded to <see cref="Scale"/> decimal places.</returns>
    public static decimal Round2AwayFromZero(decimal value) => Round2(value);
}
