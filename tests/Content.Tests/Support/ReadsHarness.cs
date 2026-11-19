using Content.Reads;
using Content.Store;
using Content.Store.Configuration;
using Content.Store.Models;
using Content.Sync.Search;
using Content.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Content.Tests.Support;

/// <summary>Remembers what the read path reported as missing from the index.</summary>
internal sealed class RecordingMissSink : IIndexMissSink
{
    private readonly List<(string Supplier, string[] Keys)> _recorded = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(string Supplier, string[] Keys)> Recorded
    {
        get
        {
            lock (_gate)
            {
                return [.. _recorded];
            }
        }
    }

    public void Record(string supplier, IReadOnlyCollection<string> keys)
    {
        lock (_gate)
        {
            _recorded.Add((supplier, [.. keys]));
        }
    }
}

/// <summary>The read path over an in-memory search cluster and a substitute content store.</summary>
internal sealed class ReadsHarness
{
    public const string Supplier = "supplier-a";
    public const string Alias = "content-hotel_supplier-a";

    public ReadsHarness()
    {
        Reader = new ContentReader(
            new ReadRouteTable(Options.Create(new ReadsOptions { Routes = [new ReadRoute { Supplier = Supplier, Collection = "hotel_supplier-a", IndexAlias = Alias }] })),
            Index,
            Store,
            Misses,
            Options.Create(new ReadsOptions { IndexTimeout = TimeSpan.FromSeconds(1) }),
            Options.Create(new ContentStoreOptions { Suppliers = [Supplier], MaxIdsPerRequest = 10 }),
            Clock,
            NullLogger<ContentReader>.Instance);
        Index.Seed(Alias + "-v1", [], Alias);
    }

    public InMemorySearchCluster Index { get; } = new();

    public IHotelContentStore Store { get; } = Substitute.For<IHotelContentStore>();

    public RecordingMissSink Misses { get; } = new();

    public FakeTimeProvider Clock { get; } = new();

    public ContentReader Reader { get; }

    public static HotelDocument Hotel(long id, string? code = null) => new()
    {
        Id = id,
        Code = code ?? $"H{id}",
        Name = $"Hotel {id}",
        CityCode = "C001",
        CountryCode = "XX",
        Rating = 4.5,
        Latitude = 10.5,
        Longitude = 20.25,
        Images = ["https://example.com/1.jpg"],
        Amenities = ["wifi"],
        Rooms = [new RoomData { Id = "R1", GroupIdRaw = "7", Name = "Double" }],
        Providers = [new ProviderRef { Supplier = "supplier-a", HotelId = $"{id}" }],
    };

    /// <summary>Puts hotels in the live index behind the alias.</summary>
    public ReadsHarness IndexHas(params long[] ids)
    {
        Index.Seed(Alias + "-v1", ids.Select(id => HotelSearchDocumentMapper.Map(Hotel(id))), Alias);
        return this;
    }

    /// <summary>Makes the store answer by id with the given hotels, whatever is asked for.</summary>
    public ReadsHarness MongoHas(params long[] ids)
    {
        Store.GetByIdsAsync(Supplier, Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<HotelDocument>>(
                [.. ((IReadOnlyCollection<long>)call[1]).Where(ids.Contains).Select(id => Hotel(id))]));
        return this;
    }

    public IReadOnlyList<long> AskedMongoFor() =>
        Store.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IHotelContentStore.GetByIdsAsync))
            .SelectMany(c => (IReadOnlyCollection<long>)c.GetArguments()[1]!)
            .ToList();
}
