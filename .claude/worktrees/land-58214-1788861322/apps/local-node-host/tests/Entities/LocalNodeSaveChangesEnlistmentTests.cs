using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class LocalNodeSaveChangesEnlistmentTests : IDisposable
{
    private readonly string _directory;

    public LocalNodeSaveChangesEnlistmentTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "harborline-local-node-enlistment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task One_registration_reaches_independent_context_types_and_sync_modes()
    {
        var recorder = new RecordingEnlister();
        await using var provider = BuildProvider(recorder);

        var firstFactory = provider.GetRequiredService<IDbContextFactory<FirstProbeContext>>();
        var secondFactory = provider.GetRequiredService<IDbContextFactory<SecondProbeContext>>();

        await using (var first = await firstFactory.CreateDbContextAsync())
        {
            await first.Database.EnsureCreatedAsync();
            first.Writes.Add(new ProbeWrite { Id = "async-write" });
            await first.SaveChangesAsync();
        }

        using (var second = secondFactory.CreateDbContext())
        {
            second.Database.EnsureCreated();
            second.Writes.Add(new ProbeWrite { Id = "sync-write" });
            second.SaveChanges();
        }

        await using (var first = await firstFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await first.Writes.CountAsync());
            Assert.Equal(1, await first.EnlistedRows.CountAsync());
        }

        await using (var second = await secondFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await second.Writes.CountAsync());
            Assert.Equal(1, await second.EnlistedRows.CountAsync());
        }

        Assert.Equal(
            new[] { typeof(FirstProbeContext), typeof(SecondProbeContext) },
            recorder.ContextTypes.OrderBy(type => type.Name));
    }

    [Fact]
    public async Task Later_enlister_failure_aborts_primary_and_already_enlisted_rows()
    {
        var recorder = new RecordingEnlister();
        await using var provider = BuildProvider(recorder, new FailingEnlister());
        var factory = provider.GetRequiredService<IDbContextFactory<FirstProbeContext>>();

        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.EnsureCreatedAsync();
        }

        await using (var write = await factory.CreateDbContextAsync())
        {
            write.Writes.Add(new ProbeWrite { Id = "must-roll-back" });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => write.SaveChangesAsync());
            Assert.Equal("enlistment-test-failure", error.Message);
        }

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.Writes.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.EnlistedRows.AsNoTracking().ToListAsync());
        Assert.Equal(new[] { typeof(FirstProbeContext) }, recorder.ContextTypes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ServiceProvider BuildProvider(params ILocalNodeSaveChangesEnlister[] enlisters)
    {
        var services = new ServiceCollection();
        foreach (var enlister in enlisters)
        {
            services.AddSingleton<ILocalNodeSaveChangesEnlister>(enlister);
        }

        services.AddLocalNodeSaveChangesEnlistment();
        services.AddDbContextFactory<FirstProbeContext>((provider, options) =>
        {
            options.UseSqlite($"Data Source={Path.Combine(_directory, "first.db")};Pooling=False");
            options.AddLocalNodeSaveChangesEnlistment(provider);
        });
        services.AddDbContextFactory<SecondProbeContext>((provider, options) =>
        {
            options.UseSqlite($"Data Source={Path.Combine(_directory, "second.db")};Pooling=False");
            options.AddLocalNodeSaveChangesEnlistment(provider);
        });

        return services.BuildServiceProvider();
    }

    private sealed class RecordingEnlister : ILocalNodeSaveChangesEnlister
    {
        private readonly ConcurrentQueue<Type> _contextTypes = new();

        internal IReadOnlyList<Type> ContextTypes => _contextTypes.ToArray();

        public void Enlist(DbContext context)
        {
            Record(context);
        }

        public ValueTask EnlistAsync(
            DbContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(context);
            return ValueTask.CompletedTask;
        }

        private void Record(DbContext context)
        {
            _contextTypes.Enqueue(context.GetType());
            context.Set<EnlistedRow>().Add(new EnlistedRow
            {
                Id = Guid.NewGuid().ToString("N"),
                ContextType = context.GetType().Name,
            });
        }
    }

    private sealed class FailingEnlister : ILocalNodeSaveChangesEnlister
    {
        public void Enlist(DbContext context)
        {
            throw new InvalidOperationException("enlistment-test-failure");
        }

        public ValueTask EnlistAsync(
            DbContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("enlistment-test-failure");
        }
    }

    private sealed class FirstProbeContext : DbContext
    {
        public FirstProbeContext(DbContextOptions<FirstProbeContext> options)
            : base(options)
        {
        }

        internal DbSet<ProbeWrite> Writes => Set<ProbeWrite>();

        internal DbSet<EnlistedRow> EnlistedRows => Set<EnlistedRow>();
    }

    private sealed class SecondProbeContext : DbContext
    {
        public SecondProbeContext(DbContextOptions<SecondProbeContext> options)
            : base(options)
        {
        }

        internal DbSet<ProbeWrite> Writes => Set<ProbeWrite>();

        internal DbSet<EnlistedRow> EnlistedRows => Set<EnlistedRow>();
    }

    private sealed class ProbeWrite
    {
        public required string Id { get; init; }
    }

    private sealed class EnlistedRow
    {
        public required string Id { get; init; }

        public required string ContextType { get; init; }
    }
}
