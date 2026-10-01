using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenOutlook.Desktop;

/// <summary>
/// The classic File > Options dialog, its Editor Options (Proofing) sub-dialog and the Reading Pane
/// dialog. Controls mirror Outlook 2024 exactly; OK persists every value to options.json (Cancel and
/// Esc discard pending changes), and features consume these values as they are implemented. Which
/// settings already change behaviour is documented in BUILD_STATUS.md; the rest are faithful
/// placeholders that keep their state like the real dialog does.
/// </summary>
public partial class MainWindow
{
    private readonly OptionsSettingsStore _optionsStore = new();
    private OptionsSettings _options = new();

    private static readonly System.Collections.Generic.Dictionary<string, string> OptionCategoryNames = new(StringComparer.Ordinal)
    {
        ["calendar"] = "Calendar", ["groups"] = "Groups", ["people"] = "People", ["tasks"] = "Tasks",
        ["search"] = "Search", ["language"] = "Language", ["accessibility"] = "Accessibility",
        ["advanced"] = "Advanced", ["ribbon"] = "Customize Ribbon", ["qat"] = "Quick Access Toolbar",
        ["addins"] = "Add-ins", ["trust"] = "Trust Center"
    };

    // ---- opening and closing ----

    private void OptionsNavClicked(object? sender, RoutedEventArgs e)
    {
        CloseBackstage();
        OpenOptionsDialog();
    }

    private void OpenOptionsDialog()
    {
        _options = _optionsStore.Load();
        ApplyMailOptions(_options);
        OptionsHost.IsVisible = true;
        OptionsFeedback.Text = "";
        SelectOptionsCategory("general");
        OptCatGeneral.IsChecked = true;
        OptionsHost.Focus();
    }

    private void OptionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && OptionsHost.IsVisible &&
            !EditorOptionsHost.IsVisible && !ReadingPaneHost.IsVisible)
        { CloseOptionsDialog(); e.Handled = true; }
    }

    private void OptionsOkClicked(object? sender, RoutedEventArgs e)
    {
        _options = CollectMailOptions(_options);
        try
        {
            _optionsStore.Save(_options);
            StatusText.Text = "Options saved.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { StatusText.Text = "Options changed, but they could not be saved. Check the settings folder."; }
        CloseOptionsDialog();
    }

    private void OptionsCancelClicked(object? sender, RoutedEventArgs e) => CloseOptionsDialog();

    private void CloseOptionsDialog() => OptionsHost.IsVisible = false;

    private void OptionsCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) SelectOptionsCategory(key);
    }

    private void SelectOptionsCategory(string key)
    {
        PageGeneral.IsVisible = key == "general";
        PageMail.IsVisible = key == "mail";
        PageOptionPlaceholder.IsVisible = OptionCategoryNames.TryGetValue(key, out var name);
        if (name is not null) OptionsPlaceholderHeading.Text = name;
    }

    private void OptionsPlaceholderClicked(object? sender, RoutedEventArgs e) =>
        OptionsFeedback.Text = $"{(sender as Button)?.Tag} is planned for a future version of OpenOutlook.";

    // ---- Editor Options dialog ----

    private void OpenEditorOptionsClicked(object? sender, RoutedEventArgs e)
    {
        ApplyProofingOptions(_options);
        EditorOptionsHost.IsVisible = true;
        EditorFeedback.Text = "";
        SelectEditorCategory("proofing");
        EdCatProofing.IsChecked = true;
    }

    private void EditorDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && EditorOptionsHost.IsVisible) { CloseEditorDialog(); e.Handled = true; }
    }

    private void EditorCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) SelectEditorCategory(key);
    }

    private void SelectEditorCategory(string key)
    {
        EdPageProofing.IsVisible = key == "proofing";
        EdPagePlaceholder.IsVisible = key != "proofing";
        EdPlaceholderHeading.Text = key == "advanced" ? "Advanced" : "Accessibility";
    }

    private void EditorOkClicked(object? sender, RoutedEventArgs e)
    {
        _options = CollectProofingOptions(_options);
        try { _optionsStore.Save(_options); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { EditorFeedback.Text = "Changes could not be saved. Check the settings folder."; return; }
        CloseEditorDialog();
    }

    private void EditorCancelClicked(object? sender, RoutedEventArgs e) => CloseEditorDialog();

    private void CloseEditorDialog() => EditorOptionsHost.IsVisible = false;

    private void EditorPlaceholderClicked(object? sender, RoutedEventArgs e) =>
        EditorFeedback.Text = $"{(sender as Button)?.Tag} are planned for a future version of OpenOutlook.";

    // ---- Reading Pane dialog ----

    private void OpenReadingPaneClicked(object? sender, RoutedEventArgs e)
    {
        RpMarkOnView.IsChecked = _options.ReadPaneMarkOnView;
        RpWait.Value = _options.ReadPaneWaitSeconds;
        RpMarkOnSelection.IsChecked = _options.ReadPaneMarkOnSelectionChange;
        RpSingleKey.IsChecked = _options.ReadPaneSingleKeyReading;
        RpFullScreen.IsChecked = _options.ReadPaneFullScreenPortrait;
        RpAlwaysPreview.IsChecked = _options.ReadPaneAlwaysPreview;
        ReadingPaneFeedback.Text = "";
        ReadingPaneHost.IsVisible = true;
    }

    private void ReadingPaneDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ReadingPaneHost.IsVisible) { CloseReadingPaneDialog(); e.Handled = true; }
    }

    private void ReadingPaneOkClicked(object? sender, RoutedEventArgs e)
    {
        _options = _options with
        {
            ReadPaneMarkOnView = RpMarkOnView.IsChecked == true,
            ReadPaneWaitSeconds = (int)Math.Clamp(RpWait.Value ?? 2, 0, 300),
            ReadPaneMarkOnSelectionChange = RpMarkOnSelection.IsChecked == true,
            ReadPaneSingleKeyReading = RpSingleKey.IsChecked == true,
            ReadPaneFullScreenPortrait = RpFullScreen.IsChecked == true,
            ReadPaneAlwaysPreview = RpAlwaysPreview.IsChecked == true
        };
        try { _optionsStore.Save(_options); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ReadingPaneFeedback.Text = "Changes could not be saved. Check the settings folder."; return; }
        CloseReadingPaneDialog();
    }

    private void ReadingPaneCancelClicked(object? sender, RoutedEventArgs e) => CloseReadingPaneDialog();

    private void CloseReadingPaneDialog() => ReadingPaneHost.IsVisible = false;

    // ---- interlocked controls ----

    private void OptAutoSaveToggled(object? sender, RoutedEventArgs e)
    { OptAutoSaveMinutes.IsEnabled = OptAutoSave.IsChecked == true; }

    private void OptExpireToggled(object? sender, RoutedEventArgs e)
    { OptExpireDays.IsEnabled = OptMarkExpired.IsChecked == true; }

    // ---- apply / collect ----

    private void ApplyMailOptions(OptionsSettings s)
    {
        SetComboText(OptComposeFormat, s.ComposeFormat);
        OptCheckSpelling.IsChecked = s.CheckSpellingBeforeSending;
        OptIgnoreOriginal.IsChecked = s.IgnoreOriginalInReply;
        OptPlaySound.IsChecked = s.PlaySound;
        OptChangePointer.IsChecked = s.ChangeMousePointer;
        OptTaskbarIcon.IsChecked = s.TaskbarEnvelopeIcon;
        OptDesktopAlert.IsChecked = s.DesktopAlert;
        OptCleanupRecreate.IsChecked = s.CleanUpRecreateHierarchy;
        OptCleanupUnread.IsChecked = s.CleanUpDontMoveUnread;
        OptCleanupCategorized.IsChecked = s.CleanUpDontMoveCategorized;
        OptCleanupFlagged.IsChecked = s.CleanUpDontMoveFlagged;
        OptCleanupSigned.IsChecked = s.CleanUpDontMoveSigned;
        OptCleanupReply.IsChecked = s.CleanUpReplyKeepsOriginal;
        OptSuggestedReplies.IsChecked = s.ShowSuggestedReplies;
        OptRepliesNewWindow.IsChecked = s.OpenRepliesInNewWindow;
        OptCloseOriginal.IsChecked = s.CloseOriginalWhenReplying;
        // The preface box is disabled until comment support exists, like classic Outlook.
        OptPrefaceComments.Text = _accountRegistry.Load().FirstOrDefault()?.DisplayAddress ?? "";
        SetComboText(OptReplyCombo, s.WhenReplying);
        SetComboText(OptForwardCombo, s.WhenForwarding);
        OptAutoSave.IsChecked = s.AutoSaveUnsent;
        OptAutoSaveMinutes.Value = s.AutoSaveMinutes;
        OptAutoSaveMinutes.IsEnabled = s.AutoSaveUnsent;
        SetComboText(OptUnsentFolder, s.UnsentSaveFolder);
        OptReplySameFolder.IsChecked = s.ReplySavesToSameFolder;
        OptSaveForwarded.IsChecked = s.SaveForwardedMessages;
        OptSaveSentCopies.IsChecked = s.SaveSentCopies;
        OptUnicode.IsChecked = s.UseUnicodeFormat;
        SetComboText(OptImportance, s.DefaultImportance);
        SetComboText(OptSensitivity, s.DefaultSensitivity);
        OptMarkExpired.IsChecked = s.MarkExpired;
        OptExpireDays.Value = s.ExpireAfterDays;
        OptExpireDays.IsEnabled = s.MarkExpired;
        OptDefaultAccount.IsChecked = s.AlwaysUseDefaultAccount;
        OptCommas.IsChecked = s.CommasSeparateRecipients;
        OptNameCheck.IsChecked = s.AutomaticNameChecking;
        OptDeleteMeetings.IsChecked = s.DeleteMeetingResponses;
        OptCtrlEnter.IsChecked = s.CtrlEnterSends;
        OptAutoComplete.IsChecked = s.UseAutoCompleteList;
        OptWarnAttachment.IsChecked = s.WarnMissingAttachment;
        OptMention.IsChecked = s.MentionSuggestions;
        OptRequestDelivery.IsChecked = s.RequestDeliveryReceipt;
        OptRequestRead.IsChecked = s.RequestReadReceipt;
        RrAlways.IsChecked = s.ReadReceiptPolicy == "Always send a read receipt";
        RrNever.IsChecked = s.ReadReceiptPolicy == "Never send a read receipt";
        RrAsk.IsChecked = s.ReadReceiptPolicy == "Ask each time whether to send a read receipt";
        if (RrAlways.IsChecked != true && RrNever.IsChecked != true && RrAsk.IsChecked != true) RrNever.IsChecked = true;
        OptAutoMeetings.IsChecked = s.AutoProcessMeetings;
        OptAutoUpdateSent.IsChecked = s.AutoUpdateSentItem;
        OptUpdateDelete.IsChecked = s.UpdateThenDeleteResponses;
        OptMoveReceipts.IsChecked = s.MoveReceiptsAfterUpdate;
        OptCss.IsChecked = s.UseCss;
        OptReduceSize.IsChecked = s.ReduceMessageSize;
        OptUuencode.IsChecked = s.UuencodeAttachments;
        OptWrapAt.Value = s.WrapAtCharacter;
        OptRemoveBreaks.IsChecked = s.RemoveExtraLineBreaks;
        SetComboText(OptRtfInternet, s.RichTextToInternet);
        OptPasteOptions.IsChecked = s.ShowPasteOptions;
        OptNextPrev.IsChecked = s.ShowNextPreviousLinks;
        OptNoAutoExpand.IsChecked = s.DontAutoExpandConversations;
        SetComboText(OptAfterMoveDelete, s.AfterMoveOrDelete);
        OptMarkReadDeleted.IsChecked = s.MarkReadWhenDeleted;
        OptHighlightFlagged.IsChecked = s.HighlightFlaggedItems;
    }

    private OptionsSettings CollectMailOptions(OptionsSettings s) => s with
    {
        ComposeFormat = ComboText(OptComposeFormat, s.ComposeFormat),
        CheckSpellingBeforeSending = OptCheckSpelling.IsChecked == true,
        IgnoreOriginalInReply = OptIgnoreOriginal.IsChecked == true,
        PlaySound = OptPlaySound.IsChecked == true,
        ChangeMousePointer = OptChangePointer.IsChecked == true,
        TaskbarEnvelopeIcon = OptTaskbarIcon.IsChecked == true,
        DesktopAlert = OptDesktopAlert.IsChecked == true,
        CleanUpRecreateHierarchy = OptCleanupRecreate.IsChecked == true,
        CleanUpDontMoveUnread = OptCleanupUnread.IsChecked == true,
        CleanUpDontMoveCategorized = OptCleanupCategorized.IsChecked == true,
        CleanUpDontMoveFlagged = OptCleanupFlagged.IsChecked == true,
        CleanUpDontMoveSigned = OptCleanupSigned.IsChecked == true,
        CleanUpReplyKeepsOriginal = OptCleanupReply.IsChecked == true,
        ShowSuggestedReplies = OptSuggestedReplies.IsChecked == true,
        OpenRepliesInNewWindow = OptRepliesNewWindow.IsChecked == true,
        CloseOriginalWhenReplying = OptCloseOriginal.IsChecked == true,
        WhenReplying = ComboText(OptReplyCombo, s.WhenReplying),
        WhenForwarding = ComboText(OptForwardCombo, s.WhenForwarding),
        AutoSaveUnsent = OptAutoSave.IsChecked == true,
        AutoSaveMinutes = (int)Math.Clamp(OptAutoSaveMinutes.Value ?? s.AutoSaveMinutes, 1, 99),
        UnsentSaveFolder = ComboText(OptUnsentFolder, s.UnsentSaveFolder),
        ReplySavesToSameFolder = OptReplySameFolder.IsChecked == true,
        SaveForwardedMessages = OptSaveForwarded.IsChecked == true,
        SaveSentCopies = OptSaveSentCopies.IsChecked == true,
        UseUnicodeFormat = OptUnicode.IsChecked == true,
        DefaultImportance = ComboText(OptImportance, s.DefaultImportance),
        DefaultSensitivity = ComboText(OptSensitivity, s.DefaultSensitivity),
        MarkExpired = OptMarkExpired.IsChecked == true,
        ExpireAfterDays = (int)Math.Clamp(OptExpireDays.Value ?? s.ExpireAfterDays, 0, 3650),
        AlwaysUseDefaultAccount = OptDefaultAccount.IsChecked == true,
        CommasSeparateRecipients = OptCommas.IsChecked == true,
        AutomaticNameChecking = OptNameCheck.IsChecked == true,
        DeleteMeetingResponses = OptDeleteMeetings.IsChecked == true,
        CtrlEnterSends = OptCtrlEnter.IsChecked == true,
        UseAutoCompleteList = OptAutoComplete.IsChecked == true,
        WarnMissingAttachment = OptWarnAttachment.IsChecked == true,
        MentionSuggestions = OptMention.IsChecked == true,
        RequestDeliveryReceipt = OptRequestDelivery.IsChecked == true,
        RequestReadReceipt = OptRequestRead.IsChecked == true,
        ReadReceiptPolicy = RrAlways.IsChecked == true ? "Always send a read receipt" :
            RrAsk.IsChecked == true ? "Ask each time whether to send a read receipt" : "Never send a read receipt",
        AutoProcessMeetings = OptAutoMeetings.IsChecked == true,
        AutoUpdateSentItem = OptAutoUpdateSent.IsChecked == true,
        UpdateThenDeleteResponses = OptUpdateDelete.IsChecked == true,
        MoveReceiptsAfterUpdate = OptMoveReceipts.IsChecked == true,
        UseCss = OptCss.IsChecked == true,
        ReduceMessageSize = OptReduceSize.IsChecked == true,
        UuencodeAttachments = OptUuencode.IsChecked == true,
        WrapAtCharacter = (int)Math.Clamp(OptWrapAt.Value ?? s.WrapAtCharacter, 25, 700),
        RemoveExtraLineBreaks = OptRemoveBreaks.IsChecked == true,
        RichTextToInternet = ComboText(OptRtfInternet, s.RichTextToInternet),
        ShowPasteOptions = OptPasteOptions.IsChecked == true,
        ShowNextPreviousLinks = OptNextPrev.IsChecked == true,
        DontAutoExpandConversations = OptNoAutoExpand.IsChecked == true,
        AfterMoveOrDelete = ComboText(OptAfterMoveDelete, s.AfterMoveOrDelete),
        MarkReadWhenDeleted = OptMarkReadDeleted.IsChecked == true,
        HighlightFlaggedItems = OptHighlightFlagged.IsChecked == true
    };

    private void ApplyProofingOptions(OptionsSettings s)
    {
        EdIgnoreUppercase.IsChecked = s.SpellIgnoreUppercase;
        EdIgnoreNumbers.IsChecked = s.SpellIgnoreNumbers;
        EdIgnoreInternet.IsChecked = s.SpellIgnoreInternetAddresses;
        EdFlagRepeated.IsChecked = s.SpellFlagRepeatedWords;
        EdFrenchAccented.IsChecked = s.SpellFrenchAccentedUppercase;
        EdMainDictOnly.IsChecked = s.SpellMainDictionaryOnly;
        SetComboText(EdFrenchCombo, s.FrenchMode);
        SetComboText(EdSpanishCombo, s.SpanishMode);
        EdCheckAsYouType.IsChecked = s.SpellCheckAsYouType;
        EdMarkGrammar.IsChecked = s.SpellMarkGrammarErrors;
        EdConfusedWords.IsChecked = s.SpellFrequentlyConfusedWords;
        EdGrammarWithSpelling.IsChecked = s.SpellGrammarWithSpelling;
        SetComboText(EdWritingStyle, "Grammar");
    }

    private OptionsSettings CollectProofingOptions(OptionsSettings s) => s with
    {
        SpellIgnoreUppercase = EdIgnoreUppercase.IsChecked == true,
        SpellIgnoreNumbers = EdIgnoreNumbers.IsChecked == true,
        SpellIgnoreInternetAddresses = EdIgnoreInternet.IsChecked == true,
        SpellFlagRepeatedWords = EdFlagRepeated.IsChecked == true,
        SpellFrenchAccentedUppercase = EdFrenchAccented.IsChecked == true,
        SpellMainDictionaryOnly = EdMainDictOnly.IsChecked == true,
        FrenchMode = ComboText(EdFrenchCombo, s.FrenchMode),
        SpanishMode = ComboText(EdSpanishCombo, s.SpanishMode),
        SpellCheckAsYouType = EdCheckAsYouType.IsChecked == true,
        SpellMarkGrammarErrors = EdMarkGrammar.IsChecked == true,
        SpellFrequentlyConfusedWords = EdConfusedWords.IsChecked == true,
        SpellGrammarWithSpelling = EdGrammarWithSpelling.IsChecked == true
    };

    private static string ComboText(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() is { Length: > 0 } text ? text : fallback;

    private static void SetComboText(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
            if ((combo.Items[i] as ComboBoxItem)?.Content?.ToString() == value)
            { combo.SelectedIndex = i; return; }
        combo.SelectedIndex = combo.Items.Count > 0 ? 0 : -1;
    }
}
