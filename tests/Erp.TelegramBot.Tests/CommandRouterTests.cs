using Erp.TelegramBot.Commands;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>What the bot says back, in phase 1.</summary>
[TestFixture]
public class CommandRouterTests
{
    private readonly CommandRouter _router = new();

    [Test]
    public void Ping_Answers_SoConnectivityCanBeProvenEndToEnd()
    {
        Assert.That(_router.Handle(CommandLine.Parse("/ping")), Is.EqualTo("pong"));
    }

    [TestCase("/start")]
    [TestCase("/help")]
    public void StartAndHelp_BothExplainTheBot(string text)
    {
        string? reply = _router.Handle(CommandLine.Parse(text));

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply, Does.Contain("/ping"));
        Assert.That(reply, Does.Contain("للمدير"),
            "the help must say plainly that this is for the manager only");
    }

    [Test]
    public void AnUnknownCommand_IsAnswered_NotIgnored()
    {
        // Distinct from an unauthorized sender, who gets silence. An authorized manager who mistypes
        // is owed an answer, or the bot looks broken to the one person allowed to use it.
        string? reply = _router.Handle(CommandLine.Parse("/stock ABC"));

        Assert.That(reply, Is.Not.Null);
        Assert.That(reply, Does.Contain("/help"));
    }

    [Test]
    public void OrdinaryChatter_GetsNoReply()
    {
        Assert.That(_router.Handle(CommandLine.Parse("hello")), Is.Null);
        Assert.That(_router.Handle(CommandLine.None), Is.Null);
    }

    [Test]
    public void EveryPhaseOneReply_FitsInOneTelegramMessage()
    {
        foreach (string text in new[] { "/start", "/help", "/ping", "/nonsense" })
        {
            string? reply = _router.Handle(CommandLine.Parse(text));
            Assert.That(reply!.Length, Is.LessThan(CommandRouter.TelegramTextLimit),
                text + " must not need pagination");
        }
    }
}
