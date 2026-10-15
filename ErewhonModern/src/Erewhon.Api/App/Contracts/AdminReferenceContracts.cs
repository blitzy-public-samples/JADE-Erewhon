using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// Body of <c>PUT /api/app/admin/company</c>: the fields of <c>EditCompanyForm</c> that <c>populateTAFromForm</c>
/// copies into the company and its address.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the company name, passed to the service as received.</param>
/// <param name="Address">The address text boxes of the form; <see langword="null"/> when the body carries no address.</param>
public sealed record CompanyRequest(string? Name, AddressFields? Address);

/// <summary>
/// Body of <c>POST /api/app/admin/commission-rates</c> and <c>PUT /api/app/admin/commission-rates/{id}</c>: the fields
/// of <c>EditCommissionRateForm</c> that <c>populateTAFromForm</c> copies into the commission rate, with the agents the
/// rate is to hold.
/// </summary>
/// <param name="ItemCategoryId">
/// The row id of the item category selected in <c>cmbItemCat</c>; <see langword="null"/> when no category is selected.
/// </param>
/// <param name="Percentage">The decimal value of <c>txtPercentage</c>, passed to the service as received.</param>
/// <param name="AgentIds">
/// The row ids of the agents to hold the rate, the <c>CommissionRateTA.allAgents</c> collection;
/// <see langword="null"/> when no agents are chosen.
/// </param>
public sealed record CommissionRateRequest(long? ItemCategoryId, decimal Percentage, IReadOnlyList<long>? AgentIds);

/// <summary>
/// One entry of <c>CommissionRatesListForm.lstRates</c>: a commission rate of the item category selected in
/// <c>cmbCategory</c>, shown as the category name and the percentage (<c>CommissionRate.display</c>), with the agents
/// using the rate.
/// </summary>
/// <param name="Id">The row id of the commission rate.</param>
/// <param name="ItemCategoryId">The row id of the item category the rate belongs to.</param>
/// <param name="ItemCategoryName">The name of the item category the rate belongs to.</param>
/// <param name="Percentage">The commission percentage of the rate.</param>
/// <param name="Agents">The agents using the rate, in agent name order (<c>CommissionRate.allAgents</c>).</param>
public sealed record CommissionRateRow(
    long Id,
    long ItemCategoryId,
    string ItemCategoryName,
    decimal Percentage,
    IReadOnlyList<NameProjection> Agents);

/// <summary>
/// Body of <c>POST /api/app/admin/item-categories</c> and <c>PUT /api/app/admin/item-categories/{id}</c>: the fields
/// of <c>EditItemCategoryForm</c> that <c>populateTAFromForm</c> copies into the item category. Each value is passed to
/// the service as received.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the category name.</param>
/// <param name="Prefix">The text of <c>txtPrefix</c>, the category code prefix.</param>
/// <param name="Description">The text of <c>txtDescription</c>, the category description.</param>
public sealed record ItemCategoryRequest(string? Name, string? Prefix, string? Description);

/// <summary>
/// One entry of <c>ItemCategoryListForm.lstCategories</c>, in category name order
/// (<c>Company.allItemCategoriesByName</c>), with the values <c>EditItemCategoryForm.displayObject</c> shows.
/// </summary>
/// <param name="Id">The row id of the item category.</param>
/// <param name="Name">The category name, the text of the list entry and of <c>txtName</c>.</param>
/// <param name="Prefix">The category code prefix, the text of <c>txtPrefix</c>.</param>
/// <param name="Description">The category description, the text of <c>txtDescription</c>.</param>
public sealed record ItemCategoryRow(long Id, string Name, string Prefix, string Description);

/// <summary>
/// Body of <c>POST /api/app/admin/countries</c> and <c>PUT /api/app/admin/countries/{id}</c>: the field of
/// <c>EditCountryForm</c> that <c>populateTAFromForm</c> copies into the country.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the country name, passed to the service as received.</param>
public sealed record CountryRequest(string? Name);

/// <summary>
/// One entry of <c>RegionsListForm.lstRegions</c>, listed for the country selected in <c>cmbCountries</c> in region
/// name order (<c>Country.allRegions</c>), with the values <c>EditRegionForm.displayObject</c> shows.
/// </summary>
/// <param name="Id">The row id of the region.</param>
/// <param name="Name">The region name, the text of the list entry and of <c>txtName</c>.</param>
/// <param name="CountryId">The row id of the country the region belongs to.</param>
/// <param name="CountryName">The name of the country the region belongs to, the entry of <c>cmbCountries</c>.</param>
public sealed record RegionRow(long Id, string Name, long CountryId, string CountryName);

/// <summary>
/// One entry of <c>CountriesListForm.lstCountries</c>, in country name order (<c>Company.allCountries</c>), with the
/// entries <c>lstCountries_click</c> puts in <c>lstCountryRegions</c>.
/// </summary>
/// <param name="Id">The row id of the country.</param>
/// <param name="Name">The country name, the text of the list entry and of <c>EditCountryForm.txtName</c>.</param>
/// <param name="Regions">
/// The regions of the country in region name order (<c>Country.allRegions</c>), the entries of
/// <c>lstCountryRegions</c>.
/// </param>
public sealed record CountryRow(long Id, string Name, IReadOnlyList<RegionRow> Regions);

/// <summary>
/// Body of <c>POST /api/app/admin/regions</c> and <c>PUT /api/app/admin/regions/{id}</c>: the fields of
/// <c>EditRegionForm</c> that <c>populateTAFromForm</c> copies into the region.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the region name, passed to the service as received.</param>
/// <param name="CountryId">
/// The row id of the country selected in <c>cmbCountry</c>; <see langword="null"/> when no country is selected.
/// </param>
public sealed record RegionRequest(string? Name, long? CountryId);
