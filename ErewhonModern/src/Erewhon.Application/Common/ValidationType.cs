namespace Erewhon.Application.Common;

/// <summary>Selects which set of validation rules a transaction agent applies to its entity.</summary>
public enum ValidationType
{
    /// <summary>Validation for creating a new entity.</summary>
    Create = 1,

    /// <summary>Validation for updating an existing entity.</summary>
    Update = 2,

    /// <summary>Validation for deleting an existing entity.</summary>
    Delete = 3,

    /// <summary>Validation for applying a modification sub-operation to an existing entity.</summary>
    Modify = 4,
}
