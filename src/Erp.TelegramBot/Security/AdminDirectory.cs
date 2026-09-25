namespace Erp.TelegramBot.Security;

/// <summary>
/// Who is allowed to use this bot, by numeric Telegram user ID.
///
/// IDs only, never @usernames: a username can be given up and taken by somebody else, so authorizing
/// on one means an account handed over or renamed silently transfers access to the pharmacy's stock
/// and takings. The numeric ID is permanent and belongs to the person.
/// </summary>
public interface IAdminDirectory
{
    /// <summary>True only for a Telegram account that may use this bot right now.</summary>
    bool IsAdmin(long telegramUserId);
}

/// <summary>
/// The phase-1 directory: a fixed list from configuration.
///
/// Replaced in phase 2 by one that reads bot_user and re-checks the linked ERP account's role live,
/// so an admin who is demoted or deactivated loses the bot on their next message. Until the linking
/// tables exist, a hand-written list is the only honest way to have a whitelist at all — and it is
/// also how the very first administrator gets in, before there is anybody to approve them.
/// </summary>
public sealed class ConfiguredAdminDirectory : IAdminDirectory
{
    private readonly HashSet<long> _allowed;

    public ConfiguredAdminDirectory(IEnumerable<long>? telegramUserIds)
    {
        // Zero and negatives are not real user IDs; a config file with a stray 0 in it must not
        // become a whitelist entry that matches an unset field somewhere else.
        _allowed = new HashSet<long>((telegramUserIds ?? []).Where(id => id > 0));
    }

    public int Count => _allowed.Count;

    public bool IsAdmin(long telegramUserId) => telegramUserId > 0 && _allowed.Contains(telegramUserId);
}
