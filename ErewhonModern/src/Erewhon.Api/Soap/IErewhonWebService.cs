using CoreWCF;
using Erewhon.Api.Soap.Contracts;

namespace Erewhon.Api.Soap;

/// <summary>
/// SOAP contract of the <c>ErewhonWebService</c> service: agent and client lookups, agent and client name
/// lists, a client's purchased photo, and address updates of an existing agent or client.
/// </summary>
[ServiceContract(Namespace = "urn:erewhon:soap", Name = "ErewhonWebService")]
[XmlSerializerFormat(Style = OperationFormatStyle.Document, Use = OperationFormatUse.Literal)]
public interface IErewhonWebService
{
    /// <summary>
    /// Returns the agent with exactly this name, or null when none exists.
    /// </summary>
    /// <param name="pName">Name of the agent, matched as given.</param>
    /// <returns>A <see cref="DTOAgent"/> with the agent's GUID, name and seven address fields, or null.</returns>
    [OperationContract(Name = "getAgent")]
    Task<DTOAgent?> getAgent(string? pName);

    /// <summary>
    /// Returns the names of all agents of the company.
    /// </summary>
    /// <returns>One <see cref="DTOName"/> per agent, in name order.</returns>
    [OperationContract(Name = "getAgentNames")]
    Task<DTOName[]> getAgentNames();

    /// <summary>
    /// Returns the client whose name equals this name with leading and trailing blanks removed, or null when none
    /// exists.
    /// </summary>
    /// <param name="pName">Name of the client; leading and trailing blanks are removed before the lookup.</param>
    /// <returns>A <see cref="DTOClient"/> with the client's GUID, name and seven address fields, or null.</returns>
    [OperationContract(Name = "getClient")]
    Task<DTOClient?> getClient(string? pName);

    /// <summary>
    /// Returns the names of all clients of the company.
    /// </summary>
    /// <returns>One <see cref="DTOName"/> per client, in name order.</returns>
    [OperationContract(Name = "getClientNames")]
    Task<DTOName[]> getClientNames();

    /// <summary>
    /// Returns a photo from the retail items the client with exactly this name has bought.
    /// </summary>
    /// <param name="pName">Name of the client, matched as given.</param>
    /// <returns>
    /// Null when no client has this name; otherwise a <see cref="DTOPhoto"/> holding the photo of the last item, in
    /// the client's retail-sale order, that has a photo, or holding no photo when none of them has one.
    /// </returns>
    [OperationContract(Name = "getPhoto")]
    Task<DTOPhoto?> getPhoto(string? pName);

    /// <summary>
    /// Replaces the seven address fields of the agent with exactly this name.
    /// </summary>
    /// <param name="pName">Name of the agent to update, matched as given.</param>
    /// <param name="pStreet">New street address.</param>
    /// <param name="pCity">New city.</param>
    /// <param name="pCountry">New country.</param>
    /// <param name="pPhone">New phone number.</param>
    /// <param name="pFax">New fax number.</param>
    /// <param name="pEmail">New e-mail address.</param>
    /// <param name="pWebsite">New web site address.</param>
    /// <returns>
    /// A <see cref="DTOResult"/> with status 404 and one error when no agent has this name, status 400 and the
    /// validation errors when the update is rejected, or status 200 and the agent's GUID when it succeeds.
    /// </returns>
    [OperationContract(Name = "updateAgent")]
    Task<DTOResult> updateAgent(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite);

    /// <summary>
    /// Replaces the seven address fields of the agent named in the given agent data with the address fields it
    /// carries.
    /// </summary>
    /// <param name="pDTOAgent">Agent data whose name selects the agent and whose address fields are written.</param>
    /// <returns>
    /// A <see cref="DTOResult"/> with status 404 and one error when no agent has that name, status 400 and the
    /// validation errors when the update is rejected, or status 200 and the agent's GUID when it succeeds.
    /// </returns>
    [OperationContract(Name = "updateAgentWithDTO")]
    Task<DTOResult> updateAgentWithDTO(DTOAgent? pDTOAgent);

    /// <summary>
    /// Replaces the seven address fields of the client with exactly this name.
    /// </summary>
    /// <param name="pName">Name of the client to update, matched as given.</param>
    /// <param name="pStreet">New street address.</param>
    /// <param name="pCity">New city.</param>
    /// <param name="pCountry">New country.</param>
    /// <param name="pPhone">New phone number.</param>
    /// <param name="pFax">New fax number.</param>
    /// <param name="pEmail">New e-mail address.</param>
    /// <param name="pWebsite">New web site address.</param>
    /// <returns>
    /// A <see cref="DTOResult"/> with status 404 and one error when no client has this name, status 400 and the
    /// validation errors when the update is rejected, or status 200 and the client's GUID when it succeeds.
    /// </returns>
    [OperationContract(Name = "updateClient")]
    Task<DTOResult> updateClient(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite);

    /// <summary>
    /// Replaces the seven address fields of the client named in the given client data with the address fields it
    /// carries.
    /// </summary>
    /// <param name="pDTOClient">Client data whose name selects the client and whose address fields are written.</param>
    /// <returns>
    /// A <see cref="DTOResult"/> with status 404 and one error when no client has that name, status 400 and the
    /// validation errors when the update is rejected, or status 200 and the client's GUID when it succeeds.
    /// </returns>
    [OperationContract(Name = "updateClientWithDTO")]
    Task<DTOResult> updateClientWithDTO(DTOClient? pDTOClient);
}
