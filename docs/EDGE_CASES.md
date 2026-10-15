# Edge cases, caveats and the tests that guard them

One place for the edge cases OpenOutlook handles on purpose, why each one matters (often a real incident), where it is handled and which test keeps it from coming back. Also: what is checked only by hand, what is known to be missing, and the traps in the test environment itself.

**Keep it current.** When a fix adds a test for an edge case, add a row here in the same commit. `python scripts/check-edge-cases.py` fails when a cited test no longer exists or a test class is not mentioned here. When a case has no automated test, put it under [Checked by hand only](#checked-by-hand-only) or [Known gaps](#known-gaps), not nowhere. Test names are `Class.Method` in `tests/OpenOutlook.Tests` unless marked *(headless)* (`tests/OpenOutlook.HeadlessTests`) or *(native)* (`native/openpst/tests`). Dates are when the case was found.

Contents: [Reading pane: HTML safety](#reading-pane-html-safety) · [Pictures](#reading-pane-pictures) · [Text selection](#reading-pane-text-selection) · [Embedded web view](#reading-pane-embedded-web-view) · [PST engine and SCANPST](#pst-engine-and-scanpst) · [Export and attachments](#export-and-attachments) · [Copy, move and MIME](#copy-move-and-mime) · [Mailbox copy (mirror)](#mailbox-copy-mirror) · [Sign-in and tokens](#sign-in-and-tokens) · [Microsoft Graph](#microsoft-graph) · [Gmail](#gmail) · [Compose and send](#compose-and-send) · [Junk Cleaner](#junk-cleaner) · [Settings files](#settings-files) · [UI behaviour](#ui-behaviour) · [Linux and platform](#linux-and-platform) · [Checked by hand only](#checked-by-hand-only) · [Known gaps](#known-gaps) · [Test environment caveats](#test-environment-caveats)

---

## Reading pane: HTML safety

| Case | Why | Handled in | Test |
|---|---|---|---|
| Scripts, forms, active links and non-image data URIs never run or reach the network | Mail is untrusted | `SafeHtmlDocument.Build`, CSP | `SafeHtmlDocumentTests.LinksAllowWebAndMailButRejectActiveSchemes`, `.InteractiveDocumentRestoresOnlyApprovedLinkTargets`, `.Malformed_long_message_keeps_its_end_and_isolates_active_content` |
| Image sources on private / local networks are refused (no SSRF) | A picture URL could probe the LAN | `SafeRemoteImageLoader.TryAcceptUrl`, `IsPublicAddress` | `SafeHtmlDocumentTests.AcceptsHttpAndHttpsButRejectsPrivateImageSources`, `SafeHtmlPreviewTests.RemoteImagesUsePublicNetworkAddresses` |
| Redirects only to public hosts, bounded; oversized bodies stopped | Redirect to a private host or a huge file | `SafeRemoteImageLoader.FetchAsync` | `SafeHtmlPreviewTests.RemoteImageFetchRejectsRedirectsAndOversizedBodies`, `.RemoteImageFetchFollowsPublicRedirect` |
| Layout CSS survives sanitizing: border / radius / underline shorthands expanded to longhands, overflow, object-fit, `@media` | 2026-10-15: newsletters lost borders and rounded corners | `SafeHtmlDocument` longhand allowlist | `SafeHtmlDocumentTests.Shorthand_borders_rounded_corners_underlines_cropping_and_media_rules_survive_sanitizing`, `.KeepsTableLayoutAndColorsWithoutBrowserNetworkUrls`, `.KeepsAuthorCssAndBackgroundTogetherOnTheSameElement` |
| `cid:` table backgrounds and pictures inside mail links are kept | Common in bank / airline mail | `SafeHtmlDocument.Build` | `SafeHtmlDocumentTests.KeepsTableBackgroundImageFromCidReference`, `.KeepsApprovedImageInsideInertMailLink`, `.ReplacesOnlyApprovedImageAndCssBackgroundWithValidatedBytes` |
| A failed remote picture shows a fallback without its URL | The URL can be a tracker | `SafeHtmlDocument.Build` | `SafeHtmlDocumentTests.Failed_remote_image_has_visible_fallback_without_request_url` |
| The plain preview never activates markup and has bounded input | Fallback when no browser is installed | `SafeHtmlPreview` | `SafeHtmlPreviewTests.KeepsReadableFormattingWithoutActivatingMarkup`, `.RejectsUnboundedInput` |
| "Open in browser" copies are private (0600), pruned, bounded | Reading mail must not pile up copies of it | `BrowserDocumentWriter` | `BrowserDocumentWriterTests.*` (6) |

## Reading pane: pictures

| Case | Why | Handled in | Test |
|---|---|---|---|
| Requests carry a browser User-Agent and accept WebP | 2026-10-15: image hosts behind bot protection answered 403 (Steelers Now 5 of 5) | `SafeRemoteImageLoader.BrowserUserAgent`, `AcceptedImageTypes` | `SafeInlineImageTests.Picture_requests_carry_a_browser_user_agent_and_accept_webp`, `.AcceptsWebPAndReadsItsSizeFromEveryHeaderKind` |
| Only PNG / JPEG / GIF / WebP; declared size checked before decode | A small file can declare a huge bitmap | `SafeInlineImage.Validate` | `SafeInlineImageTests.RejectsFormatsThatAreNotPngGifOrJpeg`, `.RejectsDeclaredSizesBeyondThePerImageCeiling`, `SafeHtmlPreviewTests.EmbeddedImageDimensionsAreBoundedBeforeDecode` |
| Per-message decoded-pixel budget, reserved up front and thread-safe | Many small files that each declare a giant bitmap | `DecodedPixelBudget` | `SafeInlineImageTests.PixelBudgetRefusesManySmallFilesThatEachDeclareAHugeBitmap`, `.ConcurrentReservationsNeverOversubscribeTheBudget`, `.BudgetIgnoresNonPositiveReservations`, `.OrdinaryMessageFitsThePixelBudgetEntirely` |
| Camera-size pictures decoded at 1600 px; budget is a Trust Center choice (64 / 128 / 256 MP) | 2026-10-15: two photos (5700x2560, 4914x3276) pushed a Delta mailing over budget | `SafeInlineImage.PreviewSize`, `OptionsSettings.TrustPictureMemoryMegapixels` | `SafeInlineImageTests.Camera_size_pictures_are_decoded_at_the_preview_width_so_a_newsletter_fits_the_message_budget`, `.The_trust_center_picture_memory_choice_sets_the_message_budget` |
| Web fonts (`@font-face`, .woff/.ttf/...) are not pictures | 2026-10-15: a .woff counted as "could not be loaded" | `SafeHtmlDocument.FindImages` | `SafeHtmlDocumentTests.Web_fonts_are_not_counted_as_pictures` |
| Hidden images (display:none, width/height 0, `hidden`) are not fetched | Tracking | `IsHiddenImage` | `SafeHtmlDocumentTests.HiddenTrackingImagesAreNotFetchedOrCountedAsMissingPictures` |
| 1 x 1 images (attributes or inline px) are tracking pixels: not fetched, not counted | 2026-10-15: an American Express tracker took 8 s per 47-byte pixel | `IsTrackingPixel` | `SafeHtmlDocumentTests.OneByOneTrackingPixelsAreNotFetched` |
| Style-sheet pictures count only in rules that can show (`@media print`, rules matching no element are skipped) | Same AmEx mail: "printed" and "forwarded" beacons | `ScreenRulesWithPictures` | `SafeHtmlDocumentTests.StyleSheetPicturesThatCannotShowAreNotFetched` |
| A style sheet wrapped in `<!-- -->` (and CSS comments) is scanned like a bare one | 2026-10-15: the AmEx body as the app reads it has `<style><!-- ... --></style>`; `<!-- @media print` read as a selector, which counts as matching, so the print beacon was fetched (8 s, "one image could not be loaded") | `ScreenRulesWithPictures` | `SafeHtmlDocumentTests.Html_comment_markers_and_css_comments_around_a_style_sheet_do_not_hide_print_rules` |
| A style-sheet picture counts only if a matching element can show a background (has content, or a width / height / padding) | AmEx's beacons target `<div id="_two50"></div>` | `CanShowBackground` | `SafeHtmlDocumentTests.A_style_sheet_picture_for_an_empty_element_with_no_size_is_not_fetched` |
| Every picture that is not shown is logged with host, path and origin (img, style sheet rule + selector, style / background attribute); the query string is left out (tracking ids) | Diagnosing "one image could not be loaded" | `MainWindow.DescribePictureSource`, `HtmlImageSource.Origin` | origin covered by the two tests above; log lines **manual** (`~/.local/share/OpenOutlook/logs/openoutlook.log`, "picture not loaded" / "picture not shown") |
| Pictures show as they arrive (6 at a time, first layout after 1.2 s); one slow server does not hold back the rest; a slow picture that fails needs no second layout; switching message or cancelling stops without a layout | AmEx: everything waited for the slowest picture | `ProgressivePictureLoader`, `MainWindow.LoadImagesAsync`, `FirstPaintDelay` | `ProgressivePictureLoaderTests.*` (7; slow pictures are test-controlled, no timing dependence); also checked in the app on the AmEx message (all pictures within ~3 s) |

## Reading pane: text selection

| Case | Why | Handled in | Test |
|---|---|---|---|
| Chrome reports every visible character with its edges; collapsed spaces dropped, hidden text skipped, `<br>` and pre newlines kept, cells marked | Character selection on the snapshot (Wayland has no embedded view) | `BrowserHtmlRenderer.TextRunScript` | `BrowserHtmlRendererTests.MeasuresEveryCharacterForSelection` (real Chrome; skipped silently if none is installed) |
| Chrome's geometry is validated: non-finite, off-page, wrong edge counts, bad block numbers dropped; 400,000 characters / 60,000 runs cap | Numbers come from an untrusted document | `ReadTextRuns` | `TextOverlayGeometryTests.*` (6) |
| Copy keeps paragraph breaks, tabs between table cells, a space where whitespace collapsed, nothing inside a wrapped link | Copies must read like a browser's | `SnapshotText.Separator` | `SnapshotTextTests.JoinsRunsLikeACopyFromABrowser`, `.CopyUsesPlatformLineEndingsAndTabsBetweenCells`, `.HardBreaksBecomeNewLinesAndWrappedWordsStayWhole` |
| Caret at the nearest character; in margins / between lines it goes to the nearest line | Drags wander | `SnapshotText.CaretAt` | `SnapshotTextTests.CaretLandsOnTheNearestCharacterBoundary`, `.CaretBetweenLinesGoesToTheNearestLine`, `.CopyReturnsTheExactCharacterRange` |
| Double-click word (also right after a word), triple-click paragraph | Outlook behaviour | `WordAt`, `BlockAt` | `SnapshotTextTests.DoubleClickWordAndTripleClickParagraph`, `.WordAtTheEndOfAWordSelectsThatWord` |
| Emoji (surrogate pairs) are never split | A half pair is garbage on the clipboard | `SnapshotText.Snap` | `SnapshotTextTests.EmojiAreNeverSplit` |
| Highlight per line, only the selected characters, per tile band | Tiles are separate bitmaps | `Highlights` | `SnapshotTextTests.HighlightsCoverOnlyTheSelectedCharactersOfEachLine` |
| One selection shared by all tiles of a message | Drag across tile joins; Ctrl+C anywhere | `SnapshotSelection` | `SnapshotTextTests.SharedSelectionReportsChanges` |
| Very long mail: per-word measuring after 2.5 s | 183,000 characters measured in ~2 s | `TextRunScript` | **No automated test**: MailSmoke `selprobe <file.html>` |
| Links take clicks first (a drag must start beside a link); RTL caret approximate; `text-transform` copies source text | Accepted limits | `HtmlPageView` | n/a (documented in `DESIGN_SPEC.md`) |

## Reading pane: embedded web view

| Case | Why | Handled in | Test |
|---|---|---|---|
| The link guard lets the reader's own document through exactly once (`data:` from NavigateToString, or the exact private file) and blocks everything else | 2026-10-15: the guard cancelled WebView2's own `data:` page, so Windows fell back to block selection | `OwnDocumentNavigation` | `OwnDocumentNavigationTests.*` (3) |
| Message colours are shown as written, like Outlook: WebView2 gets `--force-color-profile=srgb` (Options > Mail > "Show message colors exactly as the sender wrote them", on by default, applies after restart); the snapshot browser always gets it | 2026-10-15: Chromium colour-managed through the LG 32UHD144 profile: #FFFFFF -> #FFFDEF, #D9D9D6 -> #E3D7C8, #006FCF -> #4C6CBE | `ReaderColorProfile`, `Program.Main`, `BrowserProcessTracker` | `ReaderColorProfileTests.*` (3); on-screen colours **manual**: MailSmoke `colorprobe` (Windows; set `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` to compare) |
| Messages over ~2 MB load from a private temp file (WebView2 string limit) | 6.9 MB Delta mailing | `TryShowEmbeddedHtmlAsync`, `BrowserDocumentWriter` | `OwnDocumentNavigationTests.A_large_message_file_is_allowed_only_for_the_exact_path_and_only_once`; the WebKitGTK file path is **not tested** |
| Wayland starts on the snapshot; `OPENOUTLOOK_READER=embedded|snapshot` forces | WebKitGTK cannot composite into an Avalonia window under Wayland | `ReaderEngineChoice` | `ReaderEngineChoiceTests.Chooses_the_renderer_a_message_starts_on` |
| Each navigation has a unique page marker; the view stays hidden until it is verified | Rapid selection showed the previous message | `MainWindow` reader | `MainWindowHeadlessTests.ConsecutiveReaderNavigationsHaveDistinctPageMarkers` *(headless)* |
| The native view (paints above Avalonia) is hidden under the File screen and Options | It covered dialogs | `InitializeWebViewOverlayGuard` | `MainWindowHeadlessTests.Native_web_view_is_hidden_while_the_file_screen_or_options_are_open` *(headless)* |

## PST engine and SCANPST

Format rules 1-40 (what SCANPST requires) are in `docs/OpenOutlook_Design_Document.md` sections 3.4 and 15; fixer rules R1-R12 in section 6.6. They are guarded by the native checker (`opst_check`), byte-identity runs against the Python reference (`tests/pyops.py`), the fixer test (`tests/pydamage.py`) and SCANPST runs on written files (`tools/python/run_scanpst2.ps1`).

| Case | Why | Handled in | Test |
|---|---|---|---|
| Read-only handles refuse every write; a second writer gets read-only | Never corrupt an archive another process holds | engine lock | `PstEngineContractTests.Second_writer_is_refused_while_the_first_holds_the_lock_and_gets_read_only_instead`, `.Native_engine_write_members_fail_closed`, `PstImportTests.Importing_into_a_read_only_engine_fails_closed`, `test_write.c` *(native)* |
| Edits leave the integrity check unchanged; read / flag changes written behind in batches survive close | | engine | `PstEngineContractTests.Native_edits_are_persisted_and_leave_the_integrity_check_unchanged`, `.Read_and_flag_changes_are_written_behind_in_batches_and_survive_close` |
| Crash during a write rolls back byte for byte | Power loss | journal | `test_write.c` crash-recovery section *(native)* |
| ANSI and 4K-page files are read and stay read-only | Old Outlook formats | engine | `PstEngineFormatTests.*` |
| Very long property values are stored and the file stays clean | | data trees | `PstBigValueTests.Very_long_values_are_stored_and_the_file_stays_clean` |
| A long display-to (> ~2 KB) is one data tree shared by the message and its row (rule 39, cRef 3) | 2026-10-15: SCANPST minor on big recipient lists | `op_edit2.c` `tcbig_put` | MailSmoke `mkbigrecips` + SCANPST (**manual**) |
| A heap past 8 blocks gets HNBITMAPHDR blocks at 8, 136, ... (66-byte header; rule 40) | 2026-10-15: mailbox-copy sync failed on a big message index | `op_edit.c` `heap_append_item` | soak / SCANPST (**manual**); no unit test |
| A new PST is a clean empty store; never overwrites | | `opst_create` | `PstCreateTests.*` (5) |
| Imported messages read back complete; scan works read-only, repair needs write | | importer | `PstImportTests.*` (5) |
| Checker catches the importer's old defects (padded attach size, NID counters, missing row cells, AMap bits, incomplete folders) | SCANPST found them first | `opst_check` / `opst_fix` | `test_write.c` (test hooks `OPST_TEST_BAD_ATTSIZE`, `..._BAD_NIDCOUNTER`, `..._NO_ROWCELLS`) *(native)*, `pydamage.py` |
| Damaged RTF / HTML bodies never crash the reader | Corrupt archives | RTF decoder | `fuzz_rtf.c` (16,000 bodies) *(native)* |
| Mailbox wrapper folder hidden, its mail folders and non-empty system folders reachable | | `PstFolderPresentation` | `PstFolderPresentationTests.*` |
| Recipients / headers views: no duplicates, fall back to summary or stored properties, hidden attachments not listed | Message Properties sheet | `PstMessageHeader` | `PstMessageHeaderTests.*` (6), `MainWindowHeadlessTests.Header_helpers_cut_the_header_block_and_unfold_values` *(headless)* |

## Export and attachments

| Case | Why | Handled in | Test |
|---|---|---|---|
| Never overwrite; unsafe names and symlink destinations (even dangling) refused; cancel leaves no partial or temp file | Writing into the user's folders | `PstAttachmentExporter`, `PstMessageEmlExporter`, `PstFolderEmlExporter` | `PstAttachmentExporterTests.*` (12), `PstMessageEmlExporterTests.*` (11), `PstFolderEmlExporterTests.*` (4) |
| EML headers cannot be injected; legacy unquoted comma in a sender name is encoded | | `PstMessageEmlExporter` | `PstMessageEmlExporterTests.RejectsHeaderInjectionAndLeavesNoFile`, `.EncodesLegacyUnquotedCommaInSenderDisplayName` |
| Cyclic folder trees and duplicate message ids fail closed | Damaged archives | `PstFolderEmlExporter` | `PstFolderEmlExporterTests.CyclicFolderTreeAndDuplicateMessageIdsFailClosed` |
| PST attach size may include object overhead; missing metadata length still works | Real Outlook files | `PstAttachmentExporter` | `PstAttachmentExporterTests.AllowsPstAttachmentSizeToIncludeObjectOverhead`, `.MissingMetadataLengthStillPermitsNonemptyData` |
| Each opened attachment gets its own empty folder | Name clashes, leftovers | `AttachmentLauncher` | `AttachmentLauncherTests.Each_opened_attachment_gets_its_own_empty_folder` |
| EML import tolerates sloppy input, rejects non-mail | | `EmlParser` | `EmlParserTests.*` (5) |

## Copy, move and MIME

| Case | Why | Handled in | Test |
|---|---|---|---|
| A move removes each original only after its chunk is committed; a failed removal stops the move | No lost mail, no piles of duplicates | `MailTransfer` | `MailTransferTests.*` (6) |
| A message the builder cannot represent (embedded item, unreadable attachment) stays in the source | Never drop data silently | `MimeMessageBuilder` | `MimeMessageBuilderTests.An_embedded_item_or_unreadable_attachment_stops_the_transfer_instead_of_dropping_data`, `PstTransferEndToEndTests.A_message_the_builder_cannot_represent_stays_in_the_source_and_the_rest_still_move` |
| Unicode in every header, Cc/Bcc, a display name with a comma, threading headers survive; other headers are not copied; no header injection | | `MimeMessageBuilder` | `MimeMessageBuilderTests.*` (6) |
| PST to PST copy/move keeps both files clean | | engine copy | `PstTransferEndToEndTests.*`, `PstImportTests.Messages_copy_between_two_open_archives_and_a_move_removes_the_originals`, `TransferHeadlessTests.*` *(headless)* |
| Gmail import keeps labels and Date header, adds UNREAD once; Graph import marks read unless unread | | `MailboxImport` | `MailboxImportTests.*` (5) |
| Flags / categories do not travel through MIME; embedded messages cannot go to a mailbox; the mailbox copy is not a destination | Known limits | `ExecuteTransferAsync` | n/a (see [Known gaps](#known-gaps)) |

## Mailbox copy (mirror)

| Case | Why | Handled in | Test |
|---|---|---|---|
| First sync builds the tree; a second sync without changes downloads nothing; server changes arrive and the copy stays clean | | `MirrorSync` | `MirrorSyncTests.*` (7) |
| Times from the copy keep their instant (the engine hands out local times; relabelling them as UTC showed Hotmail mail 7 h early in Pacific daylight time) and the server's received time wins over the message headers, as in Outlook | 2026-10-15: 3:16 PM shown as 8:16 AM since 2026-10-05; some messages a minute early | `LocalMailboxReader.ToInstant`, `MirrorSyncEngine` | `MirrorSyncTests.Engine_times_are_local_and_keep_their_instant`, `.The_local_copy_answers_a_folder_in_the_shape_of_the_server_list` (the shift only shows on a machine whose zone is not UTC: CI runs in UTC) |
| Messages older than the keep window are not mirrored and age out; a huge message is skipped once; failing downloads retried | | `MirrorSync` | `MirrorSyncTests.Messages_older_than_the_keep_window_are_not_mirrored_and_age_out`, `.A_failing_download_is_retried_next_time_and_a_huge_message_is_skipped_once` |
| Folder-name clash gets a suffix | | `MirrorSync` | `MirrorSyncTests.A_folder_name_that_clashes_gets_a_suffix` |
| Local changes reach the server; on a conflict the server wins; offline changes wait; refused changes are counted and retried | | `MirrorPush` | `MirrorPushTests.*` (7) |
| Gmail: labels become folders, a message in several labels is downloaded once per run, permanent delete remembered but not sent, read-only sign-in pushes nothing | | `MirrorGmail` | `MirrorGmailTests.*` (7) |
| 429 is retried after a pause; a 403 that is not about the rate limit is not | | `MirrorGmail` | `MirrorGmailTests.A_rate_limited_request_is_repeated_after_a_pause`, `.A_403_that_is_not_about_the_rate_limit_is_not_repeated` |
| Settings defaults (on, 12 months, 25 MB); cloud-synced folders warned about; relative paths refused; a failed transaction changes nothing | | `MirrorSettings`, `MirrorState` | `MirrorTests.*` (9) |
| Turning a copy back on starts its sync | 2026-10-15 | `MainWindow` | **No automated test** (checked in the app) |

## Sign-in and tokens

| Case | Why | Handled in | Test |
|---|---|---|---|
| PKCE S256, pinned endpoints, state mismatch consumes the attempt (no replay), loopback callback bound and bounded | OAuth safety | `DesktopOAuth*`, `LoopbackAuthorizationCallback` | `DesktopOAuthTests.*`, `DesktopOAuthCoordinatorTests.*`, `LoopbackAuthorizationCallbackTests.*` |
| Errors never reveal tokens or provider responses | Logs and status bar | everywhere | `DesktopOAuthRefreshTests.RedirectAndProviderErrorsDoNotRevealTokens`, `AccountConnectionTests.RedirectAndTransportErrorsAreSanitized`, `SecretStoreTests.UnavailableStoreFailsClosedAndNeverLeaksSecretInError` |
| A rotated refresh token is stored before the access token is used; storage failure releases nothing | Lose the token = sign in again | `OAuthAccountRefresh` | `OAuthAccountRefreshTests.*` (4) |
| The Google client secret goes only to Google; loaded at startup for refresh | | `GoogleClientSecret` | `GoogleClientSecretTests.*` |
| Missing configuration never invents application ids | | `OAuthClientConfiguration` | `OAuthClientConfigurationTests.*` |
| Only provider authorization pages open in the system browser | | `SystemAuthorizationBrowser` | `SystemAuthorizationBrowserTests.OnlyProviderAuthorizationPagesAreAccepted` |
| Account registry: no tokens in it, corrupt file not overwritten, limits per provider, older Microsoft account read-only until new consent | | `ConnectedAccountRegistry` | `ConnectedAccountRegistryTests.*` (4) |
| Browser callback, identity confirmation and secret storage run in order; missing scopes stop before the browser; a saved refresh token works in a new session | | `DesktopAccountConnector`, `MicrosoftMailSession` | `DesktopAccountConnectorTests.*` (3), `MicrosoftMailSessionTests.SavedRefreshTokenWorksAcrossNewDesktopSessions` |
| Secret stores (Windows Credential Manager, libsecret) reject bad input and do nothing when cancelled | | `*SecretStore` | `WindowsCredentialSecretStoreTests.*`, `LibsecretSecretStoreTests.*` |

## Microsoft Graph

| Case | Why | Handled in | Test |
|---|---|---|---|
| Every call checks that the token belongs to the expected account (cached once per token when the cache is on) | A token for another account must not read or change mail | `GraphAccountVerification` | `GraphAccountVerificationTests.*`; the "wrong account stops first" test in every reader / writer class |
| Pagination and redirects to another host are refused; the bearer token is never sent to a redirect | | all Graph clients | `GraphInboxReaderTests.RedirectIsRejectedWithoutFollowingBearerToken`, `GraphCalendarClientTests.RejectsCalendarPaginationToAnotherHost`, `GraphAttachmentTests.UnsafeNextPageAndRedirectAreRejected`, `GraphContactsClientTests.Refuses_bad_contacts_and_unsafe_page_links` |
| Permanent delete checks the real Deleted Items folder and refuses a message moved out of it | | `GraphMailWriter` | `GraphMailWriterTests.ChecksActualTrashFolderBeforePermanentDelete`, `.PermanentDeleteRefusesMessageMovedOutOfTrash`, `GraphMailWriterEmptyDeletedItemsTests.*` |
| Folder listing paginates safely; move / copy / create folder refuse bad names and ids before any request | | `GraphMailFolderReader`, `GraphMailWriter` | `GraphMailFolderReaderTests.*` (3), `GraphMailWriterMoveCopyTests.*` (3) |
| Batch delete reports partial completion without retrying the done ones | | `GraphMailBatchDeleter` | `GraphMailBatchDeleterTests.*` |
| Deletion decisions are provider-independent; a queued move never escalates to a permanent delete | | `DeletionPolicy` | `DeletionPolicyTests.*` (6) |
| Large attachments use the pre-authenticated upload without the bearer token | | `GraphMailWriter` | `GraphMailWriterTests.LargeAttachmentUsesPreauthenticatedUploadWithoutBearerToken` |
| Editing a draft keeps its quoted HTML body | | `GraphMailWriter` | `GraphMailWriterTests.ReadsOriginalHtmlDraftAndUpdatesWithoutLosingQuotedBody` |
| Throttling reports a bounded retry | | `GraphJunkMailReader` | `GraphJunkMailReaderTests.Throttling_reports_bounded_retry_without_exposing_response_or_token` |
| All-day events use midnight in the calendar's time zone; calendar write is Microsoft-only and needs reconnect | | `GraphCalendarClient` | `GraphCalendarClientTests.*` (5), `CalendarPageHeadlessTests.*` *(headless)* |
| Contacts: default folder first, every page read, a missing photo reads as none; File As, sort keys, initials and avatar colours follow Outlook | People phase 1 | `GraphContactsClient`, `ContactFormat` | `GraphContactsClientTests.*`, `ContactFormatTests.*` (5), `PeoplePageHeadlessTests.*` *(headless)* |
| HTML bodies are not shown as display text; raw MIME only for the verified account | | `GraphInboxReader` | `GraphInboxReaderTests.*` (7) |

## Gmail

| Case | Why | Handled in | Test |
|---|---|---|---|
| Profile verified once per token; a token for another account is refused | | `GmailMailbox` | `GmailMailboxTests.*`, `GmailInboxReaderTests.Different_account_stops_before_listing_or_fetching` |
| A repeated page token is rejected; oversized responses rejected; errors never expose body or bearer | Endless loops, memory | `GmailInboxReader` | `GmailInboxReaderTests.*` (10) |
| Invalid ids and labels refused before any request; large batches split | | `GmailMailbox`, actions | `GmailMailboxTests.Rejects_invalid_ids_and_labels_before_any_request`, `GmailMailboxActionTests.Large_batches_are_split_and_invalid_input_is_refused_before_any_request` |
| Only accounts with the modify scope may change mail; a refused change says to sign in again | | actions | `GmailMailboxActionTests.Only_accounts_connected_with_the_modify_scope_may_change_mail`, `.A_refused_change_explains_that_the_account_must_sign_in_again` |
| Gmail folders and actions in the window; real drag and drop moves; labels copy | | `MainWindow` | `MainWindowHeadlessTests.Gmail_account_appears_with_its_standard_folders_and_survives_selection`, `.Gmail_actions_change_the_server_and_the_list`, `.Gmail_reply_reply_all_and_forward_are_prepared_from_the_original`, `.Dropping_messages_on_a_Gmail_folder_moves_them_and_adding_a_label_copies`, `.Real_mouse_and_drop_events_move_a_Gmail_message_onto_a_folder` *(headless)* |

## Compose and send

| Case | Why | Handled in | Test |
|---|---|---|---|
| Header line breaks refused; non-ASCII round-trips; oversized attachments refused; a sender is required | Header injection, 25 MB limit | `GmailMimeBuilder` | `GmailSendTests.*` (9) |
| Composed HTML keeps fonts, mailto links, pasted pictures (data URIs), signature tables; writes colours as hex (Outlook ignores rgba) | Recipients use Outlook | `ComposeHtml` | `ComposeHtmlTests.*` (14) |
| Form controls that could fake a credential prompt are removed; non-image or oversized data URIs dropped; sanitizing is idempotent | | `ComposeHtml` | `ComposeHtmlTests.RemovesFormControlsThatCouldFakeACredentialPrompt`, `.DropsDataUrisThatAreNotImages`, `.DropsOversizedDataUris`, `.IsIdempotentBecauseItRunsOnInputAndAgainOnApply` |
| Unbuilt buttons are marked; From locks after a draft is saved | | `ComposeWindow` | `MainWindowHeadlessTests.Compose_window_marks_unbuilt_buttons_locks_from_after_a_draft_and_sends_seed_files` *(headless)* |

## Junk Cleaner

| Case | Why | Handled in | Test |
|---|---|---|---|
| Nothing runs without an explicit per-account opt-in; a missing config creates nothing | Moving mail is an outward action | `JunkCleanerSettingsStore` | `JunkCleanerSettingsTests.Missing_config_does_not_create_file_or_enable_accounts`, `MicrosoftJunkPreviewMatcherTests.Requires_explicit_opt_in_and_same_bound_account` |
| Invalid / damaged config (or keyword file) is never overwritten | 2026-10-15: keywords disappeared | `JunkCleanerSettingsStore`, `JunkKeywordFile` | `JunkCleanerSettingsTests.Invalid_existing_config_is_not_replaced_by_load`, `.A_damaged_keyword_file_throws_instead_of_being_overwritten`, `.Keyword_file_is_one_comma_separated_line_and_reads_back` |
| Unknown To / sender / importance never triggers optional rules | | matcher | `MicrosoftJunkPreviewMatcherTests.*`, `UnitTest1.*` (keyword matcher) |
| Scan changes nothing; clean moves only matches, at most the cap; one failed move does not stop the rest | | `MicrosoftJunkCleanerRunner` | `MicrosoftJunkCleanerRunnerTests.*` (6) |

## Settings files

All settings stores share the same rules: a missing file gives defaults, a corrupt or oversized file gives defaults (or is refused) without being overwritten, out-of-range values are clamped, saves are atomic and leave no temp files, control characters and junk keys are dropped.

| Store | Test |
|---|---|
| Options (incl. picture memory) | `OptionsSettingsStoreTests.*` (7) |
| Folder order | `FolderPaneOrderStoreTests.*` (10) |
| Read state | `ReadStateStoreTests.*` (9) |
| View layout (columns, panes, window geometry) | `ViewLayoutSettingsTests.*` (3) |
| Shortcuts (invalid or duplicate cannot override) | `ShortcutSettingsTests.*` |
| Attached PSTs (survive restart, invalid paths refused without opening) | `AttachedPstSettingsStoreTests.*` |
| Offline message cache (private files, symlinks refused, insecure folders never "repaired") | `OfflineMessageCacheTests.*` (7; 5 fail on Windows, see caveats) |

## UI behaviour

| Case | Handled in | Test *(all headless)* |
|---|---|---|
| Deleting the highlighted message highlights the one taking its place and clears the reader | `MainWindow` | `MainWindowHeadlessTests.Deleting_the_highlighted_message_highlights_the_one_that_takes_its_place`, `.ReplacementHighlightClearsDeletedMessageFromReader`, `.TwoMicrosoftDeleteKeysKeepSelectionHeaderAndBodyTogether` |
| The chosen sort survives switching folders; arrange-by chip follows the menu | | `.The_chosen_sort_survives_opening_another_folder_and_coming_back`, `.Arrange_by_chip_follows_the_arrangement_and_the_menu_choices_group_and_sort` |
| Space pages the message, then moves to the next | | `.Space_bar_pages_the_message_then_moves_to_the_next_one` |
| Right-click menu exists before any folder loaded; Properties on folders, data files and messages | | `.Right_click_menu_exists_before_any_archive_folder_has_loaded`, `.Folder_right_click_menu_offers_Properties_on_folders_and_data_files`, `.Message_right_click_menu_ends_with_Properties_and_the_sheet_shows_the_internet_headers` |
| Ribbon squeezes groups into drop-downs as the window narrows; reader actions stay inside a narrow pane; portrait layout | | `.The_ribbon_squeezes_its_groups_into_drop_downs_as_the_window_narrows`, `.ReaderActionsStayInsideNarrowReadingPane`, `.Portrait_window_gives_the_whole_width_to_the_reading_pane_and_back_restores_it` |
| Online drag payload rejects foreign text | Drops from other apps | `.Online_drag_payload_round_trips_and_rejects_foreign_text` |
| Messages without a date go to an undated group | | `MessageDateGroupsTests.Missing_message_date_is_kept_in_an_undated_group` |
| Sync indicator: worst state wins, offline counts waiting changes | | `SyncIndicatorTests.*` |
| Crash notice includes inner exceptions and bounds the stack trace | | `CrashNoticeTests.*` |

## Linux and platform

| Case | Why | Handled in | Test |
|---|---|---|---|
| Bundled chrome-headless-shell found beside the app and listed first; old profiles cleaned | Pinned renderer | `BrowserHtmlRenderer.BundledBrowsers` | `BundledBrowserTests.*` |
| WSLg: drives added to the file dialog once; primary monitor read from the WSLg log | Windows opened on the wrong monitor | WSL helpers | `BundledBrowserTests.WSL_drives_are_added_to_the_file_dialog_sidebar_once_and_other_bookmarks_stay`, `.The_primary_monitor_origin_comes_from_the_newest_monitor_list_of_the_WSLg_log` |
| Ubuntu 23.10+ AppArmor blocks the browser sandbox (SIGTRAP) | 2026-10-06 bare metal | deb ships `packaging/openoutlook-chromium.apparmor` | **Manual** (deb install); the tar.gz has no profile |
| Single-file Linux build must ship the native PST library | 2026-10-05: "native PST library is not available" | `package-linux-x64.sh` | `PstEngineContractTests.Native_library_loads_when_present_and_matches_expected_version`; packaging itself **manual** (deb smoke) |
| WebKitGTK loads NavigateToString as `about:blank` (not `data:`) | Why the Windows guard bug never hit Linux | `OwnDocumentNavigation` | MailSmoke `wvprobe guard` under WSLg (**manual**) |

---

## Checked by hand only

These have no automated test; they were verified in the app or with a tool, and need re-checking when the code around them changes.

- **Real-server picture timing** (AmEx message, Windows, 2026-10-15; the scheduling itself is unit tested). Tool: MailSmoke `imgprobe <pst copy> <subject>` prints each fetch's time.
- **Snapshot character selection in the app**: drag, double/triple-click, right-click Copy on the Google Cloud message with `OPENOUTLOOK_READER=snapshot` (Windows, 2026-10-15). **Not yet tried on the bare-metal Wayland desktop.**
- **WebView2 character selection** and the > 2 MB file path (Google Cloud and 6.9 MB Delta messages, 2026-10-15).
- **SCANPST verdicts** on files the engine writes (big recipient lists, imports, mirrors): `tools/python/run_scanpst2.ps1`, repair-and-diff until two passes agree (rule 26).
- **Mailbox copy turned back on starts its sync**; **Hotmail live copy/move** (owner, 2026-10-08).
- **Linux bare-metal checklist** (`docs/handoff-linux-bare-metal-2026-10-06.md` section 3): window behaviour, GTK dialogs, keyring sign-in, xdg-open, HiDPI.
- **Timing**: MailSmoke `selprobe` (render + character measurement), `timing`, `gmailtiming`.

## Known gaps

- **Bcc recipients give a real SCANPST error** in files the importer writes (deferred; repro with MailSmoke `mkbigrecips` `1:bccL`).
- A single recipient *name* of 5,000+ characters still gives a SCANPST error (not a real-world case).
- No checker/fixer rule for PSTs written with the old independent display-to copy (recreate the mailbox copy).
- Heap bitmap blocks (rule 40) have no unit test; only soak + SCANPST.
- Flag status and categories do not travel through MIME; messages with an embedded message cannot go to a mailbox; moving out of a read-only data file is refused (copy works).
- Gmail live copy/move, calendar invitations, Linux calendar, People against the live account: unverified.
- WebKitGTK file navigation for > 2 MB messages (X11 Linux): untested.

## Test environment caveats

- **Xvfb proves nothing about the embedded web view or the clipboard.** Under Xvfb X11 reparenting works (the web view looks fine) and Avalonia's clipboard reports success but serves nothing. Neither is evidence for a real desktop. (`docs/reading-pane-text-selection.md`)
- **Headless test host**: `NativeWebView` fails to start (swallowed, as in the app); tests that only need construction do not `Show()` the window. "Unable to initialize GTK" in the Xvfb smoke is expected.
- **Headless input**: `MouseMove` needs `RawInputModifiers.LeftMouseButton` to count as a held button; drags need `DragEnter` before `DragOver`.
- **Five `OfflineMessageCacheTests` fail on Windows** (`PlatformNotSupportedException`, a Unix-only API in setup); they pass on Linux. The Windows baseline is "N pass + 5 known".
- **`GraphAccountVerification.CacheEnabled` is a static switch**: its tests run in a non-parallel collection; a test that relies on the cache being off or on must join that collection, or Graph tests become flaky.
- **Time-zone bugs are invisible on a UTC machine** (CI, most containers): a test of local-time handling only fails where the zone has an offset (the owner's PC). Run time tests on Windows too.
- **Real-Chrome tests return early when no browser is installed** (`BrowserHtmlRendererTests`): on such a machine they pass without checking anything.
- **Native tests need a real small PST**: `OPST_TEST_PST=small.pst ctest`; headless tests need `OPENOUTLOOK_TEST_PST`. Always copies, never the owner's files.
- **MailSmoke live steps touch the real accounts** and `sendtest` sends real mail (it happened four times on 2026-10-15 when an unknown step fell through). Only offline steps without the owner's say-so; confirm `0 Error(s)` before running a newly added step; run WebView probes as `MailSmoke.exe`.
- **The deb smoke waits for the self-test line, not a fixed time**: a CI runner once needed 20 s to start the single-file program (run 38814882482).
- **dotnet keeps stdout pipes open** after build/test (msbuild server): piping into `grep` can look hung on Linux; use `DOTNET_CLI_USE_MSBUILD_SERVER=0` and redirect to a file.
- **Two drag systems in the folder pane** (folder reorder, message drops): a handler must not touch drags that are not its own.
- **Editing sources on Windows**: never with PowerShell 5.1 `Get-Content`/`Set-Content` (UTF-8 read as ANSI); Bash heredocs collapse backslashes, so write scripts with the Write tool.
