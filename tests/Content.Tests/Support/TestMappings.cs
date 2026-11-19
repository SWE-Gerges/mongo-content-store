using Content.Mongo.Serialization;
using Content.Store;

namespace Content.Tests.Support;

/// <summary>
/// The driver registry is process-wide and the first sink bound wins, so every test that decodes hotel documents
/// shares one counter and runs inside one non-parallel collection, asserting on deltas.
/// </summary>
internal static class TestMappings
{
    public static MalformedFieldCounter Counter { get; } =
        (MalformedFieldCounter)HotelBsonMappings.EnsureRegistered(new MalformedFieldCounter());
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClassMapCollection
{
    public const string Name = "Hotel class maps";
}
