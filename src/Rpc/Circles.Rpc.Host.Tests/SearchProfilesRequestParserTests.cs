using System.Text.Json;
using Circles.Rpc.Host.Wire;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Unit tests for <see cref="SearchProfilesRequestParser"/>.
///
/// These tests pin down the positional contract of the JSON-RPC params array
/// used by <c>circles_searchProfiles</c>. They run without a Nethermind runtime
/// and guard against positional-shift bugs in the wire handler: if anyone
/// inserts a new parameter at position 4 (instead of appending) the GroupType
/// assertions here will fail.
/// </summary>
[TestFixture]
public class SearchProfilesRequestParserTests
{
    private static JsonElement[] ParseParams(string json) =>
        JsonSerializer.Deserialize<JsonElement[]>(json)!;

    [Test]
    public void Parse_throws_when_parameters_array_is_empty()
    {
        Assert.Throws<ArgumentException>(() =>
            SearchProfilesRequestParser.Parse(ParseParams("[]")));
    }

    [Test]
    public void Parse_throws_when_parameters_is_null()
    {
        Assert.Throws<ArgumentException>(() =>
            SearchProfilesRequestParser.Parse(null));
    }

    [Test]
    public void Parse_with_only_text_applies_defaults()
    {
        var result = SearchProfilesRequestParser.Parse(ParseParams("[\"alice\"]"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("alice"));
            Assert.That(result.Limit, Is.EqualTo(SearchProfilesRequestParser.DefaultLimit));
            Assert.That(result.Offset, Is.EqualTo(SearchProfilesRequestParser.DefaultOffset));
            Assert.That(result.Types, Is.Null);
            Assert.That(result.GroupType, Is.Null);
        });
    }

    [Test]
    public void Parse_reads_groupType_from_position_4()
    {
        // [text, limit, offset, types, groupType] — groupType must be index 4.
        // Regression guard: if anyone shifts the positional order, this fails.
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 10, 5, null, \"closed\"]"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.EqualTo("alice"));
            Assert.That(result.Limit, Is.EqualTo(10));
            Assert.That(result.Offset, Is.EqualTo(5));
            Assert.That(result.Types, Is.Null);
            Assert.That(result.GroupType, Is.EqualTo("closed"));
        });
    }

    [Test]
    public void Parse_reads_types_from_position_3()
    {
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 10, 0, [\"group\", \"organization\"], \"open\"]"));

        Assert.That(result.Types, Is.EqualTo(new[] { "group", "organization" }));
        Assert.That(result.GroupType, Is.EqualTo("open"));
    }

    [Test]
    public void Parse_treats_explicit_null_at_position_4_as_unset_groupType()
    {
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 10, 0, null, null]"));

        Assert.That(result.GroupType, Is.Null);
    }

    [Test]
    public void Parse_omits_groupType_when_only_4_params_are_supplied()
    {
        // Legacy callers (pre-groupType) send 4 params and must continue to work.
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 10, 0, null]"));

        Assert.That(result.GroupType, Is.Null);
        Assert.That(result.Limit, Is.EqualTo(10));
        Assert.That(result.Offset, Is.EqualTo(0));
    }

    [Test]
    public void Parse_handles_open_groupType()
    {
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 20, 0, null, \"open\"]"));

        Assert.That(result.GroupType, Is.EqualTo("open"));
    }

    [Test]
    public void Parse_passes_through_unknown_groupType_values_for_module_level_validation()
    {
        // The parser does not validate the groupType value — that's the module's
        // job (CirclesRpcModule.SearchProfiles throws on unknown values). This
        // separation keeps the parser thin and the validation centralized.
        var result = SearchProfilesRequestParser.Parse(
            ParseParams("[\"alice\", 20, 0, null, \"restricted\"]"));

        Assert.That(result.GroupType, Is.EqualTo("restricted"));
    }
}
