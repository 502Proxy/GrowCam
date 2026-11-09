using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Xml.Linq;
using GrowCam.Application;

namespace GrowCam.Infrastructure.Camera;

/// <summary>
/// Minimal ONVIF client for pan/tilt and presets (SOAP over HTTP with
/// WS-Security digest auth). Tapo cameras expose ONVIF on port 2020 and use
/// the "camera account" credentials – the same ones as for RTSP.
/// </summary>
public sealed class OnvifPtzClient(HttpClient http, string host, string user, string password, int port = 2020)
{
    private const string DeviceNs = "http://www.onvif.org/ver10/device/wsdl";
    private const string MediaNs = "http://www.onvif.org/ver10/media/wsdl";
    private const string PtzNs = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string SchemaNs = "http://www.onvif.org/ver10/schema";

    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(400);
    private const double MoveSpeed = 0.5;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _ptzUrl;
    private string? _profileToken;

    public async Task MoveAsync(Direction direction, CancellationToken ct)
    {
        var (x, y) = direction switch
        {
            Direction.Left => (-MoveSpeed, 0d),
            Direction.Right => (MoveSpeed, 0d),
            Direction.Up => (0d, MoveSpeed),
            Direction.Down => (0d, -MoveSpeed),
            _ => (0d, 0d),
        };
        var (url, token) = await EnsureInitializedAsync(ct);
        await CallAsync(url, $"""
            <ContinuousMove xmlns="{PtzNs}">
              <ProfileToken>{Esc(token)}</ProfileToken>
              <Velocity><PanTilt xmlns="{SchemaNs}" x="{Num(x)}" y="{Num(y)}"/></Velocity>
            </ContinuousMove>
            """, ct);
        await Task.Delay(MoveDuration, ct);
        await CallAsync(url, $"""
            <Stop xmlns="{PtzNs}">
              <ProfileToken>{Esc(token)}</ProfileToken>
              <PanTilt>true</PanTilt>
              <Zoom>false</Zoom>
            </Stop>
            """, ct);
    }

    public async Task<IReadOnlyList<CameraPreset>> GetPresetsAsync(CancellationToken ct)
    {
        var (url, token) = await EnsureInitializedAsync(ct);
        var response = await CallAsync(url, $"""<GetPresets xmlns="{PtzNs}"><ProfileToken>{Esc(token)}</ProfileToken></GetPresets>""", ct);
        return response.Descendants()
            .Where(e => e.Name.LocalName == "Preset")
            .Select(e => new CameraPreset(
                (string?)e.Attribute("token") ?? "",
                e.Elements().FirstOrDefault(c => c.Name.LocalName == "Name")?.Value ?? ""))
            .Where(p => p.Id.Length > 0)
            .ToList();
    }

    public async Task GotoPresetAsync(string presetId, CancellationToken ct)
    {
        var (url, token) = await EnsureInitializedAsync(ct);
        await CallAsync(url, $"""
            <GotoPreset xmlns="{PtzNs}">
              <ProfileToken>{Esc(token)}</ProfileToken>
              <PresetToken>{Esc(presetId)}</PresetToken>
            </GotoPreset>
            """, ct);
    }

    /// <summary>Saves the current position; overwrites a preset with the same name.</summary>
    public async Task<CameraPreset> SavePresetAsync(string name, CancellationToken ct)
    {
        var (url, token) = await EnsureInitializedAsync(ct);
        var existing = (await GetPresetsAsync(ct)).FirstOrDefault(p => p.Name == name);
        var existingToken = existing is null ? "" : $"<PresetToken>{Esc(existing.Id)}</PresetToken>";
        var response = await CallAsync(url, $"""
            <SetPreset xmlns="{PtzNs}">
              <ProfileToken>{Esc(token)}</ProfileToken>
              <PresetName>{Esc(name)}</PresetName>
              {existingToken}
            </SetPreset>
            """, ct);
        var id = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "PresetToken")?.Value ?? existing?.Id;
        return id is null
            ? throw new CameraException("Die Kamera hat die Position nicht gespeichert.")
            : new CameraPreset(id, name);
    }

    private async Task<(string PtzUrl, string ProfileToken)> EnsureInitializedAsync(CancellationToken ct)
    {
        if (_ptzUrl is not null && _profileToken is not null)
            return (_ptzUrl, _profileToken);

        await _initLock.WaitAsync(ct);
        try
        {
            if (_ptzUrl is null || _profileToken is null)
            {
                var deviceUrl = $"http://{host}:{port}/onvif/device_service";
                var capabilities = await CallAsync(deviceUrl, $"""<GetCapabilities xmlns="{DeviceNs}"><Category>All</Category></GetCapabilities>""", ct);
                var mediaUrl = XAddr(capabilities, "Media") ?? deviceUrl;
                _ptzUrl = XAddr(capabilities, "PTZ")
                          ?? throw new CameraException("Diese Kamera unterstützt keine Steuerung über ONVIF.");

                var profiles = await CallAsync(mediaUrl, $"""<GetProfiles xmlns="{MediaNs}"/>""", ct);
                _profileToken = profiles.Descendants()
                                    .Where(e => e.Name.LocalName == "Profiles")
                                    .Select(e => (string?)e.Attribute("token"))
                                    .FirstOrDefault(t => !string.IsNullOrEmpty(t))
                                ?? throw new CameraException("Die Kamera meldet kein Video-Profil.");
            }
            return (_ptzUrl, _profileToken);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<XElement> CallAsync(string url, string body, CancellationToken ct)
    {
        var envelope = $"""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Header>{SecurityHeader()}</s:Header>
              <s:Body>{body}</s:Body>
            </s:Envelope>
            """;
        using var content = new StringContent(envelope, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(url, content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CameraException("Die Kamera-Steuerung ist nicht erreichbar. Ist ONVIF in der Tapo-App aktiv?", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            XElement root;
            try
            {
                root = XElement.Parse(text);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new CameraException($"Unerwartete Antwort der Kamera ({(int)response.StatusCode}).", ex);
            }

            var fault = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
            if (fault is not null || !response.IsSuccessStatusCode)
            {
                var reason = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value ?? response.ReasonPhrase;
                throw new CameraException($"Die Kamera hat den Befehl abgelehnt: {reason}");
            }
            return root;
        }
    }

    /// <summary>WS-UsernameToken with password digest: Base64(SHA1(nonce + created + password)).</summary>
    private string SecurityHeader()
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var digestInput = nonce
            .Concat(Encoding.UTF8.GetBytes(created))
            .Concat(Encoding.UTF8.GetBytes(password))
            .ToArray();
        var digest = SHA1.HashData(digestInput);
        return $"""
            <Security s:mustUnderstand="1" xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd">
              <UsernameToken>
                <Username>{Esc(user)}</Username>
                <Password Type="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest">{Convert.ToBase64String(digest)}</Password>
                <Nonce EncodingType="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary">{Convert.ToBase64String(nonce)}</Nonce>
                <Created xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd">{created}</Created>
              </UsernameToken>
            </Security>
            """;
    }

    private static string? XAddr(XElement capabilities, string service) =>
        capabilities.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == service)?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")?.Value;

    private static string Esc(string value) => SecurityElement.Escape(value) ?? "";

    private static string Num(double value) => value.ToString("0.0#", CultureInfo.InvariantCulture);
}
