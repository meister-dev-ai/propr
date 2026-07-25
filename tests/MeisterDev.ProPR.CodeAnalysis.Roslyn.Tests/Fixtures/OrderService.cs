// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace Sample.Fixtures;

/// <summary>
///     Fixture exercised by RoslynSyntaxStructuralAnalyzerTests. The symbol <c>CalculateTotal</c>
///     appears as a real call in code, inside this comment (CalculateTotal), and inside a string
///     literal below. Only the real call site must be confirmed as a reference.
/// </summary>
public sealed class OrderService
{
    private readonly PricingPolicy _policy = new();

    public decimal CalculateTotal(int quantity, decimal unitPrice)
    {
        return quantity * unitPrice;
    }

    public string Describe(int quantity, decimal unitPrice)
    {
        // The next line is the only genuine reference to CalculateTotal.
        var total = this.CalculateTotal(quantity, unitPrice);

        // This mention of CalculateTotal lives in a comment and must be excluded.
        var label = "CalculateTotal was not actually invoked on this line";

        return $"{label}: {total}";
    }
}

public sealed class PricingPolicy
{
    public decimal Apply(decimal value) => value;
}
