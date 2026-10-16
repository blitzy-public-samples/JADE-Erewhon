using System.Collections;
using System.Reflection;
using Erewhon.Application.Abstractions;
using Erewhon.Application.Common;
using Erewhon.Application.Domain;
using Erewhon.Application.Messages;
using Erewhon.Application.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

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
/// does not complete is rolled back, and the change tracker is returned to the entities, entity states, values and
/// navigations it held immediately before the apply step.
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
    /// complete. When the operation does not complete because it raised an exception, an exception raised by the
    /// cleanup is dropped and the operation's exception propagates unchanged; on every other path an exception raised
    /// by the cleanup propagates. Ports <c>ModelTA.createEntityWithTransactionImplementor</c> and its update, delete
    /// and modify counterparts.
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
        bool handlingFailure = false;
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
        catch
        {
            handlingFailure = true;
            throw;
        }
        finally
        {
            if (!state.Completed)
            {
                if (handlingFailure)
                {
                    AbortTransactionCleanupWhileHandlingFailure(command, state);
                }
                else
                {
                    AbortTransactionCleanup(command, state);
                }
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
            TrackAsAdded(entity);
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
    /// Tracks a new entity as added together with the owned entities it references, such as its address; entities
    /// reached through its other navigations are left untracked.
    /// </summary>
    private void TrackAsAdded(TEntity entity) =>
        Db.ChangeTracker.TrackGraph(entity, node =>
        {
            if (node.SourceEntry is null || node.Entry.Metadata.IsOwned())
            {
                node.Entry.State = EntityState.Added;
            }
        });

    /// <summary>
    /// Returns the command's model object. A model object that is not tracked is attached as unchanged together with
    /// the owned entities it references, such as its address; entities reached through its other navigations are left
    /// untracked. A tracked model object is returned as it is.
    /// </summary>
    private TEntity TrackModelObject(TransactionCommand<TEntity> command)
    {
        TEntity model = command.ModelObject
            ?? throw new InvalidOperationException("The transaction agent has no model object to work on.");

        if (Db.Entry(model).State == EntityState.Detached)
        {
            Db.ChangeTracker.TrackGraph(model, node =>
            {
                if (node.SourceEntry is null || node.Entry.Metadata.IsOwned())
                {
                    node.Entry.State = EntityState.Unchanged;
                }
            });
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

    /// <summary>
    /// Records every entity the change tracker holds, owned entities and link rows included: its entity state, a copy
    /// of its current and original values, its modified properties, its properties holding temporary values, and the
    /// entities and loaded state of each of its reference and collection navigations. Reads no rows.
    /// </summary>
    private TrackingSnapshot TakeTrackingSnapshot() => TrackingSnapshot.Take(Db.ChangeTracker);

    /// <summary>
    /// Cleans up an operation that did not complete. When the apply step was reached, returns the change tracker to
    /// the snapshot taken immediately before it: every entity tracked then is tracked with the entity state, current
    /// and original values, modified properties, temporary keys and navigations it had then, and every entity first
    /// tracked since is no longer tracked. Then, also when that restore raises an exception, clears the model object
    /// when the operation created it or its <see cref="PersistentModel.Id"/> is 0, and runs
    /// <see cref="AbortTransactionCleanupForSubordinateObjects"/>; an exception the restore raised then propagates.
    /// Ports <c>ModelTA.doAbortTransactionCleanup</c>.
    /// </summary>
    private void AbortTransactionCleanup(TransactionCommand<TEntity> command, OperationState state)
    {
        try
        {
            state.Snapshot?.Restore(Db);
        }
        finally
        {
            if (command.ModelObject is { } modelObject
                && (ReferenceEquals(modelObject, state.CreatedEntity) || modelObject is PersistentModel { Id: 0 }))
            {
                command.ModelObject = null;
            }

            AbortTransactionCleanupForSubordinateObjects(command);
        }
    }

    /// <summary>
    /// Runs <see cref="AbortTransactionCleanup"/> while an exception is being handled. An exception raised by the
    /// cleanup itself is dropped; the exception being handled propagates.
    /// </summary>
    private void AbortTransactionCleanupWhileHandlingFailure(TransactionCommand<TEntity> command, OperationState state)
    {
        try
        {
            AbortTransactionCleanup(command, state);
        }
        catch (Exception)
        {
            return;
        }
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

        /// <summary>
        /// The tracking state of every tracked entity immediately before the apply step, which the cleanup returns the
        /// change tracker to; null until then.
        /// </summary>
        public TrackingSnapshot? Snapshot { get; set; }

        /// <summary>The model object the create path made; null until then.</summary>
        public TEntity? CreatedEntity { get; set; }

        /// <summary>Whether the operation completed; a completed operation is not cleaned up.</summary>
        public bool Completed { get; set; }
    }

    /// <summary>
    /// The tracking state of every entity a change tracker held at one moment, owned entities and link rows included,
    /// which <see cref="Restore"/> returns the change tracker to.
    /// </summary>
    private sealed class TrackingSnapshot
    {
        private readonly List<TrackedEntity> _entities;

        private TrackingSnapshot(List<TrackedEntity> entities) => _entities = entities;

        /// <summary>
        /// Records every entity <paramref name="tracker"/> holds, in the order it lists them, after one change detection
        /// when automatic change detection is on; automatic change detection is suspended while the entities are
        /// recorded and then set back as it was. Reads no rows.
        /// </summary>
        public static TrackingSnapshot Take(ChangeTracker tracker)
        {
            bool autoDetectChanges = tracker.AutoDetectChangesEnabled;
            if (autoDetectChanges)
            {
                tracker.DetectChanges();
            }

            tracker.AutoDetectChangesEnabled = false;
            try
            {
                var entities = new List<TrackedEntity>();
                foreach (EntityEntry entry in tracker.Entries())
                {
                    entities.Add(new TrackedEntity(entry));
                }

                return new TrackingSnapshot(entities);
            }
            finally
            {
                tracker.AutoDetectChangesEnabled = autoDetectChanges;
            }
        }

        /// <summary>
        /// Returns the change tracker of <paramref name="db"/> to this snapshot; reads no rows. Entities that were not
        /// recorded stop being tracked, and a recorded entity that still has its recorded tracking state is left as it
        /// is. A recorded entity still tracked as the same entity type with its recorded key and temporary values, added
        /// exactly when it was recorded as added, and with each reference navigation recorded as holding no entity or an
        /// entity that was not recorded still holding it, gets back in place its recorded current values, entity state,
        /// original values and modified properties. Every other recorded entity is tracked again together with the
        /// entities of its ownership aggregate and, when any of these held a temporary value, with every recorded entity
        /// that held one: each stops being tracked, gets back while untracked the recorded current values and navigation
        /// contents, and is tracked again in its recorded entity state with its recorded original values and modified
        /// properties. Owners are tracked again before the entities they own and, among entities of one ownership depth,
        /// deleted entities come first and added entities last; a key or foreign key that held a temporary value holds a
        /// new temporary value, which the foreign keys of the entities referencing it hold too. Every restored entity's
        /// navigations then get back the recorded contents and, once every restored entity holds them, the recorded
        /// loaded state, a reference recorded as not loaded that holds an entity included. A restored unchanged or
        /// modified entity whose CLR member held a value other than its original value without the property being marked
        /// modified holds that value again without the mark. Automatic change detection, cascade deletes and orphan
        /// deletes are suspended meanwhile and then set back as they were.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// A recorded owned entity has no recorded owner, or the change tracker gives no entry for it, or a reference
        /// navigation recorded as not loaded that holds an entity has no writable CLR member.
        /// </exception>
        public void Restore(IErewhonDbContext db)
        {
            ChangeTracker tracker = db.ChangeTracker;
            bool autoDetectChanges = tracker.AutoDetectChangesEnabled;
            CascadeTiming cascadeDeleteTiming = tracker.CascadeDeleteTiming;
            CascadeTiming deleteOrphansTiming = tracker.DeleteOrphansTiming;
            tracker.AutoDetectChangesEnabled = false;
            tracker.CascadeDeleteTiming = CascadeTiming.Never;
            tracker.DeleteOrphansTiming = CascadeTiming.Never;
            try
            {
                var tracked = new Dictionary<object, EntityEntry>(ReferenceEqualityComparer.Instance);
                foreach (EntityEntry entry in tracker.Entries())
                {
                    tracked.Add(entry.Entity, entry);
                }

                var recorded = new HashSet<object>(
                    _entities.Select(entity => entity.Entity),
                    ReferenceEqualityComparer.Instance);
                var restoredInPlace = new List<TrackedEntity>();
                var retracked = new HashSet<TrackedEntity>();
                foreach (TrackedEntity entity in _entities)
                {
                    switch (entity.Compare(tracked.GetValueOrDefault(entity.Entity), recorded))
                    {
                        case TrackingDifference.RestorableInPlace:
                            restoredInPlace.Add(entity);
                            break;
                        case TrackingDifference.RequiresRetracking:
                            retracked.Add(entity);
                            break;
                    }
                }

                Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)> owners = OwnersOfOwnedEntities();
                if (retracked.Count > 0)
                {
                    retracked = WithAggregatesAndTemporaryValues(retracked, owners);
                    restoredInPlace.RemoveAll(retracked.Contains);
                }

                StopTracking(tracked.Values, retracked);

                List<TrackedEntity> ownersFirst = _entities
                    .Where(retracked.Contains)
                    .OrderBy(entity => entity.OwnershipDepth)
                    .ThenBy(entity => entity.TrackingOrder)
                    .ToList();
                var entries = new Dictionary<object, EntityEntry>(ownersFirst.Count, ReferenceEqualityComparer.Instance);
                foreach (TrackedEntity entity in ownersFirst)
                {
                    EntityEntry entry = entity.Ownership is null
                        ? db.Entry(entity.Entity)
                        : OwnedEntry(entity, owners, entries);
                    entity.RestoreUntracked(entry);
                    entries.Add(entity.Entity, entry);
                }

                foreach (TrackedEntity entity in ownersFirst)
                {
                    entity.Track(entries[entity.Entity]);
                }

                foreach (TrackedEntity entity in restoredInPlace)
                {
                    entity.RestoreInPlace(tracked[entity.Entity]);
                }

                foreach (TrackedEntity entity in restoredInPlace)
                {
                    entity.RestoreNavigations(tracked[entity.Entity]);
                }

                foreach (TrackedEntity entity in ownersFirst)
                {
                    entity.RestoreNavigations(entries[entity.Entity]);
                }

                foreach (TrackedEntity entity in restoredInPlace)
                {
                    entity.RestoreLoaded(tracked[entity.Entity]);
                }

                foreach (TrackedEntity entity in ownersFirst)
                {
                    entity.RestoreLoaded(entries[entity.Entity]);
                }
            }
            finally
            {
                tracker.AutoDetectChangesEnabled = autoDetectChanges;
                tracker.CascadeDeleteTiming = cascadeDeleteTiming;
                tracker.DeleteOrphansTiming = deleteOrphansTiming;
            }
        }

        /// <summary>
        /// Returns the number of owners above an entity of <paramref name="entityType"/>: 0 for an entity type that is
        /// not owned.
        /// </summary>
        public static int OwnershipDepthOf(IEntityType entityType)
        {
            int depth = 0;
            for (IForeignKey? ownership = entityType.FindOwnership(); ownership is not null; ownership = ownership.PrincipalEntityType.FindOwnership())
            {
                depth++;
            }

            return depth;
        }

        /// <summary>
        /// Stops tracking every entry of <paramref name="tracked"/> whose entity was not recorded or is in
        /// <paramref name="retracked"/>, owned entities before their owners. A deleted entry is marked unchanged before
        /// it stops being tracked; the entities related to it keep their values, entity states and navigations.
        /// </summary>
        private void StopTracking(IEnumerable<EntityEntry> tracked, HashSet<TrackedEntity> retracked)
        {
            var kept = new HashSet<object>(_entities.Count, ReferenceEqualityComparer.Instance);
            foreach (TrackedEntity entity in _entities)
            {
                if (!retracked.Contains(entity))
                {
                    kept.Add(entity.Entity);
                }
            }

            List<EntityEntry> untracked = tracked
                .Where(entry => !kept.Contains(entry.Entity))
                .OrderByDescending(entry => OwnershipDepthOf(entry.Metadata))
                .ToList();
            foreach (EntityEntry entry in untracked)
            {
                if (entry.State == EntityState.Deleted)
                {
                    entry.State = EntityState.Unchanged;
                }

                entry.State = EntityState.Detached;
            }
        }

        /// <summary>
        /// Returns <paramref name="retracked"/> with every recorded entity of the ownership aggregate of each of its
        /// entities and, when any of those holds a temporary value, with every recorded entity holding a temporary value
        /// and the recorded entities of its ownership aggregate.
        /// </summary>
        private HashSet<TrackedEntity> WithAggregatesAndTemporaryValues(
            HashSet<TrackedEntity> retracked,
            Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)> owners)
        {
            var rootOf = new Dictionary<TrackedEntity, TrackedEntity>(_entities.Count);
            foreach (TrackedEntity entity in _entities)
            {
                TrackedEntity root = entity;
                while (root.Ownership is not null)
                {
                    root = OwnerOf(root, owners).Owner;
                }

                rootOf.Add(entity, root);
            }

            var roots = new HashSet<TrackedEntity>(retracked.Select(entity => rootOf[entity]));
            if (_entities.Exists(entity => entity.HasTemporaryValues && roots.Contains(rootOf[entity])))
            {
                foreach (TrackedEntity entity in _entities)
                {
                    if (entity.HasTemporaryValues)
                    {
                        roots.Add(rootOf[entity]);
                    }
                }
            }

            return _entities.Where(entity => roots.Contains(rootOf[entity])).ToHashSet();
        }

        /// <summary>
        /// Returns, for each owned entity a recorded ownership navigation held, the recorded owner and that navigation.
        /// </summary>
        private Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)> OwnersOfOwnedEntities()
        {
            var owners = new Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)>(
                ReferenceEqualityComparer.Instance);
            foreach (TrackedEntity entity in _entities)
            {
                foreach (TrackedNavigation navigation in entity.Navigations)
                {
                    if (navigation.Navigation is INavigation { ForeignKey.IsOwnership: true, IsOnDependent: false })
                    {
                        foreach (object owned in navigation.Targets)
                        {
                            owners[owned] = (entity, navigation.Navigation);
                        }
                    }
                }
            }

            return owners;
        }

        /// <summary>
        /// Returns the recorded owner of the recorded owned entity <paramref name="owned"/> and the owner's navigation
        /// to it: the owner whose recorded ownership navigation held it, otherwise the recorded owner whose key its
        /// ownership foreign key holds.
        /// </summary>
        /// <exception cref="InvalidOperationException"><paramref name="owned"/> has no recorded owner.</exception>
        private (TrackedEntity Owner, INavigationBase Navigation) OwnerOf(
            TrackedEntity owned,
            Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)> owners)
        {
            if (owners.TryGetValue(owned.Entity, out (TrackedEntity Owner, INavigationBase Navigation) owner))
            {
                return owner;
            }

            IForeignKey ownership = owned.Ownership!;
            TrackedEntity ownerEntity = _entities.Find(candidate => owned.IsOwnedBy(candidate, ownership))
                ?? throw new InvalidOperationException("A tracked owned entity has no tracked owner.");
            return (ownerEntity, ownership.PrincipalToDependent!);
        }

        /// <summary>
        /// Returns the untracked entry of the recorded owned entity <paramref name="owned"/>, read through the untracked
        /// entry of its recorded owner. The owner's navigation holds only <paramref name="owned"/> while the entry is
        /// read, and then gets back the value it held.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="owned"/> has no recorded owner, or the change tracker gives no entry for it.
        /// </exception>
        private EntityEntry OwnedEntry(
            TrackedEntity owned,
            Dictionary<object, (TrackedEntity Owner, INavigationBase Navigation)> owners,
            Dictionary<object, EntityEntry> entries)
        {
            (TrackedEntity Owner, INavigationBase Navigation) owner = OwnerOf(owned, owners);
            NavigationEntry navigation = entries[owner.Owner.Entity].Navigation(owner.Navigation);
            object? held = navigation.CurrentValue;
            EntityEntry? entry;
            if (owner.Navigation.IsCollection)
            {
                IClrCollectionAccessor accessor = owner.Navigation.GetCollectionAccessor()
                    ?? throw new InvalidOperationException("An owned collection navigation has no collection accessor.");
                object holder = accessor.Create();
                accessor.AddStandalone(holder, owned.Entity);
                navigation.CurrentValue = holder;
                entry = ((CollectionEntry)navigation).FindEntry(owned.Entity);
            }
            else
            {
                navigation.CurrentValue = owned.Entity;
                entry = ((ReferenceEntry)navigation).TargetEntry;
            }

            navigation.CurrentValue = held;
            return entry ?? throw new InvalidOperationException("The change tracker gave no entry for a tracked owned entity.");
        }
    }

    /// <summary>How the tracking state of a recorded entity differs from the state it was recorded in.</summary>
    private enum TrackingDifference
    {
        /// <summary>The entity still has its recorded tracking state.</summary>
        None,

        /// <summary>
        /// The entity is tracked as the recorded entity type with its recorded key and temporary values, added exactly
        /// when it was recorded as added, each reference navigation recorded as holding no entity or an entity that was
        /// not recorded still holds it, and the entity differs in its entity state, values, modified properties or
        /// navigations.
        /// </summary>
        RestorableInPlace,

        /// <summary>
        /// The entity is not tracked, is tracked as another entity type, has another key or other temporary values, is
        /// added when it was not recorded as added or the reverse, or has a reference navigation recorded as holding no
        /// entity or an entity that was not recorded that holds another value.
        /// </summary>
        RequiresRetracking,
    }

    /// <summary>
    /// The tracking state of one entity: its entity state, a copy of its current values, a copy of its original values
    /// when not added, its modified properties when modified, its properties holding temporary values when added or
    /// modified, and its navigations.
    /// </summary>
    private sealed class TrackedEntity
    {
        private readonly EntityState _state;

        private readonly PropertyValues _currentValues;

        private readonly PropertyValues? _originalValues;

        private readonly IProperty[] _modifiedProperties;

        private readonly IProperty[] _temporaryProperties;

        /// <summary>Records the tracking state of the entity of <paramref name="entry"/>.</summary>
        public TrackedEntity(EntityEntry entry)
        {
            Entity = entry.Entity;
            EntityType = entry.Metadata;
            Ownership = entry.Metadata.FindOwnership();
            OwnershipDepth = TrackingSnapshot.OwnershipDepthOf(entry.Metadata);
            _state = entry.State;
            _currentValues = entry.CurrentValues.Clone();
            _originalValues = _state == EntityState.Added ? null : entry.OriginalValues.Clone();
            _modifiedProperties = _state == EntityState.Modified
                ? entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata).ToArray()
                : [];
            _temporaryProperties = _state is EntityState.Added or EntityState.Modified
                ? entry.Properties.Where(property => property.IsTemporary).Select(property => property.Metadata).ToArray()
                : [];
            Navigations = entry.Navigations.Select(navigation => new TrackedNavigation(navigation)).ToArray();
        }

        /// <summary>The entity.</summary>
        public object Entity { get; }

        /// <summary>The entity type the entity was tracked as.</summary>
        public IEntityType EntityType { get; }

        /// <summary>The relationship to the entity's owner when the entity is owned; otherwise null.</summary>
        public IForeignKey? Ownership { get; }

        /// <summary>The number of owners above the entity: 0 for an entity that is not owned.</summary>
        public int OwnershipDepth { get; }

        /// <summary>
        /// The position of the entity's state among the entities of one ownership depth when they are tracked again:
        /// 0 for deleted, 1 for unchanged or modified, 2 for added.
        /// </summary>
        public int TrackingOrder => _state switch
        {
            EntityState.Deleted => 0,
            EntityState.Added => 2,
            _ => 1,
        };

        /// <summary>Whether any property of the entity held a temporary value.</summary>
        public bool HasTemporaryValues => _temporaryProperties.Length > 0;

        /// <summary>The entity's reference and collection navigations.</summary>
        public IReadOnlyList<TrackedNavigation> Navigations { get; }

        /// <summary>
        /// Returns how the tracking state of the entity, tracked by <paramref name="entry"/> or untracked when it is
        /// null, differs from the recorded one; <paramref name="recorded"/> holds every recorded entity.
        /// </summary>
        public TrackingDifference Compare(EntityEntry? entry, IReadOnlySet<object> recorded)
        {
            if (entry is null
                || entry.Metadata != EntityType
                || (entry.State == EntityState.Added) != (_state == EntityState.Added))
            {
                return TrackingDifference.RequiresRetracking;
            }

            PropertyValues currentValues = entry.CurrentValues;
            foreach (IProperty property in EntityType.FindPrimaryKey()!.Properties)
            {
                if (!Equals(currentValues[property], _currentValues[property]))
                {
                    return TrackingDifference.RequiresRetracking;
                }
            }

            if (!HasRecordedTemporaryValues(entry)
                || Navigations.Any(navigation => navigation.NoLongerHoldsUnrecordedReference(entry, recorded)))
            {
                return TrackingDifference.RequiresRetracking;
            }

            bool asRecorded = entry.State == _state
                && HoldValues(currentValues, _currentValues)
                && (_originalValues is null || HoldValues(entry.OriginalValues, _originalValues))
                && HasRecordedModifiedProperties(entry)
                && Navigations.All(navigation => navigation.IsAsRecorded(entry));
            return asRecorded ? TrackingDifference.None : TrackingDifference.RestorableInPlace;
        }

        /// <summary>
        /// Returns whether this owned entity's foreign key of <paramref name="ownership"/> holds the key of
        /// <paramref name="candidate"/>, an entity of the owner's entity type.
        /// </summary>
        public bool IsOwnedBy(TrackedEntity candidate, IForeignKey ownership)
        {
            if (!ownership.PrincipalEntityType.IsAssignableFrom(candidate.EntityType))
            {
                return false;
            }

            for (int i = 0; i < ownership.Properties.Count; i++)
            {
                if (!Equals(_currentValues[ownership.Properties[i]], candidate._currentValues[ownership.PrincipalKey.Properties[i]]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Writes to the untracked <paramref name="entry"/> the recorded current values, the sentinel value of each
        /// property that held a temporary value, and the recorded contents of each navigation.
        /// </summary>
        public void RestoreUntracked(EntityEntry entry)
        {
            entry.CurrentValues.SetValues(_currentValues);
            foreach (IProperty property in _temporaryProperties)
            {
                entry.Property(property).CurrentValue = property.Sentinel;
            }

            RestoreNavigations(entry);
        }

        /// <summary>
        /// Tracks the untracked <paramref name="entry"/>, which holds the recorded current values, again in the recorded
        /// entity state with the recorded original values and modified properties; the loaded state of its navigations
        /// is left to <see cref="RestoreLoaded"/>. An entry recorded as added is marked added; one recorded as deleted is
        /// marked deleted and gets the recorded original values; any other entry is marked unchanged and gets its
        /// recorded values and modified properties through <see cref="RestoreTrackedValues"/>, a property that held a
        /// temporary value keeping the value it holds once tracked.
        /// </summary>
        public void Track(EntityEntry entry)
        {
            switch (_state)
            {
                case EntityState.Added:
                    entry.State = EntityState.Added;
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Deleted;
                    entry.OriginalValues.SetValues(_originalValues!);
                    break;
                default:
                    entry.State = EntityState.Unchanged;
                    RestoreTrackedValues(entry, keepTemporaryValues: true);
                    break;
            }
        }

        /// <summary>
        /// Gives the tracked <paramref name="entry"/>, whose key, temporary values and added state are the recorded
        /// ones, the recorded entity state, current values, original values, modified properties and temporary
        /// markings; the loaded state of its navigations is left to <see cref="RestoreLoaded"/>. An entry recorded as
        /// added gets back each recorded current value it no longer holds; any other entry gets them through
        /// <see cref="RestoreTrackedValues"/>.
        /// </summary>
        public void RestoreInPlace(EntityEntry entry)
        {
            if (_state == EntityState.Added)
            {
                PropertyValues currentValues = entry.CurrentValues;
                foreach (IProperty property in _currentValues.Properties)
                {
                    object? value = _currentValues[property];
                    if (!Equals(currentValues[property], value))
                    {
                        currentValues[property] = value;
                    }
                }
            }
            else
            {
                RestoreTrackedValues(entry, keepTemporaryValues: false);
            }
        }

        /// <summary>Gives each navigation of <paramref name="entry"/> the recorded contents.</summary>
        public void RestoreNavigations(EntityEntry entry)
        {
            foreach (TrackedNavigation navigation in Navigations)
            {
                navigation.RestoreContents(entry);
            }
        }

        /// <summary>Returns whether every value of <paramref name="actual"/> equals that of <paramref name="expected"/>.</summary>
        private static bool HoldValues(PropertyValues actual, PropertyValues expected)
        {
            foreach (IProperty property in expected.Properties)
            {
                if (!Equals(actual[property], expected[property]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether exactly the recorded properties of <paramref name="entry"/> hold temporary values, each the
        /// recorded value.
        /// </summary>
        private bool HasRecordedTemporaryValues(EntityEntry entry)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                return _temporaryProperties.Length == 0;
            }

            int temporary = 0;
            foreach (PropertyEntry property in entry.Properties)
            {
                if (property.IsTemporary)
                {
                    if (Array.IndexOf(_temporaryProperties, property.Metadata) < 0
                        || !Equals(property.CurrentValue, _currentValues[property.Metadata]))
                    {
                        return false;
                    }

                    temporary++;
                }
            }

            return temporary == _temporaryProperties.Length;
        }

        /// <summary>
        /// Returns whether exactly the recorded modified properties of <paramref name="entry"/> are modified; true for an
        /// entity not recorded as modified.
        /// </summary>
        private bool HasRecordedModifiedProperties(EntityEntry entry)
        {
            if (_state != EntityState.Modified)
            {
                return true;
            }

            foreach (PropertyEntry property in entry.Properties)
            {
                if (property.IsModified != (Array.IndexOf(_modifiedProperties, property.Metadata) >= 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Gives each navigation of <paramref name="entry"/>, which holds its recorded contents, the recorded loaded
        /// state through <see cref="TrackedNavigation.RestoreLoaded"/>.
        /// </summary>
        public void RestoreLoaded(EntityEntry entry)
        {
            foreach (TrackedNavigation navigation in Navigations)
            {
                navigation.RestoreLoaded(entry);
            }
        }

        /// <summary>
        /// Gives the tracked <paramref name="entry"/>, recorded and tracked as not added, the recorded entity state,
        /// original values, modified properties and current values. Marks it unchanged and sets the recorded original
        /// values, which marks modified each property not holding its original value. An entry recorded as modified then
        /// gets back exactly its recorded modified properties, each marked modified with its recorded current value, and
        /// every other property gets back its original value. Any other entry is marked unchanged again, which gives
        /// every property back its original value; one recorded as deleted is then marked deleted and gets back its
        /// recorded current values. Finally, each property of an entry recorded as unchanged or modified that has a CLR
        /// member and is not a recorded modified property gets its recorded current value written to that member without
        /// change detection, so it holds that value without being marked modified. With
        /// <paramref name="keepTemporaryValues"/>, a property that held a temporary value keeps the value it holds and
        /// stays marked modified; otherwise it is marked temporary again.
        /// </summary>
        private void RestoreTrackedValues(EntityEntry entry, bool keepTemporaryValues)
        {
            if (entry.State != EntityState.Unchanged)
            {
                entry.State = EntityState.Unchanged;
            }

            entry.OriginalValues.SetValues(_originalValues!);
            if (_state != EntityState.Modified && entry.State != EntityState.Unchanged)
            {
                entry.State = EntityState.Unchanged;
            }

            PropertyValues currentValues = entry.CurrentValues;
            if (_state == EntityState.Deleted)
            {
                entry.State = EntityState.Deleted;
                foreach (IProperty property in _currentValues.Properties)
                {
                    object? value = _currentValues[property];
                    if (!Equals(currentValues[property], value))
                    {
                        currentValues[property] = value;
                    }
                }

                return;
            }

            if (_state == EntityState.Modified)
            {
                RestoreModifiedProperties(entry, keepTemporaryValues);
            }

            foreach (IProperty property in _currentValues.Properties)
            {
                if (property.IsShadowProperty()
                    || (_state == EntityState.Modified && Array.IndexOf(_modifiedProperties, property) >= 0)
                    || (keepTemporaryValues && Array.IndexOf(_temporaryProperties, property) >= 0))
                {
                    continue;
                }

                object? value = _currentValues[property];
                if (!Equals(currentValues[property], value))
                {
                    WriteToMember(entry.Entity, property, value);
                }
            }

            if (!keepTemporaryValues)
            {
                foreach (IProperty property in _temporaryProperties)
                {
                    PropertyEntry temporary = entry.Property(property);
                    if (!temporary.IsTemporary)
                    {
                        temporary.IsTemporary = true;
                    }
                }
            }
        }

        /// <summary>
        /// Marks exactly the recorded modified properties of <paramref name="entry"/> as modified, each holding its
        /// recorded current value, and every other property as not modified, which gives it back its original value,
        /// leaving the entry in the modified state. With <paramref name="keepTemporaryValues"/>, a property that held a
        /// temporary value keeps the value it holds.
        /// </summary>
        private void RestoreModifiedProperties(EntityEntry entry, bool keepTemporaryValues)
        {
            var modified = new HashSet<IProperty>(_modifiedProperties);
            foreach (PropertyEntry property in entry.Properties)
            {
                if (!modified.Contains(property.Metadata))
                {
                    continue;
                }

                object? value = _currentValues[property.Metadata];
                if (!(keepTemporaryValues && Array.IndexOf(_temporaryProperties, property.Metadata) >= 0)
                    && !Equals(property.CurrentValue, value))
                {
                    property.CurrentValue = value;
                }

                if (!property.IsModified)
                {
                    property.IsModified = true;
                }
            }

            foreach (PropertyEntry property in entry.Properties)
            {
                if (!modified.Contains(property.Metadata) && property.IsModified)
                {
                    property.IsModified = false;
                }
            }

            if (entry.State != EntityState.Modified)
            {
                entry.State = EntityState.Modified;
            }
        }

        /// <summary>
        /// Writes <paramref name="value"/> to the CLR member of the property or navigation <paramref name="property"/>
        /// on <paramref name="entity"/>: the backing field or property setter the change tracker itself writes through,
        /// outside the change tracker.
        /// </summary>
        /// <exception cref="InvalidOperationException"><paramref name="property"/> has no writable CLR member.</exception>
        public static void WriteToMember(object entity, IPropertyBase property, object? value)
        {
            switch (property.GetMemberInfo(forMaterialization: false, forSet: true))
            {
                case FieldInfo field:
                    field.SetValue(entity, value);
                    break;
                case PropertyInfo indexer when property.IsIndexerProperty():
                    indexer.SetValue(entity, value, [property.Name]);
                    break;
                case PropertyInfo member:
                    member.SetValue(entity, value);
                    break;
                default:
                    throw new InvalidOperationException(
                        "A property or navigation of a tracked entity has no writable CLR member.");
            }
        }
    }

    /// <summary>
    /// The state of one navigation of a tracked entity: the entity or collection it held, the entities in that
    /// collection in their order, and whether it was loaded.
    /// </summary>
    private sealed class TrackedNavigation
    {
        private readonly object? _value;

        private readonly object[]? _items;

        private readonly bool _isLoaded;

        /// <summary>Records the state of the navigation of <paramref name="entry"/>.</summary>
        public TrackedNavigation(NavigationEntry entry)
        {
            Navigation = entry.Metadata;
            _value = entry.CurrentValue;
            _items = Navigation.IsCollection && _value is IEnumerable items ? items.Cast<object>().ToArray() : null;
            _isLoaded = entry.IsLoaded;
        }

        /// <summary>The navigation.</summary>
        public INavigationBase Navigation { get; }

        /// <summary>The entities the navigation held: the referenced entity, or the entities in the collection.</summary>
        public IEnumerable<object> Targets => _items ?? (_value is null ? Array.Empty<object>() : new[] { _value });

        /// <summary>
        /// Returns whether the navigation of <paramref name="entry"/> holds the recorded entity, or the recorded
        /// collection with the recorded entities in their order, and has the recorded loaded state.
        /// </summary>
        public bool IsAsRecorded(EntityEntry entry)
        {
            NavigationEntry navigation = entry.Navigation(Navigation);
            object? value = navigation.CurrentValue;
            return navigation.IsLoaded == _isLoaded
                && ReferenceEquals(value, _value)
                && (_items is null || value is not IEnumerable collection || HoldsInOrder(collection, _items));
        }

        /// <summary>
        /// Returns whether this is a reference navigation that held no entity or an entity not in
        /// <paramref name="recorded"/>, and the navigation of <paramref name="entry"/> holds another value.
        /// </summary>
        public bool NoLongerHoldsUnrecordedReference(EntityEntry entry, IReadOnlySet<object> recorded) =>
            !Navigation.IsCollection
            && (_value is null || !recorded.Contains(_value))
            && !ReferenceEquals(entry.Navigation(Navigation).CurrentValue, _value);

        /// <summary>
        /// Makes the navigation of <paramref name="entry"/> hold the recorded entity, or the recorded collection with the
        /// recorded entities in their order. On a tracked entry, a reference that differs from the recorded one is set
        /// through the change tracker, which fixes up the relationship.
        /// </summary>
        public void RestoreContents(EntityEntry entry)
        {
            NavigationEntry navigation = entry.Navigation(Navigation);
            if (!ReferenceEquals(navigation.CurrentValue, _value))
            {
                navigation.CurrentValue = _value;
            }

            if (_items is null || _value is not IEnumerable collection || HoldsInOrder(collection, _items))
            {
                return;
            }

            if (collection is IList { IsReadOnly: false, IsFixedSize: false } list)
            {
                list.Clear();
                foreach (object item in _items)
                {
                    list.Add(item);
                }

                return;
            }

            IClrCollectionAccessor accessor = Navigation.GetCollectionAccessor()
                ?? throw new InvalidOperationException("A collection navigation has no collection accessor.");
            foreach (object item in collection.Cast<object>().ToList())
            {
                accessor.RemoveStandalone(collection, item);
            }

            foreach (object item in _items)
            {
                accessor.AddStandalone(collection, item);
            }
        }

        /// <summary>
        /// Gives the navigation of <paramref name="entry"/> the recorded loaded state when it has another one. A
        /// reference navigation recorded as not loaded that holds an entity is marked not loaded while its CLR member
        /// holds <see langword="null"/>, then holds that entity again; both member writes bypass the change tracker, so
        /// the entity's foreign key, entity state and relationship snapshot are unchanged.
        /// </summary>
        /// <exception cref="InvalidOperationException">The reference navigation has no writable CLR member.</exception>
        public void RestoreLoaded(EntityEntry entry)
        {
            NavigationEntry navigation = entry.Navigation(Navigation);
            if (navigation.IsLoaded == _isLoaded)
            {
                return;
            }

            if (_isLoaded || Navigation.IsCollection || navigation.CurrentValue is not { } held)
            {
                navigation.IsLoaded = _isLoaded;
                return;
            }

            TrackedEntity.WriteToMember(entry.Entity, Navigation, null);
            try
            {
                navigation.IsLoaded = false;
            }
            finally
            {
                TrackedEntity.WriteToMember(entry.Entity, Navigation, held);
            }
        }

        /// <summary>Returns whether <paramref name="collection"/> holds exactly <paramref name="items"/>, in that order.</summary>
        private static bool HoldsInOrder(IEnumerable collection, object[] items)
        {
            int index = 0;
            foreach (object? item in collection)
            {
                if (index == items.Length || !ReferenceEquals(item, items[index]))
                {
                    return false;
                }

                index++;
            }

            return index == items.Length;
        }
    }
}

