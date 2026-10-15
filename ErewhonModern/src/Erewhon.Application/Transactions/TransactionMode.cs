namespace Erewhon.Application.Transactions;

/// <summary>
/// Selects how a transaction agent obtains its transaction; ports the JADE <c>TransactionImplementor</c> strategies.
/// </summary>
public enum TransactionMode
{
    /// <summary>
    /// The agent begins, commits or rolls back its own transaction and publishes queued notifications after commit.
    /// Ports <c>CommitTransactionTI</c> (<c>ModelTA.persistEntity</c>).
    /// </summary>
    Own,

    /// <summary>
    /// The agent runs inside the caller's transaction under a savepoint and never commits; with no active transaction
    /// the first write raises <c>NotInTransactionStateException</c>.
    /// Ports <c>NoTransactionTI</c> (<c>ModelTA.persistEntityInTransState</c>).
    /// </summary>
    Ambient,
}
