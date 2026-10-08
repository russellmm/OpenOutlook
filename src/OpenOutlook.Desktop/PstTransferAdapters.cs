using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstCore;

namespace OpenOutlook.Desktop;

internal sealed class PstTransferSource(IPstEngine store, IReadOnlyDictionary<string, MailSummary> messages) : ITransferSource
{
    public Task<byte[]> ReadMimeAsync(TransferItem item, CancellationToken ct) => Task.Run(() =>
    {
        var summary = messages[item.Id];
        var message = store.OpenMessage(summary);
        return MimeMessageBuilder.Build(message, a => store.ReadAttachmentData(summary, a, (int)PstAttachmentExporter.DefaultMaximumBytes, ct));
    }, ct);

    /// <summary>A move out of a PST removes the message for good, as every PST-to-PST move does; the copy is already committed in the destination.</summary>
    public Task RemoveAsync(IReadOnlyList<TransferItem> items, CancellationToken ct) => Task.Run(() =>
    {
        foreach (var item in items) store.DeleteMessage(messages[item.Id]);
    }, ct);
}

internal sealed class PstTransferSink(IPstEngine store, MailFolder folder) : ITransferSink
{
    private readonly List<MailImport> _pending = [];

    public Task AddAsync(byte[] mime, TransferItem item, CancellationToken ct)
    {
        _pending.Add(EmlParser.Parse(mime, markRead: item.IsRead));
        return Task.CompletedTask;
    }

    /// <summary>One engine call: the whole chunk is one atomic, journaled transaction, so a failure leaves the file as it was.</summary>
    public async Task CommitAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        _pending.Clear();
        await Task.Run(() => store.ImportMessages(folder, batch, null, ct), ct);
    }
}
