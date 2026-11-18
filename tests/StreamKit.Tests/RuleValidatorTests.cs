using StreamKit.Core.Rules;
using Xunit;

namespace StreamKit.Tests;

public sealed class RuleValidatorTests
{
    [Fact]
    public void Valid_rule_has_no_errors()
    {
        var rule = new AutomationRule
        {
            Id = "gift-scene",
            On = "gift",
            Where = new RuleWhere { MinDiamonds = 50 },
            Actions = { new RuleAction { Type = "scene", Scene = "Regalos" } },
        };

        Assert.Empty(RuleValidator.Validate(rule));
    }

    [Fact]
    public void Unknown_event_and_action_are_reported()
    {
        var rule = new AutomationRule
        {
            Id = "bad",
            On = "explosion",
            Actions = { new RuleAction { Type = "teleport" } },
        };

        var errors = RuleValidator.Validate(rule).ToArray();

        Assert.Contains(errors, e => e.Contains("explosión") || e.Contains("explosion"));
        Assert.Contains(errors, e => e.Contains("teleport"));
    }

    [Fact]
    public void Missing_required_fields_are_reported_per_action()
    {
        var rule = new AutomationRule
        {
            Id = "incomplete",
            On = "any",
            Actions =
            {
                new RuleAction { Type = "filter", Source = "Camera" },
                new RuleAction { Type = "mute", Source = "Mic" },
                new RuleAction { Type = "sourceVisibility", Scene = "Gaming", Source = "Alert" },
                new RuleAction { Type = "raw" },
            },
        };

        var errors = RuleValidator.Validate(rule).ToArray();

        Assert.Contains(errors, e => e.Contains("filtro"));
        Assert.Contains(errors, e => e.Contains("muted"));
        Assert.Contains(errors, e => e.Contains("visible"));
        Assert.Contains(errors, e => e.Contains("requestType"));
    }

    [Fact]
    public void Rule_without_actions_is_invalid()
    {
        var rule = new AutomationRule { Id = "empty", On = "any" };

        Assert.Contains(RuleValidator.Validate(rule), e => e.Contains("no tiene acciones"));
    }
}
