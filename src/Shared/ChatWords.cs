using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace ServersModes.Shared;

// Chat commands answer to "!rtv" and "/rtv" through css_ commands; this makes
// the bare word ("rtv") work too, the way players type it on public servers.
public sealed class ChatWords
{
    private readonly Dictionary<string, Action<CCSPlayerController, string[]>> _words =
        new(StringComparer.OrdinalIgnoreCase);

    public ChatWords(BasePlugin plugin)
    {
        plugin.AddCommandListener("say", OnSay);
        plugin.AddCommandListener("say_team", OnSay);
    }

    // Registers css_<word> as well, so "!word" and the console both reach it.
    public void Add(BasePlugin plugin, string word, string description, Action<CCSPlayerController, string[]> handler)
    {
        _words[word] = handler;
        plugin.AddCommand(
            $"css_{word}",
            description,
            (player, info) =>
            {
                if (Players.IsHuman(player))
                {
                    handler(player!, Arguments(info));
                }
            }
        );
    }

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (!Players.IsHuman(player))
        {
            return HookResult.Continue;
        }

        var parts = info.GetArg(1).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 0 && _words.TryGetValue(parts[0], out var handler))
        {
            handler(player!, parts.Skip(1).ToArray());
        }

        return HookResult.Continue;
    }

    private static string[] Arguments(CommandInfo info) =>
        Enumerable.Range(1, Math.Max(0, info.ArgCount - 1)).Select(info.GetArg).ToArray();
}
