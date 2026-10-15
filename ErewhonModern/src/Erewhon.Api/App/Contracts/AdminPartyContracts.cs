using Erewhon.Application.Projections;

namespace Erewhon.Api.App.Contracts;

/// <summary>
/// The seven address text boxes of <c>EditAddressableEntityForm</c>, shared by the agent, client and company edit
/// dialogs. Each value is passed to the service as received.
/// </summary>
/// <param name="Street">The text of <c>txtStreet</c>.</param>
/// <param name="City">The text of <c>txtCity</c>.</param>
/// <param name="Country">The text of <c>txtCountry</c>.</param>
/// <param name="Phone">The text of <c>txtPhone</c>.</param>
/// <param name="Fax">The text of <c>txtFax</c>.</param>
/// <param name="Email">The text of <c>txtEmail</c>.</param>
/// <param name="WebSite">The text of <c>txtWebSite</c>.</param>
public sealed record AddressFields(
    string? Street,
    string? City,
    string? Country,
    string? Phone,
    string? Fax,
    string? Email,
    string? WebSite);

/// <summary>
/// Body of <c>POST /api/app/admin/agents</c> and <c>PUT /api/app/admin/agents/{id}</c>: the fields of
/// <c>EditAgentForm</c> (also opened by Agents › My Details) that <c>populateTAFromForm</c> copies into the agent and
/// its address.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the agent name, passed to the service as received.</param>
/// <param name="Address">The address text boxes of the form; <see langword="null"/> when the body carries no address.</param>
public sealed record AgentRequest(string? Name, AddressFields? Address);

/// <summary>
/// One entry of <c>AgentsListForm.lstCommissnRate</c>: a commission rate the agent holds, shown as the percentage
/// followed by the item category name. The agent's rates are listed in category name order
/// (<c>Agent.allCommissionRates</c>).
/// </summary>
/// <param name="CommissionRateId">The row id of the commission rate.</param>
/// <param name="ItemCategoryId">The row id of the item category the rate belongs to.</param>
/// <param name="ItemCategoryName">The name of the item category the rate belongs to.</param>
/// <param name="Percentage">The commission percentage of the rate.</param>
public sealed record AgentRateRow(
    long CommissionRateId,
    long ItemCategoryId,
    string ItemCategoryName,
    decimal Percentage);

/// <summary>
/// Response of <c>GET /api/app/admin/agents/{id}</c>: the agent selected in <c>AgentsListForm.lstAgents</c> with the
/// contents of <c>lstItems</c> and <c>lstCommissnRate</c>, and the values <c>EditAgentForm.displayObject</c> shows.
/// </summary>
/// <param name="Agent">The agent with its name and the seven fields of its address.</param>
/// <param name="Items">
/// The agent's items in <c>Agent.allItems</c> order (category, prefix, code number), the entries of <c>lstItems</c>;
/// each with a <see langword="null"/> <see cref="ItemProjection.Photo"/>.
/// </param>
/// <param name="CommissionRates">The agent's commission rates in category name order, the entries of <c>lstCommissnRate</c>.</param>
public sealed record AgentDetailsResponse(
    AgentProjection Agent,
    IReadOnlyList<ItemProjection> Items,
    IReadOnlyList<AgentRateRow> CommissionRates);

/// <summary>
/// One entry of <c>AgentCommissionRatesForm.lstRates</c>: a commission rate of the selected item category with the
/// agents that use it, the entries <c>loadAgents</c> puts in <c>lstAgentsUsing</c>.
/// </summary>
/// <param name="CommissionRateId">The row id of the commission rate.</param>
/// <param name="Percentage">The commission percentage of the rate.</param>
/// <param name="Agents">The agents using the rate, in agent name order (<c>CommissionRate.allAgents</c>).</param>
public sealed record CommissionRateAgents(
    long CommissionRateId,
    decimal Percentage,
    IReadOnlyList<NameProjection> Agents);

/// <summary>
/// Response of <c>GET /api/app/admin/agent-commission-rates?category={id}</c>: the data of
/// <c>AgentCommissionRatesForm</c> once an item category is chosen in <c>cmbCategory</c>.
/// </summary>
/// <param name="ItemCategoryId">The row id of the item category chosen in <c>cmbCategory</c>.</param>
/// <param name="Rates">
/// The category's commission rates in percentage order (<c>ItemCategory.allCommissionRates</c>), the entries of
/// <c>lstRates</c>, each with the agents using it.
/// </param>
/// <param name="AllAgents">
/// Every agent of the company in name order (<c>Company.allAgents</c>); the agents of a rate that are not in its
/// <see cref="CommissionRateAgents.Agents"/> are the entries of <c>lstAgentsNotUsing</c>.
/// </param>
public sealed record AgentCommissionRatesResponse(
    long ItemCategoryId,
    IReadOnlyList<CommissionRateAgents> Rates,
    IReadOnlyList<NameProjection> AllAgents);

/// <summary>
/// Body of <c>PUT /api/app/admin/agent-commission-rates</c>: the save of <c>AgentCommissionRatesForm.btnSave</c>,
/// applied through <c>CommissionRate.addOrRemoveAgents</c>.
/// </summary>
/// <param name="CommissionRateId">The row id of the commission rate selected in <c>lstRates</c>.</param>
/// <param name="AgentIds">
/// The row ids of every agent in <c>lstAgentsUsing</c> after the Add, Remove, Add All and Remove All moves: the
/// complete set of agents the rate is to have.
/// </param>
public sealed record ReplaceRateAgentsRequest(long CommissionRateId, IReadOnlyList<long> AgentIds);

/// <summary>
/// Body of <c>POST /api/app/admin/clients</c> and <c>PUT /api/app/admin/clients/{id}</c>: the fields of
/// <c>EditClientForm</c> that <c>populateTAFromForm</c> copies into the client and its address.
/// </summary>
/// <param name="Name">The text of <c>txtName</c>, the client name, passed to the service as received.</param>
/// <param name="Address">The address text boxes of the form; <see langword="null"/> when the body carries no address.</param>
public sealed record ClientRequest(string? Name, AddressFields? Address);

/// <summary>
/// Response of <c>GET /api/app/admin/clients/{id}</c>: the client selected in <c>ClientsListForm.lstClients</c> with
/// the contents of <c>lstRetailSales</c> and <c>lstTenderSales</c>, and the values <c>EditClientForm.displayObject</c>
/// shows.
/// </summary>
/// <param name="Client">The client with its name and the seven fields of its address.</param>
/// <param name="RetailSales">
/// The client's retail sales in <c>Client.allRetailSales</c> order (date sold, prefix, code number), the entries of
/// <c>lstRetailSales</c>.
/// </param>
/// <param name="TenderSales">
/// The client's tender sales in <c>Client.allTenderSales</c> order (tender time, prefix, code number), the entries of
/// <c>lstTenderSales</c>.
/// </param>
public sealed record ClientDetailsResponse(
    ClientProjection Client,
    IReadOnlyList<SalesRowProjection> RetailSales,
    IReadOnlyList<SalesRowProjection> TenderSales);
