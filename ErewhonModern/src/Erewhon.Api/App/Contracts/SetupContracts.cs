namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Result of <c>POST /api/app/setup/load-data</c>, the interactive data load that replaces the
/// logon-time load prompt offered when no company exists.
/// </summary>
/// <param name="Loaded">
/// <see langword="true"/> when the loader ran and committed the data from the configured data
/// directory; <see langword="false"/> when the request was refused with a company already present.
/// </param>
/// <param name="TenderSalesBuilt">
/// Number of tender sales created by the closure of tenders at the current date that follows the load;
/// 0 when the request was refused.
/// </param>
/// <param name="OutputLines">
/// The loader's console lines in the order written: the loading and loaded-object lines, the per-file
/// loaded lines, then the tender-sales-built or no-tenders-to-close line; empty when the request was refused.
/// </param>
public sealed record LoadDataResponse(bool Loaded, int TenderSalesBuilt, IReadOnlyList<string> OutputLines);
