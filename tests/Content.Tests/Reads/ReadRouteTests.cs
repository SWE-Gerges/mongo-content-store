using Content.Reads;
using Content.Store;
using Content.Store.Configuration;
using Content.Sync.Configuration;
using Content.Sync.Search;
using Content.Tests.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Content.Tests.Reads;

public class ReadRouteRulesTests
{
    private static readonly HotelCollectionNaming Naming = new();

    private static IReadOnlyList<string> Check(string[] suppliers, params ReadRoute[] routes) =>
        ReadRouteRules.Check(suppliers, routes, Naming.ForSupplier, collection => "content-" + collection);

    private static ReadRoute Route(string supplier, string? collection = null, string? alias = null) => new()
    {
        Supplier = supplier,
        Collection = collection ?? $"hotel_{supplier}",
        IndexAlias = alias ?? $"content-hotel_{supplier}",
    };

    [Fact]
    public void A_table_with_one_exact_route_per_supplier_is_sound() =>
        Check(["supplier-a", "supplier-b"], Route("supplier-a"), Route("supplier-b")).ShouldBeEmpty();

    [Fact]
    public void A_configured_supplier_without_a_route_is_reported_rather_than_given_a_default() =>
        Check(["supplier-a", "supplier-b"], Route("supplier-a")).ShouldHaveSingleItem().ShouldContain("'supplier-b' is configured but has no route");

    [Fact]
    public void A_route_for_a_supplier_that_is_not_configured_is_dangling() =>
        Check(["supplier-a"], Route("supplier-a"), Route("supplier-x")).ShouldHaveSingleItem().ShouldContain("dangling route");

    [Fact]
    public void A_supplier_listed_twice_is_reported()
    {
        var problems = Check(["supplier-a"], Route("supplier-a"), Route("supplier-a"));

        problems.ShouldContain(p => p.Contains("lists supplier 'supplier-a' 2 times"));
    }

    [Fact]
    public void Two_suppliers_sharing_a_collection_or_an_alias_are_reported()
    {
        var problems = Check(
            ["supplier-a", "supplier-b"],
            Route("supplier-a"),
            Route("supplier-b", collection: "hotel_supplier-a", alias: "content-hotel_supplier-a"));

        problems.ShouldContain(p => p.Contains("collection 'hotel_supplier-a'"));
        problems.ShouldContain(p => p.Contains("index alias 'content-hotel_supplier-a'"));
    }

    [Fact]
    public void A_route_to_a_collection_the_store_does_not_read_is_reported()
    {
        var problems = Check(["supplier-a"], Route("supplier-a", collection: "hotel_other"));

        problems.ShouldHaveSingleItem().ShouldContain("content store reads 'hotel_supplier-a'");
    }

    [Fact]
    public void A_route_to_an_alias_that_sync_does_not_serve_is_reported()
    {
        var problems = Check(["supplier-a"], Route("supplier-a", alias: "content-elsewhere"));

        problems.ShouldHaveSingleItem().ShouldContain("sync serves that collection as 'content-hotel_supplier-a'");
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var problems = Check(["supplier-a", "supplier-b"], Route("supplier-x"), Route("supplier-a", collection: "wrong"));

        problems.Count.ShouldBeGreaterThanOrEqualTo(3);
    }
}

public sealed class ReadRouteTableTests
{
    private static ReadRouteTable Table() => new(Options.Create(new ReadsOptions
    {
        Routes = [new ReadRoute { Supplier = "supplier-a", Collection = "hotel_supplier-a", IndexAlias = "content-hotel_supplier-a" }],
    }));

    [Fact]
    public void A_known_supplier_resolves_to_its_collection_and_alias()
    {
        var route = Table().Get("supplier-a");

        route.Collection.ShouldBe("hotel_supplier-a");
        route.IndexAlias.ShouldBe("content-hotel_supplier-a");
    }

    [Theory]
    [InlineData("supplier-z")]
    [InlineData("")]
    [InlineData("SUPPLIER-A")]
    public void An_unknown_supplier_has_no_route_and_no_default(string supplier)
    {
        Table().TryGet(supplier, out _).ShouldBeFalse();
        Should.Throw<UnknownSupplierException>(() => Table().Get(supplier));
    }

    [Fact]
    public void The_index_timeout_must_be_positive_and_bounded()
    {
        var validator = new ReadsOptionsValidator(
            Options.Create(new ContentStoreOptions { Suppliers = ["supplier-a"] }),
            new HotelCollectionNaming(),
            new SearchIndexNaming(Options.Create(new SearchOptions { Uri = "http://localhost:9200" })));
        var route = new ReadRoute { Supplier = "supplier-a", Collection = "hotel_supplier-a", IndexAlias = "content-hotel_supplier-a" };

        validator.Validate(null, new ReadsOptions { Routes = [route], IndexTimeout = TimeSpan.Zero }).Failed.ShouldBeTrue();
        validator.Validate(null, new ReadsOptions { Routes = [route], IndexTimeout = TimeSpan.FromMinutes(1) }).Failed.ShouldBeTrue();
        validator.Validate(null, new ReadsOptions { Routes = [route], IndexTimeout = TimeSpan.FromSeconds(1) }).Succeeded.ShouldBeTrue();
    }
}

/// <summary>The route table as the real host builds it from the shipped configuration.</summary>
public sealed class ReadRouteStartupTests
{
    [Fact]
    public void Every_supplier_in_the_shipped_configuration_has_a_route_with_the_expected_collection_and_alias()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var suppliers = scope.ServiceProvider.GetRequiredService<IOptions<ContentStoreOptions>>().Value.Suppliers;
        var table = scope.ServiceProvider.GetRequiredService<ReadRouteTable>();
        var naming = scope.ServiceProvider.GetRequiredService<ICollectionNaming>();
        var indexNaming = scope.ServiceProvider.GetRequiredService<SearchIndexNaming>();

        suppliers.ShouldNotBeEmpty();
        foreach (var supplier in suppliers)
        {
            table.TryGet(supplier, out var route).ShouldBeTrue($"{supplier} has no route");
            route.Collection.ShouldBe(naming.ForSupplier(supplier));
            route.IndexAlias.ShouldBe(indexNaming.IndexFor(naming.ForSupplier(supplier)));
        }

        table.Routes.Count.ShouldBe(suppliers.Count, "no route without a supplier");
    }

    [Fact]
    public void The_host_refuses_to_start_when_a_supplier_has_no_route_or_a_route_dangles()
    {
        using var factory = new ApiFactory();
        factory.Settings["Reads:Routes:2:Supplier"] = "supplier-x";
        factory.Settings["Reads:Routes:2:Collection"] = "hotel_supplier-x";
        factory.Settings["Reads:Routes:2:IndexAlias"] = "content-hotel_supplier-x";

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("'supplier-c' is configured but has no route");
        failure.Message.ShouldContain("'supplier-x', which is not a configured supplier");
    }

    [Fact]
    public void The_host_refuses_to_start_when_a_route_points_at_the_wrong_collection()
    {
        using var factory = new ApiFactory();
        factory.Settings["Reads:Routes:0:Collection"] = "hotel_somewhere-else";

        var failure = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        failure.Message.ShouldContain("the content store reads 'hotel_supplier-a'");
    }
}
