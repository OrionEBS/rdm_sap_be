using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.OpenApi.Models;
using Rdm.Api.ApiCatalog;
using Rdm.Application.ApiCatalog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Rdm.Tests;

public class ApiOutputTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed record Line(string Material, decimal Quantity, string? Unit);
    public sealed record Customer(string Code, string Name1, string? Name2);
    public sealed record Page(int Total, IReadOnlyList<Customer> Items, Customer? Owner, List<Line> Lines);

    private static JsonNode Sample() => JsonSerializer.SerializeToNode(new Page(2,
        new[] { new Customer("100166", "Cty A", null), new Customer("100002", "Cty B", "Chi nhanh") },
        new Customer("1", "Owner", "x"), new List<Line> { new("100089", 10, "TO") }), Web)!;

    private static ApiOutputRules Rules(params ApiOutputField[] fields) => ApiOutputRules.Create(fields)!;

    [Fact]
    public void Hides_renames_and_reorders_nested_array_fields()
    {
        var rules = Rules(
            new ApiOutputField("items[].name2", Visible: false),
            new ApiOutputField("items[].code", Alias: "customerCode"),
            new ApiOutputField("items[].name1", Order: 1),
            new ApiOutputField("owner", Visible: false),
            new ApiOutputField("total", Alias: "count", Order: 99));

        var json = rules.Apply(Sample())!.ToJsonString();

        // Field có order đứng trước field không khai báo order (giữ thứ tự gốc).
        json.Should().Be("""{"count":2,"items":[{"name1":"Cty A","customerCode":"100166"},{"name1":"Cty B","customerCode":"100002"}],"""
                         + """ "lines":[{"material":"100089","quantity":10,"unit":"TO"}]}""".TrimStart());
    }

    [Fact]
    public void No_effective_rule_means_no_transformation()
    {
        ApiOutputRules.Create(new[] { new ApiOutputField("total"), new ApiOutputField("items[].code", Alias: "") }).Should().BeNull();
        ApiOutputRules.Create(Array.Empty<ApiOutputField>()).Should().BeNull();
    }

    [Fact]
    public void Root_array_paths_use_brackets()
    {
        var node = JsonSerializer.SerializeToNode(new[] { new Customer("1", "A", "B") }, Web);
        Rules(new ApiOutputField("[].name2", Visible: false), new ApiOutputField("[].code", Alias: "id")).Apply(node)!.ToJsonString()
            .Should().Be("""[{"id":"1","name1":"A"}]""");
    }

    [Fact]
    public void Discovers_fields_with_serializer_names_including_nested_arrays()
    {
        var fields = ApiOutputRegistry.DiscoverFields(typeof(Page), Web);

        fields.Select(f => f.Path).Should().Equal(
            "total", "items", "items[].code", "items[].name1", "items[].name2", "owner", "owner.code", "owner.name1", "owner.name2",
            "lines", "lines[].material", "lines[].quantity", "lines[].unit");
        fields.Single(f => f.Path == "items").Should().Match<ApiOutputFieldInfo>(f => f.HasChildren && f.Type == "array" && f.Depth == 0);
        fields.Single(f => f.Path == "lines[].quantity").Should().Match<ApiOutputFieldInfo>(f => f.Type == "number" && f.Depth == 1);
    }

    [Fact]
    public void Validation_reports_unknown_path_bad_alias_and_name_clash()
    {
        var known = ApiOutputRegistry.DiscoverFields(typeof(Page), Web).Select(f => f.Path).ToList();

        var errors = ApiOutputValidation.Validate(new[]
        {
            new ApiOutputField("items[].nope", Alias: "x"),
            new ApiOutputField("items[].code", Alias: "name1"),     // trùng field name1 cùng cấp
            new ApiOutputField("total", Alias: "1abc")
        }, known);

        errors.Keys.Should().BeEquivalentTo("items[].nope", "items[].code", "items[].name1", "total");

        // Ẩn field bị trùng tên thì hợp lệ.
        ApiOutputValidation.Validate(new[]
        {
            new ApiOutputField("items[].code", Alias: "name1"), new ApiOutputField("items[].name1", Visible: false)
        }, known).Should().BeEmpty();
    }

    [Theory]
    [InlineData("api/master-data/sold-to/{customer}", "abc", new[] { "100166" }, "/api/master-data/sold-to/100166")]
    [InlineData("api/master-data/sold-to", "abc", new string[0], "/api/master-data/sold-to")]
    [InlineData("api/customer-credits/{customer}/{area?}", "abc", new[] { "100166" }, "/api/customer-credits/100166")]
    public void Copy_route_maps_back_to_source(string template, string name, string[] values, string expected)
    {
        ApiOutputRegistry.MapToSource(template, values).Should().Be(expected);
        ApiOutputRegistry.CopyRoute(name, template).Should().StartWith("api/c/abc");
    }

    [Fact]
    public void Copy_route_rejects_wrong_parameter_count()
    {
        ApiOutputRegistry.MapToSource("api/master-data/sold-to/{customer}", Array.Empty<string>()).Should().BeNull();
        ApiOutputRegistry.MapToSource("api/master-data/sold-to/{customer}", new[] { "1", "2" }).Should().BeNull();
        ApiOutputRegistry.CopyRoute("x", "api/a/{id}/b/{n}").Should().Be("api/c/x/{id}/{n}");
        ApiOutputRegistry.RouteParameters("api/files/{*path}").Should().BeNull();
    }

    [Fact]
    public void Registry_separates_original_config_and_copies()
    {
        var reg = new ApiOutputRegistry();
        reg.Load(new[]
        {
            new ApiOutputProfile { ProfileId = 1, EndpointKey = "GET api/x", FieldsJson = """[{"path":"total","visible":false}]""" },
            new ApiOutputProfile { ProfileId = 2, EndpointKey = "GET api/x/{id}", CopyName = "x-ngan", FieldsJson = "not json" }
        });

        reg.GetOriginal("get api/x")!.Rules.Should().NotBeNull();
        reg.GetCopy("X-NGAN")!.Should().Match<ApiOutputRegistry.Compiled>(c => c.Rules == null && c.Route == "api/c/x-ngan/{id}" && c.HttpMethod == "GET");
        reg.GetOriginal("GET api/x/{id}").Should().BeNull();
    }

    [Fact]
    public void Swagger_schema_is_rewritten_inline_and_untouched_parts_keep_refs()
    {
        var repo = new SchemaRepository();
        var generator = new SchemaGenerator(new SchemaGeneratorOptions(), new JsonSerializerDataContractResolver(Web));
        var schema = generator.GenerateSchema(typeof(Page), repo);
        var rules = Rules(new ApiOutputField("items[].name2", Visible: false), new ApiOutputField("items[].code", Alias: "customerCode"),
            new ApiOutputField("total", Order: 1));

        var result = ApiOutputRegistry.TransformSchema(schema, rules, repo, keepDescriptions: false);

        result.Reference.Should().BeNull();
        result.Properties.Keys.First().Should().Be("total");
        var item = result.Properties["items"].Items;
        item.Properties.Keys.Should().Equal("customerCode", "name1");
        result.Properties["lines"].Should().BeSameAs(repo.Schemas["Page"].Properties["lines"]);   // không có quy tắc → giữ nguyên
        repo.Schemas["Customer"].Properties.Keys.Should().Contain("name2");                         // schema dùng chung không bị sửa
    }
}
