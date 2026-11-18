using System.Diagnostics;
using LANCommander.Plugins.Relay.Hosting;
using LANCommander.Plugins.Relay.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LANCommander.Plugins.Relay.Tests;

/// <summary>Starves the whole process's thread pool, so nothing else may run alongside it.</summary>
[CollectionDefinition(nameof(ThreadPoolStarvation), DisableParallelization = true)]
public sealed class ThreadPoolStarvation;

/// <summary>
/// The relay forwards UDP game traffic on a thread of its own, so a server whose thread pool is
/// exhausted, by a slow request or a burst of work, does not hold up the game.
/// </summary>
[Collection(nameof(ThreadPoolStarvation))]
public sealed class RelayIsolationTests
{
    [Fact]
    public async Task UdpTrafficFlowsWhileTheThreadPoolIsStarved()
    {
        using var scratch = new ScratchDirectory();
        var services = new ServiceCollection();

        services.AddLogging();
        RelayPlugin.AddRelayServices(services, _ => scratch.Paths);

        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<RelayHost>();

        scratch.WriteSettings(new RelayPluginSettings { Enabled = true, Port = 0, RequireAuthentication = false });
        await host.StartAsync();

        await using var alice = await RelayTestClient.ConnectAsync(host.TcpEndPoint!);
        await using var bob = await RelayTestClient.ConnectAsync(host.TcpEndPoint!);

        await alice.HelloAsync("lan-party", null, "Alice");
        await bob.HelloAsync("lan-party", null, "Bob");

        alice.OpenUdp(host.UdpEndPoint!);
        bob.OpenUdp(host.UdpEndPoint!);

        // A client is sent datagrams over UDP once it has sent one that way itself. Bob's
        // reaches Alice over UDP only after Alice's own has proven her side.
        alice.SendDatagramUdp(bob.Address!, [1]);
        bob.SendDatagramUdp(alice.Address!, [2]);
        Assert.Equal(RelayTestClient.Datagram, alice.ReceiveUdp().Type);

        // Stop the pool growing, then occupy every thread it has. A sleeping loop rather than a
        // wait handle, which the pool would compensate for with extra threads.
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
        Assert.True(ThreadPool.SetMaxThreads(minWorkers, maxIo));

        var released = false;

        try
        {
            // Threads above the new maximum only retire once idle, so queue for every thread the
            // pool has now, and then some, and wait until work is left over: every thread is busy.
            for (var i = 0; i < Math.Max(minWorkers, ThreadPool.ThreadCount) * 4; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(_ =>
                {
                    while (!Volatile.Read(ref released))
                        Thread.Sleep(5);
                }, null);
            }

            var deadline = Stopwatch.StartNew();

            while (ThreadPool.PendingWorkItemCount == 0 || deadline.Elapsed < TimeSpan.FromMilliseconds(200))
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "The thread pool could not be saturated");
                Thread.Sleep(10);
            }

            var elapsed = Stopwatch.StartNew();

            alice.SendDatagramUdp(bob.Address!, [42]);
            var (type, body) = bob.ReceiveUdp();

            elapsed.Stop();

            Assert.Equal(RelayTestClient.Datagram, type);
            Assert.Equal(42, body[^1]);
            Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"The datagram took {elapsed.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            Volatile.Write(ref released, true);
            ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        }
    }
}
