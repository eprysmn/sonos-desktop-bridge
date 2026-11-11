using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SonosDesktopBridge;

/// <summary>A Sonos room. A stereo pair is one room.</summary>
public sealed record Room(string Name, string Uuid, string Ip)
{
    public override string ToString() => Name;
}

internal sealed record Member(string Name, string Uuid, string Ip, bool Visible);

internal sealed record Group(string CoordinatorUuid, List<Member> Members)
{
    public Member? Coordinator => Members.FirstOrDefault(m => m.Uuid == CoordinatorUuid) ?? Members.FirstOrDefault();

    /// <summary>One entry per room name, preferring the visible member (pairs have two members).</summary>
    public List<Room> Rooms => Members
        .GroupBy(m => m.Name)
        .Select(g => g.FirstOrDefault(m => m.Visible) ?? g.First())
        .Select(m => new Room(m.Name, m.Uuid, m.Ip))
        .ToList();
}

internal sealed record RoomVolume(string Name, int Volume);

internal sealed record SonosState(int Volume, bool Muted, string Source, bool IsLineIn, string GroupLabel, List<RoomVolume> Rooms);

internal sealed class SonosController
{
    private const string GroupRendering = "urn:schemas-upnp-org:service:GroupRenderingControl:1";
    private const string Rendering = "urn:schemas-upnp-org:service:RenderingControl:1";
    private const string Transport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string Topology = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private List<Group> _groups = new();
    private string? _entryIp;
    private string? _roomName;
    private string? _coordIp;

    public bool IsConnected => _coordIp is not null;
    public string? RoomName => _roomName;
    public Group? CurrentGroup => _groups.FirstOrDefault(g => g.Members.Any(m => m.Name == _roomName));
    public List<Room> AllRooms() => _groups.SelectMany(g => g.Rooms).DistinctBy(r => r.Name).OrderBy(r => r.Name).ToList();

    public async Task<List<Room>> LoadRoomsAsync(IEnumerable<string> ips)
    {
        Exception? last = null;
        foreach (var ip in ips)
        {
            try { await LoadTopologyAsync(ip); return AllRooms(); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("Aucun lecteur Sonos joignable.");
    }

    /// <summary>First room whose group is playing the local (line-in) source, if any.</summary>
    public async Task<string?> FindLineInRoomAsync()
    {
        foreach (var g in _groups)
        {
            try
            {
                var ip = g.Coordinator?.Ip;
                if (ip is null) continue;
                var media = await SoapAsync(ip, "/MediaRenderer/AVTransport/Control", Transport, "GetMediaInfo", "<InstanceID>0</InstanceID>");
                var uri = media.Descendants().FirstOrDefault(x => x.Name.LocalName == "CurrentURI")?.Value ?? "";
                if (uri.StartsWith("x-rincon-stream:", StringComparison.OrdinalIgnoreCase)) return g.Rooms.First().Name;
            }
            catch { }
        }
        return null;
    }

    public void SelectRoom(string name)
    {
        _roomName = name;
        Resolve();
    }

    private async Task LoadTopologyAsync(string ip)
    {
        var doc = await SoapAsync(ip, "/ZoneGroupTopology/Control", Topology, "GetZoneGroupState", "");
        var text = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "ZoneGroupState")?.Value
                   ?? throw new InvalidOperationException("Topologie Sonos illisible.");
        var groups = new List<Group>();
        foreach (var g in XDocument.Parse(text).Descendants().Where(x => x.Name.LocalName == "ZoneGroup"))
        {
            var members = new List<Member>();
            foreach (var m in g.Elements().Where(x => x.Name.LocalName == "ZoneGroupMember"))
            {
                var location = (string?)m.Attribute("Location");
                if (string.IsNullOrEmpty(location)) continue;
                members.Add(new Member((string?)m.Attribute("ZoneName") ?? "?", (string?)m.Attribute("UUID") ?? "",
                    new Uri(location).Host, (string?)m.Attribute("Invisible") != "1"));
            }
            if (members.Count > 0) groups.Add(new Group((string?)g.Attribute("Coordinator") ?? members[0].Uuid, members));
        }
        if (groups.Count == 0) throw new InvalidOperationException("Aucun groupe Sonos trouvé.");
        _groups = groups;
        _entryIp = ip;
        if (_roomName is not null) Resolve();
    }

    private void Resolve()
    {
        _coordIp = CurrentGroup?.Coordinator?.Ip ?? _coordIp;
    }

    public async Task<SonosState> RefreshAsync()
    {
        EnsureConnected();
        // The coordinator can change when rooms are linked or unlinked, so re-read the topology each time.
        var candidates = new[] { _coordIp, _entryIp }.Concat(_groups.SelectMany(g => g.Members.Select(m => m.Ip)))
            .Where(i => i is not null).Select(i => i!).Distinct();
        Exception? last = null;
        var ok = false;
        foreach (var ip in candidates)
        {
            try { await LoadTopologyAsync(ip); ok = true; break; }
            catch (Exception ex) { last = ex; }
        }
        if (!ok) throw last ?? new InvalidOperationException("Sonos injoignable.");

        var group = CurrentGroup ?? throw new InvalidOperationException($"Pièce « {_roomName} » introuvable.");
        var label = string.Join(" + ", group.Rooms.Select(r => r.Name));
        var vol = await SoapAsync(_coordIp!, "/MediaRenderer/GroupRenderingControl/Control", GroupRendering, "GetGroupVolume", "<InstanceID>0</InstanceID>");
        var mute = await SoapAsync(_coordIp!, "/MediaRenderer/GroupRenderingControl/Control", GroupRendering, "GetGroupMute", "<InstanceID>0</InstanceID>");
        var volume = int.Parse(vol.Descendants().First(x => x.Name.LocalName == "CurrentVolume").Value);
        var muted = mute.Descendants().First(x => x.Name.LocalName == "CurrentMute").Value is "1" or "true";

        var uri = "";
        try
        {
            var media = await SoapAsync(_coordIp!, "/MediaRenderer/AVTransport/Control", Transport, "GetMediaInfo", "<InstanceID>0</InstanceID>");
            uri = media.Descendants().FirstOrDefault(x => x.Name.LocalName == "CurrentURI")?.Value ?? "";
        }
        catch { /* Source is informational only. */ }
        var rooms = await Task.WhenAll(group.Rooms.OrderBy(r => r.Name).Select(async r =>
        {
            try
            {
                var d = await SoapAsync(r.Ip, "/MediaRenderer/RenderingControl/Control", Rendering, "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                return new RoomVolume(r.Name, int.Parse(d.Descendants().First(x => x.Name.LocalName == "CurrentVolume").Value));
            }
            catch { return new RoomVolume(r.Name, -1); }
        }));
        return new SonosState(volume, muted, DescribeSource(uri), uri.StartsWith("x-rincon-stream:", StringComparison.OrdinalIgnoreCase), label, rooms.ToList());
    }

    private static string DescribeSource(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "Aucune lecture";
        if (uri.StartsWith("x-rincon-stream:", StringComparison.OrdinalIgnoreCase)) return "Entrée source locale";
        if (uri.StartsWith("x-sonos-htastream:", StringComparison.OrdinalIgnoreCase)) return "TV";
        if (uri.StartsWith("x-sonos-vli:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("x-sonosapi-", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("x-rincon-mp3radio:", StringComparison.OrdinalIgnoreCase)) return "Streaming";
        return "Bibliothèque / AirPlay";
    }

    public async Task<int> AdjustVolumeAsync(int delta)
    {
        EnsureConnected();
        var doc = await SoapAsync(_coordIp!, "/MediaRenderer/GroupRenderingControl/Control", GroupRendering, "SetRelativeGroupVolume",
            $"<InstanceID>0</InstanceID><Adjustment>{delta}</Adjustment>");
        return int.Parse(doc.Descendants().First(x => x.Name.LocalName == "NewVolume").Value);
    }

    public async Task SetVolumeAsync(int volume)
    {
        EnsureConnected();
        await SoapAsync(_coordIp!, "/MediaRenderer/GroupRenderingControl/Control", GroupRendering, "SetGroupVolume",
            $"<InstanceID>0</InstanceID><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>");
    }

    /// <summary>Sets the volume of a single room, independently of the group volume.</summary>
    public async Task SetRoomVolumeAsync(string roomName, int volume)
    {
        EnsureConnected();
        var room = _groups.SelectMany(g => g.Rooms).FirstOrDefault(r => r.Name == roomName) ?? throw new InvalidOperationException("Pièce inconnue.");
        await SoapAsync(room.Ip, "/MediaRenderer/RenderingControl/Control", Rendering, "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{Math.Clamp(volume, 0, 100)}</DesiredVolume>");
    }

    public async Task SetMuteAsync(bool muted)
    {
        EnsureConnected();
        await SoapAsync(_coordIp!, "/MediaRenderer/GroupRenderingControl/Control", GroupRendering, "SetGroupMute",
            $"<InstanceID>0</InstanceID><DesiredMute>{(muted ? 1 : 0)}</DesiredMute>");
    }

    /// <summary>Links another room to the current group (it then plays what the group plays).</summary>
    public async Task JoinAsync(string roomName)
    {
        EnsureConnected();
        var target = _groups.SelectMany(g => g.Rooms).FirstOrDefault(r => r.Name == roomName) ?? throw new InvalidOperationException("Pièce inconnue.");
        var coordinator = CurrentGroup?.Coordinator ?? throw new InvalidOperationException("Groupe introuvable.");
        await SoapAsync(target.Ip, "/MediaRenderer/AVTransport/Control", Transport, "SetAVTransportURI",
            $"<InstanceID>0</InstanceID><CurrentURI>x-rincon:{coordinator.Uuid}</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>");
    }

    /// <summary>Makes a room independent again.</summary>
    public async Task LeaveAsync(string roomName)
    {
        EnsureConnected();
        var target = _groups.SelectMany(g => g.Rooms).FirstOrDefault(r => r.Name == roomName) ?? throw new InvalidOperationException("Pièce inconnue.");
        await SoapAsync(target.Ip, "/MediaRenderer/AVTransport/Control", Transport, "BecomeCoordinatorOfStandaloneGroup", "<InstanceID>0</InstanceID>");
    }

    private void EnsureConnected() { if (_coordIp is null) throw new InvalidOperationException("Aucune pièce Sonos sélectionnée."); }

    private async Task<XDocument> SoapAsync(string ip, string path, string service, string action, string body)
    {
        var envelope = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{service}\">{body}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{ip}:1400{path}");
        request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var doc = XDocument.Parse(text);
        var fault = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "faultstring")?.Value;
        if (fault is not null) throw new InvalidOperationException(fault);
        return doc;
    }
}

internal static class SonosDiscovery
{
    /// <summary>SSDP search; returns the IP of every Sonos player that answers.</summary>
    public static async Task<List<string>> FindIpsAsync()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        var request = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n");
        await udp.SendAsync(request, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));
        var ips = new List<string>();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end)
        {
            using var timeout = new CancellationTokenSource(end - DateTime.UtcNow);
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { break; }
            var match = Regex.Match(Encoding.ASCII.GetString(packet.Buffer), @"(?im)^LOCATION:\s*(\S+)");
            if (!match.Success || !Uri.TryCreate(match.Groups[1].Value.Trim(), UriKind.Absolute, out var uri)) continue;
            if (!ips.Contains(uri.Host)) ips.Add(uri.Host);
        }
        return ips;
    }
}
