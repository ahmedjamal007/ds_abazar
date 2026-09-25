using Dawaii.Core.Abstractions;
using Dawaii.Core.Bot;
using Dawaii.Core.Models;
using Microsoft.Extensions.Logging;

namespace Erp.TelegramBot.Security;

/// <summary>
/// Who may use the bot, answered from the two databases rather than from a config file.
///
/// Replaces the phase-1 fixed list. A Telegram account is admitted only when BOTH hold:
///   * it is linked and not revoked, in the bot's own database, AND
///   * the pharmacy account behind it is STILL an active administrator.
///
/// The second check is the reason this class exists and is done live, on every command, rather than
/// trusting the role stored at link time. A manager who is demoted, deactivated or dismissed loses
/// the bot on their next message. Reading a cached role would mean somebody who left the pharmacy
/// keeping stock levels and daily takings on their phone until a person remembered to go and revoke
/// them by hand — which is exactly the kind of thing nobody remembers.
///
/// It fails CLOSED. If the pharmacy's database cannot be reached the answer is no, and the reason is
/// logged. A bot that answered during a database outage would be answering without being able to
/// check who it was talking to.
/// </summary>
public sealed class LinkedAdminDirectory : IAdminDirectory
{
    private readonly BotStore _bot;
    private readonly IUserRepository _erpUsers;
    private readonly ILogger<LinkedAdminDirectory> _log;

    public LinkedAdminDirectory(BotStore bot, IUserRepository erpUsers, ILogger<LinkedAdminDirectory> log)
    {
        _bot = bot;
        _erpUsers = erpUsers;
        _log = log;
    }

    public bool IsAdmin(long telegramUserId)
    {
        if (telegramUserId <= 0) return false;

        try
        {
            BotUser linked = _bot.FindActive(telegramUserId);
            if (linked == null) return false;

            User erp = _erpUsers.GetById(linked.ErpUserId);
            if (erp == null)
            {
                // The pharmacy account was deleted outright. The binding is stale; stop honouring it.
                _log.LogWarning(
                    "Telegram user {TelegramUserId} is linked to ERP user {ErpUserId}, which no longer exists.",
                    telegramUserId, linked.ErpUserId);
                return false;
            }

            if (!erp.IsActive)
            {
                _log.LogInformation(
                    "Refused Telegram user {TelegramUserId}: ERP account '{Username}' is deactivated.",
                    telegramUserId, erp.Username);
                return false;
            }

            if (!erp.IsAdmin)
            {
                // This bot is the manager's. A demotion takes effect here, immediately.
                _log.LogInformation(
                    "Refused Telegram user {TelegramUserId}: ERP account '{Username}' is no longer an administrator.",
                    telegramUserId, erp.Username);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Fail closed, loudly. Answering while unable to verify who is asking is the one outcome
            // that must not happen quietly.
            _log.LogError(ex,
                "Could not verify Telegram user {TelegramUserId} against the pharmacy database; refusing.",
                telegramUserId);
            return false;
        }
    }

    /// <summary>
    /// Whether a pharmacy account may be linked at all, asked at the moment a code is redeemed.
    /// Same rule as above, minus the Telegram side — the account must exist, be active, and be an
    /// administrator. A code issued ten minutes ago to someone since demoted must not still work.
    /// </summary>
    public bool MayBeLinked(int erpUserId)
    {
        try
        {
            User erp = _erpUsers.GetById(erpUserId);
            return erp != null && erp.IsActive && erp.IsAdmin;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not verify ERP user {ErpUserId} while linking; refusing.", erpUserId);
            return false;
        }
    }

    /// <summary>The account's role, for the audit trail on the bot_user row.</summary>
    public string RoleOf(int erpUserId)
    {
        try { return _erpUsers.GetById(erpUserId)?.Role.ToString() ?? "Unknown"; }
        catch { return "Unknown"; }
    }
}
