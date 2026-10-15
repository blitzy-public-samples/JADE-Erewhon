using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Erewhon.Application.Transactions;

/// <summary>
/// The values one transaction-agent operation persists for an entity: the model object it works on and the
/// attributes common to every agent. Ports the common attributes of JADE <c>ModelTA</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type the agent persists.</typeparam>
/// <remarks>Instances are not thread-safe.</remarks>
public abstract class TransactionCommand<TEntity>
    where TEntity : class
{
    /// <summary>
    /// The entity the operation works on: <see langword="null"/> before a create, the new entity once the create
    /// path has made it, and <see langword="null"/> again when an aborted operation leaves it unpersisted.
    /// Ports <c>ModelTA.myModelObject</c> and <c>getModelObject</c>.
    /// </summary>
    public TEntity? ModelObject { get; set; }

    /// <summary>
    /// The GUID text given to a new entity, at most 39 characters; when <see langword="null"/> or empty a new GUID is
    /// generated. Ports <c>ModelTA.guid</c>.
    /// </summary>
    public string? Guid { get; set; }

    /// <summary>
    /// The session id written to the entity's soft edit lock by a lock-entity modify (1001), at most 64 characters.
    /// Ports <c>ModelTA.lockedByProcessId</c>.
    /// </summary>
    public string? LockedBySession { get; set; }

    /// <summary>
    /// The time written to the entity's soft edit lock by a lock-entity modify (1001).
    /// Ports <c>ModelTA.lockedTimeStamp</c>.
    /// </summary>
    public DateTime? LockedTimestamp { get; set; }

    /// <summary>
    /// The edition the caller read; passed to <see cref="TransactionAgent{TEntity}"/>'s edition check on update.
    /// Ports <c>ModelTA.expectedEdition</c>.
    /// </summary>
    public int ExpectedEdition { get; set; }

    /// <summary>
    /// The modify sub-code of the running operation: set to the sub-code at the start of a modify and to 0 at the
    /// start of an update. Ports <c>ModelTA.modificationCode</c>.
    /// </summary>
    public int ModificationCode { get; set; }

    /// <summary>
    /// Resets the common attributes: <see cref="ExpectedEdition"/> and <see cref="ModificationCode"/> to 0, and
    /// <see cref="LockedBySession"/>, <see cref="LockedTimestamp"/> and <see cref="ModelObject"/> to
    /// <see langword="null"/>. <see cref="Guid"/> is left as it is. Ports <c>ModelTA.initialize</c>; an override
    /// resets its own attributes and calls this method.
    /// </summary>
    public virtual void Initialize()
    {
        ExpectedEdition = 0;
        LockedBySession = null;
        LockedTimestamp = null;
        ModificationCode = 0;
        ModelObject = null;
    }

    /// <summary>
    /// Makes <paramref name="modelObject"/> the model object, then calls <see cref="PopulateSubordinateObjects"/>.
    /// Ports <c>ModelTA.populateFromObject</c>; an override calls this method first, then copies the entity's
    /// attributes into its own.
    /// </summary>
    /// <param name="modelObject">The entity the next operation works on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modelObject"/> is <see langword="null"/>.</exception>
    public virtual void PopulateFromObject(TEntity modelObject)
    {
        ArgumentNullException.ThrowIfNull(modelObject);
        ModelObject = modelObject;
        PopulateSubordinateObjects(modelObject);
    }

    /// <summary>
    /// Copies the values of the entity's subordinate objects into the subordinate commands; does nothing by default.
    /// Ports <c>ModelTA.populateSubordinateObjects</c>.
    /// </summary>
    /// <param name="modelObject">The entity being populated from.</param>
    protected virtual void PopulateSubordinateObjects(TEntity modelObject)
    {
    }
}

/// <summary>
/// The transaction-agent template: persists one entity, and its subordinate objects, through the steps of JADE
/// <c>ModelTA</c> in their order. Each operation starts a new <see cref="OperationResult"/>, checks field lengths,
/// pre-validates and validates outside any database transaction, then takes the locks, applies the change, saves and
/// commits. In <see cref="TransactionMode.Own"/> mode those last steps run in one transaction begun by the agent and
/// queued notifications are published after the commit; in <see cref="TransactionMode.Ambient"/> mode they run inside
/// the caller's transaction under the <c>ta_locks</c> savepoint, or raise
/// <see cref="NotInTransactionStateException"/> at the first write when no transaction is active. Any operation that
/// does not complete is rolled back and its tracked changes are detached.
/// Ports <c>ModelTA</c>, the <c>PersistentModel.onCreate</c>, <c>onUpdate</c>, <c>onDelete</c>, <c>onModify</c> and
/// <c>setCommonProperties</c> defaults, and the <c>CommitTransactionTI</c> and <c>NoTransactionTI</c> strategies.
/// </summary>
/// <typeparam name="TEntity">The entity type the agent persists.</typeparam>
/// <remarks>
/// An agent serves one dependency-injection scope and keeps no state between operations; a running operation may
/// run other agents for its subordinate objects. Instances are not safe to call from several threads at once.
/// </remarks>
public abstract class TransactionAgent<TEntity>
    where TEntity : class
{
    private const string AmbientSavepointName = "ta_locks";

    /// <summary>Creates the agent over the database, unit of work, locks, server clock and notification queue of its scope.</summary>
    /// <param name="db">The Erewhon database of the scope.</param>
    /// <param name="uow">The unit of work that begins, commits and rolls back transactions and savepoints.</param>
    /// <param name="locks">The advisory locks taken by <see cref="AcquireLocksAsync"/> overrides.</param>
    /// <param name="clock">The database server clock used by pre-validation defaults.</param>
    /// <param name="queue">The queue of change notifications raised by the scope's saves.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    protected TransactionAgent(
        IErewhonDbContext db,
        IUnitOfWork uow,
        IEntityLocks locks,
        IServerClock clock,
        NotificationQueue queue)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(queue);

        Db = db;
        Uow = uow;
        Locks = locks;
        Clock = clock;
        Queue = queue;
    }

    /// <summary>The Erewhon database of the agent's scope.</summary>
    protected IErewhonDbContext Db { get; }

    /// <summary>The unit of work that begins, commits and rolls back the agent's transactions and savepoints.</summary>
    protected IUnitOfWork Uow { get; }

    /// <summary>The advisory locks taken in <see cref="AcquireLocksAsync"/>; ports <c>ModelTA.tryLockingObject</c>.</summary>
    protected IEntityLocks Locks { get; }

    /// <summary>The database server clock, read by pre-validation defaults.</summary>
    protected IServerClock Clock { get; }

    /// <summary>The queue of change notifications, published after an Own-mode commit and discarded on rollback.</summary>
    protected NotificationQueue Queue { get; }

    /// <summary>
    /// Whether the agent persists only subordinate objects and has no model object of its own; <see langword="false"/>
    /// by default. Ports <c>ModelTA.hasOnlySubordinatePersistentObjects</c>.
    /// </summary>
    protected virtual bool HasOnlySubordinatePersistentObjects => false;

    /// <summary>
    /// Runs the operation that <paramref name="transactionType"/> selects:
    /// <see cref="TransactionType.Delete"/> deletes the model object, or succeeds without any work when there is none;
    /// <see cref="TransactionType.Persist"/> creates the entity when there is no model object and otherwise updates it;
    /// a value above 0 applies that modify sub-code; any value below -1 fails with no errors and no work.
    /// <see cref="TransactionMode.Own"/> ports <c>ModelTA.persistEntity</c> and <see cref="TransactionMode.Ambient"/>
    /// ports <c>ModelTA.persistEntityInTransState</c>.
    /// </summary>
    /// <param name="command">The values to persist.</param>
    /// <param name="transactionType">The kind of persistence work, or a modify sub-code.</param>
    /// <param name="mode">Whether the agent runs its own transaction or the caller's.</param>
    /// <param name="cancellationToken">Token that cancels the operation.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    public Task<OperationResult> ExecuteAsync(
        TransactionCommand<TEntity> command,
        TransactionType transactionType,
        TransactionMode mode = TransactionMode.Own,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (transactionType == TransactionType.Delete)
        {
            return command.ModelObject is null
                ? Task.FromResult(new OperationResult { Succeeded = true })
                : DeleteEntityAsync(command, mode, cancellationToken);
        }

        if (transactionType == TransactionType.Persist)
        {
            return command.ModelObject is null
                ? CreateEntityAsync(command, mode, cancellationToken)
                : UpdateEntityAsync(command, mode, cancellationToken);
        }

        if ((int)transactionType > (int)TransactionType.Persist)
        {
            return ModifyEntityAsync(command, (int)transactionType, mode, cancellationToken);
        }

        return Task.FromResult(new OperationResult { Succeeded = false });
    }

    /// <summary>
    /// Checks the command's field lengths with <see cref="CheckFieldLengths"/>, then runs
    /// <see cref="PreValidateCoreAsync"/>. Ports <c>ModelTA.doPreValidate</c> as a parent agent calls it on a
    /// subordinate agent.
    /// </summary>
    /// <param name="command">The values to pre-validate.</param>
    /// <param name="cancellationToken">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the command is pre-validated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    public async Task PreValidateAsync(TransactionCommand<TEntity> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        CheckFieldLengths(command);
        await PreValidateCoreAsync(command, cancellationToken);
    }

    /// <summary>
    /// Runs <see cref="ValidateCoreAsync"/>, which adds its errors to <paramref name="result"/>, and returns whether
    /// <paramref name="result"/> then holds no errors. Ports <c>ModelTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="cancellationToken">Token that cancels the validation.</param>
    /// <returns><see langword="true"/> when <paramref name="result"/> holds no errors.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="command"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    public async Task<bool> ValidateAsync(
        TransactionCommand<TEntity> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(result);
        await ValidateCoreAsync(command, validationType, result, cancellationToken);
        return result.HasNoErrors;
    }

    /// <summary>
    /// Checks every bounded string of the command, as given and before any trimming, against its declared length.
    /// The base checks <see cref="TransactionCommand{TEntity}.Guid"/> (39) and
    /// <see cref="TransactionCommand{TEntity}.LockedBySession"/> (64); an override checks its own strings and calls
    /// this method. Ports the JADE <c>String[n]</c> assignment check.
    /// </summary>
    /// <param name="command">The values to check.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="StringTooLongException">A value is longer than its declared field length.</exception>
    protected virtual void CheckFieldLengths(TransactionCommand<TEntity> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        FieldLengths.Check(command.Guid, FieldLengths.Guid, nameof(command.Guid));
        FieldLengths.Check(command.LockedBySession, FieldLengths.LockedBySession, nameof(command.LockedBySession));
    }

    /// <summary>
    /// Prepares the command's values before validation, for example by trimming, upper-casing, applying server-clock
    /// defaults or allocating codes; does nothing by default. Ports <c>ModelTA.doPreValidate</c>.
    /// </summary>
    /// <param name="command">The values to prepare.</param>
    /// <param name="ct">Token that cancels the pre-validation.</param>
    /// <returns>A task that completes when the values are prepared.</returns>
    protected virtual Task PreValidateCoreAsync(TransactionCommand<TEntity> command, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Adds to <paramref name="result"/>, in check order, the error of every rule of
    /// <paramref name="validationType"/> the command breaks. Ports <c>ModelTA.doValidate</c>.
    /// </summary>
    /// <param name="command">The values to validate.</param>
    /// <param name="validationType">The set of rules to apply.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the validation.</param>
    /// <returns>A task that completes when every rule has been checked.</returns>
    protected abstract Task ValidateCoreAsync(
        TransactionCommand<TEntity> command,
        ValidationType validationType,
        OperationResult result,
        CancellationToken ct);

    /// <summary>
    /// Takes the named locks of the operation in order through <see cref="Locks"/>, returning
    /// <see langword="false"/> at the first lock not obtained; takes none and returns <see langword="true"/> by
    /// default. Ports <c>ModelTA.lockForCreate</c>, <c>lockForUpdate</c>, <c>lockForDelete</c> and
    /// <c>lockForModify</c>.
    /// </summary>
    /// <param name="command">The values being persisted.</param>
    /// <param name="validationType">The operation the locks are taken for.</param>
    /// <param name="ct">Token that cancels the lock waits.</param>
    /// <returns><see langword="true"/> when every lock is held.</returns>
    protected virtual Task<bool> AcquireLocksAsync(
        TransactionCommand<TEntity> command,
        ValidationType validationType,
        CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>
    /// Returns whether the stored entity still has the edition the caller read; always <see langword="true"/>.
    /// Ports <c>ModelTA.checkEdition</c>.
    /// </summary>
    /// <param name="expectedEdition">The edition the caller read.</param>
    /// <returns><see langword="true"/>.</returns>
    protected virtual bool CheckEdition(int expectedEdition) => true;

    /// <summary>
    /// Returns a new, untracked instance of the entity the create path adds.
    /// Ports <c>ModelTA.getModelObjectClass</c> and the <c>create ... persistent</c> of <c>ModelTA.doCreate</c>.
    /// </summary>
    /// <param name="command">The values being created.</param>
    /// <returns>The new entity.</returns>
    protected abstract TEntity CreateModelObject(TransactionCommand<TEntity> command);

    /// <summary>
    /// Sets the attributes of a newly added entity. For a <see cref="PersistentModel"/> the base sets
    /// <see cref="PersistentModel.Guid"/> to the command's GUID, or to a new GUID when the command's GUID is
    /// <see langword="null"/> or empty, and clears the soft edit lock; an override sets its own attributes and calls
    /// this method. Ports <c>PersistentModel.onCreate</c> and <c>setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The entity being created, already tracked as added.</param>
    /// <param name="command">The values being created.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    protected virtual Task OnCreateAsync(TEntity entity, TransactionCommand<TEntity> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(command);

        if (entity is PersistentModel model)
        {
            model.Guid = JadeValues.IsNull(command.Guid) ? PersistentModel.GenerateGuid() : command.Guid!;
            model.ClearSoftLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes the command's values to the entity being updated. For a <see cref="PersistentModel"/> the base clears
    /// the soft edit lock; an override sets its own attributes and calls this method.
    /// Ports <c>PersistentModel.onUpdate</c> and <c>setCommonProperties</c>.
    /// </summary>
    /// <param name="entity">The tracked entity being updated.</param>
    /// <param name="command">The values being written.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the attributes are set.</returns>
    protected virtual Task OnUpdateAsync(TEntity entity, TransactionCommand<TEntity> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity is PersistentModel model)
        {
            model.ClearSoftLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Marks the entity as deleted. Ports <c>PersistentModel.onDelete</c> (<c>delete self</c>).
    /// </summary>
    /// <param name="entity">The tracked entity being deleted.</param>
    /// <param name="command">The values of the delete.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the entity is marked as deleted.</returns>
    protected virtual Task OnDeleteAsync(TEntity entity, TransactionCommand<TEntity> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Db.Entry(entity).State = EntityState.Deleted;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Applies a modify sub-code to the entity. For a <see cref="PersistentModel"/> the base copies the command's
    /// <see cref="TransactionCommand{TEntity}.LockedBySession"/> and
    /// <see cref="TransactionCommand{TEntity}.LockedTimestamp"/> onto the soft edit lock for
    /// <see cref="TransactionType.ModifyLockEntity"/> (1001), clears the lock for
    /// <see cref="TransactionType.ModifyUnlockEntity"/> (1002), and changes nothing for any other sub-code; an
    /// override handles its own sub-codes and calls this method for the others. Ports <c>PersistentModel.onModify</c>.
    /// </summary>
    /// <param name="entity">The tracked entity being modified.</param>
    /// <param name="command">The values of the modify.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns>A task that completes when the sub-code is applied.</returns>
    protected virtual Task OnModifyAsync(
        TEntity entity,
        TransactionCommand<TEntity> command,
        int modification,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(command);

        if (entity is not PersistentModel model)
        {
            return Task.CompletedTask;
        }

        if (modification == (int)TransactionType.ModifyLockEntity)
        {
            if (command.LockedBySession is not null && command.LockedTimestamp is not null)
            {
                model.SetSoftLock(command.LockedBySession, command.LockedTimestamp.Value);
            }
            else
            {
                model.LockedBySession = command.LockedBySession;
                model.LockedTimestamp = command.LockedTimestamp is { } timestamp
                    ? DateTime.SpecifyKind(timestamp, DateTimeKind.Unspecified)
                    : null;
            }
        }
        else if (modification == (int)TransactionType.ModifyUnlockEntity)
        {
            model.ClearSoftLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the subordinate objects of a create, adding any error to <paramref name="result"/>; returns
    /// <see langword="true"/> with no work by default. Ports <c>ModelTA.createSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The values being created.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when every subordinate object was created.</returns>
    protected virtual Task<bool> CreateSubordinateObjectsAsync(
        TransactionCommand<TEntity> command,
        OperationResult result,
        CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>
    /// Updates the subordinate objects of an update, adding any error to <paramref name="result"/>; returns
    /// <see langword="true"/> with no work by default. Ports <c>ModelTA.updateSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The values being written.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when every subordinate object was updated.</returns>
    protected virtual Task<bool> UpdateSubordinateObjectsAsync(
        TransactionCommand<TEntity> command,
        OperationResult result,
        CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>
    /// Deletes the subordinate objects of a delete, adding any error to <paramref name="result"/>; returns
    /// <see langword="true"/> with no work by default. Ports <c>ModelTA.deleteSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The values of the delete.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when every subordinate object was deleted.</returns>
    protected virtual Task<bool> DeleteSubordinateObjectsAsync(
        TransactionCommand<TEntity> command,
        OperationResult result,
        CancellationToken ct) =>
        Task.FromResult(true);

    /// <summary>
    /// Applies a modify sub-code to the subordinate objects, adding any error to <paramref name="result"/>; returns
    /// <see langword="true"/> with no work by default. Ports <c>ModelTA.modifySubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The values of the modify.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="result">The result that receives the errors.</param>
    /// <param name="ct">Token that cancels the work.</param>
    /// <returns><see langword="true"/> when the sub-code was applied to every subordinate object.</returns>
    protected virtual Task<bool> ModifySubordinateObjectsAsync(
        TransactionCommand<TEntity> command,
        int modification,
        OperationResult result,
        CancellationToken ct) =>
        Task.FromResult(true);


    /// <summary>
    /// Records, inside the transaction and before the entity is marked as deleted, the change notifications of the
    /// rows the database removes or updates with it: its cascade-deleted descendants and the rows whose reference to
    /// it is cleared. Does nothing by default.
    /// </summary>
    /// <param name="entity">The tracked entity about to be deleted.</param>
    /// <param name="ct">Token that cancels the reads.</param>
    /// <returns>A task that completes when the notifications are recorded.</returns>
    protected virtual Task DeleteCascadeNotificationsAsync(TEntity entity, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Clears the errors of the subordinate commands at the start of an operation; does nothing by default.
    /// Ports <c>ModelTA.clearErrorsOnSubordinateTAs</c>.
    /// </summary>
    /// <param name="command">The values of the operation.</param>
    protected virtual void ClearErrorsOnSubordinateTAs(TransactionCommand<TEntity> command)
    {
    }

    /// <summary>
    /// Clears the warnings of the subordinate commands at the start of an operation; does nothing by default.
    /// Ports <c>ModelTA.clearWarningsOnSubordinateTAs</c>.
    /// </summary>
    /// <param name="command">The values of the operation.</param>
    protected virtual void ClearWarningsOnSubordinateTAs(TransactionCommand<TEntity> command)
    {
    }

    /// <summary>
    /// Resets the subordinate commands of an operation that did not complete; does nothing by default.
    /// Ports <c>ModelTA.doAbortTransactionCleanupForSubordinateObjects</c>.
    /// </summary>
    /// <param name="command">The values of the operation.</param>
    protected virtual void AbortTransactionCleanupForSubordinateObjects(TransactionCommand<TEntity> command)
    {
    }

    /// <summary>
    /// Creates the entity: pre-validates and validates it as <see cref="ValidationType.Create"/>, then takes the locks,
    /// adds the model object and its subordinate objects and saves them. A lock not obtained fails the operation with
    /// <see cref="MessageKeys.CreateConflictedWithAnotherUser"/>.
    /// Ports <c>ModelTA.createEntity</c>, <c>createEntityInTransState</c> and
    /// <c>createEntityWithTransactionImplementor</c>.
    /// </summary>
    /// <param name="command">The values to create.</param>
    /// <param name="mode">Whether the agent runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the create.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    protected Task<OperationResult> CreateEntityAsync(
        TransactionCommand<TEntity> command,
        TransactionMode mode,
        CancellationToken ct) =>
        RunOperationAsync(command, ValidationType.Create, 0, mode, ct);

    /// <summary>
    /// Updates the model object: resets <see cref="TransactionCommand{TEntity}.ModificationCode"/> to 0, pre-validates
    /// and validates as <see cref="ValidationType.Update"/>, then takes the locks, checks the edition, writes the model
    /// object and its subordinate objects and saves them. A lock not obtained fails the operation with
    /// <see cref="MessageKeys.UpdateConflictedWithAnotherUser"/>, a failed edition check with
    /// <see cref="MessageKeys.AnotherUserUpdatedThisEntity"/>.
    /// Ports <c>ModelTA.updateEntity</c>, <c>updateEntityInTransState</c> and
    /// <c>updateEntityWithTransactionImplementor</c>.
    /// </summary>
    /// <param name="command">The values to write.</param>
    /// <param name="mode">Whether the agent runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the update.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The agent has a model object of its own and the command has none.</exception>
    protected Task<OperationResult> UpdateEntityAsync(
        TransactionCommand<TEntity> command,
        TransactionMode mode,
        CancellationToken ct) =>
        RunOperationAsync(command, ValidationType.Update, 0, mode, ct);

    /// <summary>
    /// Deletes the model object: pre-validates and validates as <see cref="ValidationType.Delete"/>, then takes the
    /// locks, records the cascade notifications, marks the model object as deleted, deletes the subordinate objects and
    /// saves. A lock not obtained fails the operation with <see cref="MessageKeys.DeleteConflictedWithAnotherUser"/>.
    /// Ports <c>ModelTA.deleteEntity</c>, <c>deleteEntityInTransState</c> and
    /// <c>deleteEntityWithTransactionImplementor</c>.
    /// </summary>
    /// <param name="command">The values of the delete.</param>
    /// <param name="mode">Whether the agent runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the delete.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The agent has a model object of its own and the command has none.</exception>
    protected Task<OperationResult> DeleteEntityAsync(
        TransactionCommand<TEntity> command,
        TransactionMode mode,
        CancellationToken ct) =>
        RunOperationAsync(command, ValidationType.Delete, 0, mode, ct);

    /// <summary>
    /// Applies a modify sub-code: sets <see cref="TransactionCommand{TEntity}.ModificationCode"/> to
    /// <paramref name="modification"/>, pre-validates and validates as <see cref="ValidationType.Modify"/>, then takes
    /// the locks, applies the sub-code to the model object and its subordinate objects and saves. A lock not obtained
    /// fails the operation with <see cref="MessageKeys.UpdateConflictedWithAnotherUser"/>.
    /// Ports <c>ModelTA.modifyEntity</c>, <c>modifyEntityInTransState</c> and
    /// <c>modifyEntityWithTransactionImplementor</c>.
    /// </summary>
    /// <param name="command">The values of the modify.</param>
    /// <param name="modification">The modify sub-code.</param>
    /// <param name="mode">Whether the agent runs its own transaction or the caller's.</param>
    /// <param name="ct">Token that cancels the operation.</param>
    /// <returns>The result of the modify.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="NotInTransactionStateException">
    /// <see cref="TransactionMode.Ambient"/> mode reached a write with no active transaction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The agent has a model object of its own and the command has none.</exception>
    protected Task<OperationResult> ModifyEntityAsync(
        TransactionCommand<TEntity> command,
        int modification,
        TransactionMode mode,
        CancellationToken ct) =>
        RunOperationAsync(command, ValidationType.Modify, modification, mode, ct);


    /// <summary>
    /// Returns the key of the error added when a lock is not obtained: create, delete, or update for both update and
    /// modify.
    /// </summary>
    private static string ConflictMessageKey(ValidationType operation) => operation switch
    {
        ValidationType.Create => MessageKeys.CreateConflictedWithAnotherUser,
        ValidationType.Delete => MessageKeys.DeleteConflictedWithAnotherUser,
        _ => MessageKeys.UpdateConflictedWithAnotherUser,
    };

    /// <summary>
    /// Adds the error of a failed lock or edition step; a failed apply step adds no error here.
    /// </summary>
    private static void AddStepFailureError(StepOutcome outcome, ValidationType operation, OperationResult result)
    {
        if (outcome == StepOutcome.LockFailed)
        {
            result.AddError(ConflictMessageKey(operation));
        }
        else if (outcome == StepOutcome.EditionFailed)
        {
            result.AddError(MessageKeys.AnotherUserUpdatedThisEntity);
        }
    }

    /// <summary>
    /// Runs a rollback while an exception is being handled. An exception raised by the rollback itself is dropped;
    /// the exception being handled propagates.
    /// </summary>
    private static async Task RollBackWhileHandlingFailureAsync(Func<Task> rollback)
    {
        try
        {
            await rollback();
        }
        catch (Exception)
        {
            return;
        }
    }

    /// <summary>
    /// Runs one create, update, delete or modify: starts a new result, clears the subordinate errors and warnings,
    /// sets the modification code, pre-validates and validates outside any transaction, then runs the remaining steps
    /// in the agent's own transaction, under a savepoint of the caller's transaction, or, with no active transaction in
    /// Ambient mode, raises <see cref="NotInTransactionStateException"/>. Cleans up whenever the operation does not
    /// complete. Ports <c>ModelTA.createEntityWithTransactionImplementor</c> and its update, delete and modify
    /// counterparts.
    /// </summary>
    private async Task<OperationResult> RunOperationAsync(
        TransactionCommand<TEntity> command,
        ValidationType operation,
        int modification,
        TransactionMode mode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (mode is not (TransactionMode.Own or TransactionMode.Ambient))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The transaction mode is not a defined value.");
        }

        var state = new OperationState(operation, modification);
        var result = new OperationResult();
        try
        {
            ClearErrorsOnSubordinateTAs(command);
            ClearWarningsOnSubordinateTAs(command);

            if (operation == ValidationType.Modify)
            {
                command.ModificationCode = modification;
            }
            else if (operation == ValidationType.Update)
            {
                command.ModificationCode = 0;
            }

            await PreValidateAsync(command, ct);

            if (!await ValidateAsync(command, operation, result, ct))
            {
                result.Succeeded = false;
                return result;
            }

            if (mode == TransactionMode.Own)
            {
                await RunInOwnTransactionAsync(command, state, result, ct);
            }
            else if (Uow.HasActiveTransaction)
            {
                await RunInCallerTransactionAsync(command, state, result, ct);
            }
            else
            {
                RunOutsideTransaction(command, state, result);
            }

            return result;
        }
        finally
        {
            if (!state.Completed)
            {
                AbortTransactionCleanup(command, state);
            }
        }
    }

    /// <summary>
    /// Begins a transaction, takes the locks, applies the change, saves and commits, then publishes the queued
    /// notifications. A failed step rolls back, discards the queued notifications and adds its error; an exception
    /// rolls back and discards likewise, then propagates. Ports <c>CommitTransactionTI</c>.
    /// </summary>
    private async Task RunInOwnTransactionAsync(
        TransactionCommand<TEntity> command,
        OperationState state,
        OperationResult result,
        CancellationToken ct)
    {
        await Uow.BeginAsync(ct);

        StepOutcome outcome;
        try
        {
            outcome = await LockAndApplyAsync(command, state, result, ct);
            if (outcome == StepOutcome.Applied)
            {
                state.SavedEntities = CaptureSavedEntities();
                await Db.SaveChangesAsync(ct);
                await Uow.CommitAsync(ct);
            }
        }
        catch
        {
            await RollBackWhileHandlingFailureAsync(() => Uow.RollbackAsync(CancellationToken.None));
            Queue.Clear();
            throw;
        }

        if (outcome != StepOutcome.Applied)
        {
            try
            {
                await Uow.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                Queue.Clear();
            }

            AddStepFailureError(outcome, state.Operation, result);
            result.Succeeded = false;
            return;
        }

        state.Completed = true;
        SetIdentity(command, state.Operation, result);
        result.Succeeded = true;
        await Queue.FlushAsync(CancellationToken.None);
    }

    /// <summary>
    /// Creates the <c>ta_locks</c> savepoint in the caller's transaction, takes the locks, applies the change, saves and
    /// releases the savepoint, leaving commit and publication to the caller. A failed step rolls back to and releases
    /// the savepoint, discards the notifications queued since it was created and adds its error; an exception does
    /// the same, then propagates. Ports <c>NoTransactionTI</c> inside a transaction.
    /// </summary>
    private async Task RunInCallerTransactionAsync(
        TransactionCommand<TEntity> command,
        OperationState state,
        OperationResult result,
        CancellationToken ct)
    {
        await Uow.CreateSavepointAsync(AmbientSavepointName, ct);
        int mark = Queue.Mark();

        StepOutcome outcome;
        try
        {
            outcome = await LockAndApplyAsync(command, state, result, ct);
            if (outcome == StepOutcome.Applied)
            {
                state.SavedEntities = CaptureSavedEntities();
                await Db.SaveChangesAsync(ct);
                await Uow.ReleaseSavepointAsync(AmbientSavepointName, ct);
            }
        }
        catch
        {
            await RollBackWhileHandlingFailureAsync(RollBackToAmbientSavepointAsync);
            Queue.DiscardSince(mark);
            throw;
        }

        if (outcome != StepOutcome.Applied)
        {
            try
            {
                await RollBackToAmbientSavepointAsync();
            }
            finally
            {
                Queue.DiscardSince(mark);
            }

            AddStepFailureError(outcome, state.Operation, result);
            result.Succeeded = false;
            return;
        }

        state.Completed = true;
        SetIdentity(command, state.Operation, result);
        result.Succeeded = true;
    }

    /// <summary>
    /// Ambient mode with no active transaction: takes no locks, fails an update whose edition check fails, and
    /// otherwise raises <see cref="NotInTransactionStateException"/> before any apply hook runs, for every create,
    /// update, delete and modify sub-code.
    /// Ports <c>NoTransactionTI</c> outside a transaction.
    /// </summary>
    private void RunOutsideTransaction(TransactionCommand<TEntity> command, OperationState state, OperationResult result)
    {
        if (state.Operation == ValidationType.Update && !CheckEdition(command.ExpectedEdition))
        {
            result.AddError(MessageKeys.AnotherUserUpdatedThisEntity);
            result.Succeeded = false;
            return;
        }

        throw new NotInTransactionStateException();
    }

    /// <summary>Rolls back to and releases the <c>ta_locks</c> savepoint.</summary>
    private async Task RollBackToAmbientSavepointAsync()
    {
        await Uow.RollbackToSavepointAsync(AmbientSavepointName, CancellationToken.None);
        await Uow.ReleaseSavepointAsync(AmbientSavepointName, CancellationToken.None);
    }

    /// <summary>
    /// Takes the locks, checks the edition on update, records the tracking snapshot and applies the change through
    /// the matching <c>Do*</c> member.
    /// </summary>
    private async Task<StepOutcome> LockAndApplyAsync(
        TransactionCommand<TEntity> command,
        OperationState state,
        OperationResult result,
        CancellationToken ct)
    {
        if (!await AcquireLocksAsync(command, state.Operation, ct))
        {
            return StepOutcome.LockFailed;
        }

        if (state.Operation == ValidationType.Update && !CheckEdition(command.ExpectedEdition))
        {
            return StepOutcome.EditionFailed;
        }

        state.Snapshot = TakeTrackingSnapshot();

        bool applied = state.Operation switch
        {
            ValidationType.Create => await DoCreateAsync(command, state, result, ct),
            ValidationType.Update => await DoUpdateAsync(command, result, ct),
            ValidationType.Delete => await DoDeleteAsync(command, result, ct),
            ValidationType.Modify => await DoModifyAsync(command, state.Modification, result, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Operation, "The operation is not a defined value."),
        };

        return applied ? StepOutcome.Applied : StepOutcome.ApplyFailed;
    }


    /// <summary>
    /// Adds a new model object, unless the agent has only subordinate objects, then creates the subordinate objects.
    /// Ports <c>ModelTA.doCreate</c>.
    /// </summary>
    private async Task<bool> DoCreateAsync(
        TransactionCommand<TEntity> command,
        OperationState state,
        OperationResult result,
        CancellationToken ct)
    {
        if (!HasOnlySubordinatePersistentObjects)
        {
            TEntity entity = CreateModelObject(command);
            state.CreatedEntity = entity;
            command.ModelObject = entity;
            Db.Entry(entity).State = EntityState.Added;
            await OnCreateAsync(entity, command, ct);
        }

        return await CreateSubordinateObjectsAsync(command, result, ct);
    }

    /// <summary>
    /// Writes the command to the model object, unless the agent has only subordinate objects, then updates the
    /// subordinate objects. Ports <c>ModelTA.doUpdate</c>.
    /// </summary>
    private async Task<bool> DoUpdateAsync(TransactionCommand<TEntity> command, OperationResult result, CancellationToken ct)
    {
        if (!HasOnlySubordinatePersistentObjects)
        {
            TEntity model = TrackModelObject(command);
            await OnUpdateAsync(model, command, ct);
        }

        return await UpdateSubordinateObjectsAsync(command, result, ct);
    }

    /// <summary>
    /// Records the cascade notifications and marks the model object as deleted, unless the agent has only subordinate
    /// objects, then deletes the subordinate objects. Ports <c>ModelTA.doDelete</c>.
    /// </summary>
    private async Task<bool> DoDeleteAsync(TransactionCommand<TEntity> command, OperationResult result, CancellationToken ct)
    {
        if (!HasOnlySubordinatePersistentObjects)
        {
            TEntity model = TrackModelObject(command);
            await DeleteCascadeNotificationsAsync(model, ct);
            await OnDeleteAsync(model, command, ct);
        }

        return await DeleteSubordinateObjectsAsync(command, result, ct);
    }

    /// <summary>
    /// Applies the modify sub-code to the model object, unless the agent has only subordinate objects, then to the
    /// subordinate objects. Ports <c>ModelTA.doModify</c>.
    /// </summary>
    private async Task<bool> DoModifyAsync(
        TransactionCommand<TEntity> command,
        int modification,
        OperationResult result,
        CancellationToken ct)
    {
        if (!HasOnlySubordinatePersistentObjects)
        {
            TEntity model = TrackModelObject(command);
            await OnModifyAsync(model, command, modification, ct);
        }

        return await ModifySubordinateObjectsAsync(command, modification, result, ct);
    }

    /// <summary>
    /// Returns the command's model object, attaching it as unchanged when it is not tracked.
    /// </summary>
    private TEntity TrackModelObject(TransactionCommand<TEntity> command)
    {
        TEntity model = command.ModelObject
            ?? throw new InvalidOperationException("The transaction agent has no model object to work on.");

        EntityEntry<TEntity> entry = Db.Entry(model);
        if (entry.State == EntityState.Detached)
        {
            entry.State = EntityState.Unchanged;
        }

        return model;
    }

    /// <summary>
    /// Copies the id and GUID of the model object into the result after a create, update or modify.
    /// </summary>
    private void SetIdentity(TransactionCommand<TEntity> command, ValidationType operation, OperationResult result)
    {
        if (operation == ValidationType.Delete || HasOnlySubordinatePersistentObjects)
        {
            return;
        }

        if (command.ModelObject is PersistentModel model)
        {
            result.Id = model.Id;
            result.Guid = model.Guid;
        }
    }

    /// <summary>Returns every tracked entity with its current state.</summary>
    private Dictionary<object, EntityState> TakeTrackingSnapshot()
    {
        var snapshot = new Dictionary<object, EntityState>(ReferenceEqualityComparer.Instance);
        foreach (EntityEntry entry in Db.ChangeTracker.Entries())
        {
            snapshot[entry.Entity] = entry.State;
        }

        return snapshot;
    }

    /// <summary>Returns every tracked entity that is added, modified or deleted.</summary>
    private HashSet<object> CaptureSavedEntities()
    {
        var saved = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (EntityEntry entry in Db.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                saved.Add(entry.Entity);
            }
        }

        return saved;
    }

    /// <summary>
    /// Cleans up an operation that did not complete: detaches every tracked entity the operation added, changed or
    /// saved, keeping the entities tracked before the apply step and left unchanged by it; clears the model object
    /// when the operation created it or it was never saved; then runs
    /// <see cref="AbortTransactionCleanupForSubordinateObjects"/>. Ports <c>ModelTA.doAbortTransactionCleanup</c>.
    /// </summary>
    private void AbortTransactionCleanup(TransactionCommand<TEntity> command, OperationState state)
    {
        if (state.Snapshot is { } snapshot)
        {
            foreach (EntityEntry entry in Db.ChangeTracker.Entries().ToList())
            {
                bool changedByOperation =
                    !snapshot.TryGetValue(entry.Entity, out EntityState stateBefore)
                    || stateBefore != entry.State
                    || (state.SavedEntities?.Contains(entry.Entity) ?? false);

                if (changedByOperation)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }

        if (command.ModelObject is { } modelObject
            && (ReferenceEquals(modelObject, state.CreatedEntity) || modelObject is PersistentModel { Id: 0 }))
        {
            command.ModelObject = null;
        }

        AbortTransactionCleanupForSubordinateObjects(command);
    }

    /// <summary>Outcome of the lock, edition-check and apply steps of one operation.</summary>
    private enum StepOutcome
    {
        Applied,
        LockFailed,
        EditionFailed,
        ApplyFailed,
    }

    /// <summary>The state of one running operation.</summary>
    private sealed class OperationState(ValidationType operation, int modification)
    {
        /// <summary>The operation: create, update, delete or modify.</summary>
        public ValidationType Operation { get; } = operation;

        /// <summary>The modify sub-code; 0 for the other operations.</summary>
        public int Modification { get; } = modification;

        /// <summary>The tracked entities and their states immediately before the apply step; null until then.</summary>
        public Dictionary<object, EntityState>? Snapshot { get; set; }

        /// <summary>The entities added, modified or deleted immediately before the save; null until then.</summary>
        public HashSet<object>? SavedEntities { get; set; }

        /// <summary>The model object the create path made; null until then.</summary>
        public TEntity? CreatedEntity { get; set; }

        /// <summary>Whether the operation completed; a completed operation is not cleaned up.</summary>
        public bool Completed { get; set; }
    }
}

