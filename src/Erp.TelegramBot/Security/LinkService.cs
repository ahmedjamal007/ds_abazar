using Dawaii.Core.Bot;
using Erp.TelegramBot.Commands;
using Microsoft.Extensions.Logging;

namespace Erp.TelegramBot.Security;

/// <summary>
/// Redeeming a link code: the bot's own database on one side, the pharmacy's on the other.
///
/// <see cref="BotStore.Redeem"/> deliberately does not know how to ask whether an ERP account is
/// still an administrator — it never opens the pharmacy's database. This is where the two are put
/// together, and it is also the only place in the bot that writes anything anywhere.
/// </summary>
public sealed class LinkService : ILinkService
{
    private readonly BotStore _bot;
    private readonly LinkedAdminDirectory _admins;
    private readonly ILogger<LinkService> _log;

    public LinkService(BotStore bot, LinkedAdminDirectory admins, ILogger<LinkService> log)
    {
        _bot = bot;
        _admins = admins;
        _log = log;
    }

    public LinkOutcome Redeem(string code, long telegramUserId, long chatId)
    {
        try
        {
            LinkResult result = _bot.Redeem(
                code, telegramUserId, chatId,
                _admins.MayBeLinked,      // asked of the PHARMACY's database, at this moment
                _admins.RoleOf,
                DateTime.Now);

            if (result.Success)
            {
                // Worth an ordinary log line, not a debug one: this is somebody gaining access to the
                // pharmacy's figures, and the service log is where that is visible without opening a
                // database.
                _log.LogInformation(
                    "Telegram user {TelegramUserId} linked to ERP user {ErpUserId}.",
                    telegramUserId, result.ErpUserId);
            }
            else
            {
                _log.LogWarning(
                    "Link attempt from Telegram user {TelegramUserId} refused: {Outcome}.",
                    telegramUserId, result.Outcome);
            }

            return result.Outcome;
        }
        catch (Exception ex)
        {
            // A database that cannot be written is not a reason to tell the caller they are linked.
            _log.LogError(ex, "Link attempt from Telegram user {TelegramUserId} failed.", telegramUserId);
            return LinkOutcome.NoSuchCode;
        }
    }
}
