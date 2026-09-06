using BarRaider.SdTools;
using BarRaider.SdTools.Events;
using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// What both property inspectors share: the "sessions" data source that
/// fills the preferred-player select with whatever Windows currently
/// reports, and the global setting that carries the choice.
/// </summary>
internal static class PropertyInspectorBridge
{
    /// <summary>Name of the sdpi-select data source; the inspector asks for it with sendToPlugin.</summary>
    public const string SessionsDataSource = "sessions";

    /// <summary>Global setting key holding the preferred app id.</summary>
    public const string PreferredAppKey = "preferredApp";

    /// <summary>The select value meaning "follow the ranking".</summary>
    public const string AutomaticValue = "auto";

    public static void Attach(SDConnection connection)
    {
        connection.OnSendToPlugin += (_, e) => Handle(connection, e.Event);
    }

    /// <summary>Name of the sdpi-select data source listing monitors that answer DDC/CI.</summary>
    public const string MonitorsDataSource = "monitors";

    private static void Handle(ISDConnection connection, SendToPlugin request)
    {
        var name = request.Payload?["event"]?.ToString();
        switch (name)
        {
            case SessionsDataSource:
            {
                var items = new JArray
                {
                    new JObject { ["label"] = "Automatic (follow Windows)", ["value"] = AutomaticValue },
                };
                foreach (var appId in MediaHub.KnownAppIds)
                {
                    items.Add(new JObject
                    {
                        ["label"] = $"{FeedbackRenderer.AppDisplayName(appId)}  ({appId})",
                        ["value"] = appId,
                    });
                }

                Reply(connection, SessionsDataSource, items);
                break;
            }

            case MonitorsDataSource:
            {
                var items = new JArray
                {
                    new JObject { ["label"] = "Automatic (primary monitor)", ["value"] = AutomaticValue },
                };
                foreach (var monitor in DisplayBrightnessHub.MonitorNames)
                {
                    items.Add(new JObject { ["label"] = monitor, ["value"] = monitor });
                }

                Reply(connection, MonitorsDataSource, items);
                break;
            }
        }
    }

    private static void Reply(ISDConnection connection, string dataSource, JArray items) =>
        _ = connection.SendToPropertyInspectorAsync(new JObject
        {
            ["event"] = dataSource,
            ["items"] = items,
        });

    /// <summary>The preferred app id from global settings, or null for automatic.</summary>
    public static string? PreferredAppFrom(JObject? globalSettings)
    {
        var value = SettingsReader.GetString(globalSettings, PreferredAppKey, AutomaticValue);
        return value == AutomaticValue ? null : value;
    }
}
