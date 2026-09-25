using Dawaii.Core.Bot;
using Erp.TelegramBot.Commands;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>What the bot says back.</summary>
[TestFixture]
public class CommandRouterTests
{
    /// <summary>A linking service that returns whatever the test wants, and records what it was asked.</summary>
    private sealed class FakeLinking : ILinkService
    {
        public LinkOutcome Next = LinkOutcome.Linked;
        public string LastCode;
        public long LastTelegramUserId;

        public LinkOutcome Redeem(string code, long telegramUserId, long chatId)
        {
            LastCode = code;
            LastTelegramUserId = telegramUserId;
            return Next;
        }
    }

    private FakeLinking _linking;
    private CommandRouter _router;
    private static readonly Sender Manager = new(111222333, 111222333);

    [SetUp]
    public void SetUp()
    {
        _linking = new FakeLinking();
        _router = new CommandRouter(_linking);
    }

    private string Reply(string text) => _router.Handle(CommandLine.Parse(text), Manager);

    // ---------------- the phase-1 commands ----------------

    [Test]
    public void Ping_Answers_SoConnectivityCanBeProvenEndToEnd()
    {
        Assert.That(Reply("/ping"), Is.EqualTo("pong"));
    }

    [TestCase("/start")]
    [TestCase("/help")]
    public void StartAndHelp_BothExplainTheBot(string text)
    {
        string reply = Reply(text);

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply, Does.Contain("/ping"));
        Assert.That(reply, Does.Contain("/link"));
        Assert.That(reply, Does.Contain("للمدير"),
            "the help must say plainly that this is for the manager only");
    }

    [Test]
    public void AnUnknownCommand_IsAnswered_NotIgnored()
    {
        // Distinct from an unauthorized sender, who gets silence. An authorized manager who mistypes
        // is owed an answer, or the bot looks broken to the one person allowed to use it.
        string reply = Reply("/stock ABC");

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply, Does.Contain("/help"));
    }

    [Test]
    public void OrdinaryChatter_GetsNoReply()
    {
        Assert.That(Reply("hello"), Is.Null);
        Assert.That(_router.Handle(CommandLine.None, Manager), Is.Null);
    }

    // ---------------- linking ----------------

    [Test]
    public void LinkIsTheOnlyCommandOpenToAStranger()
    {
        // Everything else from someone the bot does not know gets silence, so a stranger cannot even
        // establish that the bot is real. /link has to be the exception — it is how a manager becomes
        // known in the first place.
        Assert.That(CommandRouter.IsOpenToStrangers("link"), Is.True);

        foreach (string verb in new[] { "start", "help", "ping", "stock", "low", "sales", "report" })
            Assert.That(CommandRouter.IsOpenToStrangers(verb), Is.False, verb);
    }

    [Test]
    public void LinkWithNoCode_ExplainsWhereToGetOne()
    {
        string reply = Reply("/link");

        Assert.That(reply, Does.Contain("/link 123456"));
        Assert.That(reply, Does.Contain("تيليجرام"),
            "it must point at the admin page, not leave the manager guessing");
        Assert.That(_linking.LastCode, Is.Null, "nothing was redeemed");
    }

    [Test]
    public void LinkPassesTheCodeAndTheSendersOwnId()
    {
        Reply("/link 123456");

        Assert.That(_linking.LastCode, Is.EqualTo("123456"));
        Assert.That(_linking.LastTelegramUserId, Is.EqualTo(Manager.TelegramUserId),
            "the binding must be to the sender, never to anything they could put in the message");
    }

    [Test]
    public void ASuccessfulLink_SaysSoAndPointsAtHelp()
    {
        _linking.Next = LinkOutcome.Linked;

        string reply = Reply("/link 123456");

        Assert.That(reply, Does.Contain("/help"));
        Assert.That(reply, Does.Not.Contain("123456"), "no need to echo the code back");
    }

    [Test]
    public void AnUnknownCodeAndAnExpiredOne_AreAnsweredIdentically()
    {
        // Telling a guesser which of the two they hit tells them whether a code exists. The
        // manager's remedy is the same either way: generate a fresh one.
        _linking.Next = LinkOutcome.NoSuchCode;
        string unknown = Reply("/link 000000");

        _linking.Next = LinkOutcome.Expired;
        string expired = Reply("/link 000000");

        Assert.That(expired, Is.EqualTo(unknown));
    }

    [Test]
    public void AnAlreadyUsedCode_SaysSo_BecauseTheRemedyIsDifferent()
    {
        _linking.Next = LinkOutcome.NoSuchCode;
        string unknown = Reply("/link 000000");

        _linking.Next = LinkOutcome.AlreadyUsed;
        string used = Reply("/link 123456");

        // Unlike unknown-vs-expired, this one IS worth distinguishing: the remedy differs. A guesser
        // learns only that some code once existed, which they could infer anyway.
        Assert.That(used, Is.Not.EqualTo(unknown));
        Assert.That(used, Does.Contain("مرة واحدة"),
            "explaining that codes are single-use stops the manager retyping the same one");
    }

    [Test]
    public void ANonAdminAccount_IsToldThisBotIsForTheManager()
    {
        _linking.Next = LinkOutcome.NotAnAdmin;

        Assert.That(Reply("/link 123456"), Does.Contain("المدير"));
    }

    // ---------------- message size ----------------

    [Test]
    public void EveryReply_FitsInOneTelegramMessage()
    {
        foreach (LinkOutcome outcome in Enum.GetValues<LinkOutcome>())
        {
            _linking.Next = outcome;
            Assert.That(Reply("/link 123456").Length, Is.LessThan(CommandRouter.TelegramTextLimit));
        }

        foreach (string text in new[] { "/start", "/help", "/ping", "/nonsense", "/link" })
            Assert.That(Reply(text).Length, Is.LessThan(CommandRouter.TelegramTextLimit), text);
    }
}
