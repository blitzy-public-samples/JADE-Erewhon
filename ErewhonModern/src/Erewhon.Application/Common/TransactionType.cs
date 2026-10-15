namespace Erewhon.Application.Common;

/// <summary>Kind of persistence work a transaction agent is asked to carry out.</summary>
public enum TransactionType
{
    /// <summary>Delete request that removes the entity.</summary>
    Delete = -1,

    /// <summary>Persist request that creates the entity when none exists, otherwise updates it.</summary>
    Persist = 0,

    /// <summary>Base value of the modify requests.</summary>
    ModifyBaseModification = 1000,

    /// <summary>Modify request that sets the entity's soft lock.</summary>
    ModifyLockEntity = 1001,

    /// <summary>Modify request that clears the entity's soft lock.</summary>
    ModifyUnlockEntity = 1002,

    /// <summary>Modify request that adds a commission rate to the agent.</summary>
    ModifyAddCommissionRateForAgent = 1004,

    /// <summary>Modify request that removes a commission rate from the agent.</summary>
    ModifyRemoveCommissionRateForAgent = 1005,
}
