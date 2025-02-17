using System.Collections.Concurrent;
using DotNet.Globbing;
using DotNet.Globbing.Token;
using ESPresense.Extensions;
using ESPresense.Locators;
using ESPresense.Services;
using MathNet.Numerics;
using MathNet.Spatial.Euclidean;
using Microsoft.AspNetCore.Mvc.Diagnostics;

namespace ESPresense.Models;

public class State
{
    public State(ConfigLoader cl)
    {
        IEnumerable<Floor> GetFloorsByIds(string[]? floorIds)
        {
            if (floorIds == null) yield break;
            foreach (var floorId in floorIds)
                if (Floors.TryGetValue(floorId, out var floor))
                    yield return floor;
        }

        void LoadConfig(Config c)
        {
            Config = c;
            foreach (var floor in c.Floors ?? Enumerable.Empty<ConfigFloor>()) Floors.GetOrAdd(floor.GetId(), a => new Floor()).Update(c, floor);
            foreach (var node in c.Nodes ?? Enumerable.Empty<ConfigNode>()) Nodes.GetOrAdd(node.GetId(), a => new Node()).Update(c, node, GetFloorsByIds(node.Floors));

            var idsToTrack = new List<Glob>();
            var configDeviceById = new ConcurrentDictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(d.Id))
                {
                    var glob = Glob.Parse(d.Id);
                    if (glob.Tokens.All(a => a is LiteralToken))
                        configDeviceById.GetOrAdd(d.Id, a => d);
                    else
                        idsToTrack.Add(glob);
                }

            var namesToTrack = new List<Glob>();
            var configDeviceByName = new ConcurrentDictionary<string, ConfigDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in c.Devices ?? Enumerable.Empty<ConfigDevice>())
                if (!string.IsNullOrWhiteSpace(d.Name))
                {
                    var glob = Glob.Parse(d.Name);
                    if (glob.Tokens.All(a => a is LiteralToken))
                        configDeviceByName.GetOrAdd(d.Name, a => d);
                    else
                        namesToTrack.Add(glob);
                }

            IdsToTrack = idsToTrack;
            ConfigDeviceById = configDeviceById;
            NamesToTrack = namesToTrack;
            ConfigDeviceByName = configDeviceByName;

            Weighting = c.Weighting?.Algorithm switch
            {
                "equal" => new EqualWeighting(),
                "gaussian" => new GaussianWeighting(c.Weighting?.Props),
                "exponential" => new ExponentialWeighting(c.Weighting?.Props),
                _ => new GaussianWeighting(c.Weighting?.Props),
            };
            foreach (var device in Devices.Values) device.Check = true;
        }

        cl.ConfigChanged += (_, args) => { LoadConfig(args); };
        if (cl.Config != null) LoadConfig(cl.Config);
    }

    public Config? Config;

    public ConcurrentDictionary<string, Node> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Device> Devices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Floor> Floors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ConfigDevice> ConfigDeviceByName { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, ConfigDevice> ConfigDeviceById { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Glob> IdsToTrack { get; private set; } = new();
    public List<Glob> NamesToTrack { get; private set; } = new();
    public List<OptimizationSnapshot> OptimizationSnaphots { get; } = new();
    public IWeighting? Weighting { get; set; }

    public OptimizationSnapshot TakeOptimizationSnapshot()
    {
        Dictionary<string, OptNode> nodes = new();
        var os = new OptimizationSnapshot();
        foreach (var (txId, txNode) in Nodes)
            foreach (var (rxId, meas) in txNode.RxNodes)
            {
                var tx = nodes.GetOrAdd(txId, a => new OptNode { Id = txId, Name = txNode.Name, Location = txNode.Location });
                var rx = nodes.GetOrAdd(rxId, a => new OptNode { Id = rxId, Name = meas.Rx!.Name, Location = meas.Rx.Location });
                os.Measures.Add(new Measure()
                {
                    Current = meas.Current,
                    Distance = meas.Distance,
                    RefRssi = meas.RefRssi,
                    Rssi = meas.Rssi,
                    Tx = tx,
                    Rx = rx,
                });

            }

        if (OptimizationSnaphots.Count > Config?.Optimization.MaxSnapshots) OptimizationSnaphots.RemoveAt(0);
        OptimizationSnaphots.Add(os);

        return os;
    }
}

