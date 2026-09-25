using OpenOutlook.Domain;

namespace OpenOutlook.Tests;

public sealed class DeletionPolicyTests
{
    private static MessageState State(MailProvider provider, FolderKind folder, string? revision = "rev-1") =>
        new(provider, "account-1", "message-1", folder == FolderKind.Trash ? "trash-id" : "inbox-id",
            folder, revision);

    public static TheoryData<MailProvider, FolderKind, DeleteGesture, DeletionAction> Decisions => new()
    {
        { MailProvider.Pst, FolderKind.OutsideTrash, DeleteGesture.Delete, DeletionAction.MoveToTrash },
        { MailProvider.Microsoft, FolderKind.OutsideTrash, DeleteGesture.Delete, DeletionAction.MoveToTrash },
        { MailProvider.Gmail, FolderKind.OutsideTrash, DeleteGesture.Delete, DeletionAction.MoveToTrash },
        { MailProvider.Pst, FolderKind.Trash, DeleteGesture.Delete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Microsoft, FolderKind.Trash, DeleteGesture.Delete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Gmail, FolderKind.Trash, DeleteGesture.Delete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Pst, FolderKind.OutsideTrash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Microsoft, FolderKind.OutsideTrash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Gmail, FolderKind.OutsideTrash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Pst, FolderKind.Trash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Microsoft, FolderKind.Trash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Gmail, FolderKind.Trash, DeleteGesture.ShiftDelete, DeletionAction.ConfirmPermanentDelete },
        { MailProvider.Pst, FolderKind.Unknown, DeleteGesture.Delete, DeletionAction.RequireReview },
        { MailProvider.Microsoft, FolderKind.Unknown, DeleteGesture.ShiftDelete, DeletionAction.RequireReview },
        { MailProvider.Gmail, FolderKind.Unknown, DeleteGesture.Delete, DeletionAction.RequireReview }
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Decision_is_provider_independent(MailProvider provider, FolderKind folder,
        DeleteGesture gesture, DeletionAction expected)
    {
        var state = State(provider, folder);
        Assert.Equal(expected, DeletionPolicy.Decide(state, gesture));
        var plan = DeletionPolicy.Queue(state, gesture);
        Assert.Equal(expected, plan.Action);
        Assert.Equal(expected == DeletionAction.MoveToTrash, plan.Operation is not null);
        if (expected == DeletionAction.ConfirmPermanentDelete)
        {
            Assert.Null(plan.Operation); // Permanent deletion cannot be queued without consent.
            Assert.NotNull(DeletionPolicy.Queue(state, gesture, permanentDeleteConfirmed: true).Operation);
        }
    }

    public static TheoryData<MailProvider, FolderKind> ProvidersAndFolders => new()
    {
        { MailProvider.Pst, FolderKind.OutsideTrash },
        { MailProvider.Pst, FolderKind.Trash },
        { MailProvider.Microsoft, FolderKind.OutsideTrash },
        { MailProvider.Microsoft, FolderKind.Trash },
        { MailProvider.Gmail, FolderKind.OutsideTrash },
        { MailProvider.Gmail, FolderKind.Trash }
    };

    [Theory]
    [MemberData(nameof(ProvidersAndFolders))]
    public void Queued_action_survives_only_identical_state(MailProvider provider, FolderKind folder)
    {
        var state = State(provider, folder);
        var plan = DeletionPolicy.Queue(state, DeleteGesture.Delete, permanentDeleteConfirmed: true);
        var operation = Assert.IsType<QueuedDeletion>(plan.Operation);
        Assert.Equal(state.FolderId, operation.ExpectedFolderId);
        Assert.Equal(state.Revision, operation.ExpectedRevision);
        Assert.Equal(provider, operation.ExpectedProvider);
        Assert.Equal(plan.Action, DeletionPolicy.Evaluate(operation, state));
    }

    public static TheoryData<Func<MessageState, MessageState?>> StaleChanges => new()
    {
        { state => state with { Folder = state.Folder == FolderKind.Trash ? FolderKind.OutsideTrash : FolderKind.Trash,
            FolderId = state.Folder == FolderKind.Trash ? "inbox-id" : "trash-id" } },
        { state => state with { Folder = FolderKind.OutsideTrash, FolderId = "other-folder" } },
        { state => state with { Folder = FolderKind.Unknown } },
        { state => state with { Revision = "rev-2" } },
        { state => state with { Revision = null } },
        { state => state with { Provider = MailProvider.Gmail } },
        { state => state with { AccountId = "other-account" } },
        { state => state with { MessageId = "other-message" } },
        { _ => null }
    };

    [Theory]
    [MemberData(nameof(StaleChanges))]
    public void Queued_move_never_escalates_to_permanent_delete(Func<MessageState, MessageState?> change)
    {
        var state = State(MailProvider.Pst, FolderKind.OutsideTrash);
        var operation = Assert.IsType<QueuedDeletion>(DeletionPolicy.Queue(state, DeleteGesture.Delete).Operation);
        Assert.Equal(DeletionAction.MoveToTrash, operation.Action);
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Evaluate(operation, change(state)));
    }

    [Theory]
    [MemberData(nameof(StaleChanges))]
    public void Queued_permanent_delete_requires_fresh_unchanged_state(Func<MessageState, MessageState?> change)
    {
        var state = State(MailProvider.Microsoft, FolderKind.Trash);
        var operation = Assert.IsType<QueuedDeletion>(DeletionPolicy.Queue(state, DeleteGesture.Delete,
            permanentDeleteConfirmed: true).Operation);
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Evaluate(operation, change(state)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_revision_requires_review_and_cannot_be_queued(string? revision)
    {
        var state = State(MailProvider.Gmail, FolderKind.OutsideTrash, revision);
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Decide(state, DeleteGesture.ShiftDelete));
        Assert.Null(DeletionPolicy.Queue(state, DeleteGesture.Delete).Operation);
    }

    [Fact]
    public void Unknown_identity_and_unknown_gesture_require_review()
    {
        var state = State(MailProvider.Pst, FolderKind.OutsideTrash);
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Decide(null, DeleteGesture.Delete));
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Decide(state with { FolderId = "" }, DeleteGesture.Delete));
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Decide(state with { Provider = (MailProvider)99 }, DeleteGesture.Delete));
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Decide(state, (DeleteGesture)99));
        Assert.Equal(DeletionAction.RequireReview, DeletionPolicy.Evaluate(null, state));
    }
}
