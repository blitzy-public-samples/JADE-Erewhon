using Erewhon.Application.Common;
using Erewhon.Application.Domain;

namespace Erewhon.Application.Abstractions;

/// <summary>
/// The company bound to the current scope. After <see cref="Bind(long)"/> the bound company is the
/// company with that id; otherwise it is the company with the lowest <c>companies.id</c>.
/// When the bound company does not exist, <see cref="Current"/>, <see cref="CurrentId"/> and
/// <see cref="GetCurrentAsync(CancellationToken)"/> throw <see cref="NoCompanyException"/>, and
/// <see cref="FindCurrentAsync(CancellationToken)"/> returns <see langword="null"/>.
/// </summary>
public interface ICompanyContext
{
    /// <summary>
    /// The bound company.
    /// </summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    Company Current { get; }

    /// <summary>
    /// The internal id (<c>companies.id</c>) of the bound company.
    /// </summary>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    long CurrentId { get; }

    /// <summary>
    /// Returns the bound company, as <see cref="Current"/> does.
    /// </summary>
    /// <param name="ct">Cancels the company read.</param>
    /// <returns>The bound company.</returns>
    /// <exception cref="NoCompanyException">The bound company does not exist.</exception>
    Task<Company> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the bound company, or <see langword="null"/> when it does not exist.
    /// </summary>
    /// <param name="ct">Cancels the company read.</param>
    /// <returns>The bound company, or <see langword="null"/> when it does not exist.</returns>
    Task<Company?> FindCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Binds the company with internal id <paramref name="companyId"/> to the current scope.
    /// </summary>
    /// <param name="companyId">The internal id (<c>companies.id</c>) of the company to bind.</param>
    void Bind(long companyId);
}
