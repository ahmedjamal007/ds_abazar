using System.Data.SQLite;
using Dawaii.Core.Bot;
using Dawaii.Core.Data;
using Dawaii.Core.Models;
using Erp.TelegramBot.Commands;
using Erp.TelegramBot.Configuration;
using Erp.TelegramBot.Pharmacy;
using Erp.TelegramBot.Security;
using Erp.TelegramBot.Telegram;
using Erp.TelegramBot.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// The polling loop — the one place where "who is asking" is decided before the pharmacy's data is
/// touched.
///
/// Everything below it is already tested in isolation: the parser, the authorizer, both rate
/// limiters, the router, every report. What is NOT testable anywhere else is the ORDER they run in,
/// and the order is the security property. A price lookup that ran before the authorization check
/// would answer a stranger, and every unit test underneath would still be green.
///
/// So this drives the real worker through a fake Telegram, and asks what came back out.
/// </summary>
[TestFixture]
public class CommandWorkerTests
{
    /// <summary>A Telegram that hands over one batch and then goes quiet.</summary>
    private sealed class FakeTelegram : ITelegramGateway
    {
        public readonly List<Incoming> Inbox = [];
        public readonly List<(long ChatId, string Text)> Sent = [];
        public readonly List<string> Documents = [];

        private readonly TaskCompletionSource _drained = new();
        public Task Drained => _drained.Task;

        private bool _delivered;

        public Task<string> WhoAmIAsync(CancellationToken ct) => Task.FromResult("DawaiiTestBot");

        public async Task<IReadOnlyList<Incoming>> PollAsync(int offset, TimeSpan timeout, CancellationToken ct)
        {
            if (!_delivered)
            {
                _delivered = true;
                return Inbox;
            }

            // Everything queued has been through the loop; let the test go on, then park like a real
            // long poll rather than spinning.
            _drained.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }

        public Task SendAsync(long chatId, string text, IReadOnlyList<Button>? buttons, CancellationToken ct)
        {
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task EditAsync(long chatId, int messageId, string text, IReadOnlyList<Button>? buttons,
            CancellationToken ct)
        {
            Sent.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task SendDocumentAsync(long chatId, string fileName, byte[] content, string? caption,
            CancellationToken ct)
        {
            Documents.Add(fileName);
            Sent.Add((chatId, caption ?? ""));
            return Task.CompletedTask;
        }

        public Task AcknowledgeAsync(string callbackId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Directory : IAdminDirectory
    {
        public readonly HashSet<long> Allowed = [];
        public bool IsAdmin(long telegramUserId) => Allowed.Contains(telegramUserId);
    }

    private sealed class NoLinking : ILinkService
    {
        public LinkOutcome Redeem(string code, long telegramUserId, long chatId) => LinkOutcome.NoSuchCode;
    }

    private const long Owner = 111222333;
    private const long Stranger = 999888777;
    private const int ErpUser = 7;

    private string _dbPath = null!;
    private BotStore _store = null!;
    private FakeTelegram _telegram = null!;
    private Directory _directory = null!;
    private TestPharmacy _pharmacy = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "dawaii_worker_" + Guid.NewGuid().ToString("N") + ".db");
        _store = new BotStore(new SqliteConnectionFactory(_dbPath));
        _store.EnsureSchema();

        _telegram = new FakeTelegram();
        _directory = new Directory();
        _pharmacy = new TestPharmacy();

        _pharmacy.PriceRows =
            [new PriceLine(new Item { NameEn = "panadol", UnitsPerStrip = 10, StripsPerBox = 10, SellingPrice = 60m }, 1200)];
    }

    [TearDown]
    public void TearDown()
    {
        SQLiteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* a locked temp file is not a failure */ }
    }

    /// <summary>Makes this Telegram account a linked, active administrator.</summary>
    private void Link(long telegramUserId)
    {
        _directory.Allowed.Add(telegramUserId);

        // Through the real code path rather than an INSERT, so the row the worker reads back is the
        // row linking actually produces.
        string code = _store.CreateLinkCode(ErpUser, DateTime.Now);
        LinkResult result = _store.Redeem(code, telegramUserId, telegramUserId,
            _ => true, _ => "Admin", DateTime.Now);
        Assert.That(result.Outcome, Is.EqualTo(LinkOutcome.Linked), "test setup");
    }

    private async Task RunAsync(int rateLimitPerMinute = 20)
    {
        var options = Options.Create(new BotOptions { RateLimitPerMinute = rateLimitPerMinute });

        var worker = new CommandWorker(
            _telegram,
            new CommandAuthorizer(_directory, rateLimitPerMinute),
            new LinkThrottle(10),
            new CommandRouter(new NoLinking(), _pharmacy, "صيدلية النيل"),
            _store,
            options,
            NullLogger<CommandWorker>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task running = worker.StartAsync(cts.Token);

        await _telegram.Drained.WaitAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);
        await running;
    }

    private void Incoming(long from, string text, int updateId = 1) =>
        _telegram.Inbox.Add(new Incoming(updateId, from, from, text));

    // ---------------- the order that matters ----------------

    [Test]
    public async Task AStrangerTypingADrugName_GetsNothingAndTheShelfIsNeverRead()
    {
        Incoming(Stranger, "panadol");

        await RunAsync();

        Assert.That(_telegram.Sent, Is.Empty, "silence — any reply confirms the bot is real and listening");
        Assert.That(_pharmacy.LastPriceTerm, Is.Null,
            "and the pharmacy's data was never queried on their behalf");
    }

    [Test]
    public async Task AStrangersAttemptIsStillRecorded()
    {
        // Silence is for them, not for the owner: a run of these from one id is what somebody trying
        // the door looks like, and the audit log is the only place it exists.
        Incoming(Stranger, "panadol");

        await RunAsync();

        var log = _store.RecentAudit(10);
        Assert.That(log, Is.Not.Empty);
        Assert.That(log[0].TelegramUserId, Is.EqualTo(Stranger));
        Assert.That(log[0].Success, Is.False);
    }

    [Test]
    public async Task ALinkedOwnerTypingADrugName_GetsThePrice()
    {
        Link(Owner);
        Incoming(Owner, "panadol");

        await RunAsync();

        Assert.That(_pharmacy.LastPriceTerm, Is.EqualTo("panadol"));
        Assert.That(_telegram.Sent, Has.Count.EqualTo(1));
        Assert.That(_telegram.Sent[0].Text, Does.Contain("6,000.00"));
    }

    [Test]
    public async Task TheReportRunsAsTheLinkedPharmacyAccount_NotAsNobody()
    {
        // The ERP decides what this person may see. Passing 0 here would ask the pharmacy's services
        // about a user that does not exist — which either throws or, worse, resolves to something.
        Link(Owner);
        Incoming(Owner, "/sales today");

        await RunAsync();

        Assert.That(_pharmacy.ErpUserIdSeen, Is.EqualTo(ErpUser));
    }

    // ---------------- the rate limit ----------------

    [Test]
    public async Task APlainTextFloodIsLimitedLikeCommands_AndTheOwnerIsToldWhy()
    {
        // Silence here would read as a broken bot and they would keep typing into it. A stranger gets
        // silence; somebody the bot knows gets an explanation.
        Link(Owner);
        for (int i = 1; i <= 4; i++) Incoming(Owner, "panadol", updateId: i);

        await RunAsync(rateLimitPerMinute: 2);

        Assert.That(_telegram.Sent.Count(s => s.Text.Contains("أوامر كثيرة")), Is.EqualTo(2),
            "two answered, two turned away with a reason");
    }

    [Test]
    public async Task ARateLimitedStrangerIsStillNotAnswered()
    {
        for (int i = 1; i <= 4; i++) Incoming(Stranger, "panadol", updateId: i);

        await RunAsync(rateLimitPerMinute: 2);

        Assert.That(_telegram.Sent, Is.Empty,
            "the rejection message must never become a way to probe whether the bot exists");
    }

    // ---------------- the loop's own promises ----------------

    [Test]
    public async Task OneBadMessageDoesNotStopTheOnesBehindIt()
    {
        Link(Owner);
        _pharmacy.Refuse = new Dawaii.Core.PermissionDeniedException("لا يسمح لك.");

        Incoming(Owner, "/sales today", updateId: 1);   // the ERP refuses this one
        Incoming(Owner, "/ping", updateId: 2);

        await RunAsync();

        Assert.That(_telegram.Sent.Any(s => s.Text == "pong"), Is.True,
            "a message that fails must not take the rest of the batch with it");
    }

    [Test]
    public async Task ASticker_IsNotAnError()
    {
        Link(Owner);
        Incoming(Owner, "");

        await RunAsync();

        Assert.That(_telegram.Sent, Is.Empty);
    }

    [Test]
    public async Task AskingForTheWholePriceList_ComesBackAsAFile()
    {
        Link(Owner);
        _pharmacy.AllPriceRows = Enumerable.Range(1, 300)
            .Select(i => new PriceLine(new Item { NameEn = "drug-" + i, SellingPrice = i }, 10))
            .ToList();

        Incoming(Owner, "*");

        await RunAsync();

        Assert.That(_telegram.Documents, Has.Count.EqualTo(1));
        Assert.That(_telegram.Documents[0], Does.EndWith(".csv"));
    }
}
