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
    /// The largest magnitude a money value holds: <see cref="Precision"/> digits, <see cref="Scale"/> of them after
    /// the decimal point, all nines.
    /// </summary>
    public const decimal MaxMoney = 9_999_999_999.99m;

    /// <summary>
    /// Rounds to 2 decimal places, ties away from zero.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The value rounded to <see cref="Scale"/> decimal places.</returns>
    public static decimal Round2(decimal value) =>
        Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Rounds a money value to 2 decimal places, ties away from zero, and checks that the rounded value has a
    /// magnitude of at most <see cref="MaxMoney"/>.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The value rounded to <see cref="Scale"/> decimal places.</returns>
    /// <exception cref="DecimalOverflowException">
    /// The magnitude of the rounded value is greater than <see cref="MaxMoney"/>.
    /// </exception>
    public static decimal RoundMoney(decimal value)
    {
        decimal rounded = Round2(value);
        if (Math.Abs(rounded) > MaxMoney)
        {
            throw new DecimalOverflowException();
        }

        return rounded;
    }

    /// <summary>
    /// Rounds to 2 decimal places, ties away from zero.
    /// </summary>
    /// <param name="value">The value to round.</param>
    /// <returns>The value rounded to <see cref="Scale"/> decimal places.</returns>
    public static decimal Round2AwayFromZero(decimal value) => Round2(value);
}
