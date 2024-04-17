// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using k8s;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;

namespace Aspire.Hosting.Tests.Dcp;

internal sealed class TestKubernetesService : IKubernetesService
{
    // In user port range, but otherwise no particular reason to start with this value.
    // This is meant to help tests select ports that do not clash with ports auto-generated by TestKubernetesService.
    public const int StartOfAutoPortRange = 52000;

    public ConcurrentQueue<CustomResource> CreatedResources { get; } = [];

    private readonly List<Channel<(WatchEventType, CustomResource)>> _watchChannels = [];
    private int _nextPort = StartOfAutoPortRange; 

    public Task<T> GetAsync<T>(string name, string? namespaceParameter = null, CancellationToken _ = default) where T : CustomResource
    {
        var res = CreatedResources.OfType<T>().FirstOrDefault(r =>
            r.Metadata.Name == name &&
            string.Equals(r.Metadata.NamespaceProperty ?? string.Empty, namespaceParameter ?? string.Empty)
        );
        if (res == null)
        {
            throw new ArgumentException($"Resource '{namespaceParameter ?? ""}/{name}' not found");
        }
        return Task.FromResult(res);
    }

    public Task<T> CreateAsync<T>(T obj, CancellationToken cancellationToken = default) where T : CustomResource
    {
        static T Clone(T r)
        {
            var serialized = JsonSerializer.Serialize(r);
            var clone = JsonSerializer.Deserialize<T>(serialized);
            return clone!;
        }

        var res = Clone(obj);

        // "Allocate" port for a service.
        if (res is Service svc)
        {
            if (svc.Status is null)
            {
                svc.Status = new ServiceStatus();
            }
            svc.Status.EffectiveAddress = svc.Spec.Address ?? "localhost";
            svc.Status.EffectivePort = svc.Spec.Port ?? Interlocked.Increment(ref _nextPort);
        }

        lock (CreatedResources)
        {
            CreatedResources.Enqueue(res);
            foreach (var c in _watchChannels)
            {
                c.Writer.TryWrite((WatchEventType.Added, res));
            }
        }
        
        return Task.FromResult(res);
    }

    public Task<T> DeleteAsync<T>(string name, string? namespaceParameter = null, CancellationToken cancellationToken = default) where T : CustomResource
    {
        throw new NotImplementedException();
    }

    public Task<List<T>> ListAsync<T>(string? namespaceParameter = null, CancellationToken cancellationToken = default) where T : CustomResource
    {
        var res = CreatedResources.OfType<T>().Where(r =>
            string.Equals(r.Metadata.NamespaceProperty ?? string.Empty, namespaceParameter ?? string.Empty)
        );
        return Task.FromResult(res.ToList());
    }

    public async IAsyncEnumerable<(WatchEventType, T)> WatchAsync<T>(string? namespaceParameter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : CustomResource
    {
        var chan = Channel.CreateUnbounded<(WatchEventType, CustomResource)>();

        lock (CreatedResources)
        {
            _watchChannels.Add(chan);
            foreach (var res in CreatedResources.OfType<T>())
            {
                chan.Writer.TryWrite((WatchEventType.Added, res));
            }
        }

        try
        {
            while (true)
            {
                var (evtType, res) = await chan.Reader.ReadAsync(cancellationToken);
                if (res is T tRes)
                {
                    yield return (evtType, tRes);
                }
            }
        }
        finally
        {
            lock (CreatedResources)
            {
                _watchChannels.Remove(chan);
            }
        }
    }

    public Task<Stream> GetLogStreamAsync<T>(T obj, string logStreamType, bool? follow = true, bool? timestamps = false, CancellationToken cancellationToken = default) where T : CustomResource
    {
        var ms = new MemoryStream(Encoding.UTF8.GetBytes($"Logs for {obj.Metadata.Name} ({logStreamType})"));
        return Task.FromResult((Stream) ms);
    }
}