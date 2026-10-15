using CoreWCF;
using Erewhon.Api.Soap.Contracts;

namespace Erewhon.Api.Soap;

/// <summary>
/// SOAP contract of the <c>ErewhonWebServiceAdmin</c> service: creates and deletes agents and clients by name.
/// Every operation returns a <see cref="DTOResult"/> whose <c>statusCode</c> reports the outcome.
/// </summary>
[ServiceContract(Namespace = "urn:erewhon:soap", Name = "ErewhonWebServiceAdmin")]
[XmlSerializerFormat(Style = OperationFormatStyle.Document, Use = OperationFormatUse.Literal)]
public interface IErewhonWebServiceAdmin
{
    /// <summary>Creates an agent with the given name and address.</summary>
    /// <param name="pName">Name of the agent to create.</param>
    /// <param name="pStreet">Street address of the agent.</param>
    /// <param name="pCity">City of the agent.</param>
    /// <param name="pCountry">Country of the agent.</param>
    /// <param name="pPhone">Phone number of the agent.</param>
    /// <param name="pFax">Fax number of the agent.</param>
    /// <param name="pEmail">E-mail address of the agent.</param>
    /// <param name="pWebsite">Web site address of the agent.</param>
    /// <returns>
    /// Status 409 and one error when an agent with exactly this name already exists; status 400 and the
    /// validation errors when the agent is not created; status 201 and the new agent's GUID on success.
    /// </returns>
    [OperationContract(Name = "createAgent")]
    Task<DTOResult> createAgent(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite);

    /// <summary>Creates a client with the given name and address.</summary>
    /// <param name="pName">Name of the client to create.</param>
    /// <param name="pStreet">Street address of the client.</param>
    /// <param name="pCity">City of the client.</param>
    /// <param name="pCountry">Country of the client.</param>
    /// <param name="pPhone">Phone number of the client.</param>
    /// <param name="pFax">Fax number of the client.</param>
    /// <param name="pEmail">E-mail address of the client.</param>
    /// <param name="pWebsite">Web site address of the client.</param>
    /// <returns>
    /// Status 409 and one error when a client with exactly this name already exists; status 400 and the
    /// validation errors when the client is not created; status 201 and the new client's GUID on success.
    /// </returns>
    [OperationContract(Name = "createClient")]
    Task<DTOResult> createClient(
        string? pName,
        string? pStreet,
        string? pCity,
        string? pCountry,
        string? pPhone,
        string? pFax,
        string? pEmail,
        string? pWebsite);

    /// <summary>Deletes the agent with exactly the given name.</summary>
    /// <param name="pName">Name of the agent to delete.</param>
    /// <returns>
    /// Status 404 and one error when no agent has this name; status 500 and one "could not delete" error when
    /// the delete fails; status 200 with no GUID and no errors on success.
    /// </returns>
    [OperationContract(Name = "deleteAgent")]
    Task<DTOResult> deleteAgent(string? pName);

    /// <summary>Deletes the client with exactly the given name.</summary>
    /// <param name="pName">Name of the client to delete.</param>
    /// <returns>
    /// Status 404 and one error when no client has this name; status 500 and one "could not delete" error when
    /// the delete fails; status 200 with no GUID and no errors on success.
    /// </returns>
    [OperationContract(Name = "deleteClient")]
    Task<DTOResult> deleteClient(string? pName);
}
