namespace OpenOutlook.Domain;

/// <summary>The provider label is part of identity, not a provider operation.</summary>
public enum MailProvider { Pst, Microsoft, Gmail }

public enum FolderKind { Unknown, OutsideTrash, Trash }

public enum DeleteGesture { Delete, ShiftDelete }

public enum DeletionAction { RequireReview, MoveToTrash, ConfirmPermanentDelete }

/// <summary>A read-only observation; Revision must change whenever the message's folder/state changes.</summary>
public sealed record MessageState(
    MailProvider Provider,
    string AccountId,
    string MessageId,
    string FolderId,
    FolderKind Folder,
    string? Revision);

/// <summary>An explicit, immutable operation bound to the observed identity, folder and revision.</summary>
public sealed class QueuedDeletion
{
    internal QueuedDeletion(DeletionAction action, DeleteGesture gesture, MessageState state)
    {
        Action = action;
        Gesture = gesture;
        ExpectedProvider = state.Provider;
        ExpectedAccountId = state.AccountId;
        ExpectedMessageId = state.MessageId;
        ExpectedFolderId = state.FolderId;
        ExpectedFolder = state.Folder;
        ExpectedRevision = state.Revision!;
    }

    public DeletionAction Action { get; }
    public DeleteGesture Gesture { get; }
    public MailProvider ExpectedProvider { get; }
    public string ExpectedAccountId { get; }
    public string ExpectedMessageId { get; }
    public string ExpectedFolderId { get; }
    public FolderKind ExpectedFolder { get; }
    public string ExpectedRevision { get; }
}

public sealed record DeletionPlan(DeletionAction Action, QueuedDeletion? Operation);

/// <summary>
/// Pure authorization policy only. This class never calls a provider, modifies a PST or performs a delete.
/// Call Evaluate again immediately before any provider-side operation using a fresh state observation.
/// </summary>
public static class DeletionPolicy
{
    public static DeletionAction Decide(MessageState? state, DeleteGesture gesture)
    {
        if (!IsKnown(state) || !Enum.IsDefined(gesture))
            return DeletionAction.RequireReview;

        return gesture == DeleteGesture.ShiftDelete || state!.Folder == FolderKind.Trash
            ? DeletionAction.ConfirmPermanentDelete
            : DeletionAction.MoveToTrash;
    }

    /// <summary>
    /// Creates a queued operation only after explicit confirmation for permanent deletion.
    /// A review result cannot be queued. The caller must display the decision and obtain confirmation.
    /// </summary>
    public static DeletionPlan Queue(MessageState? state, DeleteGesture gesture, bool permanentDeleteConfirmed = false)
    {
        var action = Decide(state, gesture);
        if (action == DeletionAction.RequireReview ||
            (action == DeletionAction.ConfirmPermanentDelete && !permanentDeleteConfirmed))
            return new DeletionPlan(action, null);

        return new DeletionPlan(action, new QueuedDeletion(action, gesture, state!));
    }

    /// <summary>
    /// Returns only the queued action if the entire observed state and the policy still match.
    /// Any stale or unknown state, even a move-to-trash becoming a trash item, requires review.
    /// This authorizes no actual I/O; a provider adapter must separately enforce atomic preconditions.
    /// </summary>
    public static DeletionAction Evaluate(QueuedDeletion? operation, MessageState? current)
    {
        if (operation is null || !IsKnown(current) ||
            operation.Action == DeletionAction.RequireReview ||
            !string.Equals(operation.ExpectedAccountId, current!.AccountId, StringComparison.Ordinal) ||
            !string.Equals(operation.ExpectedMessageId, current.MessageId, StringComparison.Ordinal) ||
            operation.ExpectedProvider != current.Provider ||
            !string.Equals(operation.ExpectedFolderId, current.FolderId, StringComparison.Ordinal) ||
            operation.ExpectedFolder != current.Folder ||
            !string.Equals(operation.ExpectedRevision, current.Revision, StringComparison.Ordinal) ||
            Decide(current, operation.Gesture) != operation.Action)
            return DeletionAction.RequireReview;

        return operation.Action;
    }

    private static bool IsKnown(MessageState? state) =>
        state is not null && Enum.IsDefined(state.Provider) && Enum.IsDefined(state.Folder) &&
        state.Folder != FolderKind.Unknown &&
        !string.IsNullOrWhiteSpace(state.AccountId) &&
        !string.IsNullOrWhiteSpace(state.MessageId) &&
        !string.IsNullOrWhiteSpace(state.FolderId) &&
        !string.IsNullOrWhiteSpace(state.Revision);
}
