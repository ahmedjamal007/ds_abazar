using Erp.TelegramBot.Commands;
using NUnit.Framework;

namespace Erp.TelegramBot.Tests;

/// <summary>
/// Taking a Telegram message apart.
///
/// Most of what reaches a bot is not a command, and the cases that trip parsers are the boring ones:
/// a trailing space, a capital letter, the "@BotName" Telegram staples onto commands in groups, and
/// a command aimed at somebody else's bot in the same group. Each of those answered wrongly is
/// either a bot that ignores its owner or one that replies to strangers.
/// </summary>
[TestFixture]
public class CommandLineTests
{
    [Test]
    public void APlainCommand_IsRecognised()
    {
        CommandLine c = CommandLine.Parse("/ping");

        Assert.That(c.IsCommand, Is.True);
        Assert.That(c.Verb, Is.EqualTo("ping"));
        Assert.That(c.Args, Is.Empty);
    }

    [Test]
    public void TheVerb_IsCaseInsensitive()
    {
        Assert.That(CommandLine.Parse("/Help").Verb, Is.EqualTo("help"));
        Assert.That(CommandLine.Parse("/HELP").Verb, Is.EqualTo("help"));
    }

    [Test]
    public void ArgumentsAreSeparated_AndSurroundingSpaceIgnored()
    {
        CommandLine c = CommandLine.Parse("   /link   123456   ");

        Assert.That(c.Verb, Is.EqualTo("link"));
        Assert.That(c.FirstArg, Is.EqualTo("123456"));
        Assert.That(c.Args.Length, Is.EqualTo(1), "runs of spaces are not empty arguments");
    }

    [Test]
    public void ADrugNameOfSeveralWords_CanBeReadBackWhole()
    {
        CommandLine c = CommandLine.Parse("/stock panadol extra 500");

        Assert.That(c.FirstArg, Is.EqualTo("panadol"));
        Assert.That(c.Rest, Is.EqualTo("panadol extra 500"),
            "an item is often typed as several words, and the search takes the lot");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase(null)]
    [TestCase("hello")]
    [TestCase("\u0645\u0631\u062d\u0628\u0627")]
    [TestCase("stock")]
    [TestCase("/")]
    [TestCase("/@SomeBot")]
    public void WhatIsNotACommand_IsNotTreatedAsOne(string? text)
    {
        CommandLine c = CommandLine.Parse(text);

        Assert.That(c.IsCommand, Is.False);
        Assert.That(c.Verb, Is.Empty);
        Assert.That(c.Args, Is.Empty);
    }

    // ---------------- the @mention Telegram adds in groups ----------------

    [Test]
    public void ACommandAddressedToThisBot_IsAccepted_WithoutTheMention()
    {
        CommandLine c = CommandLine.Parse("/stock@DawaiiBot ABC123", "DawaiiBot");

        Assert.That(c.Verb, Is.EqualTo("stock"), "the @name is addressing, not part of the command");
        Assert.That(c.FirstArg, Is.EqualTo("ABC123"));
    }

    [Test]
    public void TheMention_IsMatchedIgnoringCase()
    {
        Assert.That(CommandLine.Parse("/ping@dawaiibot", "DawaiiBot").Verb, Is.EqualTo("ping"));
    }

    [Test]
    public void ACommandAddressedToAnotherBot_IsIgnoredEntirely()
    {
        CommandLine c = CommandLine.Parse("/stock@SomeOtherBot ABC123", "DawaiiBot");

        Assert.That(c.IsCommand, Is.False,
            "two bots in one group must not both answer the same message");
    }

    [Test]
    public void AMention_IsStrippedEvenWhenThisBotsNameIsUnknown()
    {
        // Before the first successful GetMe the bot does not yet know its own name. It must still
        // answer its owner rather than sit mute.
        CommandLine c = CommandLine.Parse("/ping@DawaiiBot");

        Assert.That(c.Verb, Is.EqualTo("ping"));
    }
}
