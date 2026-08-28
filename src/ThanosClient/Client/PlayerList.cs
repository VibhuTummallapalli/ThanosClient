using System.Collections.Concurrent;
using ThanosClient.Protocol;

namespace ThanosClient.Client;

public sealed record PlayerInfo(Guid Uuid, string Name)
{
    public int Ping { get; set; }
    public int Gamemode { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>The server-side tab list, kept in sync from Player List Item packets.</summary>
public sealed class PlayerList
{
    private readonly ConcurrentDictionary<Guid, PlayerInfo> _players = new();

    public IReadOnlyCollection<PlayerInfo> All => _players.Values.ToList();
    public int Count => _players.Count;

    public string? Header { get; internal set; }
    public string? Footer { get; internal set; }

    public PlayerInfo? Find(string name) =>
        _players.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        _players.Clear();
        Header = null;
        Footer = null;
    }

    /// <summary>
    /// Applies one Player List Item packet. Returns the players added and removed so the
    /// client can raise join/leave events without diffing the whole list.
    /// </summary>
    public (List<PlayerInfo> Added, List<PlayerInfo> Removed) Apply(PacketReader reader)
    {
        var added = new List<PlayerInfo>();
        var removed = new List<PlayerInfo>();

        int action = reader.VarInt();
        int count = reader.VarInt();

        for (int i = 0; i < count; i++)
        {
            Guid uuid = reader.Uuid();

            switch (action)
            {
                case 0:   // add player
                {
                    string name = reader.String(16);

                    int properties = reader.VarInt();
                    for (int p = 0; p < properties; p++)
                    {
                        reader.String();            // property name
                        reader.String();            // value
                        if (reader.Bool()) reader.String();   // signature
                    }

                    var info = new PlayerInfo(uuid, name)
                    {
                        Gamemode = reader.VarInt(),
                        Ping = reader.VarInt(),
                    };
                    if (reader.Bool()) info.DisplayName = reader.String();

                    if (_players.TryAdd(uuid, info)) added.Add(info);
                    else _players[uuid] = info;
                    break;
                }

                case 1:   // update gamemode
                {
                    int gamemode = reader.VarInt();
                    if (_players.TryGetValue(uuid, out PlayerInfo? info)) info.Gamemode = gamemode;
                    break;
                }

                case 2:   // update latency
                {
                    int ping = reader.VarInt();
                    if (_players.TryGetValue(uuid, out PlayerInfo? info)) info.Ping = ping;
                    break;
                }

                case 3:   // update display name
                {
                    string? display = reader.Bool() ? reader.String() : null;
                    if (_players.TryGetValue(uuid, out PlayerInfo? info)) info.DisplayName = display;
                    break;
                }

                case 4:   // remove player
                {
                    if (_players.TryRemove(uuid, out PlayerInfo? info)) removed.Add(info);
                    break;
                }

                default:
                    // Unknown action: the rest of the packet can no longer be parsed safely.
                    return (added, removed);
            }
        }

        return (added, removed);
    }
}
