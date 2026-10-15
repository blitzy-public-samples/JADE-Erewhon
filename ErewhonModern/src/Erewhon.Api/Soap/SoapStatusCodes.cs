namespace Erewhon.Api.Soap;

/// <summary>
/// Port of the <c>ErewhonWebService</c> status constants, shared by the <c>ErewhonWebService</c> and
/// <c>ErewhonWebServiceAdmin</c> SOAP services. Each value is written to <c>DTOResult.statusCode</c>;
/// none is an HTTP response status.
/// </summary>
public static class SoapStatusCodes
{
    /// <summary>Status code 400 placed in <c>DTOResult.statusCode</c> when a create or update of an agent or client is rejected.</summary>
    public const int ErrorBadRequest = 400;

    /// <summary>Status code 409 placed in <c>DTOResult.statusCode</c> when the named agent or client already exists.</summary>
    public const int ErrorConflict = 409;

    /// <summary>Status code 403 for a forbidden request. No operation returns it.</summary>
    public const int ErrorForbidden = 403;

    /// <summary>Status code 404 placed in <c>DTOResult.statusCode</c> when the named agent or client is not found.</summary>
    public const int ErrorNotFound = 404;

    /// <summary>Status code 500 placed in <c>DTOResult.statusCode</c> when a delete of an agent or client is rejected.</summary>
    public const int ErrorServerError = 500;

    /// <summary>Status code 403 for an unauthorised request. No operation returns it.</summary>
    public const int ErrorUnauthorized = 403;

    /// <summary>Status code 201 placed in <c>DTOResult.statusCode</c> when an agent or client is created.</summary>
    public const int StatusCreated = 201;

    /// <summary>Status code 200 placed in <c>DTOResult.statusCode</c> when an update or delete of an agent or client succeeds.</summary>
    public const int StatusSuccess = 200;
}
